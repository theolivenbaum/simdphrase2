using BenchmarkDotNet.Attributes;
using System.Collections.Generic;
using System.IO;

namespace SimdPhrase2.Benchmarks
{
    [MemoryDiagnoser]
    public class BM25Benchmark : BaseBenchmark
    {
        private LuceneService _luceneService;
        private SimdPhraseService _simdPhraseService;
        private List<string> _bm25Queries;

        [GlobalSetup]
        public void Setup()
        {
            var generator = new DataGenerator(42, 10000, 1.0);
            var docs = generator.GenerateDocuments(N);

            var tempPath = Path.Combine(Path.GetTempPath(), "SimdPhrase2.Benchmark", "RunBM25");
            Directory.CreateDirectory(tempPath);

            _luceneService = new LuceneService(Path.Combine(tempPath, $"lucene_bm25_{N}"), useBm25: true);
            _luceneService.Index(docs);
            _luceneService.PrepareSearcher();

            _simdPhraseService = new SimdPhraseService(Path.Combine(tempPath, $"simd_bm25_{N}"), forceNaive: false);
            _simdPhraseService.Index(docs);
            _simdPhraseService.PrepareSearcher();

            _bm25Queries = new List<string>();
            for(int i=0; i<50; i++)
            {
                // Mix of single term and 2-term queries
                if (i % 2 == 0) _bm25Queries.Add(generator.GetRandomTerm());
                else _bm25Queries.Add(generator.GetRandomPhrase(2));
            }
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _luceneService?.Dispose();
            _simdPhraseService?.Dispose();
            var tempPath = Path.Combine(Path.GetTempPath(), "SimdPhrase2.Benchmark", "RunBM25");
            if (Directory.Exists(tempPath)) Directory.Delete(tempPath, true);
        }

        [Benchmark]
        public int Lucene_BM25()
        {
            int total = 0;
            foreach (var q in _bm25Queries) total += _luceneService.SearchBM25(q, 10);
            return total;
        }

        [Benchmark]
        public int SimdPhrase_BM25()
        {
            int total = 0;
            foreach (var q in _bm25Queries) total += _simdPhraseService.SearchBM25(q, 10);
            return total;
        }
    }
}
