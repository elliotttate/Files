// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.Collections.Specialized;
using System.IO;
using Windows.Storage;

namespace Files.App.Utils.Cloud
{
	public static class CloudDrivesManager
	{
		private static readonly ILogger _logger = Ioc.Default.GetRequiredService<ILogger<App>>();

		private static readonly ICloudDetector _detector = Ioc.Default.GetRequiredService<ICloudDetector>();

		public static EventHandler<NotifyCollectionChangedEventArgs> DataChanged;

		private static readonly List<DriveItem> _Drives = [];
		public static IReadOnlyList<DriveItem> Drives
		{
			get
			{
				lock (_Drives)
				{
					return _Drives.ToList().AsReadOnly();
				}
			}
		}

		public static async Task UpdateDrivesAsync()
		{
			var providers = await _detector.DetectCloudProvidersAsync();
			if (providers is null)
				return;

			foreach (var provider in providers)
			{
				// Skip cloud providers mapped to paths inside Git folders
				if (IsPathInsideGitFolder(provider.SyncFolder))
				{
					_logger?.LogWarning($"Skipping cloud provider \"{provider.Name}\" - path inside Git folder: {provider.SyncFolder}");
					continue;
				}
				
				_logger?.LogInformation($"Adding cloud provider \"{provider.Name}\" mapped to {provider.SyncFolder}");

				var cloudProviderItem = new DriveItem()
				{
					Text = provider.Name,
					Path = provider.SyncFolder,
					Type = Data.Items.DriveType.CloudDrive,
				};

				try
				{
					cloudProviderItem.Root = await StorageFolder.GetFolderFromPathAsync(cloudProviderItem.Path);

					_ = MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(() => cloudProviderItem.UpdatePropertiesAsync());
				}
				catch (FileNotFoundException ex)
				{
					_logger?.LogInformation(ex, "Failed to find the cloud folder");
				}
				catch (UnauthorizedAccessException ex)
				{
					_logger?.LogInformation(ex, " Failed to access the cloud folder");
				}
				catch (Exception ex)
				{
					_logger?.LogWarning(ex, "Cloud provider local folder couldn't be found");
				}

				cloudProviderItem.MenuOptions = new ContextMenuOptions()
				{
					IsLocationItem = true,
					ShowEjectDevice = cloudProviderItem.IsRemovable,
					ShowShellItems = true,
					ShowProperties = true,
				};

				var iconData = provider.IconData;

				if (iconData is null)
				{
					var result = await FileThumbnailHelper.GetIconAsync(
						provider.SyncFolder,
						Constants.ShellIconSizes.Large,
						false,
						IconOptions.ReturnIconOnly | IconOptions.UseCurrentScale);

					iconData = result;
				}

				if (iconData is not null)
				{
					cloudProviderItem.IconData = iconData;

					await MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(async ()
						=> cloudProviderItem.Icon = await iconData.ToBitmapAsync());
				}

				lock (_Drives)
				{
					if (_Drives.Any(x => x.Path == cloudProviderItem.Path))
						continue;

					_Drives.Add(cloudProviderItem);
				}

				DataChanged?.Invoke(
					SectionType.CloudDrives,
					new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, cloudProviderItem)
				);
			}
		}
		
		private static bool IsPathInsideGitFolder(string path)
		{
			if (string.IsNullOrEmpty(path))
				return false;
				
			try
			{
				// Check if path contains .git or other version control folders
				var pathParts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
				foreach (var part in pathParts)
				{
					if (part.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
					    part.Equals(".github", StringComparison.OrdinalIgnoreCase) ||
					    part.Equals(".svn", StringComparison.OrdinalIgnoreCase) ||
					    part.Equals(".hg", StringComparison.OrdinalIgnoreCase) ||
					    part.Equals(".bzr", StringComparison.OrdinalIgnoreCase) ||
					    part.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
					{
						return true;
					}
				}
				
				// Also check parent directories
				var directory = new DirectoryInfo(path);
				while (directory != null)
				{
					if (directory.Name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
					    directory.Name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
					{
						return true;
					}
					directory = directory.Parent;
				}
			}
			catch (Exception ex)
			{
				_logger?.LogWarning(ex, "Error checking if path is inside Git folder: {Path}", path);
			}
			
			return false;
		}
	}
}
