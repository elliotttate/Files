// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media.Imaging;
using System.IO;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Search;
using Windows.Storage.Streams;
using FileAttributes = System.IO.FileAttributes;
using WIN32_FIND_DATA = Files.App.Helpers.Win32PInvoke.WIN32_FIND_DATA;

namespace Files.App.Utils.Storage
{
	public sealed class FolderSearch
	{
		private IUserSettingsService UserSettingsService { get; } = Ioc.Default.GetRequiredService<IUserSettingsService>();
		private DrivesViewModel drivesViewModel = Ioc.Default.GetRequiredService<DrivesViewModel>();
		private readonly IStorageTrashBinService StorageTrashBinService = Ioc.Default.GetRequiredService<IStorageTrashBinService>();
		private readonly IFileTagsSettingsService fileTagsSettingsService = Ioc.Default.GetRequiredService<IFileTagsSettingsService>();

		private const uint defaultStepSize = 500;

		public string? Query { get; set; }

		public string? Folder { get; set; }

		public uint MaxItemCount { get; set; } = 0; // 0: no limit

		private uint UsedMaxItemCount => MaxItemCount > 0 ? MaxItemCount : uint.MaxValue;

		public EventHandler? SearchTick;

		private bool IsAQSQuery => Query is not null && (Query.StartsWith('$') || Query.Contains(':', StringComparison.Ordinal));

		private string QueryWithWildcard
		{
			get
			{
				if (!string.IsNullOrEmpty(Query) && Query.Contains('.')) // ".docx" -> "*.docx"
				{
					var split = Query.Split('.');
					var leading = string.Join('.', split.SkipLast(1));
					var query = $"{leading}*.{split.Last()}";
					return $"{query}*";
				}
				return $"{Query}*";
			}
		}

		public string AQSQuery
		{
			get
			{
				// if the query starts with a $, assume the query is in aqs format, otherwise assume the user is searching for the file name
				if (Query is not null && Query.StartsWith('$'))
				{
					return Query.Substring(1);
				}
				else if (Query is not null && Query.Contains(':', StringComparison.Ordinal))
				{
					return Query;
				}
				else
				{
					return $"System.FileName:\"{QueryWithWildcard}\"";
				}
			}
		}

		public async Task SearchAsync(IList<ListedItem> results, CancellationToken token)
		{
			try
			{
				App.Logger?.LogInformation($"=== FolderSearch.SearchAsync START === Query: '{Query}', Folder: '{Folder}'");
				
				// Check if we should use Everything for global search  
				var userSettingsService = Ioc.Default.GetRequiredService<IUserSettingsService>();
				var searchEngine = userSettingsService.GeneralSettingsService.PreferredSearchEngine;
				App.Logger?.LogInformation($"Search Engine Setting: {searchEngine}");
				
				if (searchEngine == Files.App.Data.Enums.SearchEngine.Everything)
				{
					var everythingService = Ioc.Default.GetService<Files.App.Services.Search.IEverythingSearchService>();
					if (everythingService != null)
					{
						var isAvailable = everythingService.IsEverythingAvailable();
						App.Logger?.LogInformation($"Everything Service Available: {isAvailable}");
						
						if (isAvailable)
						{
							App.Logger?.LogInformation("Using Everything for search");
							try
							{
								var everythingResults = await everythingService.SearchAsync(Query, Folder, token);
								App.Logger?.LogInformation($"Everything search completed with {everythingResults.Count} results");
								App.Logger?.LogInformation($"MaxItemCount: {MaxItemCount}, UsedMaxItemCount: {UsedMaxItemCount}");
								App.Logger?.LogInformation($"results is null: {results == null}, results.Count before: {results?.Count ?? -1}");
								App.Logger?.LogInformation($"everythingResults is null: {everythingResults == null}");
								
								if (everythingResults != null && everythingResults.Count > 0)
								{
									App.Logger?.LogInformation($"First result: {everythingResults[0]?.ItemPath ?? "null"}");
								}
								
								int addedCount = 0;
								// Fix: UsedMaxItemCount can be uint.MaxValue which overflows when cast to int
								var itemsToTake = UsedMaxItemCount == uint.MaxValue ? everythingResults.Count : Math.Min(everythingResults.Count, (int)UsedMaxItemCount);
								App.Logger?.LogInformation($"Taking {itemsToTake} items from {everythingResults.Count} results");
								
								foreach (var item in everythingResults.Take(itemsToTake))
								{
									if (item == null)
									{
										App.Logger?.LogInformation("Null item in everythingResults");
										continue;
									}
									results.Add(item);
									addedCount++;
									if (results.Count == 32 || results.Count % 300 == 0)
									{
										SearchTick?.Invoke(this, EventArgs.Empty);
									}
								}
								
								App.Logger?.LogInformation($"Added {addedCount} items to results, final results.Count: {results.Count}");
								return; // Exit early since we used Everything
							}
							catch (OperationCanceledException)
							{
								App.Logger?.LogInformation("Everything search was cancelled");
								return;
							}
							catch (Exception ex)
							{
								App.Logger?.LogError(ex, "Everything search failed");
								// Fall through to use default search
							}
						}
					}
					else
					{
						App.Logger?.LogInformation("Everything service not found in DI container");
					}
				}
				else
				{
					App.Logger?.LogInformation($"Not using Everything because search engine is set to: {searchEngine}");
				}

				// Fall back to default search
				if (App.LibraryManager.TryGetLibrary(Folder, out var library))
				{
					await AddItemsForLibraryAsync(library, results, token);
				}
				else if (Folder == "Home")
				{
					await AddItemsForHomeAsync(results, token);
				}
				else
				{
					await AddItemsAsync(Folder, results, token);
				}
			}
			catch (Exception e)
			{
				App.Logger?.LogInformation(e, "Search failure");
			}
		}

