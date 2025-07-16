// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Utils;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System.Collections.Concurrent;
using Windows.Foundation;

namespace Files.App.Helpers.UI
{
	/// <summary>
	/// Helper class to track visible items in a ScrollViewer for optimized thumbnail loading.
	/// </summary>
	public class ScrollViewerThumbnailHelper
	{
		private ScrollViewer? _scrollViewer;
		private ItemsControl? _itemsControl;
		private readonly Timer _visibilityCheckTimer;
		private readonly ConcurrentDictionary<string, DateTime> _lastVisibilityCheck = new();
		
		// Events
		public event EventHandler<VisibleItemsChangedEventArgs>? VisibleItemsChanged;
		public event EventHandler<ScrollDirectionChangedEventArgs>? ScrollDirectionChanged;
		
		// Configuration
		private const int VISIBILITY_CHECK_INTERVAL_MS = 100;
		private const int PRELOAD_ITEM_COUNT = 10;
		private const double VISIBILITY_THRESHOLD = 0.1; // 10% visible to count
		
		private double _lastVerticalOffset;
		private double _lastHorizontalOffset;
		private ScrollDirection _currentScrollDirection = ScrollDirection.None;
		
		public ScrollViewerThumbnailHelper()
		{
			_visibilityCheckTimer = new Timer(
				CheckVisibleItems,
				null,
				Timeout.Infinite,
				VISIBILITY_CHECK_INTERVAL_MS);
		}
		
		/// <summary>
		/// Attaches the helper to a ScrollViewer and ItemsControl.
		/// </summary>
		public void Attach(ScrollViewer scrollViewer, ItemsControl itemsControl)
		{
			Detach();
			
			_scrollViewer = scrollViewer;
			_itemsControl = itemsControl;
			
			if (_scrollViewer != null)
			{
				_scrollViewer.ViewChanged += OnViewChanged;
				_scrollViewer.ViewChanging += OnViewChanging;
				
				// Start periodic visibility checks
				_visibilityCheckTimer.Change(0, VISIBILITY_CHECK_INTERVAL_MS);
			}
		}
		
		/// <summary>
		/// Detaches the helper from the current ScrollViewer.
		/// </summary>
		public void Detach()
		{
			_visibilityCheckTimer.Change(Timeout.Infinite, Timeout.Infinite);
			
			if (_scrollViewer != null)
			{
				_scrollViewer.ViewChanged -= OnViewChanged;
				_scrollViewer.ViewChanging -= OnViewChanging;
			}
			
			_scrollViewer = null;
			_itemsControl = null;
			_lastVisibilityCheck.Clear();
		}
		
		private void OnViewChanging(object? sender, ScrollViewerViewChangingEventArgs e)
		{
			if (_scrollViewer == null)
				return;
			
			// Detect scroll direction
			var newVerticalOffset = e.NextView.VerticalOffset;
			var newHorizontalOffset = e.NextView.HorizontalOffset;
			
			var newDirection = ScrollDirection.None;
			
			if (Math.Abs(newVerticalOffset - _lastVerticalOffset) > 1)
			{
				newDirection = newVerticalOffset > _lastVerticalOffset 
					? ScrollDirection.Down 
					: ScrollDirection.Up;
			}
			else if (Math.Abs(newHorizontalOffset - _lastHorizontalOffset) > 1)
			{
				newDirection = newHorizontalOffset > _lastHorizontalOffset 
					? ScrollDirection.Right 
					: ScrollDirection.Left;
			}
			
			if (newDirection != _currentScrollDirection)
			{
				_currentScrollDirection = newDirection;
				ScrollDirectionChanged?.Invoke(this, new ScrollDirectionChangedEventArgs 
				{ 
					Direction = newDirection 
				});
			}
			
			_lastVerticalOffset = newVerticalOffset;
			_lastHorizontalOffset = newHorizontalOffset;
		}
		
		private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
		{
			if (!e.IsIntermediate)
			{
				// Scroll completed - do a final visibility check
				CheckVisibleItems(null);
			}
		}
		
