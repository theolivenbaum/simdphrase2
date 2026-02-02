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
            // Normalization emits both original (if upper) and lowercased.
            var input = "  Hello, World!  ".AsSpan();
            var expected = new[] { "Hello", "hello", ",", "World", "world", "!" };

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

            // Expected: "The", "the", "quick", "brown", "fox", "jumps", "over", "the", "lazy", "dog", "."
            var expected = new[] { "The", "the", "quick", "brown", "fox", "jumps", "over", "the", "lazy", "dog", "." };

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
        public void Tokenize_ShouldLowerCase()
        {
            var tokenizer = new BasicTokenizer();
            var input = "UPPER Case".AsSpan();
            // Should be lowercased
            var expected = new[] { "UPPER", "upper", "Case", "case" };

            var result = new List<string>();
            foreach(var t in tokenizer.Tokenize(input))
            {
                result.Add(t.ToString());
            }

            Assert.Equal(expected, result.ToArray());
        }

        [Fact]
        public void Tokenize_Unicode_ShouldLowerCase()
        {
            var tokenizer = new BasicTokenizer();
            var input = "Crème Brûlée!".AsSpan();
            // "crème", "brûlée", "!"
            var expected = new[] { "Crème", "crème", "Brûlée", "brûlée", "!" };

            var result = new List<string>();
            foreach(var t in tokenizer.Tokenize(input))
            {
                result.Add(t.ToString());
            }

            Assert.Equal(expected, result.ToArray());
        }
    }
}
