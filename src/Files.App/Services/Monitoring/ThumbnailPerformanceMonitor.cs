// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.Mvvm.DependencyInjection;
using Files.App.Services.Caching;
using Files.App.Services.Thumbnails;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.IO;

namespace Files.App.Services.Monitoring
{
	/// <summary>
	/// Export format options for performance data.
	/// </summary>
	public enum ExportFormat
	{
		Json,
		Csv,
		Markdown
	}

	/// <summary>
	/// Monitors and tracks performance metrics for thumbnail loading operations.
	/// </summary>
	public sealed class ThumbnailPerformanceMonitor : IThumbnailPerformanceMonitor
	{
		// Services
		private readonly IThumbnailLoadingQueue _thumbnailQueue;
		private readonly IFileModelCacheService _cacheService;
		private readonly ILogger<ThumbnailPerformanceMonitor> _logger;

		// Metrics storage
		private readonly ConcurrentDictionary<string, ThumbnailLoadMetrics> _loadMetrics = new();
		private readonly ConcurrentQueue<ThumbnailLoadEvent> _recentLoadEvents = new();
		private readonly ConcurrentQueue<QueueDepthSnapshot> _queueDepthHistory = new();
		private readonly object _metricsLock = new();

		// Performance counters
		private long _totalLoadsRequested;
		private long _totalLoadsCompleted;
		private long _totalLoadsCancelled;
		private long _totalCacheHits;
		private long _totalCacheMisses;
		private long _totalBytesLoaded;
		private long _totalLoadTimeMs;
		private long _peakMemoryUsage;
		private int _peakQueueDepth;
		private int _peakActiveRequests;

		// Configuration
		private const int MAX_RECENT_EVENTS = 1000;
		private const int MAX_QUEUE_DEPTH_HISTORY = 500;
		private const int METRICS_SNAPSHOT_INTERVAL_MS = 1000;

		// Monitoring state
		private readonly Timer _metricsSnapshotTimer;
		private readonly Stopwatch _sessionStopwatch;
		private bool _isDisposed;

		public ThumbnailPerformanceMonitor()
		{
			_thumbnailQueue = Ioc.Default.GetRequiredService<IThumbnailLoadingQueue>();
			_cacheService = Ioc.Default.GetRequiredService<IFileModelCacheService>();
			_logger = Ioc.Default.GetRequiredService<ILogger<ThumbnailPerformanceMonitor>>();

			_sessionStopwatch = Stopwatch.StartNew();

			// Subscribe to events
			_thumbnailQueue.ThumbnailLoaded += OnThumbnailLoaded;
			_thumbnailQueue.BatchCompleted += OnBatchCompleted;
			_thumbnailQueue.ProgressChanged += OnProgressChanged;
			_cacheService.ThumbnailLoaded += OnCacheThumbnailLoaded;

			// Start metrics snapshot timer
			_metricsSnapshotTimer = new Timer(
				TakeMetricsSnapshot,
				null,
				TimeSpan.FromMilliseconds(METRICS_SNAPSHOT_INTERVAL_MS),
				TimeSpan.FromMilliseconds(METRICS_SNAPSHOT_INTERVAL_MS));

			_logger.LogInformation("ThumbnailPerformanceMonitor started");
		}

		#region Event Handlers

		private void OnThumbnailLoaded(object? sender, ThumbnailLoadedEventArgs e)
		{
			RecordLoadEvent(new ThumbnailLoadEvent
			{
				Path = e.Path,
				Timestamp = DateTime.UtcNow,
				LoadTimeMs = 0, // Will be updated from batch event
				Success = true,
				WasCancelled = false,
				Source = LoadSource.Queue
			});
		}

