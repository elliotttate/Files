// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using Files.Core.Storage.Contracts;

namespace Files.App.Services.FileSystem
{
	/// <summary>
	/// Defines a contract for an optimized file system watcher that supports event batching and aggregation.
	/// </summary>
	public interface IOptimizedFileSystemWatcher : IWatcher
	{
		/// <summary>
		/// Occurs when file system changes have been batched and processed.
		/// </summary>
		event EventHandler<BatchedFileSystemEventArgs> BatchedChanges;

		/// <summary>
		/// Occurs when an error is encountered during file system watching.
		/// </summary>
		event EventHandler<System.IO.ErrorEventArgs> Error;

		/// <summary>
		/// Gets or sets the delay in milliseconds for batching events.
		/// </summary>
		TimeSpan BatchDelay { get; set; }

		/// <summary>
		/// Gets or sets whether subdirectories should be monitored.
		/// </summary>
		bool IncludeSubdirectories { get; set; }

		/// <summary>
		/// Gets the path being monitored.
		/// </summary>
		string Path { get; }

		/// <summary>
		/// Gets statistics about the watcher's performance.
		/// </summary>
		FileSystemWatcherStatistics GetStatistics();
	}

	/// <summary>
	/// Provides statistics about the file system watcher's performance.
	/// </summary>
	public class FileSystemWatcherStatistics
	{
		// Use fields instead of properties so they can be used with Interlocked operations
		public long TotalEventsReceived;
		public long TotalBatchesProcessed;
		public long EventsNormalized;
		public long EventsIgnored;
		
		public DateTime StartTime { get; set; }
		public TimeSpan UpTime => DateTime.UtcNow - StartTime;
		public double EventsPerSecond => TotalEventsReceived / Math.Max(1, UpTime.TotalSeconds);
		public double AverageEventsPerBatch => TotalEventsReceived / Math.Max(1.0, TotalBatchesProcessed);
	}
}