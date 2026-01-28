using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SimdPhrase2
{
    public static class Utils
    {
        /// <summary>
        /// Normalizes the input string by trimming leading and trailing
        /// whitespaces and converting it to lowercase.
        /// </summary>
        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s))
                return string.Empty;

            return s.Trim().ToLowerInvariant();
        }

        /// <summary>
        /// Tokenizes the input string by splitting it into word bounds
        /// also remove all tokens that are considered whitespace.
        /// </summary>
        public static IEnumerable<string> Tokenize(string s)
        {
            if (string.IsNullOrEmpty(s))
                yield break;

            // This regex matches sequences of word characters OR sequences of non-word-non-whitespace characters.
            // Effectively splitting by whitespace and keeping punctuation as separate tokens if they are not word characters.
            // Note: This is an approximation of unicode-segmentation's split_word_bounds + filter whitespace.
            // \w matches [a-zA-Z0-9_] usually.

            // A better approximation might be needed for full unicode support,
            // but for this port, Regex is a good start.

            // We use Matches to find all tokens.
            var matches = Regex.Matches(s, @"[\w]+|[^\w\s]+");
            foreach (Match match in matches)
            {
                yield return match.Value;
            }
        }
    }
}
