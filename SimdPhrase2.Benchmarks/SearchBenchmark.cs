using BenchmarkDotNet.Attributes;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SimdPhrase2.Benchmarks
{
    [MemoryDiagnoser]
    public class SearchBenchmark
    {
        private LuceneService _luceneService;
        private SimdPhraseService _simdPhraseService;
        private List<string> _singleTermQueries;
        private List<string> _phraseQueries2;
        private List<string> _phraseQueries3;

        [Params(10_000, 100_000, 1_000_000)]
        public int N; // Number of documents
        // Skipping 1M for now as it might be too slow for CI/sandbox limits,
        // but user asked for it. I'll add 500k instead of 1M to be safer or just 1M.
        // Let's try 1M but be aware of timeouts.
        // Actually, previous 10k took ~8ms per op for SimdPhrase, ~50ms for Lucene.
        // Indexing time for 1M docs might be significant.
        // Given sandbox constraints (timeout ~5-10 mins), 1M might timeout during setup.
        // I will use 10k and 100k for now to be safe.
        // Wait, user explicitly asked for 1M. I should try to include it or note why I didn't.
        // I'll add 200_000 as a proxy for "large" if I can't do 1M.
        // But let's stick to user request but maybe comment out 1M if it fails?
        // Let's use 10_000 and 100_000 for this iteration to ensure completion.

        // Actually, user asked for "10k, 100k, 1m". I should probably try to respect that.
        // But 1M doc generation and indexing in setup will definitely kill the process if it's not super fast.
        // Generating 10k took some time. 100x that might be minutes.
        // I'll stick to 10k, 100k for safety in this environment.

        [GlobalSetup]
        public void Setup()
        {
            var generator = new DataGenerator(42, 10000, 1.0);
            var docs = generator.GenerateDocuments(N);

            // Lucene
            _luceneService = new LuceneService($"lucene_index_{N}");
            _luceneService.Index(docs);
            _luceneService.PrepareSearcher();

            // SimdPhrase
            _simdPhraseService = new SimdPhraseService($"simd_index_{N}");
            _simdPhraseService.Index(docs);
            _simdPhraseService.PrepareSearcher();

            _singleTermQueries = new List<string>();
            for(int i=0; i<50; i++)
            {
                _singleTermQueries.Add(generator.GetRandomTerm());
            }

            _phraseQueries2 = new List<string>();
            for(int i=0; i<50; i++)
            {
                _phraseQueries2.Add(generator.GetRandomPhrase(2));
            }

            _phraseQueries3 = new List<string>();
            for(int i=0; i<50; i++)
            {
                _phraseQueries3.Add(generator.GetRandomPhrase(3));
            }
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _luceneService?.Dispose();
            _simdPhraseService?.Dispose();
            if (Directory.Exists($"lucene_index_{N}")) Directory.Delete($"lucene_index_{N}", true);
            if (Directory.Exists($"simd_index_{N}")) Directory.Delete($"simd_index_{N}", true);
        }

        [Benchmark]
        public void Lucene_Search_SingleTerm()
        {
            foreach (var q in _singleTermQueries)
            {
                _luceneService.Search(q);
            }
        }

        [Benchmark]
        public void SimdPhrase_Search_SingleTerm()
        {
             foreach (var q in _singleTermQueries)
            {
                _simdPhraseService.Search(q);
            }
        }

        [Benchmark]
        public void Lucene_Search_Phrase_Len2()
        {
             foreach (var q in _phraseQueries2)
            {
                _luceneService.Search(q);
            }
        }

        [Benchmark]
        public void SimdPhrase_Search_Phrase_Len2()
        {
             foreach (var q in _phraseQueries2)
            {
                _simdPhraseService.Search(q);
            }
        }

        [Benchmark]
        public void Lucene_Search_Phrase_Len3()
        {
             foreach (var q in _phraseQueries3)
            {
                _luceneService.Search(q);
            }
        }

        [Benchmark]
        public void SimdPhrase_Search_Phrase_Len3()
        {
             foreach (var q in _phraseQueries3)
            {
                _simdPhraseService.Search(q);
            }
        }
    }
}
