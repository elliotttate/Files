// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.Mvvm.DependencyInjection;
using Files.App.Services.Monitoring;
using Files.App.Views.Debugging;
using Microsoft.UI.Xaml.Controls;
using System;
using System.IO;

namespace Files.App.Extensions
{
	/// <summary>
	/// Extension methods for thumbnail performance monitoring.
	/// </summary>
	public static class ThumbnailPerformanceExtensions
	{
		/// <summary>
		/// Shows the thumbnail performance dashboard in a content dialog.
		/// </summary>
		public static async Task ShowPerformanceDashboardAsync()
		{
			var dialog = new ContentDialog
			{
				Title = "Thumbnail Performance Monitor",
				Content = new ThumbnailPerformanceDashboard(),
				CloseButtonText = "Close",
				DefaultButton = ContentDialogButton.Close,
				XamlRoot = MainWindow.Instance.Content.XamlRoot
			};

			dialog.Resources["ContentDialogMaxWidth"] = 1200;
			dialog.Resources["ContentDialogMaxHeight"] = 800;

			await dialog.ShowAsync();
		}

		/// <summary>
		/// Gets a quick performance summary string.
		/// </summary>
		public static string GetPerformanceSummary()
		{
			try
			{
				var monitor = Ioc.Default.GetService<IThumbnailPerformanceMonitor>();
				if (monitor == null)
					return "Performance monitor not available";

				var report = monitor.GenerateReport();
				return $"Thumbnails: {report.TotalCompleted:N0} loaded, " +
					   $"{report.CacheHitRate:P0} cache hit, " +
					   $"{report.AverageLoadTimeMs:F0}ms avg";
			}
			catch
			{
				return "Performance data unavailable";
			}
		}

		/// <summary>
		/// Checks if performance monitoring is available.
		/// </summary>
		public static bool IsPerformanceMonitoringAvailable()
		{
			try
			{
				return Ioc.Default.GetService<IThumbnailPerformanceMonitor>() != null;
			}
			catch
			{
				return false;
			}
		}

		/// <summary>
		/// Exports performance report to the desktop.
		/// </summary>
		public static async Task<string?> ExportPerformanceReportAsync()
		{
			try
			{
				var monitor = Ioc.Default.GetService<IThumbnailPerformanceMonitor>();
				if (monitor == null)
					return null;

				var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
				var fileName = $"ThumbnailPerformance_{DateTime.Now:yyyyMMdd_HHmmss}.md";
				var filePath = Path.Combine(desktopPath, fileName);

				var data = monitor.ExportPerformanceData(ExportFormat.Markdown);
				await File.WriteAllTextAsync(filePath, data);

				return filePath;
			}
			catch
			{
				return null;
			}
		}
	}
}