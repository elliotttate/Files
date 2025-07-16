// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using Files.App.Services.Monitoring;
using Files.App.Services.Thumbnails;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Timers;
using Windows.Storage;
using Windows.Storage.Pickers;
using System.IO;

namespace Files.App.ViewModels.Debugging
{
	/// <summary>
	/// View model for the thumbnail performance debugging dashboard.
	/// </summary>
	public partial class ThumbnailPerformanceDashboardViewModel : ObservableObject, IDisposable
	{
		// Services
		private readonly IThumbnailPerformanceMonitor _performanceMonitor;
		private readonly IThumbnailLoadingQueue _thumbnailQueue;
		private readonly ILogger<ThumbnailPerformanceDashboardViewModel> _logger;

		// Update timer
		private readonly System.Timers.Timer _updateTimer;
		private bool _isDisposed;

		// Observable properties
		[ObservableProperty]
		private long _totalRequests;

		[ObservableProperty]
		private long _totalCompleted;

		[ObservableProperty]
		private long _totalCancelled;

		[ObservableProperty]
		private double _cacheHitRate;

		[ObservableProperty]
		private double _averageLoadTimeMs;

		[ObservableProperty]
		private int _currentQueueDepth;

		[ObservableProperty]
		private int _currentActiveRequests;

		[ObservableProperty]
		private int _peakQueueDepth;

		[ObservableProperty]
		private int _peakActiveRequests;

		[ObservableProperty]
		private double _peakMemoryUsageMB;

		[ObservableProperty]
		private string _sessionDuration = "00:00:00";

		[ObservableProperty]
		private double _requestsPerSecond;

		[ObservableProperty]
		private double _completionsPerSecond;

		[ObservableProperty]
		private string _queueStatus = "Idle";

		[ObservableProperty]
		private string _performanceSummary = string.Empty;

		// Load time distribution
		[ObservableProperty]
		private double _minLoadTimeMs;

		[ObservableProperty]
		private double _medianLoadTimeMs;

		[ObservableProperty]
		private double _p95LoadTimeMs;

		[ObservableProperty]
		private double _p99LoadTimeMs;

		[ObservableProperty]
		private double _maxLoadTimeMs;

		// Collections
		[ObservableProperty]
		private ObservableCollection<PerformanceChartPoint> _queueDepthHistory = new();

		[ObservableProperty]
		private ObservableCollection<PerformanceChartPoint> _loadTimeHistory = new();

		[ObservableProperty]
		private ObservableCollection<FileLoadMetricViewModel> _topLoadedFiles = new();

		// Configuration
		private const int MAX_CHART_POINTS = 60; // 1 minute of data at 1 second intervals
		private const int UPDATE_INTERVAL_MS = 1000;

		// Tracking
		private long _lastTotalRequests;
		private long _lastTotalCompleted;
		private DateTime _lastUpdateTime = DateTime.UtcNow;

		public ThumbnailPerformanceDashboardViewModel()
		{
			_performanceMonitor = Ioc.Default.GetRequiredService<IThumbnailPerformanceMonitor>();
			_thumbnailQueue = Ioc.Default.GetRequiredService<IThumbnailLoadingQueue>();
			_logger = Ioc.Default.GetRequiredService<ILogger<ThumbnailPerformanceDashboardViewModel>>();

			// Subscribe to queue events for real-time updates
			_thumbnailQueue.ProgressChanged += OnQueueProgressChanged;

			// Setup update timer
			_updateTimer = new System.Timers.Timer(UPDATE_INTERVAL_MS);
			_updateTimer.Elapsed += OnUpdateTimerElapsed;
			_updateTimer.Start();

			// Initial update
			UpdateMetrics();
		}

		private void OnQueueProgressChanged(object? sender, ThumbnailQueueProgressEventArgs e)
		{
			// Update real-time queue metrics
			CurrentQueueDepth = e.QueueDepth;
			CurrentActiveRequests = e.ActiveRequests;

			// Update queue status
			QueueStatus = e.ActiveRequests > 0 ? 
				$"Processing ({e.ActiveRequests} active)" : 
				e.QueueDepth > 0 ? 
				$"Queued ({e.QueueDepth} pending)" : 
				"Idle";
		}

		private void OnUpdateTimerElapsed(object? sender, ElapsedEventArgs e)
		{
			try
			{
				UpdateMetrics();
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error updating performance metrics");
			}
		}

