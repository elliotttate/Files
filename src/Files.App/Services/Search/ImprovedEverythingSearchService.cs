// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Data.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.IO;
using Windows.Storage;
using ByteSize = ByteSizeLib.ByteSize;
using FileAttributes = System.IO.FileAttributes;

namespace Files.App.Services.Search
{
	/// <summary>
	/// Improved Everything search service with architecture-aware DLL loading,
	/// proper async patterns, and structured logging
	/// </summary>
	public sealed class ImprovedEverythingSearchService : IEverythingSearchService, IDisposable
	{
		// Everything API constants
		private const int EVERYTHING_REQUEST_FILE_NAME = 0x00000001;
		private const int EVERYTHING_REQUEST_PATH = 0x00000002;
		private const int EVERYTHING_REQUEST_DATE_MODIFIED = 0x00000040;
		private const int EVERYTHING_REQUEST_SIZE = 0x00000010;
		private const int EVERYTHING_REQUEST_DATE_CREATED = 0x00000020;
		private const int EVERYTHING_REQUEST_ATTRIBUTES = 0x00000100;

		// Architecture-aware DLL name
		private static readonly string EverythingDllName = Environment.Is64BitProcess ? "Everything64.dll" : "Everything32.dll";

		#region P/Invoke declarations with architecture awareness

		// Using DllImport with dynamic DLL name resolution via DllImportResolver
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

		[DllImport("Everything", EntryPoint = "Everything_GetMajorVersion")]
		private static extern uint Everything_GetMajorVersion();

		[DllImport("Everything", EntryPoint = "Everything_GetMinorVersion")]
		private static extern uint Everything_GetMinorVersion();

		[DllImport("Everything", EntryPoint = "Everything_GetRevision")]
		private static extern uint Everything_GetRevision();

		#endregion

