// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.Mvvm.DependencyInjection;
using Files.App.Data.Contracts;
using Files.App.Data.Items;
using Files.App.Extensions;
using Files.App.Services.Caching;
using Files.App.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Files.App.Services.Thumbnails
{
	/// <summary>
	/// Thread-safe implementation of viewport-based thumbnail loading that avoids UI reentrancy
	/// </summary>
	public sealed class SafeViewportThumbnailLoaderService : IViewportThumbnailLoaderService, IDisposable
	{
		private readonly IFileModelCacheService _cacheService;
		private readonly ILogger<SafeViewportThumbnailLoaderService> _logger;
		private readonly DispatcherQueue _dispatcherQueue;
		
		// Thread-safe collections
		private readonly ConcurrentDictionary<string, bool> _visibleItems = new(StringComparer.OrdinalIgnoreCase);
		private readonly ConcurrentDictionary<string, WeakReference<ListedItem>> _itemReferences = new(StringComparer.OrdinalIgnoreCase);
		private readonly ConcurrentQueue<ThumbnailLoadRequest> _loadQueue = new();
		
		// Synchronization
		private readonly SemaphoreSlim _loadingSemaphore = new(1, 1);
		private readonly CancellationTokenSource _serviceCancellationTokenSource = new();
		private CancellationTokenSource _currentBatchCancellationTokenSource = new();
		
		// Background processing
		private readonly Task _processingTask;
		private readonly AutoResetEvent _workAvailable = new(false);
		
		// Constants
		private const int BATCH_SIZE = 10;
		private const int PROCESSING_DELAY_MS = 50;
		private const int MAX_RETRY_COUNT = 2;
		
		public int ActiveLoadCount => _loadQueue.Count;
		public bool IsLoading => !_loadQueue.IsEmpty;

		private class ThumbnailLoadRequest
		{
			public string Path { get; set; }
			public WeakReference<ListedItem> ItemReference { get; set; }
			public uint ThumbnailSize { get; set; }
			public bool IsPriority { get; set; }
			public int RetryCount { get; set; }
			public DateTime QueuedTime { get; set; } = DateTime.UtcNow;
		}

		public SafeViewportThumbnailLoaderService()
		{
			_cacheService = Ioc.Default.GetService<IFileModelCacheService>();
			_logger = Ioc.Default.GetService<ILogger<SafeViewportThumbnailLoaderService>>();
			_dispatcherQueue = DispatcherQueue.GetForCurrentThread();
			
			// Start background processing task
			_processingTask = Task.Run(ProcessThumbnailQueueAsync, _serviceCancellationTokenSource.Token);
		}

		public async Task UpdateViewportAsync(IEnumerable<ListedItem> visibleItems, uint thumbnailSize, CancellationToken cancellationToken = default)
		{
			if (visibleItems == null)
			{
				_logger?.LogDebug("UpdateViewportAsync called with null items");
				return;
			}

			var updateId = Guid.NewGuid().ToString().Substring(0, 8);
			_logger?.LogInformation("[{UpdateId}] UpdateViewportAsync started with {Count} visible items", updateId, visibleItems.Count());

			try
			{
				// Cancel previous batch
				_logger?.LogDebug("[{UpdateId}] Cancelling previous batch", updateId);
				_currentBatchCancellationTokenSource.Cancel();
				_currentBatchCancellationTokenSource = new CancellationTokenSource();
				
				var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(
					cancellationToken,
					_currentBatchCancellationTokenSource.Token,
					_serviceCancellationTokenSource.Token).Token;

				// Update visible items dictionary
				var newVisiblePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				var itemsToProcess = visibleItems.Where(i => i != null && !string.IsNullOrEmpty(i.ItemPath)).ToList();
				
				_logger?.LogDebug("[{UpdateId}] Processing {Count} valid items", updateId, itemsToProcess.Count);
				
				foreach (var item in itemsToProcess)
				{
					newVisiblePaths.Add(item.ItemPath);
					_visibleItems.TryAdd(item.ItemPath, true);
					_itemReferences.AddOrUpdate(item.ItemPath, 
						new WeakReference<ListedItem>(item),
						(k, v) => new WeakReference<ListedItem>(item));
				}

				// Remove items no longer visible
				var toRemove = _visibleItems.Keys.Where(path => !newVisiblePaths.Contains(path)).ToList();
				_logger?.LogDebug("[{UpdateId}] Removing {Count} items no longer visible", updateId, toRemove.Count);
				
				foreach (var path in toRemove)
				{
					_visibleItems.TryRemove(path, out _);
					_itemReferences.TryRemove(path, out _);
				}

				// Queue thumbnail loads for visible items without thumbnails
				var queuedCount = 0;
				var cachedCount = 0;
				
				foreach (var item in itemsToProcess)
				{
					if (linkedToken.IsCancellationRequested)
					{
						_logger?.LogDebug("[{UpdateId}] Viewport update cancelled during queueing", updateId);
						break;
					}
					
					if (item?.FileImage == null)
					{
						// Check cache first
						BitmapImage? cached = _cacheService?.GetCachedThumbnail(item.ItemPath);
						if (cached != null)
						{
							cachedCount++;
							// Update on UI thread safely
							await UpdateItemThumbnailSafelyAsync(item, cached);
							continue;
						}

						// Queue for loading
						var request = new ThumbnailLoadRequest
						{
							Path = item.ItemPath,
							ItemReference = new WeakReference<ListedItem>(item),
							ThumbnailSize = thumbnailSize,
							IsPriority = true
						};
						
						_loadQueue.Enqueue(request);
						queuedCount++;
					}
				}

				_logger?.LogInformation("[{UpdateId}] Viewport update complete: {Queued} queued, {Cached} from cache, Queue size: {QueueSize}", 
					updateId, queuedCount, cachedCount, _loadQueue.Count);

				// Signal work available
				_workAvailable.Set();
			}
			catch (OperationCanceledException)
			{
				_logger?.LogDebug("[{UpdateId}] Viewport update cancelled", updateId);
			}
			catch (Exception ex)
			{
				_logger?.LogError(ex, "[{UpdateId}] Error updating viewport", updateId);
			}
		}

		public async Task PreloadNearViewportAsync(IEnumerable<ListedItem> itemsNearViewport, uint thumbnailSize, CancellationToken cancellationToken = default)
		{
			if (itemsNearViewport == null)
				return;

			try
			{
				foreach (var item in itemsNearViewport.Where(i => i?.FileImage == null && !string.IsNullOrEmpty(i?.ItemPath)))
				{
					var request = new ThumbnailLoadRequest
					{
						Path = item.ItemPath,
						ItemReference = new WeakReference<ListedItem>(item),
						ThumbnailSize = thumbnailSize,
						IsPriority = false
					};
					
					_loadQueue.Enqueue(request);
				}

				// Signal work available
				_workAvailable.Set();
			}
			catch (Exception ex)
			{
				_logger?.LogError(ex, "Error preloading thumbnails");
			}
		}

		private async Task ProcessThumbnailQueueAsync()
		{
			_logger?.LogInformation("Thumbnail processing thread started");
			
			while (!_serviceCancellationTokenSource.Token.IsCancellationRequested)
			{
				try
				{
					// Wait for work
					_workAvailable.WaitOne(TimeSpan.FromSeconds(1));
					
					if (_serviceCancellationTokenSource.Token.IsCancellationRequested)
					{
						_logger?.LogDebug("Processing thread shutdown requested");
						break;
					}

					// Process queue in batches
					var batch = new List<ThumbnailLoadRequest>(BATCH_SIZE);
					var skippedCount = 0;
					
					while (batch.Count < BATCH_SIZE && _loadQueue.TryDequeue(out var request))
					{
						// Skip if item is no longer visible
						if (!_visibleItems.ContainsKey(request.Path) && request.IsPriority)
						{
							skippedCount++;
							continue;
						}
							
						batch.Add(request);
					}

					if (batch.Count == 0)
					{
						if (skippedCount > 0)
							_logger?.LogDebug("Skipped {Count} requests for non-visible items", skippedCount);
						continue;
					}

					_logger?.LogDebug("Processing batch of {BatchSize} thumbnails (skipped {SkippedCount}), queue remaining: {QueueSize}", 
						batch.Count, skippedCount, _loadQueue.Count);

					// Process batch
					await ProcessBatchAsync(batch);
					
					// Small delay to prevent overwhelming the system
					await Task.Delay(PROCESSING_DELAY_MS, _serviceCancellationTokenSource.Token);
				}
				catch (OperationCanceledException)
				{
					_logger?.LogDebug("Processing thread cancelled");
				}
				catch (Exception ex)
				{
					_logger?.LogError(ex, "Error in thumbnail processing loop");
					// Continue processing after error
					await Task.Delay(100);
				}
			}
			
			_logger?.LogInformation("Thumbnail processing thread stopped");
		}

		private async Task ProcessBatchAsync(List<ThumbnailLoadRequest> batch)
		{
			var tasks = batch.Select(request => Task.Run(async () =>
			{
				try
				{
					// Check if item still exists
					if (!request.ItemReference.TryGetTarget(out var item))
						return;

					// Skip if already has thumbnail
					if (item.FileImage != null)
						return;

					// Load thumbnail
					var iconData = await FileThumbnailHelper.GetIconAsync(
						request.Path,
						request.ThumbnailSize,
						item.PrimaryItemAttribute == Windows.Storage.StorageItemTypes.Folder,
						IconOptions.UseCurrentScale);

					if (iconData != null && iconData.Length > 0)
					{
						BitmapImage? image = await iconData.ToBitmapAsync();
						if (image != null)
						{
							await UpdateItemThumbnailSafelyAsync(item, image);
							
							// Add to cache
							_cacheService?.AddOrUpdateThumbnail(request.Path, image);
						}
					}
				}
				catch (Exception ex)
				{
					_logger?.LogDebug(ex, "Failed to load thumbnail for {Path}", request.Path);
					
					// Retry if needed
					if (request.RetryCount < MAX_RETRY_COUNT)
					{
						request.RetryCount++;
						_loadQueue.Enqueue(request);
					}
				}
			}));

			await Task.WhenAll(tasks);
		}

		private async Task UpdateItemThumbnailSafelyAsync(ListedItem item, BitmapImage thumbnail)
		{
			var updateId = Guid.NewGuid().ToString().Substring(0, 8);
			
			try
			{
				_logger?.LogDebug("[{UpdateId}] Updating thumbnail for {Path}", updateId, item?.ItemPath);
				
				if (_dispatcherQueue == null)
				{
					_logger?.LogWarning("[{UpdateId}] No dispatcher queue available, updating directly", updateId);
					// Fallback to direct update if no dispatcher
					item.FileImage = thumbnail;
					return;
				}

				// Use dispatcher queue to update UI safely without reentrancy
				var tcs = new TaskCompletionSource<bool>();
				var enqueued = false;
				
				try
				{
					enqueued = _dispatcherQueue.TryEnqueue(() =>
					{
						try
						{
							// Only update if item still doesn't have a thumbnail
							if (item.FileImage == null)
							{
								item.FileImage = thumbnail;
								_logger?.LogDebug("[{UpdateId}] Thumbnail updated successfully for {Path}", updateId, item.ItemPath);
							}
							else
							{
								_logger?.LogDebug("[{UpdateId}] Item already has thumbnail, skipping {Path}", updateId, item.ItemPath);
							}
							tcs.SetResult(true);
						}
						catch (Exception ex)
						{
							_logger?.LogError(ex, "[{UpdateId}] Error updating thumbnail for {Path}", updateId, item?.ItemPath);
							tcs.SetException(ex);
						}
					});
				}
				catch (Exception ex)
				{
					_logger?.LogError(ex, "[{UpdateId}] Failed to enqueue UI update for {Path}", updateId, item?.ItemPath);
					// Don't throw, just log and continue
					return;
				}

				if (!enqueued)
				{
					_logger?.LogWarning("[{UpdateId}] Failed to enqueue UI update for {Path}", updateId, item?.ItemPath);
					return;
				}

				await tcs.Task;
			}
			catch (Exception ex)
			{
				_logger?.LogError(ex, "[{UpdateId}] Exception in UpdateItemThumbnailSafelyAsync for {Path}", updateId, item?.ItemPath);
			}
		}

		public void ClearViewport()
		{
			_currentBatchCancellationTokenSource.Cancel();
			_visibleItems.Clear();
			_itemReferences.Clear();
			
			// Clear queue
			while (_loadQueue.TryDequeue(out _)) { }
		}

		public void Dispose()
		{
			try
			{
				_logger?.LogInformation("SafeViewportThumbnailLoaderService.Dispose started");
				
				_serviceCancellationTokenSource.Cancel();
				_workAvailable.Set(); // Wake up processing thread
				
				try
				{
					_processingTask.Wait(TimeSpan.FromSeconds(2));
					_logger?.LogDebug("Processing task completed gracefully");
				}
				catch (Exception ex)
				{
					_logger?.LogWarning(ex, "Processing task did not complete gracefully within timeout");
				}
				
				_serviceCancellationTokenSource.Dispose();
				_currentBatchCancellationTokenSource.Dispose();
				_loadingSemaphore.Dispose();
				_workAvailable.Dispose();
				
				_logger?.LogInformation("SafeViewportThumbnailLoaderService.Dispose completed successfully");
			}
			catch (Exception ex)
			{
				_logger?.LogError(ex, "Error during SafeViewportThumbnailLoaderService.Dispose");
			}
		}
	}
}