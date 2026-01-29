using BenchmarkDotNet.Attributes;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SimdPhrase2.Benchmarks
{
    public abstract class BaseBenchmark
    {
        protected List<string> _singleTermQueries;
        protected List<string> _phraseQueries2;
        protected List<string> _phraseQueries3;

        [Params(10_000, 100_000, 1_000_000)]
        public int N;

        protected void SetupQueries(DataGenerator generator)
        {
            _singleTermQueries = new List<string>();
            for(int i=0; i<50; i++) _singleTermQueries.Add(generator.GetRandomTerm());

            _phraseQueries2 = new List<string>();
            for(int i=0; i<50; i++) _phraseQueries2.Add(generator.GetRandomPhrase(2));

            _phraseQueries3 = new List<string>();
            for(int i=0; i<50; i++) _phraseQueries3.Add(generator.GetRandomPhrase(3));
        }
    }

    [MemoryDiagnoser]
    public class LuceneBenchmark : BaseBenchmark
    {
        private LuceneService _luceneService;

        [GlobalSetup]
        public void Setup()
        {
            var generator = new DataGenerator(42, 10000, 1.0);
            var docs = generator.GenerateDocuments(N);
            
            var tempPath = Path.Combine(Path.GetTempPath(), "SimdPhrase2.Benchmark", "Run");
            Directory.CreateDirectory(tempPath);

            _luceneService = new LuceneService(Path.Combine(tempPath, $"lucene_index_{N}"));
            _luceneService.Index(docs);
            _luceneService.PrepareSearcher();

            SetupQueries(generator);
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _luceneService?.Dispose();
            var tempPath = Path.Combine(Path.GetTempPath(), "SimdPhrase2.Benchmark", "Run");
            if (Directory.Exists(Path.Combine(tempPath, $"lucene_index_{N}"))) Directory.Delete(Path.Combine(tempPath, $"lucene_index_{N}"), true);
        }

        [Benchmark]
        public int Lucene_Search_SingleTerm()
        {
            int total = 0;
            foreach (var q in _singleTermQueries) total += _luceneService.Search(q);
            return total;
        }

        [Benchmark]
        public int Lucene_Search_Phrase_Len2()
        {
            int total = 0;
            foreach (var q in _phraseQueries2) total += _luceneService.Search(q);
            return total;
        }

        [Benchmark]
        public int Lucene_Search_Phrase_Len3()
        {
            int total = 0;
            foreach (var q in _phraseQueries3) total += _luceneService.Search(q);
            return total;
        }
    }

    [MemoryDiagnoser]
    public class SimdPhraseBenchmark : BaseBenchmark
    {
        private SimdPhraseService _simdPhraseService;

        [GlobalSetup]
        public void Setup()
        {
            var generator = new DataGenerator(42, 10000, 1.0);
            var docs = generator.GenerateDocuments(N);

            var tempPath = Path.Combine(Path.GetTempPath(), "SimdPhrase2.Benchmark", "Run");

            _simdPhraseService = new SimdPhraseService(Path.Combine(tempPath, $"simd_index_{N}"));
            _simdPhraseService.Index(docs);
            _simdPhraseService.PrepareSearcher();

            SetupQueries(generator);
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _simdPhraseService?.Dispose();
            var tempPath = Path.Combine(Path.GetTempPath(), "SimdPhrase2.Benchmark", "Run");

            if (Directory.Exists(Path.Combine(tempPath, $"simd_index_{N}"))) Directory.Delete(Path.Combine(tempPath, $"simd_index_{N}"), true);
        }

        [Benchmark]
        public int SimdPhrase_Search_SingleTerm()
        {
            int total = 0;
            foreach (var q in _singleTermQueries) total += _simdPhraseService.Search(q);
            return total;
        }

        [Benchmark]
        public int SimdPhrase_Search_Phrase_Len2()
        {
            int total = 0;
            foreach (var q in _phraseQueries2) total += _simdPhraseService.Search(q);
            return total;
        }

        [Benchmark]
        public int SimdPhrase_Search_Phrase_Len3()
        {
            int total = 0;
            foreach (var q in _phraseQueries3) total += _simdPhraseService.Search(q);
            return total;
        }
    }
}
