// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Data.Models;
using Files.App.ViewModels;
using System.Runtime.InteropServices;
using System.Text;
using System.IO;
using Windows.Storage;
using Microsoft.Extensions.Logging;

namespace Files.App.Services.Search
{
	public interface IEverythingSearchService
	{
		bool IsEverythingAvailable();
		Task<List<ListedItem>> SearchAsync(string query, string searchPath = null, CancellationToken cancellationToken = default);
		Task<List<ListedItem>> FilterItemsAsync(IEnumerable<ListedItem> items, string query, CancellationToken cancellationToken = default);
	}

	public sealed class EverythingSearchService : IEverythingSearchService
	{
		// Everything API constants
		private const int EVERYTHING_OK = 0;
		private const int EVERYTHING_ERROR_IPC = 2;
		
		private const int EVERYTHING_REQUEST_FILE_NAME = 0x00000001;
		private const int EVERYTHING_REQUEST_PATH = 0x00000002;
		private const int EVERYTHING_REQUEST_DATE_MODIFIED = 0x00000040;
		private const int EVERYTHING_REQUEST_SIZE = 0x00000010;
		private const int EVERYTHING_REQUEST_DATE_CREATED = 0x00000020;
		private const int EVERYTHING_REQUEST_ATTRIBUTES = 0x00000100;

