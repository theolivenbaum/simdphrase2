using BenchmarkDotNet.Attributes;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System;
using Lucene.Net.Util;

namespace SimdPhrase2.Benchmarks
{
    public abstract class BaseNGramBenchmark
    {
        protected List<string> _singleTermQueries;
        protected List<string> _phraseQueries2;
        protected List<string> _phraseQueries3;

        [Params(10_000, 100_000)]
        public int N;

        // This will be set by the implementation
        protected abstract void SetupQueries(DataGenerator generator, bool isIdentifierMode);
    }

    [MemoryDiagnoser]
    public class NGramIdentifierBenchmark : BaseNGramBenchmark
    {
        private LuceneService _luceneService;
        private SimdPhraseService _simdService;
        private const int NGramSize = 3;

        protected override void SetupQueries(DataGenerator generator, bool isIdentifierMode)
        {
             // Not used here, done in GlobalSetup
        }

        [GlobalSetup]
        public void Setup()
        {
            var tempPath = Path.Combine(Path.GetTempPath(), "SimdPhrase2.Benchmark", "RunNGramId");
            Directory.CreateDirectory(tempPath);

            // Generate 10-digit numbers
            var r = new Random(42);
            var docs = new List<(string content, uint docId)>();
            for(uint i = 0; i < N; i++)
            {
                // 10 digit number
                string num = r.NextInt64(1000000000, 9999999999).ToString();
                docs.Add((num, i));
            }

            // Lucene Setup
            var luceneAnalyzer = new NGramAnalyzer(NGramSize, NGramSize);
            _luceneService = new LuceneService(Path.Combine(tempPath, $"lucene_ngram_id_{N}"), analyzer: luceneAnalyzer);
            _luceneService.Index(docs);
            _luceneService.PrepareSearcher();

            // SimdPhrase Setup
            var simdTokenizer = new NGramTokenizer(NGramSize);
            _simdService = new SimdPhraseService(Path.Combine(tempPath, $"simd_ngram_id_{N}"), forceNaive: false, tokenizer: simdTokenizer);
            _simdService.Index(docs);
            _simdService.PrepareSearcher();

            // Setup Queries
            // We'll pick some random docs and take substrings
            _singleTermQueries = new List<string>();
            for(int i=0; i<50; i++)
            {
                var target = docs[r.Next(docs.Count)].content;
                // Query: random substring of length 4 to 6
                int len = r.Next(4, 7);
                if (len > target.Length) len = target.Length;
                int start = r.Next(0, target.Length - len);
                _singleTermQueries.Add(target.Substring(start, len));
            }
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _luceneService?.Dispose();
            _simdService?.Dispose();
            var tempPath = Path.Combine(Path.GetTempPath(), "SimdPhrase2.Benchmark", "RunNGramId");
            if (Directory.Exists(tempPath)) Directory.Delete(tempPath, true);
        }

        [Benchmark]
        public int Lucene_Search()
        {
            int total = 0;
            foreach (var q in _singleTermQueries) total += _luceneService.Search(q);
            return total;
        }

        [Benchmark]
        public int SimdPhrase_Search()
        {
            int total = 0;
            foreach (var q in _singleTermQueries) total += _simdService.Search(q);
            return total;
        }
    }

    [MemoryDiagnoser]
    public class BreakingNGramTextBenchmark : BaseNGramBenchmark
    {
        private LuceneService _luceneService;
        private SimdPhraseService _simdService;
        private const int NGramSize = 3;

        protected override void SetupQueries(DataGenerator generator, bool isIdentifierMode)
        {
             // Not used here, done in GlobalSetup
        }

        [GlobalSetup]
        public void Setup()
        {
            var tempPath = Path.Combine(Path.GetTempPath(), "SimdPhrase2.Benchmark", "RunNGramText");
            Directory.CreateDirectory(tempPath);

            var generator = new DataGenerator(42, 10000, 1.0);
            var docs = generator.GenerateDocuments(N);

            // Custom Analyzer for Lucene
            var luceneAnalyzer = new WhitespaceNGramAnalyzer(NGramSize, NGramSize);
            _luceneService = new LuceneService(Path.Combine(tempPath, $"lucene_ngram_text_{N}"), analyzer: luceneAnalyzer);
            _luceneService.Index(docs);
            _luceneService.PrepareSearcher();

            // SimdPhrase Setup
            // BreakingNGramTokenizer defaults to whitespace breaking
            var simdTokenizer = new BreakingNGramTokenizer(NGramSize);
            _simdService = new SimdPhraseService(Path.Combine(tempPath, $"simd_ngram_text_{N}"), forceNaive: false, tokenizer: simdTokenizer);
            _simdService.Index(docs);
            _simdService.PrepareSearcher();

            // Setup Queries
            _singleTermQueries = new List<string>();

            for(int i=0; i<50; i++) _singleTermQueries.Add(generator.GetRandomTerm());

            _phraseQueries2 = new List<string>();
            for(int i=0; i<50; i++) _phraseQueries2.Add(generator.GetRandomPhrase(2));
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _luceneService?.Dispose();
            _simdService?.Dispose();
            var tempPath = Path.Combine(Path.GetTempPath(), "SimdPhrase2.Benchmark", "RunNGramText");
            if (Directory.Exists(tempPath)) Directory.Delete(tempPath, true);
        }

        [Benchmark]
        public int Lucene_Search_Term()
        {
            int total = 0;
            foreach (var q in _singleTermQueries) total += _luceneService.Search(q);
            return total;
        }

        [Benchmark]
        public int SimdPhrase_Search_Term()
        {
            int total = 0;
            foreach (var q in _singleTermQueries) total += _simdService.Search(q);
            return total;
        }

        [Benchmark]
        public int Lucene_Search_Phrase2()
        {
            int total = 0;
            foreach (var q in _phraseQueries2) total += _luceneService.Search(q);
            return total;
        }

        [Benchmark]
        public int SimdPhrase_Search_Phrase2()
        {
            int total = 0;
            foreach (var q in _phraseQueries2) total += _simdService.Search(q);
            return total;
        }
    }

    public class WhitespaceNGramAnalyzer : Lucene.Net.Analysis.Analyzer
    {
        private readonly int _minGram;
        private readonly int _maxGram;

        public WhitespaceNGramAnalyzer(int minGram, int maxGram)
        {
            _minGram = minGram;
            _maxGram = maxGram;
        }

        protected override Lucene.Net.Analysis.TokenStreamComponents CreateComponents(string fieldName, TextReader reader)
        {
            var tokenizer = new Lucene.Net.Analysis.Core.WhitespaceTokenizer(LuceneVersion.LUCENE_48, reader);
            var filter = new Lucene.Net.Analysis.Core.LowerCaseFilter(LuceneVersion.LUCENE_48, tokenizer);
            var ngram = new Lucene.Net.Analysis.NGram.NGramTokenFilter(LuceneVersion.LUCENE_48, filter, _minGram, _maxGram);
            return new Lucene.Net.Analysis.TokenStreamComponents(tokenizer, ngram);
        }
    }
}
