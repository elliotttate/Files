// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.Mvvm.DependencyInjection;
using Files.App.Services.Monitoring;
using Microsoft.Extensions.Logging;
using System.IO;

namespace Files.App.Helpers.Debugging
{
	/// <summary>
	/// Provides helper methods for debugging thumbnail performance.
	/// </summary>
	public static class ThumbnailPerformanceHelper
	{
		private static IThumbnailPerformanceMonitor? _performanceMonitor;
		private static ILogger? _logger;

		/// <summary>
		/// Gets the thumbnail performance monitor instance.
		/// </summary>
		public static IThumbnailPerformanceMonitor? PerformanceMonitor
		{
			get
			{
				if (_performanceMonitor == null)
				{
					try
					{
						_performanceMonitor = Ioc.Default.GetService<IThumbnailPerformanceMonitor>();
					}
					catch
					{
						// Service might not be registered yet
					}
				}
				return _performanceMonitor;
			}
		}

		/// <summary>
		/// Starts performance monitoring if not already started.
		/// </summary>
		public static void StartMonitoring()
		{
			if (PerformanceMonitor == null)
			{
				_logger?.LogWarning("Performance monitor service not available");
				return;
			}

			_logger?.LogInformation("Thumbnail performance monitoring started");
		}

		/// <summary>
		/// Logs current performance metrics to the debug output.
		/// </summary>
		public static void LogCurrentMetrics()
		{
			if (PerformanceMonitor == null)
			{
				_logger?.LogWarning("Performance monitor service not available");
				return;
			}

			try
			{
				var report = PerformanceMonitor.GenerateReport();
				
				_logger?.LogInformation(
					"Thumbnail Performance Summary:\n" +
					"  Total Requests: {TotalRequests}\n" +
					"  Completed: {TotalCompleted}\n" +
					"  Cache Hit Rate: {CacheHitRate:P}\n" +
					"  Avg Load Time: {AvgLoadTime:F1}ms\n" +
					"  Peak Queue Depth: {PeakQueueDepth}\n" +
					"  Peak Memory: {PeakMemory:F1}MB",
					report.TotalRequests,
					report.TotalCompleted,
					report.CacheHitRate,
					report.AverageLoadTimeMs,
					report.PeakQueueDepth,
					report.PeakMemoryUsageMB);
			}
			catch (Exception ex)
			{
				_logger?.LogError(ex, "Failed to log performance metrics");
			}
		}

		/// <summary>
		/// Exports performance data to the specified file path.
		/// </summary>
		public static async Task ExportPerformanceDataAsync(string filePath, ExportFormat format = ExportFormat.Json)
		{
			if (PerformanceMonitor == null)
			{
				_logger?.LogWarning("Performance monitor service not available");
				return;
			}

			try
			{
				var data = PerformanceMonitor.ExportPerformanceData(format);
				await File.WriteAllTextAsync(filePath, data);
				_logger?.LogInformation("Performance data exported to {FilePath}", filePath);
			}
			catch (Exception ex)
			{
				_logger?.LogError(ex, "Failed to export performance data");
				throw;
			}
		}

		/// <summary>
		/// Gets a performance summary suitable for display in UI.
		/// </summary>
		public static string GetPerformanceSummary()
		{
			if (PerformanceMonitor == null)
			{
				return "Performance monitor not available";
			}

			try
			{
				var report = PerformanceMonitor.GenerateReport();
				var avgLoadTime = report.AverageLoadTimeMs;
				var cacheHitRate = report.CacheHitRate;

				// Determine performance status
				string status;
				if (avgLoadTime < 50 && cacheHitRate > 0.7)
					status = "Excellent";
				else if (avgLoadTime < 100 && cacheHitRate > 0.5)
					status = "Good";
				else if (avgLoadTime < 200 && cacheHitRate > 0.3)
					status = "Fair";
				else
					status = "Poor";

				return $"Performance: {status} | " +
					   $"Avg Load: {avgLoadTime:F0}ms | " +
					   $"Cache Hit: {cacheHitRate:P0} | " +
					   $"Queue: {report.CurrentQueueDepth}";
			}
			catch
			{
				return "Error retrieving performance data";
			}
		}

		static ThumbnailPerformanceHelper()
		{
			try
			{
				_logger = Ioc.Default.GetService<ILogger>();
			}
			catch
			{
				// Logger might not be available
			}
		}
	}
}