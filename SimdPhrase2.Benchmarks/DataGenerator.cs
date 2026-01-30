using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;

namespace SimdPhrase2.Benchmarks
{
    public class DataGenerator
    {
        private readonly string[] _vocabulary;
        private readonly double[] _zipfCdf;
        private readonly Random _random;

        public DataGenerator(int seed = 42, int vocabSize = 10000, double zipfSkew = 1.0)
        {
            _random = new Random(seed);
            _vocabulary = GenerateVocabulary(vocabSize);
            _zipfCdf = GenerateZipfCdf(vocabSize, zipfSkew);
        }

        private string[] GenerateVocabulary(int size)
        {
            // Load real words from file or resource.
            // For now, assume words.txt is available in the output directory
            // In a real scenario, this would be an embedded resource.
            string[] realWords;
            try
            {
                realWords = File.ReadAllLines("words.txt");
            }
            catch (Exception)
            {
                // Fallback if file not found (e.g. during simple build without file copy)
                // This prevents crashes but ideally words.txt should be there.
                realWords = new[] { "the", "of", "and", "a", "to", "in", "is", "you", "that", "it" };
            }

            var vocab = new List<string>(size);

            // Filter out extremely short words or noise if desired, but 10k list is usually decent.
            foreach (var word in realWords)
            {
                if (!string.IsNullOrWhiteSpace(word))
                {
                    vocab.Add(word.Trim().ToLowerInvariant());
                    if (vocab.Count >= size) break;
                }
            }

            // If we still don't have enough, fill with synthetic
            int current = 0;
            while (vocab.Count < size)
            {
                vocab.Add($"term{current++}");
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
                    sb.Append(RandomCase(_vocabulary[idx]));
                    if (j < wordCount - 1)
                        sb.Append(" ");
                }
                docs.Add((sb.ToString(), i));
            }
            return docs;
        }

        private string RandomCase(string input)
        {
            if (_random.Next(20) < 2) return input.ToUpper();
            return input;
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
                 int idx = SampleZipfIndex();
                 sb.Append(_vocabulary[idx]);
                 if (i < length - 1) sb.Append(" ");
            }
            return sb.ToString();
        }

        public string GetRandomBooleanQuery()
        {
            // Simple generation: A AND B, A OR B, A AND (NOT B)
            int type = _random.Next(3);
            string t1 = GetRandomTerm();
            string t2 = GetRandomTerm();

            if (type == 0) return $"{t1} AND {t2}";
            if (type == 1) return $"{t1} OR {t2}";
            // Removing parentheses to ensure compatibility/simplicity with standard parsers
            if (type == 2) return $"{t1} AND NOT {t2}";

            return t1;
        }
    }
}