		private async Task AddItemsForHomeAsync(IList<ListedItem> results, CancellationToken token)
		{
			if (AQSQuery.StartsWith("tag:", StringComparison.Ordinal))
			{
				await SearchTagsAsync("", results, token); // Search tags everywhere, not only local drives
			}
			else
			{
				foreach (var drive in drivesViewModel.Drives.ToList().Cast<DriveItem>().Where(x => !x.IsNetwork))
				{
					await AddItemsAsync(drive.Path, results, token);
				}
			}
		}

		public async Task<ObservableCollection<ListedItem>> SearchAsync()
		{
			ObservableCollection<ListedItem> results = [];
			try
			{
				var token = CancellationToken.None;
				if (App.LibraryManager.TryGetLibrary(Folder, out var library))
				{
					await AddItemsForLibraryAsync(library, results, token);
				}
				else if (Folder == "Home")
				{
					await AddItemsForHomeAsync(results, token);
				}
				else
				{
					await AddItemsAsync(Folder, results, token);
				}
			}
			catch (Exception e)
			{
				App.Logger?.LogInformation(e, "Search failure");
			}

			return results;
		}

		private async Task SearchAsync(BaseStorageFolder folder, IList<ListedItem> results, CancellationToken token)
		{
			if (folder is null)
			{
				App.Logger?.LogInformation("SearchAsync called with null folder");
				return;
			}

			//var sampler = new IntervalSampler(500);
			uint index = 0;
			var stepSize = Math.Min(defaultStepSize, UsedMaxItemCount);
			var options = ToQueryOptions();

			if (options is null)
			{
				App.Logger?.LogInformation("ToQueryOptions returned null");
				return;
			}

			var queryResult = folder.CreateItemQueryWithOptions(options);
			var items = await queryResult.GetItemsAsync(0, stepSize);

			while (items.Count > 0)
			{
				foreach (IStorageItem item in items)
				{
					if (token.IsCancellationRequested)
					{
						return;
					}

					try
					{
						if (!item.Name.StartsWith('.') || UserSettingsService.FoldersSettingsService.ShowDotFiles)
							results.Add(await GetListedItemAsync(item));
					}
					catch (Exception ex)
					{
						App.Logger?.LogInformation(ex, "Error creating ListedItem from StorageItem");
					}

					if (results.Count == 32 || results.Count % 300 == 0 /*|| sampler.CheckNow()*/)
					{
						SearchTick?.Invoke(this, EventArgs.Empty);
					}
				}

				index += (uint)items.Count;
				stepSize = Math.Min(defaultStepSize, UsedMaxItemCount - (uint)results.Count);
				items = await queryResult.GetItemsAsync(index, stepSize);
			}
		}