		private void OnBatchCompleted(object? sender, BatchThumbnailLoadedEventArgs e)
		{
			foreach (var result in e.Results)
			{
				var loadEvent = new ThumbnailLoadEvent
				{
					Path = result.Path,
					Timestamp = DateTime.UtcNow,
					LoadTimeMs = result.LoadTime.TotalMilliseconds,
					Success = result.Success,
					WasCancelled = result.WasCancelled,
					Source = LoadSource.Queue,
					ErrorMessage = result.ErrorMessage
				};

				RecordLoadEvent(loadEvent);
				UpdateLoadMetrics(result);
			}

			// Update batch statistics
			lock (_metricsLock)
			{
				_totalLoadsCompleted += e.TotalRequests;
				_totalLoadsCancelled += e.Results.Count(r => r.WasCancelled);
			}
		}

		private void OnProgressChanged(object? sender, ThumbnailQueueProgressEventArgs e)
		{
			// Update peak values
			lock (_metricsLock)
			{
				if (e.QueueDepth > _peakQueueDepth)
					_peakQueueDepth = e.QueueDepth;

				if (e.ActiveRequests > _peakActiveRequests)
					_peakActiveRequests = e.ActiveRequests;
			}

			// Record queue depth history
			var snapshot = new QueueDepthSnapshot
			{
				Timestamp = DateTime.UtcNow,
				QueueDepth = e.QueueDepth,
				ActiveRequests = e.ActiveRequests,
				ProcessedCount = e.ProcessedCount,
				AverageLoadTimeMs = e.AverageLoadTimeMs
			};

			_queueDepthHistory.Enqueue(snapshot);

			// Maintain history size
			while (_queueDepthHistory.Count > MAX_QUEUE_DEPTH_HISTORY)
			{
				_queueDepthHistory.TryDequeue(out _);
			}
		}

		private void OnCacheThumbnailLoaded(object? sender, ThumbnailLoadedEventArgs e)
		{
			Interlocked.Increment(ref _totalCacheHits);

			RecordLoadEvent(new ThumbnailLoadEvent
			{
				Path = e.Path,
				Timestamp = DateTime.UtcNow,
				LoadTimeMs = 0,
				Success = true,
				WasCancelled = false,
				Source = LoadSource.Cache
			});
		}

		#endregion

		#region Metrics Recording

		private void RecordLoadEvent(ThumbnailLoadEvent loadEvent)
		{
			// Add to recent events
			_recentLoadEvents.Enqueue(loadEvent);

			// Maintain queue size
			while (_recentLoadEvents.Count > MAX_RECENT_EVENTS)
			{
				_recentLoadEvents.TryDequeue(out _);
			}

			// Update request counter
			if (loadEvent.Source == LoadSource.Queue)
			{
				Interlocked.Increment(ref _totalLoadsRequested);

				if (!loadEvent.Success && !loadEvent.WasCancelled)
				{
					_logger.LogWarning("Thumbnail load failed for {Path}: {Error}", 
						loadEvent.Path, loadEvent.ErrorMessage);
				}
			}
		}

		private void UpdateLoadMetrics(ThumbnailLoadResult result)
		{
			var metrics = _loadMetrics.AddOrUpdate(result.Path,
				path => new ThumbnailLoadMetrics { Path = path },
				(path, existing) => existing);

			metrics.LoadCount++;
			metrics.TotalLoadTimeMs += result.LoadTime.TotalMilliseconds;
			metrics.LastLoadTime = DateTime.UtcNow;
			metrics.LastLoadSuccess = result.Success;

			if (result.Success)
			{
				metrics.SuccessCount++;
				if (result.Thumbnail != null)
				{
					// Estimate memory usage (rough approximation)
					var pixelCount = (result.Thumbnail.PixelWidth * result.Thumbnail.PixelHeight);
					var estimatedBytes = pixelCount * 4; // 4 bytes per pixel (RGBA)
					metrics.EstimatedMemoryBytes = estimatedBytes;
					Interlocked.Add(ref _totalBytesLoaded, estimatedBytes);
				}
			}
			else if (result.WasCancelled)
			{
				metrics.CancelCount++;
			}
			else
			{
				metrics.FailureCount++;
			}

			Interlocked.Add(ref _totalLoadTimeMs, (long)result.LoadTime.TotalMilliseconds);
		}

