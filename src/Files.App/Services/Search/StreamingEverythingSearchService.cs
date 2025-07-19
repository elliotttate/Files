// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Data.Models;
using Microsoft.Extensions.Logging;
using System.Buffers;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.IO;
using System.Linq;
using System.Reflection;
using Windows.Storage;
using ByteSize = ByteSizeLib.ByteSize;
using FileAttributes = System.IO.FileAttributes;

namespace Files.App.Services.Search
{
	/// <summary>
	/// Everything search error codes with descriptions
	/// </summary>
	public enum EverythingError
	{
		[Description("Success")]
		Ok = 0,
		[Description("Failed to allocate memory for the search query")]
		Memory = 1,
		[Description("IPC is not available. Make sure Everything is running")]
		Ipc = 2,
		[Description("Failed to register window class")]
		RegisterClassEx = 3,
		[Description("Failed to create window")]
		CreateWindow = 4,
		[Description("Failed to create thread")]
		CreateThread = 5,
		[Description("Invalid index")]
		InvalidIndex = 6,
		[Description("Invalid call")]
		InvalidCall = 7
	}

	/// <summary>
	/// Exception thrown by Everything search operations
	/// </summary>
	public class EverythingException : Exception
	{
		public EverythingError ErrorCode { get; }

		public EverythingException(EverythingError errorCode) 
			: base(GetErrorDescription(errorCode))
		{
			ErrorCode = errorCode;
		}

		private static string GetErrorDescription(EverythingError errorCode)
		{
			var field = typeof(EverythingError).GetField(errorCode.ToString());
			var attributes = field?.GetCustomAttributes(typeof(DescriptionAttribute), false);
			var attribute = attributes?.FirstOrDefault() as DescriptionAttribute;
			return attribute?.Description ?? $"Unknown error code {(int)errorCode}";
		}
	}

	/// <summary>
	/// Streaming Everything search service with memory-efficient result streaming,
	/// structured error handling, and performance optimizations
	/// </summary>
	public sealed class StreamingEverythingSearchService : IEverythingSearchService, IDisposable
	{
		// Everything API constants
		private const int EVERYTHING_REQUEST_FILE_NAME = 0x00000001;
		private const int EVERYTHING_REQUEST_PATH = 0x00000002;
		private const int EVERYTHING_REQUEST_FULL_PATH_AND_FILENAME = 0x00000004;
		private const int EVERYTHING_REQUEST_EXTENSION = 0x00000008;
		private const int EVERYTHING_REQUEST_SIZE = 0x00000010;
		private const int EVERYTHING_REQUEST_DATE_CREATED = 0x00000020;
		private const int EVERYTHING_REQUEST_DATE_MODIFIED = 0x00000040;
		private const int EVERYTHING_REQUEST_DATE_ACCESSED = 0x00000080;
		private const int EVERYTHING_REQUEST_ATTRIBUTES = 0x00000100;
		private const int EVERYTHING_REQUEST_FILE_LIST_FILE_NAME = 0x00000200;
		private const int EVERYTHING_REQUEST_RUN_COUNT = 0x00000400;
		private const int EVERYTHING_REQUEST_DATE_RUN = 0x00000800;
		private const int EVERYTHING_REQUEST_DATE_RECENTLY_CHANGED = 0x00001000;
		private const int EVERYTHING_REQUEST_HIGHLIGHTED_FILE_NAME = 0x00002000;
		private const int EVERYTHING_REQUEST_HIGHLIGHTED_PATH = 0x00004000;
		private const int EVERYTHING_REQUEST_HIGHLIGHTED_FULL_PATH_AND_FILE_NAME = 0x00008000;

		// File info types for indexing checks
		private enum FileInfoType : uint
		{
			FileSize = 1,
			FolderSize = 2,
			DateCreated = 3,
			DateModified = 4,
			DateAccessed = 5,
			Attributes = 6
		}

		#region P/Invoke declarations

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

		[DllImport("Everything64.dll", EntryPoint = "Everything_GetTotResults")]
		private static extern uint Everything_GetTotResults();