		// Win32 API imports for DLL loading
		[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
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
		private readonly ILogger<ImprovedEverythingSearchService> _logger;
		private readonly SemaphoreSlim _searchSemaphore;
		private readonly ConcurrentDictionary<string, (List<ListedItem> items, DateTime expiry)> _cache;

		private static readonly object _initLock = new();
		private static IntPtr _everythingModule = IntPtr.Zero;
		private static readonly List<IntPtr> _dllDirectoryCookies = new();
		private static bool _initialized = false;
		private static bool _everythingAvailable = false;
		private static string _everythingVersion = string.Empty;

		private bool _disposed = false;

		static ImprovedEverythingSearchService()
		{
			// Set up DLL import resolver for architecture-aware loading
			NativeLibrary.SetDllImportResolver(typeof(ImprovedEverythingSearchService).Assembly, DllImportResolver);
		}

		public ImprovedEverythingSearchService(IUserSettingsService userSettingsService)
		{
			_userSettingsService = userSettingsService;
			_logger = Ioc.Default.GetService<ILogger<ImprovedEverythingSearchService>>() ?? 
					  NullLogger<ImprovedEverythingSearchService>.Instance;
			_searchSemaphore = new SemaphoreSlim(1, 1);
			_cache = new ConcurrentDictionary<string, (List<ListedItem>, DateTime)>();

			Initialize();
		}

		private static IntPtr DllImportResolver(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
		{
			if (libraryName == "Everything")
			{
				lock (_initLock)
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

		private void Initialize()
		{
			lock (_initLock)
			{
				if (_initialized)
					return;

				try
				{
					SetupDllSearchPaths();
					CheckEverythingAvailability();
					_initialized = true;
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Failed to initialize Everything search service");
					_everythingAvailable = false;
				}
			}
		}

		private void SetupDllSearchPaths()
		{
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

				foreach (var path in searchPaths.Where(Directory.Exists))
				{
					try
					{
						var cookie = AddDllDirectory(path);
						if (cookie != IntPtr.Zero)
						{
							_dllDirectoryCookies.Add(cookie);
							_logger.LogDebug($"Added DLL search path: {path}");
						}
					}
					catch (Exception ex)
					{
						_logger.LogWarning(ex, $"Failed to add DLL search path: {path}");
					}
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to set up DLL search paths");
			}
		}

		private void CheckEverythingAvailability()
		{
			try
			{
				// Check if we can call Everything functions
				Everything_Reset();
				
				// Get version information
				var major = Everything_GetMajorVersion();
				var minor = Everything_GetMinorVersion();
				var revision = Everything_GetRevision();
				_everythingVersion = $"{major}.{minor}.{revision}";
				
				// Check if database is loaded
				_everythingAvailable = Everything_IsDBLoaded();
				
				Everything_CleanUp();

				_logger.LogInformation($"Everything {_everythingVersion} - Available: {_everythingAvailable}");
			}
			catch (DllNotFoundException ex)
			{
				_logger.LogWarning($"Everything DLL not found: {ex.Message}");
				_everythingAvailable = false;
			}
			catch (BadImageFormatException ex)
			{
				_logger.LogError($"Everything DLL architecture mismatch. Expected {(Environment.Is64BitProcess ? "64-bit" : "32-bit")} DLL: {ex.Message}");
				_everythingAvailable = false;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to check Everything availability");
				_everythingAvailable = false;
			}
		}

		public bool IsEverythingAvailable()
		{
			lock (_initLock)
			{
				if (!_initialized)
					Initialize();
				
				return _everythingAvailable;
			}
		}

		public async Task<List<ListedItem>> SearchAsync(string query, string searchPath = null, CancellationToken cancellationToken = default)
		{
			if (!IsEverythingAvailable())
			{
				_logger.LogWarning("Everything is not available for search");
				return new List<ListedItem>();
			}

			// Check cache first
			var cacheKey = $"{searchPath ?? "Global"}|{query}";
			if (_cache.TryGetValue(cacheKey, out var cached) && cached.expiry > DateTime.UtcNow)
			{
				_logger.LogDebug($"Returning cached results for query: {query}");
				return cached.items;
			}

			await _searchSemaphore.WaitAsync(cancellationToken);
			try
			{
				var results = await Task.Run(() => PerformSearch(query, searchPath, cancellationToken), cancellationToken);
				
				// Cache results for 60 seconds
				_cache[cacheKey] = (results, DateTime.UtcNow.AddSeconds(60));
				
				// Clean old cache entries
				CleanCache();
				
				return results;
			}
			finally
			{
				_searchSemaphore.Release();
			}
		}

		private List<ListedItem> PerformSearch(string query, string searchPath, CancellationToken cancellationToken)
		{
			var results = new List<ListedItem>();
			var stopwatch = System.Diagnostics.Stopwatch.StartNew();

			try
			{
				Everything_Reset();

				// Build optimized query
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

				// Limit results for performance
				Everything_SetMax(1000);

				_logger.LogDebug($"Executing Everything query: {searchQuery}");

				if (!Everything_QueryW(true))
				{
					LogEverythingError();
					return results;
				}

				var numResults = Everything_GetNumResults();
				_logger.LogDebug($"Everything returned {numResults} results");

				for (uint i = 0; i < numResults; i++)
				{
					if (cancellationToken.IsCancellationRequested)
					{
						_logger.LogDebug("Search cancelled by user");
						break;
					}

					try
					{
						var item = CreateListedItem(i);
						if (item != null && ShouldIncludeItem(item, searchPath))
						{
							results.Add(item);
						}
					}
					catch (Exception ex)
					{
						_logger.LogWarning(ex, $"Failed to process search result at index {i}");
					}
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Everything search failed");
			}
			finally
			{
				try
				{
					Everything_CleanUp();
				}
				catch (Exception ex)
				{
					_logger.LogWarning(ex, "Error cleaning up Everything");
				}

				stopwatch.Stop();
				_logger.LogInformation($"Everything search completed in {stopwatch.ElapsedMilliseconds}ms with {results.Count} results");
			}

			return results;
		}

		private ListedItem CreateListedItem(uint index)
		{
			var fileName = Marshal.PtrToStringUni(Everything_GetResultFileName(index));
			var path = Marshal.PtrToStringUni(Everything_GetResultPath(index));

			if (string.IsNullOrEmpty(fileName) || string.IsNullOrEmpty(path))
				return null;

			var fullPath = Path.Combine(path, fileName);
			var isFolder = Everything_IsFolderResult(index);

			// Skip dot files if settings say so
			if (fileName.StartsWith('.') && !_userSettingsService.FoldersSettingsService.ShowDotFiles)
				return null;

			// Check attributes
			var attributes = Everything_GetResultAttributes(index);
			var isHidden = (attributes & 0x02) != 0; // FILE_ATTRIBUTE_HIDDEN

			if (isHidden && !_userSettingsService.FoldersSettingsService.ShowHiddenItems)
				return null;

			var item = new ListedItem(null)
			{
				PrimaryItemAttribute = isFolder ? StorageItemTypes.Folder : StorageItemTypes.File,
				ItemNameRaw = fileName,
				ItemPath = fullPath,
				IsHiddenItem = isHidden,
				LoadFileIcon = true,  // Enable thumbnail loading with hybrid approach
				FileExtension = isFolder ? null : Path.GetExtension(fullPath),
				Opacity = isHidden ? Constants.UI.DimItemOpacity : 1
			};

			// Set dates
			if (Everything_GetResultDateModified(index, out long dateModified))
				item.ItemDateModifiedReal = DateTime.FromFileTime(dateModified);

			if (Everything_GetResultDateCreated(index, out long dateCreated))
				item.ItemDateCreatedReal = DateTime.FromFileTime(dateCreated);

			// Set size for files
			if (!isFolder && Everything_GetResultSize(index, out long size))
			{
				item.FileSizeBytes = size;
				item.FileSize = ByteSize.FromBytes((ulong)size).ToBinaryString();
			}

			// Set item type
			if (!isFolder && !string.IsNullOrEmpty(item.FileExtension))
			{
				item.ItemType = item.FileExtension.Trim('.') + " " + Strings.File.GetLocalizedResource();
			}

			return item;
		}

		private bool ShouldIncludeItem(ListedItem item, string searchPath)
		{
			if (string.IsNullOrEmpty(searchPath) || searchPath == "Home")
				return true;

			return item.ItemPath.StartsWith(searchPath, StringComparison.OrdinalIgnoreCase);
		}

		private string BuildOptimizedQuery(string query, string searchPath)
		{
			if (string.IsNullOrEmpty(searchPath) || searchPath == "Home")
			{
				// Global search
				return query;
			}
			else if (searchPath.Length <= 3) // Root drive like C:\
			{
				// For root drives, use optimized syntax
				return $"path:\"{searchPath}\" {query}";
			}
			else
			{
				// For specific paths, use exact path matching
				var escapedPath = searchPath.Replace("\"", "\"\"");
				return $"path:\"{escapedPath}\" {query}";
			}
		}

		private void LogEverythingError()
		{
			var lastError = Everything_GetLastError();
			var errorMessage = lastError switch
			{
				0 => "Success",
				1 => "Failed to allocate memory for search query",
				2 => "IPC not available. Is Everything running?",
				3 => "Failed to register window class",
				4 => "Failed to create window",
				5 => "Failed to create thread",
				6 => "Invalid index",
				7 => "Invalid call",
				_ => $"Unknown error code {lastError}"
			};

			_logger.LogError($"Everything error: {errorMessage}");
		}

		private void CleanCache()
		{
			var expiredKeys = _cache
				.Where(kvp => kvp.Value.expiry < DateTime.UtcNow)
				.Select(kvp => kvp.Key)
				.ToList();

			foreach (var key in expiredKeys)
			{
				_cache.TryRemove(key, out _);
			}
		}

		public async Task<List<ListedItem>> FilterItemsAsync(IEnumerable<ListedItem> items, string query, CancellationToken cancellationToken = default)
		{
			if (!IsEverythingAvailable())
				return items.ToList();

			var itemsList = items.ToList();
			if (!itemsList.Any())
				return itemsList;

			// Get the directory from the first item
			var firstItem = itemsList.First();
			var directoryPath = Path.GetDirectoryName(firstItem.ItemPath);

			// Search in that directory
			var searchResults = await SearchAsync(query, directoryPath, cancellationToken);

			// Filter to only items that were in the original list
			var itemPaths = new HashSet<string>(itemsList.Select(i => i.ItemPath), StringComparer.OrdinalIgnoreCase);
			return searchResults.Where(r => itemPaths.Contains(r.ItemPath)).ToList();
		}

		public void Dispose()
		{
			if (_disposed)
				return;

			_disposed = true;

			_searchSemaphore?.Dispose();
			_cache?.Clear();

			lock (_initLock)
			{
				// Remove DLL directories
				foreach (var cookie in _dllDirectoryCookies)
				{
					try
					{
						RemoveDllDirectory(cookie);
					}
					catch (Exception ex)
					{
						_logger.LogWarning(ex, "Failed to remove DLL directory");
					}
				}
				_dllDirectoryCookies.Clear();

				// Free the Everything module if loaded
				if (_everythingModule != IntPtr.Zero)
				{
					try
					{
						FreeLibrary(_everythingModule);
						_everythingModule = IntPtr.Zero;
					}
					catch (Exception ex)
					{
						_logger.LogWarning(ex, "Failed to free Everything module");
					}
				}
			}

			GC.SuppressFinalize(this);
		}
	}
}