// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Services.Caching;
using Files.App.Services.SizeProvider;
using Files.Shared.Helpers;
using Microsoft.Extensions.Logging;
using System.IO;
using Windows.Storage;
using static Files.App.Helpers.Win32Helper;
using FileAttributes = System.IO.FileAttributes;

namespace Files.App.Utils.Storage
{
	public static class Win32StorageEnumerator
	{
		private static readonly ISizeProvider folderSizeProvider = Ioc.Default.GetService<ISizeProvider>();
		private static readonly IStorageCacheService fileListCache = Ioc.Default.GetRequiredService<IStorageCacheService>();
		private static readonly IFileModelCacheService fileModelCache = Ioc.Default.GetService<IFileModelCacheService>();

		private static readonly string folderTypeTextLocalized = Strings.Folder.GetLocalizedResource();

		// Performance optimization: Adaptive batch sizes
		private const int INITIAL_BATCH_SIZE = 50;  // First batch to show items quickly
		private const int REGULAR_BATCH_SIZE = 100; // Subsequent batches for normal directories
		private const int LARGE_DIR_BATCH_SIZE = 500; // Larger batches for huge directories
		private const int BATCH_DELAY_MS = 10;      // Small delay between batches to keep UI responsive
		private const int LARGE_DIR_THRESHOLD = 5000; // Consider directory "large" after this many items

		// Problematic system files and directories to skip
		private static readonly HashSet<string> ProblematicFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"pagefile.sys",
			"hiberfil.sys",
			"swapfile.sys",
			"bootstat.dat",
			"ntuser.dat",
			"ntuser.pol",
			"usrclass.dat"
		};

