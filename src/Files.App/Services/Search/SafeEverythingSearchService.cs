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
	/// Safe Everything search service implementing best practices from EverythingSearchClient
	/// to prevent memory issues and crashes
	/// </summary>
	public sealed class SafeEverythingSearchService : IEverythingSearchService, IDisposable
	{
		// Everything API constants
		private const int EVERYTHING_REQUEST_FILE_NAME = 0x00000001;
		private const int EVERYTHING_REQUEST_PATH = 0x00000002;
		private const int EVERYTHING_REQUEST_DATE_MODIFIED = 0x00000040;
		private const int EVERYTHING_REQUEST_SIZE = 0x00000010;
		private const int EVERYTHING_REQUEST_DATE_CREATED = 0x00000020;
		private const int EVERYTHING_REQUEST_ATTRIBUTES = 0x00000100;

		// Error codes
		private const uint EVERYTHING_ERROR_IPC = 2;
		private const uint EVERYTHING_ERROR_REGISTERCLASSEX = 3;
		private const uint EVERYTHING_ERROR_CREATEWINDOW = 4;
		private const uint EVERYTHING_ERROR_CREATETHREAD = 5;
		private const uint EVERYTHING_ERROR_INVALIDINDEX = 6;
		private const uint EVERYTHING_ERROR_INVALIDCALL = 7;

		// Safe limits based on EverythingSearchClient analysis
		private const uint SAFE_PAGE_SIZE = 1000;        // Process 1000 items at a time
		private const uint MAX_TOTAL_RESULTS = 10000;    // Absolute maximum results
		private const int QUERY_TIMEOUT_MS = 5000;       // 5 second timeout
		private const int RETRY_COUNT = 3;               // Number of retries on failure

		// Behavior when Everything is busy
		public enum BehaviorWhenBusy
		{
			WaitOrError,    // Wait with timeout, error if still busy
			WaitOrContinue, // Wait with timeout, continue anyway if still busy
			Error,          // Immediate error if busy
			Continue        // Continue anyway (may abort other queries)
		}

		#region P/Invoke declarations

		[DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
		private static extern uint Everything_SetSearchW(string lpSearchString);

		[DllImport("Everything64.dll")]
		private static extern void Everything_SetMatchPath(bool bEnable);

		[DllImport("Everything64.dll")]
		private static extern void Everything_SetMatchCase(bool bEnable);

		[DllImport("Everything64.dll")]
		private static extern void Everything_SetMatchWholeWord(bool bEnable);

		[DllImport("Everything64.dll")]
		private static extern void Everything_SetRegex(bool bEnable);

		[DllImport("Everything64.dll")]
		private static extern void Everything_SetMax(uint dwMax);

		[DllImport("Everything64.dll")]
		private static extern void Everything_SetOffset(uint dwOffset);

		[DllImport("Everything64.dll")]
		private static extern void Everything_SetRequestFlags(uint dwRequestFlags);

		[DllImport("Everything64.dll")]
		private static extern bool Everything_QueryW(bool bWait);

		[DllImport("Everything64.dll")]
		private static extern uint Everything_GetNumResults();

		[DllImport("Everything64.dll")]
		private static extern uint Everything_GetTotResults();

		[DllImport("Everything64.dll")]
		private static extern uint Everything_GetLastError();

		[DllImport("Everything64.dll")]
		private static extern bool Everything_IsFileResult(uint nIndex);

		[DllImport("Everything64.dll")]
		private static extern bool Everything_IsFolderResult(uint nIndex);

		[DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
		private static extern IntPtr Everything_GetResultPath(uint nIndex);

		[DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
		private static extern IntPtr Everything_GetResultFileName(uint nIndex);

		[DllImport("Everything64.dll")]
		private static extern bool Everything_GetResultDateModified(uint nIndex, out long lpFileTime);

		[DllImport("Everything64.dll")]
		private static extern bool Everything_GetResultDateCreated(uint nIndex, out long lpFileTime);

		[DllImport("Everything64.dll")]
		private static extern bool Everything_GetResultSize(uint nIndex, out long lpFileSize);

		[DllImport("Everything64.dll")]
		private static extern uint Everything_GetResultAttributes(uint nIndex);

		[DllImport("Everything64.dll")]
		private static extern void Everything_Reset();

		[DllImport("Everything64.dll")]
		private static extern void Everything_CleanUp();

		[DllImport("Everything64.dll")]
		private static extern bool Everything_IsDBLoaded();

		[DllImport("Everything64.dll")]
		private static extern uint Everything_GetMajorVersion();

		[DllImport("Everything64.dll")]
		private static extern uint Everything_GetMinorVersion();

		[DllImport("Everything64.dll")]
		private static extern uint Everything_GetRevision();

		[DllImport("Everything64.dll")]
		private static extern bool Everything_IsFastSort(uint sortType);

		[DllImport("Everything64.dll")]
		private static extern void Everything_SortResultsByPath();

		#endregion

		// Win32 API imports for DLL loading
		[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
		private static extern bool SetDllDirectory(string lpPathName);

		[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
		private static extern IntPtr AddDllDirectory(string newDirectory);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool RemoveDllDirectory(IntPtr cookie);

		// Known problematic paths that should not use Everything
		private static readonly HashSet<string> ProblematicPaths = new(StringComparer.OrdinalIgnoreCase)
		{
			@"C:\",
			@"C:\Windows",
			@"C:\Windows\System32",
			@"C:\Windows\SysWOW64",
			@"C:\Windows\WinSxS",
			@"C:\Program Files",
			@"C:\Program Files (x86)",
			@"C:\ProgramData",
			@"C:\Users",
			@"C:\$Recycle.Bin",
			@"D:\",
			@"E:\",
			@"F:\"
		};

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

		public SafeEverythingSearchService(IUserSettingsService userSettingsService)
		{
			_userSettingsService = userSettingsService;
			
			try
			{
				_logger = Ioc.Default.GetService<ILogger<SafeEverythingSearchService>>();
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
				Everything_Reset();
				
				var major = Everything_GetMajorVersion();
				var minor = Everything_GetMinorVersion();
				var revision = Everything_GetRevision();
				_everythingVersion = $"{major}.{minor}.{revision}";
				
				_everythingAvailable = Everything_IsDBLoaded();
				
				Everything_CleanUp();

				LogInformation($"Everything {_everythingVersion} - Available: {_everythingAvailable}");
			}
			catch (DllNotFoundException ex)
			{
				LogWarning($"Everything DLL not found: {ex.Message}");
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
			// Validate inputs
			if (!ValidateSearchRequest(query, searchPath))
			{
				LogWarning($"Invalid search request - Query: '{query}', Path: '{searchPath}'");
				return new List<ListedItem>();
			}

			if (!IsEverythingAvailable())
			{
				LogWarning("Everything is not available for search");
				return new List<ListedItem>();
			}

			// Check cache
			var cacheKey = $"{searchPath ?? "Global"}|{query}";
			if (_cache.TryGetValue(cacheKey, out var cached) && cached.expiry > DateTime.UtcNow)
			{
				LogDebug($"Returning cached results for query: {query}");
				return cached.items;
			}

			await _searchSemaphore.WaitAsync(cancellationToken);
			try
			{
				var results = await PerformSafeSearchAsync(query, searchPath, cancellationToken);
				
				// Cache results
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

		private bool ValidateSearchRequest(string query, string searchPath)
		{
			// Reject empty or very short queries
			if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
				return false;

			// Reject wildcards only
			if (query.Trim() == "*" || query.Trim() == "*.*")
				return false;

			// For now, allow all paths including C:\ to support root drive searches
			// The pagination and limits will prevent memory issues
			return true;
		}

		private async Task<List<ListedItem>> PerformSafeSearchAsync(string query, string searchPath, CancellationToken cancellationToken)
		{
			var results = new List<ListedItem>();
			var stopwatch = System.Diagnostics.Stopwatch.StartNew();
			int retryCount = 0;

			while (retryCount < RETRY_COUNT)
			{
				try
				{
					results = await Task.Run(() => ExecutePaginatedSearch(query, searchPath, cancellationToken), cancellationToken);
					break; // Success, exit retry loop
				}
				catch (OperationCanceledException)
				{
					LogDebug("Search was cancelled");
					return results;
				}
				catch (Exception ex)
				{
					retryCount++;
					LogWarning($"Search attempt {retryCount} failed: {ex.Message}");
					
					if (retryCount < RETRY_COUNT)
					{
						// Wait before retry with exponential backoff
						await Task.Delay(100 * retryCount, cancellationToken);
					}
					else
					{
						LogError($"Search failed after {RETRY_COUNT} attempts");
						throw;
					}
				}
			}

			stopwatch.Stop();
			LogInformation($"Search completed in {stopwatch.ElapsedMilliseconds}ms with {results.Count} results");
			return results;
		}

		private List<ListedItem> ExecutePaginatedSearch(string query, string searchPath, CancellationToken cancellationToken)
		{
			var results = new List<ListedItem>();
			uint offset = 0;
			uint totalProcessed = 0;
			bool hasMoreResults = true;

			try
			{
				var searchQuery = BuildSafeQuery(query, searchPath);
				LogDebug($"Executing search with query: {searchQuery}");
				LogDebug($"Search path: '{searchPath}'");

				while (hasMoreResults && totalProcessed < MAX_TOTAL_RESULTS && !cancellationToken.IsCancellationRequested)
				{
					Everything_Reset();
					Everything_SetSearchW(searchQuery);
					Everything_SetMatchCase(false);
					Everything_SetRequestFlags(
						EVERYTHING_REQUEST_FILE_NAME |
						EVERYTHING_REQUEST_PATH |
						EVERYTHING_REQUEST_DATE_MODIFIED |
						EVERYTHING_REQUEST_DATE_CREATED |
						EVERYTHING_REQUEST_SIZE |
						EVERYTHING_REQUEST_ATTRIBUTES);

					Everything_SetOffset(offset);
					Everything_SetMax(SAFE_PAGE_SIZE);

					// Sort by path for better performance
					Everything_SortResultsByPath();

					LogDebug($"Querying page at offset {offset}");

					if (!Everything_QueryW(true))
					{
						var error = Everything_GetLastError();
						if (error == EVERYTHING_ERROR_IPC)
						{
							throw new InvalidOperationException("Everything IPC is not available. Is Everything running?");
						}
						LogError($"Everything query failed with error code: {error}");
						break;
					}

					var numResults = Everything_GetNumResults();
					var totalResults = Everything_GetTotResults();

					LogDebug($"Retrieved {numResults} results in this page (total available: {totalResults})");

					// Process results
					var itemsCreated = 0;
					var itemsIncluded = 0;
					for (uint i = 0; i < numResults && !cancellationToken.IsCancellationRequested; i++)
					{
						try
						{
							var item = CreateListedItem(i);
							if (item != null)
							{
								itemsCreated++;
								if (ShouldIncludeItem(item, searchPath))
								{
									results.Add(item);
									totalProcessed++;
									itemsIncluded++;
								}
								else if (i < 5)
								{
									LogInformation($"Item excluded by ShouldIncludeItem: {item.ItemPath} (searchPath: '{searchPath}')");
								}
							}
						}
						catch (Exception ex)
						{
							LogDebug($"Failed to process result at index {i}: {ex.Message}");
						}
					}
					
					if (itemsCreated > 0)
					{
						LogInformation($"Created {itemsCreated} items, included {itemsIncluded} items for searchPath: '{searchPath}'");
					}

					// Clean up after each page
					Everything_CleanUp();

					// Check if we have more results
					offset += numResults;
					hasMoreResults = numResults == SAFE_PAGE_SIZE && offset < totalResults;

					// Allow other operations between pages
					if (hasMoreResults && offset % 5000 == 0)
					{
						Thread.Yield();
					}
				}
			}
			catch (Exception ex)
			{
				LogError($"Search execution failed: {ex.Message}");
				throw;
			}
			finally
			{
				try
				{
					Everything_CleanUp();
				}
				catch (Exception ex)
				{
					LogWarning($"Error during cleanup: {ex.Message}");
				}
			}

			return results;
		}

		private string BuildSafeQuery(string query, string searchPath)
		{
			// For very generic queries, make them more specific
			if (query.Length <= 2)
			{
				query = $"ww:{query}"; // Whole word match
			}

			// Add path constraint if specified
			if (!string.IsNullOrEmpty(searchPath))
			{
				var escapedPath = searchPath.Replace("\"", "\"\"");
				// Everything expects paths without trailing backslash in path: queries
				if (escapedPath.EndsWith("\\") && escapedPath.Length > 3)
				{
					escapedPath = escapedPath.TrimEnd('\\');
				}
				var finalQuery = $"path:\"{escapedPath}\" {query}";
				LogInformation($"Built query with path constraint: {finalQuery}");
				return finalQuery;
			}

			// For global searches without a specific path
			return query;
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
			{
				return true;
			}

			// Normalize both paths for comparison
			var normalizedSearchPath = searchPath.TrimEnd('\\');
			var normalizedItemPath = Path.GetFullPath(item.ItemPath);

			// For drive roots like C:\, include all items on that drive
			if (normalizedSearchPath.Length <= 3 && normalizedSearchPath.EndsWith(":\\"))
			{
				normalizedSearchPath = normalizedSearchPath.TrimEnd('\\');
			}
			
			// Special handling for drive roots
			if (normalizedSearchPath.Length == 2 && normalizedSearchPath[1] == ':')
			{
				var result = normalizedItemPath.StartsWith(normalizedSearchPath + "\\", StringComparison.OrdinalIgnoreCase);
				return result;
			}

			var includeResult = normalizedItemPath.StartsWith(normalizedSearchPath, StringComparison.OrdinalIgnoreCase);
			return includeResult;
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
			var itemsList = items.ToList();
			if (!itemsList.Any() || !ValidateSearchRequest(query, null))
				return itemsList;

			var firstItem = itemsList.First();
			var directoryPath = Path.GetDirectoryName(firstItem.ItemPath);

			// Don't use Everything for filtering if the directory is problematic
			if (!ValidateSearchRequest(query, directoryPath))
				return itemsList;

			var searchResults = await SearchAsync(query, directoryPath, cancellationToken);
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