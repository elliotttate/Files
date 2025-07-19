// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.Mvvm.DependencyInjection;
using Files.App.Data.Contracts;
using Files.App.Extensions;
using Files.App.Services.Caching;
using Files.App.Utils;
using Files.App.Utils.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Files.App.Services.Thumbnails
{
	/// <summary>
	/// Implements an optimized thumbnail loading queue with priority-based processing,
	/// batching support, and integration with FileModelCacheService.
	/// </summary>
	public sealed class ThumbnailLoadingQueue : IThumbnailLoadingQueue
	{
		// Services
		private readonly IFileModelCacheService _cacheService;
		private readonly IThreadingService _threadingService;
		private readonly ILogger<ThumbnailLoadingQueue> _logger;

		// Queue structures
		private readonly PriorityQueue<InternalThumbnailRequest, int> _requestQueue = new();
		private readonly ConcurrentDictionary<string, InternalThumbnailRequest> _pendingRequests = new(StringComparer.OrdinalIgnoreCase);
		private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellationTokens = new(StringComparer.OrdinalIgnoreCase);
		
		// Processing control
		private readonly SemaphoreSlim _queueSemaphore = new(0);
		private readonly SemaphoreSlim _processingSlots;
		private readonly CancellationTokenSource _serviceCancellationTokenSource = new();
		private readonly object _queueLock = new();
		
		// Configuration
		private const int MAX_CONCURRENT_LOADS = 8;
		private const int MAX_QUEUE_SIZE = 1000;
		private const int BATCH_SIZE = 20;
		private const int PROGRESS_REPORT_INTERVAL_MS = 500;
		
		// Statistics
		private int _activeRequests;
		private int _processedCount;
		private readonly List<double> _recentLoadTimes = new();
		private readonly object _statsLock = new();
		private DateTime _lastProgressReport = DateTime.UtcNow;

		// Background tasks
		private readonly Task[] _processingTasks;
		private readonly Timer _progressReportTimer;

		// Events
		public event EventHandler<ThumbnailLoadedEventArgs>? ThumbnailLoaded;
		public event EventHandler<BatchThumbnailLoadedEventArgs>? BatchCompleted;
		public event EventHandler<ThumbnailQueueProgressEventArgs>? ProgressChanged;

		// Properties
		public int QueueDepth
		{
			get
			{
				lock (_queueLock)
				{
					return _requestQueue.Count;
				}
			}
		}

		public int ActiveRequests => _activeRequests;

		public ThumbnailLoadingQueue()
		{
			_cacheService = Ioc.Default.GetRequiredService<IFileModelCacheService>();
			_threadingService = Ioc.Default.GetRequiredService<IThreadingService>();
			_logger = Ioc.Default.GetRequiredService<ILogger<ThumbnailLoadingQueue>>();

			_processingSlots = new SemaphoreSlim(MAX_CONCURRENT_LOADS, MAX_CONCURRENT_LOADS);

			// Start processing tasks
			_processingTasks = new Task[Math.Min(Environment.ProcessorCount, MAX_CONCURRENT_LOADS)];
			for (int i = 0; i < _processingTasks.Length; i++)
			{
				_processingTasks[i] = Task.Run(ProcessQueueAsync);
			}

			// Start progress reporting timer
			_progressReportTimer = new Timer(
				ReportProgress,
				null,
				TimeSpan.FromMilliseconds(PROGRESS_REPORT_INTERVAL_MS),
				TimeSpan.FromMilliseconds(PROGRESS_REPORT_INTERVAL_MS));
		}

		public async Task<ThumbnailLoadResult> QueueThumbnailRequestAsync(
			string path,
			ListedItem item,
			uint thumbnailSize,
			int priority,
			CancellationToken cancellationToken)
		{
			if (string.IsNullOrEmpty(path) || item == null)
			{
				return new ThumbnailLoadResult
				{
					Path = path,
					Success = false,
					ErrorMessage = "Invalid path or item"
				};
			}

			// Check cache first
			var cached = _cacheService.GetCachedThumbnail(path);
			if (cached != null)
			{
				return new ThumbnailLoadResult
				{
					Path = path,
					Thumbnail = cached,
					Success = true,
					LoadTime = TimeSpan.Zero
				};
			}

			// Create internal request
			var internalRequest = new InternalThumbnailRequest
			{
				Path = path,
				Item = item,
				ThumbnailSize = thumbnailSize,
				Priority = priority,
				IconOptions = IconOptions.None,
				QueuedTime = DateTime.UtcNow,
				CompletionSource = new TaskCompletionSource<ThumbnailLoadResult>()
			};

			// Check queue size limit and handle overflow
			lock (_queueLock)
			{
				if (_requestQueue.Count >= MAX_QUEUE_SIZE)
				{
					// Remove oldest low-priority requests to make room
					var removedCount = RemoveOldestLowPriorityRequests(MAX_QUEUE_SIZE / 10); // Remove 10% of queue
					
					if (_requestQueue.Count >= MAX_QUEUE_SIZE)
					{
						// Still full after cleanup
						_logger.LogWarning("Thumbnail queue overflow. Removed {Count} items but queue still full", removedCount);
						return new ThumbnailLoadResult
						{
							Path = path,
							Success = false,
							ErrorMessage = "Queue is full after cleanup"
						};
					}
					
					_logger.LogDebug("Thumbnail queue overflow handled. Removed {Count} old requests", removedCount);
				}

				// Cancel any existing request for the same path
				if (_pendingRequests.TryRemove(path, out var existingRequest))
				{
					existingRequest.CompletionSource.TrySetResult(new ThumbnailLoadResult
					{
						Path = path,
						Success = false,
						WasCancelled = true
					});
				}

				// Add to queue
				_pendingRequests[path] = internalRequest;
				_requestQueue.Enqueue(internalRequest, -priority); // Negative for max-heap behavior
			}

			// Create linked cancellation token
			var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
				cancellationToken,
				_serviceCancellationTokenSource.Token);
			_cancellationTokens[path] = linkedCts;

			// Signal processing
			_queueSemaphore.Release();

			// Register cancellation callback
			linkedCts.Token.Register(() =>
			{
				if (_pendingRequests.TryRemove(path, out var request))
				{
					request.CompletionSource.TrySetResult(new ThumbnailLoadResult
					{
						Path = path,
						Success = false,
						WasCancelled = true
					});
				}
				// Don't dispose here - it will be disposed in the finally block of ProcessRequestAsync
				_cancellationTokens.TryRemove(path, out _);
			});

			return await internalRequest.CompletionSource.Task;
		}

		public async Task<IEnumerable<ThumbnailLoadResult>> QueueBatchRequestAsync(
			IEnumerable<ThumbnailRequest> requests,
			CancellationToken cancellationToken)
		{
			var tasks = new List<Task<ThumbnailLoadResult>>();

			foreach (var request in requests)
			{
				var task = QueueThumbnailRequestAsync(
					request.Path,
					request.Item,
					request.ThumbnailSize,
					request.Priority,
					cancellationToken);
				tasks.Add(task);
			}

			var results = await Task.WhenAll(tasks);

			// Raise batch completed event
			var successCount = results.Count(r => r.Success);
			var totalTime = results.Sum(r => r.LoadTime.TotalMilliseconds);

			BatchCompleted?.Invoke(this, new BatchThumbnailLoadedEventArgs
			{
				Results = results,
				TotalRequests = results.Length,
				SuccessfulLoads = successCount,
				TotalLoadTime = TimeSpan.FromMilliseconds(totalTime)
			});

			return results;
		}

		public bool UpdateRequestPriority(string path, int newPriority)
		{
			lock (_queueLock)
			{
				if (_pendingRequests.TryGetValue(path, out var request))
				{
					request.Priority = newPriority;
					// Note: PriorityQueue doesn't support updating priorities,
					// so this will only affect future dequeues
					return true;
				}
			}
			return false;
		}

		public bool CancelRequest(string path)
		{
			if (_cancellationTokens.TryRemove(path, out var cts))
			{
				cts.Cancel();
				cts.Dispose();
				return true;
			}
			return false;
		}

		public int CancelRequests(Func<ThumbnailRequest, bool> predicate)
		{
			var cancelledCount = 0;
			var pathsToCancel = new List<string>();

			lock (_queueLock)
			{
				foreach (var kvp in _pendingRequests)
				{
					var request = kvp.Value;
					var thumbnailRequest = new ThumbnailRequest
					{
						Path = request.Path,
						Item = request.Item,
						ThumbnailSize = request.ThumbnailSize,
						Priority = request.Priority,
						IconOptions = request.IconOptions,
						QueuedTime = request.QueuedTime
					};

					if (predicate(thumbnailRequest))
					{
						pathsToCancel.Add(kvp.Key);
					}
				}
			}

			foreach (var path in pathsToCancel)
			{
				if (CancelRequest(path))
				{
					cancelledCount++;
				}
			}

			return cancelledCount;
		}

		private async Task ProcessQueueAsync()
		{
			var batch = new List<InternalThumbnailRequest>(BATCH_SIZE);

			while (!_serviceCancellationTokenSource.Token.IsCancellationRequested)
			{
				try
				{
					// Wait for items in queue
					await _queueSemaphore.WaitAsync(_serviceCancellationTokenSource.Token);

					// Collect a batch of requests
					batch.Clear();
					lock (_queueLock)
					{
						while (batch.Count < BATCH_SIZE && _requestQueue.TryDequeue(out var request, out _))
						{
							// Skip if already cancelled
							if (_pendingRequests.ContainsKey(request.Path))
							{
								batch.Add(request);
							}
						}
					}

					if (batch.Count > 0)
					{
						// Process batch
						await ProcessBatchAsync(batch);
					}
				}
				catch (OperationCanceledException)
				{
					break;
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Error in thumbnail processing task");
				}
			}
		}

		private async Task ProcessBatchAsync(List<InternalThumbnailRequest> batch)
		{
			// Group by similar requests for potential optimization
			var groups = batch.GroupBy(r => new { r.ThumbnailSize, r.IconOptions });

			foreach (var group in groups)
			{
				var tasks = new List<Task>();

				foreach (var request in group)
				{
					// Check if still valid
					if (!_pendingRequests.ContainsKey(request.Path))
						continue;

					// Get cancellation token
					if (!_cancellationTokens.TryGetValue(request.Path, out var cts))
						continue;

					// Process individual request
					var task = ProcessSingleRequestAsync(request, cts.Token);
					tasks.Add(task);
				}

				// Wait for group to complete
				await Task.WhenAll(tasks);
			}
		}

		private async Task ProcessSingleRequestAsync(InternalThumbnailRequest request, CancellationToken cancellationToken)
		{
			await _processingSlots.WaitAsync(cancellationToken);
			Interlocked.Increment(ref _activeRequests);

			var stopwatch = Stopwatch.StartNew();
			ThumbnailLoadResult result;

			try
			{
				// Load thumbnail using Win32 API
				var thumbnailData = await FileThumbnailHelper.GetIconAsync(
					request.Path,
					request.ThumbnailSize,
					request.Item.IsFolder,
					request.IconOptions);

				if (thumbnailData != null && !cancellationToken.IsCancellationRequested)
				{
					// Convert to BitmapImage on UI thread
					BitmapImage? bitmap = null;
					await _threadingService.ExecuteOnUiThreadAsync(async () =>
					{
						try
						{
							bitmap = await thumbnailData.ToBitmapAsync();
							if (bitmap != null)
							{
								// Update cache
								_cacheService.AddOrUpdateThumbnail(request.Path, bitmap);
							}
						}
						catch (Exception ex)
						{
							_logger.LogWarning(ex, "Failed to create bitmap for {Path}", request.Path);
						}
					});

					result = new ThumbnailLoadResult
					{
						Path = request.Path,
						Thumbnail = bitmap,
						Success = bitmap != null,
						LoadTime = stopwatch.Elapsed,
						ErrorMessage = bitmap == null ? "Failed to create bitmap" : null
					};

					// Raise event
					if (bitmap != null)
					{
						ThumbnailLoaded?.Invoke(this, new ThumbnailLoadedEventArgs
						{
							Path = request.Path,
							Thumbnail = bitmap
						});
					}
				}
				else
				{
					result = new ThumbnailLoadResult
					{
						Path = request.Path,
						Success = false,
						LoadTime = stopwatch.Elapsed,
						WasCancelled = cancellationToken.IsCancellationRequested,
						ErrorMessage = cancellationToken.IsCancellationRequested ? "Cancelled" : "Failed to load thumbnail data"
					};
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to load thumbnail for {Path}", request.Path);
				result = new ThumbnailLoadResult
				{
					Path = request.Path,
					Success = false,
					LoadTime = stopwatch.Elapsed,
					ErrorMessage = ex.Message
				};
			}
			finally
			{
				// Cleanup
				_pendingRequests.TryRemove(request.Path, out _);
				if (_cancellationTokens.TryRemove(request.Path, out var cts))
				{
					cts?.Dispose(); // Properly dispose CancellationTokenSource to prevent resource leak
				}
				
				Interlocked.Decrement(ref _activeRequests);
				_processingSlots.Release();

				// Update statistics
				lock (_statsLock)
				{
					_processedCount++;
					_recentLoadTimes.Add(stopwatch.Elapsed.TotalMilliseconds);
					if (_recentLoadTimes.Count > 100)
					{
						_recentLoadTimes.RemoveAt(0);
					}
				}
			}

			// Complete the request
			request.CompletionSource.TrySetResult(result);
		}
		
		/// <summary>
		/// Removes oldest low-priority requests from the queue to prevent overflow
		/// </summary>
		private int RemoveOldestLowPriorityRequests(int countToRemove)
		{
			var removedCount = 0;
			
			// Note: This method must be called within a lock (_queueLock)
			// Get all items from queue
			var allRequests = new List<(InternalThumbnailRequest request, int priority)>();
			while (_requestQueue.TryDequeue(out var request, out var priority))
			{
				allRequests.Add((request, priority));
			}
			
			// Sort by priority (ascending) and then by queued time (oldest first)
			allRequests.Sort((a, b) => 
			{
				var priorityCompare = a.priority.CompareTo(b.priority);
				if (priorityCompare != 0) return priorityCompare;
				return a.request.QueuedTime.CompareTo(b.request.QueuedTime);
			});
			
			// Remove the specified number of lowest priority items
			for (int i = 0; i < Math.Min(countToRemove, allRequests.Count); i++)
			{
				var request = allRequests[i].request;
				if (_pendingRequests.TryRemove(request.Path, out _))
				{
					request.CompletionSource.TrySetResult(new ThumbnailLoadResult
					{
						Path = request.Path,
						Success = false,
						ErrorMessage = "Removed due to queue overflow",
						WasCancelled = true
					});
					
					_cancellationTokens.TryRemove(request.Path, out var cts);
					cts?.Cancel();
					cts?.Dispose();
					
					removedCount++;
				}
			}
			
			// Re-add remaining items back to queue
			for (int i = removedCount; i < allRequests.Count; i++)
			{
				_requestQueue.Enqueue(allRequests[i].request, allRequests[i].priority);
			}
			
			return removedCount;
		}

		private void ReportProgress(object? state)
		{
			if (DateTime.UtcNow - _lastProgressReport < TimeSpan.FromMilliseconds(PROGRESS_REPORT_INTERVAL_MS / 2))
				return;

			double averageLoadTime;
			int processedCount;

			lock (_statsLock)
			{
				averageLoadTime = _recentLoadTimes.Count > 0 ? _recentLoadTimes.Average() : 0;
				processedCount = _processedCount;
			}

			ProgressChanged?.Invoke(this, new ThumbnailQueueProgressEventArgs
			{
				QueueDepth = QueueDepth,
				ActiveRequests = ActiveRequests,
				ProcessedCount = processedCount,
				AverageLoadTimeMs = averageLoadTime
			});

			_lastProgressReport = DateTime.UtcNow;
		}

		public void Dispose()
		{
			// Signal cancellation
			_serviceCancellationTokenSource.Cancel();

			// Cancel all pending requests
			foreach (var kvp in _cancellationTokens)
			{
				kvp.Value.Cancel();
				kvp.Value.Dispose();
			}

			// Wait for processing tasks
			try
			{
				Task.WaitAll(_processingTasks, TimeSpan.FromSeconds(5));
			}
			catch (AggregateException) { }

			// Cleanup
			_progressReportTimer?.Dispose();
			_queueSemaphore?.Dispose();
			_processingSlots?.Dispose();
			_serviceCancellationTokenSource?.Dispose();

			// Complete any remaining requests
			foreach (var request in _pendingRequests.Values)
			{
				request.CompletionSource.TrySetResult(new ThumbnailLoadResult
				{
					Path = request.Path,
					Success = false,
					WasCancelled = true
				});
			}
		}

		private class InternalThumbnailRequest : ThumbnailRequest
		{
			public TaskCompletionSource<ThumbnailLoadResult> CompletionSource { get; init; } = null!;
		}
	}
}