		private void TakeMetricsSnapshot(object? state)
		{
			try
			{
				// Get current memory usage
				using var process = Process.GetCurrentProcess();
				var currentMemory = process.WorkingSet64;

				lock (_metricsLock)
				{
					if (currentMemory > _peakMemoryUsage)
						_peakMemoryUsage = currentMemory;
				}

				// Calculate cache metrics
				var cacheSize = _cacheService.GetCacheSizeInBytes();
				var cacheHitRate = CalculateCacheHitRate();

				// Log periodic summary
				if (_sessionStopwatch.Elapsed.TotalSeconds % 30 < 1) // Every 30 seconds
				{
					_logger.LogInformation(
						"Thumbnail Performance: Requests={Requests}, Completed={Completed}, " +
						"CacheHitRate={HitRate:P}, AvgLoadTime={AvgTime:F1}ms, " +
						"QueueDepth={Queue}, Active={Active}",
						_totalLoadsRequested,
						_totalLoadsCompleted,
						cacheHitRate,
						GetAverageLoadTime(),
						_thumbnailQueue.QueueDepth,
						_thumbnailQueue.ActiveRequests);
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error taking metrics snapshot");
			}
		}

		#endregion

		#region Metrics Calculation

		public double GetAverageLoadTime()
		{
			var completed = Interlocked.Read(ref _totalLoadsCompleted);
			if (completed == 0) return 0;

			var totalTime = Interlocked.Read(ref _totalLoadTimeMs);
			return totalTime / (double)completed;
		}

		public double CalculateCacheHitRate()
		{
			var hits = Interlocked.Read(ref _totalCacheHits);
			var misses = Interlocked.Read(ref _totalCacheMisses);
			var total = hits + misses;

			return total > 0 ? hits / (double)total : 0;
		}

		public ThumbnailPerformanceReport GenerateReport()
		{
			var report = new ThumbnailPerformanceReport
			{
				SessionDuration = _sessionStopwatch.Elapsed,
				TotalRequests = Interlocked.Read(ref _totalLoadsRequested),
				TotalCompleted = Interlocked.Read(ref _totalLoadsCompleted),
				TotalCancelled = Interlocked.Read(ref _totalLoadsCancelled),
				TotalCacheHits = Interlocked.Read(ref _totalCacheHits),
				TotalCacheMisses = Interlocked.Read(ref _totalCacheMisses),
				TotalBytesLoaded = Interlocked.Read(ref _totalBytesLoaded),
				AverageLoadTimeMs = GetAverageLoadTime(),
				CacheHitRate = CalculateCacheHitRate(),
				PeakQueueDepth = _peakQueueDepth,
				PeakActiveRequests = _peakActiveRequests,
				PeakMemoryUsageMB = _peakMemoryUsage / (1024.0 * 1024.0),
				CurrentQueueDepth = _thumbnailQueue.QueueDepth,
				CurrentActiveRequests = _thumbnailQueue.ActiveRequests
			};

			// Add load time distribution
			var recentEvents = _recentLoadEvents.ToArray();
			if (recentEvents.Length > 0)
			{
				var loadTimes = recentEvents
					.Where(e => e.Success && e.LoadTimeMs > 0)
					.Select(e => e.LoadTimeMs)
					.OrderBy(t => t)
					.ToArray();

				if (loadTimes.Length > 0)
				{
					report.MinLoadTimeMs = loadTimes.First();
					report.MaxLoadTimeMs = loadTimes.Last();
					report.MedianLoadTimeMs = GetMedian(loadTimes);
					report.P95LoadTimeMs = GetPercentile(loadTimes, 0.95);
					report.P99LoadTimeMs = GetPercentile(loadTimes, 0.99);
				}
			}

			// Add queue depth trends
			var queueHistory = _queueDepthHistory.ToArray();
			if (queueHistory.Length > 0)
			{
				report.AverageQueueDepth = queueHistory.Average(s => s.QueueDepth);
				report.QueueDepthTrend = CalculateTrend(queueHistory.Select(s => (double)s.QueueDepth).ToArray());
			}

			// Add per-file metrics
			report.FileMetrics = _loadMetrics.Values
				.OrderByDescending(m => m.LoadCount)
				.Take(100)
				.Select(m => new FileLoadMetrics
				{
					Path = m.Path,
					LoadCount = m.LoadCount,
					SuccessCount = m.SuccessCount,
					FailureCount = m.FailureCount,
					CancelCount = m.CancelCount,
					AverageLoadTimeMs = m.LoadCount > 0 ? m.TotalLoadTimeMs / m.LoadCount : 0,
					EstimatedMemoryMB = m.EstimatedMemoryBytes / (1024.0 * 1024.0)
				})
				.ToList();

			return report;
		}

		public string ExportPerformanceData(ExportFormat format = ExportFormat.Json)
		{
			var report = GenerateReport();

			return format switch
			{
				ExportFormat.Json => System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions
				{
					WriteIndented = true
				}),
				ExportFormat.Csv => ExportToCsv(report),
				ExportFormat.Markdown => ExportToMarkdown(report),
				_ => throw new ArgumentException($"Unsupported export format: {format}")
			};
		}