		[DllImport("Everything64.dll", EntryPoint = "Everything_GetLastError")]
		private static extern uint Everything_GetLastError();

		[DllImport("Everything64.dll", EntryPoint = "Everything_IsFileResult")]
		private static extern bool Everything_IsFileResult(uint nIndex);

		[DllImport("Everything64.dll", EntryPoint = "Everything_IsFolderResult")]
		private static extern bool Everything_IsFolderResult(uint nIndex);

		[DllImport("Everything64.dll", EntryPoint = "Everything_IsVolumeResult")]
		private static extern bool Everything_IsVolumeResult(uint nIndex);

		[DllImport("Everything64.dll", EntryPoint = "Everything_GetResultPath", CharSet = CharSet.Unicode)]
		private static extern IntPtr Everything_GetResultPath(uint nIndex);

		[DllImport("Everything64.dll", EntryPoint = "Everything_GetResultFileName", CharSet = CharSet.Unicode)]
		private static extern IntPtr Everything_GetResultFileName(uint nIndex);

		[DllImport("Everything64.dll", EntryPoint = "Everything_GetResultFullPathNameW", CharSet = CharSet.Unicode)]
		private static extern void Everything_GetResultFullPathNameW(uint nIndex, StringBuilder lpString, uint nMaxCount);

		[DllImport("Everything64.dll", EntryPoint = "Everything_GetResultHighlightedFileName", CharSet = CharSet.Unicode)]
		private static extern IntPtr Everything_GetResultHighlightedFileName(uint nIndex);

		[DllImport("Everything64.dll", EntryPoint = "Everything_GetResultHighlightedPath", CharSet = CharSet.Unicode)]
		private static extern IntPtr Everything_GetResultHighlightedPath(uint nIndex);

		[DllImport("Everything64.dll", EntryPoint = "Everything_GetResultHighlightedFullPathAndFileName", CharSet = CharSet.Unicode)]
		private static extern IntPtr Everything_GetResultHighlightedFullPathAndFileName(uint nIndex);

		[DllImport("Everything64.dll", EntryPoint = "Everything_GetResultDateModified")]
		private static extern bool Everything_GetResultDateModified(uint nIndex, out long lpFileTime);

		[DllImport("Everything64.dll", EntryPoint = "Everything_GetResultDateCreated")]
		private static extern bool Everything_GetResultDateCreated(uint nIndex, out long lpFileTime);

		[DllImport("Everything64.dll", EntryPoint = "Everything_GetResultDateAccessed")]
		private static extern bool Everything_GetResultDateAccessed(uint nIndex, out long lpFileTime);

		[DllImport("Everything64.dll", EntryPoint = "Everything_GetResultSize")]
		private static extern bool Everything_GetResultSize(uint nIndex, out long lpFileSize);

		[DllImport("Everything64.dll", EntryPoint = "Everything_GetResultAttributes")]
		private static extern uint Everything_GetResultAttributes(uint nIndex);

		[DllImport("Everything64.dll", EntryPoint = "Everything_GetResultRunCount")]
		private static extern uint Everything_GetResultRunCount(uint nIndex);

		[DllImport("Everything64.dll", EntryPoint = "Everything_Reset")]
		private static extern void Everything_Reset();

		[DllImport("Everything64.dll", EntryPoint = "Everything_CleanUp")]
		private static extern void Everything_CleanUp();

		[DllImport("Everything64.dll", EntryPoint = "Everything_IsDBLoaded")]
		private static extern bool Everything_IsDBLoaded();

		[DllImport("Everything64.dll", EntryPoint = "Everything_IsFileInfoIndexed")]
		private static extern bool Everything_IsFileInfoIndexed(uint fileInfoType);

		[DllImport("Everything64.dll", EntryPoint = "Everything_GetMajorVersion")]
		private static extern uint Everything_GetMajorVersion();

		[DllImport("Everything64.dll", EntryPoint = "Everything_GetMinorVersion")]
		private static extern uint Everything_GetMinorVersion();

		[DllImport("Everything64.dll", EntryPoint = "Everything_GetRevision")]
		private static extern uint Everything_GetRevision();

