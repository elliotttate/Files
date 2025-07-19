// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Files.App.Utils.Diagnostics
{
	/// <summary>
	/// Detects potential deadlocks by tracking long-running operations
	/// </summary>
	public static class DeadlockDetector
	{
		private static readonly ConcurrentDictionary<string, OperationTracker> _activeOperations = new();
		private static readonly Timer _checkTimer;
		private static readonly ILogger _logger = App.Logger;
		private static readonly TimeSpan _warningThreshold = TimeSpan.FromSeconds(10);
		private static readonly TimeSpan _errorThreshold = TimeSpan.FromSeconds(30);
		
		static DeadlockDetector()
		{
			// Check for stuck operations every 5 seconds
			_checkTimer = new Timer(CheckForStuckOperations, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
		}
		
		private class OperationTracker
		{
			public string Name { get; set; }
			public DateTime StartTime { get; set; }
			public int ThreadId { get; set; }
			public string StackTrace { get; set; }
		}
		
		/// <summary>
		/// Tracks the start of an operation
		/// </summary>
		public static IDisposable TrackOperation(string operationName)
		{
			var key = $"{operationName}_{Guid.NewGuid():N}";
			var tracker = new OperationTracker
			{
				Name = operationName,
				StartTime = DateTime.UtcNow,
				ThreadId = Thread.CurrentThread.ManagedThreadId,
				StackTrace = Environment.StackTrace
			};
			
			_activeOperations[key] = tracker;
			
			return new OperationScope(key);
		}
		
		private class OperationScope : IDisposable
		{
			private readonly string _key;
			private bool _disposed;
			
			public OperationScope(string key)
			{
				_key = key;
			}
			
			public void Dispose()
			{
				if (!_disposed)
				{
					_disposed = true;
					_activeOperations.TryRemove(_key, out _);
				}
			}
		}
		
		private static void CheckForStuckOperations(object state)
		{
			try
			{
				var now = DateTime.UtcNow;
				var stuckOperations = new List<string>();
				
				foreach (var kvp in _activeOperations)
				{
					var duration = now - kvp.Value.StartTime;
					
					if (duration > _errorThreshold)
					{
						stuckOperations.Add($"{kvp.Value.Name} (Thread: {kvp.Value.ThreadId}, Duration: {duration.TotalSeconds:F1}s)");
						
						_logger?.LogError(
							"[DEADLOCK-DETECTOR] Operation stuck for {Duration}s: {Operation} on thread {ThreadId}\nStack:\n{Stack}",
							duration.TotalSeconds,
							kvp.Value.Name,
							kvp.Value.ThreadId,
							kvp.Value.StackTrace);
					}
					else if (duration > _warningThreshold)
					{
						System.Diagnostics.Debug.WriteLine(
							$"[DEADLOCK-WARNING] Operation running for {duration.TotalSeconds:F1}s: {kvp.Value.Name} on thread {kvp.Value.ThreadId}");
					}
				}
				
				if (stuckOperations.Count > 0)
				{
					_logger?.LogError("[DEADLOCK-DETECTOR] {Count} operations are stuck: {Operations}", 
						stuckOperations.Count, string.Join(", ", stuckOperations));
					
					// Force resource report
					ResourceMonitor.ReportResourceUsage("DEADLOCK-DETECTED");
					ResourceMonitor.ReportThreadPoolStatus("DEADLOCK-DETECTED");
				}
			}
			catch (Exception ex)
			{
				_logger?.LogError(ex, "Error in deadlock detector");
			}
		}
	}
}