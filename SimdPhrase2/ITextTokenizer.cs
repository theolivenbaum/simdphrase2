using System;

namespace SimdPhrase2
{
    public interface ITextTokenizer
    {
        // Low-level method to advance to the next token.
        // Returns true if a token was found, false if end of text.
        // startPosition: index in text to start searching.
        // tokenStart: output index of start of found token.
        // tokenLength: output length of found token.
        // nextPosition: output index to start next search from.
        bool GetNextToken(ReadOnlySpan<char> text, int startPosition, out int tokenStart, out int tokenLength, out int nextPosition);
    }

    public static class TextTokenizerExtensions
    {
        public static TokenEnumerable Tokenize(this ITextTokenizer tokenizer, ReadOnlySpan<char> text)
            => new TokenEnumerable(tokenizer, text);
    }

    public readonly ref struct TokenEnumerable
    {
        private readonly ITextTokenizer _tokenizer;
        private readonly ReadOnlySpan<char> _text;

        public TokenEnumerable(ITextTokenizer tokenizer, ReadOnlySpan<char> text)
        {
            _tokenizer = tokenizer;
            _text = text;
        }

        public Enumerator GetEnumerator() => new Enumerator(_tokenizer, _text);

        public ref struct Enumerator
        {
            private readonly ITextTokenizer _tokenizer;
            private readonly ReadOnlySpan<char> _text;
            private int _nextPosition;
            private int _currentStart;
            private int _currentLength;

            public Enumerator(ITextTokenizer tokenizer, ReadOnlySpan<char> text)
            {
                _tokenizer = tokenizer;
                _text = text;
                _nextPosition = 0;
                _currentStart = 0;
                _currentLength = 0;
            }

            public ReadOnlySpan<char> Current => _text.Slice(_currentStart, _currentLength);

            public bool MoveNext()
            {
                return _tokenizer.GetNextToken(_text, _nextPosition, out _currentStart, out _currentLength, out _nextPosition);
            }
        }
    }
}