		#endregion

		#region Export Helpers

		private string ExportToCsv(ThumbnailPerformanceReport report)
		{
			var sb = new StringBuilder();

			// Summary section
			sb.AppendLine("Metric,Value");
			sb.AppendLine($"Session Duration,{report.SessionDuration}");
			sb.AppendLine($"Total Requests,{report.TotalRequests}");
			sb.AppendLine($"Total Completed,{report.TotalCompleted}");
			sb.AppendLine($"Total Cancelled,{report.TotalCancelled}");
			sb.AppendLine($"Cache Hit Rate,{report.CacheHitRate:P}");
			sb.AppendLine($"Average Load Time (ms),{report.AverageLoadTimeMs:F2}");
			sb.AppendLine($"Peak Queue Depth,{report.PeakQueueDepth}");
			sb.AppendLine($"Peak Memory Usage (MB),{report.PeakMemoryUsageMB:F2}");
			sb.AppendLine();

			// Load time distribution
			sb.AppendLine("Load Time Distribution");
			sb.AppendLine("Percentile,Time (ms)");
			sb.AppendLine($"Min,{report.MinLoadTimeMs:F2}");
			sb.AppendLine($"Median,{report.MedianLoadTimeMs:F2}");
			sb.AppendLine($"P95,{report.P95LoadTimeMs:F2}");
			sb.AppendLine($"P99,{report.P99LoadTimeMs:F2}");
			sb.AppendLine($"Max,{report.MaxLoadTimeMs:F2}");
			sb.AppendLine();

			// Per-file metrics
			if (report.FileMetrics.Any())
			{
				sb.AppendLine("Per-File Metrics");
				sb.AppendLine("Path,Load Count,Success Count,Failure Count,Cancel Count,Avg Load Time (ms),Est Memory (MB)");
				foreach (var metric in report.FileMetrics)
				{
					sb.AppendLine($"\"{metric.Path}\",{metric.LoadCount},{metric.SuccessCount}," +
						$"{metric.FailureCount},{metric.CancelCount},{metric.AverageLoadTimeMs:F2},{metric.EstimatedMemoryMB:F2}");
				}
			}

			return sb.ToString();
		}

