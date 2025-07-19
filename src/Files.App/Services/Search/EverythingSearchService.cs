// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Data.Models;
using Files.App.ViewModels;
using System.Runtime.InteropServices;
using System.Text;
using System.IO;
using Windows.Storage;
using Microsoft.Extensions.Logging;

namespace Files.App.Services.Search
{
	public interface IEverythingSearchService
	{
		bool IsEverythingAvailable();
		Task<List<ListedItem>> SearchAsync(string query, string searchPath = null, CancellationToken cancellationToken = default);
		Task<List<ListedItem>> FilterItemsAsync(IEnumerable<ListedItem> items, string query, CancellationToken cancellationToken = default);
	}

	public sealed class EverythingSearchService : IEverythingSearchService
	{
		// Everything API constants
		private const int EVERYTHING_OK = 0;
		private const int EVERYTHING_ERROR_IPC = 2;
		
		private const int EVERYTHING_REQUEST_FILE_NAME = 0x00000001;
		private const int EVERYTHING_REQUEST_PATH = 0x00000002;
		private const int EVERYTHING_REQUEST_DATE_MODIFIED = 0x00000040;
		private const int EVERYTHING_REQUEST_SIZE = 0x00000010;
		private const int EVERYTHING_REQUEST_DATE_CREATED = 0x00000020;
		private const int EVERYTHING_REQUEST_ATTRIBUTES = 0x00000100;

		// Architecture-aware DLL name
		private static readonly string EverythingDllName = Environment.Is64BitProcess ? "Everything64.dll" : "Everything32.dll";

		// Everything API imports - using architecture-aware DLL resolution
		[DllImport("Everything", EntryPoint = "Everything_SetSearchW", CharSet = CharSet.Unicode)]
		private static extern uint Everything_SetSearchW(string lpSearchString);
		
		[DllImport("Everything", EntryPoint = "Everything_SetMatchPath")]
		private static extern void Everything_SetMatchPath(bool bEnable);
		
		[DllImport("Everything", EntryPoint = "Everything_SetMatchCase")]
		private static extern void Everything_SetMatchCase(bool bEnable);
		
		[DllImport("Everything", EntryPoint = "Everything_SetMatchWholeWord")]
		private static extern void Everything_SetMatchWholeWord(bool bEnable);
		
		[DllImport("Everything", EntryPoint = "Everything_SetRegex")]
		private static extern void Everything_SetRegex(bool bEnable);
		
		[DllImport("Everything", EntryPoint = "Everything_SetMax")]
		private static extern void Everything_SetMax(uint dwMax);
		
		[DllImport("Everything", EntryPoint = "Everything_SetOffset")]
		private static extern void Everything_SetOffset(uint dwOffset);
		
		[DllImport("Everything", EntryPoint = "Everything_SetRequestFlags")]
		private static extern void Everything_SetRequestFlags(uint dwRequestFlags);
		
		[DllImport("Everything", EntryPoint = "Everything_QueryW")]
		private static extern bool Everything_QueryW(bool bWait);
		
		[DllImport("Everything", EntryPoint = "Everything_GetNumResults")]
		private static extern uint Everything_GetNumResults();
		
		[DllImport("Everything", EntryPoint = "Everything_GetLastError")]
		private static extern uint Everything_GetLastError();
		
		[DllImport("Everything", EntryPoint = "Everything_IsFileResult")]
		private static extern bool Everything_IsFileResult(uint nIndex);
		
		[DllImport("Everything", EntryPoint = "Everything_IsFolderResult")]
		private static extern bool Everything_IsFolderResult(uint nIndex);
		
		[DllImport("Everything", EntryPoint = "Everything_GetResultPath", CharSet = CharSet.Unicode)]
		private static extern IntPtr Everything_GetResultPath(uint nIndex);
		
		[DllImport("Everything", EntryPoint = "Everything_GetResultFileName", CharSet = CharSet.Unicode)]
		private static extern IntPtr Everything_GetResultFileName(uint nIndex);
		
		[DllImport("Everything", EntryPoint = "Everything_GetResultDateModified")]
		private static extern bool Everything_GetResultDateModified(uint nIndex, out long lpFileTime);
		
		[DllImport("Everything", EntryPoint = "Everything_GetResultDateCreated")]
		private static extern bool Everything_GetResultDateCreated(uint nIndex, out long lpFileTime);
		
