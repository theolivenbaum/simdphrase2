using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SimdPhrase2.Benchmarks
{
    public class DataGenerator
    {
        private readonly string[] _vocabulary;
        private readonly double[] _zipfCdf;
        private readonly Random _random;

        private static readonly string[] _commonWords = new[]
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
            "find", "long", "down", "day", "did", "get", "come", "made", "may", "part"
        };

        public DataGenerator(int seed = 42, int vocabSize = 10000, double zipfSkew = 1.0)
        {
            _random = new Random(seed);
            _vocabulary = GenerateVocabulary(vocabSize);
            _zipfCdf = GenerateZipfCdf(vocabSize, zipfSkew);
        }

        private string[] GenerateVocabulary(int size)
        {
            var vocab = new List<string>(size);
            vocab.AddRange(_commonWords);

            // Fill the rest with synthetic terms
            int current = 0;
            while (vocab.Count < size)
            {
                vocab.Add($"term{current++}");
            }

            // Truncate if _commonWords is larger than requested size (unlikely for default)
            if (vocab.Count > size)
            {
                return vocab.Take(size).ToArray();
            }
            return vocab.ToArray();
        }

        private double[] GenerateZipfCdf(int n, double s)
        {
            double[] cdf = new double[n];
            double sum = 0;
            for (int i = 1; i <= n; i++)
            {
                sum += 1.0 / Math.Pow(i, s);
            }

            double currentSum = 0;
            for (int i = 0; i < n; i++)
            {
                currentSum += 1.0 / Math.Pow(i + 1, s);
                cdf[i] = currentSum / sum;
            }
            return cdf;
        }

        private int SampleZipfIndex()
        {
            double p = _random.NextDouble();
            // Binary search for p in _zipfCdf
            int idx = Array.BinarySearch(_zipfCdf, p);
            if (idx < 0)
            {
                idx = ~idx;
            }
            if (idx >= _zipfCdf.Length) idx = _zipfCdf.Length - 1;
            return idx;
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
                    int idx = SampleZipfIndex();
                    sb.Append(_vocabulary[idx]);
                    if (j < wordCount - 1)
                        sb.Append(" ");
                }
                docs.Add((sb.ToString(), i));
            }
            return docs;
        }

        public string GetRandomTerm()
        {
             // For queries, we might want to sample uniformly from the vocabulary
             // to test both common and rare terms.
             return _vocabulary[_random.Next(_vocabulary.Length)];
        }

        public string GetRandomPhrase(int length = 2)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < length; i++)
            {
                 // Phrases in queries are often natural language, so Zipf sampling makes sense
                 // to find phrases that actually exist (or likely exist).
                 // However, testing rare phrases is also important.
                 // Let's mix: 50% chance Zipf, 50% Uniform.
                 // Actually, simpler to just use Zipf to maximize hit chance for valid phrases,
                 // or uniform for random testing.
                 // Let's stick to Zipf for phrases to ensure we are searching for things
                 // that look like the document text.
                 int idx = SampleZipfIndex();
                 sb.Append(_vocabulary[idx]);
                 if (i < length - 1) sb.Append(" ");
            }
            return sb.ToString();
        }
    }
}
