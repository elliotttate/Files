// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.Mvvm.DependencyInjection;
using Files.App.Data.Models;
using Files.App.ViewModels;
using System.Collections.Concurrent;
using System.Threading.Tasks.Dataflow;

namespace Files.App.Services.FuzzyMatcher
{
	public interface IFuzzySearchService
	{
		Task<List<FuzzySearchResult>> SearchAsync(IEnumerable<ListedItem> items, string query, CancellationToken cancellationToken = default);
		List<ListedItem> FilterItems(IEnumerable<ListedItem> items, string query);
		Task<List<ListedItem>> FilterItemsAsync(IEnumerable<ListedItem> items, string query, CancellationToken cancellationToken = default);
		bool IsMatch(string text, string query);
	}

	public sealed class FuzzySearchService : IFuzzySearchService
	{
		private readonly FuzzyMatcher _matcher;
		private readonly int _maxConcurrency;

		public FuzzySearchService()
		{
			_matcher = new FuzzyMatcher();
			_maxConcurrency = Math.Min(Environment.ProcessorCount * 2, 8);
		}

		public async Task<List<FuzzySearchResult>> SearchAsync(IEnumerable<ListedItem> items, string query, 
			CancellationToken cancellationToken = default)
		{
			if (string.IsNullOrWhiteSpace(query))
				return items.Select(item => new FuzzySearchResult { Item = item, Score = 0 }).ToList();

			var results = new ConcurrentBag<FuzzySearchResult>();

			// Create a dataflow pipeline for parallel processing
			var processBlock = new ActionBlock<ListedItem>(
				item => ProcessItem(item, query, results),
				new ExecutionDataflowBlockOptions
				{
					MaxDegreeOfParallelism = _maxConcurrency,
					CancellationToken = cancellationToken
				});

			// Feed items to the pipeline
			foreach (var item in items)
			{
				await processBlock.SendAsync(item, cancellationToken);
			}

			processBlock.Complete();
			await processBlock.Completion;

			// Sort by score (highest first) and return
			return results
				.Where(r => r.Score > 0)
				.OrderByDescending(r => r.Score)
				.ThenBy(r => r.Item.Name.Length) // Prefer shorter names for same score
				.ToList();
		}

		public List<ListedItem> FilterItems(IEnumerable<ListedItem> items, string query)
		{
			return FilterItemsAsync(items, query, CancellationToken.None).GetAwaiter().GetResult();
		}

		public async Task<List<ListedItem>> FilterItemsAsync(IEnumerable<ListedItem> items, string query, 
			CancellationToken cancellationToken = default)
		{
			if (string.IsNullOrWhiteSpace(query))
				return items.ToList();

			var results = new ConcurrentBag<FuzzySearchResult>();
			var itemsList = items.ToList();
			
			// Limit results to prevent UI freezing
			const int maxResults = 500;
			long resultCount = 0;

			// Process in parallel for better performance
			var parallelOptions = new ParallelOptions
			{
				CancellationToken = cancellationToken,
				MaxDegreeOfParallelism = _maxConcurrency
			};

			try
			{
				await Task.Run(() =>
				{
					Parallel.ForEach(itemsList, parallelOptions, (item, state) =>
					{
						// Check cancellation frequently
						if (cancellationToken.IsCancellationRequested)
						{
							state.Stop();
							return;
						}
						
						if (Interlocked.Read(ref resultCount) >= maxResults)
						{
							state.Stop();
							return;
						}

						try
						{
							var result = MatchItem(item, query);
							if (result.Score > 0)
							{
								results.Add(new FuzzySearchResult { Item = item, Score = result.Score, MatchResult = result });
								Interlocked.Increment(ref resultCount);
							}
						}
						catch (Exception ex)
						{
							// Ignore individual item errors (e.g., permission issues)
							System.Diagnostics.Debug.WriteLine($"Error matching item {item.ItemPath}: {ex.Message}");
						}
					});
				}, cancellationToken);
			}
			catch (OperationCanceledException)
			{
				// Return partial results if cancelled
			}

			// Return items sorted by relevance (limit to top results)
			return results
				.OrderByDescending(r => r.Score)
				.ThenBy(r => r.Item.Name.Length)
				.Take(maxResults)
				.Select(r => r.Item)
				.ToList();
		}

		public bool IsMatch(string text, string query)
		{
			if (string.IsNullOrWhiteSpace(query))
				return true;

			var result = _matcher.Match(text, query, caseSensitive: false);
			return result.IsMatch;
		}

		private void ProcessItem(ListedItem item, string query, ConcurrentBag<FuzzySearchResult> results)
		{
			var matchResult = MatchItem(item, query);
			if (matchResult.Score > 0)
			{
				results.Add(new FuzzySearchResult 
				{ 
					Item = item, 
					Score = matchResult.Score,
					MatchResult = matchResult
				});
			}
		}

		private FuzzyMatchResult MatchItem(ListedItem item, string query)
		{
			// Try matching against different properties with different weights
			var nameResult = _matcher.Match(item.Name, query, caseSensitive: false);
			
			// Boost score for exact prefix matches
			if (item.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
			{
				nameResult.Score = (int)(nameResult.Score * 1.5);
			}

			// Also try matching against the full path for better results
			if (!string.IsNullOrEmpty(item.ItemPath))
			{
				var pathResult = _matcher.Match(item.ItemPath, query, caseSensitive: false);
				
				// Use the better of the two results, but prefer name matches
				if (pathResult.Score > nameResult.Score * 0.8)
				{
					return pathResult;
				}
			}

			return nameResult;
		}
	}

	public class FuzzySearchResult
	{
		public ListedItem Item { get; set; }
		public int Score { get; set; }
		public FuzzyMatchResult MatchResult { get; set; }
	}
}