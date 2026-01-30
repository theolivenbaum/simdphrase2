using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace SimdPhrase2.Tests
{
    public class BasicTokenizerTests
    {
        [Fact]
        public void Tokenize_ShouldSplitCorrectly()
        {
            var tokenizer = new BasicTokenizer();
            // "  Hello, World!  " -> "Hello", ",", "World", "!"
            var input = "  Hello, World!  ".AsSpan();
            var expected = new[] { "Hello", ",", "World", "!" };

            var result = new List<string>();
            foreach(var t in tokenizer.Tokenize(input))
            {
                result.Add(t.ToString());
            }

            Assert.Equal(expected, result.ToArray());
        }

        [Fact]
        public void Tokenize_EmptyString_ShouldReturnEmpty()
        {
            var tokenizer = new BasicTokenizer();
            var input = "".AsSpan();

            var count = 0;
            foreach(var t in tokenizer.Tokenize(input))
            {
                count++;
            }

            Assert.Equal(0, count);
        }

        [Fact]
        public void Tokenize_Complex_ShouldMatchExpectations()
        {
            var tokenizer = new BasicTokenizer();
            var inputStr = "The quick brown fox jumps over the lazy dog.";
            var input = inputStr.AsSpan();

            // Expected: "The", "quick", "brown", "fox", "jumps", "over", "the", "lazy", "dog", "."
            var expected = new[] { "The", "quick", "brown", "fox", "jumps", "over", "the", "lazy", "dog", "." };

            var result = new List<string>();
            foreach(var t in tokenizer.Tokenize(input))
            {
                result.Add(t.ToString());
            }

            Assert.Equal(expected, result.ToArray());
        }

        [Fact]
        public void Tokenize_MixedPunctuation()
        {
            var tokenizer = new BasicTokenizer();
            var inputStr = "a,b.c...d_e";
            var input = inputStr.AsSpan();

            // "a", ",", "b", ".", "c", "...", "d_e"
            // Note: _ is a word char in our logic (and regex \w).
            var expected = new[] { "a", ",", "b", ".", "c", "...", "d_e" };

            var result = new List<string>();
            foreach(var t in tokenizer.Tokenize(input))
            {
                result.Add(t.ToString());
            }

            Assert.Equal(expected, result.ToArray());
        }

        [Fact]
        public void TokenUtils_NormalizeToString_ShouldLowerCase()
        {
            var input = "UPPER Case".AsSpan();
            // Should be lowercased
            Assert.Equal("upper case", TokenUtils.NormalizeToString(input));
        }

        [Fact]
        public void TokenUtils_NormalizeToString_ShouldAvoidAllocIfAlreadyLower()
        {
            // Hard to test allocation without memory diagnostics, but we can verify correctness
            var input = "lower case".AsSpan();
            Assert.Equal("lower case", TokenUtils.NormalizeToString(input));
        }

        [Fact]
        public void Integration_TokenizeAndNormalize()
        {
            var tokenizer = new BasicTokenizer();
            var input = "Crème Brûlée!".AsSpan();
            // "Crème", "Brûlée", "!" -> "crème", "brûlée", "!"
            var expected = new[] { "crème", "brûlée", "!" };

            var result = new List<string>();
            foreach(var t in tokenizer.Tokenize(input))
            {
                result.Add(TokenUtils.NormalizeToString(t));
            }

            Assert.Equal(expected, result.ToArray());
        }
    }
}