		private void UpdateMetrics()
		{
			var report = _performanceMonitor.GenerateReport();

			// Update basic metrics
			TotalRequests = report.TotalRequests;
			TotalCompleted = report.TotalCompleted;
			TotalCancelled = report.TotalCancelled;
			CacheHitRate = report.CacheHitRate;
			AverageLoadTimeMs = report.AverageLoadTimeMs;
			PeakQueueDepth = report.PeakQueueDepth;
			PeakActiveRequests = report.PeakActiveRequests;
			PeakMemoryUsageMB = report.PeakMemoryUsageMB;
			SessionDuration = report.SessionDuration.ToString(@"hh\:mm\:ss");

			// Update load time distribution
			MinLoadTimeMs = report.MinLoadTimeMs;
			MedianLoadTimeMs = report.MedianLoadTimeMs;
			P95LoadTimeMs = report.P95LoadTimeMs;
			P99LoadTimeMs = report.P99LoadTimeMs;
			MaxLoadTimeMs = report.MaxLoadTimeMs;

			// Calculate rates
			var now = DateTime.UtcNow;
			var timeDelta = (now - _lastUpdateTime).TotalSeconds;
			if (timeDelta > 0)
			{
				RequestsPerSecond = (TotalRequests - _lastTotalRequests) / timeDelta;
				CompletionsPerSecond = (TotalCompleted - _lastTotalCompleted) / timeDelta;

				_lastTotalRequests = TotalRequests;
				_lastTotalCompleted = TotalCompleted;
				_lastUpdateTime = now;
			}

			// Update charts
			UpdateCharts(report);

			// Update top loaded files
			UpdateTopLoadedFiles(report);

			// Update performance summary
			UpdatePerformanceSummary(report);
		}

		private void UpdateCharts(ThumbnailPerformanceReport report)
		{
			var now = DateTime.Now;

			// Add queue depth point
			QueueDepthHistory.Add(new PerformanceChartPoint
			{
				Timestamp = now,
				Value = report.CurrentQueueDepth
			});

			// Add load time point
			LoadTimeHistory.Add(new PerformanceChartPoint
			{
				Timestamp = now,
				Value = report.AverageLoadTimeMs
			});

			// Maintain chart size
			while (QueueDepthHistory.Count > MAX_CHART_POINTS)
			{
				QueueDepthHistory.RemoveAt(0);
			}

			while (LoadTimeHistory.Count > MAX_CHART_POINTS)
			{
				LoadTimeHistory.RemoveAt(0);
			}
		}

		private void UpdateTopLoadedFiles(ThumbnailPerformanceReport report)
		{
			TopLoadedFiles.Clear();

			foreach (var metric in report.FileMetrics.Take(10))
			{
				TopLoadedFiles.Add(new FileLoadMetricViewModel
				{
					FileName = Path.GetFileName(metric.Path),
					FullPath = metric.Path,
					LoadCount = metric.LoadCount,
					SuccessRate = metric.LoadCount > 0 ? 
						(double)metric.SuccessCount / metric.LoadCount : 0,
					AverageLoadTimeMs = metric.AverageLoadTimeMs,
					EstimatedMemoryMB = metric.EstimatedMemoryMB
				});
			}
		}

		private void UpdatePerformanceSummary(ThumbnailPerformanceReport report)
		{
			var summary = new System.Text.StringBuilder();

			// Performance grade
			var grade = CalculatePerformanceGrade(report);
			summary.AppendLine($"Performance Grade: {grade}");
			summary.AppendLine();

			// Key insights
			if (report.CacheHitRate < 0.5)
			{
				summary.AppendLine("⚠️ Low cache hit rate - consider increasing cache size");
			}

			if (report.AverageLoadTimeMs > 100)
			{
				summary.AppendLine("⚠️ High average load time - check for I/O bottlenecks");
			}

			if (report.PeakQueueDepth > 500)
			{
				summary.AppendLine("⚠️ Large queue buildup detected - may need optimization");
			}

			if (report.QueueDepthTrend > 0.5)
			{
				summary.AppendLine("📈 Queue depth is increasing - monitor for issues");
			}

			var cancelRate = report.TotalRequests > 0 ? 
				(double)report.TotalCancelled / report.TotalRequests : 0;
			if (cancelRate > 0.2)
			{
				summary.AppendLine($"⚠️ High cancellation rate ({cancelRate:P}) - check viewport logic");
			}

			// Recommendations
			summary.AppendLine();
			summary.AppendLine("Recommendations:");

			if (report.P99LoadTimeMs > report.MedianLoadTimeMs * 10)
			{
				summary.AppendLine("• Investigate slow outliers (P99 >> median)");
			}

			if (report.PeakMemoryUsageMB > 500)
			{
				summary.AppendLine("• Consider implementing memory pressure handling");
			}

			PerformanceSummary = summary.ToString();
		}

