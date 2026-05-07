using BenchmarkDotNet.Attributes;
using SimdPhrase2;
using SimdPhrase2.Segments;
using System;
using System.Collections.Generic;
using System.IO;

namespace SimdPhrase2.Benchmarks
{
    // Compares indexing throughput and search latency under three regimes:
    //   Single: traditional one-shot index + one segment.
    //   Many:   index split into N batched commits, no force merge (many segments
    //           remain after auto-merge cleanup).
    //   Forced: as Many, but ForceMerge() is invoked at the end, leaving one segment.
    //
    // The Search benchmarks use the same query set so we can compare the search-time
    // overhead of multi-segment merging (Many) vs. a single segment (Single, Forced).
    [MemoryDiagnoser]
    public class SegmentMergeBenchmark
    {
        [Params(10_000, 100_000)]
        public int N;

        [Params(10, 50)]
        public int Commits;

        private List<(string content, uint docId)> _docs;
        private List<string> _singleTermQueries;
        private List<string> _phraseQueries2;
        private string _basePath;

        [GlobalSetup]
        public void Setup()
        {
            var generator = new DataGenerator(42, 10000, 1.0);
            _docs = new List<(string, uint)>(generator.GenerateDocuments(N));

            _singleTermQueries = new List<string>();
            for (int i = 0; i < 50; i++) _singleTermQueries.Add(generator.GetRandomTerm());
            _phraseQueries2 = new List<string>();
            for (int i = 0; i < 50; i++) _phraseQueries2.Add(generator.GetRandomPhrase(2));

            _basePath = Path.Combine(Path.GetTempPath(), "SimdPhrase2.Benchmark", "SegmentMerge");
            Directory.CreateDirectory(_basePath);
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            try { Directory.Delete(_basePath, true); } catch { }
        }

        // Index everything in a single commit; equivalent to the legacy single-segment
        // path. The auto-merge policy is a no-op here.
        [Benchmark(Baseline = true)]
        public int Index_Single()
        {
            string path = Path.Combine(_basePath, $"single_{N}_{Commits}");
            if (Directory.Exists(path)) Directory.Delete(path, true);
            using (var indexer = new Indexer(path))
            {
                foreach (var doc in _docs) indexer.AddDocument(doc.content, doc.docId);
                indexer.Commit();
            }
            return 1;
        }

        // Split documents into Commits batches; each Commit() produces a new segment
        // (modulo auto-merge). Measures the cost of segment writes plus tiered merges.
        [Benchmark]
        public int Index_ManyCommits_AutoMerge()
        {
            string path = Path.Combine(_basePath, $"many_{N}_{Commits}");
            if (Directory.Exists(path)) Directory.Delete(path, true);

            using var indexer = new Indexer(path);
            int per = (N + Commits - 1) / Commits;
            int idx = 0;
            for (int b = 0; b < Commits; b++)
            {
                int upper = Math.Min(idx + per, N);
                for (int i = idx; i < upper; i++)
                {
                    var d = _docs[i];
                    indexer.AddDocument(d.content, d.docId);
                }
                indexer.Commit();
                idx = upper;
            }
            return Commits;
        }

        // Same as ManyCommits, then collapse all segments to one with ForceMerge.
        // Surfaces the cost of the force-merge step (which is what the user pays once
        // up front to get search-time perf back to single-segment levels).
        [Benchmark]
        public int Index_ManyCommits_ForceMerge()
        {
            string path = Path.Combine(_basePath, $"force_{N}_{Commits}");
            if (Directory.Exists(path)) Directory.Delete(path, true);

            using var indexer = new Indexer(path);
            int per = (N + Commits - 1) / Commits;
            int idx = 0;
            for (int b = 0; b < Commits; b++)
            {
                int upper = Math.Min(idx + per, N);
                for (int i = idx; i < upper; i++)
                {
                    var d = _docs[i];
                    indexer.AddDocument(d.content, d.docId);
                }
                indexer.Commit();
                idx = upper;
            }
            indexer.ForceMerge();
            return 1;
        }

        // ----- Search benchmarks -----

        private Searcher _singleSearcher;
        private Searcher _manySearcher;
        private Searcher _forcedSearcher;

        [IterationSetup(Targets = new[] { nameof(Search_Single_Term), nameof(Search_Single_Phrase2) })]
        public void SetupSingleSearcher()
        {
            _singleSearcher ??= BuildSearcher("single_search", commits: 1);
        }

        [IterationSetup(Targets = new[] { nameof(Search_Many_Term), nameof(Search_Many_Phrase2) })]
        public void SetupManySearcher()
        {
            _manySearcher ??= BuildSearcher("many_search", commits: Commits);
        }

        [IterationSetup(Targets = new[] { nameof(Search_Forced_Term), nameof(Search_Forced_Phrase2) })]
        public void SetupForcedSearcher()
        {
            _forcedSearcher ??= BuildSearcher("forced_search", commits: Commits, forceMerge: true);
        }

        private Searcher BuildSearcher(string tag, int commits, bool forceMerge = false)
        {
            string path = Path.Combine(_basePath, $"{tag}_{N}_{Commits}");
            if (!Directory.Exists(path))
            {
                using var indexer = new Indexer(path);
                int per = (N + commits - 1) / commits;
                int idx = 0;
                for (int b = 0; b < commits; b++)
                {
                    int upper = Math.Min(idx + per, N);
                    for (int i = idx; i < upper; i++)
                    {
                        var d = _docs[i];
                        indexer.AddDocument(d.content, d.docId);
                    }
                    indexer.Commit();
                    idx = upper;
                }
                if (forceMerge) indexer.ForceMerge();
            }
            return new Searcher(path);
        }

        [Benchmark]
        public int Search_Single_Term()
        {
            int total = 0;
            foreach (var q in _singleTermQueries) total += _singleSearcher.Search(q).Count;
            return total;
        }

        [Benchmark]
        public int Search_Single_Phrase2()
        {
            int total = 0;
            foreach (var q in _phraseQueries2) total += _singleSearcher.Search(q).Count;
            return total;
        }

        [Benchmark]
        public int Search_Many_Term()
        {
            int total = 0;
            foreach (var q in _singleTermQueries) total += _manySearcher.Search(q).Count;
            return total;
        }

        [Benchmark]
        public int Search_Many_Phrase2()
        {
            int total = 0;
            foreach (var q in _phraseQueries2) total += _manySearcher.Search(q).Count;
            return total;
        }

        [Benchmark]
        public int Search_Forced_Term()
        {
            int total = 0;
            foreach (var q in _singleTermQueries) total += _forcedSearcher.Search(q).Count;
            return total;
        }

        [Benchmark]
        public int Search_Forced_Phrase2()
        {
            int total = 0;
            foreach (var q in _phraseQueries2) total += _forcedSearcher.Search(q).Count;
            return total;
        }
    }
}
