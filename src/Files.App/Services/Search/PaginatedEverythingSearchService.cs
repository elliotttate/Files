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
	/// Paginated Everything search service that prevents memory issues by limiting results
	/// and using proper pagination techniques
	/// </summary>
	public sealed class PaginatedEverythingSearchService : IEverythingSearchService, IDisposable
	{
		// Everything API constants
		private const int EVERYTHING_REQUEST_FILE_NAME = 0x00000001;
		private const int EVERYTHING_REQUEST_PATH = 0x00000002;
		private const int EVERYTHING_REQUEST_DATE_MODIFIED = 0x00000040;
		private const int EVERYTHING_REQUEST_SIZE = 0x00000010;
		private const int EVERYTHING_REQUEST_DATE_CREATED = 0x00000020;
		private const int EVERYTHING_REQUEST_ATTRIBUTES = 0x00000100;

		// Pagination constants to prevent memory issues
		private const uint MAX_RESULTS_PER_QUERY = 100;  // Limit each query to 100 results
		private const uint MAX_TOTAL_RESULTS = 1000;     // Maximum total results to return
		private const uint RESULTS_PAGE_SIZE = 50;       // Process results in pages of 50

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
		private static extern void Everything_SetSort(uint dwSortType);

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
		private readonly ConcurrentDictionary<string, CachedSearchResult> _cache;

		private static readonly object _initLock = new();
		private static readonly List<IntPtr> _dllDirectoryCookies = new();
		private static bool _initialized = false;
		private static bool _everythingAvailable = false;
		private static string _everythingVersion = string.Empty;

		private bool _disposed = false;

		private class CachedSearchResult
		{
			public List<ListedItem> Items { get; set; }
			public DateTime Expiry { get; set; }
			public bool IsComplete { get; set; }
			public uint TotalCount { get; set; }
		}

		public PaginatedEverythingSearchService(IUserSettingsService userSettingsService)
		{
			_userSettingsService = userSettingsService;
			
			// Try to get logger, fallback to App.Logger if not available
			try
			{
				_logger = Ioc.Default.GetService<ILogger<PaginatedEverythingSearchService>>();
			}
			catch
			{
				_logger = null;
			}

			_searchSemaphore = new SemaphoreSlim(1, 1);
			_cache = new ConcurrentDictionary<string, CachedSearchResult>();

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
			catch (BadImageFormatException ex)
			{
				LogError($"Everything DLL architecture mismatch: {ex.Message}");
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

			// Safety check: Skip Everything for certain problematic queries
			if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
			{
				LogWarning($"Query too short for Everything search: '{query}'");
				return new List<ListedItem>();
			}

			// Skip Everything for root drive searches with very generic queries
			if ((string.IsNullOrEmpty(searchPath) || searchPath == "Home" || searchPath.Length <= 3) && 
			    (query == "*" || query == "*.*" || query.Length < 3))
			{
				LogWarning($"Skipping Everything for too generic query: '{query}' in path '{searchPath}'");
				return new List<ListedItem>();
			}

			// Check cache first
			var cacheKey = $"{searchPath ?? "Global"}|{query}";
			if (_cache.TryGetValue(cacheKey, out var cached) && cached.Expiry > DateTime.UtcNow)
			{
				LogDebug($"Returning cached results for query: {query}");
				return cached.Items;
			}

			await _searchSemaphore.WaitAsync(cancellationToken);
			try
			{
				var results = await Task.Run(() => PerformPaginatedSearch(query, searchPath, cancellationToken), cancellationToken);
				
				// Cache results for 60 seconds
				_cache[cacheKey] = new CachedSearchResult
				{
					Items = results,
					Expiry = DateTime.UtcNow.AddSeconds(60),
					IsComplete = results.Count < MAX_TOTAL_RESULTS
				};
				
				// Clean old cache entries
				CleanCache();
				
				return results;
			}
			finally
			{
				_searchSemaphore.Release();
			}
		}

		private List<ListedItem> PerformPaginatedSearch(string query, string searchPath, CancellationToken cancellationToken)
		{
			var results = new List<ListedItem>();
			var stopwatch = System.Diagnostics.Stopwatch.StartNew();
			uint offset = 0;
			uint totalProcessed = 0;

			try
			{
				var searchQuery = BuildOptimizedQuery(query, searchPath);
				LogInformation($"Starting paginated search for: {searchQuery}");

				// First, get total count with a minimal query
				Everything_Reset();
				Everything_SetSearchW(searchQuery);
				Everything_SetMatchCase(false);
				Everything_SetMax(0); // Get count only
				
				if (!Everything_QueryW(true))
				{
					LogEverythingError();
					return results;
				}

				var totalResults = Everything_GetTotResults();
				LogInformation($"Total results available: {totalResults}");

				if (totalResults == 0)
					return results;

				// Warn if there are too many results
				if (totalResults > MAX_TOTAL_RESULTS)
				{
					LogWarning($"Query returned {totalResults} results, limiting to {MAX_TOTAL_RESULTS} to prevent memory issues");
				}

				// Now fetch results in pages
				while (offset < Math.Min(totalResults, MAX_TOTAL_RESULTS) && !cancellationToken.IsCancellationRequested)
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
					Everything_SetMax(Math.Min(RESULTS_PAGE_SIZE, MAX_TOTAL_RESULTS - offset));

					LogDebug($"Fetching results {offset} to {offset + RESULTS_PAGE_SIZE}");

					if (!Everything_QueryW(true))
					{
						LogEverythingError();
						break;
					}

					var numResults = Everything_GetNumResults();
					LogDebug($"Retrieved {numResults} results in this page");

					for (uint i = 0; i < numResults && !cancellationToken.IsCancellationRequested; i++)
					{
						try
						{
							var item = CreateListedItem(i);
							if (item != null)
							{
								if (ShouldIncludeItem(item, searchPath))
								{
									results.Add(item);
									totalProcessed++;
								}
								else
								{
									LogDebug($"Item excluded by path filter: {item.ItemPath} (searchPath: {searchPath})");
								}
							}
							else
							{
								LogDebug($"CreateListedItem returned null for index {i}");
							}
						}
						catch (Exception ex)
						{
							LogWarning($"Failed to process search result at index {i}: {ex.Message}");
						}
					}

					// Clean up after each page to free memory
					Everything_CleanUp();

					offset += RESULTS_PAGE_SIZE;

					// Allow other operations to proceed
					if (offset % 1000 == 0)
					{
						System.Threading.Thread.Yield();
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
				LogInformation($"Paginated search completed in {stopwatch.ElapsedMilliseconds}ms with {results.Count} results");
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
			// For very short queries that might return too many results, make them more specific
			if (query.Length < 3)
			{
				// Force whole word matching for very short queries
				query = $"ww:{query}";
			}
			
			// Add file size limits for root searches to prevent memory issues
			if (string.IsNullOrEmpty(searchPath) || searchPath == "Home")
			{
				// For global searches, exclude very large files and system directories
				// This helps prevent Everything from running out of memory
				return $"{query} !C:\\Windows\\ !C:\\ProgramData\\ size:<1gb";
			}
			else if (searchPath.Length <= 3) // Root drive like C:\
			{
				// For root drive searches, exclude system directories and limit size
				return $"path:\"{searchPath}\" {query} !\\Windows\\ !\\ProgramData\\ !\\$Recycle.Bin\\ size:<1gb";
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
				.Where(kvp => kvp.Value.Expiry < DateTime.UtcNow)
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