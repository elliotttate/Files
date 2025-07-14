// Copyright (c) Files Community
// Licensed under the MIT License.
// Based on fzf algorithm by Junegunn Choi (https://github.com/junegunn/fzf)

using System.Runtime.CompilerServices;
using System.Text;

namespace Files.App.Services.FuzzyMatcher
{
	/// <summary>
	/// Implements fzf-style fuzzy matching algorithm optimized for file paths and names
	/// </summary>
	public sealed class FuzzyMatcher
	{
		// Scoring constants from fzf
		private const int ScoreMatch = 16;
		private const int ScoreGapStart = -3;
		private const int ScoreGapExtension = -1;
		private const int BonusBoundary = 8;
		private const int BonusNonWord = 8;
		private const int BonusCamel123 = 7;
		private const int BonusConsecutive = 4;
		private const int BonusFirstCharMultiplier = 2;

		// Character type constants
		private const byte CharWhite = 0;
		private const byte CharNonWord = 1;
		private const byte CharDelimiter = 2;
		private const byte CharLower = 3;
		private const byte CharUpper = 4;
		private const byte CharLetter = 5;
		private const byte CharNumber = 6;

		private readonly byte[] _charTypes = new byte[128];
		private readonly int[] _bonusTable = new int[128];

		public FuzzyMatcher()
		{
			InitializeCharTypes();
			InitializeBonusTable();
		}

		private void InitializeCharTypes()
		{
			// Initialize ASCII character types
			for (int i = 0; i < 128; i++)
			{
				char c = (char)i;
				if (char.IsWhiteSpace(c))
					_charTypes[i] = CharWhite;
				else if (c == '/' || c == '\\' || c == '.' || c == '-' || c == '_' || c == ' ')
					_charTypes[i] = CharDelimiter;
				else if (char.IsLower(c))
					_charTypes[i] = CharLower;
				else if (char.IsUpper(c))
					_charTypes[i] = CharUpper;
				else if (char.IsLetter(c))
					_charTypes[i] = CharLetter;
				else if (char.IsDigit(c))
					_charTypes[i] = CharNumber;
				else
					_charTypes[i] = CharNonWord;
			}
		}

