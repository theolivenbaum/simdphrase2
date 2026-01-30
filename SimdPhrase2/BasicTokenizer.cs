using System;
using System.Buffers;

namespace SimdPhrase2
{
    public class BasicTokenizer : ITextTokenizer
    {
        public bool GetNextToken(ReadOnlySpan<char> text, int startPosition, out int tokenStart, out int tokenLength, out int nextPosition, out string? overrideToken)
        {
            tokenStart = 0;
            tokenLength = 0;
            nextPosition = startPosition;
            overrideToken = null;

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

            // Check casing and normalize if needed
            bool needsLower = false;
            var tokenSpan = text.Slice(start, length);
            for (int i = 0; i < length; i++)
            {
                if (char.IsUpper(tokenSpan[i]))
                {
                    needsLower = true;
                    break;
                }
            }

            if (needsLower)
            {
                if (length <= 256)
                {
                    Span<char> buffer = stackalloc char[length];
                    tokenSpan.ToLower(buffer, System.Globalization.CultureInfo.InvariantCulture);
                    overrideToken = buffer.ToString();
                }
                else
                {
                    char[] buffer = ArrayPool<char>.Shared.Rent(length);
                    try
                    {
                        tokenSpan.ToLower(buffer.AsSpan(0, length), System.Globalization.CultureInfo.InvariantCulture);
                        overrideToken = new string(buffer, 0, length);
                    }
                    finally
                    {
                        ArrayPool<char>.Shared.Return(buffer);
                    }
                }
            }

            return true;
        }

        private static bool IsWordChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }
    }
}
