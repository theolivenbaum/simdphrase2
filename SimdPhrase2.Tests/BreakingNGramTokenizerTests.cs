using System;
using System.Collections.Generic;
using Xunit;

namespace SimdPhrase2.Tests
{
    public class BreakingNGramTokenizerTests
    {
        [Fact]
        public void Tokenize_DefaultWhitespace_Basic()
        {
            var tokenizer = new BreakingNGramTokenizer(2);
            var input = "AB CD".AsSpan();
            // "AB", " C" (skip), "CD"
            // Expect: "ab", "cd"
            var expected = new[] { "ab", "cd" };

            var result = new List<string>();
            foreach(var t in tokenizer.Tokenize(input))
            {
                result.Add(t.ToString());
            }

            Assert.Equal(expected, result.ToArray());
        }

        [Fact]
        public void Tokenize_DefaultWhitespace_MultipleSpaces()
        {
            var tokenizer = new BreakingNGramTokenizer(3);
            var input = "ABC   DEF".AsSpan();
            // "abc", "def"
            var expected = new[] { "abc", "def" };

            var result = new List<string>();
            foreach(var t in tokenizer.Tokenize(input))
            {
                result.Add(t.ToString());
            }

            Assert.Equal(expected, result.ToArray());
        }

        [Fact]
        public void Tokenize_CustomBreakingChars()
        {
            var tokenizer = new BreakingNGramTokenizer(2, new[] { '_' });
            var input = "AB_CD".AsSpan();
            // "ab", "cd"
            var expected = new[] { "ab", "cd" };

            var result = new List<string>();
            foreach(var t in tokenizer.Tokenize(input))
            {
                result.Add(t.ToString());
            }

            Assert.Equal(expected, result.ToArray());
        }

        [Fact]
        public void Tokenize_CustomBreakingChars_PreservesWhitespace()
        {
            var tokenizer = new BreakingNGramTokenizer(2, new[] { '_' });
            var input = "A B".AsSpan(); // Space is NOT breaking here
            // "a ", " b"
            var expected = new[] { "a ", " b" };

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
            var tokenizer = new BreakingNGramTokenizer(2, lowerCase: false);
            var input = "AB CD".AsSpan();
            var expected = new[] { "AB", "CD" };

            var result = new List<string>();
            foreach(var t in tokenizer.Tokenize(input))
            {
                result.Add(t.ToString());
            }

            Assert.Equal(expected, result.ToArray());
        }

        [Fact]
        public void Tokenize_Overlap()
        {
            var tokenizer = new BreakingNGramTokenizer(2);
            var input = "ABC DEF".AsSpan();
            // "ab", "bc", (skip "c "), (skip " d"), "de", "ef"
            var expected = new[] { "ab", "bc", "de", "ef" };

            var result = new List<string>();
            foreach(var t in tokenizer.Tokenize(input))
            {
                result.Add(t.ToString());
            }

            Assert.Equal(expected, result.ToArray());
        }

        [Fact]
        public void Tokenize_ShortString_ReturnsEmpty()
        {
            var tokenizer = new BreakingNGramTokenizer(3);
            var input = "AB".AsSpan();

            var result = new List<string>();
            foreach(var t in tokenizer.Tokenize(input))
            {
                result.Add(t.ToString());
            }
            Assert.Empty(result);
        }
    }
}
