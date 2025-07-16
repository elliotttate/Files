# Thumbnail Loading Queue Integration Example

This example demonstrates how to integrate the `ThumbnailLoadingQueue` with the Files app's GridView layout for optimized thumbnail loading with viewport awareness and predictive loading.

## Overview

The integration consists of three main components:

1. **ThumbnailLoadingQueue** - The core service that manages thumbnail loading with priorities
2. **OptimizedGridLayoutViewModel** - A view model that tracks viewport changes and manages loading
3. **GridLayoutPage** - The UI layer that displays items with thumbnails

## Key Features

- **Viewport-based Priority Loading**: Items closer to the viewport center get higher priority
- **Predictive Loading**: Pre-loads thumbnails in the scroll direction
- **Cancellation Management**: Cancels loads for items that scroll out of view
- **Batch Processing**: Groups requests for efficient processing
- **Memory Efficiency**: Integrates with the file model cache service

## Implementation Steps

### 1. Service Registration

Register the thumbnail loading queue in your dependency injection container:

```csharp
// In App.xaml.cs or your DI configuration
services.AddSingleton<IThumbnailLoadingQueue, ThumbnailLoadingQueue>();
```

### 2. View Model Integration

In your GridLayoutPage, initialize the OptimizedGridLayoutViewModel:

```csharp
public partial class GridLayoutPage : BaseGroupableLayoutPage
{
    private OptimizedGridLayoutViewModel? _thumbnailViewModel;

    protected override void OnNavigatedTo(NavigationEventArgs eventArgs)
    {
        base.OnNavigatedTo(eventArgs);
        
        // Initialize thumbnail optimization
        _thumbnailViewModel = new OptimizedGridLayoutViewModel();
        
        // Get icon size for current layout mode
        uint thumbnailSize = LayoutSizeKindHelper.GetIconSize(FolderSettings.LayoutMode);
        
        // Initialize with GridView and items
        if (FileList != null && ParentShellPageInstance?.ShellViewModel?.FilesAndFolders != null)
        {
            _thumbnailViewModel.Initialize(
                FileList, 
                ParentShellPageInstance.ShellViewModel.FilesAndFolders.View,
                thumbnailSize);
        }
    }
    
    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        base.OnNavigatingFrom(e);
        
        // Clean up
        _thumbnailViewModel?.Dispose();
        _thumbnailViewModel = null;
    }
}
```

### 3. Handle Layout Changes

Update thumbnail size when the user changes the view size:

```csharp
private async void LayoutSettingsService_PropertyChanged(object? sender, PropertyChangedEventArgs e)
{
    // Existing code...
    
    if (e.PropertyName == nameof(ILayoutSettingsService.GridViewSize))
    {
        if (_thumbnailViewModel != null)
        {
            uint newSize = LayoutSizeKindHelper.GetIconSize(FolderSettings.LayoutMode);
            await _thumbnailViewModel.UpdateThumbnailSizeAsync(newSize);
        }
    }
}
```

### 4. XAML Configuration

Configure the GridView for optimal performance:

```xml
<GridView x:Name="FileList"
          VirtualizingStackPanel.VirtualizationMode="Recycling"
          ScrollViewer.VerticalScrollBarVisibility="Auto"
          IncrementalLoadingTrigger="Edge"
          DataFetchSize="2.0">
    <GridView.ItemsPanel>
        <ItemsPanelTemplate>
            <ItemsWrapGrid Orientation="Horizontal"
                           CacheLength="2" />
        </ItemsPanelTemplate>
    </GridView.ItemsPanel>
</GridView>
```

### 5. Item Template with Loading States

Create item templates that show loading progress:

```xml
<DataTemplate x:Key="GridViewBrowserTemplate">
    <Grid Width="{Binding ItemWidthGridView}" 
          Height="Auto">
        <!-- Placeholder while loading -->
        <Border x:Name="LoadingPlaceholder"
                Background="{ThemeResource CardBackgroundFillColorDefaultBrush}"
                CornerRadius="4"
                Visibility="{x:Bind ShowLoadingPlaceholder, Mode=OneWay}">
            <ProgressRing IsActive="True" Width="20" Height="20" />
        </Border>
        
        <!-- Thumbnail image -->
        <Image Source="{x:Bind CustomIcon, Mode=OneWay}"
               Stretch="Uniform"
               Opacity="0">
            <interactivity:Interaction.Behaviors>
                <behaviors:FadeInBehavior Duration="200" />
            </interactivity:Interaction.Behaviors>
        </Image>
        
        <!-- File name and details -->
        <TextBlock Text="{x:Bind Name}"
                   VerticalAlignment="Bottom"
                   Margin="8,0,8,4" />
    </Grid>
</DataTemplate>
```

