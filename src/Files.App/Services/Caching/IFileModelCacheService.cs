// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Utils;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.Concurrent;

namespace Files.App.Services.Caching
{
	/// <summary>
	/// Provides a service for caching file models and their associated data including thumbnails, media properties, and file details.
	/// </summary>
	public interface IFileModelCacheService
	{
		/// <summary>
		/// Retrieves a cached ListedItem by its path
		/// </summary>
		/// <param name="path">The file path</param>
		/// <returns>The cached ListedItem or null if not found</returns>
		ListedItem GetCachedItem(string path);

		/// <summary>
		/// Adds or updates a ListedItem in the cache
		/// </summary>
		/// <param name="path">The file path</param>
		/// <param name="item">The ListedItem to cache</param>
		void AddOrUpdateItem(string path, ListedItem item);

		/// <summary>
		/// Removes an item from the cache
		/// </summary>
		/// <param name="path">The file path</param>
		/// <returns>True if the item was removed, false otherwise</returns>
		bool RemoveItem(string path);

		/// <summary>
		/// Clears all items from the cache
		/// </summary>
		void ClearCache();

		/// <summary>
		/// Gets a cached thumbnail for a file
		/// </summary>
		/// <param name="path">The file path</param>
		/// <returns>The cached thumbnail or null if not found</returns>
		BitmapImage GetCachedThumbnail(string path);

		/// <summary>
		/// Adds or updates a thumbnail in the cache
		/// </summary>
		/// <param name="path">The file path</param>
		/// <param name="thumbnail">The thumbnail to cache</param>
		void AddOrUpdateThumbnail(string path, BitmapImage thumbnail);

		/// <summary>
		/// Queues a thumbnail for loading
		/// </summary>
		/// <param name="path">The file path</param>
		/// <param name="item">The ListedItem associated with the thumbnail</param>
		/// <param name="thumbnailSize">The requested thumbnail size</param>
		/// <param name="cancellationToken">Cancellation token</param>
		/// <param name="isPriority">Whether this is a high priority request (e.g., for visible items)</param>
		/// <returns>A task that completes when the thumbnail is loaded</returns>
		Task QueueThumbnailLoadAsync(string path, ListedItem item, uint thumbnailSize, CancellationToken cancellationToken, bool isPriority = false);

		/// <summary>
		/// Gets cached media properties for a file
		/// </summary>
		/// <param name="path">The file path</param>
		/// <returns>The cached media properties or null if not found</returns>
		MediaProperties GetCachedMediaProperties(string path);

		/// <summary>
		/// Adds or updates media properties in the cache
		/// </summary>
		/// <param name="path">The file path</param>
		/// <param name="properties">The media properties to cache</param>
		void AddOrUpdateMediaProperties(string path, MediaProperties properties);

		/// <summary>
		/// Loads media properties asynchronously
		/// </summary>
		/// <param name="path">The file path</param>
		/// <param name="cancellationToken">Cancellation token</param>
		/// <returns>A task containing the media properties</returns>
		Task<MediaProperties> LoadMediaPropertiesAsync(string path, CancellationToken cancellationToken);

		/// <summary>
		/// Gets the current cache size in bytes
		/// </summary>
		long GetCacheSizeInBytes();

		/// <summary>
		/// Performs cache cleanup to free memory
		/// </summary>
		/// <param name="targetSizeInBytes">Target cache size in bytes</param>
		/// <returns>The number of items removed</returns>
		int PerformCacheCleanup(long targetSizeInBytes);

		/// <summary>
		/// Event raised when a thumbnail is loaded
		/// </summary>
		event EventHandler<ThumbnailLoadedEventArgs> ThumbnailLoaded;

		/// <summary>
		/// Event raised when media properties are loaded
		/// </summary>
		event EventHandler<MediaPropertiesLoadedEventArgs> MediaPropertiesLoaded;
	}

	/// <summary>
	/// Media properties for cached files
	/// </summary>
	public class MediaProperties
	{
		public string ImageDimensions { get; set; }
		public string MediaDuration { get; set; }
		public string FileVersion { get; set; }
		public int? Width { get; set; }
		public int? Height { get; set; }
		public TimeSpan? Duration { get; set; }
		public DateTime LastUpdated { get; set; }
	}

	/// <summary>
	/// Event args for thumbnail loaded event
	/// </summary>
	public class ThumbnailLoadedEventArgs : EventArgs
	{
		public string Path { get; set; }
		public BitmapImage Thumbnail { get; set; }
	}

	/// <summary>
	/// Event args for media properties loaded event
	/// </summary>
	public class MediaPropertiesLoadedEventArgs : EventArgs
	{
		public string Path { get; set; }
		public MediaProperties Properties { get; set; }
	}
}