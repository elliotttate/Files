// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.Mvvm.DependencyInjection;
using Files.App.Services.Caching;
using Files.App.Services.Thumbnails;
using Files.App.Utils;
using Files.App.Data.Enums;
using Microsoft.UI.Xaml.Data;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using System.IO;

namespace Files.App.ViewModels.Layouts
{
	/// <summary>
	/// Example view model demonstrating integration with the optimized ThumbnailLoadingQueue service.
	/// This class shows how to efficiently load thumbnails for visible items in a virtualized list.
	/// </summary>
	public class ThumbnailOptimizedLayoutViewModel : IDisposable
	{
		// Services
		private readonly IThumbnailLoadingQueue _thumbnailQueue;
		private readonly IFileModelCacheService _cacheService;
		
		// Fields
		private readonly CancellationTokenSource _lifecycleCancellationTokenSource = new();
		private readonly Dictionary<string, CancellationTokenSource> _itemCancellationTokens = new();
		private readonly HashSet<string> _visibleItemPaths = new();
		private readonly object _visibleItemsLock = new();
		
		// Constants
		private const int HIGH_PRIORITY = 100;
		private const int MEDIUM_PRIORITY = 50;
		private const int LOW_PRIORITY = 10;
		private const uint DEFAULT_THUMBNAIL_SIZE = 96;
		
		public ThumbnailOptimizedLayoutViewModel()
		{
			_thumbnailQueue = Ioc.Default.GetRequiredService<IThumbnailLoadingQueue>();
			_cacheService = Ioc.Default.GetRequiredService<IFileModelCacheService>();
			
			// Subscribe to events
			_thumbnailQueue.ThumbnailLoaded += OnThumbnailLoaded;
			_thumbnailQueue.BatchCompleted += OnBatchCompleted;
			_thumbnailQueue.ProgressChanged += OnProgressChanged;
		}
		
		/// <summary>
		/// Called when items become visible in the viewport.
		/// This method queues high-priority thumbnail loads for visible items.
		/// </summary>
		public async Task OnItemsVisibleAsync(IEnumerable<ListedItem> visibleItems)
		{
			var requests = new List<ThumbnailRequest>();
			
			lock (_visibleItemsLock)
			{
				_visibleItemPaths.Clear();
				
				foreach (var item in visibleItems)
				{
					if (string.IsNullOrEmpty(item.ItemPath))
						continue;
						
					_visibleItemPaths.Add(item.ItemPath);
					
					// Skip if thumbnail already loaded
					if (item.FileImage != null || _cacheService.GetCachedThumbnail(item.ItemPath) != null)
						continue;
					
					// Create high-priority request for visible item
					requests.Add(new ThumbnailRequest
					{
						Path = item.ItemPath,
						Item = item,
						ThumbnailSize = GetThumbnailSizeForItem(item),
						Priority = HIGH_PRIORITY,
						IconOptions = IconOptions.UseCurrentScale
					});
					
					// Cancel any existing low-priority request
					CancelItemRequest(item.ItemPath);
				}
			}
			
			// Queue batch request for all visible items
			if (requests.Count > 0)
			{
				try
				{
					await _thumbnailQueue.QueueBatchRequestAsync(requests, _lifecycleCancellationTokenSource.Token);
				}
				catch (OperationCanceledException)
				{
					// Expected when view model is disposed
				}
			}
		}
		
		/// <summary>
		/// Called when items are no longer visible (scrolled out of viewport).
		/// This method cancels or reduces priority of thumbnail loads for hidden items.
		/// </summary>
		public void OnItemsHidden(IEnumerable<ListedItem> hiddenItems)
		{
			lock (_visibleItemsLock)
			{
				foreach (var item in hiddenItems)
				{
					if (string.IsNullOrEmpty(item.ItemPath))
						continue;
						
					_visibleItemPaths.Remove(item.ItemPath);
					
					// If thumbnail not yet loaded, reduce priority or cancel
					if (item.FileImage == null)
					{
						// Try to reduce priority first
						if (!_thumbnailQueue.UpdateRequestPriority(item.ItemPath, LOW_PRIORITY))
						{
							// If that fails, cancel the request
							CancelItemRequest(item.ItemPath);
						}
					}
				}
			}
		}
		
		/// <summary>
		/// Preloads thumbnails for items that are likely to become visible soon.
		/// This is useful for predictive loading based on scroll direction.
		/// </summary>
		public async Task PreloadThumbnailsAsync(IEnumerable<ListedItem> itemsToPreload, int scrollDirection)
		{
			var requests = new List<ThumbnailRequest>();
			
			foreach (var item in itemsToPreload)
			{
				if (string.IsNullOrEmpty(item.ItemPath))
					continue;
					
				// Skip if already loaded or queued
				if (item.FileImage != null || 
					_cacheService.GetCachedThumbnail(item.ItemPath) != null ||
					_itemCancellationTokens.ContainsKey(item.ItemPath))
					continue;
				
				// Use medium priority for preloaded items
				requests.Add(new ThumbnailRequest
				{
					Path = item.ItemPath,
					Item = item,
					ThumbnailSize = GetThumbnailSizeForItem(item),
					Priority = MEDIUM_PRIORITY,
					IconOptions = IconOptions.UseCurrentScale
				});
			}
			
			if (requests.Count > 0)
			{
				try
				{
					// Create cancellation token for this batch
					var cts = new CancellationTokenSource();
					var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
						cts.Token, 
						_lifecycleCancellationTokenSource.Token);
					
					// Track cancellation tokens
					foreach (var request in requests)
					{
						_itemCancellationTokens[request.Path] = cts;
					}
					
					await _thumbnailQueue.QueueBatchRequestAsync(requests, linkedCts.Token);
				}
				catch (OperationCanceledException)
				{
					// Expected when cancelled
				}
			}
		}
		