		private async Task AddItemsForLibraryAsync(LibraryLocationItem library, IList<ListedItem> results, CancellationToken token)
		{
			foreach (var folder in library.Folders)
			{
				await AddItemsAsync(folder, results, token);
			}
		}

		private async Task SearchTagsAsync(string folder, IList<ListedItem> results, CancellationToken token)
		{
			//var sampler = new IntervalSampler(500);
			var tags = AQSQuery.Substring("tag:".Length)?.Split(',').Where(t => !string.IsNullOrWhiteSpace(t))
				.SelectMany(t => fileTagsSettingsService.GetTagsByName(t), (_, t) => t.Uid).ToHashSet();
			if (tags?.Any() != true)
			{
				return;
			}

			var dbInstance = FileTagsHelper.GetDbInstance();
			var matches = dbInstance.GetAllUnderPath(folder)
				.Where(x => tags.All(x.Tags.Contains));
			if (string.IsNullOrEmpty(folder))
				matches = matches.Where(x => !StorageTrashBinService.IsUnderTrashBin(x.FilePath));

			foreach (var match in matches)
			{
				(IntPtr hFile, WIN32_FIND_DATA findData) = await Task.Run(() =>
				{
					int additionalFlags = Win32PInvoke.FIND_FIRST_EX_LARGE_FETCH;
					IntPtr hFileTsk = Win32PInvoke.FindFirstFileExFromApp(match.FilePath, Win32PInvoke.FINDEX_INFO_LEVELS.FindExInfoBasic,
						out WIN32_FIND_DATA findDataTsk, Win32PInvoke.FINDEX_SEARCH_OPS.FindExSearchNameMatch, IntPtr.Zero, additionalFlags);
					return (hFileTsk, findDataTsk);
				}).WithTimeoutAsync(TimeSpan.FromSeconds(5));

				if (hFile != IntPtr.Zero && hFile.ToInt64() != -1)
				{
					var isSystem = ((FileAttributes)findData.dwFileAttributes & FileAttributes.System) == FileAttributes.System;
					var isHidden = ((FileAttributes)findData.dwFileAttributes & FileAttributes.Hidden) == FileAttributes.Hidden;
					var startWithDot = findData.cFileName.StartsWith('.');

					bool shouldBeListed = (!isHidden ||
						(UserSettingsService.FoldersSettingsService.ShowHiddenItems &&
						(!isSystem || UserSettingsService.FoldersSettingsService.ShowProtectedSystemFiles))) &&
						(!startWithDot || UserSettingsService.FoldersSettingsService.ShowDotFiles);

					if (shouldBeListed)
					{
						var item = GetListedItemAsync(match.FilePath, findData);
						if (item is not null)
						{
							results.Add(item);
						}
					}

					Win32PInvoke.FindClose(hFile);
				}
				else
				{
					try
					{
						IStorageItem item = null;
						var fileResult = await GetStorageFileAsync(match.FilePath);
						if (fileResult && fileResult.Result is not null)
						{
							item = fileResult.Result;
						}
						else
						{
							var folderResult = await GetStorageFolderAsync(match.FilePath);
							if (folderResult && folderResult.Result is not null)
								item = folderResult.Result;
						}
						
						if (item is not null && (!item.Name.StartsWith('.') || UserSettingsService.FoldersSettingsService.ShowDotFiles))
							results.Add(await GetListedItemAsync(item));
					}
					catch (Exception ex)
					{
						App.Logger?.LogInformation(ex, "Error creating ListedItem from StorageItem");
					}
				}

				if (token.IsCancellationRequested)
					return;

				if (results.Count == 32 || results.Count % 300 == 0 /*|| sampler.CheckNow()*/)
				{
					SearchTick?.Invoke(this, EventArgs.Empty);
				}
			}
		}

