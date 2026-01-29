using BenchmarkDotNet.Attributes;
using System.Collections.Generic;
using System.IO;

namespace SimdPhrase2.Benchmarks
{
    [MemoryDiagnoser]
    public class BooleanBenchmark : BaseBenchmark
    {
        private LuceneService _luceneService;
        private SimdPhraseService _simdPhraseService;
        private List<string> _booleanQueries;

        [GlobalSetup]
        public void Setup()
        {
            var generator = new DataGenerator(42, 10000, 1.0);
            var docs = generator.GenerateDocuments(N);

            var tempPath = Path.Combine(Path.GetTempPath(), "SimdPhrase2.Benchmark", "RunBoolean");
            Directory.CreateDirectory(tempPath);

            _luceneService = new LuceneService(Path.Combine(tempPath, $"lucene_bool_{N}"));
            _luceneService.Index(docs);
            _luceneService.PrepareSearcher();

            _simdPhraseService = new SimdPhraseService(Path.Combine(tempPath, $"simd_bool_{N}"), forceNaive: false);
            _simdPhraseService.Index(docs);
            _simdPhraseService.PrepareSearcher();

            _booleanQueries = new List<string>();
            for(int i=0; i<50; i++)
            {
                _booleanQueries.Add(generator.GetRandomBooleanQuery());
            }
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _luceneService?.Dispose();
            _simdPhraseService?.Dispose();
            var tempPath = Path.Combine(Path.GetTempPath(), "SimdPhrase2.Benchmark", "RunBoolean");
            if (Directory.Exists(tempPath)) Directory.Delete(tempPath, true);
        }

        [Benchmark]
        public int Lucene_Boolean()
        {
            int total = 0;
            foreach (var q in _booleanQueries) total += _luceneService.SearchBoolean(q);
            return total;
        }

        [Benchmark]
        public int SimdPhrase_Boolean()
        {
            int total = 0;
            foreach (var q in _booleanQueries) total += _simdPhraseService.SearchBoolean(q);
            return total;
        }
    }
}
