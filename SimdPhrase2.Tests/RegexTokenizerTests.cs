using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace SimdPhrase2.Tests
{
    public class RegexTokenizerTests
    {
        [Fact]
        public void Tokenize_ShouldNormalizeAndSplit()
        {
            var tokenizer = new RegexTokenizer();
            // "  Hello, World!  " -> "hello, world!" -> ["hello", ",", "world", "!"]
            var input = "  Hello, World!  ".AsMemory();
            var expected = new[] { "hello", ",", "world", "!" };

            var result = tokenizer.Tokenize(input).Select(m => m.ToString()).ToArray();

            Assert.Equal(expected, result);
        }

        [Fact]
        public void Tokenize_EmptyString_ShouldReturnEmpty()
        {
            var tokenizer = new RegexTokenizer();
            var input = "".AsMemory();

            var result = tokenizer.Tokenize(input);

            Assert.Empty(result);
        }

        [Fact]
        public void Tokenize_Complex_ShouldMatchUtilsBehavior()
        {
            var tokenizer = new RegexTokenizer();
            var inputStr = "The quick brown fox jumps over the lazy dog.";
            var input = inputStr.AsMemory();

            var expected = Utils.Tokenize(Utils.Normalize(inputStr)).ToArray();
            var result = tokenizer.Tokenize(input).Select(m => m.ToString()).ToArray();

            Assert.Equal(expected, result);
        }
    }
}
