using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SimdPhrase2;
using SimdPhrase2.Analysis;
using Xunit;

namespace SimdPhrase2.Tests
{
    public class PrefixSearchTests : IDisposable
    {
        private string _indexName;

        public PrefixSearchTests()
        {
            _indexName = Path.Combine(Path.GetTempPath(), "SimdPhrase2_PrefixSearchTests_" + Guid.NewGuid());
        }

        public void Dispose()
        {
            if (Directory.Exists(_indexName)) Directory.Delete(_indexName, true);
        }

        [Fact]
        public void FstPrefixMatcher_ShouldMatchPrefixes()
        {
            var tokens = new[] { "apple", "app", "application", "banana", "band" };
            var matcher = new FstPrefixMatcher(tokens);

            var res1 = matcher.Match("app").OrderBy(x => x).ToArray();
            Assert.Equal(new[] { "app", "apple", "application" }, res1);

            var res2 = matcher.Match("ban").OrderBy(x => x).ToArray();
            Assert.Equal(new[] { "banana", "band" }, res2);

            var res3 = matcher.Match("z").ToArray();
            Assert.Empty(res3);

            var res4 = matcher.Match("apple").ToArray();
            Assert.Single(res4);
            Assert.Equal("apple", res4[0]);
        }

        [Fact]
        public void Searcher_ShouldHandlePrefixQueries()
        {
            var docs = new List<(string, uint)>
            {
                ("apple pie", 0),
                ("banana split", 1),
                ("application form", 2),
                ("app store", 3)
            };

            using (var indexer = new Indexer(_indexName))
            {
                indexer.Index(docs);
            }

            using (var searcher = new Searcher(_indexName))
            {
                // "app*" -> "apple", "app", "application"
                // Docs: 0 (apple), 2 (application), 3 (app)
                var res1 = searcher.SearchBoolean("app*");
                Assert.Equal(new uint[] { 0, 2, 3 }, res1.OrderBy(x => x).ToArray());

                // "ban*" -> "banana" -> 1
                var res2 = searcher.SearchBoolean("ban*");
                Assert.Equal(new uint[] { 1 }, res2.ToArray());

                // Case sensitivity check (default tokenizer lowercases)
                // "App*" -> "app*" -> same as above
                var res3 = searcher.SearchBoolean("App*");
                Assert.Equal(new uint[] { 0, 2, 3 }, res3.OrderBy(x => x).ToArray());

                // NOT app* -> 1
                var res4 = searcher.SearchBoolean("NOT app*");
                Assert.Equal(new uint[] { 1 }, res4.ToArray());

                // Boolean combinations
                // "app* OR ban*" -> 0, 1, 2, 3
                var res5 = searcher.SearchBoolean("app* OR ban*");
                Assert.Equal(new uint[] { 0, 1, 2, 3 }, res5.OrderBy(x => x).ToArray());
            }
        }

        [Fact]
        public void Searcher_ShouldHandlePrefixQueries_AfterPersistence()
        {
            var docs = new List<(string, uint)>
            {
                ("persistent apple", 10),
                ("persistent app", 11),
                ("persistent application", 12),
                ("transient banana", 13)
            };

            // Index and Close
            using (var indexer = new Indexer(_indexName))
            {
                indexer.Index(docs);
            }

            // Re-open in a new searcher instance to simulate persistence load
            using (var searcher = new Searcher(_indexName))
            {
                // Verify tokens are loaded and prefix matcher is rebuilt
                // "persistent*" -> "persistent" -> 10, 11, 12
                // Note: "persistent" is a token in all docs.

                // "app*" -> "apple" (10), "app" (11), "application" (12)
                var res1 = searcher.SearchBoolean("app*");
                Assert.Equal(new uint[] { 10, 11, 12 }, res1.OrderBy(x => x).ToArray());

                // "ban*" -> "banana" (13)
                var res2 = searcher.SearchBoolean("ban*");
                Assert.Equal(new uint[] { 13 }, res2.ToArray());
            }
        }
    }
}