		private async Task AddItemsAsync(string folder, IList<ListedItem> results, CancellationToken token)
		{
			if (AQSQuery.StartsWith("tag:", StringComparison.Ordinal))
			{
				await SearchTagsAsync(folder, results, token);
			}
			else
			{
				var workingFolder = await GetStorageFolderAsync(folder);

				var hiddenOnlyFromWin32 = false;
				if (workingFolder && workingFolder.Result is not null)
				{
					await SearchAsync(workingFolder.Result, results, token);
					hiddenOnlyFromWin32 = (results.Count != 0);
				}

				if (!IsAQSQuery && (!hiddenOnlyFromWin32 || UserSettingsService.FoldersSettingsService.ShowHiddenItems))
				{
					await SearchWithWin32Async(folder, hiddenOnlyFromWin32, UsedMaxItemCount - (uint)results.Count, results, token);
				}
			}
		}

		private async Task SearchWithWin32Async(string folder, bool hiddenOnly, uint maxItemCount, IList<ListedItem> results, CancellationToken token)
		{
			//var sampler = new IntervalSampler(500);
			(IntPtr hFile, WIN32_FIND_DATA findData) = await Task.Run(() =>
			{
				int additionalFlags = Win32PInvoke.FIND_FIRST_EX_LARGE_FETCH;
				IntPtr hFileTsk = Win32PInvoke.FindFirstFileExFromApp($"{folder}\\{QueryWithWildcard}", Win32PInvoke.FINDEX_INFO_LEVELS.FindExInfoBasic,
					out WIN32_FIND_DATA findDataTsk, Win32PInvoke.FINDEX_SEARCH_OPS.FindExSearchNameMatch, IntPtr.Zero, additionalFlags);
				return (hFileTsk, findDataTsk);
			}).WithTimeoutAsync(TimeSpan.FromSeconds(5));

			if (hFile != IntPtr.Zero && hFile.ToInt64() != -1)
			{
				await Task.Run(() =>
				{
					var hasNextFile = false;
					do
					{
						if (results.Count >= maxItemCount)
						{
							break;
						}
						var itemPath = Path.Combine(folder, findData.cFileName);

						var isSystem = ((FileAttributes)findData.dwFileAttributes & FileAttributes.System) == FileAttributes.System;
						var isHidden = ((FileAttributes)findData.dwFileAttributes & FileAttributes.Hidden) == FileAttributes.Hidden;
						var startWithDot = findData.cFileName.StartsWith('.');

						bool shouldBeListed = (hiddenOnly ?
							isHidden && (!isSystem || !UserSettingsService.FoldersSettingsService.ShowProtectedSystemFiles) :
							!isHidden || (UserSettingsService.FoldersSettingsService.ShowHiddenItems && (!isSystem || UserSettingsService.FoldersSettingsService.ShowProtectedSystemFiles))) &&
							(!startWithDot || UserSettingsService.FoldersSettingsService.ShowDotFiles);

						if (shouldBeListed)
						{
							var item = GetListedItemAsync(itemPath, findData);
							if (item is not null)
							{
								results.Add(item);
							}
						}

						if (token.IsCancellationRequested)
						{
							break;
						}

						if (results.Count == 32 || results.Count % 300 == 0 /*|| sampler.CheckNow()*/)
						{
							SearchTick?.Invoke(this, EventArgs.Empty);
						}

						hasNextFile = Win32PInvoke.FindNextFile(hFile, out findData);
					} while (hasNextFile);

					Win32PInvoke.FindClose(hFile);
				}, token);
			}
		}

