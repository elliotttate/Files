// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.Mvvm.DependencyInjection;
using Files.App.Data.Contracts;
using Files.App.Data.Items;
using Files.App.Extensions;
using Files.App.Services.Caching;
using Files.App.Utils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Files.App.Services.Thumbnails
{
	/// <summary>
	/// Implements viewport-based thumbnail loading to optimize performance
	/// </summary>
	public sealed class ViewportThumbnailLoaderService : IViewportThumbnailLoaderService, IDisposable
	{
		private readonly IFileModelCacheService _cacheService;
		private readonly ILogger<ViewportThumbnailLoaderService> _logger;
		private readonly ConcurrentDictionary<string, ListedItem> _viewportItems = new(StringComparer.OrdinalIgnoreCase);
		private readonly ConcurrentDictionary<string, CancellationTokenSource> _loadingTasks = new(StringComparer.OrdinalIgnoreCase);
		private readonly SemaphoreSlim _updateSemaphore = new(1, 1);
		private CancellationTokenSource _viewportCancellationTokenSource = new();
		
		// Constants
		private const int MAX_CONCURRENT_LOADS = 8;
		private const int VIEWPORT_UPDATE_DELAY_MS = 100; // Delay to batch viewport updates
		private const int PRELOAD_BUFFER_SIZE = 10; // Number of items to preload near viewport
		
		private Timer _viewportUpdateTimer;
		private List<(IEnumerable<ListedItem> items, uint size, CancellationToken token)> _pendingUpdates = new();
		private readonly object _pendingUpdatesLock = new();
		
		public int ActiveLoadCount => _loadingTasks.Count;
		public bool IsLoading => _loadingTasks.Any();

		public ViewportThumbnailLoaderService()
		{
			_cacheService = Ioc.Default.GetService<IFileModelCacheService>();
			_logger = Ioc.Default.GetService<ILogger<ViewportThumbnailLoaderService>>();
			
			// Initialize viewport update timer
			_viewportUpdateTimer = new Timer(ProcessPendingViewportUpdates, null, Timeout.Infinite, Timeout.Infinite);
		}

		public async Task UpdateViewportAsync(IEnumerable<ListedItem> visibleItems, uint thumbnailSize, CancellationToken cancellationToken = default)
		{
			if (visibleItems == null)
				return;

			// Add to pending updates to batch rapid viewport changes
			lock (_pendingUpdatesLock)
			{
				_pendingUpdates.Add((visibleItems, thumbnailSize, cancellationToken));
			}

			// Reset timer to process updates after delay
			_viewportUpdateTimer.Change(VIEWPORT_UPDATE_DELAY_MS, Timeout.Infinite);
		}

		private async void ProcessPendingViewportUpdates(object state)
		{
			List<(IEnumerable<ListedItem> items, uint size, CancellationToken token)> updates;
			
			lock (_pendingUpdatesLock)
			{
				if (_pendingUpdates.Count == 0)
					return;
					
				updates = new List<(IEnumerable<ListedItem>, uint, CancellationToken)>(_pendingUpdates);
				_pendingUpdates.Clear();
			}

			// Process only the latest update
			var latestUpdate = updates.Last();
			await ProcessViewportUpdateAsync(latestUpdate.items, latestUpdate.size, latestUpdate.token);
		}

		private async Task ProcessViewportUpdateAsync(IEnumerable<ListedItem> visibleItems, uint thumbnailSize, CancellationToken cancellationToken)
		{
			await _updateSemaphore.WaitAsync(cancellationToken);
			try
			{
				// Cancel previous viewport loads
				_viewportCancellationTokenSource.Cancel();
				_viewportCancellationTokenSource = new CancellationTokenSource();
				
				var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(
					cancellationToken, 
					_viewportCancellationTokenSource.Token).Token;

				// Get current viewport items
				var currentItems = visibleItems.Where(item => item != null && !string.IsNullOrEmpty(item.ItemPath)).ToList();
				var currentPaths = new HashSet<string>(currentItems.Select(i => i.ItemPath), StringComparer.OrdinalIgnoreCase);

				// Remove items no longer in viewport
				var toRemove = _viewportItems.Keys.Where(path => !currentPaths.Contains(path)).ToList();
				foreach (var path in toRemove)
				{
					if (_viewportItems.TryRemove(path, out _) && 
						_loadingTasks.TryRemove(path, out var cts))
					{
						cts.Cancel();
						cts.Dispose();
					}
				}

				// Add new items to viewport
				var newItems = currentItems.Where(item => !_viewportItems.ContainsKey(item.ItemPath)).ToList();
				foreach (var item in newItems)
				{
					_viewportItems.TryAdd(item.ItemPath, item);
				}

				// Load thumbnails for visible items
				await LoadThumbnailsAsync(currentItems, thumbnailSize, linkedToken, isPriority: true);
			}
			catch (OperationCanceledException)
			{
				_logger?.LogDebug("Viewport update cancelled");
			}
			catch (Exception ex)
			{
				_logger?.LogError(ex, "Error updating viewport");
			}
			finally
			{
				_updateSemaphore.Release();
			}
		}

		public async Task PreloadNearViewportAsync(IEnumerable<ListedItem> itemsNearViewport, uint thumbnailSize, CancellationToken cancellationToken = default)
		{
			if (itemsNearViewport == null || !itemsNearViewport.Any())
				return;

			try
			{
				var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(
					cancellationToken, 
					_viewportCancellationTokenSource.Token).Token;

				// Take limited number of items to preload
				var itemsToPreload = itemsNearViewport
					.Where(item => item != null && !string.IsNullOrEmpty(item.ItemPath))
					.Take(PRELOAD_BUFFER_SIZE)
					.ToList();

				// Load thumbnails with lower priority
				await LoadThumbnailsAsync(itemsToPreload, thumbnailSize, linkedToken, isPriority: false);
			}
			catch (OperationCanceledException)
			{
				_logger?.LogDebug("Preload cancelled");
			}
			catch (Exception ex)
			{
				_logger?.LogError(ex, "Error preloading thumbnails");
			}
		}

		private async Task LoadThumbnailsAsync(IEnumerable<ListedItem> items, uint thumbnailSize, CancellationToken cancellationToken, bool isPriority)
		{
			if (_cacheService == null)
				return;

			var semaphore = new SemaphoreSlim(MAX_CONCURRENT_LOADS, MAX_CONCURRENT_LOADS);
			var loadTasks = new List<Task>();

			foreach (var item in items)
			{
				if (cancellationToken.IsCancellationRequested)
					break;

				// Skip if already loaded or loading
				if (item.FileImage != null || _loadingTasks.ContainsKey(item.ItemPath))
					continue;

				// Check cache first
				var cached = _cacheService.GetCachedThumbnail(item.ItemPath);
				if (cached != null)
				{
					item.FileImage = cached;
					continue;
				}

				await semaphore.WaitAsync(cancellationToken);
				
				var loadTask = Task.Run(async () =>
				{
					var cts = new CancellationTokenSource();
					var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);
					
					try
					{
						if (_loadingTasks.TryAdd(item.ItemPath, cts))
						{
							await item.LoadThumbnailAsync(thumbnailSize, linkedCts.Token);
						}
					}
					catch (Exception ex)
					{
						_logger?.LogDebug(ex, "Failed to load thumbnail for {Path}", item.ItemPath);
					}
					finally
					{
						_loadingTasks.TryRemove(item.ItemPath, out _);
						linkedCts.Dispose();
						cts.Dispose();
						semaphore.Release();
					}
				}, cancellationToken);

				loadTasks.Add(loadTask);
			}

			// Wait for all loads to complete
			await Task.WhenAll(loadTasks);
			semaphore.Dispose();
		}

		public void ClearViewport()
		{
			// Cancel all loading tasks
			_viewportCancellationTokenSource.Cancel();
			
			foreach (var kvp in _loadingTasks)
			{
				kvp.Value.Cancel();
				kvp.Value.Dispose();
			}
			
			_loadingTasks.Clear();
			_viewportItems.Clear();
			
			lock (_pendingUpdatesLock)
			{
				_pendingUpdates.Clear();
			}
		}

		public void Dispose()
		{
			_viewportUpdateTimer?.Dispose();
			_updateSemaphore?.Dispose();
			ClearViewport();
			_viewportCancellationTokenSource?.Dispose();
		}
	}
}