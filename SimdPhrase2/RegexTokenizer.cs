using System;
using System.Collections.Generic;

namespace SimdPhrase2
{
    public class RegexTokenizer : ITextTokenizer
    {
        public IEnumerable<ReadOnlyMemory<char>> Tokenize(ReadOnlyMemory<char> text)
        {
            // Convert to string because current logic in Utils is string-based.
            // In a future optimization, this could operate directly on spans.
            string s = text.ToString();

            // Normalize (Trim + LowerCase)
            string normalized = Utils.Normalize(s);

            // Tokenize (Regex split)
            foreach (var token in Utils.Tokenize(normalized))
            {
                yield return token.AsMemory();
            }
        }
    }
}
