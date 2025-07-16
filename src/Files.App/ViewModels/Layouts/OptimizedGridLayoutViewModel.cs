// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.WinUI;
using Files.App.Data.Contracts;
using Files.App.Extensions;
using Files.App.Services.Caching;
using Files.App.Services.Thumbnails;
using Files.App.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Linq;
using Windows.Foundation;
using Windows.Storage;

namespace Files.App.ViewModels.Layouts
{
	/// <summary>
	/// Enhanced view model for Grid Layout that integrates with ThumbnailLoadingQueue
	/// for optimized thumbnail loading with viewport awareness and scroll prediction.
	/// </summary>
	public class OptimizedGridLayoutViewModel : ObservableObject, IDisposable
	{
		// Services
		private readonly IThumbnailLoadingQueue _thumbnailQueue;
		private readonly ILogger<OptimizedGridLayoutViewModel> _logger;
		private readonly IThreadingService _threadingService;

		// Grid view reference
		private GridView? _gridView;
		private ScrollViewer? _scrollViewer;

		// Collection view for items
		private Microsoft.UI.Xaml.Data.ICollectionView? _itemsView;

		// Tracking visible items
		private readonly HashSet<string> _visibleItemPaths = new();
		private readonly ConcurrentDictionary<string, CancellationTokenSource> _loadingCancellations = new();

		// Viewport tracking
		private Rect _currentViewport;
		private double _lastScrollOffset;
		private bool _isScrollingDown = true;
		private DateTime _lastScrollTime = DateTime.UtcNow;

		// Configuration
		private const int VIEWPORT_BUFFER_ROWS = 2; // Load 2 rows ahead/behind
		private const int PREDICTIVE_LOAD_ITEMS = 20; // Load 20 items in scroll direction
		private const int BATCH_LOAD_SIZE = 10;
		private const int SCROLL_VELOCITY_THRESHOLD = 500; // px/s for fast scrolling
		private const double PRIORITY_DECAY_DISTANCE = 100; // Priority decay per 100px from viewport

		// Thumbnail size tracking
		private uint _currentThumbnailSize = 96;

		// Disposal
		private bool _disposed;

		public OptimizedGridLayoutViewModel()
		{
			_thumbnailQueue = Ioc.Default.GetRequiredService<IThumbnailLoadingQueue>();
			_logger = Ioc.Default.GetRequiredService<ILogger<OptimizedGridLayoutViewModel>>();
			_threadingService = Ioc.Default.GetRequiredService<IThreadingService>();

			// Subscribe to thumbnail events
			_thumbnailQueue.ThumbnailLoaded += OnThumbnailLoaded;
			_thumbnailQueue.ProgressChanged += OnProgressChanged;
		}

		/// <summary>
		/// Initializes the view model with a GridView control.
		/// </summary>
		public void Initialize(GridView gridView, Microsoft.UI.Xaml.Data.ICollectionView itemsView, uint thumbnailSize)
		{
			_gridView = gridView;
			_itemsView = itemsView;
			_currentThumbnailSize = thumbnailSize;

			// Find ScrollViewer
			_ = _threadingService.ExecuteOnUiThreadAsync(() =>
			{
				_scrollViewer = _gridView.FindDescendant<ScrollViewer>();
				if (_scrollViewer != null)
				{
					_scrollViewer.ViewChanged += OnScrollViewerViewChanged;
					_scrollViewer.ViewChanging += OnScrollViewerViewChanging;
				}
			});

			// Subscribe to collection changes
			if (_itemsView is INotifyCollectionChanged notifyCollection)
			{
				notifyCollection.CollectionChanged += OnItemsCollectionChanged;
			}

			// Initial load
			_ = LoadVisibleThumbnailsAsync();
		}

		/// <summary>
		/// Updates the thumbnail size and reloads visible items.
		/// </summary>
		public async Task UpdateThumbnailSizeAsync(uint newSize)
		{
			if (_currentThumbnailSize == newSize)
				return;

			_currentThumbnailSize = newSize;

			// Cancel all current loads
			CancelAllLoads();

			// Reload visible thumbnails with new size
			await LoadVisibleThumbnailsAsync();
		}

