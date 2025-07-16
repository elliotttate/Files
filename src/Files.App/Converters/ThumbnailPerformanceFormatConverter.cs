// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.UI.Xaml.Data;
using System;
using System.Globalization;

namespace Files.App.Converters
{
	public class ThumbnailPerformanceFormatConverter : IValueConverter
	{
		public object Convert(object value, Type targetType, object parameter, string language)
		{
			if (value == null || parameter == null)
				return string.Empty;

			var format = parameter.ToString();
			
			return format switch
			{
				"Percentage" => value is double d ? $"{d:P0}" : value.ToString(),
				"TimeMs" => value is double t ? $"{t:F1} ms" : $"{value} ms",
				"Number" => value is double n ? $"{n:F1}" : value.ToString(),
				"MemoryMB" => value is double m ? $"{m:F1} MB" : $"{value} MB",
				_ => value.ToString()
			};
		}

		public object ConvertBack(object value, Type targetType, object parameter, string language)
		{
			throw new NotImplementedException();
		}
	}
}