		private ListedItem GetListedItemAsync(string itemPath, WIN32_FIND_DATA findData)
		{
			ListedItem listedItem = null;
			var isHidden = ((FileAttributes)findData.dwFileAttributes & FileAttributes.Hidden) == FileAttributes.Hidden;
			var isFolder = ((FileAttributes)findData.dwFileAttributes & FileAttributes.Directory) == FileAttributes.Directory;
			Win32PInvoke.FileTimeToSystemTime(ref findData.ftLastWriteTime, out Win32PInvoke.SYSTEMTIME systemModifiedTimeOutput);
			Win32PInvoke.FileTimeToSystemTime(ref findData.ftCreationTime, out Win32PInvoke.SYSTEMTIME systemCreatedTimeOutput);

			if (!isFolder)
			{
				string itemFileExtension = null;
				string itemType = null;
				long fileSize = Win32FindDataExtensions.GetSize(findData);
				if (findData.cFileName.Contains('.', StringComparison.Ordinal))
				{
					itemFileExtension = Path.GetExtension(itemPath);
					itemType = itemFileExtension.Trim('.') + " " + itemType;
				}

				listedItem = new ListedItem(null)
				{
					PrimaryItemAttribute = StorageItemTypes.File,
					ItemNameRaw = findData.cFileName,
					ItemPath = itemPath,
					ItemDateModifiedReal = systemModifiedTimeOutput.ToDateTime(),
					ItemDateCreatedReal = systemCreatedTimeOutput.ToDateTime(),
					IsHiddenItem = isHidden,
					LoadFileIcon = false,
					FileExtension = itemFileExtension,
					ItemType = itemType,
					Opacity = isHidden ? Constants.UI.DimItemOpacity : 1,
					FileSize = fileSize.ToSizeString(),
					FileSizeBytes = fileSize,
				};
			}
			else
			{
				if (findData.cFileName != "." && findData.cFileName != "..")
				{
					listedItem = new ListedItem(null)
					{
						PrimaryItemAttribute = StorageItemTypes.Folder,
						ItemNameRaw = findData.cFileName,
						ItemPath = itemPath,
						ItemDateModifiedReal = systemModifiedTimeOutput.ToDateTime(),
						ItemDateCreatedReal = systemCreatedTimeOutput.ToDateTime(),
						IsHiddenItem = isHidden,
						LoadFileIcon = false,
						Opacity = isHidden ? Constants.UI.DimItemOpacity : 1
					};
				}
			}

			// For Everything search results, defer thumbnail loading to avoid reentrancy issues
			if (listedItem is not null && (MaxItemCount > 0 || listedItem.LoadFileIcon)) // Load icon for searchbox suggestions or when explicitly requested
			{
				App.Logger?.LogInformation("FolderSearch: Deferring icon load for {Path}, isFolder={IsFolder}", listedItem.ItemPath, isFolder);
				// Just mark that we want to load the icon later, don't load it now
				listedItem.LoadFileIcon = true;  // Enable thumbnail loading with hybrid approach
				listedItem.NeedsPlaceholderGlyph = true;
			}
			else
			{
				App.Logger?.LogInformation("FolderSearch: Skipping icon load for {Path}, listedItem null={IsNull}, MaxItemCount={MaxItemCount}, LoadFileIcon={LoadFileIcon}", 
					listedItem?.ItemPath ?? "null", listedItem is null, MaxItemCount, listedItem?.LoadFileIcon ?? false);
			}

			return listedItem;
		}

