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
    /// Deletion throughput. The index is pre-built once in [GlobalSetup]; for each
    /// benchmark iteration the template is copied into a fresh working directory
    /// (in [IterationSetup], not measured) and only the open + delete + commit
    /// path is timed.
    /// </summary>
    [MemoryDiagnoser]
    [SimpleJob(launchCount: 1, warmupCount: 1, iterationCount: 5, invocationCount: 1)]
    public class DeletionBenchmark
    {
        [Params(10_000, 100_000)]
        public int N;

        private const int DeleteCount = 1_000;
        private uint[] _toDelete;
        private string _basePath;
        private string _luceneTemplate;
        private string _simdTemplate;

        private string _luceneWorkDir;
        private string _simdWorkDir;

        [GlobalSetup]
        public void Setup()
        {
            var generator = new DataGenerator(42, 10_000, 1.0);
            var docs = generator.GenerateDocuments(N);
            _basePath = Path.Combine(Path.GetTempPath(), "SimdPhrase2.Benchmark", "Deletion");
            Directory.CreateDirectory(_basePath);

            var rng = new Random(123);
            _toDelete = new uint[DeleteCount];
            for (int i = 0; i < DeleteCount; i++) _toDelete[i] = (uint)rng.Next(N);

            // Pre-build template indexes once. These are NOT measured.
            _luceneTemplate = Path.Combine(_basePath, $"lucene_tmpl_{N}");
            _simdTemplate = Path.Combine(_basePath, $"simd_tmpl_{N}");
            if (Directory.Exists(_luceneTemplate)) Directory.Delete(_luceneTemplate, true);
            if (Directory.Exists(_simdTemplate)) Directory.Delete(_simdTemplate, true);

            using (var lucene = new LuceneService(_luceneTemplate))
            {
                lucene.Index(docs);
            }
            using (var simd = new SimdPhraseService(_simdTemplate, forceNaive: false))
            {
                simd.Index(docs);
            }
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            try { Directory.Delete(_basePath, true); } catch { }
        }

        [IterationSetup(Target = nameof(Lucene_Delete))]
        public void SetupLucene()
        {
            _luceneWorkDir = Path.Combine(_basePath, $"lucene_work_{N}_{Guid.NewGuid():N}");
            CopyDirectory(_luceneTemplate, _luceneWorkDir);
        }

        [IterationCleanup(Target = nameof(Lucene_Delete))]
        public void CleanupLucene()
        {
            try { if (_luceneWorkDir != null) Directory.Delete(_luceneWorkDir, true); } catch { }
        }

        [IterationSetup(Target = nameof(SimdPhrase_Delete))]
        public void SetupSimd()
        {
            _simdWorkDir = Path.Combine(_basePath, $"simd_work_{N}_{Guid.NewGuid():N}");
            CopyDirectory(_simdTemplate, _simdWorkDir);
        }

        [IterationCleanup(Target = nameof(SimdPhrase_Delete))]
        public void CleanupSimd()
        {
            try { if (_simdWorkDir != null) Directory.Delete(_simdWorkDir, true); } catch { }
        }

        [Benchmark]
        public int Lucene_Delete()
        {
            using (var lucene = new LuceneService(_luceneWorkDir))
            {
                lucene.DeleteByIds(_toDelete);
            }
            return _toDelete.Length;
        }

        [Benchmark]
        public int SimdPhrase_Delete()
        {
            using (var indexer = new Indexer(_simdWorkDir, new IndexerOptions { ClearExisting = false }))
            {
                foreach (var id in _toDelete) indexer.DeleteDocument(id);
                indexer.Commit();
            }
            return _toDelete.Length;
        }

        private static void CopyDirectory(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(dir.Replace(src, dst));
            }
            foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                File.Copy(file, file.Replace(src, dst), overwrite: true);
            }
        }
    }
}
