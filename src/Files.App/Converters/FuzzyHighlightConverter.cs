// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Services.FuzzyMatcher;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Text;
using Windows.UI;

namespace Files.App.Converters
{
	/// <summary>
	/// Converter to highlight matched characters in fuzzy search results
	/// </summary>
	public sealed class FuzzyHighlightConverter : IValueConverter
	{
		private static readonly SolidColorBrush HighlightBrush = new SolidColorBrush(Color.FromArgb(255, 255, 200, 0));

		public object Convert(object value, Type targetType, object parameter, string language)
		{
			if (value is not string text || parameter is not string query)
				return value;

			if (string.IsNullOrWhiteSpace(query))
				return CreateTextBlock(text);

			// Get fuzzy matcher service
			var fuzzyService = Ioc.Default.GetService<IFuzzySearchService>();
			if (fuzzyService == null)
				return CreateTextBlock(text);

			// Get match result
			var matcher = new FuzzyMatcher();
			var result = matcher.Match(text, query, caseSensitive: false);

			if (!result.IsMatch || result.Positions == null || result.Positions.Length == 0)
				return CreateTextBlock(text);

			// Create TextBlock with highlighted runs
			var textBlock = new TextBlock();
			var positions = result.Positions.OrderBy(p => p).ToList();
			int lastPos = 0;

			foreach (int pos in positions)
			{
				// Add non-highlighted text before this match
				if (pos > lastPos)
				{
					textBlock.Inlines.Add(new Run { Text = text.Substring(lastPos, pos - lastPos) });
				}

				// Add highlighted character
				textBlock.Inlines.Add(new Run 
				{ 
					Text = text[pos].ToString(),
					FontWeight = FontWeights.Bold,
					Foreground = HighlightBrush
				});

				lastPos = pos + 1;
			}

			// Add remaining text
			if (lastPos < text.Length)
			{
				textBlock.Inlines.Add(new Run { Text = text.Substring(lastPos) });
			}

			return textBlock;
		}

		public object ConvertBack(object value, Type targetType, object parameter, string language)
		{
			throw new NotImplementedException();
		}

		private static TextBlock CreateTextBlock(string text)
		{
			return new TextBlock { Text = text };
		}
	}
}