// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Services.Search;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Files.App.Services.SizeProvider
{
	public sealed class EverythingSizeProvider : ISizeProvider
	{
		private readonly ConcurrentDictionary<string, ulong> sizes = new();
		private readonly IEverythingSearchService everythingService;

		public event EventHandler<SizeChangedEventArgs>? SizeChanged;

		// Everything API imports for folder size calculation
		[DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
		private static extern uint Everything_SetSearchW(string lpSearchString);
		
		[DllImport("Everything64.dll")]
		private static extern void Everything_SetRequestFlags(uint dwRequestFlags);
		
		[DllImport("Everything64.dll")]
		private static extern void Everything_SetMax(uint dwMax);
		
		[DllImport("Everything64.dll")]
		private static extern bool Everything_QueryW(bool bWait);
		
		[DllImport("Everything64.dll")]
		private static extern uint Everything_GetNumResults();
		
		[DllImport("Everything64.dll")]
		private static extern bool Everything_GetResultSize(uint nIndex, out long lpFileSize);
		
		[DllImport("Everything64.dll")]
		private static extern void Everything_Reset();
		
		[DllImport("Everything64.dll")]
		private static extern void Everything_CleanUp();

		private const int EVERYTHING_REQUEST_SIZE = 0x00000010;

		public EverythingSizeProvider(IEverythingSearchService everythingSearchService)
		{
			everythingService = everythingSearchService;
		}

		public Task CleanAsync() => Task.CompletedTask;

		public Task ClearAsync()
		{
			sizes.Clear();
			return Task.CompletedTask;
		}

		public async Task UpdateAsync(string path, CancellationToken cancellationToken)
		{
			await Task.Yield();
			
			// Return cached size immediately if available
			if (sizes.TryGetValue(path, out ulong cachedSize))
			{
				RaiseSizeChanged(path, cachedSize, SizeChangedValueState.Final);
			}
			else
			{
				RaiseSizeChanged(path, 0, SizeChangedValueState.None);
			}

			// TEMPORARY: Disable Everything size calculation to prevent crashes
			// TODO: Re-enable once Everything memory issues are resolved
			System.Diagnostics.Debug.WriteLine($"Everything size calculation disabled for: {path}");
			await FallbackCalculateAsync(path, cancellationToken);
			return;

			// Check if Everything is available
			if (!everythingService.IsEverythingAvailable())
			{
				// Fall back to standard calculation if Everything is not available
				await FallbackCalculateAsync(path, cancellationToken);
				return;
			}

			try
			{
				// Calculate using Everything
				var stopwatch = Stopwatch.StartNew();
				ulong totalSize = await CalculateWithEverythingAsync(path, cancellationToken);
				
				sizes[path] = totalSize;
				RaiseSizeChanged(path, totalSize, SizeChangedValueState.Final);
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"Everything size calculation failed: {ex.Message}");
				// Fall back to standard calculation on error
				await FallbackCalculateAsync(path, cancellationToken);
			}
		}

		private async Task<ulong> CalculateWithEverythingAsync(string path, CancellationToken cancellationToken)
		{
			return await Task.Run(() =>
			{
				try
				{
					Everything_Reset();
					
					// IMPORTANT: For large directories like C:\, this query can return millions of results
					// causing Everything to run out of memory. For root drives, fall back to standard calculation.
					if (path.Length <= 3 && path.EndsWith(":\\"))
					{
						System.Diagnostics.Debug.WriteLine($"Skipping Everything size calculation for root drive: {path}");
						return 0UL; // Will trigger fallback calculation
					}
					
					// For large known directories, also skip Everything
					var knownLargePaths = new[] { 
						@"C:\Windows", 
						@"C:\Program Files", 
						@"C:\Program Files (x86)",
						@"C:\Users",
						@"C:\ProgramData",
						@"C:\$Recycle.Bin",
						Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
						Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
						Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
						Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
					};
					
					if (knownLargePaths.Any(largePath => 
						string.Equals(path, largePath, StringComparison.OrdinalIgnoreCase) ||
						string.Equals(Path.GetFullPath(path), Path.GetFullPath(largePath), StringComparison.OrdinalIgnoreCase)))
					{
						System.Diagnostics.Debug.WriteLine($"Skipping Everything size calculation for large directory: {path}");
						return 0UL; // Will trigger fallback calculation
					}
					
					// Search for all files under this path
					// Use folder: to search within specific folder
					var searchQuery = $"folder:\"{path}\"";
					Everything_SetSearchW(searchQuery);
					Everything_SetRequestFlags(EVERYTHING_REQUEST_SIZE);
					
					// Add a maximum limit to prevent memory issues
					Everything_SetMax(1000); // Limit to 1,000 results - much safer limit
					
					if (!Everything_QueryW(true))
						return 0UL;

					var numResults = Everything_GetNumResults();
					
					// If we hit the limit, fall back to standard calculation
					if (numResults >= 1000)
					{
						System.Diagnostics.Debug.WriteLine($"Too many results ({numResults}) for Everything size calculation: {path}");
						return 0UL; // Will trigger fallback calculation
					}
					
					ulong totalSize = 0;

					for (uint i = 0; i < numResults; i++)
					{
						if (cancellationToken.IsCancellationRequested)
							break;

						if (Everything_GetResultSize(i, out long size))
						{
							totalSize += (ulong)size;
						}
					}

					return totalSize;
				}
				catch (Exception ex)
				{
					System.Diagnostics.Debug.WriteLine($"Everything size calculation error: {ex.Message}");
					return 0UL;
				}
				finally
				{
					try
					{
						Everything_CleanUp();
					}
					catch (Exception ex)
					{
						System.Diagnostics.Debug.WriteLine($"Error cleaning up Everything: {ex.Message}");
					}
				}
			}, cancellationToken);
		}

		private async Task FallbackCalculateAsync(string path, CancellationToken cancellationToken)
		{
			// Fallback to directory enumeration if Everything is not available
			var stopwatch = Stopwatch.StartNew();
			ulong size = await CalculateRecursive(path, cancellationToken);
			
			sizes[path] = size;
			RaiseSizeChanged(path, size, SizeChangedValueState.Final);

			async Task<ulong> CalculateRecursive(string currentPath, CancellationToken ct, int level = 0)
			{
				if (string.IsNullOrEmpty(currentPath))
					return 0;

				ulong totalSize = 0;

				try
				{
					var directory = new DirectoryInfo(currentPath);
					
					// Get files in current directory
					foreach (var file in directory.GetFiles())
					{
						if (ct.IsCancellationRequested)
							break;
							
						totalSize += (ulong)file.Length;
					}

					// Recursively process subdirectories
					foreach (var subDirectory in directory.GetDirectories())
					{
						if (ct.IsCancellationRequested)
							break;

						// Skip symbolic links and junctions
						if ((subDirectory.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
							continue;

						var subDirSize = await CalculateRecursive(subDirectory.FullName, ct, level + 1);
						totalSize += subDirSize;
					}

					// Update intermediate results for top-level calculation
					if (level == 0 && stopwatch.ElapsedMilliseconds > 500)
					{
						stopwatch.Restart();
						RaiseSizeChanged(path, totalSize, SizeChangedValueState.Intermediate);
					}
				}
				catch (UnauthorizedAccessException)
				{
					// Skip directories we can't access
				}
				catch (DirectoryNotFoundException)
				{
					// Directory was deleted during enumeration
				}

				return totalSize;
			}
		}

		public bool TryGetSize(string path, out ulong size) => sizes.TryGetValue(path, out size);

		public void Dispose() { }

		private void RaiseSizeChanged(string path, ulong newSize, SizeChangedValueState valueState)
			=> SizeChanged?.Invoke(this, new SizeChangedEventArgs(path, newSize, valueState));
	}
}