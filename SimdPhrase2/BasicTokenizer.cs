using System;

namespace SimdPhrase2
{
    public class BasicTokenizer : ITextTokenizer
    {
        public bool GetNextToken(ReadOnlySpan<char> text, int startPosition, out int tokenStart, out int tokenLength, out int nextPosition)
        {
            tokenStart = 0;
            tokenLength = 0;
            nextPosition = startPosition;

            int len = text.Length;
            int pos = startPosition;

            if (pos >= len) return false;

            // Skip whitespace
            while (pos < len && char.IsWhiteSpace(text[pos]))
            {
                pos++;
            }

            if (pos >= len)
            {
                nextPosition = pos;
                return false;
            }

            int start = pos;
            int length = 0;
            char c = text[start];

            if (IsWordChar(c))
            {
                // Consume word
                while (start + length < len && IsWordChar(text[start + length]))
                {
                    length++;
                }
            }
            else
            {
                // Consume non-word, non-whitespace sequence
                while (start + length < len && !IsWordChar(text[start + length]) && !char.IsWhiteSpace(text[start + length]))
                {
                    length++;
                }
            }

            tokenStart = start;
            tokenLength = length;
            nextPosition = start + length;
            return true;
        }

        private static bool IsWordChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }
    }
}