		[DllImport("Everything64.dll", EntryPoint = "Everything_SetSort")]
		private static extern void Everything_SetSort(uint dwSortType);

		[DllImport("Everything64.dll", EntryPoint = "Everything_GetSort")]
		private static extern uint Everything_GetSort();

		[DllImport("Everything64.dll", EntryPoint = "Everything_GetResultListSort")]
		private static extern uint Everything_GetResultListSort();

		[DllImport("Everything64.dll", EntryPoint = "Everything_SetReplyWindow")]
		private static extern void Everything_SetReplyWindow(IntPtr hWnd);

		[DllImport("Everything64.dll", EntryPoint = "Everything_SetReplyID")]
		private static extern void Everything_SetReplyID(uint nId);

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
		private readonly ArrayPool<char> _charPool = ArrayPool<char>.Shared;

		private static readonly object _initLock = new();
		private static readonly List<IntPtr> _dllDirectoryCookies = new();
		private static bool _initialized = false;
		private static bool _everythingAvailable = false;
		private static string _everythingVersion = string.Empty;
		private static uint _optimalRequestFlags = 0;

		private bool _disposed = false;

		public StreamingEverythingSearchService(IUserSettingsService userSettingsService)
		{
			_userSettingsService = userSettingsService;
			
			// Try to get logger, fallback to App.Logger if not available
			try
			{
				_logger = Ioc.Default.GetService<ILogger<StreamingEverythingSearchService>>();
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
					DetermineOptimalRequestFlags();
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

		private void DetermineOptimalRequestFlags()
		{
			// Always request basic info
			_optimalRequestFlags = EVERYTHING_REQUEST_FILE_NAME | EVERYTHING_REQUEST_PATH;

			// Check what additional info is indexed and worth requesting
			try
			{
				if (Everything_IsFileInfoIndexed((uint)FileInfoType.DateModified))
				{
					_optimalRequestFlags |= EVERYTHING_REQUEST_DATE_MODIFIED;
					LogDebug("Date modified is indexed");
				}

				if (Everything_IsFileInfoIndexed((uint)FileInfoType.DateCreated))
				{
					_optimalRequestFlags |= EVERYTHING_REQUEST_DATE_CREATED;
					LogDebug("Date created is indexed");
				}

				if (Everything_IsFileInfoIndexed((uint)FileInfoType.FileSize))
				{
					_optimalRequestFlags |= EVERYTHING_REQUEST_SIZE;
					LogDebug("File size is indexed");
				}

				if (Everything_IsFileInfoIndexed((uint)FileInfoType.Attributes))
				{
					_optimalRequestFlags |= EVERYTHING_REQUEST_ATTRIBUTES;
					LogDebug("Attributes are indexed");
				}
			}
			catch (Exception ex)
			{
				LogWarning($"Failed to determine indexed fields: {ex.Message}");
				// Fall back to requesting everything
				_optimalRequestFlags = EVERYTHING_REQUEST_FILE_NAME | 
				                      EVERYTHING_REQUEST_PATH | 
				                      EVERYTHING_REQUEST_DATE_MODIFIED | 
				                      EVERYTHING_REQUEST_DATE_CREATED |
				                      EVERYTHING_REQUEST_SIZE |
				                      EVERYTHING_REQUEST_ATTRIBUTES;
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
			// For compatibility, collect all results into a list
			var results = new List<ListedItem>();
			await foreach (var item in SearchStreamAsync(query, searchPath, cancellationToken))
			{
				results.Add(item);
			}
			return results;
		}

		public async IAsyncEnumerable<ListedItem> SearchStreamAsync(string query, string searchPath = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			if (!IsEverythingAvailable())
			{
				LogWarning("Everything is not available for search");
				yield break;
			}

			await _searchSemaphore.WaitAsync(cancellationToken);
			try
			{
				await foreach (var item in PerformStreamingSearch(query, searchPath, cancellationToken))
				{
					yield return item;
				}
			}
			finally
			{
				_searchSemaphore.Release();
			}
		}

		private async IAsyncEnumerable<ListedItem> PerformStreamingSearch(string query, string searchPath, [EnumeratorCancellation] CancellationToken cancellationToken)
		{
			var stopwatch = System.Diagnostics.Stopwatch.StartNew();
			uint processedCount = 0;

			await Task.Run(() =>
			{
				try
				{
					Everything_Reset();

					// Build optimized query
					var searchQuery = BuildOptimizedQuery(query, searchPath);
					Everything_SetSearchW(searchQuery);
					Everything_SetMatchCase(false);
					Everything_SetRequestFlags(_optimalRequestFlags);

					// Limit results for performance
					Everything_SetMax(10000);

					LogDebug($"Executing Everything query: {searchQuery}");

					if (!Everything_QueryW(true))
					{
						var error = (EverythingError)Everything_GetLastError();
						throw new EverythingException(error);
					}
				}
				catch (Exception ex)
				{
					LogError($"Everything search setup failed: {ex.Message}");
					throw;
				}
			}, cancellationToken);

			// Stream results
			var numResults = Everything_GetNumResults();
			LogDebug($"Everything returned {numResults} results");

			for (uint i = 0; i < numResults; i++)
			{
				if (cancellationToken.IsCancellationRequested)
				{
					LogDebug("Search cancelled by user");
					yield break;
				}

				ListedItem item = null;
				try
				{
					item = await Task.Run(() => CreateListedItem(i), cancellationToken);
				}
				catch (Exception ex)
				{
					LogWarning($"Failed to process search result at index {i}: {ex.Message}");
					continue;
				}

				if (item != null && ShouldIncludeItem(item, searchPath))
				{
					processedCount++;
					yield return item;

					// Yield control periodically to keep UI responsive
					if (processedCount % 100 == 0)
					{
						await Task.Yield();
					}
				}
			}

			// Cleanup
			try
			{
				Everything_CleanUp();
			}
			catch (Exception ex)
			{
				LogWarning($"Error cleaning up Everything: {ex.Message}");
			}

			stopwatch.Stop();
			LogInformation($"Everything search completed in {stopwatch.ElapsedMilliseconds}ms with {processedCount} results");
		}

		private ListedItem CreateListedItem(uint index)
		{
			// Use ArrayPool for temporary string buffer
			const int maxPathLength = 260;
			var pathBuffer = _charPool.Rent(maxPathLength * 2);
			
			try
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

				// Set dates if indexed
				if ((_optimalRequestFlags & EVERYTHING_REQUEST_DATE_MODIFIED) != 0 && 
				    Everything_GetResultDateModified(index, out long dateModified))
				{
					item.ItemDateModifiedReal = DateTime.FromFileTime(dateModified);
				}

				if ((_optimalRequestFlags & EVERYTHING_REQUEST_DATE_CREATED) != 0 && 
				    Everything_GetResultDateCreated(index, out long dateCreated))
				{
					item.ItemDateCreatedReal = DateTime.FromFileTime(dateCreated);
				}

				// Set size for files if indexed
				if (!isFolder && (_optimalRequestFlags & EVERYTHING_REQUEST_SIZE) != 0 && 
				    Everything_GetResultSize(index, out long size))
				{
					item.FileSizeBytes = size;
					item.FileSize = ByteSize.FromBytes((ulong)size).ToBinaryString();
				}

				// Set item type
				if (!isFolder && !string.IsNullOrEmpty(item.FileExtension))
				{
					item.ItemType = item.FileExtension.Trim('.') + " " + Strings.File.GetLocalizedResource();
				}

				// Get run count if available (for future use)
				// Note: Run count could be stored in a custom property or extension
				// if needed for sorting frequently accessed files

				return item;
			}
			finally
			{
				_charPool.Return(pathBuffer, clearArray: true);
			}
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

			// Search in that directory and filter to only items that were in the original list
			var itemPaths = new HashSet<string>(itemsList.Select(i => i.ItemPath), StringComparer.OrdinalIgnoreCase);
			var results = new List<ListedItem>();

			await foreach (var item in SearchStreamAsync(query, directoryPath, cancellationToken))
			{
				if (itemPaths.Contains(item.ItemPath))
				{
					results.Add(item);
				}
			}

			return results;
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