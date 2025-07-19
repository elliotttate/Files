// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.Mvvm.DependencyInjection;
using Files.App.Data.Contracts;
using Files.App.Extensions;
using Files.App.Utils;
using Files.App.Utils.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace Files.App.Services.Caching
{
	/// <summary>
	/// Implements a thread-safe caching service for file models with lazy loading of media properties and thumbnails
	/// </summary>
	public sealed class FileModelCacheService : IFileModelCacheService, IDisposable
	{
		// Cache dictionaries
		private readonly ConcurrentDictionary<string, CachedFileModel> _fileModelCache = new(StringComparer.OrdinalIgnoreCase);
		private readonly ConcurrentDictionary<string, CachedThumbnail> _thumbnailCache = new(StringComparer.OrdinalIgnoreCase);
		private readonly ConcurrentDictionary<string, MediaProperties> _mediaPropertiesCache = new(StringComparer.OrdinalIgnoreCase);

		// Thumbnail loading queue with priority support
		private readonly ConcurrentQueue<ThumbnailRequest> _thumbnailQueue = new();
		private readonly ConcurrentQueue<ThumbnailRequest> _priorityQueue = new(); // High priority queue for visible items
		private readonly SemaphoreSlim _thumbnailQueueSemaphore = new(0, int.MaxValue);
		private readonly CancellationTokenSource _serviceCancellationTokenSource = new();
		private readonly List<(string Path, BitmapImage Thumbnail)> _pendingUIUpdates = new();
		private readonly object _pendingUIUpdatesLock = new();
		private Timer _uiUpdateTimer;

		// Constants
		private const int MAX_CONCURRENT_THUMBNAIL_LOADS = 32; // Increased for better performance with large folders
		private const long DEFAULT_MAX_CACHE_SIZE = 4L * 1024 * 1024 * 1024; // 4GB
		private const int CLEANUP_THRESHOLD_PERCENTAGE = 90; // Cleanup when cache reaches 90% of max size
		private const int UI_UPDATE_BATCH_SIZE = 10; // Batch UI updates for better performance
		private const int UI_UPDATE_BATCH_DELAY_MS = 50; // Delay between UI update batches

		// Events
		public event EventHandler<ThumbnailLoadedEventArgs> ThumbnailLoaded;
		public event EventHandler<MediaPropertiesLoadedEventArgs> MediaPropertiesLoaded;

		// Background tasks
		private readonly Task _thumbnailProcessingTask;
		private readonly Timer _cacheCleanupTimer;

		// Services
		private readonly IThreadingService _threadingService;

		// Statistics
		private long _estimatedCacheSizeInBytes = 0;
		private readonly object _cacheSizeLock = new();

		public FileModelCacheService()
		{
			_threadingService = Ioc.Default.GetRequiredService<IThreadingService>();

			// Start thumbnail processing task
			_thumbnailProcessingTask = Task.Run(ProcessThumbnailQueueAsync);

			// Start periodic cache cleanup (more frequent)
			_cacheCleanupTimer = new Timer(
				PerformPeriodicCleanup,
				null,
				TimeSpan.FromMinutes(2), // Initial delay
				TimeSpan.FromMinutes(2)); // Run every 2 minutes instead of 5

			// Start UI update batching timer
			_uiUpdateTimer = new Timer(
				ProcessPendingUIUpdates,
				null,
				TimeSpan.FromMilliseconds(UI_UPDATE_BATCH_DELAY_MS),
				TimeSpan.FromMilliseconds(UI_UPDATE_BATCH_DELAY_MS));
		}

		#region IFileModelCacheService Implementation

		public ListedItem GetCachedItem(string path)
		{
			if (string.IsNullOrEmpty(path))
				return null;

			return _fileModelCache.TryGetValue(path, out var cachedModel) ? cachedModel.Item : null;
		}

		public void AddOrUpdateItem(string path, ListedItem item)
		{
			if (string.IsNullOrEmpty(path) || item == null)
				return;

			var cachedModel = new CachedFileModel
			{
				Item = item,
				LastAccessTime = DateTime.UtcNow,
				SizeInBytes = EstimateItemSize(item)
			};

			_fileModelCache.AddOrUpdate(path, cachedModel, (key, existing) =>
			{
				UpdateCacheSize(-existing.SizeInBytes);
				cachedModel.SizeInBytes = EstimateItemSize(item);
				UpdateCacheSize(cachedModel.SizeInBytes);
				return cachedModel;
			});

			UpdateCacheSize(cachedModel.SizeInBytes);
		}

		public bool RemoveItem(string path)
		{
			if (string.IsNullOrEmpty(path))
				return false;

			if (_fileModelCache.TryRemove(path, out var removed))
			{
				UpdateCacheSize(-removed.SizeInBytes);
				
				// Also remove associated data
				_thumbnailCache.TryRemove(path, out _);
				_mediaPropertiesCache.TryRemove(path, out _);
				
				return true;
			}

			return false;
		}

		public void ClearCache()
		{
			_fileModelCache.Clear();
			_thumbnailCache.Clear();
			_mediaPropertiesCache.Clear();
			
			lock (_cacheSizeLock)
			{
				_estimatedCacheSizeInBytes = 0;
			}
		}

		public BitmapImage GetCachedThumbnail(string path)
		{
			if (string.IsNullOrEmpty(path))
				return null;

			if (_thumbnailCache.TryGetValue(path, out var cached))
			{
				// Update last access time
				cached.LastAccessTime = DateTime.UtcNow;
				return cached.Thumbnail;
			}
			
			return null;
		}

		public void AddOrUpdateThumbnail(string path, BitmapImage thumbnail)
		{
			if (string.IsNullOrEmpty(path) || thumbnail == null)
				return;

			var cachedThumbnail = new CachedThumbnail
			{
				Thumbnail = thumbnail,
				LastAccessTime = DateTime.UtcNow,
				SizeInBytes = EstimateBitmapSize(thumbnail)
			};
			
			_thumbnailCache.AddOrUpdate(path, cachedThumbnail, (key, existing) => 
			{
				// Update cache size tracking
				UpdateCacheSize(-existing.SizeInBytes);
				UpdateCacheSize(cachedThumbnail.SizeInBytes);
				return cachedThumbnail;
			});
			
			UpdateCacheSize(cachedThumbnail.SizeInBytes);
			
			// Update the ListedItem if it exists
			if (_fileModelCache.TryGetValue(path, out var cachedModel))
			{
				cachedModel.Item.FileImage = thumbnail;
				cachedModel.Item.LoadFileIcon = true;
				cachedModel.Item.NeedsPlaceholderGlyph = false;
			}
		}

		public async Task QueueThumbnailLoadAsync(string path, ListedItem item, uint thumbnailSize, CancellationToken cancellationToken, bool isPriority = false)
		{
			if (string.IsNullOrEmpty(path) || item == null)
				return;

			// Check if thumbnail already cached
			if (_thumbnailCache.ContainsKey(path))
				return;

			var request = new ThumbnailRequest
			{
				Path = path,
				Item = item,
				ThumbnailSize = thumbnailSize,
				CancellationToken = cancellationToken,
				CompletionSource = new TaskCompletionSource(),
				IsPriority = isPriority
			};

			if (isPriority)
				_priorityQueue.Enqueue(request);
			else
				_thumbnailQueue.Enqueue(request);
				
			_thumbnailQueueSemaphore.Release();

			await request.CompletionSource.Task;
		}

		public MediaProperties GetCachedMediaProperties(string path)
		{
			if (string.IsNullOrEmpty(path))
				return null;

			return _mediaPropertiesCache.TryGetValue(path, out var properties) ? properties : null;
		}

		public void AddOrUpdateMediaProperties(string path, MediaProperties properties)
		{
			if (string.IsNullOrEmpty(path) || properties == null)
				return;

			properties.LastUpdated = DateTime.UtcNow;
			_mediaPropertiesCache.AddOrUpdate(path, properties, (key, existing) => properties);

			// Update the ListedItem if it exists
			if (_fileModelCache.TryGetValue(path, out var cachedModel))
			{
				var item = cachedModel.Item;
				item.ImageDimensions = properties.ImageDimensions;
				item.MediaDuration = properties.MediaDuration;
				item.FileVersion = properties.FileVersion;
			}
		}

		public async Task<MediaProperties> LoadMediaPropertiesAsync(string path, CancellationToken cancellationToken)
		{
			if (string.IsNullOrEmpty(path))
				return null;

			// Skip known problematic system files
			var fileName = Path.GetFileName(path);
			if (ProblematicSystemFiles.Contains(fileName))
				return null;

			// Skip protected system paths
			if (IsSystemProtectedPath(path))
				return null;

			// Check cache first
			if (_mediaPropertiesCache.TryGetValue(path, out var cached))
				return cached;

			try
			{
				var properties = await LoadMediaPropertiesFromFileAsync(path, cancellationToken);
				if (properties != null)
				{
					AddOrUpdateMediaProperties(path, properties);
					MediaPropertiesLoaded?.Invoke(this, new MediaPropertiesLoadedEventArgs { Path = path, Properties = properties });
				}
				return properties;
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"Failed to load media properties for {path}: {ex.Message}");
				return null;
			}
		}

		public long GetCacheSizeInBytes()
		{
			lock (_cacheSizeLock)
			{
				return _estimatedCacheSizeInBytes;
			}
		}

		public int PerformCacheCleanup(long targetSizeInBytes)
		{
			var itemsRemoved = 0;
			var currentSize = GetCacheSizeInBytes();

			if (currentSize <= targetSizeInBytes)
				return 0;

			// First, clean up thumbnails (they typically use more memory)
			var thumbnailsToRemove = _thumbnailCache
				.OrderBy(kvp => kvp.Value.LastAccessTime)
				.Take(_thumbnailCache.Count / 3) // Remove oldest 33% of thumbnails
				.ToList();
				
			foreach (var kvp in thumbnailsToRemove)
			{
				if (currentSize <= targetSizeInBytes)
					break;
					
				if (_thumbnailCache.TryRemove(kvp.Key, out var removed))
				{
					UpdateCacheSize(-removed.SizeInBytes);
					itemsRemoved++;
					currentSize = GetCacheSizeInBytes();
				}
			}

			// Then clean up file models if needed
			if (currentSize > targetSizeInBytes)
			{
				var itemsToRemove = _fileModelCache
					.OrderBy(kvp => kvp.Value.LastAccessTime)
					.ToList();

				foreach (var kvp in itemsToRemove)
				{
					if (currentSize <= targetSizeInBytes)
						break;

					if (RemoveItem(kvp.Key))
					{
						itemsRemoved++;
						currentSize = GetCacheSizeInBytes();
					}
				}
			}

			return itemsRemoved;
		}

		#endregion

		#region Private Methods

		private async Task ProcessThumbnailQueueAsync()
		{
			var semaphore = new SemaphoreSlim(MAX_CONCURRENT_THUMBNAIL_LOADS);

			while (!_serviceCancellationTokenSource.Token.IsCancellationRequested)
			{
				try
				{
					await _thumbnailQueueSemaphore.WaitAsync(_serviceCancellationTokenSource.Token);

					var tasks = new List<Task>();

					// Process priority queue first
					while (_priorityQueue.TryDequeue(out var request) && tasks.Count < MAX_CONCURRENT_THUMBNAIL_LOADS)
					{
						if (request.CancellationToken.IsCancellationRequested)
						{
							request.CompletionSource.TrySetCanceled();
							continue;
						}

						var task = ProcessThumbnailRequestAsync(request, semaphore);
						tasks.Add(task);
					}

					// Then process regular queue
					while (_thumbnailQueue.TryDequeue(out var request) && tasks.Count < MAX_CONCURRENT_THUMBNAIL_LOADS)
					{
						if (request.CancellationToken.IsCancellationRequested)
						{
							request.CompletionSource.TrySetCanceled();
							continue;
						}

						var task = ProcessThumbnailRequestAsync(request, semaphore);
						tasks.Add(task);
					}

					if (tasks.Any())
						await Task.WhenAll(tasks);
				}
				catch (OperationCanceledException)
				{
					break;
				}
				catch (Exception ex)
				{
					Debug.WriteLine($"Error processing thumbnail queue: {ex.Message}");
				}
			}
		}

		private async Task ProcessThumbnailRequestAsync(ThumbnailRequest request, SemaphoreSlim semaphore)
		{
			await semaphore.WaitAsync();
			try
			{
				// Check if already cached
				if (_thumbnailCache.ContainsKey(request.Path))
				{
					request.CompletionSource.TrySetResult();
					return;
				}

				// Try loading thumbnail with retry logic
				byte[]? thumbnailData = null;
				int retryCount = 0;
				const int maxRetries = 2;

				while (thumbnailData == null && retryCount <= maxRetries)
				{
					try
					{
						thumbnailData = await FileThumbnailHelper.GetIconAsync(
							request.Path,
							request.ThumbnailSize,
							request.Item.IsFolder,
							IconOptions.UseCurrentScale);
					}
					catch (Exception ex)
					{
						if (retryCount < maxRetries)
						{
							retryCount++;
							await Task.Delay(100 * retryCount); // Exponential backoff
							continue;
						}
						Debug.WriteLine($"Failed to load icon for {request.Path} after {maxRetries} retries: {ex.Message}");
					}
					break;
				}

				if (thumbnailData != null && !request.CancellationToken.IsCancellationRequested)
				{
					try
					{
						// Store the raw data and create BitmapImage on UI thread to avoid RPC_E_WRONG_THREAD
						var rawData = thumbnailData;
						
						// Schedule UI update on UI thread
						await _threadingService.ExecuteOnUiThreadAsync(async () =>
						{
							try
							{
								using var ms = new MemoryStream(rawData);
								var bitmap = new BitmapImage();
								await bitmap.SetSourceAsync(ms.AsRandomAccessStream());
								
								if (bitmap != null)
								{
									AddOrUpdateThumbnail(request.Path, bitmap);
									ThumbnailLoaded?.Invoke(this, new ThumbnailLoadedEventArgs { Path = request.Path, Thumbnail = bitmap });
								}
							}
							catch (Exception ex)
							{
								Debug.WriteLine($"Failed to create bitmap for {request.Path}: {ex.Message}");
							}
						});
					}
					catch (Exception ex)
					{
						Debug.WriteLine($"Failed to create bitmap for {request.Path}: {ex.Message}");
					}
				}
				else if (thumbnailData == null)
				{
					Debug.WriteLine($"No thumbnail data retrieved for {request.Path}");
				}

				request.CompletionSource.TrySetResult();
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"Failed to load thumbnail for {request.Path}: {ex.Message}");
				request.CompletionSource.TrySetException(ex);
			}
			finally
			{
				semaphore.Release();
			}
		}

		private static readonly HashSet<string> ProblematicSystemFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"bootstat.dat",
			"pagefile.sys",
			"hiberfil.sys",
			"swapfile.sys",
			"ntuser.dat",
			"usrclass.dat",
			"ntuser.pol",
			"desktop.ini",
			"thumbs.db"
		};

		private static bool IsSystemProtectedPath(string path)
		{
			if (string.IsNullOrEmpty(path))
				return false;

			// Check for protected system folders
			var protectedPaths = new[]
			{
				@"C:\Windows\System32\config",
				@"C:\Windows\ServiceProfiles",
				@"C:\Windows\CSC",
				@"C:\System Volume Information",
				@"C:\$Recycle.Bin"
			};

			return protectedPaths.Any(protectedPath => 
				path.StartsWith(protectedPath, StringComparison.OrdinalIgnoreCase));
		}

		private async Task<MediaProperties> LoadMediaPropertiesFromFileAsync(string path, CancellationToken cancellationToken)
		{
			try
			{
				// Skip known problematic system files
				var fileName = Path.GetFileName(path);
				if (ProblematicSystemFiles.Contains(fileName))
					return null;

				// Skip protected system paths
				if (IsSystemProtectedPath(path))
					return null;

				// Check if it's a file first
				try
				{
					var attributes = File.GetAttributes(path);
					if ((attributes & System.IO.FileAttributes.Directory) == System.IO.FileAttributes.Directory)
						return null; // Skip folders
				}
				catch (UnauthorizedAccessException)
				{
					return null; // Skip files we can't access
				}
				catch (IOException)
				{
					return null; // Skip files that might not exist anymore
				}

				StorageFile file;
				try
				{
					file = await StorageFile.GetFileFromPathAsync(path);
					if (file == null)
						return null;
				}
				catch (UnauthorizedAccessException)
				{
					return null; // Skip files we can't access
				}
				catch (FileNotFoundException)
				{
					return null; // File doesn't exist anymore
				}

				var properties = new MediaProperties();
				var fileExtension = Path.GetExtension(path).ToLowerInvariant();
				
				try
				{
					var basicProperties = await file.GetBasicPropertiesAsync();

					// Load based on file type
					
					if (IsImageFile(fileExtension))
					{
						try
						{
							var imageProperties = await file.Properties.GetImagePropertiesAsync();
							if (imageProperties != null)
							{
								properties.Width = (int)imageProperties.Width;
								properties.Height = (int)imageProperties.Height;
								properties.ImageDimensions = $"{imageProperties.Width} × {imageProperties.Height}";
							}
						}
						catch (Exception ex)
						{
							Debug.WriteLine($"Failed to get image properties for {path}: {ex.Message}");
						}
					}
					else if (IsVideoFile(fileExtension))
					{
						try
						{
							var videoProperties = await file.Properties.GetVideoPropertiesAsync();
							if (videoProperties != null)
							{
								properties.Duration = videoProperties.Duration;
								properties.MediaDuration = FormatDuration(videoProperties.Duration);
								properties.Width = (int)videoProperties.Width;
								properties.Height = (int)videoProperties.Height;
								properties.ImageDimensions = $"{videoProperties.Width} × {videoProperties.Height}";
							}
						}
						catch (Exception ex)
						{
							Debug.WriteLine($"Failed to get video properties for {path}: {ex.Message}");
						}
					}
					else if (IsAudioFile(fileExtension))
					{
						try
						{
							var musicProperties = await file.Properties.GetMusicPropertiesAsync();
							if (musicProperties != null)
							{
								properties.Duration = musicProperties.Duration;
								properties.MediaDuration = FormatDuration(musicProperties.Duration);
							}
						}
						catch (Exception ex)
						{
							Debug.WriteLine($"Failed to get music properties for {path}: {ex.Message}");
						}
					}
				}
				catch (UnauthorizedAccessException)
				{
					// Can't access file properties, return what we have
					return properties;
				}
				catch (Exception ex)
				{
					Debug.WriteLine($"Failed to get properties for {path}: {ex.Message}");
					return properties;
				}

				// Try to get version info for executable files
				if (IsExecutableFile(fileExtension))
				{
					try
					{
						var extraProperties = await file.Properties.RetrievePropertiesAsync(new[] { "System.FileVersion" });
						if (extraProperties.TryGetValue("System.FileVersion", out var version))
						{
							properties.FileVersion = version?.ToString();
						}
					}
					catch (Exception ex)
					{
						Debug.WriteLine($"Failed to get version info for {path}: {ex.Message}");
					}
				}

				return properties;
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"Failed to load media properties from file {path}: {ex.Message}");
				return null;
			}
		}

		private void UpdateCacheSize(long delta)
		{
			lock (_cacheSizeLock)
			{
				_estimatedCacheSizeInBytes += delta;
				if (_estimatedCacheSizeInBytes < 0)
					_estimatedCacheSizeInBytes = 0;
			}
		}

		private long EstimateItemSize(ListedItem item)
		{
			// Rough estimation of memory usage
			long size = 0;
			
			// Base object overhead
			size += 24; // Object header
			
			// String properties (estimate 2 bytes per char)
			size += EstimateStringSize(item.ItemPath);
			size += EstimateStringSize(item.ItemNameRaw);
			size += EstimateStringSize(item.ItemType);
			size += EstimateStringSize(item.FileExtension);
			size += EstimateStringSize(item.FileSize);
			size += EstimateStringSize(item.ImageDimensions);
			size += EstimateStringSize(item.MediaDuration);
			size += EstimateStringSize(item.FileVersion);
			
			// Other properties
			size += 8 * 10; // Various long/double/DateTime fields
			
			// Thumbnail (if loaded)
			if (item.FileImage != null)
			{
				size += 1024 * 1024; // Estimate 1MB per thumbnail
			}
			
			return size;
		}

		private long EstimateStringSize(string str)
		{
			return string.IsNullOrEmpty(str) ? 0 : str.Length * 2 + 24;
		}
		
		private long EstimateBitmapSize(BitmapImage bitmap)
		{
			if (bitmap == null)
				return 0;
				
			try
			{
				// Estimate based on pixel dimensions if available
				if (bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0)
				{
					// Assume 4 bytes per pixel (RGBA) plus object overhead
					return (long)(bitmap.PixelWidth * bitmap.PixelHeight * 4) + 256;
				}
			}
			catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == unchecked((int)0x8001010E))
			{
				// RPC_E_WRONG_THREAD - Can't access BitmapImage properties from this thread
				// Fall through to default estimate
				App.Logger?.LogDebug("EstimateBitmapSize: Cannot access BitmapImage properties from background thread, using default estimate");
			}
			catch (Exception ex)
			{
				App.Logger?.LogWarning(ex, "EstimateBitmapSize: Error accessing BitmapImage properties");
			}
			
			// Default estimate for thumbnails (48x48 pixels * 4 bytes)
			return 48 * 48 * 4 + 256;
		}

		private void PerformPeriodicCleanup(object state)
		{
			var currentSize = GetCacheSizeInBytes();
			var threshold = DEFAULT_MAX_CACHE_SIZE * CLEANUP_THRESHOLD_PERCENTAGE / 100;
			
			// More aggressive cleanup
			if (currentSize > threshold)
			{
				var targetSize = DEFAULT_MAX_CACHE_SIZE * 50 / 100; // Reduce to 50% of max (more aggressive)
				var removed = PerformCacheCleanup(targetSize);
				Debug.WriteLine($"Cache cleanup removed {removed} items. Size: {FormatBytes(currentSize)} -> {FormatBytes(GetCacheSizeInBytes())}");
			}
			
			// Also log current status
			if (currentSize > 0)
			{
				Debug.WriteLine($"Cache status - Size: {FormatBytes(currentSize)}, Items: {_fileModelCache.Count}, Thumbnails: {_thumbnailCache.Count}");
			}
		}

		private static bool IsImageFile(string extension)
		{
			return extension switch
			{
				".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".tiff" or ".webp" => true,
				_ => false
			};
		}

		private static bool IsVideoFile(string extension)
		{
			return extension switch
			{
				".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" or ".flv" or ".webm" => true,
				_ => false
			};
		}
		
		private static string FormatBytes(long bytes)
		{
			string[] sizes = { "B", "KB", "MB", "GB", "TB" };
			double len = bytes;
			int order = 0;
			while (len >= 1024 && order < sizes.Length - 1)
			{
				order++;
				len = len / 1024;
			}
			return $"{len:0.##} {sizes[order]}";
		}

		private static bool IsAudioFile(string extension)
		{
			return extension switch
			{
				".mp3" or ".wav" or ".flac" or ".aac" or ".ogg" or ".wma" or ".m4a" => true,
				_ => false
			};
		}

		private static bool IsExecutableFile(string extension)
		{
			return extension switch
			{
				".exe" or ".dll" or ".sys" => true,
				_ => false
			};
		}

		private static string FormatDuration(TimeSpan duration)
		{
			if (duration.TotalHours >= 1)
				return $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}";
			else
				return $"{duration.Minutes}:{duration.Seconds:00}";
		}

		private async void ProcessPendingUIUpdates(object state)
		{
			try
			{
				List<(string Path, BitmapImage Thumbnail)> updates;
				
				lock (_pendingUIUpdatesLock)
				{
					if (_pendingUIUpdates.Count == 0)
						return;
						
					// Take up to UI_UPDATE_BATCH_SIZE items
					updates = _pendingUIUpdates.Take(UI_UPDATE_BATCH_SIZE).ToList();
					_pendingUIUpdates.RemoveRange(0, updates.Count);
				}

				// Update UI on UI thread
				await _threadingService.ExecuteOnUiThreadAsync(() =>
				{
					foreach (var (path, thumbnail) in updates)
					{
						try
						{
							AddOrUpdateThumbnail(path, thumbnail);
							ThumbnailLoaded?.Invoke(this, new ThumbnailLoadedEventArgs { Path = path, Thumbnail = thumbnail });
						}
						catch (Exception ex)
						{
							Debug.WriteLine($"Error updating thumbnail for {path}: {ex.Message}");
						}
					}
				});
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"Error in ProcessPendingUIUpdates: {ex.Message}");
			}
		}

		#endregion

		#region IDisposable

		public void Dispose()
		{
			_serviceCancellationTokenSource?.Cancel();
			_thumbnailProcessingTask?.Wait(TimeSpan.FromSeconds(5));
			_cacheCleanupTimer?.Dispose();
			_uiUpdateTimer?.Dispose();
			_thumbnailQueueSemaphore?.Dispose();
			_serviceCancellationTokenSource?.Dispose();
			ClearCache();
		}

		#endregion

		#region Private Classes

		private class CachedFileModel
		{
			public ListedItem Item { get; set; }
			public DateTime LastAccessTime { get; set; }
			public long SizeInBytes { get; set; }
		}
		
		private class CachedThumbnail
		{
			public BitmapImage Thumbnail { get; set; }
			public DateTime LastAccessTime { get; set; }
			public long SizeInBytes { get; set; }
		}

		private class ThumbnailRequest
		{
			public string Path { get; set; }
			public ListedItem Item { get; set; }
			public uint ThumbnailSize { get; set; }
			public CancellationToken CancellationToken { get; set; }
			public TaskCompletionSource CompletionSource { get; set; }
			public bool IsPriority { get; set; }
		}

		#endregion
	}
}