		[DllImport("Everything", EntryPoint = "Everything_GetResultSize")]
		private static extern bool Everything_GetResultSize(uint nIndex, out long lpFileSize);
		
		[DllImport("Everything", EntryPoint = "Everything_GetResultAttributes")]
		private static extern uint Everything_GetResultAttributes(uint nIndex);
		
		[DllImport("Everything", EntryPoint = "Everything_Reset")]
		private static extern void Everything_Reset();
		
		[DllImport("Everything", EntryPoint = "Everything_CleanUp")]
		private static extern void Everything_CleanUp();
		
		[DllImport("Everything", EntryPoint = "Everything_IsDBLoaded")]
		private static extern bool Everything_IsDBLoaded();

		// Win32 API imports for DLL loading
		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern IntPtr LoadLibrary(string lpLibFileName);
		
		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool FreeLibrary(IntPtr hLibModule);
		
		[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
		private static extern bool SetDllDirectory(string lpPathName);
		
		[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
		private static extern IntPtr AddDllDirectory(string newDirectory);
		
		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool RemoveDllDirectory(IntPtr cookie);

		private readonly IUserSettingsService _userSettingsService;
		private static readonly object _dllSetupLock = new object();
		private static IntPtr _everythingModule = IntPtr.Zero;
		private static readonly List<IntPtr> _dllDirectoryCookies = new();
		private static bool _dllDirectorySet = false;
		private static bool _everythingAvailable = false;
		private static bool _availabilityChecked = false;

		static EverythingSearchService()
		{
			// Set up DLL import resolver for architecture-aware loading
			NativeLibrary.SetDllImportResolver(typeof(EverythingSearchService).Assembly, DllImportResolver);
		}

		public EverythingSearchService(IUserSettingsService userSettingsService)
		{
			_userSettingsService = userSettingsService;
			
			// Set up DLL search path if not already done
			lock (_dllSetupLock)
			{
				if (!_dllDirectorySet)
				{
					SetupDllSearchPath();
					_dllDirectorySet = true;
				}
			}
		}

		private static IntPtr DllImportResolver(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
		{
			if (libraryName == "Everything")
			{
				lock (_dllSetupLock)
				{
					if (_everythingModule != IntPtr.Zero)
						return _everythingModule;

					// Try to load Everything DLL from various locations
					var appDirectory = AppContext.BaseDirectory;
					var possiblePaths = new[]
					{
						Path.Combine(appDirectory, EverythingDllName),
						Path.Combine(appDirectory, "Libraries", EverythingDllName),
						Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Everything", EverythingDllName),
						Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Everything", EverythingDllName),
						EverythingDllName // Try system path
					};

					foreach (var path in possiblePaths)
					{
						if (File.Exists(path))
						{
							_everythingModule = LoadLibrary(path);
							if (_everythingModule != IntPtr.Zero)
							{
								App.Logger?.LogInformation($"Successfully loaded Everything DLL from: {path}");
								return _everythingModule;
							}
						}
					}

					// If not found, let the default resolver handle it
					return IntPtr.Zero;
				}
			}

			// Use default resolver for other libraries
			return NativeLibrary.Load(libraryName, assembly, searchPath);
		}

		private static void SetupDllSearchPath()
		{
			try
			{
				// Get the application directory
				var appDirectory = AppContext.BaseDirectory;
				var searchPaths = new[]
				{
					appDirectory,
					Path.Combine(appDirectory, "Libraries"),
					Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Everything"),
					Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Everything")
				};

				foreach (var path in searchPaths.Where(Directory.Exists))
				{
					try
					{
						SetDllDirectory(path);
						var cookie = AddDllDirectory(path);
						if (cookie != IntPtr.Zero)
						{
							_dllDirectoryCookies.Add(cookie);
						}
					}
					catch (Exception ex)
					{
						// Log the error if logging is available
						System.Diagnostics.Debug.WriteLine($"Failed to add DLL search path {path}: {ex.Message}");
					}
				}
			}
			catch (Exception ex)
			{
				// Log the error if logging is available
				System.Diagnostics.Debug.WriteLine($"Failed to set DLL search path: {ex.Message}");
			}
		}

		public bool IsEverythingAvailable()
		{
			lock (_dllSetupLock)
			{
				if (_availabilityChecked)
					return _everythingAvailable;

				try
				{
					// Check if the Everything DLL exists
					var appDirectory = AppContext.BaseDirectory;
					var possiblePaths = new[]
					{
						Path.Combine(appDirectory, EverythingDllName),
						Path.Combine(appDirectory, "Libraries", EverythingDllName),
						Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Everything", EverythingDllName),
						Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Everything", EverythingDllName)
					};

					bool dllExists = possiblePaths.Any(File.Exists);
					if (!dllExists)
					{
						System.Diagnostics.Debug.WriteLine($"No Everything DLL found in expected locations. Looking for: {EverythingDllName}");
						_everythingAvailable = false;
						_availabilityChecked = true;
						return false;
					}

					// Test if we can actually load and use the DLL
					Everything_Reset();
					var isLoaded = Everything_IsDBLoaded();
					Everything_CleanUp();
					
					_everythingAvailable = isLoaded;
					_availabilityChecked = true;
					
					App.Logger?.LogInformation($"Everything {(Environment.Is64BitProcess ? "64-bit" : "32-bit")} - Available: {_everythingAvailable}");
					return _everythingAvailable;
				}
				catch (DllNotFoundException ex)
				{
					System.Diagnostics.Debug.WriteLine($"Everything DLL not found: {ex.Message}");
					App.Logger?.LogWarning($"Everything DLL not found: {ex.Message}");
					_everythingAvailable = false;
					_availabilityChecked = true;
					return false;
				}
				catch (BadImageFormatException ex)
				{
					System.Diagnostics.Debug.WriteLine($"Everything DLL architecture mismatch: {ex.Message}. Process is {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}");
					App.Logger?.LogError($"Everything DLL architecture mismatch. Expected {(Environment.Is64BitProcess ? "64-bit" : "32-bit")} DLL: {ex.Message}");
					_everythingAvailable = false;
					_availabilityChecked = true;
					return false;
				}
				catch (EntryPointNotFoundException ex)
				{
					System.Diagnostics.Debug.WriteLine($"Everything DLL function not found: {ex.Message}");
					App.Logger?.LogError($"Everything DLL function not found: {ex.Message}");
					_everythingAvailable = false;
					_availabilityChecked = true;
					return false;
				}
				catch (Exception ex)
				{
					System.Diagnostics.Debug.WriteLine($"Error checking Everything availability: {ex.Message}");
					App.Logger?.LogError(ex, "Error checking Everything availability");
					_everythingAvailable = false;
					_availabilityChecked = true;
					return false;
				}
			}
		}

		public async Task<List<ListedItem>> SearchAsync(string query, string searchPath = null, CancellationToken cancellationToken = default)
		{
			if (!IsEverythingAvailable())
			{
				App.Logger?.LogWarning("Everything is not available for search");
				return new List<ListedItem>();
			}

			return await Task.Run(() =>
			{
				var results = new List<ListedItem>();

				try
				{
					Everything_Reset();

					// Set up the search query
					var searchQuery = BuildOptimizedQuery(query, searchPath);
					Everything_SetSearchW(searchQuery);
					Everything_SetMatchCase(false);
					Everything_SetRequestFlags(
						EVERYTHING_REQUEST_FILE_NAME | 
						EVERYTHING_REQUEST_PATH | 
						EVERYTHING_REQUEST_DATE_MODIFIED | 
						EVERYTHING_REQUEST_DATE_CREATED |
						EVERYTHING_REQUEST_SIZE |
						EVERYTHING_REQUEST_ATTRIBUTES);

					// Limit results to prevent overwhelming the UI
					Everything_SetMax(1000);

					// Execute the query
					if (!Everything_QueryW(true))
					{
						var error = Everything_GetLastError();
						if (error == EVERYTHING_ERROR_IPC)
						{
							App.Logger?.LogWarning("Everything IPC error: Everything is not running");
							return results;
						}
						else
						{
							App.Logger?.LogError($"Everything query failed with error code: {error}");
							return results;
						}
					}

					var numResults = Everything_GetNumResults();
					App.Logger?.LogDebug($"Everything returned {numResults} results");

					for (uint i = 0; i < numResults; i++)
					{
						if (cancellationToken.IsCancellationRequested)
							break;

						try
						{
							var fileName = Marshal.PtrToStringUni(Everything_GetResultFileName(i));
							var path = Marshal.PtrToStringUni(Everything_GetResultPath(i));
							
							if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(path))
								continue;

							var fullPath = Path.Combine(path, fileName);

							// Skip if it doesn't match our filter criteria
							if (!string.IsNullOrEmpty(searchPath) && searchPath != "Home" && 
								!fullPath.StartsWith(searchPath, StringComparison.OrdinalIgnoreCase))
								continue;

							var isFolder = Everything_IsFolderResult(i);
							var attributes = Everything_GetResultAttributes(i);
							var isHidden = (attributes & 0x02) != 0; // FILE_ATTRIBUTE_HIDDEN

							// Check user settings for hidden items
							if (isHidden && !_userSettingsService.FoldersSettingsService.ShowHiddenItems)
								continue;

							// Check for dot files
							if (fileName.StartsWith('.') && !_userSettingsService.FoldersSettingsService.ShowDotFiles)
								continue;

							Everything_GetResultDateModified(i, out long dateModified);
							Everything_GetResultDateCreated(i, out long dateCreated);
							Everything_GetResultSize(i, out long size);

							var item = new ListedItem(null)
							{
								PrimaryItemAttribute = isFolder ? StorageItemTypes.Folder : StorageItemTypes.File,
								ItemNameRaw = fileName,
								ItemPath = fullPath,
								ItemDateModifiedReal = DateTime.FromFileTime(dateModified),
								ItemDateCreatedReal = DateTime.FromFileTime(dateCreated),
								IsHiddenItem = isHidden,
								LoadFileIcon = true,  // Enable thumbnail loading with hybrid approach
								FileExtension = isFolder ? null : Path.GetExtension(fullPath),
								FileSizeBytes = isFolder ? 0 : size,
								FileSize = isFolder ? null : ByteSizeLib.ByteSize.FromBytes((ulong)size).ToBinaryString(),
								Opacity = isHidden ? Constants.UI.DimItemOpacity : 1
							};

							if (!isFolder)
							{
								item.ItemType = item.FileExtension?.Trim('.') + " " + Strings.File.GetLocalizedResource();
							}

							results.Add(item);
						}
						catch (Exception ex)
						{
							// Skip items that cause errors
							System.Diagnostics.Debug.WriteLine($"Error processing Everything result {i}: {ex.Message}");
						}
					}
				}
				catch (Exception ex)
				{
					App.Logger?.LogError(ex, "Everything search failed");
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

				return results;
			}, cancellationToken);
		}

		private string BuildOptimizedQuery(string query, string searchPath)
		{
			if (string.IsNullOrEmpty(searchPath) || searchPath == "Home")
			{
				return query;
			}
			else if (searchPath.Length <= 3) // Root drive like C:\
			{
				return $"path:\"{searchPath}\" {query}";
			}
			else
			{
				var escapedPath = searchPath.Replace("\"", "\"\"");
				return $"path:\"{escapedPath}\" {query}";
			}
		}

		public async Task<List<ListedItem>> FilterItemsAsync(IEnumerable<ListedItem> items, string query, CancellationToken cancellationToken = default)
		{
			// For filtering existing items, we'll use Everything's search on the current directory
			var firstItem = items.FirstOrDefault();
			if (firstItem == null || string.IsNullOrEmpty(firstItem.ItemPath))
				return new List<ListedItem>();

			// Get the directory path from the first item
			string directoryPath;
			try
			{
				directoryPath = Path.GetDirectoryName(firstItem.ItemPath);
				if (string.IsNullOrEmpty(directoryPath))
					return new List<ListedItem>();
			}
			catch (Exception ex)
			{
				App.Logger?.LogWarning(ex, "Failed to get directory path from {Path}", firstItem.ItemPath);
				return new List<ListedItem>();
			}
			
			// Search within this directory
			var searchResults = await SearchAsync(query, directoryPath, cancellationToken);
			
			// Return only items that exist in the original collection
			var itemPaths = new HashSet<string>(items.Select(i => i.ItemPath).Where(path => !string.IsNullOrEmpty(path)), StringComparer.OrdinalIgnoreCase);
			return searchResults.Where(r => !string.IsNullOrEmpty(r.ItemPath) && itemPaths.Contains(r.ItemPath)).ToList();
		}
	}
}