// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.Mvvm.DependencyInjection;
using Files.App.Data.Contracts;
using Files.App.Data.Items;
using Files.App.Extensions;
using Files.App.Services.Caching;
using Files.App.Utils;
using Files.App.Utils.Diagnostics;
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
		private DispatcherQueue _dispatcherQueue; // Made non-readonly to allow late initialization
		
		// Request status tracking
		private enum RequestStatus
		{
			Queued,
			Processing,
			Completed,
			Failed,
			Cancelled
		}
		
		// Thread-safe collections
		private readonly ConcurrentDictionary<string, bool> _visibleItems = new(StringComparer.OrdinalIgnoreCase);
		private readonly ConcurrentDictionary<string, WeakReference<ListedItem>> _itemReferences = new(StringComparer.OrdinalIgnoreCase);
		private readonly ConcurrentQueue<ThumbnailLoadRequest> _loadQueue = new();
		private readonly ConcurrentDictionary<string, RequestStatus> _requestStatus = new(StringComparer.OrdinalIgnoreCase); // Track request status instead of simple boolean
		private readonly ConcurrentDictionary<string, List<WeakReference<ListedItem>>> _pendingRequestsForPath = new(StringComparer.OrdinalIgnoreCase); // Track all items waiting for same path
		
		// Synchronization
		private readonly SemaphoreSlim _loadingSemaphore = new(1, 1);
		private readonly CancellationTokenSource _serviceCancellationTokenSource = new();
		private CancellationTokenSource _currentBatchCancellationTokenSource = new();
		
		// Background processing
		private readonly Task _processingTask;
		private readonly AutoResetEvent _workAvailable = new(false);
		
		// Scroll velocity tracking
		private readonly Queue<DateTime> _viewportUpdateTimes = new();
		private readonly object _velocityLock = new();
		private double _currentScrollVelocity = 0;
		private DateTime _lastCoalesceTime = DateTime.UtcNow;
		
		// Constants
		private const int BATCH_SIZE = 5; // TEMP: Reduced from 10 for testing
		private const int PROCESSING_DELAY_MS = 200; // TEMP: Increased to 200ms for testing timing issues
		private const int MAX_RETRY_COUNT = 2;
		private const int MAX_CONCURRENT_LOADS = 5; // TEMP: Reduced from 10 for testing
		private const int SCROLL_VELOCITY_THRESHOLD = 30; // Events/sec threshold for high-speed scrolling
		private const int HIGH_SPEED_BATCH_SIZE = 3; // TEMP: Reduced from 5 for testing
		private const int COALESCE_WINDOW_MS = 100; // Window for coalescing requests
		private const long MEMORY_PRESSURE_THRESHOLD = 300_000_000; // 300MB threshold for GC
		
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
			
			// Try to get the current thread's dispatcher queue
			_dispatcherQueue = DispatcherQueue.GetForCurrentThread();
			
			// Log warning if we're not on a UI thread
			if (_dispatcherQueue == null)
			{
				_logger?.LogWarning("[DISPATCHER] SafeViewportThumbnailLoaderService created without DispatcherQueue - will need to get UI queue later");
			}
			else
			{
				_logger?.LogInformation("[DISPATCHER] SafeViewportThumbnailLoaderService created with DispatcherQueue on thread {ThreadId}", System.Threading.Thread.CurrentThread.ManagedThreadId);
			}
			
			// Remove thread pool limit - let system manage it
			// ThreadPool.SetMaxThreads(100, 100); // This was too restrictive
			
			// Start background processing task
			_processingTask = Task.Run(ProcessThumbnailQueueAsync, _serviceCancellationTokenSource.Token);
		}
		
		private DispatcherQueue GetOrCreateDispatcherQueue()
		{
			// If we already have a valid dispatcher queue, use it
			if (_dispatcherQueue != null)
				return _dispatcherQueue;
			
			// Try to get the main window's dispatcher queue
			try
			{
				if (App.Window != null && App.Window.DispatcherQueue != null)
				{
					_dispatcherQueue = App.Window.DispatcherQueue;
					_logger?.LogInformation("[DISPATCHER] Got UI DispatcherQueue from App.Window");
					return _dispatcherQueue;
				}
			}
			catch (Exception ex)
			{
				_logger?.LogWarning(ex, "[DISPATCHER] Failed to get DispatcherQueue from App.Window");
			}
			
			// Last resort - try current thread
			_dispatcherQueue = DispatcherQueue.GetForCurrentThread();
			if (_dispatcherQueue != null)
			{
				_logger?.LogWarning("[DISPATCHER] Using current thread's DispatcherQueue as fallback");
			}
			
			return _dispatcherQueue;
		}

		public async Task UpdateViewportAsync(IEnumerable<ListedItem> visibleItems, uint thumbnailSize, CancellationToken cancellationToken = default)
		{
			if (visibleItems == null)
			{
				_logger?.LogDebug("UpdateViewportAsync called with null items");
				return;
			}

			var updateId = Guid.NewGuid().ToString().Substring(0, 8);
			var itemsList = visibleItems.ToList();
			_logger?.LogInformation("[{UpdateId}] UpdateViewportAsync started with {Count} visible items, thumbnailSize: {Size}", updateId, itemsList.Count, thumbnailSize);
			
			// Log details about visible items
			if (itemsList.Count > 0 && itemsList.Count <= 10)
			{
				foreach (var item in itemsList.Take(5))
				{
					_logger?.LogDebug("[{UpdateId}] Visible item: Path={Path}, HasThumbnail={HasThumbnail}, IsFolder={IsFolder}", 
						updateId, item?.ItemPath, item?.FileImage != null, item?.IsFolder);
				}
			}

			// Track scroll velocity
			UpdateScrollVelocity();
			
			// Check if we should coalesce this update
			if (_currentScrollVelocity > SCROLL_VELOCITY_THRESHOLD)
			{
				var timeSinceLastCoalesce = DateTime.UtcNow - _lastCoalesceTime;
				if (timeSinceLastCoalesce.TotalMilliseconds < COALESCE_WINDOW_MS)
				{
					_logger?.LogDebug("[{UpdateId}] Coalescing viewport update due to high scroll velocity: {Velocity:F1} events/sec", 
						updateId, _currentScrollVelocity);
					return; // Skip this update, will be handled by next one
				}
				_lastCoalesceTime = DateTime.UtcNow;
			}

			try
			{
				// Cancel previous batch
				_logger?.LogDebug("[{UpdateId}] Cancelling previous batch", updateId);
				var oldCts = _currentBatchCancellationTokenSource;
				_currentBatchCancellationTokenSource = new CancellationTokenSource();
				oldCts.Cancel();
				oldCts.Dispose();
				
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
				var skippedCount = 0;
				var alreadyHasThumbnail = 0;
				
				// Check thread pool availability before queueing
				ThreadPool.GetAvailableThreads(out int workerThreads, out int ioThreads);
				if (workerThreads < 50)
				{
					_logger?.LogWarning("[{UpdateId}] Thread pool low on workers: {Workers} available, skipping viewport update", updateId, workerThreads);
					return;
				}
				
				foreach (var item in itemsToProcess)
				{
					if (linkedToken.IsCancellationRequested)
					{
						_logger?.LogDebug("[{UpdateId}] Viewport update cancelled during queueing", updateId);
						break;
					}
					
					if (item?.FileImage == null)
					{
						_logger?.LogDebug("[{UpdateId}] Item needs thumbnail: {Path}", updateId, item.ItemPath);
						
						// Check cache first
						BitmapImage? cached = _cacheService?.GetCachedThumbnail(item.ItemPath);
						if (cached != null)
						{
							cachedCount++;
							_logger?.LogDebug("[{UpdateId}] Found cached thumbnail for {Path}", updateId, item.ItemPath);
							// Update on UI thread safely
							await UpdateItemThumbnailSafelyAsync(item, cached);
							continue;
						}

						// Queue for loading (with deduplication and request coalescing)
						var itemRef = new WeakReference<ListedItem>(item);
						
						// Add to pending requests for this path
						_pendingRequestsForPath.AddOrUpdate(item.ItemPath, 
							new List<WeakReference<ListedItem>> { itemRef },
							(key, list) => 
							{
								list.Add(itemRef);
								return list;
							});
						
						// Check current request status
						var canQueue = false;
						if (_requestStatus.TryGetValue(item.ItemPath, out var status))
						{
							// Allow re-queueing if previous attempt failed or was cancelled
							if (status == RequestStatus.Failed || status == RequestStatus.Cancelled)
							{
								canQueue = _requestStatus.TryUpdate(item.ItemPath, RequestStatus.Queued, status);
								_logger?.LogDebug("[{UpdateId}] Re-queueing previously {Status} request for {Path}", updateId, status, item.ItemPath);
							}
							else
							{
								_logger?.LogDebug("[{UpdateId}] Path already has status {Status}, added to pending list: {Path}", updateId, status, item.ItemPath);
								skippedCount++;
							}
						}
						else
						{
							// First request for this path
							canQueue = _requestStatus.TryAdd(item.ItemPath, RequestStatus.Queued);
						}
						
						if (canQueue)
						{
							var request = new ThumbnailLoadRequest
							{
								Path = item.ItemPath,
								ItemReference = itemRef,
								ThumbnailSize = thumbnailSize,
								IsPriority = true
							};
							
							_loadQueue.Enqueue(request);
							queuedCount++;
							_logger?.LogDebug("[{UpdateId}] Queued thumbnail load for {Path}", updateId, item.ItemPath);
						}
					}
					else
					{
						alreadyHasThumbnail++;
					}
				}

				_logger?.LogInformation("[{UpdateId}] Viewport update complete: {Queued} queued, {Cached} from cache, {Skipped} skipped, {HasThumbnail} already have thumbnails, Queue size: {QueueSize}", 
					updateId, queuedCount, cachedCount, skippedCount, alreadyHasThumbnail, _loadQueue.Count);

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
					// Only queue if not already queued (deduplication)
					if (_queuedPaths.TryAdd(item.ItemPath, true))
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
			var lastHealthCheck = DateTime.UtcNow;
			var processedSinceLastCheck = 0;
			
			while (!_serviceCancellationTokenSource.Token.IsCancellationRequested)
			{
				try
				{
					// Check memory pressure
					var totalMemory = GC.GetTotalMemory(false);
					if (totalMemory > MEMORY_PRESSURE_THRESHOLD)
					{
						_logger?.LogWarning("[MEMORY] High memory usage detected: {Memory:N0} bytes. Forcing GC.", totalMemory);
						GC.Collect(2, GCCollectionMode.Forced);
						await Task.Delay(500); // Give GC time to work
					}
					
					// Check thread pool health
					ThreadPool.GetAvailableThreads(out int workerThreads, out int ioThreads);
					if (workerThreads < 20)
					{
						_logger?.LogWarning("[THREADPOOL] Low on worker threads: {Workers} available. Pausing processing.", workerThreads);
						await Task.Delay(1000); // Pause to let threads complete
						continue;
					}
					
					// Log health status every 10 seconds
					if (DateTime.UtcNow - lastHealthCheck > TimeSpan.FromSeconds(10))
					{
						_logger?.LogInformation("[HEALTH] Queue: {QueueSize}, Queued paths: {QueuedPaths}, Visible: {Visible}, Processed: {Processed}/10s, Memory: {Memory:N0} bytes, Threads: {Threads}",
							_loadQueue.Count, _queuedPaths.Count, _visibleItems.Count, processedSinceLastCheck, totalMemory, workerThreads);
						
						// Report resource usage
						ResourceMonitor.ReportResourceUsage("SafeViewportThumbnailLoader");
						ResourceMonitor.ReportThreadPoolStatus("SafeViewportThumbnailLoader");
						
						lastHealthCheck = DateTime.UtcNow;
						processedSinceLastCheck = 0;
					}
					
					// Wait for work
					var gotSignal = _workAvailable.WaitOne(TimeSpan.FromSeconds(1));
					
					if (!gotSignal)
					{
						// Timeout - log if queue is not empty
						if (_loadQueue.Count > 0)
						{
							_logger?.LogWarning("[STUCK] Processing thread timeout but queue has {Count} items!", _loadQueue.Count);
							// Force resource report when stuck
							ResourceMonitor.ReportResourceUsage("STUCK-SafeViewportThumbnailLoader");
							ResourceMonitor.ReportThreadPoolStatus("STUCK-SafeViewportThumbnailLoader");
						}
						continue;
					}
					
					if (_serviceCancellationTokenSource.Token.IsCancellationRequested)
					{
						_logger?.LogDebug("Processing thread shutdown requested");
						break;
					}

					// Process queue in batches - smaller batches during high-speed scrolling
					var batchSize = _currentScrollVelocity > SCROLL_VELOCITY_THRESHOLD ? HIGH_SPEED_BATCH_SIZE : BATCH_SIZE;
					var batch = new List<ThumbnailLoadRequest>(batchSize);
					var skippedCount = 0;
					
					while (batch.Count < batchSize && _loadQueue.TryDequeue(out var request))
					{
						// Skip priority items that are no longer visible
						if (request.IsPriority && !_visibleItems.ContainsKey(request.Path))
						{
							// Don't remove from tracking - just defer for later
							// Re-enqueue at the end if not at max retries
							if (request.RetryCount < MAX_RETRY_COUNT)
							{
								request.RetryCount++;
								_loadQueue.Enqueue(request);
							}
							else
							{
								// Only remove if we've exceeded retry count
								_queuedPaths.TryRemove(request.Path, out _);
							}
							skippedCount++;
							continue;
						}
						
						// For non-priority items, check if we should process them
						if (!request.IsPriority && _loadQueue.Count > BATCH_SIZE * 2)
						{
							// Skip non-priority items when queue is backed up
							_queuedPaths.TryRemove(request.Path, out _);
							skippedCount++;
							continue;
						}
							
						batch.Add(request);
					}

					if (batch.Count == 0)
					{
						if (skippedCount > 0)
							_logger?.LogDebug("Skipped {Count} requests for non-visible items", skippedCount);
						
						// Log if we're stuck with items in queue but can't process them
						if (_loadQueue.Count > 0)
						{
							_logger?.LogWarning("[STUCK] Have {Count} items in queue but couldn't dequeue any! Queued paths: {QueuedCount}", 
								_loadQueue.Count, _queuedPaths.Count);
							
							// Clear stale entries from _queuedPaths if we're stuck
							if (_queuedPaths.Count > _loadQueue.Count * 2)
							{
								_logger?.LogWarning("[STUCK] Clearing stale entries from queued paths tracking");
								_queuedPaths.Clear();
							}
						}
						continue;
					}

					_logger?.LogDebug("Processing batch of {BatchSize} thumbnails (skipped {SkippedCount}), queue remaining: {QueueSize}", 
						batch.Count, skippedCount, _loadQueue.Count);

					// Process batch
					await ProcessBatchAsync(batch);
					processedSinceLastCheck += batch.Count;
					
					// Small delay to prevent overwhelming the system
					await Task.Delay(PROCESSING_DELAY_MS, _serviceCancellationTokenSource.Token);
					
					// TEMP: Additional delay after each batch for testing
					if (batch.Count > 5)
					{
						_logger?.LogDebug("[TEMP-DELAY] Adding extra 100ms delay after large batch");
						await Task.Delay(100, _serviceCancellationTokenSource.Token);
					}
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
			var batchId = Guid.NewGuid().ToString().Substring(0, 8);
			_logger?.LogDebug("[BATCH-{BatchId}] Starting batch processing of {Count} items", batchId, batch.Count);
			
			// Process in smaller groups to avoid thread explosion
			const int GROUP_SIZE = 2; // Reduced from 4 to prevent thread exhaustion
			
			for (int i = 0; i < batch.Count; i += GROUP_SIZE)
			{
				var group = batch.Skip(i).Take(GROUP_SIZE).ToList();
				var tasks = new List<Task>();
				
				foreach (var request in group)
				{
					// Don't use Task.Run - process on existing thread pool threads
					var task = ProcessSingleRequestAsync(request, batchId);
					tasks.Add(task);
				}
				
				// Wait for this group before starting next to avoid thread explosion
				await Task.WhenAll(tasks);
				
				// TEMP: Add delay between groups to ensure UI can keep up
				if (i + GROUP_SIZE < batch.Count)
				{
					_logger?.LogDebug("[TEMP-DELAY] Adding 50ms delay between processing groups");
					await Task.Delay(50);
				}
			}
			
			_logger?.LogDebug("[BATCH-{BatchId}] Batch processing completed", batchId);
		}

		private async Task ProcessSingleRequestAsync(ThumbnailLoadRequest request, string batchId)
		{
			var requestId = Guid.NewGuid().ToString().Substring(0, 8);
			_logger?.LogDebug("[BATCH-{BatchId}][REQ-{RequestId}] Starting ProcessSingleRequestAsync for {Path}", batchId, requestId, request.Path);
			
			try
			{
				// Check if item still exists
				if (!request.ItemReference.TryGetTarget(out var item))
				{
					_logger?.LogDebug("[BATCH-{BatchId}][REQ-{RequestId}] Item reference lost for {Path}", batchId, requestId, request.Path);
					// Remove from tracking since the item is gone
					_queuedPaths.TryRemove(request.Path, out _);
					return;
				}

				// Skip if already has thumbnail
				if (item.FileImage != null)
				{
					_logger?.LogDebug("[BATCH-{BatchId}][REQ-{RequestId}] Item already has thumbnail: {Path}", batchId, requestId, request.Path);
					// Remove from tracking since it already has a thumbnail
					_queuedPaths.TryRemove(request.Path, out _);
					return;
				}

				_logger?.LogInformation("[BATCH-{BatchId}][REQ-{RequestId}] Loading thumbnail for {Path}, ThumbnailSize: {Size}, IsFolder: {IsFolder}", 
					batchId, requestId, request.Path, request.ThumbnailSize, item.PrimaryItemAttribute == Windows.Storage.StorageItemTypes.Folder);
				
				// Load thumbnail
				_logger?.LogDebug("[BATCH-{BatchId}][REQ-{RequestId}] Calling FileThumbnailHelper.GetIconAsync for {Path}", batchId, requestId, request.Path);
				
				byte[]? iconData = null;
				try
				{
					iconData = await FileThumbnailHelper.GetIconAsync(
						request.Path,
						request.ThumbnailSize,
						item.PrimaryItemAttribute == Windows.Storage.StorageItemTypes.Folder,
						IconOptions.UseCurrentScale);
				}
				catch (Exception ex)
				{
					_logger?.LogError(ex, "[BATCH-{BatchId}][REQ-{RequestId}] FileThumbnailHelper.GetIconAsync threw exception for {Path}", batchId, requestId, request.Path);
					throw;
				}

				if (iconData != null && iconData.Length > 0)
				{
					_logger?.LogInformation("[BATCH-{BatchId}][REQ-{RequestId}] Got icon data ({Size} bytes) for {Path}", batchId, requestId, iconData.Length, request.Path);
					
					// Create BitmapImage on UI thread to avoid COM exceptions
					BitmapImage? image = null;
					var dispatcherQueue = GetOrCreateDispatcherQueue();
					if (dispatcherQueue != null)
					{
						_logger?.LogDebug("[BATCH-{BatchId}][REQ-{RequestId}] Creating bitmap on UI thread for {Path}", batchId, requestId, request.Path);
						
						var tcs = new TaskCompletionSource<BitmapImage?>();
						bool enqueued = dispatcherQueue.TryEnqueue(async () =>
						{
							try
							{
								_logger?.LogDebug("[BATCH-{BatchId}][REQ-{RequestId}] UI thread: Creating bitmap for {Path}", batchId, requestId, request.Path);
								image = await iconData.ToBitmapAsync();
								_logger?.LogDebug("[BATCH-{BatchId}][REQ-{RequestId}] UI thread: Bitmap created successfully for {Path}, IsNull: {IsNull}", 
									batchId, requestId, request.Path, image == null);
								tcs.SetResult(image);
							}
							catch (Exception ex)
							{
								_logger?.LogError(ex, "[BATCH-{BatchId}][REQ-{RequestId}] UI thread: Failed to create bitmap for {Path}", batchId, requestId, request.Path);
								tcs.SetResult(null);
							}
						});
						
						if (!enqueued)
						{
							_logger?.LogError("[BATCH-{BatchId}][REQ-{RequestId}] Failed to enqueue bitmap creation for {Path}", batchId, requestId, request.Path);
							tcs.SetResult(null);
						}
						
						image = await tcs.Task;
					}
					else
					{
						// Fallback to direct creation
						image = await iconData.ToBitmapAsync();
					}
					
					if (image != null)
					{
						_logger?.LogInformation("[BATCH-{BatchId}][REQ-{RequestId}] Successfully created bitmap for {Path}", batchId, requestId, request.Path);
						
						// Update all items waiting for this thumbnail
						var itemsToUpdate = new List<ListedItem>();
						
						// Get all pending items for this path
						if (_pendingRequestsForPath.TryRemove(request.Path, out var pendingRefs))
						{
							foreach (var pendingRef in pendingRefs)
							{
								if (pendingRef.TryGetTarget(out var pendingItem))
								{
									itemsToUpdate.Add(pendingItem);
								}
							}
							_logger?.LogDebug("[BATCH-{BatchId}][REQ-{RequestId}] Found {Count} items waiting for thumbnail: {Path}", 
								batchId, requestId, itemsToUpdate.Count, request.Path);
						}
						else
						{
							// No pending items, just update the original
							itemsToUpdate.Add(item);
						}
						
						// Update all items
						foreach (var itemToUpdate in itemsToUpdate)
						{
							_logger?.LogDebug("[BATCH-{BatchId}][REQ-{RequestId}] Updating thumbnail for item: {Path}", batchId, requestId, itemToUpdate.ItemPath);
							await UpdateItemThumbnailSafelyAsync(itemToUpdate, image);
						}
						
						_logger?.LogInformation("[BATCH-{BatchId}][REQ-{RequestId}] Updated {Count} items with thumbnail for {Path}", 
							batchId, requestId, itemsToUpdate.Count, request.Path);
						
						// Add to cache on UI thread to avoid COM exceptions
						var cacheDispatcher = GetOrCreateDispatcherQueue();
						if (cacheDispatcher != null)
						{
							var tcs2 = new TaskCompletionSource<bool>();
							cacheDispatcher.TryEnqueue(() =>
							{
								try
								{
									_cacheService?.AddOrUpdateThumbnail(request.Path, image);
									tcs2.SetResult(true);
								}
								catch (Exception ex)
								{
									tcs2.SetException(ex);
								}
							});
							await tcs2.Task;
						}
						else
						{
							// Fallback to direct cache update
							_cacheService?.AddOrUpdateThumbnail(request.Path, image);
						}
						
						_logger?.LogDebug("[BATCH-{BatchId}] Successfully loaded thumbnail for {Path}", batchId, request.Path);
						
						// Successfully processed - remove from tracking
						_queuedPaths.TryRemove(request.Path, out _);
					}
					else
					{
						_logger?.LogWarning("[BATCH-{BatchId}][REQ-{RequestId}] Failed to create bitmap from icon data for {Path} (image is null)", batchId, requestId, request.Path);
						// Retry if under limit
						if (request.RetryCount < MAX_RETRY_COUNT)
						{
							request.RetryCount++;
							_loadQueue.Enqueue(request);
							_logger?.LogDebug("[BATCH-{BatchId}] Queued retry {RetryCount} for {Path} after bitmap creation failure", batchId, request.RetryCount, request.Path);
						}
						else
						{
							// Remove from tracking only after max retries
							_queuedPaths.TryRemove(request.Path, out _);
							_pendingRequestsForPath.TryRemove(request.Path, out _);
						}
					}
				}
				else
				{
					_logger?.LogWarning("[BATCH-{BatchId}][REQ-{RequestId}] No icon data returned for {Path} (iconData is {IconDataState})", 
						batchId, requestId, request.Path, iconData == null ? "null" : "empty");
					// Retry if under limit
					if (request.RetryCount < MAX_RETRY_COUNT)
					{
						request.RetryCount++;
						_loadQueue.Enqueue(request);
						_logger?.LogDebug("[BATCH-{BatchId}] Queued retry {RetryCount} for {Path} after no icon data", batchId, request.RetryCount, request.Path);
					}
					else
					{
						// Remove from tracking only after max retries
						_queuedPaths.TryRemove(request.Path, out _);
						_pendingRequestsForPath.TryRemove(request.Path, out _);
					}
				}
			}
			catch (Exception ex)
			{
				_logger?.LogError(ex, "[BATCH-{BatchId}][REQ-{RequestId}] Exception in ProcessSingleRequestAsync for {Path}. Retry: {RetryCount}/{MaxRetry}", 
					batchId, requestId, request.Path, request.RetryCount, MAX_RETRY_COUNT);
				
				// Retry if needed
				if (request.RetryCount < MAX_RETRY_COUNT)
				{
					request.RetryCount++;
					if (_queuedPaths.TryAdd(request.Path, true))
					{
						_loadQueue.Enqueue(request);
						_logger?.LogDebug("[BATCH-{BatchId}] Queued retry {RetryCount} for {Path}", batchId, request.RetryCount, request.Path);
					}
					else
					{
						_logger?.LogWarning("[BATCH-{BatchId}] Failed to queue retry for {Path} - already queued", batchId, request.Path);
					}
				}
				else
				{
					_logger?.LogError("[BATCH-{BatchId}] Giving up on {Path} after {MaxRetry} retries", batchId, request.Path, MAX_RETRY_COUNT);
					// Remove from tracking since we're giving up
					_queuedPaths.TryRemove(request.Path, out _);
					// Also remove pending requests for this path
					_pendingRequestsForPath.TryRemove(request.Path, out _);
				}
			}
		}

		private async Task UpdateItemThumbnailSafelyAsync(ListedItem item, BitmapImage thumbnail)
		{
			var updateId = Guid.NewGuid().ToString().Substring(0, 8);
			
			try
			{
				var dispatcherQueue = GetOrCreateDispatcherQueue();
				_logger?.LogDebug("[{UpdateId}] UpdateItemThumbnailSafelyAsync starting for {Path}, HasDispatcher: {HasDispatcher}", 
					updateId, item?.ItemPath, dispatcherQueue != null);
				
				if (dispatcherQueue == null)
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
					enqueued = dispatcherQueue.TryEnqueue(() =>
					{
						try
						{
							_logger?.LogDebug("[{UpdateId}] UI thread: Checking if item needs thumbnail update for {Path}, Current FileImage: {HasImage}", 
								updateId, item?.ItemPath, item?.FileImage != null);
							
							// Only update if item still doesn't have a thumbnail
							if (item.FileImage == null)
							{
								_logger?.LogDebug("[{UpdateId}] UI thread: Setting FileImage property for {Path}", updateId, item.ItemPath);
								item.FileImage = thumbnail;
								_logger?.LogInformation("[{UpdateId}] UI thread: Thumbnail SUCCESSFULLY SET for {Path}", updateId, item.ItemPath);
							}
							else
							{
								_logger?.LogDebug("[{UpdateId}] UI thread: Item already has thumbnail, skipping {Path}", updateId, item.ItemPath);
							}
							tcs.SetResult(true);
						}
						catch (Exception ex)
						{
							_logger?.LogError(ex, "[{UpdateId}] UI thread: Error updating thumbnail for {Path}", updateId, item?.ItemPath);
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
			var oldCts = _currentBatchCancellationTokenSource;
			_currentBatchCancellationTokenSource = new CancellationTokenSource();
			oldCts.Cancel();
			oldCts.Dispose();
			
			_visibleItems.Clear();
			_itemReferences.Clear();
			_queuedPaths.Clear();
			
			// Clear queue
			while (_loadQueue.TryDequeue(out _)) { }
			
			// Clear pending requests
			_pendingRequestsForPath.Clear();
		}

		private void UpdateScrollVelocity()
		{
			lock (_velocityLock)
			{
				var now = DateTime.UtcNow;
				_viewportUpdateTimes.Enqueue(now);
				
				// Keep only updates from the last second
				while (_viewportUpdateTimes.Count > 0 && (now - _viewportUpdateTimes.Peek()).TotalSeconds > 1.0)
				{
					_viewportUpdateTimes.Dequeue();
				}
				
				// Calculate velocity (events per second)
				_currentScrollVelocity = _viewportUpdateTimes.Count;
				
				if (_currentScrollVelocity > SCROLL_VELOCITY_THRESHOLD)
				{
					_logger?.LogDebug("High scroll velocity detected: {Velocity:F1} events/sec", _currentScrollVelocity);
				}
			}
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