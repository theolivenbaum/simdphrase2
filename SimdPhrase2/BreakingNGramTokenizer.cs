using System;
using System.Buffers;
using System.Collections.Generic;

namespace SimdPhrase2
{
    public class BreakingNGramTokenizer : ITextTokenizer
    {
        private readonly int _n;
        private readonly bool _lowerCase;
        private readonly HashSet<char>? _breakingChars;
        private readonly bool _useDefaultWhitespace;
        private bool _resetted = true;

        public BreakingNGramTokenizer(int n, IEnumerable<char>? breakingChars = null, bool lowerCase = true)
        {
            if (n <= 0) throw new ArgumentOutOfRangeException(nameof(n), "N must be greater than 0");
            _n = n;
            _lowerCase = lowerCase;

            if (breakingChars != null)
            {
                _breakingChars = new HashSet<char>(breakingChars);
                _useDefaultWhitespace = false;
            }
            else
            {
                _breakingChars = null;
                _useDefaultWhitespace = true;
            }
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
            int pos = startPosition;

            while (pos + _n <= len)
            {
                // Check if the window [pos, pos + n) contains a breaking char
                int breakingIndex = -1;

                // We want to find the *first* breaking char in the window
                for (int i = 0; i < _n; i++)
                {
                    char c = text[pos + i];
                    bool isBreak = _useDefaultWhitespace ? char.IsWhiteSpace(c) : _breakingChars!.Contains(c);
                    if (isBreak)
                    {
                        breakingIndex = i;
                        break;
                    }
                }

                if (breakingIndex != -1)
                {
                    // Found a breaking char at index `i` (relative to pos).
                    // Any n-gram starting at `pos`, `pos+1`, ... `pos + breakingIndex` would include this char.
                    // So the next possible start is `pos + breakingIndex + 1`.
                    pos += breakingIndex + 1;
                    continue;
                }

                // Found a valid n-gram
                tokenStart = pos;
                tokenLength = _n;
                nextPosition = pos + 1;

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

            // No more valid n-grams found
            // Ensure nextPosition is advanced to avoid infinite loops in poorly implemented callers,
            // though returning false should signal stop.
            nextPosition = len;
            return false;
        }
    }
}
