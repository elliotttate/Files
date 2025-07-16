// Copyright (c) Files Community
// Licensed under the MIT License.

// NOTE: This is an integration example showing how to use OptimizedGridLayoutViewModel
// in the GridLayoutPage. This code demonstrates the integration points.

using Files.App.ViewModels.Layouts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using System;

namespace Files.App.Views.Layouts
{
	/// <summary>
	/// Example integration code for using OptimizedGridLayoutViewModel with GridLayoutPage.
	/// This file shows how to integrate the optimized thumbnail loading system.
	/// </summary>
	public static class GridLayoutPageIntegrationExample
	{
		/*
		// Add this field to GridLayoutPage
		private OptimizedGridLayoutViewModel _thumbnailViewModel;

		// Add this method to GridLayoutPage's OnNavigatedTo
		private void InitializeThumbnailOptimization()
		{
			// Create and initialize the optimized thumbnail view model
			_thumbnailViewModel = new OptimizedGridLayoutViewModel();

			// Get the current thumbnail size based on layout mode
			uint thumbnailSize = LayoutSizeKindHelper.GetIconSize(FolderSettings.LayoutMode);

			// Initialize with the GridView and items collection
			if (FileList != null && ParentShellPageInstance?.ShellViewModel?.FilesAndFolders != null)
			{
				_thumbnailViewModel.Initialize(
					FileList, 
					ParentShellPageInstance.ShellViewModel.FilesAndFolders.View,
					thumbnailSize);
			}
		}

		// Update the existing LayoutSettingsService_PropertyChanged method
		private async void LayoutSettingsService_PropertyChanged_Enhanced(object? sender, PropertyChangedEventArgs e)
		{
			// ... existing code ...

			// Add this to handle thumbnail size changes
			if (e.PropertyName == nameof(ILayoutSettingsService.GridViewSize))
			{
				if (_thumbnailViewModel != null)
				{
					uint newThumbnailSize = LayoutSizeKindHelper.GetIconSize(FolderSettings.LayoutMode);
					await _thumbnailViewModel.UpdateThumbnailSizeAsync(newThumbnailSize);
				}
			}
		}

		// Update OnNavigatingFrom to dispose the view model
		// Note: In your actual implementation, override the appropriate method
		protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
		{
			base.OnNavigatingFrom(e);
			
			// Dispose thumbnail view model
			_thumbnailViewModel?.Dispose();
			_thumbnailViewModel = null;
		}

		// Example of how to integrate with existing container content changing
		private void FileList_ContainerContentChanging_Enhanced(ListViewBase sender, ContainerContentChangingEventArgs args)
		{
			// Let the optimized view model handle container changes
			_thumbnailViewModel?.OnContainerContentChanging(sender, args);
			
			// ... existing code if any ...
		}

		// XAML Integration Example
		// In GridLayoutPage.xaml, ensure your GridView is set up properly:
		
		// <GridView x:Name="FileList"
		//           ScrollViewer.HorizontalScrollBarVisibility="Auto"
		//           ScrollViewer.VerticalScrollBarVisibility="Auto">
		//     <GridView.ScrollViewer>
		//         <ScrollViewer x:Name="ContentScroller" />
		//     </GridView.ScrollViewer>
		// </GridView>

		// Optional: Add these event handlers to support image loading animations
		// <DataTemplate>
		//     <Grid>
		//         <Image Loading="Image_Loading" 
		//                Loaded="Image_Loaded" 
		//                ImageFailed="Image_Failed" />
		//         <Grid x:Name="LoadingPlaceholder" Visibility="Collapsed">
		//             <!-- Loading indicator -->
		//         </Grid>
		//     </Grid>
		// </DataTemplate>

		// Example event handlers for image loading states
		private void Image_Loading(FrameworkElement sender, object args)
		{
			// Show loading placeholder
			if (sender.Parent is Grid parent)
			{
				var placeholder = parent.FindName("LoadingPlaceholder") as FrameworkElement;
				if (placeholder != null)
					placeholder.Visibility = Visibility.Visible;
			}
		}

		private void Image_Loaded(object sender, RoutedEventArgs e)
		{
			// Hide loading placeholder and show image with fade-in
			if (sender is Image image && image.Parent is Grid parent)
			{
				var placeholder = parent.FindName("LoadingPlaceholder") as FrameworkElement;
				if (placeholder != null)
					placeholder.Visibility = Visibility.Collapsed;

				// Ensure smooth fade-in animation
				image.Opacity = 0;
				var fadeInStoryboard = new Storyboard();
				var fadeInAnimation = new DoubleAnimation
				{
					From = 0,
					To = 1,
					Duration = new Duration(TimeSpan.FromMilliseconds(200))
				};
				Storyboard.SetTarget(fadeInAnimation, image);
				Storyboard.SetTargetProperty(fadeInAnimation, "Opacity");
				fadeInStoryboard.Children.Add(fadeInAnimation);
				fadeInStoryboard.Begin();
			}
		}

		private void Image_Failed(object sender, ExceptionRoutedEventArgs e)
		{
			// Hide loading placeholder and show error state
			if (sender is Image image && image.Parent is Grid parent)
			{
				var placeholder = parent.FindName("LoadingPlaceholder") as FrameworkElement;
				if (placeholder != null)
					placeholder.Visibility = Visibility.Collapsed;

				// Optionally show an error icon or placeholder
			}
		}
		*/
	}
}