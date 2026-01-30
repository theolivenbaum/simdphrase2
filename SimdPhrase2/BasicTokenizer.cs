using System;
using System.Collections.Generic;

namespace SimdPhrase2
{
    public class BasicTokenizer : ITextTokenizer
    {
        public IEnumerable<ReadOnlyMemory<char>> Tokenize(ReadOnlyMemory<char> text)
        {
            if (text.IsEmpty) yield break;

            int start = 0;
            int len = text.Length;

            while (start < len)
            {
                int tokenStart = start;
                int tokenLength = 0;

                // Skip whitespace
                while (tokenStart < len && char.IsWhiteSpace(text.Span[tokenStart]))
                {
                    tokenStart++;
                }

                if (tokenStart >= len)
                {
                    start = len;
                }
                else
                {
                    char c = text.Span[tokenStart];
                    if (IsWordChar(c))
                    {
                        // Consume word
                        while (tokenStart + tokenLength < len && IsWordChar(text.Span[tokenStart + tokenLength]))
                        {
                            tokenLength++;
                        }
                    }
                    else
                    {
                        // Consume non-word, non-whitespace sequence
                        while (tokenStart + tokenLength < len && !IsWordChar(text.Span[tokenStart + tokenLength]) && !char.IsWhiteSpace(text.Span[tokenStart + tokenLength]))
                        {
                            tokenLength++;
                        }
                    }
                }

                if (tokenStart >= len) break;

                // Check casing
                bool needsLower = false;
                for(int i=0; i<tokenLength; i++)
                {
                    if (char.IsUpper(text.Span[tokenStart+i]))
                    {
                        needsLower = true;
                        break;
                    }
                }

                if (needsLower)
                {
                     string s = text.Span.Slice(tokenStart, tokenLength).ToString().ToLowerInvariant();
                     yield return s.AsMemory();
                }
                else
                {
                     yield return text.Slice(tokenStart, tokenLength);
                }

                start = tokenStart + tokenLength;
            }
        }

        private static bool IsWordChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }
    }
}
