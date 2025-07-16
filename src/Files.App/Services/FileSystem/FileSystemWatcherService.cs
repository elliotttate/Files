// Copyright (c) Files Community
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.DependencyInjection;
using Files.App.Data.Contracts;
using Files.App.ViewModels;
using Files.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace Files.App.Services.FileSystem
{
	/// <summary>
	/// Service that manages file system watchers for the Files app.
	/// </summary>
	public class FileSystemWatcherService : IFileSystemWatcherService
	{
		private readonly Dictionary<string, IOptimizedFileSystemWatcher> _watchers = new();
		private readonly object _watchersLock = new();
		private readonly DispatcherQueue _dispatcherQueue;

		public FileSystemWatcherService()
		{
			_dispatcherQueue = DispatcherQueue.GetForCurrentThread();
		}

		/// <summary>
		/// Creates or gets a file system watcher for the specified path.
		/// </summary>
		/// <param name="path">The path to watch.</param>
		/// <param name="includeSubdirectories">Whether to include subdirectories.</param>
		/// <returns>The file system watcher for the path.</returns>
		public IOptimizedFileSystemWatcher GetWatcher(string path, bool includeSubdirectories = true)
		{
			if (string.IsNullOrEmpty(path))
				throw new ArgumentNullException(nameof(path));

			lock (_watchersLock)
			{
				// Check if we already have a watcher for this path
				if (_watchers.TryGetValue(path, out var existingWatcher))
				{
					return existingWatcher;
				}

				// Create a new watcher
				var watcher = new OptimizedFileSystemWatcher(path)
				{
					IncludeSubdirectories = includeSubdirectories
				};

				// Subscribe to events
				watcher.BatchedChanges += OnBatchedChanges;
				watcher.Error += OnWatcherError;

				// Start the watcher
				watcher.StartWatcher();

				// Add to our collection
				_watchers[path] = watcher;

				return watcher;
			}
		}

		/// <summary>
		/// Stops and removes a file system watcher for the specified path.
		/// </summary>
		/// <param name="path">The path to stop watching.</param>
		public void RemoveWatcher(string path)
		{
			if (string.IsNullOrEmpty(path))
				return;

			lock (_watchersLock)
			{
				if (_watchers.TryGetValue(path, out var watcher))
				{
					// Unsubscribe from events
					watcher.BatchedChanges -= OnBatchedChanges;
					watcher.Error -= OnWatcherError;

					// Stop and dispose the watcher
					watcher.StopWatcher();
					watcher.Dispose();

					// Remove from our collection
					_watchers.Remove(path);
				}
			}
		}

		/// <summary>
		/// Stops all file system watchers.
		/// </summary>
		public void StopAll()
		{
			lock (_watchersLock)
			{
				foreach (var watcher in _watchers.Values)
				{
					watcher.BatchedChanges -= OnBatchedChanges;
					watcher.Error -= OnWatcherError;
					watcher.StopWatcher();
					watcher.Dispose();
				}

				_watchers.Clear();
			}
		}

		/// <summary>
		/// Gets statistics for all active watchers.
		/// </summary>
		/// <returns>A dictionary of path to statistics.</returns>
		public Dictionary<string, FileSystemWatcherStatistics> GetAllStatistics()
		{
			lock (_watchersLock)
			{
				return _watchers.ToDictionary(
					kvp => kvp.Key,
					kvp => kvp.Value.GetStatistics()
				);
			}
		}

		private async void OnBatchedChanges(object sender, BatchedFileSystemEventArgs e)
		{
			if (sender is not IOptimizedFileSystemWatcher watcher)
				return;

			Debug.WriteLine($"Batched file system changes: {e.TotalCount} events " +
				$"(Created: {e.CreatedCount}, Deleted: {e.DeletedCount}, " +
				$"Changed: {e.ChangedCount}, Renamed: {e.RenamedCount})");

			// Process changes on the UI thread
			await _dispatcherQueue.EnqueueOrInvokeAsync(() =>
			{
				// Get the current shell page
				var shellPage = Ioc.Default.GetService<IShellPage>();
				if (shellPage?.SlimContentPage != null)
				{
					// Check if the current directory matches the watcher path
					var currentPath = shellPage.ShellViewModel?.WorkingDirectory;
					if (currentPath is not null && currentPath.Equals(watcher.Path, StringComparison.OrdinalIgnoreCase))
					{
						// Refresh the view with the batched changes
						ProcessBatchedChanges(shellPage.ShellViewModel, e);
					}
				}
			});
		}

		private void ProcessBatchedChanges(ShellViewModel viewModel, BatchedFileSystemEventArgs e)
		{
			// Process different types of changes
			var itemsToAdd = new List<string>();
			var itemsToRemove = new List<string>();
			var itemsToUpdate = new List<string>();

			foreach (var evt in e.Events)
			{
				switch (evt.ChangeType)
				{
					case System.IO.WatcherChangeTypes.Created:
						itemsToAdd.Add(evt.FullPath);
						break;
					case System.IO.WatcherChangeTypes.Deleted:
						itemsToRemove.Add(evt.FullPath);
						break;
					case System.IO.WatcherChangeTypes.Changed:
						itemsToUpdate.Add(evt.FullPath);
						break;
					case System.IO.WatcherChangeTypes.Renamed:
						// Handle rename as remove old + add new
						if (!string.IsNullOrEmpty(evt.OldFullPath))
							itemsToRemove.Add(evt.OldFullPath);
						itemsToAdd.Add(evt.FullPath);
						break;
				}
			}

			// Apply changes to the view model
			// This is where you would integrate with the existing refresh logic
			// For now, we'll just trigger a full refresh if there are many changes
			if (e.TotalCount > 10)
			{
				// Many changes, do a full refresh
				viewModel.RefreshItems(null);
			}
			else
			{
				// Process individual changes
				// This would require integration with the existing item management logic
				// For demonstration, we'll still do a refresh
				viewModel.RefreshItems(null);
			}
		}

		private void OnWatcherError(object sender, System.IO.ErrorEventArgs e)
		{
			Debug.WriteLine($"File system watcher error: {e.GetException().Message}");
			
			// Log the error
			App.Logger?.LogWarning(e.GetException(), "File system watcher encountered an error");
		}
	}

	/// <summary>
	/// Interface for the file system watcher service.
	/// </summary>
	public interface IFileSystemWatcherService
	{
		IOptimizedFileSystemWatcher GetWatcher(string path, bool includeSubdirectories = true);
		void RemoveWatcher(string path);
		void StopAll();
		Dictionary<string, FileSystemWatcherStatistics> GetAllStatistics();
	}
}