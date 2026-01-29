using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SimdPhrase2.Benchmarks
{
    public class DataGenerator
    {
        private readonly string[] _vocabulary = new[]
        {
            "the", "of", "and", "a", "to", "in", "is", "you", "that", "it",
            "he", "was", "for", "on", "are", "as", "with", "his", "they", "I",
            "at", "be", "this", "have", "from", "or", "one", "had", "by", "word",
            "but", "not", "what", "all", "were", "we", "when", "your", "can", "said",
            "there", "use", "an", "each", "which", "she", "do", "how", "their", "if",
            "will", "up", "other", "about", "out", "many", "then", "them", "these", "so",
            "some", "her", "would", "make", "like", "him", "into", "time", "has", "look",
            "two", "more", "write", "go", "see", "number", "no", "way", "could", "people",
            "my", "than", "first", "water", "been", "call", "who", "oil", "its", "now",
            "find", "long", "down", "day", "did", "get", "come", "made", "may", "part",
            "apple", "banana", "cherry", "date", "elderberry", "fig", "grape", "honeydew",
            "kiwi", "lemon", "mango", "nectarine", "orange", "pear", "quince", "raspberry",
            "strawberry", "tangerine", "ugli", "vanilla", "watermelon", "xigua", "yam", "zucchini",
            "computer", "keyboard", "mouse", "monitor", "screen", "code", "program", "software",
            "hardware", "internet", "web", "website", "server", "client", "network", "data",
            "database", "algorithm", "function", "variable", "class", "object", "method", "interface"
        };

        private readonly Random _random;

        public DataGenerator(int seed = 42)
        {
            _random = new Random(seed);
        }

        public List<(string content, uint docId)> GenerateDocuments(int count, int minWords = 10, int maxWords = 50)
        {
            var docs = new List<(string, uint)>(count);
            for (uint i = 0; i < count; i++)
            {
                int wordCount = _random.Next(minWords, maxWords + 1);
                var sb = new StringBuilder();
                for (int j = 0; j < wordCount; j++)
                {
                    sb.Append(_vocabulary[_random.Next(_vocabulary.Length)]);
                    if (j < wordCount - 1)
                        sb.Append(" ");
                }
                docs.Add((sb.ToString(), i));
            }
            return docs;
        }

        public string GetRandomTerm()
        {
             return _vocabulary[_random.Next(_vocabulary.Length)];
        }

        public string GetRandomPhrase(int length = 2)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < length; i++)
            {
                 sb.Append(_vocabulary[_random.Next(_vocabulary.Length)]);
                 if (i < length - 1) sb.Append(" ");
            }
            return sb.ToString();
        }
    }
}