		// Everything API imports - using 64-bit version
		[DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
		private static extern uint Everything_SetSearchW(string lpSearchString);
		
		[DllImport("Everything64.dll")]
		private static extern void Everything_SetMatchPath(bool bEnable);
		
		[DllImport("Everything64.dll")]
		private static extern void Everything_SetMatchCase(bool bEnable);
		
		[DllImport("Everything64.dll")]
		private static extern void Everything_SetMatchWholeWord(bool bEnable);
		
		[DllImport("Everything64.dll")]
		private static extern void Everything_SetRegex(bool bEnable);
		
		[DllImport("Everything64.dll")]
		private static extern void Everything_SetMax(uint dwMax);
		
		[DllImport("Everything64.dll")]
		private static extern void Everything_SetOffset(uint dwOffset);
		
		[DllImport("Everything64.dll")]
		private static extern void Everything_SetRequestFlags(uint dwRequestFlags);
		
		[DllImport("Everything64.dll")]
		private static extern bool Everything_QueryW(bool bWait);
		
		[DllImport("Everything64.dll")]
		private static extern uint Everything_GetNumResults();
		
		[DllImport("Everything64.dll")]
		private static extern uint Everything_GetLastError();
		
		[DllImport("Everything64.dll")]
		private static extern bool Everything_IsFileResult(uint nIndex);
		
		[DllImport("Everything64.dll")]
		private static extern bool Everything_IsFolderResult(uint nIndex);
		
		[DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
		private static extern IntPtr Everything_GetResultPath(uint nIndex);
		
		[DllImport("Everything64.dll", CharSet = CharSet.Unicode)]
		private static extern IntPtr Everything_GetResultFileName(uint nIndex);
		
		[DllImport("Everything64.dll")]
		private static extern bool Everything_GetResultDateModified(uint nIndex, out long lpFileTime);
		
		[DllImport("Everything64.dll")]
		private static extern bool Everything_GetResultDateCreated(uint nIndex, out long lpFileTime);
		
		[DllImport("Everything64.dll")]
		private static extern bool Everything_GetResultSize(uint nIndex, out long lpFileSize);
		
		[DllImport("Everything64.dll")]
		private static extern uint Everything_GetResultAttributes(uint nIndex);
		
		[DllImport("Everything64.dll")]
		private static extern void Everything_Reset();
		
		[DllImport("Everything64.dll")]
		private static extern void Everything_CleanUp();
		
		[DllImport("Everything64.dll")]
		private static extern bool Everything_IsDBLoaded();

		private readonly IUserSettingsService _userSettingsService;

		public EverythingSearchService()
		{
			_userSettingsService = Ioc.Default.GetRequiredService<IUserSettingsService>();
		}

		public bool IsEverythingAvailable()
		{
			try
			{
				// Check if Everything is running and database is loaded
				Everything_Reset();
				var isLoaded = Everything_IsDBLoaded();
				Everything_CleanUp();
				return isLoaded;
			}
			catch (DllNotFoundException)
			{
				// Everything DLL not found
				return false;
			}
			catch (Exception)
			{
				return false;
			}
		}

		public async Task<List<ListedItem>> SearchAsync(string query, string searchPath = null, CancellationToken cancellationToken = default)
		{
			return await Task.Run(() =>
			{
				var results = new List<ListedItem>();

				try
				{
					Everything_Reset();

					// Set up the search query
					var searchQuery = query;
					if (!string.IsNullOrEmpty(searchPath) && searchPath != "Home")
					{
						// Limit search to specific path
						searchQuery = $"\"{searchPath}\\\" {query}";
					}

					Everything_SetSearchW(searchQuery);
					Everything_SetMatchCase(false);
					Everything_SetRequestFlags(
						EVERYTHING_REQUEST_FILE_NAME | 
						EVERYTHING_REQUEST_PATH | 
						EVERYTHING_REQUEST_DATE_MODIFIED | 
						EVERYTHING_REQUEST_DATE_CREATED |
						EVERYTHING_REQUEST_SIZE |
						EVERYTHING_REQUEST_ATTRIBUTES);

					// Limit results to prevent overwhelming the UI
					Everything_SetMax(1000);

					// Execute the query
					if (!Everything_QueryW(true))
					{
						var error = Everything_GetLastError();
						if (error == EVERYTHING_ERROR_IPC)
						{
							// Everything is not running
							return results;
						}
					}

					var numResults = Everything_GetNumResults();

					for (uint i = 0; i < numResults; i++)
					{
						if (cancellationToken.IsCancellationRequested)
							break;

						try
						{
							var fileName = Marshal.PtrToStringUni(Everything_GetResultFileName(i));
							var path = Marshal.PtrToStringUni(Everything_GetResultPath(i));
							var fullPath = Path.Combine(path, fileName);

							// Skip if it doesn't match our filter criteria
							if (!string.IsNullOrEmpty(searchPath) && searchPath != "Home" && 
								!fullPath.StartsWith(searchPath, StringComparison.OrdinalIgnoreCase))
								continue;

							var isFolder = Everything_IsFolderResult(i);
							var attributes = Everything_GetResultAttributes(i);
							var isHidden = (attributes & 0x02) != 0; // FILE_ATTRIBUTE_HIDDEN

							// Check user settings for hidden items
							if (isHidden && !_userSettingsService.FoldersSettingsService.ShowHiddenItems)
								continue;

							// Check for dot files
							if (fileName.StartsWith('.') && !_userSettingsService.FoldersSettingsService.ShowDotFiles)
								continue;

							Everything_GetResultDateModified(i, out long dateModified);
							Everything_GetResultDateCreated(i, out long dateCreated);
							Everything_GetResultSize(i, out long size);

							var item = new ListedItem(null)
							{
								PrimaryItemAttribute = isFolder ? StorageItemTypes.Folder : StorageItemTypes.File,
								ItemNameRaw = fileName,
								ItemPath = fullPath,
								ItemDateModifiedReal = DateTime.FromFileTime(dateModified),
								ItemDateCreatedReal = DateTime.FromFileTime(dateCreated),
								IsHiddenItem = isHidden,
								LoadFileIcon = false,
								FileExtension = isFolder ? null : Path.GetExtension(fullPath),
								FileSizeBytes = isFolder ? 0 : size,
								FileSize = isFolder ? null : ByteSizeLib.ByteSize.FromBytes((ulong)size).ToBinaryString(),
								Opacity = isHidden ? Constants.UI.DimItemOpacity : 1
							};

							if (!isFolder)
							{
								item.ItemType = item.FileExtension?.Trim('.') + " " + Strings.File.GetLocalizedResource();
							}

							results.Add(item);
						}
						catch (Exception ex)
						{
							// Skip items that cause errors
							System.Diagnostics.Debug.WriteLine($"Error processing Everything result {i}: {ex.Message}");
						}
					}
				}
				catch (Exception ex)
				{
					App.Logger?.LogWarning(ex, "Everything search failed");
				}
				finally
				{
					Everything_CleanUp();
				}

				return results;
			}, cancellationToken);
		}

		public async Task<List<ListedItem>> FilterItemsAsync(IEnumerable<ListedItem> items, string query, CancellationToken cancellationToken = default)
		{
			// For filtering existing items, we'll use Everything's search on the current directory
			var firstItem = items.FirstOrDefault();
			if (firstItem == null)
				return new List<ListedItem>();

			// Get the directory path from the first item
			var directoryPath = Path.GetDirectoryName(firstItem.ItemPath);
			
			// Search within this directory
			var searchResults = await SearchAsync(query, directoryPath, cancellationToken);
			
			// Return only items that exist in the original collection
			var itemPaths = new HashSet<string>(items.Select(i => i.ItemPath), StringComparer.OrdinalIgnoreCase);
			return searchResults.Where(r => itemPaths.Contains(r.ItemPath)).ToList();
		}
	}
}