		/// <summary>
		/// Handles scroll view changing for predictive loading.
		/// </summary>
		private void OnScrollViewerViewChanging(object sender, ScrollViewerViewChangingEventArgs args)
		{
			if (!args.IsInertial)
				return;

			// Calculate scroll velocity
			var currentTime = DateTime.UtcNow;
			var timeDelta = (currentTime - _lastScrollTime).TotalSeconds;
			if (timeDelta <= 0)
				return;

			var scrollDelta = Math.Abs(args.FinalView.VerticalOffset - _lastScrollOffset);
			var velocity = scrollDelta / timeDelta;

			// Update scroll direction
			_isScrollingDown = args.FinalView.VerticalOffset > _lastScrollOffset;
			_lastScrollOffset = args.FinalView.VerticalOffset;
			_lastScrollTime = currentTime;

			// For fast scrolling, load predictively in scroll direction
			if (velocity > SCROLL_VELOCITY_THRESHOLD)
			{
				_ = LoadPredictiveThumbnailsAsync(args.FinalView);
			}
		}

		/// <summary>
		/// Handles scroll view changes to update visible items.
		/// </summary>
		private void OnScrollViewerViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
		{
			if (sender is not ScrollViewer scrollViewer)
				return;
				
			// Update viewport
			_currentViewport = new Rect(
				scrollViewer.HorizontalOffset,
				scrollViewer.VerticalOffset,
				scrollViewer.ViewportWidth,
				scrollViewer.ViewportHeight);

			// Don't load during scrolling for performance
			if (!e.IsIntermediate)
			{
				_ = LoadVisibleThumbnailsAsync();
			}
		}

		/// <summary>
		/// Loads thumbnails for currently visible items.
		/// </summary>
		private async Task LoadVisibleThumbnailsAsync()
		{
			if (_gridView == null || _scrollViewer == null || _itemsView == null)
				return;

			try
			{
				await _threadingService.ExecuteOnUiThreadAsync(async () =>
				{
					// Get visible range with buffer
					var visibleRange = GetVisibleItemsRange();
					if (!visibleRange.HasValue)
						return;

					var (startIndex, endIndex) = visibleRange.Value;

					// Track new visible items
					var newVisiblePaths = new HashSet<string>();
					var requests = new List<ThumbnailRequest>();

					// Create requests for visible items
					for (int i = startIndex; i <= endIndex && i < _itemsView.Count; i++)
					{
						if (i < _itemsView.Count && _itemsView.ElementAt(i) is ListedItem item && !string.IsNullOrEmpty(item.ItemPath))
						{
							newVisiblePaths.Add(item.ItemPath);

							// Skip if already has thumbnail
							if (item.CustomIcon != null)
								continue;

							// Calculate priority based on distance from viewport center
							var priority = CalculatePriority(i, startIndex, endIndex);

							requests.Add(new ThumbnailRequest
							{
								Path = item.ItemPath,
								Item = item,
								ThumbnailSize = _currentThumbnailSize,
								Priority = priority
							});
						}
					}

					// Cancel loads for items no longer visible
					var pathsToCancel = _visibleItemPaths.Except(newVisiblePaths).ToList();
					foreach (var path in pathsToCancel)
					{
						CancelThumbnailLoad(path);
					}

					_visibleItemPaths.Clear();
					foreach (var path in newVisiblePaths)
					{
						_visibleItemPaths.Add(path);
					}

					// Queue batch requests
					if (requests.Count > 0)
					{
						await QueueBatchRequestsAsync(requests);
					}
				});
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error loading visible thumbnails");
			}
		}

