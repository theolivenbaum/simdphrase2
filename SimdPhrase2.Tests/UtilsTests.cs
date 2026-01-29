using Xunit;
using SimdPhrase2;
using System.Linq;

namespace SimdPhrase2.Tests
{
    public class UtilsTests
    {
        [Fact]
        public void Normalize_ShouldTrimAndLowerCase()
        {
            var input = "  Hello World  ";
            var expected = "hello world";
            var result = Utils.Normalize(input);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void Tokenize_ShouldSplitWordsAndPunctuation()
        {
            var input = "Hello, world!";
            var expected = new[] { "Hello", ",", "world", "!" };
            var result = Utils.Tokenize(input).ToArray();
            Assert.Equal(expected, result);
        }

        [Fact]
        public void Tokenize_ShouldIgnoreWhitespace()
        {
            var input = "Hello   world";
            var expected = new[] { "Hello", "world" };
            var result = Utils.Tokenize(input).ToArray();
            Assert.Equal(expected, result);
        }

        [Fact]
        public void Tokenize_Complex()
        {
             var input = "look at my beautiful cat";
             var expected = new[] { "look", "at", "my", "beautiful", "cat" };
             var result = Utils.Tokenize(input).ToArray();
             Assert.Equal(expected, result);
        }
    }
}
