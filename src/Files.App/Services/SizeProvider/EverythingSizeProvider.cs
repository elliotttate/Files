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
		private static readonly object _everythingLock = new object();
		private static bool _everythingInitialized = false;

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

		[DllImport("Everything64.dll")]
		private static extern bool Everything_IsDBLoaded();

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern IntPtr LoadLibrary(string lpFileName);

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern bool SetDllDirectory(string lpPathName);

		private const int EVERYTHING_REQUEST_SIZE = 0x00000010;

		static EverythingSizeProvider()
		{
			// Setup DLL loading for Everything API
			try
			{
				var appDirectory = AppContext.BaseDirectory;
				var searchPaths = new[]
				{
					appDirectory,
					Path.Combine(appDirectory, "Libraries"),
					Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Everything"),
					Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Everything")
				};

				// Try to load Everything DLL from various locations
				foreach (var path in searchPaths.Where(Directory.Exists))
				{
					var dllPath = Path.Combine(path, "Everything64.dll");
					if (File.Exists(dllPath))
					{
						SetDllDirectory(path);
						var handle = LoadLibrary(dllPath);
						if (handle != IntPtr.Zero)
						{
							_everythingInitialized = true;
							break;
						}
					}
				}
			}
			catch
			{
				_everythingInitialized = false;
			}
		}

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

		private bool ShouldSkipEverything(string path)
		{
			// Skip root drives
			if (path.Length <= 3 && path.EndsWith(":\\"))
			{
				System.Diagnostics.Debug.WriteLine($"Skipping Everything size calculation for root drive: {path}");
				return true;
			}
			
			// Skip known large system directories
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
				(largePath != null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(largePath), StringComparison.OrdinalIgnoreCase))))
			{
				System.Diagnostics.Debug.WriteLine($"Skipping Everything size calculation for large directory: {path}");
				return true;
			}
			
			return false;
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

			// Check if Everything is available
			if (!everythingService.IsEverythingAvailable())
			{
				// Fall back to standard calculation if Everything is not available
				await FallbackCalculateAsync(path, cancellationToken);
				return;
			}

			// Check if this is a problematic path that should skip Everything
			if (ShouldSkipEverything(path))
			{
				await FallbackCalculateAsync(path, cancellationToken);
				return;
			}

			try
			{
				// Calculate using Everything
				var stopwatch = Stopwatch.StartNew();
				ulong totalSize = await CalculateWithEverythingAsync(path, cancellationToken);
				
				// If Everything returns 0 (likely due to limits), fall back
				if (totalSize == 0)
				{
					await FallbackCalculateAsync(path, cancellationToken);
					return;
				}
				
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
			if (!_everythingInitialized || !everythingService.IsEverythingAvailable())
			{
				return 0UL;
			}

			return await Task.Run(() =>
			{
				lock (_everythingLock)
				{
					try
					{
						// Safely call Everything_Reset with error handling
						try
						{
							Everything_Reset();
						}
						catch (AccessViolationException ave)
						{
							System.Diagnostics.Debug.WriteLine($"Access violation in Everything_Reset: {ave.Message}");
							_everythingInitialized = false;
							return 0UL;
						}
						
						// Search for all files recursively under this path
						// Use proper search syntax: path*.* to get all files recursively
						// Escape the path for Everything search
						var escapedPath = path.Replace("\\", "\\\\");
						if (!escapedPath.EndsWith("\\\\"))
							escapedPath += "\\\\";
						
						// Use path:"folder\**" to search recursively
						var searchQuery = $"\"{escapedPath}*\"";
						Everything_SetSearchW(searchQuery);
						Everything_SetRequestFlags(EVERYTHING_REQUEST_SIZE);
						
						// Add a maximum limit to prevent memory issues
						Everything_SetMax(10000); // Increased limit for better accuracy
						
						if (!Everything_QueryW(true))
							return 0UL;

						var numResults = Everything_GetNumResults();
						
						// If we hit the limit, fall back to standard calculation
						if (numResults >= 10000)
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
					catch (AccessViolationException ave)
					{
						System.Diagnostics.Debug.WriteLine($"Access violation in Everything API: {ave.Message}");
						_everythingInitialized = false;
						return 0UL;
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
					
					// Use EnumerateFiles for better performance and exception handling
					try
					{
						foreach (var file in directory.EnumerateFiles())
						{
							if (ct.IsCancellationRequested)
								break;
								
							try
							{
								totalSize += (ulong)file.Length;
							}
							catch (UnauthorizedAccessException)
							{
								// Skip files we can't access
							}
						}
					}
					catch (UnauthorizedAccessException)
					{
						// Skip if we can't enumerate files
					}

					// Recursively process subdirectories
					try
					{
						foreach (var subDirectory in directory.EnumerateDirectories())
						{
							if (ct.IsCancellationRequested)
								break;

							try
							{
								// Skip symbolic links and junctions
								if ((subDirectory.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
									continue;

								var subDirSize = await CalculateRecursive(subDirectory.FullName, ct, level + 1);
								totalSize += subDirSize;
							}
							catch (UnauthorizedAccessException)
							{
								// Skip subdirectories we can't access
							}
						}
					}
					catch (UnauthorizedAccessException)
					{
						// Skip if we can't enumerate directories
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
					// Skip directories we can't access - don't log to avoid spam
				}
				catch (DirectoryNotFoundException)
				{
					// Directory was deleted during enumeration
				}
				catch (Exception ex)
				{
					// Log unexpected errors
					System.Diagnostics.Debug.WriteLine($"Error calculating size for {currentPath}: {ex.Message}");
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