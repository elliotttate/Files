// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Services.Monitoring
{
	/// <summary>
	/// Interface for monitoring and tracking performance metrics for thumbnail loading operations.
	/// </summary>
	public interface IThumbnailPerformanceMonitor : IDisposable
	{
		/// <summary>
		/// Gets the average load time in milliseconds for completed thumbnail loads.
		/// </summary>
		double GetAverageLoadTime();

		/// <summary>
		/// Calculates the cache hit rate as a percentage between 0 and 1.
		/// </summary>
		double CalculateCacheHitRate();

		/// <summary>
		/// Generates a comprehensive performance report with all collected metrics.
		/// </summary>
		ThumbnailPerformanceReport GenerateReport();

		/// <summary>
		/// Exports performance data in the specified format.
		/// </summary>
		/// <param name="format">The export format (JSON, CSV, or Markdown)</param>
		/// <returns>The exported data as a string</returns>
		string ExportPerformanceData(ExportFormat format = ExportFormat.Json);
	}
}