		/// <summary>
		/// Loads thumbnails predictively based on scroll direction.
		/// </summary>
		private async Task LoadPredictiveThumbnailsAsync(ScrollViewerView targetView)
		{
			if (_gridView == null || _itemsView == null)
				return;

			try
			{
				await _threadingService.ExecuteOnUiThreadAsync(async () =>
				{
					// Calculate predictive range
					var itemHeight = GetEstimatedItemHeight();
					if (itemHeight <= 0)
						return;

					var itemsPerRow = Math.Max(1, (int)(_scrollViewer!.ViewportWidth / GetEstimatedItemWidth()));
					var predictiveRows = PREDICTIVE_LOAD_ITEMS / itemsPerRow;
					var predictiveHeight = predictiveRows * itemHeight;

					int startIndex, endIndex;
					if (_isScrollingDown)
					{
						// Load ahead
						var topIndex = (int)(targetView.VerticalOffset / itemHeight) * itemsPerRow;
						var bottomIndex = (int)((targetView.VerticalOffset + _scrollViewer.ViewportHeight + predictiveHeight) / itemHeight) * itemsPerRow;
						startIndex = Math.Max(0, topIndex);
						endIndex = Math.Min(_itemsView.Count - 1, bottomIndex);
					}
					else
					{
						// Load behind
						var topIndex = (int)((targetView.VerticalOffset - predictiveHeight) / itemHeight) * itemsPerRow;
						var bottomIndex = (int)((targetView.VerticalOffset + _scrollViewer.ViewportHeight) / itemHeight) * itemsPerRow;
						startIndex = Math.Max(0, topIndex);
						endIndex = Math.Min(_itemsView.Count - 1, bottomIndex);
					}

					// Create predictive requests with lower priority
					var requests = new List<ThumbnailRequest>();
					for (int i = startIndex; i <= endIndex && i < _itemsView.Count; i++)
					{
						if (i < _itemsView.Count && _itemsView.ElementAt(i) is ListedItem item && 
						    !string.IsNullOrEmpty(item.ItemPath) && 
						    item.CustomIcon == null)
						{
							requests.Add(new ThumbnailRequest
							{
								Path = item.ItemPath,
								Item = item,
								ThumbnailSize = _currentThumbnailSize,
								Priority = -100 // Lower priority for predictive loads
							});
						}
					}

					if (requests.Count > 0)
					{
						await QueueBatchRequestsAsync(requests);
					}
				});
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error loading predictive thumbnails");
			}
		}

		/// <summary>
		/// Queues thumbnail requests in batches.
		/// </summary>
		private async Task QueueBatchRequestsAsync(List<ThumbnailRequest> requests)
		{
			// Process in batches to avoid overwhelming the queue
			for (int i = 0; i < requests.Count; i += BATCH_LOAD_SIZE)
			{
				var batch = requests.Skip(i).Take(BATCH_LOAD_SIZE);
				var cts = new CancellationTokenSource();

				// Track cancellation tokens
				foreach (var request in batch)
				{
					_loadingCancellations.TryAdd(request.Path, cts);
				}

				try
				{
					await _thumbnailQueue.QueueBatchRequestAsync(batch, cts.Token);
				}
				catch (OperationCanceledException)
				{
					// Expected when scrolling quickly
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Error queuing batch thumbnail requests");
				}
			}
		}

		/// <summary>
		/// Gets the range of visible items with buffer.
		/// </summary>
		private (int startIndex, int endIndex)? GetVisibleItemsRange()
		{
			if (_gridView == null || _scrollViewer == null)
				return null;

			var itemHeight = GetEstimatedItemHeight();
			var itemWidth = GetEstimatedItemWidth();
			if (itemHeight <= 0 || itemWidth <= 0)
				return null;

			var itemsPerRow = Math.Max(1, (int)(_scrollViewer.ViewportWidth / itemWidth));
			var bufferHeight = VIEWPORT_BUFFER_ROWS * itemHeight;

			// Calculate visible range with buffer
			var topRow = Math.Max(0, (int)((_scrollViewer.VerticalOffset - bufferHeight) / itemHeight));
			var bottomRow = (int)((_scrollViewer.VerticalOffset + _scrollViewer.ViewportHeight + bufferHeight) / itemHeight);

			var startIndex = topRow * itemsPerRow;
			var endIndex = (bottomRow + 1) * itemsPerRow - 1;

			return (startIndex, endIndex);
		}

		/// <summary>
		/// Calculates priority based on position relative to viewport.
		/// </summary>
		private int CalculatePriority(int itemIndex, int visibleStart, int visibleEnd)
		{
			var visibleCenter = (visibleStart + visibleEnd) / 2;
			var distance = Math.Abs(itemIndex - visibleCenter);
			var priority = 1000 - (int)(distance * PRIORITY_DECAY_DISTANCE);
			return Math.Max(0, priority);
		}