		private async Task<ListedItem> GetListedItemAsync(IStorageItem item)
		{
			ListedItem listedItem = null;
			if (item.IsOfType(StorageItemTypes.Folder))
			{
				var folder = item.AsBaseStorageFolder();
				var props = await folder.GetBasicPropertiesAsync();
				if (folder is BinStorageFolder binFolder)
				{
					listedItem = new RecycleBinItem(null)
					{
						PrimaryItemAttribute = StorageItemTypes.Folder,
						ItemNameRaw = folder.DisplayName,
						ItemPath = folder.Path,
						ItemDateModifiedReal = props.DateModified,
						ItemDateCreatedReal = folder.DateCreated,
						NeedsPlaceholderGlyph = false,
						Opacity = 1,
						FileSize = props.Size.ToSizeString(),
						FileSizeBytes = (long)props.Size,
						ItemDateDeletedReal = binFolder.DateDeleted,
						ItemOriginalPath = binFolder.OriginalPath
					};
				}
				else
				{
					listedItem = new ListedItem(null)
					{
						PrimaryItemAttribute = StorageItemTypes.Folder,
						ItemNameRaw = folder.DisplayName,
						ItemPath = folder.Path,
						ItemDateModifiedReal = props.DateModified,
						ItemDateCreatedReal = folder.DateCreated,
						NeedsPlaceholderGlyph = false,
						Opacity = 1
					};
				}
			}
			else if (item.IsOfType(StorageItemTypes.File))
			{
				var file = item.AsBaseStorageFile();
				var props = await file.GetBasicPropertiesAsync();
				string itemFileExtension = null;
				string itemType = null;
				if (file.Name.Contains('.', StringComparison.Ordinal))
				{
					itemFileExtension = Path.GetExtension(file.Path);
					itemType = itemFileExtension.Trim('.') + " " + itemType;
				}

				var itemSize = props.Size.ToSizeString();

				if (file is BinStorageFile binFile)
				{
					listedItem = new RecycleBinItem(null)
					{
						PrimaryItemAttribute = StorageItemTypes.File,
						ItemNameRaw = file.Name,
						ItemPath = file.Path,
						LoadFileIcon = false,
						FileExtension = itemFileExtension,
						FileSizeBytes = (long)props.Size,
						FileSize = itemSize,
						ItemDateModifiedReal = props.DateModified,
						ItemDateCreatedReal = file.DateCreated,
						ItemType = itemType,
						NeedsPlaceholderGlyph = false,
						Opacity = 1,
						ItemDateDeletedReal = binFile.DateDeleted,
						ItemOriginalPath = binFile.OriginalPath
					};
				}
				else
				{
					listedItem = new ListedItem(null)
					{
						PrimaryItemAttribute = StorageItemTypes.File,
						ItemNameRaw = file.Name,
						ItemPath = file.Path,
						LoadFileIcon = false,
						FileExtension = itemFileExtension,
						FileSizeBytes = (long)props.Size,
						FileSize = itemSize,
						ItemDateModifiedReal = props.DateModified,
						ItemDateCreatedReal = file.DateCreated,
						ItemType = itemType,
						NeedsPlaceholderGlyph = false,
						Opacity = 1
					};
				}
			}
			// For Everything search results, defer thumbnail loading to avoid reentrancy issues
			if (listedItem is not null && (MaxItemCount > 0 || listedItem.LoadFileIcon)) // Load icon for searchbox suggestions or when explicitly requested
			{
				App.Logger?.LogInformation("FolderSearch: Deferring icon load for storage item {Path}", item.Path);
				// Just mark that we want to load the icon later, don't load it now
				listedItem.LoadFileIcon = true;  // Enable thumbnail loading with hybrid approach
				listedItem.NeedsPlaceholderGlyph = true;
			}
			else
			{
				App.Logger?.LogInformation("FolderSearch: Skipping icon load for storage item {Path}, listedItem null={IsNull}, MaxItemCount={MaxItemCount}, LoadFileIcon={LoadFileIcon}", 
					item?.Path ?? "null", listedItem is null, MaxItemCount, listedItem?.LoadFileIcon ?? false);
			}
			return listedItem;
		}

		private QueryOptions ToQueryOptions()
		{
			var query = new QueryOptions
			{
				FolderDepth = FolderDepth.Deep,
				UserSearchFilter = AQSQuery ?? string.Empty,
			};

			query.IndexerOption = IndexerOption.UseIndexerWhenAvailable;

			query.SortOrder.Clear();
			query.SortOrder.Add(new SortEntry { PropertyName = "System.Search.Rank", AscendingOrder = false });

			query.SetPropertyPrefetch(PropertyPrefetchOptions.BasicProperties, null);
			query.SetThumbnailPrefetch(ThumbnailMode.ListView, 24, ThumbnailOptions.UseCurrentScale);

			return query;
		}

		private static Task<FilesystemResult<BaseStorageFolder>> GetStorageFolderAsync(string path)
			=> FilesystemTasks.Wrap(() => StorageFileExtensions.DangerousGetFolderFromPathAsync(path));

		private static Task<FilesystemResult<BaseStorageFile>> GetStorageFileAsync(string path)
			=> FilesystemTasks.Wrap(() => StorageFileExtensions.DangerousGetFileFromPathAsync(path));
	}
}
