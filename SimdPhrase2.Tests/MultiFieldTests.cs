using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using SimdPhrase2;

namespace SimdPhrase2.Tests
{
    public class MultiFieldTests : IDisposable
    {
        private readonly string _indexName;

        public MultiFieldTests()
        {
            _indexName = Path.Combine(Path.GetTempPath(), "SimdPhrase2_MultiField_" + Guid.NewGuid());
        }

        public void Dispose()
        {
            if (Directory.Exists(_indexName)) Directory.Delete(_indexName, true);
        }

        [Fact]
        public void IndexAndSearch_PerField()
        {
            using (var indexer = new Indexer(_indexName))
            {
                indexer.AddDocument(new IndexDocument(0).Add("title", "quick brown fox").Add("body", "lazy sleeping dog"));
                indexer.AddDocument(new IndexDocument(1).Add("title", "lazy dog").Add("body", "running quick fox"));
                indexer.Commit();
            }

            using (var searcher = new Searcher(_indexName))
            {
                Assert.Equal(new uint[] { 0 }, searcher.Search("fox", "title").OrderBy(x => x).ToArray());
                Assert.Equal(new uint[] { 1 }, searcher.Search("fox", "body").OrderBy(x => x).ToArray());

                Assert.Equal(new uint[] { 0 }, searcher.Search("dog", "body").OrderBy(x => x).ToArray());
                Assert.Equal(new uint[] { 1 }, searcher.Search("dog", "title").OrderBy(x => x).ToArray());
            }
        }

        [Fact]
        public void PhraseSearch_WithinField()
        {
            using (var indexer = new Indexer(_indexName))
            {
                indexer.AddDocument(new IndexDocument(0).Add("title", "the quick brown fox"));
                indexer.AddDocument(new IndexDocument(1).Add("body", "the quick brown fox"));
                indexer.Commit();
            }

            using (var searcher = new Searcher(_indexName))
            {
                Assert.Equal(new uint[] { 0 }, searcher.Search("quick brown", "title").ToArray());
                Assert.Equal(new uint[] { 1 }, searcher.Search("quick brown", "body").ToArray());
                Assert.Empty(searcher.Search("brown fox", "subject"));
            }
        }

        [Fact]
        public void DefaultField_MatchesExistingApi()
        {
            using (var indexer = new Indexer(_indexName))
            {
                indexer.AddDocument("hello world", 0);
                indexer.AddDocument("hello kitty", 1);
                indexer.Commit();
            }

            using (var searcher = new Searcher(_indexName))
            {
                var r = searcher.Search("hello").OrderBy(x => x).ToArray();
                Assert.Equal(new uint[] { 0, 1 }, r);
                // Calling Search(query, defaultField) should match.
                var r2 = searcher.Search("hello", SimdPhrase2.Db.FieldRegistry.DefaultField).OrderBy(x => x).ToArray();
                Assert.Equal(r, r2);
            }
        }

        [Fact]
        public void BooleanSearch_WithFieldSyntax()
        {
            using (var indexer = new Indexer(_indexName))
            {
                indexer.AddDocument(new IndexDocument(0).Add("title", "alpha").Add("body", "delta"));
                indexer.AddDocument(new IndexDocument(1).Add("title", "delta").Add("body", "alpha"));
                indexer.AddDocument(new IndexDocument(2).Add("title", "alpha").Add("body", "alpha"));
                indexer.Commit();
            }

            using (var searcher = new Searcher(_indexName))
            {
                var both = searcher.SearchBoolean("title:alpha AND body:alpha");
                Assert.Equal(new uint[] { 2 }, both.ToArray());

                var either = searcher.SearchBoolean("title:alpha OR body:alpha");
                Assert.Equal(new uint[] { 0, 1, 2 }, either.OrderBy(x => x).ToArray());
            }
        }

        [Fact]
        public void PerFieldBoost_AffectsBM25Ranking()
        {
            using (var indexer = new Indexer(_indexName, new IndexerOptions
            {
                Fields = new List<FieldOptions>
                {
                    new FieldOptions("title", boost: 5.0f),
                    new FieldOptions("body", boost: 1.0f),
                }
            }))
            {
                indexer.AddDocument(new IndexDocument(0).Add("title", "search engine").Add("body", "filler content here"));
                indexer.AddDocument(new IndexDocument(1).Add("title", "filler content here").Add("body", "search engine"));
                indexer.Commit();
            }

            using (var searcher = new Searcher(_indexName))
            {
                var titleHits = searcher.SearchBM25("search", "title");
                var bodyHits = searcher.SearchBM25("search", "body");

                // Title-boosted score should beat body score for the same hit shape.
                Assert.Equal(0u, titleHits[0].DocId);
                Assert.Equal(1u, bodyHits[0].DocId);
                Assert.True(titleHits[0].Score > bodyHits[0].Score,
                    $"Expected title boost to outscore body: title={titleHits[0].Score} body={bodyHits[0].Score}");
            }
        }
    }
}
