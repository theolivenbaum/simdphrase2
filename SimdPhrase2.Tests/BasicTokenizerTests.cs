using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace SimdPhrase2.Tests
{
    public class BasicTokenizerTests
    {
        [Fact]
        public void Tokenize_ShouldNormalizeAndSplit()
        {
            var tokenizer = new BasicTokenizer();
            // "  Hello, World!  " -> "hello, world!" -> ["hello", ",", "world", "!"]
            var input = "  Hello, World!  ".AsMemory();
            var expected = new[] { "hello", ",", "world", "!" };

            var result = tokenizer.Tokenize(input).Select(m => m.ToString()).ToArray();

            Assert.Equal(expected, result);
        }

        [Fact]
        public void Tokenize_EmptyString_ShouldReturnEmpty()
        {
            var tokenizer = new BasicTokenizer();
            var input = "".AsMemory();

            var result = tokenizer.Tokenize(input);

            Assert.Empty(result);
        }

        [Fact]
        public void Tokenize_Complex_ShouldMatchExpectations()
        {
            var tokenizer = new BasicTokenizer();
            var inputStr = "The quick brown fox jumps over the lazy dog.";
            var input = inputStr.AsMemory();

            // Expected: "the", "quick", "brown", "fox", "jumps", "over", "the", "lazy", "dog", "."
            var expected = new[] { "the", "quick", "brown", "fox", "jumps", "over", "the", "lazy", "dog", "." };
            var result = tokenizer.Tokenize(input).Select(m => m.ToString()).ToArray();

            Assert.Equal(expected, result);
        }

        [Fact]
        public void Tokenize_MixedPunctuation()
        {
            var tokenizer = new BasicTokenizer();
            var inputStr = "a,b.c...d_e";
            var input = inputStr.AsMemory();

            // "a", ",", "b", ".", "c", "...", "d_e"
            // Note: _ is a word char in our logic (and regex \w).
            var expected = new[] { "a", ",", "b", ".", "c", "...", "d_e" };
            var result = tokenizer.Tokenize(input).Select(m => m.ToString()).ToArray();

            Assert.Equal(expected, result);
        }

        [Fact]
        public void Tokenize_UppercasePreservation()
        {
            // Checks that we actually lowercase
            var tokenizer = new BasicTokenizer();
            var input = "UPPER Case".AsMemory();
            var expected = new[] { "upper", "case" };

            var result = tokenizer.Tokenize(input).Select(m => m.ToString()).ToArray();
            Assert.Equal(expected, result);
        }

        [Fact]
        public void Tokenize_Unicode()
        {
            var tokenizer = new BasicTokenizer();
            var input = "Crème brûlée".AsMemory();
            var expected = new[] { "crème", "brûlée" };

            var result = tokenizer.Tokenize(input).Select(m => m.ToString()).ToArray();
            Assert.Equal(expected, result);
        }
    }
}
