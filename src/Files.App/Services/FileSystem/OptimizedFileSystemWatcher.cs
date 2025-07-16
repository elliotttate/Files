// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Files.Core.Storage.Contracts;
using Files.Shared.Extensions;

namespace Files.App.Services.FileSystem
{
	/// <summary>
	/// An optimized file system watcher that implements event aggregation and batching
	/// to reduce the frequency of UI updates and improve performance.
	/// </summary>
	public sealed class OptimizedFileSystemWatcher : IOptimizedFileSystemWatcher, IDisposable
	{
		private readonly string _watchPath;
		private TimeSpan _batchDelay = TimeSpan.FromMilliseconds(100);
		private readonly BlockingCollection<FileSystemEventInfo> _eventQueue = new();
		private readonly Dictionary<string, FileSystemEventInfo> _pendingEvents = new();
		private readonly object _pendingEventsLock = new();
		private readonly FileSystemWatcherStatistics _statistics = new();
		
		private FileSystemWatcher _watcher;
		private Task _processingTask;
		private CancellationTokenSource _cancellationTokenSource;
		private Timer _batchTimer;
		private bool _isDisposed;
		private bool _includeSubdirectories = true;

		/// <summary>
		/// Occurs when file system changes have been batched and processed.
		/// </summary>
		public event EventHandler<BatchedFileSystemEventArgs> BatchedChanges;

		/// <summary>
		/// Occurs when an error is encountered during file system watching.
		/// </summary>
		public event EventHandler<ErrorEventArgs> Error;

		/// <inheritdoc/>
		public TimeSpan BatchDelay
		{
			get => _batchDelay;
			set
			{
				if (value < TimeSpan.Zero || value > TimeSpan.FromSeconds(10))
					throw new ArgumentOutOfRangeException(nameof(value), "Batch delay must be between 0 and 10 seconds.");
				_batchDelay = value;
			}
		}

		/// <inheritdoc/>
		public bool IncludeSubdirectories
		{
			get => _includeSubdirectories;
			set
			{
				_includeSubdirectories = value;
				if (_watcher is not null)
					_watcher.IncludeSubdirectories = value;
			}
		}

		/// <inheritdoc/>
		public string Path => _watchPath;

		/// <summary>
		/// Initializes a new instance of the OptimizedFileSystemWatcher class.
		/// </summary>
		/// <param name="path">The directory to monitor.</param>
		public OptimizedFileSystemWatcher(string path)
		{
			_watchPath = path ?? throw new ArgumentNullException(nameof(path));
			_statistics.StartTime = DateTime.UtcNow;
		}

		/// <inheritdoc/>
		public void StartWatcher()
		{
			if (_isDisposed)
				throw new ObjectDisposedException(nameof(OptimizedFileSystemWatcher));

			if (_watcher is not null)
				return; // Already started

			_cancellationTokenSource = new CancellationTokenSource();

			// Start the background processing task
			_processingTask = Task.Run(() => ProcessEventsAsync(_cancellationTokenSource.Token));

			// Initialize the batch timer
			_batchTimer = new Timer(ProcessBatch, null, Timeout.Infinite, Timeout.Infinite);

			// Create and configure the FileSystemWatcher
			SafetyExtensions.IgnoreExceptions(() =>
			{
				_watcher = new FileSystemWatcher
				{
					Path = _watchPath,
					Filter = "*.*",
					NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
					IncludeSubdirectories = _includeSubdirectories
				};

				// Dynamically adjust buffer size based on path type
				_watcher.InternalBufferSize = IsNetworkPath(_watchPath) ? 65536 : 32768;

				// Subscribe to events
				_watcher.Created += OnFileSystemEvent;
				_watcher.Deleted += OnFileSystemEvent;
				_watcher.Changed += OnFileSystemEvent;
				_watcher.Renamed += OnFileSystemRenamed;
				_watcher.Error += OnError;

				_watcher.EnableRaisingEvents = true;
			}, App.Logger);
		}

