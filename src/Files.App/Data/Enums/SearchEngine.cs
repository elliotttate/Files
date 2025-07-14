// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Data.Enums
{
	/// <summary>
	/// Defines the available search engines for file search
	/// </summary>
	public enum SearchEngine
	{
		/// <summary>
		/// Built-in fuzzy search using fzf algorithm
		/// </summary>
		BuiltInFuzzy = 0,

		/// <summary>
		/// Everything search engine (requires Everything to be installed)
		/// </summary>
		Everything = 1,

		/// <summary>
		/// Simple contains search (legacy)
		/// </summary>
		SimpleContains = 2
	}
}