		private static readonly HashSet<string> ProblematicDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"System Volume Information",
			"$Recycle.Bin",
			"$RECYCLE.BIN",
			"Config.Msi",
			"Recovery",
			".git",         // Git repository folder
			".github",      // GitHub metadata folder
			".svn",         // Subversion folder
			".hg",          // Mercurial folder
			".bzr"          // Bazaar folder
		};

		public static async Task<List<ListedItem>> ListEntries(
			string path,
			IntPtr hFile,
			Win32PInvoke.WIN32_FIND_DATA findData,
			CancellationToken cancellationToken,
			int countLimit,
			Func<List<ListedItem>, Task> intermediateAction
		)
		{
			var sampler = new IntervalSampler(100); // Reduced from 500ms for more responsive updates
			var tempList = new List<ListedItem>();
			var count = 0;
			var batchCount = 0;

			IUserSettingsService userSettingsService = Ioc.Default.GetRequiredService<IUserSettingsService>();
			bool CalculateFolderSizes = userSettingsService.FoldersSettingsService.CalculateFolderSizes;

			// Known large system directories - disable folder size calculation for performance
			var knownLargeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			{
				@"C:\Windows\WinSxS",
				@"C:\Windows\Installer", 
				@"C:\Windows\System32",
				@"C:\Windows\SysWOW64",
				@"C:\ProgramData",
				@"C:\Users\All Users",
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages")
			};

			// Disable folder size calculation for known large directories
			if (knownLargeDirectories.Any(largePath => path.StartsWith(largePath, StringComparison.OrdinalIgnoreCase)))
			{
				CalculateFolderSizes = false;
			}

			var isGitRepo = GitHelpers.IsRepositoryEx(path, out var repoPath) && !string.IsNullOrEmpty((await GitHelpers.GetRepositoryHead(repoPath).ConfigureAwait(false))?.Name);

			do
			{
				var isSystem = ((FileAttributes)findData.dwFileAttributes & FileAttributes.System) == FileAttributes.System;
				var isHidden = ((FileAttributes)findData.dwFileAttributes & FileAttributes.Hidden) == FileAttributes.Hidden;
				var startWithDot = findData.cFileName.StartsWith('.');
				if ((!isHidden ||
					(userSettingsService.FoldersSettingsService.ShowHiddenItems &&
					(!isSystem || userSettingsService.FoldersSettingsService.ShowProtectedSystemFiles))) &&
					(!startWithDot || userSettingsService.FoldersSettingsService.ShowDotFiles))
				{
					if (((FileAttributes)findData.dwFileAttributes & FileAttributes.Directory) != FileAttributes.Directory)
					{
						// Skip problematic system files
						if (ProblematicFiles.Contains(findData.cFileName))
						{
							continue;
						}
						
						var file = await GetFile(findData, path, isGitRepo, cancellationToken).ConfigureAwait(false);
						if (file is not null)
						{
							tempList.Add(file);
							++count;

							if (userSettingsService.FoldersSettingsService.AreAlternateStreamsVisible)
							{
								tempList.AddRange(EnumAdsForPath(file.ItemPath, file));
							}
						}
					}
					else if (((FileAttributes)findData.dwFileAttributes & FileAttributes.Directory) == FileAttributes.Directory)
					{
						if (findData.cFileName != "." && findData.cFileName != "..")
						{
							// Skip problematic system directories
							if (ProblematicDirectories.Contains(findData.cFileName))
							{
								App.Logger?.LogInformation("Skipping problematic directory: {DirectoryName}", findData.cFileName);
								continue;
							}
							
							// Also skip any directory ending with .git (bare repositories)
							if (findData.cFileName.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
							{
								App.Logger?.LogInformation("Skipping bare Git repository: {DirectoryName}", findData.cFileName);
								continue;
							}
							
							var folder = await GetFolder(findData, path, isGitRepo, cancellationToken).ConfigureAwait(false);
							if (folder is not null)
							{
								tempList.Add(folder);
								++count;

								if (userSettingsService.FoldersSettingsService.AreAlternateStreamsVisible)
									tempList.AddRange(EnumAdsForPath(folder.ItemPath, folder));

								if (CalculateFolderSizes)
								{
									if (folderSizeProvider.TryGetSize(folder.ItemPath, out var size))
									{
										folder.FileSizeBytes = (long)size;
										folder.FileSize = size.ToSizeString();
									}

									_ = folderSizeProvider.UpdateAsync(folder.ItemPath, cancellationToken).ConfigureAwait(false);
								}
							}
						}
					}
				}

				if (cancellationToken.IsCancellationRequested || count == countLimit)
					break;

				// Progressive loading: Adaptive batch sizing based on total items
				int currentBatchSize;
				if (batchCount == 0)
				{
					currentBatchSize = INITIAL_BATCH_SIZE; // First batch - show items quickly
				}
				else if (count >= LARGE_DIR_THRESHOLD)
				{
					currentBatchSize = LARGE_DIR_BATCH_SIZE; // Large directory - use bigger batches
				}
				else
				{
					currentBatchSize = REGULAR_BATCH_SIZE; // Normal directory
				}
				
				if (intermediateAction is not null && (tempList.Count >= currentBatchSize || sampler.CheckNow()))
				{
					await intermediateAction(tempList).ConfigureAwait(false);
					tempList.Clear();
					batchCount++;
					
					// Log progress for large directories
					if (count >= LARGE_DIR_THRESHOLD && batchCount % 10 == 0)
					{
						System.Diagnostics.Debug.WriteLine($"Loading large directory {path}: {count} items loaded...");
					}
					
					// Small delay between batches to keep UI responsive
					if (batchCount > 1)
						await Task.Delay(BATCH_DELAY_MS, cancellationToken).ConfigureAwait(false);
				}
			} while (Win32PInvoke.FindNextFile(hFile, out findData));

			Win32PInvoke.FindClose(hFile);

			// Log completion for large directories
			if (count >= LARGE_DIR_THRESHOLD)
			{
				System.Diagnostics.Debug.WriteLine($"Completed loading large directory {path}: {count} total items");
			}

			return tempList;
		}

		private static IEnumerable<ListedItem> EnumAdsForPath(string itemPath, ListedItem main)
		{
			foreach (var ads in Win32Helper.GetAlternateStreams(itemPath))
				yield return GetAlternateStream(ads, main);
		}

		public static ListedItem GetAlternateStream((string Name, long Size) ads, ListedItem main)
		{
			string itemType = Strings.File.GetLocalizedResource();
			string itemFileExtension = null;

			if (ads.Name.Contains('.'))
			{
				itemFileExtension = Path.GetExtension(ads.Name);
				itemType = itemFileExtension.Trim('.') + " " + itemType;
			}

			string adsName = ads.Name.Substring(1, ads.Name.Length - 7); // Remove ":" and ":$DATA"

			return new AlternateStreamItem()
			{
				PrimaryItemAttribute = StorageItemTypes.File,
				FileExtension = itemFileExtension,
				FileImage = null,
				LoadFileIcon = true,
				NeedsPlaceholderGlyph = true,
				ItemNameRaw = adsName,
				IsHiddenItem = false,
				Opacity = Constants.UI.DimItemOpacity,
				ItemDateModifiedReal = main.ItemDateModifiedReal,
				ItemDateAccessedReal = main.ItemDateAccessedReal,
				ItemDateCreatedReal = main.ItemDateCreatedReal,
				ItemType = itemType,
				ItemPath = $"{main.ItemPath}:{adsName}",
				FileSize = ads.Size.ToSizeString(),
				FileSizeBytes = ads.Size
			};
		}

		public static async Task<ListedItem> GetFolder(
			Win32PInvoke.WIN32_FIND_DATA findData,
			string pathRoot,
			bool isGitRepo,
			CancellationToken cancellationToken
		)
		{
			if (cancellationToken.IsCancellationRequested)
				return null;

			var itemPath = Path.Combine(pathRoot, findData.cFileName);

			// Check cache first
			if (fileModelCache != null)
			{
				var cached = fileModelCache.GetCachedItem(itemPath);
				if (cached != null)
				{
					// Update timestamps from file system
					try
					{
						Win32PInvoke.FileTimeToSystemTime(ref findData.ftLastWriteTime, out Win32PInvoke.SYSTEMTIME systemModifiedTimeOutput);
						cached.ItemDateModifiedReal = systemModifiedTimeOutput.ToDateTime();
						
						Win32PInvoke.FileTimeToSystemTime(ref findData.ftCreationTime, out Win32PInvoke.SYSTEMTIME systemCreatedTimeOutput);
						cached.ItemDateCreatedReal = systemCreatedTimeOutput.ToDateTime();
					}
					catch { }
					
					return cached;
				}
			}

			DateTime itemModifiedDate;
			DateTime itemCreatedDate;

			try
			{
				Win32PInvoke.FileTimeToSystemTime(ref findData.ftLastWriteTime, out Win32PInvoke.SYSTEMTIME systemModifiedTimeOutput);
				itemModifiedDate = systemModifiedTimeOutput.ToDateTime();

				Win32PInvoke.FileTimeToSystemTime(ref findData.ftCreationTime, out Win32PInvoke.SYSTEMTIME systemCreatedTimeOutput);
				itemCreatedDate = systemCreatedTimeOutput.ToDateTime();
			}
			catch (ArgumentException)
			{
				// Invalid date means invalid findData, do not add to list
				return null;
			}

			string itemName = await fileListCache.GetDisplayName(itemPath, cancellationToken).ConfigureAwait(false);
			if (string.IsNullOrEmpty(itemName))
				itemName = findData.cFileName;

			bool isHidden = (((FileAttributes)findData.dwFileAttributes & FileAttributes.Hidden) == FileAttributes.Hidden);
			double opacity = 1;

			if (isHidden)
				opacity = Constants.UI.DimItemOpacity;

			if (isGitRepo)
			{
				return CacheAndReturnItem(new GitItem()
				{
					PrimaryItemAttribute = StorageItemTypes.Folder,
					ItemNameRaw = itemName,
					ItemDateModifiedReal = itemModifiedDate,
					ItemDateCreatedReal = itemCreatedDate,
					ItemType = folderTypeTextLocalized,
					FileImage = null,
					IsHiddenItem = isHidden,
					Opacity = opacity,
					LoadFileIcon = true,
					NeedsPlaceholderGlyph = true,
					ItemPath = itemPath,
					FileSize = null,
					FileSizeBytes = 0,
				});
			}
			else
			{
				return CacheAndReturnItem(new ListedItem(null)
				{
					PrimaryItemAttribute = StorageItemTypes.Folder,
					ItemNameRaw = itemName,
					ItemDateModifiedReal = itemModifiedDate,
					ItemDateCreatedReal = itemCreatedDate,
					ItemType = folderTypeTextLocalized,
					FileImage = null,
					IsHiddenItem = isHidden,
					Opacity = opacity,
					LoadFileIcon = true,
					NeedsPlaceholderGlyph = true,
					ItemPath = itemPath,
					FileSize = null,
					FileSizeBytes = 0,
				});
			}
		}

		public static async Task<ListedItem> GetFile(
			Win32PInvoke.WIN32_FIND_DATA findData,
			string pathRoot,
			bool isGitRepo,
			CancellationToken cancellationToken
		)
		{
			var itemPath = Path.Combine(pathRoot, findData.cFileName);
			var itemName = findData.cFileName;

			// Check cache first
			if (fileModelCache != null)
			{
				var cached = fileModelCache.GetCachedItem(itemPath);
				if (cached != null)
				{
					// Update timestamps from file system
					try
					{
						Win32PInvoke.FileTimeToSystemTime(ref findData.ftLastWriteTime, out Win32PInvoke.SYSTEMTIME systemModifiedDateOutput);
						cached.ItemDateModifiedReal = systemModifiedDateOutput.ToDateTime();
						
						Win32PInvoke.FileTimeToSystemTime(ref findData.ftCreationTime, out Win32PInvoke.SYSTEMTIME systemCreatedDateOutput);
						cached.ItemDateCreatedReal = systemCreatedDateOutput.ToDateTime();
						
						Win32PInvoke.FileTimeToSystemTime(ref findData.ftLastAccessTime, out Win32PInvoke.SYSTEMTIME systemLastAccessOutput);
						cached.ItemDateAccessedReal = systemLastAccessOutput.ToDateTime();
					}
					catch { }
					
					return cached;
				}
			}

			DateTime itemModifiedDate, itemCreatedDate, itemLastAccessDate;

			try
			{
				Win32PInvoke.FileTimeToSystemTime(ref findData.ftLastWriteTime, out Win32PInvoke.SYSTEMTIME systemModifiedDateOutput);
				itemModifiedDate = systemModifiedDateOutput.ToDateTime();

				Win32PInvoke.FileTimeToSystemTime(ref findData.ftCreationTime, out Win32PInvoke.SYSTEMTIME systemCreatedDateOutput);
				itemCreatedDate = systemCreatedDateOutput.ToDateTime();

				Win32PInvoke.FileTimeToSystemTime(ref findData.ftLastAccessTime, out Win32PInvoke.SYSTEMTIME systemLastAccessOutput);
				itemLastAccessDate = systemLastAccessOutput.ToDateTime();
			}
			catch (ArgumentException)
			{
				// Invalid date means invalid findData, do not add to list
				return null;
			}

			long itemSizeBytes = findData.GetSize();
			var itemSize = itemSizeBytes.ToSizeString();
			string itemType = Strings.File.GetLocalizedResource();
			string itemFileExtension = null;

			if (findData.cFileName.Contains('.'))
			{
				itemFileExtension = Path.GetExtension(itemPath);
				itemType = itemFileExtension.Trim('.') + " " + itemType;
			}

			// Always show placeholder icons initially
			bool itemThumbnailImgVis = true;
			bool itemEmptyImgVis = true;

			if (cancellationToken.IsCancellationRequested)
				return null;

			bool isHidden = ((FileAttributes)findData.dwFileAttributes & FileAttributes.Hidden) == FileAttributes.Hidden;
			double opacity = isHidden ? Constants.UI.DimItemOpacity : 1;

			// https://learn.microsoft.com/openspecs/windows_protocols/ms-fscc/c8e77b37-3909-4fe6-a4ea-2b9d423b1ee4
			bool isReparsePoint = ((FileAttributes)findData.dwFileAttributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
			bool isSymlink = isReparsePoint && findData.dwReserved0 == Win32PInvoke.IO_REPARSE_TAG_SYMLINK;

			if (isSymlink)
			{
				var targetPath = Win32Helper.ParseSymLink(itemPath);
				if (isGitRepo)
				{
					return CacheAndReturnItem(new GitShortcutItem()
					{
						PrimaryItemAttribute = StorageItemTypes.File,
						FileExtension = itemFileExtension,
						IsHiddenItem = isHidden,
						Opacity = opacity,
						FileImage = null,
						LoadFileIcon = true,
						NeedsPlaceholderGlyph = true,
						ItemNameRaw = itemName,
						ItemDateModifiedReal = itemModifiedDate,
						ItemDateAccessedReal = itemLastAccessDate,
						ItemDateCreatedReal = itemCreatedDate,
						ItemType = Strings.Shortcut.GetLocalizedResource(),
						ItemPath = itemPath,
						FileSize = itemSize,
						FileSizeBytes = itemSizeBytes,
						TargetPath = targetPath,
						IsSymLink = true,
					});
				}
				else
				{
					return CacheAndReturnItem(new ShortcutItem(null)
					{
						PrimaryItemAttribute = StorageItemTypes.File,
						FileExtension = itemFileExtension,
						IsHiddenItem = isHidden,
						Opacity = opacity,
						FileImage = null,
						LoadFileIcon = true,
						NeedsPlaceholderGlyph = true,
						ItemNameRaw = itemName,
						ItemDateModifiedReal = itemModifiedDate,
						ItemDateAccessedReal = itemLastAccessDate,
						ItemDateCreatedReal = itemCreatedDate,
						ItemType = Strings.Shortcut.GetLocalizedResource(),
						ItemPath = itemPath,
						FileSize = itemSize,
						FileSizeBytes = itemSizeBytes,
						TargetPath = targetPath,
						IsSymLink = true
					});
				}
			}
			else if (FileExtensionHelpers.IsShortcutOrUrlFile(findData.cFileName))
			{
				var isUrl = FileExtensionHelpers.IsWebLinkFile(findData.cFileName);

				var shInfo = await FileOperationsHelpers.ParseLinkAsync(itemPath).ConfigureAwait(false);
				if (shInfo is null)
					return null;

				if (isGitRepo)
				{
					return CacheAndReturnItem(new GitShortcutItem()
					{
						PrimaryItemAttribute = shInfo.IsFolder ? StorageItemTypes.Folder : StorageItemTypes.File,
						FileExtension = itemFileExtension,
						IsHiddenItem = isHidden,
						Opacity = opacity,
						FileImage = null,
						LoadFileIcon = true,
						NeedsPlaceholderGlyph = true,
						ItemNameRaw = itemName,
						ItemDateModifiedReal = itemModifiedDate,
						ItemDateAccessedReal = itemLastAccessDate,
						ItemDateCreatedReal = itemCreatedDate,
						ItemType = isUrl ? Strings.ShortcutWebLinkFileType.GetLocalizedResource() : Strings.Shortcut.GetLocalizedResource(),
						ItemPath = itemPath,
						FileSize = itemSize,
						FileSizeBytes = itemSizeBytes,
						TargetPath = shInfo.TargetPath,
						Arguments = shInfo.Arguments,
						WorkingDirectory = shInfo.WorkingDirectory,
						RunAsAdmin = shInfo.RunAsAdmin,
						ShowWindowCommand = shInfo.ShowWindowCommand,
						IsUrl = isUrl,
					});
				}
				else
				{
					return CacheAndReturnItem(new ShortcutItem(null)
					{
						PrimaryItemAttribute = shInfo.IsFolder ? StorageItemTypes.Folder : StorageItemTypes.File,
						FileExtension = itemFileExtension,
						IsHiddenItem = isHidden,
						Opacity = opacity,
						FileImage = null,
						LoadFileIcon = true,
						NeedsPlaceholderGlyph = true,
						ItemNameRaw = itemName,
						ItemDateModifiedReal = itemModifiedDate,
						ItemDateAccessedReal = itemLastAccessDate,
						ItemDateCreatedReal = itemCreatedDate,
						ItemType = isUrl ? Strings.ShortcutWebLinkFileType.GetLocalizedResource() : Strings.Shortcut.GetLocalizedResource(),
						ItemPath = itemPath,
						FileSize = itemSize,
						FileSizeBytes = itemSizeBytes,
						TargetPath = shInfo.TargetPath,
						Arguments = shInfo.Arguments,
						WorkingDirectory = shInfo.WorkingDirectory,
						RunAsAdmin = shInfo.RunAsAdmin,
						ShowWindowCommand = shInfo.ShowWindowCommand,
						IsUrl = isUrl,
					});
				}
			}
			else if (App.LibraryManager.TryGetLibrary(itemPath, out LibraryLocationItem library))
			{
				return CacheAndReturnItem(new LibraryItem(library)
				{
					Opacity = opacity,
					ItemDateModifiedReal = itemModifiedDate,
					ItemDateCreatedReal = itemCreatedDate,
				});
			}
			else
			{
				if (ZipStorageFolder.IsZipPath(itemPath) && await ZipStorageFolder.CheckDefaultZipApp(itemPath).ConfigureAwait(false))
				{
					return CacheAndReturnItem(new ZipItem(null)
					{
						PrimaryItemAttribute = StorageItemTypes.Folder, // Treat zip files as folders
						FileExtension = itemFileExtension,
						FileImage = null,
						LoadFileIcon = true,
						NeedsPlaceholderGlyph = true,
						ItemNameRaw = itemName,
						IsHiddenItem = isHidden,
						Opacity = opacity,
						ItemDateModifiedReal = itemModifiedDate,
						ItemDateAccessedReal = itemLastAccessDate,
						ItemDateCreatedReal = itemCreatedDate,
						ItemType = itemType,
						ItemPath = itemPath,
						FileSize = itemSize,
						FileSizeBytes = itemSizeBytes
					});
				}
				else if (isGitRepo)
				{
					return CacheAndReturnItem(new GitItem()
					{
						PrimaryItemAttribute = StorageItemTypes.File,
						FileExtension = itemFileExtension,
						FileImage = null,
						LoadFileIcon = true,
						NeedsPlaceholderGlyph = true,
						ItemNameRaw = itemName,
						IsHiddenItem = isHidden,
						Opacity = opacity,
						ItemDateModifiedReal = itemModifiedDate,
						ItemDateAccessedReal = itemLastAccessDate,
						ItemDateCreatedReal = itemCreatedDate,
						ItemType = itemType,
						ItemPath = itemPath,
						FileSize = itemSize,
						FileSizeBytes = itemSizeBytes
					});
				}
				else
				{
					return CacheAndReturnItem(new ListedItem(null)
					{
						PrimaryItemAttribute = StorageItemTypes.File,
						FileExtension = itemFileExtension,
						FileImage = null,
						LoadFileIcon = true,
						NeedsPlaceholderGlyph = true,
						ItemNameRaw = itemName,
						IsHiddenItem = isHidden,
						Opacity = opacity,
						ItemDateModifiedReal = itemModifiedDate,
						ItemDateAccessedReal = itemLastAccessDate,
						ItemDateCreatedReal = itemCreatedDate,
						ItemType = itemType,
						ItemPath = itemPath,
						FileSize = itemSize,
						FileSizeBytes = itemSizeBytes
					});
				}
			}

			return null;
		}

		private static ListedItem CacheAndReturnItem(ListedItem item)
		{
			if (item != null && fileModelCache != null && !string.IsNullOrEmpty(item.ItemPath))
			{
				fileModelCache.AddOrUpdateItem(item.ItemPath, item);
			}
			return item;
		}
	}
}