		/// <summary>
		/// Gets estimated item height based on layout mode.
		/// </summary>
		private double GetEstimatedItemHeight()
		{
			// Try to get actual item container
			if (_gridView?.Items.Count > 0)
			{
				var container = _gridView.ContainerFromIndex(0) as GridViewItem;
				if (container != null)
					return container.ActualHeight;
			}

			// Fallback to estimated size based on thumbnail size
			return _currentThumbnailSize + 60; // Thumbnail + padding + text
		}

		/// <summary>
		/// Gets estimated item width based on layout mode.
		/// </summary>
		private double GetEstimatedItemWidth()
		{
			// Try to get actual item container
			if (_gridView?.Items.Count > 0)
			{
				var container = _gridView.ContainerFromIndex(0) as GridViewItem;
				if (container != null)
					return container.ActualWidth;
			}

			// Fallback to estimated size
			return _currentThumbnailSize + 40; // Thumbnail + padding
		}

		/// <summary>
		/// Cancels thumbnail loading for a specific path.
		/// </summary>
		private void CancelThumbnailLoad(string path)
		{
			if (_loadingCancellations.TryRemove(path, out var cts))
			{
				cts.Cancel();
				cts.Dispose();
			}
			_thumbnailQueue.CancelRequest(path);
		}

		/// <summary>
		/// Cancels all pending thumbnail loads.
		/// </summary>
		private void CancelAllLoads()
		{
			foreach (var kvp in _loadingCancellations)
			{
				kvp.Value.Cancel();
				kvp.Value.Dispose();
			}
			_loadingCancellations.Clear();

			// Cancel all in queue
			_thumbnailQueue.CancelRequests(_ => true);
		}

		/// <summary>
		/// Handles collection changes to load new items.
		/// </summary>
		private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
		{
			if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
			{
				// Queue loading for new items if they're visible
				_ = LoadVisibleThumbnailsAsync();
			}
			else if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems != null)
			{
				// Cancel loading for removed items
				foreach (var item in e.OldItems)
				{
					if (item is ListedItem listedItem && !string.IsNullOrEmpty(listedItem.ItemPath))
					{
						CancelThumbnailLoad(listedItem.ItemPath);
					}
				}
			}
			else if (e.Action == NotifyCollectionChangedAction.Reset)
			{
				// Clear all and reload
				CancelAllLoads();
				_visibleItemPaths.Clear();
				_ = LoadVisibleThumbnailsAsync();
			}
		}

		/// <summary>
		/// Handles thumbnail loaded events.
		/// </summary>
		private async void OnThumbnailLoaded(object? sender, ThumbnailLoadedEventArgs e)
		{
			// Update UI on UI thread
			await _threadingService.ExecuteOnUiThreadAsync(() =>
			{
				// Clean up cancellation token
				_loadingCancellations.TryRemove(e.Path, out _);

				// Log successful load
				_logger.LogDebug("Thumbnail loaded for {Path}", e.Path);
			});
		}

		/// <summary>
		/// Handles progress changed events for diagnostics.
		/// </summary>
		private void OnProgressChanged(object? sender, ThumbnailQueueProgressEventArgs e)
		{
			_logger.LogDebug("Thumbnail queue: {QueueDepth} queued, {ActiveRequests} active, {ProcessedCount} processed, {AverageLoadTime:F2}ms avg",
				e.QueueDepth, e.ActiveRequests, e.ProcessedCount, e.AverageLoadTimeMs);
		}

		public void Dispose()
		{
			if (_disposed)
				return;

			_disposed = true;

			// Unsubscribe from events
			_thumbnailQueue.ThumbnailLoaded -= OnThumbnailLoaded;
			_thumbnailQueue.ProgressChanged -= OnProgressChanged;

			if (_scrollViewer != null)
			{
				_scrollViewer.ViewChanged -= OnScrollViewerViewChanged;
				_scrollViewer.ViewChanging -= OnScrollViewerViewChanging;
			}

			if (_itemsView is INotifyCollectionChanged notifyCollection)
			{
				notifyCollection.CollectionChanged -= OnItemsCollectionChanged;
			}

			// Cancel all pending loads
			CancelAllLoads();
		}
	}
}