		private string CalculatePerformanceGrade(ThumbnailPerformanceReport report)
		{
			var score = 100.0;

			// Deduct points for performance issues
			if (report.CacheHitRate < 0.7) score -= 10;
			if (report.CacheHitRate < 0.5) score -= 10;
			if (report.AverageLoadTimeMs > 50) score -= 10;
			if (report.AverageLoadTimeMs > 100) score -= 10;
			if (report.P99LoadTimeMs > 500) score -= 10;
			if (report.PeakQueueDepth > 500) score -= 10;

			var cancelRate = report.TotalRequests > 0 ? 
				(double)report.TotalCancelled / report.TotalRequests : 0;
			if (cancelRate > 0.1) score -= 5;
			if (cancelRate > 0.2) score -= 10;

			return score switch
			{
				>= 90 => "A - Excellent",
				>= 80 => "B - Good",
				>= 70 => "C - Fair",
				>= 60 => "D - Poor",
				_ => "F - Critical"
			};
		}

		#region Commands

		[RelayCommand]
		private async Task ExportReportAsync()
		{
			try
			{
				var picker = new FileSavePicker
				{
					SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
					SuggestedFileName = $"ThumbnailPerformance_{DateTime.Now:yyyyMMdd_HHmmss}"
				};

				picker.FileTypeChoices.Add("JSON", new[] { ".json" });
				picker.FileTypeChoices.Add("CSV", new[] { ".csv" });
				picker.FileTypeChoices.Add("Markdown", new[] { ".md" });

				var file = await picker.PickSaveFileAsync();
				if (file != null)
				{
					var format = Path.GetExtension(file.Path).ToLower() switch
					{
						".json" => ExportFormat.Json,
						".csv" => ExportFormat.Csv,
						".md" => ExportFormat.Markdown,
						_ => ExportFormat.Json
					};

					var data = _performanceMonitor.ExportPerformanceData(format);
					await FileIO.WriteTextAsync(file, data);

					_logger.LogInformation("Performance report exported to {Path}", file.Path);
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to export performance report");
			}
		}

		[RelayCommand]
		private void ResetMetrics()
		{
			// Note: This would require adding a reset method to the monitor
			_logger.LogInformation("Metrics reset requested");
		}

		[RelayCommand]
		private void ClearQueueHistory()
		{
			QueueDepthHistory.Clear();
			LoadTimeHistory.Clear();
		}

		#endregion

		#region IDisposable

		public void Dispose()
		{
			if (_isDisposed) return;

			_updateTimer?.Stop();
			_updateTimer?.Dispose();

			_thumbnailQueue.ProgressChanged -= OnQueueProgressChanged;

			_isDisposed = true;
		}

		#endregion
	}

	/// <summary>
	/// Represents a point on a performance chart.
	/// </summary>
	public class PerformanceChartPoint
	{
		public DateTime Timestamp { get; set; }
		public double Value { get; set; }
	}

	/// <summary>
	/// View model for individual file load metrics.
	/// </summary>
	public partial class FileLoadMetricViewModel : ObservableObject
	{
		[ObservableProperty]
		private string _fileName = string.Empty;

		[ObservableProperty]
		private string _fullPath = string.Empty;

		[ObservableProperty]
		private int _loadCount;

		[ObservableProperty]
		private double _successRate;

		[ObservableProperty]
		private double _averageLoadTimeMs;

		[ObservableProperty]
		private double _estimatedMemoryMB;

		public string SuccessRateFormatted => $"{SuccessRate:P0}";
		public string LoadTimeFormatted => $"{AverageLoadTimeMs:F1} ms";
		public string MemoryFormatted => $"{EstimatedMemoryMB:F1} MB";
	}
}