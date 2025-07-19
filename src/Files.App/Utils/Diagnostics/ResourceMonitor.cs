// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Files.App.Utils.Diagnostics
{
	/// <summary>
	/// Monitors system resources to help diagnose resource exhaustion issues
	/// </summary>
	public static class ResourceMonitor
	{
		private static readonly ILogger _logger = App.Logger;
		private static DateTime _lastReport = DateTime.MinValue;
		private static readonly TimeSpan _reportInterval = TimeSpan.FromSeconds(30);
		
		[DllImport("kernel32.dll")]
		private static extern bool GetProcessHandleCount(IntPtr hProcess, out uint handleCount);
		
		[DllImport("kernel32.dll")]
		private static extern IntPtr GetCurrentProcess();

		/// <summary>
		/// Reports current resource usage
		/// </summary>
		public static void ReportResourceUsage(string context = "")
		{
			try
			{
				// Throttle reports
				if (DateTime.UtcNow - _lastReport < _reportInterval)
					return;
				
				_lastReport = DateTime.UtcNow;
				
				// Thread pool info
				ThreadPool.GetAvailableThreads(out int workerThreads, out int completionPortThreads);
				ThreadPool.GetMaxThreads(out int maxWorkerThreads, out int maxCompletionPortThreads);
				ThreadPool.GetMinThreads(out int minWorkerThreads, out int minCompletionPortThreads);
				
				// Process info
				using var process = Process.GetCurrentProcess();
				var workingSet = process.WorkingSet64 / (1024 * 1024); // MB
				var privateMemory = process.PrivateMemorySize64 / (1024 * 1024); // MB
				var threadCount = process.Threads.Count;
				
				// Handle count
				uint handleCount = 0;
				GetProcessHandleCount(GetCurrentProcess(), out handleCount);
				
				// GC info
				var gen0 = GC.CollectionCount(0);
				var gen1 = GC.CollectionCount(1);
				var gen2 = GC.CollectionCount(2);
				var totalMemory = GC.GetTotalMemory(false) / (1024 * 1024); // MB
				
				_logger?.LogInformation(
					"[RESOURCES] {Context} - Threads: {ThreadCount} (Pool: {WorkerThreads}/{MaxWorkerThreads}), " +
					"Memory: {WorkingSet}MB (Private: {PrivateMemory}MB, Managed: {ManagedMemory}MB), " +
					"Handles: {HandleCount}, GC: {Gen0}/{Gen1}/{Gen2}",
					context,
					threadCount,
					workerThreads, maxWorkerThreads,
					workingSet, privateMemory, totalMemory,
					handleCount,
					gen0, gen1, gen2);
				
				// Warning if resources are low
				if (workerThreads < 10)
				{
					_logger?.LogWarning("[RESOURCES-WARNING] Thread pool exhaustion! Available workers: {WorkerThreads}", workerThreads);
				}
				
				if (handleCount > 5000)
				{
					_logger?.LogWarning("[RESOURCES-WARNING] High handle count: {HandleCount}", handleCount);
				}
				
				if (workingSet > 2000) // 2GB
				{
					_logger?.LogWarning("[RESOURCES-WARNING] High memory usage: {WorkingSet}MB", workingSet);
				}
			}
			catch (Exception ex)
			{
				_logger?.LogError(ex, "Failed to report resource usage");
			}
		}
		
		/// <summary>
		/// Reports thread pool queue status
		/// </summary>
		public static void ReportThreadPoolStatus(string context = "")
		{
			try
			{
				ThreadPool.GetAvailableThreads(out int workerThreads, out int completionPortThreads);
				ThreadPool.GetMaxThreads(out int maxWorkerThreads, out int maxCompletionPortThreads);
				long pendingWorkItems = ThreadPool.PendingWorkItemCount;
				
				System.Diagnostics.Debug.WriteLine(
					$"[THREADPOOL-{context}] Workers: {workerThreads}/{maxWorkerThreads}, " +
					$"IO: {completionPortThreads}, Pending: {pendingWorkItems}");
				
				if (workerThreads < 5 || pendingWorkItems > 100)
				{
					_logger?.LogWarning(
						"[THREADPOOL-WARNING] {Context} - Low workers: {WorkerThreads}/{MaxWorkerThreads}, Pending: {PendingWorkItems}",
						context, workerThreads, maxWorkerThreads, pendingWorkItems);
				}
			}
			catch { }
		}
	}
}