		private void InitializeBonusTable()
		{
			// Pre-calculate bonuses for each character type transition
			for (int i = 0; i < 128; i++)
			{
				_bonusTable[i] = CalculateBonus((char)i);
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private int CalculateBonus(char c)
		{
			if (c < 128)
			{
				byte charType = _charTypes[c];
				if (charType == CharDelimiter)
					return BonusBoundary;
				else if (charType == CharNonWord)
					return BonusNonWord;
			}
			return 0;
		}

		public FuzzyMatchResult Match(string text, string pattern, bool caseSensitive = false)
		{
			if (string.IsNullOrEmpty(pattern))
				return new FuzzyMatchResult { IsMatch = true, Score = 0 };

			if (string.IsNullOrEmpty(text))
				return new FuzzyMatchResult { IsMatch = false };

			// For file paths, normalize separators
			text = text.Replace('\\', '/');
			
			// Use V2 algorithm for better accuracy
			return FuzzyMatchV2(text, pattern, caseSensitive);
		}

		private FuzzyMatchResult FuzzyMatchV2(string text, string pattern, bool caseSensitive)
		{
			int textLen = text.Length;
			int patternLen = pattern.Length;

			if (patternLen > textLen)
				return new FuzzyMatchResult { IsMatch = false };

			// Limit text length to prevent excessive memory usage
			const int maxTextLength = 256;
			if (textLen > maxTextLength)
			{
				// For very long paths, just check if pattern exists
				if (text.IndexOf(pattern, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase) >= 0)
				{
					return new FuzzyMatchResult { IsMatch = true, Score = 1 };
				}
				return new FuzzyMatchResult { IsMatch = false };
			}

			// Quick ASCII check for performance
			if (!ContainsPattern(text, pattern, caseSensitive))
				return new FuzzyMatchResult { IsMatch = false };

			// Dynamic programming approach
			int[,] scores = new int[patternLen + 1, textLen + 1];
			int[,] consecutive = new int[patternLen + 1, textLen + 1];
			int[] positions = new int[patternLen];
			
			int maxScore = int.MinValue;
			int maxScorePos = -1;

			// Initialize first row
			for (int j = 0; j <= textLen; j++)
			{
				scores[0, j] = 0;
				consecutive[0, j] = 0;
			}

			// Fill the DP table
			for (int i = 1; i <= patternLen; i++)
			{
				char patternChar = caseSensitive ? pattern[i - 1] : char.ToLowerInvariant(pattern[i - 1]);
				int prevMaxScore = int.MinValue;
				int prevMaxScorePos = -1;

				scores[i, 0] = int.MinValue;
				
				for (int j = 1; j <= textLen; j++)
				{
					char textChar = caseSensitive ? text[j - 1] : char.ToLowerInvariant(text[j - 1]);
					
					if (patternChar == textChar)
					{
						int bonus = 0;
						
						// Calculate position bonus
						if (j == 1 || IsWordBoundary(text, j - 1))
						{
							bonus = BonusBoundary;
						}
						else if (j > 1)
						{
							char prevChar = text[j - 2];
							if (char.IsLower(prevChar) && char.IsUpper(text[j - 1]))
								bonus = BonusCamel123;
							else if (prevChar < 128 && _charTypes[prevChar] == CharDelimiter)
								bonus = BonusBoundary;
						}

						// First character bonus
						if (i == 1)
							bonus *= BonusFirstCharMultiplier;

						int consecutiveBonus = 0;
						if (consecutive[i - 1, j - 1] > 0)
						{
							consecutiveBonus = BonusConsecutive + consecutive[i - 1, j - 1];
							if (consecutiveBonus > BonusBoundary)
								consecutiveBonus = BonusBoundary;
						}

						int scoreMatch = ScoreMatch + bonus + consecutiveBonus;
						int scorePrev = (prevMaxScore > int.MinValue) ? 
							prevMaxScore + ScoreGapStart + (j - prevMaxScorePos - 1) * ScoreGapExtension : 
							int.MinValue;

						if (scoreMatch > scorePrev)
						{
							scores[i, j] = scores[i - 1, j - 1] + scoreMatch;
							consecutive[i, j] = consecutive[i - 1, j - 1] + 1;
						}
						else
						{
							scores[i, j] = scorePrev;
							consecutive[i, j] = 0;
						}
					}
					else
					{
						scores[i, j] = int.MinValue;
						consecutive[i, j] = 0;
					}

					if (scores[i, j] > prevMaxScore)
					{
						prevMaxScore = scores[i, j];
						prevMaxScorePos = j;
					}
				}

				if (i == patternLen && prevMaxScore > maxScore)
				{
					maxScore = prevMaxScore;
					maxScorePos = prevMaxScorePos;
				}
			}

			if (maxScore == int.MinValue)
				return new FuzzyMatchResult { IsMatch = false };

			// Backtrack to find positions
			BacktrackPositions(text, pattern, scores, positions, maxScorePos, caseSensitive);

			return new FuzzyMatchResult
			{
				IsMatch = true,
				Score = maxScore,
				Positions = positions
			};
		}

		private bool ContainsPattern(string text, string pattern, bool caseSensitive)
		{
			int patternIndex = 0;
			for (int i = 0; i < text.Length && patternIndex < pattern.Length; i++)
			{
				char textChar = caseSensitive ? text[i] : char.ToLowerInvariant(text[i]);
				char patternChar = caseSensitive ? pattern[patternIndex] : char.ToLowerInvariant(pattern[patternIndex]);
				
				if (textChar == patternChar)
					patternIndex++;
			}
			return patternIndex == pattern.Length;
		}

		private bool IsWordBoundary(string text, int position)
		{
			if (position == 0)
				return true;

			char curr = text[position];
			char prev = text[position - 1];

			// Check for path separators
			if (prev == '/' || prev == '\\')
				return true;

			// Check for case changes (camelCase)
			if (char.IsLower(prev) && char.IsUpper(curr))
				return true;

			// Check for alphanumeric boundaries
			if (!char.IsLetterOrDigit(prev) && char.IsLetterOrDigit(curr))
				return true;

			return false;
		}

		private void BacktrackPositions(string text, string pattern, int[,] scores, int[] positions, 
			int endPos, bool caseSensitive)
		{
			int patternLen = pattern.Length;
			int j = endPos;

			for (int i = patternLen; i > 0; i--)
			{
				while (j > 0)
				{
					char textChar = caseSensitive ? text[j - 1] : char.ToLowerInvariant(text[j - 1]);
					char patternChar = caseSensitive ? pattern[i - 1] : char.ToLowerInvariant(pattern[i - 1]);

					if (textChar == patternChar && scores[i, j] > scores[i, j - 1])
					{
						positions[i - 1] = j - 1;
						j--;
						break;
					}
					j--;
				}
			}
		}
	}

	public struct FuzzyMatchResult
	{
		public bool IsMatch { get; set; }
		public int Score { get; set; }
		public int[] Positions { get; set; }

		public string GetHighlightedText(string originalText, string highlightStart = "<b>", string highlightEnd = "</b>")
		{
			if (!IsMatch || Positions == null || Positions.Length == 0)
				return originalText;

			var result = new StringBuilder();
			int lastPos = 0;

			foreach (int pos in Positions.OrderBy(p => p))
			{
				if (pos > lastPos)
					result.Append(originalText.Substring(lastPos, pos - lastPos));
				
				result.Append(highlightStart);
				result.Append(originalText[pos]);
				result.Append(highlightEnd);
				
				lastPos = pos + 1;
			}

			if (lastPos < originalText.Length)
				result.Append(originalText.Substring(lastPos));

			return result.ToString();
		}
	}
}