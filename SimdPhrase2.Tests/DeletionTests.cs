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
    public class DeletionTests : IDisposable
    {
        private readonly string _indexName;

        public DeletionTests()
        {
            _indexName = Path.Combine(Path.GetTempPath(), "SimdPhrase2_Delete_" + Guid.NewGuid());
        }

        public void Dispose()
        {
            if (Directory.Exists(_indexName)) Directory.Delete(_indexName, true);
        }

        private string SegmentsRoot => Path.Combine(_indexName, "segments");

        [Fact]
        public void Delete_RemovesDocFromSearchResults()
        {
            using (var indexer = new Indexer(_indexName))
            {
                indexer.AddDocument("apple banana", 0);
                indexer.AddDocument("apple cherry", 1);
                indexer.AddDocument("banana cherry", 2);
                indexer.Commit();
            }

            using (var indexer = new Indexer(_indexName, new IndexerOptions { ClearExisting = false }))
            {
                indexer.DeleteDocument(0);
                indexer.Commit();
            }

            using (var searcher = new Searcher(_indexName))
            {
                Assert.Equal(new uint[] { 1 }, searcher.Search("apple").OrderBy(x => x).ToArray());
                Assert.Equal(new uint[] { 1, 2 }, searcher.Search("cherry").OrderBy(x => x).ToArray());
            }
        }

        [Fact]
        public void Delete_AffectsBM25Scoring()
        {
            using (var indexer = new Indexer(_indexName))
            {
                indexer.AddDocument("apple banana cherry", 0);
                indexer.AddDocument("apple", 1);
                indexer.AddDocument("apple", 2);
                indexer.Commit();
            }

            using (var indexer = new Indexer(_indexName, new IndexerOptions { ClearExisting = false }))
            {
                indexer.DeleteDocument(0);
                indexer.DeleteDocument(2);
                indexer.Commit();
            }

            using (var searcher = new Searcher(_indexName))
            {
                var hits = searcher.SearchBM25("apple");
                Assert.Single(hits);
                Assert.Equal(1u, hits[0].DocId);
            }
        }

        [Fact]
        public void Compact_PhysicallyRemovesDeletedDocs()
        {
            using (var indexer = new Indexer(_indexName, new IndexerOptions { MaxSegmentsBeforeCompact = 100 }))
            {
                indexer.AddDocument("aaa bbb", 0);
                indexer.Commit();
                indexer.AddDocument("aaa ccc", 1);
                indexer.Commit();
                indexer.AddDocument("aaa ddd", 2);
                indexer.Commit();

                indexer.DeleteDocument(1);
                indexer.CompactAll();
                indexer.Commit();
            }

            // After compaction, only one segment remains and the deleted doc is gone.
            Assert.Single(Directory.GetDirectories(SegmentsRoot));

            using (var searcher = new Searcher(_indexName))
            {
                var aaa = searcher.Search("aaa").OrderBy(x => x).ToArray();
                Assert.Equal(new uint[] { 0, 2 }, aaa);

                Assert.Empty(searcher.Search("ccc"));
                Assert.Equal(new uint[] { 0 }, searcher.Search("bbb"));
                Assert.Equal(new uint[] { 2 }, searcher.Search("ddd"));
            }

            // After compaction, the global delete set should be cleared.
            var liveDocs = LiveDocs.Load(new FileSystemStorage(), Path.Combine(_indexName, "deleted_docs.bin"));
            Assert.Equal(0, liveDocs.DeletedCount);
        }

        [Fact]
        public void Delete_PersistsAcrossSearcher()
        {
            using (var indexer = new Indexer(_indexName))
            {
                indexer.AddDocument("alpha", 0);
                indexer.AddDocument("alpha", 1);
                indexer.Commit();
            }

            using (var indexer = new Indexer(_indexName, new IndexerOptions { ClearExisting = false }))
            {
                indexer.DeleteDocument(0);
                indexer.Commit();
            }

            // Open a fresh searcher - should still see the deletion.
            using (var searcher = new Searcher(_indexName))
            {
                Assert.Equal(new uint[] { 1 }, searcher.Search("alpha"));
            }
        }

        [Fact]
        public void DeleteAndReindex_HandlesGracefully()
        {
            using (var indexer = new Indexer(_indexName))
            {
                indexer.AddDocument("foo bar", 0);
                indexer.Commit();
            }

            using (var indexer = new Indexer(_indexName, new IndexerOptions { ClearExisting = false }))
            {
                indexer.DeleteDocument(0);
                indexer.AddDocument("baz qux", 1);
                indexer.Commit();
            }

            using (var searcher = new Searcher(_indexName))
            {
                Assert.Empty(searcher.Search("foo"));
                Assert.Equal(new uint[] { 1 }, searcher.Search("baz"));
            }
        }
    }
}
