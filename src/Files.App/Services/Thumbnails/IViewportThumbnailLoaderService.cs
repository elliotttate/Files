// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Data.Items;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Files.App.Services.Thumbnails
{
	/// <summary>
	/// Service that manages thumbnail loading based on viewport visibility
	/// </summary>
	public interface IViewportThumbnailLoaderService
	{
		/// <summary>
		/// Updates the viewport with the currently visible items
		/// </summary>
		/// <param name="visibleItems">Collection of items currently visible in the viewport</param>
		/// <param name="thumbnailSize">Size of thumbnails to load</param>
		/// <param name="cancellationToken">Cancellation token</param>
		Task UpdateViewportAsync(IEnumerable<ListedItem> visibleItems, uint thumbnailSize, CancellationToken cancellationToken = default);

		/// <summary>
		/// Clears the current viewport and cancels pending thumbnail loads
		/// </summary>
		void ClearViewport();

		/// <summary>
		/// Preloads thumbnails for items near the viewport
		/// </summary>
		/// <param name="itemsNearViewport">Items that are near but not in the viewport</param>
		/// <param name="thumbnailSize">Size of thumbnails to preload</param>
		/// <param name="cancellationToken">Cancellation token</param>
		Task PreloadNearViewportAsync(IEnumerable<ListedItem> itemsNearViewport, uint thumbnailSize, CancellationToken cancellationToken = default);

		/// <summary>
		/// Gets the number of items currently being loaded
		/// </summary>
		int ActiveLoadCount { get; }

		/// <summary>
		/// Gets whether the service is currently loading thumbnails
		/// </summary>
		bool IsLoading { get; }
	}
}