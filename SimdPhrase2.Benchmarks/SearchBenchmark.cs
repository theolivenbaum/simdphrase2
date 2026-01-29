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

            _luceneService = new LuceneService($"lucene_index_{N}");
            _luceneService.Index(docs);
            _luceneService.PrepareSearcher();

            SetupQueries(generator);
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _luceneService?.Dispose();
            if (Directory.Exists($"lucene_index_{N}")) Directory.Delete($"lucene_index_{N}", true);
        }

        [Benchmark]
        public void Lucene_Search_SingleTerm()
        {
            foreach (var q in _singleTermQueries) _luceneService.Search(q);
        }

        [Benchmark]
        public void Lucene_Search_Phrase_Len2()
        {
            foreach (var q in _phraseQueries2) _luceneService.Search(q);
        }

        [Benchmark]
        public void Lucene_Search_Phrase_Len3()
        {
            foreach (var q in _phraseQueries3) _luceneService.Search(q);
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

            _simdPhraseService = new SimdPhraseService($"simd_index_{N}");
            _simdPhraseService.Index(docs);
            _simdPhraseService.PrepareSearcher();

            SetupQueries(generator);
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _simdPhraseService?.Dispose();
            if (Directory.Exists($"simd_index_{N}")) Directory.Delete($"simd_index_{N}", true);
        }

        [Benchmark]
        public void SimdPhrase_Search_SingleTerm()
        {
            foreach (var q in _singleTermQueries) _simdPhraseService.Search(q);
        }

        [Benchmark]
        public void SimdPhrase_Search_Phrase_Len2()
        {
            foreach (var q in _phraseQueries2) _simdPhraseService.Search(q);
        }

        [Benchmark]
        public void SimdPhrase_Search_Phrase_Len3()
        {
            foreach (var q in _phraseQueries3) _simdPhraseService.Search(q);
        }
    }
}
