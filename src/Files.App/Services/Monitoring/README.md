# Thumbnail Performance Monitoring System

This directory contains the performance monitoring infrastructure for the thumbnail loading system in Files.

## Overview

The performance monitoring system tracks and analyzes thumbnail loading operations to help identify bottlenecks, optimize performance, and ensure a smooth user experience.

## Components

### 1. ThumbnailPerformanceMonitor
The core monitoring service that tracks:
- Load times (min, max, average, median, P95, P99)
- Cache hit rates
- Queue depth and processing metrics
- Memory usage
- Cancellation rates
- Per-file loading statistics

### 2. ThumbnailPerformanceDashboard
A debugging UI that displays real-time performance metrics:
- Key performance indicators
- Load time distribution charts
- Queue depth visualization
- Top loaded files
- Performance analysis and recommendations

### 3. Integration Points
- **IThumbnailLoadingQueue**: Emits events for monitoring
- **IFileModelCacheService**: Tracks cache hits/misses
- **ThumbnailPerformanceHelper**: Utility methods for debugging

## Usage

### Accessing the Dashboard

```csharp
// Show performance dashboard in a dialog
await ThumbnailPerformanceExtensions.ShowPerformanceDashboardAsync();
```

### Programmatic Access

```csharp
// Get performance monitor
var monitor = Ioc.Default.GetService<IThumbnailPerformanceMonitor>();

// Generate report
var report = monitor.GenerateReport();

// Export data
var jsonData = monitor.ExportPerformanceData(ExportFormat.Json);
var csvData = monitor.ExportPerformanceData(ExportFormat.Csv);
var markdownData = monitor.ExportPerformanceData(ExportFormat.Markdown);
```

### Quick Performance Check

```csharp
// Get summary string
var summary = ThumbnailPerformanceExtensions.GetPerformanceSummary();
// Output: "Thumbnails: 1,234 loaded, 85% cache hit, 45ms avg"
```

## Metrics Tracked

### Load Time Metrics
- **Average Load Time**: Mean time to load a thumbnail
- **Min/Max Load Time**: Fastest and slowest load times
- **Median Load Time**: Middle value of all load times
- **P95/P99 Load Time**: 95th and 99th percentile load times

### Queue Metrics
- **Current Queue Depth**: Number of pending requests
- **Peak Queue Depth**: Maximum queue size reached
- **Active Requests**: Currently processing thumbnails
- **Queue Depth Trend**: Increasing/decreasing/stable

### Cache Metrics
- **Cache Hit Rate**: Percentage of requests served from cache
- **Total Cache Hits/Misses**: Absolute numbers
- **Memory Usage**: Estimated memory consumed by thumbnails

### Reliability Metrics
- **Total Requests**: All thumbnail load requests
- **Completed**: Successfully loaded thumbnails
- **Cancelled**: User-cancelled operations
- **Failed**: Load failures with error tracking

## Performance Grades

The system assigns performance grades based on multiple factors:

- **A (Excellent)**: <50ms avg load, >70% cache hit rate
- **B (Good)**: <100ms avg load, >50% cache hit rate
- **C (Fair)**: <200ms avg load, >30% cache hit rate
- **D (Poor)**: Higher load times or lower cache rates
- **F (Critical)**: Severe performance issues detected

## Export Formats

### JSON
Complete structured data suitable for analysis tools.

### CSV
Tabular format for spreadsheet analysis:
- Summary metrics
- Load time distribution
- Per-file statistics

### Markdown
Human-readable report with:
- Summary statistics tables
- Performance analysis
- Top loaded files
- Recommendations

## Integration with DevTools

The performance monitor can be accessed through:
1. Developer settings page (when implemented)
2. Debug console commands
3. Programmatic API calls

## Best Practices

1. **Monitor in Production**: Enable monitoring to track real-world performance
2. **Regular Exports**: Export data periodically for trend analysis
3. **Act on Recommendations**: The system provides actionable insights
4. **Memory Pressure**: Monitor peak memory usage to prevent issues

## Future Enhancements

- [ ] Real-time graphing of metrics
- [ ] Historical trend analysis
- [ ] Automated performance regression detection
- [ ] Integration with Windows Performance Counters
- [ ] Network vs local file performance comparison