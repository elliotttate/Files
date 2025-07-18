﻿// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Dialogs;
using Microsoft.UI.Xaml.Controls;
using System.Text;
using Windows.Foundation.Metadata;
using Windows.Storage;

namespace Files.App.Actions
{
	internal sealed partial class DecompressArchiveToChildFolderAction : BaseDecompressArchiveAction
	{
		public override string Label
			=> ComputeLabel();

		public override string Description
			=> Strings.DecompressArchiveToChildFolderDescription.GetLocalizedFormatResource(context.SelectedItems.Count);

		public DecompressArchiveToChildFolderAction()
		{
		}

		public override async Task ExecuteAsync(object? parameter = null)
		{
			if (context.SelectedItems.Count is 0)
				return;

			foreach (var selectedItem in context.SelectedItems)
			{
				var password = string.Empty;

				BaseStorageFile archive = await StorageHelpers.ToStorageItem<BaseStorageFile>(selectedItem.ItemPath);
				var currentFolderPath = context.ShellPage?.ShellViewModel?.CurrentFolder?.ItemPath;
				if (string.IsNullOrEmpty(currentFolderPath))
					return;
				
				BaseStorageFolder currentFolder = await StorageHelpers.ToStorageItem<BaseStorageFolder>(currentFolderPath);
				BaseStorageFolder? destinationFolder = null;

				if (archive?.Path is null)
					return;

				if (await FilesystemTasks.Wrap(() => StorageArchiveService.IsEncryptedAsync(archive.Path)))
				{
					DecompressArchiveDialog decompressArchiveDialog = new();
					DecompressArchiveDialogViewModel decompressArchiveViewModel = new(archive)
					{
						IsArchiveEncrypted = true,
						ShowPathSelection = false
					};
					decompressArchiveDialog.ViewModel = decompressArchiveViewModel;

					if (ApiInformation.IsApiContractPresent("Windows.Foundation.UniversalApiContract", 8))
						decompressArchiveDialog.XamlRoot = MainWindow.Instance.Content.XamlRoot;

					ContentDialogResult option = await decompressArchiveDialog.TryShowAsync();
					if (option != ContentDialogResult.Primary)
						return;

					password = decompressArchiveViewModel.Password != null ? Encoding.UTF8.GetString(decompressArchiveViewModel.Password) : string.Empty;
				}

				if (currentFolder is not null)
					destinationFolder = await FilesystemTasks.Wrap(() => currentFolder.CreateFolderAsync(SystemIO.Path.GetFileNameWithoutExtension(archive.Path), CreationCollisionOption.GenerateUniqueName).AsTask());

				// Operate decompress
				var result = await FilesystemTasks.Wrap(() =>
					StorageArchiveService.DecompressAsync(selectedItem.ItemPath, destinationFolder?.Path ?? string.Empty, password));
			}
		}

		protected override void Context_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			switch (e.PropertyName)
			{
				case nameof(IContentPageContext.SelectedItems):
				case nameof(IContentPageContext.Folder):
					{
						if (IsContextPageTypeAdaptedToCommand())
						{
							OnPropertyChanged(nameof(Label));
							OnPropertyChanged(nameof(IsExecutable));
						}

						break;
					}
			}
		}

		private string ComputeLabel()
		{
			if (context.SelectedItems == null || context.SelectedItems.Count == 0)
				return string.Format(Strings.BaseLayoutItemContextFlyoutExtractToChildFolder.GetLocalizedResource(), string.Empty);

			return context.SelectedItems.Count > 1
				? string.Format(Strings.BaseLayoutItemContextFlyoutExtractToChildFolder.GetLocalizedResource(), "*")
				: string.Format(Strings.BaseLayoutItemContextFlyoutExtractToChildFolder.GetLocalizedResource(), SystemIO.Path.GetFileNameWithoutExtension(context.SelectedItems.First().Name));
		}
	}
}
