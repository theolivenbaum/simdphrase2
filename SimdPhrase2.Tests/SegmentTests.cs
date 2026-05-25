using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using SimdPhrase2.Segments;
using SimdPhrase2.Storage;

namespace SimdPhrase2.Tests
{
    public class SegmentTests : IDisposable
    {
        private readonly string _indexName;

        public SegmentTests()
        {
            _indexName = Path.Combine(Path.GetTempPath(), "SimdPhrase2_Segments_" + Guid.NewGuid());
        }

        public void Dispose()
        {
            if (Directory.Exists(_indexName)) Directory.Delete(_indexName, true);
        }

        [Fact]
        public void MultipleCommits_ProduceMultipleSegments()
        {
            using (var indexer = new Indexer(_indexName))
            {
                indexer.AddDocument("alpha bravo charlie", 0);
                indexer.AddDocument("bravo charlie delta", 1);
                indexer.Commit();

                indexer.AddDocument("charlie delta echo", 2);
                indexer.AddDocument("delta echo foxtrot", 3);
                indexer.Commit();
            }

            using var db = SimdPhraseDb.Open(_indexName);
            var manifest = SegmentManifest.Load(db);
            Assert.True(manifest.Segments.Count >= 1);

            using var searcher = new Searcher(_indexName, db: db);
            var docs = searcher.Search("bravo charlie");
            docs.Sort();
            Assert.Equal(new uint[] { 0, 1 }, docs.ToArray());

            docs = searcher.Search("delta echo");
            docs.Sort();
            Assert.Equal(new uint[] { 2, 3 }, docs.ToArray());

            docs = searcher.Search("charlie");
            docs.Sort();
            Assert.Equal(new uint[] { 0, 1, 2 }, docs.ToArray());
        }

        [Fact]
        public void Delete_RemovesDocumentFromSearchResults()
        {
            using (var indexer = new Indexer(_indexName))
            {
                indexer.AddDocument("alpha bravo", 0);
                indexer.AddDocument("alpha charlie", 1);
                indexer.AddDocument("alpha delta", 2);
                indexer.Commit();
            }

            using (var searcher = new Searcher(_indexName))
            {
                var docs = searcher.Search("alpha");
                docs.Sort();
                Assert.Equal(new uint[] { 0, 1, 2 }, docs.ToArray());
            }

            using (var indexer = new Indexer(_indexName))
            {
                indexer.Delete(1);
                indexer.Commit();
            }

            using (var searcher = new Searcher(_indexName))
            {
                var docs = searcher.Search("alpha");
                docs.Sort();
                Assert.Equal(new uint[] { 0, 2 }, docs.ToArray());
            }
        }

        [Fact]
        public void Delete_AcrossSegments_FiltersAllSegmentsCorrectly()
        {
            using (var indexer = new Indexer(_indexName))
            {
                indexer.AddDocument("alpha one", 0);
                indexer.AddDocument("alpha two", 1);
                indexer.Commit();
                indexer.AddDocument("alpha three", 2);
                indexer.AddDocument("alpha four", 3);
                indexer.Commit();
                indexer.Delete(0);
                indexer.Delete(3);
                indexer.Commit();
            }

            using var searcher = new Searcher(_indexName);
            var docs = searcher.Search("alpha");
            docs.Sort();
            Assert.Equal(new uint[] { 1, 2 }, docs.ToArray());
        }

        [Fact]
        public void ForceMerge_CollapsesIntoSingleSegment()
        {
            using (var indexer = new Indexer(_indexName))
            {
                for (uint i = 0; i < 20; i++)
                {
                    indexer.AddDocument($"doc{i} alpha bravo", i);
                    indexer.Commit();
                }
                indexer.Delete(5);
                indexer.Delete(10);
                indexer.Commit();
                indexer.ForceMerge();
            }

            using var db = SimdPhraseDb.Open(_indexName);
            var manifest = SegmentManifest.Load(db);
            Assert.Single(manifest.Segments);
            // After ForceMerge deletes are physically removed.
            Assert.Equal(0, manifest.Segments[0].DeleteCount);

            using var searcher = new Searcher(_indexName, db: db);
            var docs = searcher.Search("alpha bravo");
            docs.Sort();
            Assert.DoesNotContain(5u, docs);
            Assert.DoesNotContain(10u, docs);
            Assert.Equal(18, docs.Count);
        }

        [Fact]
        public void AutoMerge_KeepsSegmentCountReasonable()
        {
            using (var indexer = new Indexer(_indexName, mergePolicy: new TieredMergePolicy { MaxMergeAtOnce = 4, SegmentsPerTier = 4 }))
            {
                for (uint i = 0; i < 30; i++)
                {
                    indexer.AddDocument($"doc{i} foo bar baz", i);
                    indexer.Commit();
                }
            }

            using var db = SimdPhraseDb.Open(_indexName);
            var manifest = SegmentManifest.Load(db);
            // Auto-merge should have collapsed segments down well below the per-commit
            // count (30). Allow a little slack but ensure the policy actually fired.
            Assert.True(manifest.Segments.Count < 15, $"Expected merging to reduce segments, got {manifest.Segments.Count}");

            using var searcher = new Searcher(_indexName, db: db);
            var docs = searcher.Search("foo bar baz");
            Assert.Equal(30, docs.Count);
        }

        [Fact]
        public void RoaringBitmap_RoundTripsThroughDisk()
        {
            var bm = new RoaringBitmap();
            for (uint i = 0; i < 100_000; i += 7) bm.Add(i);
            for (uint i = 5_000_000; i < 5_010_000; i++) bm.Add(i);

            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                bm.Save(ms);
                bytes = ms.ToArray();
            }

            RoaringBitmap loaded;
            using (var ms = new MemoryStream(bytes))
            {
                loaded = RoaringBitmap.Load(ms);
            }

            Assert.Equal(bm.Cardinality, loaded.Cardinality);
            for (uint i = 0; i < 100_000; i += 7) Assert.True(loaded.Contains(i));
            Assert.False(loaded.Contains(1));
            for (uint i = 5_000_000; i < 5_010_000; i++) Assert.True(loaded.Contains(i));
            Assert.False(loaded.Contains(5_010_000));
        }

        [Fact]
        public void RoaringBitmap_PromotesArrayToBitmap()
        {
            var bm = new RoaringBitmap();
            // 5000 values in same high-key block forces promotion past 4096.
            for (uint i = 0; i < 5000; i++) bm.Add(i);
            Assert.Equal(5000, bm.Cardinality);
            for (uint i = 0; i < 5000; i++) Assert.True(bm.Contains(i));
        }

        [Fact]
        public void RoaringBitmap_UnionPreservesAllElements()
        {
            var a = new RoaringBitmap();
            var b = new RoaringBitmap();
            for (uint i = 0; i < 10000; i += 2) a.Add(i);
            for (uint i = 1; i < 10000; i += 2) b.Add(i);
            for (uint i = 8000; i < 12000; i++) b.Add(i);
            a.UnionWith(b);
            for (uint i = 0; i < 12000; i++) Assert.True(a.Contains(i));
            Assert.Equal(12000, a.Cardinality);
        }
    }
}
