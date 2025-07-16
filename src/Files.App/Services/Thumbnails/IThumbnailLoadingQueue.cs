// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Utils;
using Files.App.Services.Caching;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Files.App.Services.Thumbnails
{
	/// <summary>
	/// Provides an optimized queue system for loading thumbnails with priority-based processing,
	/// batching support, and cancellation capabilities.
	/// </summary>
	public interface IThumbnailLoadingQueue : IDisposable
	{
		/// <summary>
		/// Queues a thumbnail loading request with priority based on viewport visibility.
		/// </summary>
		/// <param name="path">The file path</param>
		/// <param name="item">The ListedItem to update with the thumbnail</param>
		/// <param name="thumbnailSize">The requested thumbnail size</param>
		/// <param name="priority">Priority level (higher values processed first)</param>
		/// <param name="cancellationToken">Cancellation token for the request</param>
		/// <returns>A task that completes when the thumbnail is loaded or cancelled</returns>
		Task<ThumbnailLoadResult> QueueThumbnailRequestAsync(
			string path,
			ListedItem item,
			uint thumbnailSize,
			int priority,
			CancellationToken cancellationToken);

		/// <summary>
		/// Queues multiple thumbnail requests as a batch for optimized processing.
		/// </summary>
		/// <param name="requests">Collection of thumbnail requests</param>
		/// <param name="cancellationToken">Cancellation token for all requests in the batch</param>
		/// <returns>A task containing results for all requests in the batch</returns>
		Task<IEnumerable<ThumbnailLoadResult>> QueueBatchRequestAsync(
			IEnumerable<ThumbnailRequest> requests,
			CancellationToken cancellationToken);

		/// <summary>
		/// Updates the priority of an existing request (useful when viewport changes).
		/// </summary>
		/// <param name="path">The file path</param>
		/// <param name="newPriority">The new priority level</param>
		/// <returns>True if the priority was updated, false if request not found</returns>
		bool UpdateRequestPriority(string path, int newPriority);

		/// <summary>
		/// Cancels a pending thumbnail request.
		/// </summary>
		/// <param name="path">The file path</param>
		/// <returns>True if the request was cancelled, false if not found or already processing</returns>
		bool CancelRequest(string path);

		/// <summary>
		/// Cancels all pending requests that match the given predicate.
		/// </summary>
		/// <param name="predicate">Condition to match requests for cancellation</param>
		/// <returns>Number of requests cancelled</returns>
		int CancelRequests(Func<ThumbnailRequest, bool> predicate);

		/// <summary>
		/// Gets the current queue depth.
		/// </summary>
		int QueueDepth { get; }

		/// <summary>
		/// Gets the number of requests currently being processed.
		/// </summary>
		int ActiveRequests { get; }

		/// <summary>
		/// Event raised when a thumbnail loading operation completes.
		/// </summary>
		event EventHandler<ThumbnailLoadedEventArgs> ThumbnailLoaded;

		/// <summary>
		/// Event raised when a batch of thumbnails completes loading.
		/// </summary>
		event EventHandler<BatchThumbnailLoadedEventArgs> BatchCompleted;

		/// <summary>
		/// Event raised periodically to report queue processing progress.
		/// </summary>
		event EventHandler<ThumbnailQueueProgressEventArgs> ProgressChanged;
	}

	/// <summary>
	/// Represents a thumbnail loading request.
	/// </summary>
	public class ThumbnailRequest
	{
		public required string Path { get; init; }
		public required ListedItem Item { get; init; }
		public required uint ThumbnailSize { get; init; }
		public required int Priority { get; set; }
		public IconOptions IconOptions { get; init; } = IconOptions.None;
		public DateTime QueuedTime { get; init; } = DateTime.UtcNow;
	}

	/// <summary>
	/// Result of a thumbnail loading operation.
	/// </summary>
	public class ThumbnailLoadResult
	{
		public required string Path { get; init; }
		public BitmapImage? Thumbnail { get; init; }
		public bool Success { get; init; }
		public string? ErrorMessage { get; init; }
		public TimeSpan LoadTime { get; init; }
		public bool WasCancelled { get; init; }
	}

	/// <summary>
	/// Event args for batch thumbnail loaded event.
	/// </summary>
	public class BatchThumbnailLoadedEventArgs : EventArgs
	{
		public required IEnumerable<ThumbnailLoadResult> Results { get; init; }
		public required int TotalRequests { get; init; }
		public required int SuccessfulLoads { get; init; }
		public required TimeSpan TotalLoadTime { get; init; }
	}

	/// <summary>
	/// Event args for thumbnail queue progress.
	/// </summary>
	public class ThumbnailQueueProgressEventArgs : EventArgs
	{
		public required int QueueDepth { get; init; }
		public required int ActiveRequests { get; init; }
		public required int ProcessedCount { get; init; }
		public required double AverageLoadTimeMs { get; init; }
	}
}