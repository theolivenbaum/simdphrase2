using System;
using System.Buffers;

namespace SimdPhrase2
{
    public class BasicTokenizer : ITextTokenizer
    {
        private bool _resetted = true;

        public void Reset()
        {
            _resetted = true;
        }

        public bool GetNextToken(ReadOnlySpan<char> text, ref uint currentIndex, int startPosition, out int tokenStart, out int tokenLength, out int nextPosition, out string overrideToken)
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

            foreach (var cs in tokenSpan)
            {
                if (char.IsUpper(cs))
                {
                    needsLower = true;
                    break;
                }
            }

            if (needsLower)
            {
                char[] pooled = length > 256 ? ArrayPool<char>.Shared.Rent(length) : null;
                Span<char> buffer = length <= 256 ? stackalloc char[length] : pooled.AsSpan(0, length);

                tokenSpan.ToLower(buffer, System.Globalization.CultureInfo.InvariantCulture);
                overrideToken = buffer.ToString();

                if (pooled is not null)
                {
                    ArrayPool<char>.Shared.Return(pooled);
                }
            }

            if (!_resetted) currentIndex++;
            _resetted = false;

            return true;
        }

        private static bool IsWordChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }
    }
}