		/// <inheritdoc/>
		public void StopWatcher()
		{
			if (_watcher is null)
				return;

			_watcher.EnableRaisingEvents = false;
			_watcher.Dispose();
			_watcher = null;

			_cancellationTokenSource?.Cancel();
			_batchTimer?.Dispose();

			// Wait for processing to complete
			try
			{
				_processingTask?.Wait(TimeSpan.FromSeconds(2));
			}
			catch (AggregateException)
			{
				// Expected when cancellation occurs
			}

			_eventQueue.CompleteAdding();
			_cancellationTokenSource?.Dispose();
			_cancellationTokenSource = null;
			_processingTask = null;
		}

		/// <inheritdoc/>
		public FileSystemWatcherStatistics GetStatistics()
		{
			return new FileSystemWatcherStatistics
			{
				TotalEventsReceived = _statistics.TotalEventsReceived,
				TotalBatchesProcessed = _statistics.TotalBatchesProcessed,
				EventsNormalized = _statistics.EventsNormalized,
				EventsIgnored = _statistics.EventsIgnored,
				StartTime = _statistics.StartTime
			};
		}

		/// <inheritdoc/>
		public void Dispose()
		{
			if (_isDisposed)
				return;

			_isDisposed = true;
			StopWatcher();
			_eventQueue.Dispose();
			_batchTimer?.Dispose();
		}

		private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
		{
			Interlocked.Increment(ref _statistics.TotalEventsReceived);

			var eventInfo = new FileSystemEventInfo
			{
				ChangeType = e.ChangeType,
				FullPath = e.FullPath,
				Name = e.Name,
				Timestamp = DateTime.UtcNow
			};

			// Add to queue for processing
			if (!_eventQueue.IsAddingCompleted)
			{
				try
				{
					_eventQueue.Add(eventInfo);
				}
				catch (InvalidOperationException)
				{
					// Queue is being shut down
				}
			}
		}

		private void OnFileSystemRenamed(object sender, RenamedEventArgs e)
		{
			Interlocked.Increment(ref _statistics.TotalEventsReceived);

			var eventInfo = new FileSystemEventInfo
			{
				ChangeType = e.ChangeType,
				FullPath = e.FullPath,
				OldFullPath = e.OldFullPath,
				Name = e.Name,
				OldName = e.OldName,
				Timestamp = DateTime.UtcNow
			};

			// Add to queue for processing
			if (!_eventQueue.IsAddingCompleted)
			{
				try
				{
					_eventQueue.Add(eventInfo);
				}
				catch (InvalidOperationException)
				{
					// Queue is being shut down
				}
			}
		}

		private void OnError(object sender, ErrorEventArgs e)
		{
			Error?.Invoke(this, e);
			
			// Log the error
			Debug.WriteLine($"FileSystemWatcher error: {e.GetException().Message}");
			
			// Attempt to restart the watcher after an error
			Task.Delay(1000).ContinueWith(_ =>
			{
				if (!_isDisposed && _watcher is not null)
				{
					StopWatcher();
					StartWatcher();
				}
			});
		}

		private async Task ProcessEventsAsync(CancellationToken cancellationToken)
		{
			try
			{
				foreach (var eventInfo in _eventQueue.GetConsumingEnumerable(cancellationToken))
				{
					lock (_pendingEventsLock)
					{
						// Apply smart event normalization
						if (_pendingEvents.TryGetValue(eventInfo.FullPath, out var existingEvent))
						{
							var normalizedEvent = NormalizeEvents(existingEvent, eventInfo);
							if (normalizedEvent is not null)
							{
								_pendingEvents[eventInfo.FullPath] = normalizedEvent;
							}
							else
							{
								// Events cancelled each other out
								_pendingEvents.Remove(eventInfo.FullPath);
							}
						}
						else
						{
							_pendingEvents[eventInfo.FullPath] = eventInfo;
						}

						// Reset the batch timer
						_batchTimer?.Change(_batchDelay, Timeout.InfiniteTimeSpan);
					}
				}
			}
			catch (OperationCanceledException)
			{
				// Expected during shutdown
			}
		}

