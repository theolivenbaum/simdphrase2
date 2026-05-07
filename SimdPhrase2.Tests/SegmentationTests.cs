using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using SimdPhrase2;
using SimdPhrase2.Db;
using SimdPhrase2.Storage;

namespace SimdPhrase2.Tests
{
    public class SegmentationTests : IDisposable
    {
        private readonly string _indexName;

        public SegmentationTests()
        {
            _indexName = Path.Combine(Path.GetTempPath(), "SimdPhrase2_Segments_" + Guid.NewGuid());
        }

        public void Dispose()
        {
            if (Directory.Exists(_indexName)) Directory.Delete(_indexName, true);
        }

        private string SegmentsRoot => Path.Combine(_indexName, "segments");

        [Fact]
        public void MultipleCommits_CreateMultipleSegments()
        {
            using (var indexer = new Indexer(_indexName, new IndexerOptions { MaxSegmentsBeforeCompact = 100 }))
            {
                indexer.AddDocument("apple banana", 0);
                indexer.Commit();

                indexer.AddDocument("cherry date", 1);
                indexer.Commit();

                indexer.AddDocument("elderberry fig", 2);
                indexer.Commit();
            }

            var segDirs = Directory.GetDirectories(SegmentsRoot);
            Assert.Equal(3, segDirs.Length);

            using (var searcher = new Searcher(_indexName))
            {
                Assert.Equal(new uint[] { 0 }, searcher.Search("apple"));
                Assert.Equal(new uint[] { 1 }, searcher.Search("cherry"));
                Assert.Equal(new uint[] { 2 }, searcher.Search("fig"));
            }
        }

        [Fact]
        public void AutoCompact_AfterThresholdSegments()
        {
            using (var indexer = new Indexer(_indexName, new IndexerOptions { MaxSegmentsBeforeCompact = 3 }))
            {
                for (uint i = 0; i < 5; i++)
                {
                    indexer.AddDocument($"token{i} extrafiller", i);
                    indexer.Commit();
                }
            }

            // After 5 commits with threshold 3, we should have fewer than 5 segments
            // because at least one auto-compaction must have run.
            var segDirs = Directory.GetDirectories(SegmentsRoot);
            Assert.True(segDirs.Length < 5, $"Expected auto-compaction to reduce segment count, got {segDirs.Length}");

            using (var searcher = new Searcher(_indexName))
            {
                for (uint i = 0; i < 5; i++)
                {
                    var r = searcher.Search($"token{i}");
                    Assert.Equal(new uint[] { i }, r);
                }
            }
        }

        [Fact]
        public void ManualCompact_PreservesAllDocs()
        {
            using (var indexer = new Indexer(_indexName, new IndexerOptions { MaxSegmentsBeforeCompact = 100 }))
            {
                for (uint i = 0; i < 10; i++)
                {
                    indexer.AddDocument($"doc{i} word{i % 3}", i);
                    indexer.Commit();
                }

                Assert.Equal(10, Directory.GetDirectories(SegmentsRoot).Length);

                indexer.CompactAll();
                indexer.Commit();
            }

            Assert.Single(Directory.GetDirectories(SegmentsRoot));

            using (var searcher = new Searcher(_indexName))
            {
                for (uint i = 0; i < 10; i++)
                {
                    var r = searcher.Search($"doc{i}");
                    Assert.Single(r);
                    Assert.Equal(i, r[0]);
                }
                // word0 should match docs 0, 3, 6, 9
                var w0 = searcher.Search("word0").OrderBy(x => x).ToArray();
                Assert.Equal(new uint[] { 0, 3, 6, 9 }, w0);
            }
        }

        [Fact]
        public void Reopen_PersistsSegments()
        {
            using (var indexer = new Indexer(_indexName, new IndexerOptions { MaxSegmentsBeforeCompact = 100 }))
            {
                indexer.AddDocument("apple banana", 0);
                indexer.Commit();
            }

            // Reopen without clearing.
            using (var indexer = new Indexer(_indexName, new IndexerOptions
            {
                ClearExisting = false,
                MaxSegmentsBeforeCompact = 100
            }))
            {
                indexer.AddDocument("cherry date", 1);
                indexer.Commit();
            }

            Assert.Equal(2, Directory.GetDirectories(SegmentsRoot).Length);

            using (var searcher = new Searcher(_indexName))
            {
                Assert.Equal(new uint[] { 0 }, searcher.Search("apple"));
                Assert.Equal(new uint[] { 1 }, searcher.Search("date"));
            }
        }

        [Fact]
        public void CrossSegmentBM25_AggregatesScores()
        {
            using (var indexer = new Indexer(_indexName, new IndexerOptions { MaxSegmentsBeforeCompact = 100 }))
            {
                indexer.AddDocument("alpha beta gamma", 0);
                indexer.Commit();

                indexer.AddDocument("alpha alpha beta", 1);
                indexer.Commit();

                indexer.AddDocument("delta", 2);
                indexer.Commit();
            }

            Assert.Equal(3, Directory.GetDirectories(SegmentsRoot).Length);

            using (var searcher = new Searcher(_indexName))
            {
                var hits = searcher.SearchBM25("alpha");
                Assert.Equal(2, hits.Count);
                // doc 1 has higher TF; expect it ranked first.
                Assert.Equal(1u, hits[0].DocId);
                Assert.Equal(0u, hits[1].DocId);
            }
        }
    }
}