		private string ExportToMarkdown(ThumbnailPerformanceReport report)
		{
			var sb = new StringBuilder();

			sb.AppendLine("# Thumbnail Performance Report");
			sb.AppendLine();
			sb.AppendLine($"**Generated:** {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
			sb.AppendLine($"**Session Duration:** {report.SessionDuration}");
			sb.AppendLine();

			sb.AppendLine("## Summary Statistics");
			sb.AppendLine();
			sb.AppendLine("| Metric | Value |");
			sb.AppendLine("|--------|--------|");
			sb.AppendLine($"| Total Requests | {report.TotalRequests:N0} |");
			sb.AppendLine($"| Total Completed | {report.TotalCompleted:N0} |");
			sb.AppendLine($"| Total Cancelled | {report.TotalCancelled:N0} |");
			sb.AppendLine($"| Cache Hit Rate | {report.CacheHitRate:P} |");
			sb.AppendLine($"| Average Load Time | {report.AverageLoadTimeMs:F2} ms |");
			sb.AppendLine($"| Peak Queue Depth | {report.PeakQueueDepth} |");
			sb.AppendLine($"| Peak Active Requests | {report.PeakActiveRequests} |");
			sb.AppendLine($"| Peak Memory Usage | {report.PeakMemoryUsageMB:F2} MB |");
			sb.AppendLine();

			sb.AppendLine("## Load Time Distribution");
			sb.AppendLine();
			sb.AppendLine("| Percentile | Time (ms) |");
			sb.AppendLine("|-----------|-----------|");
			sb.AppendLine($"| Min | {report.MinLoadTimeMs:F2} |");
			sb.AppendLine($"| Median | {report.MedianLoadTimeMs:F2} |");
			sb.AppendLine($"| P95 | {report.P95LoadTimeMs:F2} |");
			sb.AppendLine($"| P99 | {report.P99LoadTimeMs:F2} |");
			sb.AppendLine($"| Max | {report.MaxLoadTimeMs:F2} |");
			sb.AppendLine();

			sb.AppendLine("## Queue Performance");
			sb.AppendLine();
			sb.AppendLine($"- **Current Queue Depth:** {report.CurrentQueueDepth}");
			sb.AppendLine($"- **Current Active Requests:** {report.CurrentActiveRequests}");
			sb.AppendLine($"- **Average Queue Depth:** {report.AverageQueueDepth:F2}");
			sb.AppendLine($"- **Queue Depth Trend:** {(report.QueueDepthTrend > 0 ? "Increasing" : report.QueueDepthTrend < 0 ? "Decreasing" : "Stable")}");
			sb.AppendLine();

			if (report.FileMetrics.Any())
			{
				sb.AppendLine("## Top Files by Load Count");
				sb.AppendLine();
				sb.AppendLine("| File | Loads | Success | Failed | Cancelled | Avg Time (ms) | Memory (MB) |");
				sb.AppendLine("|------|-------|---------|--------|-----------|---------------|-------------|");

				foreach (var metric in report.FileMetrics.Take(20))
				{
					var fileName = Path.GetFileName(metric.Path);
					sb.AppendLine($"| {fileName} | {metric.LoadCount} | {metric.SuccessCount} | " +
						$"{metric.FailureCount} | {metric.CancelCount} | {metric.AverageLoadTimeMs:F2} | {metric.EstimatedMemoryMB:F2} |");
				}
			}

			return sb.ToString();
		}

		#endregion

		#region Statistical Helpers

		private double GetMedian(double[] sortedValues)
		{
			if (sortedValues.Length == 0) return 0;

			int middle = sortedValues.Length / 2;
			if (sortedValues.Length % 2 == 0)
			{
				return (sortedValues[middle - 1] + sortedValues[middle]) / 2.0;
			}
			return sortedValues[middle];
		}

		private double GetPercentile(double[] sortedValues, double percentile)
		{
			if (sortedValues.Length == 0) return 0;

			var index = (int)Math.Ceiling(percentile * sortedValues.Length) - 1;
			return sortedValues[Math.Max(0, Math.Min(index, sortedValues.Length - 1))];
		}

		private double CalculateTrend(double[] values)
		{
			if (values.Length < 2) return 0;

			// Simple linear regression
			var n = values.Length;
			var sumX = 0.0;
			var sumY = 0.0;
			var sumXY = 0.0;
			var sumX2 = 0.0;

			for (int i = 0; i < n; i++)
			{
				sumX += i;
				sumY += values[i];
				sumXY += i * values[i];
				sumX2 += i * i;
			}

			var slope = (n * sumXY - sumX * sumY) / (n * sumX2 - sumX * sumX);
			return slope;
		}

		#endregion

		#region IDisposable

		public void Dispose()
		{
			if (_isDisposed) return;

			// Unsubscribe from events
			_thumbnailQueue.ThumbnailLoaded -= OnThumbnailLoaded;
			_thumbnailQueue.BatchCompleted -= OnBatchCompleted;
			_thumbnailQueue.ProgressChanged -= OnProgressChanged;
			_cacheService.ThumbnailLoaded -= OnCacheThumbnailLoaded;

			// Stop timers
			_metricsSnapshotTimer?.Dispose();

			// Log final report
			try
			{
				var finalReport = GenerateReport();
				_logger.LogInformation("Final Performance Report: {Report}", 
					System.Text.Json.JsonSerializer.Serialize(finalReport));
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error generating final performance report");
			}

			_isDisposed = true;
		}

		#endregion

		#region Supporting Types

		private class ThumbnailLoadMetrics
		{
			public string Path { get; set; } = string.Empty;
			public int LoadCount { get; set; }
			public int SuccessCount { get; set; }
			public int FailureCount { get; set; }
			public int CancelCount { get; set; }
			public double TotalLoadTimeMs { get; set; }
			public DateTime LastLoadTime { get; set; }
			public bool LastLoadSuccess { get; set; }
			public long EstimatedMemoryBytes { get; set; }
		}

		private class ThumbnailLoadEvent
		{
			public string Path { get; set; } = string.Empty;
			public DateTime Timestamp { get; set; }
			public double LoadTimeMs { get; set; }
			public bool Success { get; set; }
			public bool WasCancelled { get; set; }
			public LoadSource Source { get; set; }
			public string? ErrorMessage { get; set; }
		}

		private class QueueDepthSnapshot
		{
			public DateTime Timestamp { get; set; }
			public int QueueDepth { get; set; }
			public int ActiveRequests { get; set; }
			public int ProcessedCount { get; set; }
			public double AverageLoadTimeMs { get; set; }
		}

		private enum LoadSource
		{
			Queue,
			Cache
		}


		#endregion
	}