		/// <summary>
		/// Loads a single thumbnail with custom priority.
		/// Useful for special cases like hover preview or selection.
		/// </summary>
		public async Task LoadThumbnailAsync(ListedItem item, int priority = MEDIUM_PRIORITY)
		{
			if (string.IsNullOrEmpty(item.ItemPath) || item.FileImage != null)
				return;
				
			// Check cache first
			var cached = _cacheService.GetCachedThumbnail(item.ItemPath);
			if (cached != null)
			{
				item.FileImage = cached;
				item.LoadFileIcon = true;
				item.NeedsPlaceholderGlyph = false;
				return;
			}
			
			try
			{
				var cts = new CancellationTokenSource();
				var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
					cts.Token, 
					_lifecycleCancellationTokenSource.Token);
				
				_itemCancellationTokens[item.ItemPath] = cts;
				
				var result = await _thumbnailQueue.QueueThumbnailRequestAsync(
					item.ItemPath,
					item,
					GetThumbnailSizeForItem(item),
					priority,
					linkedCts.Token);
				
				if (result.Success && result.Thumbnail != null)
				{
					// Thumbnail is automatically set by the queue service
					// via cache service integration
				}
			}
			catch (OperationCanceledException)
			{
				// Expected when cancelled
			}
			finally
			{
				_itemCancellationTokens.Remove(item.ItemPath);
			}
		}
		
		/// <summary>
		/// Cancels all pending thumbnail loads.
		/// Useful when navigating away or changing folders.
		/// </summary>
		public void CancelAllPendingLoads()
		{
			// Cancel all item-specific requests
			foreach (var kvp in _itemCancellationTokens)
			{
				kvp.Value.Cancel();
				kvp.Value.Dispose();
			}
			_itemCancellationTokens.Clear();
			
			// Clear visible items tracking
			lock (_visibleItemsLock)
			{
				_visibleItemPaths.Clear();
			}
		}
		
		private void CancelItemRequest(string path)
		{
			if (_itemCancellationTokens.TryGetValue(path, out var cts))
			{
				cts.Cancel();
				cts.Dispose();
				_itemCancellationTokens.Remove(path);
			}
			
			_thumbnailQueue.CancelRequest(path);
		}
		
		private uint GetThumbnailSizeForItem(ListedItem item)
		{
			// Customize thumbnail size based on layout mode or item type
			// This is just an example - adjust based on your needs
			if (item.IsFolder)
				return 64;
			
			var extension = Path.GetExtension(item.ItemPath)?.ToLowerInvariant();
			return extension switch
			{
				".jpg" or ".jpeg" or ".png" or ".gif" => 128, // Larger for images
				".mp4" or ".avi" or ".mkv" => 96, // Medium for videos
				_ => DEFAULT_THUMBNAIL_SIZE
			};
		}
		
		private void OnThumbnailLoaded(object? sender, ThumbnailLoadedEventArgs e)
		{
			// Update any UI-specific state if needed
			// The ListedItem is automatically updated by the cache service
			
			// Clean up cancellation token
			_itemCancellationTokens.Remove(e.Path);
		}
		
		private void OnBatchCompleted(object? sender, BatchThumbnailLoadedEventArgs e)
		{
			// Log batch performance for optimization
			System.Diagnostics.Debug.WriteLine(
				$"Thumbnail batch completed: {e.SuccessfulLoads}/{e.TotalRequests} " +
				$"in {e.TotalLoadTime.TotalMilliseconds:F2}ms");
		}
		
		private void OnProgressChanged(object? sender, ThumbnailQueueProgressEventArgs e)
		{
			// Update progress UI if needed
			System.Diagnostics.Debug.WriteLine(
				$"Thumbnail queue: {e.QueueDepth} pending, {e.ActiveRequests} active, " +
				$"avg {e.AverageLoadTimeMs:F2}ms");
		}
		
		public void Dispose()
		{
			// Unsubscribe from events
			_thumbnailQueue.ThumbnailLoaded -= OnThumbnailLoaded;
			_thumbnailQueue.BatchCompleted -= OnBatchCompleted;
			_thumbnailQueue.ProgressChanged -= OnProgressChanged;
			
			// Cancel all operations
			_lifecycleCancellationTokenSource.Cancel();
			CancelAllPendingLoads();
			
			// Cleanup
			_lifecycleCancellationTokenSource.Dispose();
		}
	}
}