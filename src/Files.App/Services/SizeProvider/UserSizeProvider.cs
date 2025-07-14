// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Services.SizeProvider;

namespace Files.App.Services
{
	public sealed partial class UserSizeProvider : ISizeProvider
	{
		private readonly IFoldersSettingsService folderPreferences
			= Ioc.Default.GetRequiredService<IFoldersSettingsService>();
		private readonly IGeneralSettingsService generalSettings
			= Ioc.Default.GetRequiredService<IGeneralSettingsService>();

		private ISizeProvider provider;

		public event EventHandler<SizeChangedEventArgs> SizeChanged;

		public UserSizeProvider()
		{
			provider = GetProvider();
			provider.SizeChanged += Provider_SizeChanged;

			folderPreferences.PropertyChanged += FolderPreferences_PropertyChanged;
			generalSettings.PropertyChanged += GeneralSettings_PropertyChanged;
		}

		public Task CleanAsync()
			=> provider.CleanAsync();

		public async Task ClearAsync()
			=> await provider.ClearAsync();

		public Task UpdateAsync(string path, CancellationToken cancellationToken)
			=> provider.UpdateAsync(path, cancellationToken);

		public bool TryGetSize(string path, out ulong size)
			=> provider.TryGetSize(path, out size);

		public void Dispose()
		{
			provider.Dispose();
			folderPreferences.PropertyChanged -= FolderPreferences_PropertyChanged;
			generalSettings.PropertyChanged -= GeneralSettings_PropertyChanged;
		}

		private ISizeProvider GetProvider()
		{
			if (!folderPreferences.CalculateFolderSizes)
				return new NoSizeProvider();

			// Use Everything for folder sizes if it's selected and available
			if (generalSettings.PreferredSearchEngine == Files.App.Data.Enums.SearchEngine.Everything)
			{
				var everythingService = Ioc.Default.GetService<Files.App.Services.Search.IEverythingSearchService>();
				if (everythingService != null && everythingService.IsEverythingAvailable())
				{
					return new EverythingSizeProvider();
				}
			}

			// Fall back to standard provider
			return new DrivesSizeProvider();
		}

		private async void FolderPreferences_PropertyChanged(object sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName is nameof(IFoldersSettingsService.CalculateFolderSizes))
			{
				await provider.ClearAsync();
				provider.SizeChanged -= Provider_SizeChanged;
				provider = GetProvider();
				provider.SizeChanged += Provider_SizeChanged;
			}
		}

		private async void GeneralSettings_PropertyChanged(object sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName is nameof(IGeneralSettingsService.PreferredSearchEngine))
			{
				// Only update if folder size calculation is enabled
				if (folderPreferences.CalculateFolderSizes)
				{
					await provider.ClearAsync();
					provider.SizeChanged -= Provider_SizeChanged;
					provider = GetProvider();
					provider.SizeChanged += Provider_SizeChanged;
				}
			}
		}

		private void Provider_SizeChanged(object sender, SizeChangedEventArgs e)
			=> SizeChanged?.Invoke(this, e);
	}
}
