# Optimized File System Watcher

This directory contains an optimized file system watcher implementation for the Files app that provides better performance through event batching and normalization.

## Features

1. **Event Batching**: Aggregates file system events with a configurable delay (default 100ms) to reduce UI update frequency
2. **Smart Event Normalization**: Intelligently combines related events:
   - CREATE + DELETE → Ignored (file was created and immediately deleted)
   - DELETE + CREATE → CHANGE (file was replaced)
   - Multiple CHANGE events → Keep only the latest
3. **Thread-Safe Queue**: Uses BlockingCollection for safe multi-threaded event processing
4. **Background Processing**: Events are processed on a dedicated background thread
5. **Dynamic Buffer Sizing**: Automatically adjusts buffer size based on path type (network vs local)
6. **Performance Statistics**: Tracks events received, batches processed, and normalization metrics

## Components

### OptimizedFileSystemWatcher
The main watcher implementation that replaces the standard FileSystemWatcher with enhanced capabilities.

### IOptimizedFileSystemWatcher
Interface defining the contract for the optimized watcher, including:
- Batched change events
- Error handling
- Configurable batch delay
- Performance statistics

### FileSystemWatcherService
A service that manages multiple watchers and integrates with the Files app architecture.

## Usage Example

```csharp
// Create a watcher for a directory
var watcher = new OptimizedFileSystemWatcher(@"C:\Users\Documents");

// Configure batch delay
watcher.BatchDelay = TimeSpan.FromMilliseconds(200);

// Subscribe to batched changes
watcher.BatchedChanges += (sender, e) =>
{
    Console.WriteLine($"Received batch with {e.TotalCount} events");
    Console.WriteLine($"Created: {e.CreatedCount}, Deleted: {e.DeletedCount}");
    Console.WriteLine($"Changed: {e.ChangedCount}, Renamed: {e.RenamedCount}");
};

// Start watching
watcher.StartWatcher();

// Get statistics
var stats = watcher.GetStatistics();
Console.WriteLine($"Events per second: {stats.EventsPerSecond}");
Console.WriteLine($"Average events per batch: {stats.AverageEventsPerBatch}");
```

## Integration with Files App

To integrate this optimized watcher with the existing Files app:

1. Replace the FileSystemWatcher in ShellViewModel with OptimizedFileSystemWatcher
2. Update the event handlers to process batched events instead of individual events
3. Register FileSystemWatcherService in the dependency injection container
4. Use the service to manage watchers across different views

### Example Integration in ShellViewModel

```csharp
private IOptimizedFileSystemWatcher _optimizedWatcher;

private void WatchForDirectoryChanges(string folderPath)
{
    // Stop existing watcher
    _optimizedWatcher?.StopWatcher();
    _optimizedWatcher?.Dispose();

    if (string.IsNullOrWhiteSpace(folderPath))
        return;

    // Create new optimized watcher
    _optimizedWatcher = new OptimizedFileSystemWatcher(folderPath);
    _optimizedWatcher.BatchedChanges += OnBatchedDirectoryChanges;
    _optimizedWatcher.StartWatcher();
}

private async void OnBatchedDirectoryChanges(object sender, BatchedFileSystemEventArgs e)
{
    Debug.WriteLine($"Directory watcher batch: {e.TotalCount} events");
    
    await dispatcherQueue.EnqueueOrInvokeAsync(() =>
    {
        // Process only if we have a reasonable number of changes
        if (e.TotalCount < 50)
        {
            // Process individual changes
            RefreshItems(null);
        }
        else
        {
            // Too many changes, do a full refresh
            RefreshItems(null);
        }
    });
}
```

## Performance Benefits

1. **Reduced UI Updates**: Batching events reduces the frequency of UI refreshes
2. **Lower CPU Usage**: Event normalization eliminates redundant processing
3. **Better Responsiveness**: Background processing keeps the UI thread responsive
4. **Network Optimization**: Larger buffers for network paths reduce the chance of missing events
5. **Scalability**: Can handle rapid file system changes without overwhelming the application

## Configuration

The watcher can be configured through:
- `BatchDelay`: Time to wait before processing a batch (default: 100ms)
- `IncludeSubdirectories`: Whether to monitor subdirectories
- Buffer sizes are automatically adjusted based on path type

## Error Handling

The watcher includes robust error handling:
- Automatic restart on errors
- Error events for logging
- Graceful shutdown with cancellation support