	/// <summary>
	/// Comprehensive performance report for thumbnail loading operations.
	/// </summary>
	public class ThumbnailPerformanceReport
	{
		public TimeSpan SessionDuration { get; set; }
		public long TotalRequests { get; set; }
		public long TotalCompleted { get; set; }
		public long TotalCancelled { get; set; }
		public long TotalCacheHits { get; set; }
		public long TotalCacheMisses { get; set; }
		public long TotalBytesLoaded { get; set; }
		public double AverageLoadTimeMs { get; set; }
		public double CacheHitRate { get; set; }
		public int PeakQueueDepth { get; set; }
		public int PeakActiveRequests { get; set; }
		public double PeakMemoryUsageMB { get; set; }
		public int CurrentQueueDepth { get; set; }
		public int CurrentActiveRequests { get; set; }
		public double MinLoadTimeMs { get; set; }
		public double MaxLoadTimeMs { get; set; }
		public double MedianLoadTimeMs { get; set; }
		public double P95LoadTimeMs { get; set; }
		public double P99LoadTimeMs { get; set; }
		public double AverageQueueDepth { get; set; }
		public double QueueDepthTrend { get; set; }
		public List<FileLoadMetrics> FileMetrics { get; set; } = new();
	}

	/// <summary>
	/// Performance metrics for individual files.
	/// </summary>
	public class FileLoadMetrics
	{
		public string Path { get; set; } = string.Empty;
		public int LoadCount { get; set; }
		public int SuccessCount { get; set; }
		public int FailureCount { get; set; }
		public int CancelCount { get; set; }
		public double AverageLoadTimeMs { get; set; }
		public double EstimatedMemoryMB { get; set; }
	}
}