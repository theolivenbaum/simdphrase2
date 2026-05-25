using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using SimdPhrase2.Queries;

namespace SimdPhrase2.Tests
{
    public class FieldsTests : IDisposable
    {
        private readonly string _indexName;

        public FieldsTests()
        {
            _indexName = Path.Combine(Path.GetTempPath(), "SimdPhrase2_Fields_" + Guid.NewGuid());
        }

        public void Dispose()
        {
            if (Directory.Exists(_indexName)) Directory.Delete(_indexName, true);
        }

        [Fact]
        public void MultiField_TermQuery_OnlyMatchesTargetField()
        {
            // Two fields: 0 = title, 1 = body. The token "alpha" appears in both
            // titles and bodies but for different docs; the field-restricted query
            // should not cross-match.

            using (var indexer = new Indexer(_indexName, fieldCount: 2))
            {
                indexer.AddDocument(0, "alpha title", "beta body");
                indexer.AddDocument(1, "gamma title", "alpha body");
                indexer.AddDocument(2, "alpha", "alpha");
                indexer.Commit();
            }

            using var searcher = new Searcher(_indexName);

            // alpha in field 0 (title): docs 0 and 2
            var titleHits = searcher.SearchField(0, "alpha");
            titleHits.Sort();
            Assert.Equal(new uint[] { 0, 2 }, titleHits.ToArray());

            // alpha in field 1 (body): docs 1 and 2
            var bodyHits = searcher.SearchField(1, "alpha");
            bodyHits.Sort();
            Assert.Equal(new uint[] { 1, 2 }, bodyHits.ToArray());
        }

        [Fact]
        public void MultiField_PhraseQuery_StaysInsideField()
        {
            // Phrase "alpha beta" exists in field 0 of doc 0 but not in field 1 of
            // doc 1 (where alpha and beta straddle two different fields). Phrase
            // search per-field must not bridge across fields.

            using (var indexer = new Indexer(_indexName, fieldCount: 2))
            {
                indexer.AddDocument(0, "alpha beta", "something else");
                indexer.AddDocument(1, "alpha", "beta gamma");
                indexer.AddDocument(2, "delta", "alpha beta");
                indexer.Commit();
            }

            using var searcher = new Searcher(_indexName);

            var f0 = searcher.SearchField(0, "alpha beta");
            f0.Sort();
            Assert.Equal(new uint[] { 0 }, f0.ToArray());

            var f1 = searcher.SearchField(1, "alpha beta");
            f1.Sort();
            Assert.Equal(new uint[] { 2 }, f1.ToArray());
        }

        [Fact]
        public void ComposableQuery_AndAcrossFields_Intersects()
        {
            using (var indexer = new Indexer(_indexName, fieldCount: 2))
            {
                indexer.AddDocument(0, "rust performance", "search engine");
                indexer.AddDocument(1, "rust performance", "compiler");
                indexer.AddDocument(2, "go performance", "search engine");
                indexer.Commit();
            }

            using var searcher = new Searcher(_indexName);

            // (field 0 contains "rust") AND (field 1 contains "search")
            var query = new AndQuery(
                new TermQuery(0, "rust"),
                new TermQuery(1, "search")
            );
            var docs = searcher.SearchBoolean(query);
            Assert.Equal(new uint[] { 0 }, docs.ToArray());
        }

        [Fact]
        public void ComposableQuery_OrAcrossFields_Unions()
        {
            using (var indexer = new Indexer(_indexName, fieldCount: 2))
            {
                indexer.AddDocument(0, "alpha", "x");
                indexer.AddDocument(1, "beta", "y");
                indexer.AddDocument(2, "gamma", "z");
                indexer.Commit();
            }

            using var searcher = new Searcher(_indexName);

            var query = new OrQuery(
                new TermQuery(0, "alpha"),
                new TermQuery(1, "z")
            );
            var docs = searcher.SearchBoolean(query);
            Assert.Equal(new uint[] { 0, 2 }, docs.ToArray());
        }

