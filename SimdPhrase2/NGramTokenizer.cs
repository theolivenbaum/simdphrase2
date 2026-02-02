using System;
using System.Buffers;

namespace SimdPhrase2
{
    public class NGramTokenizer : ITextTokenizer
    {
        private readonly int _n;
        private readonly bool _lowerCase;
        private bool _resetted = true;

        public NGramTokenizer(int n, bool lowerCase = true)
        {
            if (n <= 0) throw new ArgumentOutOfRangeException(nameof(n), "N must be greater than 0");
            _n = n;
            _lowerCase = lowerCase;
        }

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
            if (startPosition + _n > len)
            {
                return false;
            }

            tokenStart = startPosition;
            tokenLength = _n;
            nextPosition = startPosition + 1;

            if (_lowerCase)
            {
                bool needsLower = false;
                var tokenSpan = text.Slice(tokenStart, tokenLength);
                foreach (var c in tokenSpan)
                {
                    if (char.IsUpper(c))
                    {
                        needsLower = true;
                        break;
                    }
                }

                if (needsLower)
                {
                     char[] pooled = tokenLength > 256 ? ArrayPool<char>.Shared.Rent(tokenLength) : null;
                     Span<char> buffer = tokenLength <= 256 ? stackalloc char[tokenLength] : pooled.AsSpan(0, tokenLength);
                     tokenSpan.ToLower(buffer, System.Globalization.CultureInfo.InvariantCulture);
                     overrideToken = buffer.ToString();

                     if (pooled != null)
                     {
                         ArrayPool<char>.Shared.Return(pooled);
                     }
                }
            }

            if (!_resetted)
            {
                currentIndex++;
            }
            _resetted = false;

            return true;
        }
    }
}
