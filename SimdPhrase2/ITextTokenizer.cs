using System;

namespace SimdPhrase2
{
    /// <summary>
    /// Interface for tokenizing text.
    /// Implementations are responsible for normalization (e.g. lowercasing) of the tokens.
    /// </summary>
    public interface ITextTokenizer
    {
        void Reset();

        /// <summary>
        /// Low-level method to advance to the next token.
        /// Returns true if a token was found, false if end of text.
        /// </summary>
        /// <param name="text">The source text to tokenize.</param>
        /// <param name="currentTokenIndex">The index position, to be incremented by the tokenizer accordingly.</param>
        /// <param name="startPosition">Index in text to start searching.</param>
        /// <param name="tokenStart">Output index of start of found token in the original text (if overrideToken is null).</param>
        /// <param name="tokenLength">Output length of found token.</param>
        /// <param name="nextPosition">Output index to start next search from.</param>
        /// <param name="overrideToken">Output string containing the token if it differs from the original text (e.g. due to normalization).</param>
        /// <returns>True if a token was found, otherwise false.</returns>
        bool GetNextToken(ReadOnlySpan<char> text, ref uint currentTokenIndex, int startPosition, out int tokenStart, out int tokenLength, out int nextPosition, out string overrideToken);
    }

    public static class TextTokenizerExtensions
    {
        public static TokenEnumerable Tokenize(this ITextTokenizer tokenizer, ReadOnlySpan<char> text) => new TokenEnumerable(tokenizer, text);
    }

    public readonly ref struct TokenEnumerable
    {
        private readonly ITextTokenizer _tokenizer;
        private readonly ReadOnlySpan<char> _text;

        public TokenEnumerable(ITextTokenizer tokenizer, ReadOnlySpan<char> text)
        {
            _tokenizer = tokenizer;
            _tokenizer.Reset();
            _text = text;
        }

        public TokenAndIndexEnumerator GetEnumerator() => new TokenAndIndexEnumerator(_tokenizer, _text);

        public ref struct TokenAndIndexEnumerator
        {
            private readonly ITextTokenizer _tokenizer;
            private readonly ReadOnlySpan<char> _text;
            private int _nextPosition;
            private int _currentStart;
            private int _currentLength;
            private string _currentOverride;
            private uint _currentIndex;

            public TokenAndIndexEnumerator(ITextTokenizer tokenizer, ReadOnlySpan<char> text)
            {
                _tokenizer = tokenizer;
                _text = text;
                _nextPosition = 0;
                _currentStart = 0;
                _currentLength = 0;
                _currentIndex = 0;
                _currentOverride = null;
            }

            public ReadOnlySpan<char> Current => _currentOverride != null ? _currentOverride.AsSpan() : _text.Slice(_currentStart, _currentLength);
            public uint CurrentIndex => _currentIndex;

            public bool MoveNext()
            {
                return _tokenizer.GetNextToken(_text, ref _currentIndex, _nextPosition, out _currentStart, out _currentLength, out _nextPosition, out _currentOverride);
            }
        }
    }
}
