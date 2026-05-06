using BenchmarkDotNet.Attributes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SimdPhrase2.Benchmarks
{
    /// <summary>
    /// Cold-start indexing throughput. Each iteration writes a fresh index from
    /// scratch using the same generated corpus.
    /// </summary>
    [MemoryDiagnoser]
    [SimpleJob(launchCount: 1, warmupCount: 0, iterationCount: 1, invocationCount: 1)]
    public class IndexingBenchmark
    {
        [Params(10_000, 100_000)]
        public int N;

        private List<(string, uint)> _docs;
        private string _basePath;

        [GlobalSetup]
        public void Setup()
        {
            var generator = new DataGenerator(42, 10_000, 1.0);
            _docs = generator.GenerateDocuments(N);
            _basePath = Path.Combine(Path.GetTempPath(), "SimdPhrase2.Benchmark", "Indexing");
            Directory.CreateDirectory(_basePath);
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            try { Directory.Delete(_basePath, true); } catch { }
        }

        [Benchmark]
        public int Lucene_Index()
        {
            string path = Path.Combine(_basePath, $"lucene_{N}_{Guid.NewGuid():N}");
            using (var lucene = new LuceneService(path))
            {
                lucene.Index(_docs);
            }
            try { Directory.Delete(path, true); } catch { }
            return _docs.Count;
        }

        [Benchmark]
        public int SimdPhrase_Index()
        {
            string path = Path.Combine(_basePath, $"simd_{N}_{Guid.NewGuid():N}");
            using (var simd = new SimdPhraseService(path, forceNaive: false))
            {
                simd.Index(_docs);
            }
            try { Directory.Delete(path, true); } catch { }
            return _docs.Count;
        }
    }

    /// <summary>
    /// Deletion throughput. Index is built once during setup, then per iteration
    /// we open a writer, delete a fixed number of doc ids, and commit.
    /// </summary>
    [MemoryDiagnoser]
    [SimpleJob(launchCount: 1, warmupCount: 0, iterationCount: 1, invocationCount: 1)]
    public class DeletionBenchmark
    {
        [Params(10_000, 100_000)]
        public int N;

        private const int DeleteCount = 1_000;
        private List<(string, uint)> _docs;
        private uint[] _toDelete;
        private string _basePath;

        [GlobalSetup]
        public void Setup()
        {
            var generator = new DataGenerator(42, 10_000, 1.0);
            _docs = generator.GenerateDocuments(N);
            _basePath = Path.Combine(Path.GetTempPath(), "SimdPhrase2.Benchmark", "Deletion");
            Directory.CreateDirectory(_basePath);

            var rng = new Random(123);
            _toDelete = new uint[DeleteCount];
            for (int i = 0; i < DeleteCount; i++) _toDelete[i] = (uint)rng.Next(N);
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            try { Directory.Delete(_basePath, true); } catch { }
        }

        [Benchmark]
        public int Lucene_Delete()
        {
            string path = Path.Combine(_basePath, $"lucene_{N}_{Guid.NewGuid():N}");
            using (var lucene = new LuceneService(path))
            {
                lucene.Index(_docs);
                lucene.DeleteByIds(_toDelete);
            }
            try { Directory.Delete(path, true); } catch { }
            return _toDelete.Length;
        }

        [Benchmark]
        public int SimdPhrase_Delete()
        {
            string path = Path.Combine(_basePath, $"simd_{N}_{Guid.NewGuid():N}");
            using (var indexer = new Indexer(path))
            {
                indexer.Index(_docs);
            }
            using (var indexer = new Indexer(path, new IndexerOptions { ClearExisting = false }))
            {
                foreach (var id in _toDelete) indexer.DeleteDocument(id);
                indexer.Commit();
            }
            try { Directory.Delete(path, true); } catch { }
            return _toDelete.Length;
        }
    }
}
