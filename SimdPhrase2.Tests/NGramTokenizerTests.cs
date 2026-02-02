using System;
using System.Collections.Generic;
using Xunit;

namespace SimdPhrase2.Tests
{
    public class NGramTokenizerTests
    {
        [Fact]
        public void Tokenize_Basic()
        {
            var tokenizer = new NGramTokenizer(3);
            var input = "ABCDE".AsSpan();
            // N=3, Lower=true (default)
            // "abc", "bcd", "cde"
            var expected = new[] { "abc", "bcd", "cde" };

            var result = new List<string>();
            foreach(var t in tokenizer.Tokenize(input))
            {
                result.Add(t.ToString());
            }

            Assert.Equal(expected, result.ToArray());
        }

        [Fact]
        public void Tokenize_ShortInput()
        {
            var tokenizer = new NGramTokenizer(3);
            var input = "AB".AsSpan();

            var result = new List<string>();
            foreach(var t in tokenizer.Tokenize(input))
            {
                result.Add(t.ToString());
            }

            Assert.Empty(result);
        }

        [Fact]
        public void Tokenize_ExactLength()
        {
            var tokenizer = new NGramTokenizer(3);
            var input = "ABC".AsSpan();
            var expected = new[] { "abc" };

            var result = new List<string>();
            foreach(var t in tokenizer.Tokenize(input))
            {
                result.Add(t.ToString());
            }

            Assert.Equal(expected, result.ToArray());
        }

        [Fact]
        public void Tokenize_NoLowerCase()
        {
            var tokenizer = new NGramTokenizer(3, lowerCase: false);
            var input = "ABC".AsSpan();
            var expected = new[] { "ABC" };

            var result = new List<string>();
            foreach(var t in tokenizer.Tokenize(input))
            {
                result.Add(t.ToString());
            }

            Assert.Equal(expected, result.ToArray());
        }

        [Fact]
        public void Tokenize_Whitespace()
        {
            var tokenizer = new NGramTokenizer(3);
            var input = "A B".AsSpan();
            // "a b"
            var expected = new[] { "a b" };

            var result = new List<string>();
            foreach(var t in tokenizer.Tokenize(input))
            {
                result.Add(t.ToString());
            }

            Assert.Equal(expected, result.ToArray());
        }

        [Fact]
        public void Tokenize_SpecialChars()
        {
            var tokenizer = new NGramTokenizer(3);
            var input = "A_B".AsSpan();
            var expected = new[] { "a_b" };

            var result = new List<string>();
            foreach(var t in tokenizer.Tokenize(input))
            {
                result.Add(t.ToString());
            }

            Assert.Equal(expected, result.ToArray());
        }

        [Fact]
        public void Tokenize_SlidingWindow()
        {
            var tokenizer = new NGramTokenizer(2);
            var input = "ABCD".AsSpan();
            // "ab", "bc", "cd"
            var expected = new[] { "ab", "bc", "cd" };

            var result = new List<string>();
            foreach(var t in tokenizer.Tokenize(input))
            {
                result.Add(t.ToString());
            }

            Assert.Equal(expected, result.ToArray());
        }

        [Fact]
        public void Constructor_InvalidN_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new NGramTokenizer(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new NGramTokenizer(-1));
        }
    }
}