        [Fact]
        public void ComposableQuery_NotInsideAnd_Excludes()
        {
            using (var indexer = new Indexer(_indexName, fieldCount: 2))
            {
                indexer.AddDocument(0, "alpha", "tag1");
                indexer.AddDocument(1, "alpha", "tag2");
                indexer.AddDocument(2, "alpha", "tag1");
                indexer.Commit();
            }

            using var searcher = new Searcher(_indexName);

            // alpha in field 0 AND NOT (tag1 in field 1)
            var query = new AndQuery(
                new TermQuery(0, "alpha"),
                new NotQuery(new TermQuery(1, "tag1"))
            );
            var docs = searcher.SearchBoolean(query);
            Assert.Equal(new uint[] { 1 }, docs.ToArray());
        }

        [Fact]
        public void BM25_MultiField_Ranks()
        {
            // Doc with the term in both fields should outrank one that only has it in one.
            using (var indexer = new Indexer(_indexName, fieldCount: 2))
            {
                indexer.AddDocument(0, "rare", "rare");
                indexer.AddDocument(1, "rare", "other");
                indexer.AddDocument(2, "other", "other");
                indexer.Commit();
            }

            using var searcher = new Searcher(_indexName);

            // Score "rare" in BOTH fields, summed (with OrQuery the scores add up).
            var query = new OrQuery(
                new TermQuery(0, "rare"),
                new TermQuery(1, "rare")
            );
            var results = searcher.SearchBM25(query, k: 10);

            Assert.Equal(2, results.Count);
            Assert.Equal(0u, results[0].DocId);
            Assert.Equal(1u, results[1].DocId);
            Assert.True(results[0].Score > results[1].Score);
        }

        [Fact]
        public void BM25_Boost_AmplifiesContribution()
        {
            using (var indexer = new Indexer(_indexName, fieldCount: 2))
            {
                indexer.AddDocument(0, "match", "noise");
                indexer.AddDocument(1, "noise", "match");
                indexer.Commit();
            }

            using var searcher = new Searcher(_indexName);

            // Without boost: doc 0 and doc 1 have symmetric scores (different field
            // doc lengths, but the same tf=1, same idf).
            var plain = searcher.SearchBM25(new OrQuery(
                new TermQuery(0, "match"),
                new TermQuery(1, "match")
            ), k: 10);

            // With 10x boost on field 0, doc 0 should outrank doc 1.
            var boosted = searcher.SearchBM25(new OrQuery(
                new BoostQuery(new TermQuery(0, "match"), 10f),
                new TermQuery(1, "match")
            ), k: 10);

            var doc0Boosted = boosted.First(r => r.DocId == 0).Score;
            var doc1Boosted = boosted.First(r => r.DocId == 1).Score;
            Assert.True(doc0Boosted > doc1Boosted, $"Expected boosted doc 0 ({doc0Boosted}) > doc 1 ({doc1Boosted})");
        }

        [Fact]
        public void SingleField_LegacyAPI_StillWorks()
        {
            // Existing single-field code path is the default (fieldCount=1).
            using (var indexer = new Indexer(_indexName))
            {
                indexer.AddDocument("alpha bravo charlie", 0);
                indexer.AddDocument("bravo charlie delta", 1);
                indexer.Commit();
            }

            using var searcher = new Searcher(_indexName);
            var docs = searcher.Search("bravo charlie");
            docs.Sort();
            Assert.Equal(new uint[] { 0, 1 }, docs.ToArray());

            // And new composable API also works at field 0.
            var docs2 = searcher.SearchBoolean(new PhraseQuery(0, "bravo charlie"));
            docs2.Sort();
            Assert.Equal(new uint[] { 0, 1 }, docs2.ToArray());
        }

        [Fact]
        public void Reopen_PreservesFieldCount()
        {
            using (var indexer = new Indexer(_indexName, fieldCount: 3))
            {
                indexer.AddDocument(0, "a", "b", "c");
                indexer.Commit();
            }

            // Reopening with the same count works.
            using (var indexer = new Indexer(_indexName, fieldCount: 3))
            {
                indexer.AddDocument(1, "d", "e", "f");
                indexer.Commit();
            }

            // Reopening with a different count throws.
            Assert.Throws<InvalidOperationException>(() => new Indexer(_indexName, fieldCount: 2));

            using var searcher = new Searcher(_indexName);
            Assert.Equal(3, searcher.FieldCount);

            var hits = searcher.SearchField(2, "c");
            Assert.Equal(new uint[] { 0 }, hits.ToArray());
            var hits2 = searcher.SearchField(1, "e");
            Assert.Equal(new uint[] { 1 }, hits2.ToArray());
        }
    }
}
