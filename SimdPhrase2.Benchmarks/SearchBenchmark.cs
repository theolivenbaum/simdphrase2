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
        private List<string> _phraseQueries;

        [Params(10000)]
        public int N; // Number of documents

        [GlobalSetup]
        public void Setup()
        {
            var generator = new DataGenerator(42);
            var docs = generator.GenerateDocuments(N);

            _luceneService = new LuceneService("lucene_index");
            _luceneService.Index(docs);
            _luceneService.PrepareSearcher();

            _simdPhraseService = new SimdPhraseService("simd_index");
            _simdPhraseService.Index(docs);
            _simdPhraseService.PrepareSearcher();

            _singleTermQueries = new List<string>();
            // Generate some queries. We want them to be found sometimes.
            // Using random terms from vocabulary ensures they exist in vocabulary, but maybe not in documents if N is small.
            // But with N=10000, most terms should be present.
            for(int i=0; i<100; i++)
            {
                _singleTermQueries.Add(generator.GetRandomTerm());
            }

            _phraseQueries = new List<string>();
            for(int i=0; i<100; i++)
            {
                _phraseQueries.Add(generator.GetRandomPhrase());
            }
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _luceneService.Dispose();
            _simdPhraseService.Dispose();
            if (Directory.Exists("lucene_index")) Directory.Delete("lucene_index", true);
            if (Directory.Exists("simd_index")) Directory.Delete("simd_index", true);
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
        public void Lucene_Search_Phrase()
        {
             foreach (var q in _phraseQueries)
            {
                _luceneService.Search(q);
            }
        }

        [Benchmark]
        public void SimdPhrase_Search_Phrase()
        {
             foreach (var q in _phraseQueries)
            {
                _simdPhraseService.Search(q);
            }
        }
    }
}