## How It Works

### Viewport Tracking

The `OptimizedGridLayoutViewModel` monitors the ScrollViewer to track:
- Current viewport bounds
- Scroll direction and velocity
- Visible item range

```csharp
private void OnScrollViewerViewChanged(ScrollViewer sender, ScrollViewerViewChangedEventArgs e)
{
    _currentViewport = new Rect(
        sender.HorizontalOffset,
        sender.VerticalOffset,
        sender.ViewportWidth,
        sender.ViewportHeight);
    
    if (!e.IsIntermediate)
    {
        _ = LoadVisibleThumbnailsAsync();
    }
}
```

### Priority Calculation

Items are prioritized based on their distance from the viewport center:

```csharp
private int CalculatePriority(int itemIndex, int visibleStart, int visibleEnd)
{
    var visibleCenter = (visibleStart + visibleEnd) / 2;
    var distance = Math.Abs(itemIndex - visibleCenter);
    var priority = 1000 - (int)(distance * PRIORITY_DECAY_DISTANCE);
    return Math.Max(0, priority);
}
```

### Predictive Loading

When scrolling quickly, the system pre-loads thumbnails in the scroll direction:

```csharp
if (velocity > SCROLL_VELOCITY_THRESHOLD)
{
    // Load additional items in scroll direction
    var predictiveRange = _isScrollingDown 
        ? GetItemsBelow(currentViewport) 
        : GetItemsAbove(currentViewport);
    
    await QueuePredictiveLoads(predictiveRange);
}
```

### Cancellation Management

When items scroll out of view, their thumbnail loads are cancelled:

```csharp
var pathsToCancel = _visibleItemPaths.Except(newVisiblePaths);
foreach (var path in pathsToCancel)
{
    _thumbnailQueue.CancelRequest(path);
}
```

## Performance Considerations

1. **Batch Processing**: Requests are processed in batches of 10-20 items
2. **Concurrent Loading**: Up to 8 thumbnails load simultaneously
3. **Memory Management**: Integrates with FileModelCacheService for caching
4. **UI Thread Safety**: All UI updates happen on the UI thread

## Configuration Options

You can adjust these constants in `OptimizedGridLayoutViewModel`:

```csharp
// Number of rows to load beyond viewport
private const int VIEWPORT_BUFFER_ROWS = 2;

// Items to load predictively when scrolling fast  
private const int PREDICTIVE_LOAD_ITEMS = 20;

// Batch size for queue processing
private const int BATCH_LOAD_SIZE = 10;

// Velocity threshold for predictive loading (px/s)
private const int SCROLL_VELOCITY_THRESHOLD = 500;
```

## Debugging

Enable debug logging to monitor performance:

```csharp
_thumbnailQueue.ProgressChanged += (s, e) =>
{
    Debug.WriteLine($"Queue: {e.QueueDepth} pending, " +
                   $"{e.ActiveRequests} active, " +
                   $"{e.ProcessedCount} processed, " +
                   $"{e.AverageLoadTimeMs:F2}ms avg");
};
```

## Best Practices

1. **Dispose Properly**: Always dispose the view model when navigating away
2. **Handle Errors**: Implement fallback icons for failed loads
3. **Test Performance**: Monitor with different folder sizes and scroll speeds
4. **Adjust Priorities**: Fine-tune priority calculation for your use case
5. **Cache Warmup**: Consider pre-loading thumbnails for common folders

## Example Usage Flow

1. User opens a folder with 1000 images
2. GridView displays items with placeholder icons
3. OptimizedGridLayoutViewModel calculates visible range (e.g., items 0-50)
4. Thumbnails for items 0-50 are queued with priorities (center items highest)
5. User scrolls down quickly
6. System detects fast downward scroll
7. Cancels loads for items 0-20 that scrolled out
8. Queues items 51-70 (visible) with high priority
9. Queues items 71-90 (predictive) with lower priority
10. Thumbnails load and fade in as they complete

This approach ensures smooth scrolling performance while efficiently loading thumbnails based on what the user is likely to see.