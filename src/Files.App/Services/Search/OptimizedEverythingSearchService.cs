// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Data.Models;
using Microsoft.Extensions.Logging;
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
	/// Optimized Everything search service with architecture-aware DLL loading,
	/// proper async patterns, and structured logging
	/// </summary>
	public sealed class OptimizedEverythingSearchService : IEverythingSearchService, IDisposable
	{
		// Everything API constants
		private const int EVERYTHING_REQUEST_FILE_NAME = 0x00000001;
		private const int EVERYTHING_REQUEST_PATH = 0x00000002;
		private const int EVERYTHING_REQUEST_DATE_MODIFIED = 0x00000040;
		private const int EVERYTHING_REQUEST_SIZE = 0x00000010;
		private const int EVERYTHING_REQUEST_DATE_CREATED = 0x00000020;
		private const int EVERYTHING_REQUEST_ATTRIBUTES = 0x00000100;

		#region P/Invoke declarations

		// Always use 64-bit DLL for x64 builds, 32-bit for x86
		[DllImport("Everything64.dll", EntryPoint = "Everything_SetSearchW", CharSet = CharSet.Unicode)]
		private static extern uint Everything_SetSearchW(string lpSearchString);

		[DllImport("Everything64.dll", EntryPoint = "Everything_SetMatchPath")]
		private static extern void Everything_SetMatchPath(bool bEnable);

		[DllImport("Everything64.dll", EntryPoint = "Everything_SetMatchCase")]
		private static extern void Everything_SetMatchCase(bool bEnable);

		[DllImport("Everything64.dll", EntryPoint = "Everything_SetMatchWholeWord")]
		private static extern void Everything_SetMatchWholeWord(bool bEnable);

		[DllImport("Everything64.dll", EntryPoint = "Everything_SetRegex")]
		private static extern void Everything_SetRegex(bool bEnable);

		[DllImport("Everything64.dll", EntryPoint = "Everything_SetMax")]
		private static extern void Everything_SetMax(uint dwMax);

		[DllImport("Everything64.dll", EntryPoint = "Everything_SetOffset")]
		private static extern void Everything_SetOffset(uint dwOffset);

		[DllImport("Everything64.dll", EntryPoint = "Everything_SetRequestFlags")]
		private static extern void Everything_SetRequestFlags(uint dwRequestFlags);

		[DllImport("Everything64.dll", EntryPoint = "Everything_QueryW")]
		private static extern bool Everything_QueryW(bool bWait);

		[DllImport("Everything64.dll", EntryPoint = "Everything_GetNumResults")]
		private static extern uint Everything_GetNumResults();

		[DllImport("Everything64.dll", EntryPoint = "Everything_GetLastError")]
		private static extern uint Everything_GetLastError();

		[DllImport("Everything64.dll", EntryPoint = "Everything_IsFileResult")]
		private static extern bool Everything_IsFileResult(uint nIndex);

		[DllImport("Everything64.dll", EntryPoint = "Everything_IsFolderResult")]
		private static extern bool Everything_IsFolderResult(uint nIndex);

		[DllImport("Everything64.dll", EntryPoint = "Everything_GetResultPath", CharSet = CharSet.Unicode)]
		private static extern IntPtr Everything_GetResultPath(uint nIndex);

		[DllImport("Everything64.dll", EntryPoint = "Everything_GetResultFileName", CharSet = CharSet.Unicode)]
		private static extern IntPtr Everything_GetResultFileName(uint nIndex);

		[DllImport("Everything64.dll", EntryPoint = "Everything_GetResultDateModified")]
		private static extern bool Everything_GetResultDateModified(uint nIndex, out long lpFileTime);

		[DllImport("Everything64.dll", EntryPoint = "Everything_GetResultDateCreated")]
		private static extern bool Everything_GetResultDateCreated(uint nIndex, out long lpFileTime);

		[DllImport("Everything64.dll", EntryPoint = "Everything_GetResultSize")]
		private static extern bool Everything_GetResultSize(uint nIndex, out long lpFileSize);

		[DllImport("Everything64.dll", EntryPoint = "Everything_GetResultAttributes")]
		private static extern uint Everything_GetResultAttributes(uint nIndex);

		[DllImport("Everything64.dll", EntryPoint = "Everything_Reset")]
		private static extern void Everything_Reset();

		[DllImport("Everything64.dll", EntryPoint = "Everything_CleanUp")]
		private static extern void Everything_CleanUp();

		[DllImport("Everything64.dll", EntryPoint = "Everything_IsDBLoaded")]
		private static extern bool Everything_IsDBLoaded();

		[DllImport("Everything64.dll", EntryPoint = "Everything_GetMajorVersion")]
		private static extern uint Everything_GetMajorVersion();

		[DllImport("Everything64.dll", EntryPoint = "Everything_GetMinorVersion")]
		private static extern uint Everything_GetMinorVersion();

		[DllImport("Everything64.dll", EntryPoint = "Everything_GetRevision")]
		private static extern uint Everything_GetRevision();

		#endregion

		// Win32 API imports for DLL loading
		[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
		private static extern bool SetDllDirectory(string lpPathName);

		[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
		private static extern IntPtr AddDllDirectory(string newDirectory);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool RemoveDllDirectory(IntPtr cookie);

		private readonly IUserSettingsService _userSettingsService;
		private readonly ILogger _logger;
		private readonly SemaphoreSlim _searchSemaphore;
		private readonly ConcurrentDictionary<string, (List<ListedItem> items, DateTime expiry)> _cache;

		private static readonly object _initLock = new();
		private static readonly List<IntPtr> _dllDirectoryCookies = new();
		private static bool _initialized = false;
		private static bool _everythingAvailable = false;
		private static string _everythingVersion = string.Empty;

		private bool _disposed = false;

		public OptimizedEverythingSearchService(IUserSettingsService userSettingsService)
		{
			_userSettingsService = userSettingsService;
			
			// Try to get logger, fallback to null if not available
			try
			{
				_logger = Ioc.Default.GetService<ILogger<OptimizedEverythingSearchService>>();
			}
			catch
			{
				_logger = null;
			}

			_searchSemaphore = new SemaphoreSlim(1, 1);
			_cache = new ConcurrentDictionary<string, (List<ListedItem>, DateTime)>();

			Initialize();
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
					LogError($"Failed to initialize Everything search service: {ex.Message}");
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

				// First try SetDllDirectory with app directory
				SetDllDirectory(appDirectory);

				foreach (var path in searchPaths.Where(Directory.Exists))
				{
					try
					{
						var cookie = AddDllDirectory(path);
						if (cookie != IntPtr.Zero)
						{
							_dllDirectoryCookies.Add(cookie);
							LogDebug($"Added DLL search path: {path}");
						}
					}
					catch (Exception ex)
					{
						LogWarning($"Failed to add DLL search path {path}: {ex.Message}");
					}
				}
			}
			catch (Exception ex)
			{
				LogError($"Failed to set up DLL search paths: {ex.Message}");
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

				LogInformation($"Everything {_everythingVersion} - Available: {_everythingAvailable}");
			}
			catch (DllNotFoundException ex)
			{
				LogWarning($"Everything DLL not found: {ex.Message}");
				_everythingAvailable = false;
			}
			catch (BadImageFormatException ex)
			{
				LogError($"Everything DLL architecture mismatch. Expected {(Environment.Is64BitProcess ? "64-bit" : "32-bit")} DLL: {ex.Message}");
				_everythingAvailable = false;
			}
			catch (Exception ex)
			{
				LogError($"Failed to check Everything availability: {ex.Message}");
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
				LogWarning("Everything is not available for search");
				return new List<ListedItem>();
			}

			// Check cache first
			var cacheKey = $"{searchPath ?? "Global"}|{query}";
			if (_cache.TryGetValue(cacheKey, out var cached) && cached.expiry > DateTime.UtcNow)
			{
				LogDebug($"Returning cached results for query: {query}");
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

				LogDebug($"Executing Everything query: {searchQuery}");

				if (!Everything_QueryW(true))
				{
					LogEverythingError();
					return results;
				}

				var numResults = Everything_GetNumResults();
				LogDebug($"Everything returned {numResults} results");

				for (uint i = 0; i < numResults; i++)
				{
					if (cancellationToken.IsCancellationRequested)
					{
						LogDebug("Search cancelled by user");
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
						LogWarning($"Failed to process search result at index {i}: {ex.Message}");
					}
				}
			}
			catch (Exception ex)
			{
				LogError($"Everything search failed: {ex.Message}");
			}
			finally
			{
				try
				{
					Everything_CleanUp();
				}
				catch (Exception ex)
				{
					LogWarning($"Error cleaning up Everything: {ex.Message}");
				}

				stopwatch.Stop();
				LogInformation($"Everything search completed in {stopwatch.ElapsedMilliseconds}ms with {results.Count} results");
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

			LogError($"Everything error: {errorMessage}");
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

		#region Logging helpers

		private void LogInformation(string message)
		{
			if (_logger != null)
				_logger.LogInformation(message);
			else
				App.Logger?.LogInformation($"[EverythingSearch] {message}");
		}

		private void LogWarning(string message)
		{
			if (_logger != null)
				_logger.LogWarning(message);
			else
				App.Logger?.LogWarning($"[EverythingSearch] {message}");
		}

		private void LogError(string message)
		{
			if (_logger != null)
				_logger.LogError(message);
			else
				App.Logger?.LogError($"[EverythingSearch] {message}");
		}

		private void LogDebug(string message)
		{
			if (_logger != null)
				_logger.LogDebug(message);
			else
				App.Logger?.LogDebug($"[EverythingSearch] {message}");
		}

		#endregion

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
						LogWarning($"Failed to remove DLL directory: {ex.Message}");
					}
				}
				_dllDirectoryCookies.Clear();
			}

			GC.SuppressFinalize(this);
		}
	}
}