		private void CheckVisibleItems(object? state)
		{
			if (_scrollViewer == null || _itemsControl == null)
				return;
			
			try
			{
				var visibleItems = new List<ListedItem>();
				var hiddenItems = new List<ListedItem>();
				var itemsToPreload = new List<ListedItem>();
				
				// Get viewport bounds
				var viewportBounds = new Windows.Foundation.Rect(0, 0, _scrollViewer.ActualWidth, _scrollViewer.ActualHeight);
				var scrollViewerTransform = _scrollViewer.TransformToVisual(null);
				var scrollViewerBounds = scrollViewerTransform.TransformBounds(viewportBounds);
				
				// Check each item container
				if (_itemsControl.ItemsPanelRoot != null)
				{
					var itemCount = _itemsControl.Items.Count;
					var checkedCount = 0;
					
					for (int i = 0; i < itemCount; i++)
					{
						var item = _itemsControl.Items[i] as ListedItem;
						if (item == null)
							continue;
						
						var container = _itemsControl.ContainerFromItem(item) as FrameworkElement;
						if (container == null)
							continue;
						
						checkedCount++;
						
						// Get item bounds relative to viewport
						var itemTransform = container.TransformToVisual(_scrollViewer);
						var itemBounds = itemTransform.TransformBounds(
							new Windows.Foundation.Rect(0, 0, container.ActualWidth, container.ActualHeight));
						
						// Check visibility
						var isVisible = IsItemVisible(itemBounds, viewportBounds);
						var wasVisible = _lastVisibilityCheck.ContainsKey(item.ItemPath);
						
						if (isVisible)
						{
							visibleItems.Add(item);
							_lastVisibilityCheck[item.ItemPath] = DateTime.UtcNow;
						}
						else if (wasVisible)
						{
							hiddenItems.Add(item);
							_lastVisibilityCheck.TryRemove(item.ItemPath, out _);
						}
						
						// Check if item should be preloaded based on scroll direction
						if (!isVisible && ShouldPreloadItem(i, itemBounds, viewportBounds))
						{
							itemsToPreload.Add(item);
						}
					}
				}
				
				// Raise event if there are changes
				if (visibleItems.Count > 0 || hiddenItems.Count > 0 || itemsToPreload.Count > 0)
				{
					VisibleItemsChanged?.Invoke(this, new VisibleItemsChangedEventArgs
					{
						VisibleItems = visibleItems,
						HiddenItems = hiddenItems,
						ItemsToPreload = itemsToPreload,
						ScrollDirection = _currentScrollDirection
					});
				}
			}
			catch (Exception ex)
			{
				// Log error but don't crash
				System.Diagnostics.Debug.WriteLine($"Error checking visible items: {ex.Message}");
			}
		}
		
		private bool IsItemVisible(Windows.Foundation.Rect itemBounds, Windows.Foundation.Rect viewportBounds)
		{
			// Calculate intersection
			var intersectionWidth = Math.Max(0, 
				Math.Min(itemBounds.Right, viewportBounds.Right) - 
				Math.Max(itemBounds.Left, viewportBounds.Left));
			
			var intersectionHeight = Math.Max(0, 
				Math.Min(itemBounds.Bottom, viewportBounds.Bottom) - 
				Math.Max(itemBounds.Top, viewportBounds.Top));
			
			// Check if enough of the item is visible
			var visibleArea = intersectionWidth * intersectionHeight;
			var itemArea = itemBounds.Width * itemBounds.Height;
			
			return visibleArea > 0 && (visibleArea / itemArea) >= VISIBILITY_THRESHOLD;
		}
		
		private bool ShouldPreloadItem(int itemIndex, Windows.Foundation.Rect itemBounds, Windows.Foundation.Rect viewportBounds)
		{
			// Preload items just outside the viewport in the scroll direction
			const double preloadDistance = 200; // pixels
			
			return _currentScrollDirection switch
			{
				ScrollDirection.Down => itemBounds.Top < viewportBounds.Bottom + preloadDistance &&
										itemBounds.Top > viewportBounds.Bottom,
				
				ScrollDirection.Up => itemBounds.Bottom > viewportBounds.Top - preloadDistance &&
									  itemBounds.Bottom < viewportBounds.Top,
				
				ScrollDirection.Right => itemBounds.Left < viewportBounds.Right + preloadDistance &&
										 itemBounds.Left > viewportBounds.Right,
				
				ScrollDirection.Left => itemBounds.Right > viewportBounds.Left - preloadDistance &&
										itemBounds.Right < viewportBounds.Left,
				
				_ => false
			};
		}
		
		/// <summary>
		/// Forces an immediate visibility check.
		/// </summary>
		public void ForceVisibilityCheck()
		{
			CheckVisibleItems(null);
		}
		
		public void Dispose()
		{
			Detach();
			_visibilityCheckTimer?.Dispose();
		}
	}
	
	public class VisibleItemsChangedEventArgs : EventArgs
	{
		public required IReadOnlyList<ListedItem> VisibleItems { get; init; }
		public required IReadOnlyList<ListedItem> HiddenItems { get; init; }
		public required IReadOnlyList<ListedItem> ItemsToPreload { get; init; }
		public required ScrollDirection ScrollDirection { get; init; }
	}
	
	public class ScrollDirectionChangedEventArgs : EventArgs
	{
		public required ScrollDirection Direction { get; init; }
	}
	
	public enum ScrollDirection
	{
		None,
		Up,
		Down,
		Left,
		Right
	}
}