		private FileSystemEventInfo NormalizeEvents(FileSystemEventInfo existing, FileSystemEventInfo newEvent)
		{
			// Smart event normalization logic
			if (existing.ChangeType == WatcherChangeTypes.Created && newEvent.ChangeType == WatcherChangeTypes.Deleted)
			{
				// CREATE + DELETE = Ignore (file was created and immediately deleted)
				Interlocked.Increment(ref _statistics.EventsIgnored);
				Interlocked.Increment(ref _statistics.EventsNormalized);
				return null;
			}
			
			if (existing.ChangeType == WatcherChangeTypes.Deleted && newEvent.ChangeType == WatcherChangeTypes.Created)
			{
				// DELETE + CREATE = CHANGE (file was replaced)
				Interlocked.Increment(ref _statistics.EventsNormalized);
				return new FileSystemEventInfo
				{
					ChangeType = WatcherChangeTypes.Changed,
					FullPath = newEvent.FullPath,
					Name = newEvent.Name,
					Timestamp = newEvent.Timestamp
				};
			}
			
			if (existing.ChangeType == WatcherChangeTypes.Changed && newEvent.ChangeType == WatcherChangeTypes.Changed)
			{
				// Multiple CHANGE events = Keep latest
				Interlocked.Increment(ref _statistics.EventsNormalized);
				return newEvent;
			}
			
			if (existing.ChangeType == WatcherChangeTypes.Created && newEvent.ChangeType == WatcherChangeTypes.Changed)
			{
				// CREATE + CHANGE = CREATE (file was created and modified)
				return existing;
			}

			// For other combinations, keep the latest event
			return newEvent;
		}

		private void ProcessBatch(object state)
		{
			List<FileSystemEventInfo> batchedEvents;
			
			lock (_pendingEventsLock)
			{
				if (_pendingEvents.Count == 0)
					return;

				batchedEvents = _pendingEvents.Values.ToList();
				_pendingEvents.Clear();
			}

			Interlocked.Increment(ref _statistics.TotalBatchesProcessed);

			// Group events by type for efficient processing
			var eventGroups = batchedEvents.GroupBy(e => e.ChangeType).ToList();

			// Raise the batched event
			var args = new BatchedFileSystemEventArgs
			{
				Events = batchedEvents,
				CreatedCount = eventGroups.FirstOrDefault(g => g.Key == WatcherChangeTypes.Created)?.Count() ?? 0,
				DeletedCount = eventGroups.FirstOrDefault(g => g.Key == WatcherChangeTypes.Deleted)?.Count() ?? 0,
				ChangedCount = eventGroups.FirstOrDefault(g => g.Key == WatcherChangeTypes.Changed)?.Count() ?? 0,
				RenamedCount = eventGroups.FirstOrDefault(g => g.Key == WatcherChangeTypes.Renamed)?.Count() ?? 0
			};

			BatchedChanges?.Invoke(this, args);
		}

		private static bool IsNetworkPath(string path)
		{
			if (string.IsNullOrEmpty(path))
				return false;

			// Check for UNC paths
			if (path.StartsWith(@"\\"))
				return true;

			// Check if the drive is a network drive
			try
			{
				var root = System.IO.Path.GetPathRoot(path);
				if (!string.IsNullOrEmpty(root))
				{
					var driveInfo = new DriveInfo(root);
					return driveInfo.DriveType == System.IO.DriveType.Network;
				}
			}
			catch
			{
				// If we can't determine, assume local
			}

			return false;
		}
	}

	/// <summary>
	/// Represents information about a file system event.
	/// </summary>
	public class FileSystemEventInfo
	{
		public WatcherChangeTypes ChangeType { get; set; }
		public string FullPath { get; set; }
		public string Name { get; set; }
		public string OldFullPath { get; set; }
		public string OldName { get; set; }
		public DateTime Timestamp { get; set; }
	}

	/// <summary>
	/// Provides data for the BatchedChanges event.
	/// </summary>
	public class BatchedFileSystemEventArgs : EventArgs
	{
		public IReadOnlyList<FileSystemEventInfo> Events { get; set; }
		public int CreatedCount { get; set; }
		public int DeletedCount { get; set; }
		public int ChangedCount { get; set; }
		public int RenamedCount { get; set; }
		public int TotalCount => CreatedCount + DeletedCount + ChangedCount + RenamedCount;
	}
}