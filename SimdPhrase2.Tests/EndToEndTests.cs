using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace SimdPhrase2.Tests
{
    public class EndToEndTests
    {
        [Fact]
        public void IndexAndSearch_ShouldWork()
        {
            string indexName = "test_index";
            if (Directory.Exists(indexName)) Directory.Delete(indexName, true);

            var docs = new List<(string, uint)>
            {
                ("look at my beautiful cat", 0),
                ("this is a document", 50),
                ("look at my dog", 25),
                ("look at my beautiful hamster", 35),
            };

            using (var indexer = new Indexer(indexName))
            {
                indexer.Index(docs);
            }

            using (var searcher = new Searcher(indexName))
            {
                // Search "at my beautiful"
                // Should match doc 0 and 35.
                // "look at my beautiful cat" -> at my beautiful matches
                // "look at my beautiful hamster" -> at my beautiful matches

                var results = searcher.Search("at my beautiful");
                results.Sort();

                Assert.Equal(new uint[] { 0, 35 }, results.ToArray());

                // Search "look at"
                results = searcher.Search("look at");
                results.Sort();
                Assert.Equal(new uint[] { 0, 25, 35 }, results.ToArray());

                // Search "dog"
                results = searcher.Search("dog");
                Assert.True(results.Contains(25u), $"Results count: {results.Count}. First: {(results.Count > 0 ? results[0] : -1)}");

                // Search "cat"
                results = searcher.Search("cat");
                Assert.True(results.Contains(0u), $"Cat results: {results.Count}. First: {(results.Count > 0 ? results[0] : -1)}");

                // Search non-existent
                results = searcher.Search("elephant");
                Assert.Empty(results);

                // Search phrase that doesn't exist
                results = searcher.Search("beautiful dog");
                Assert.Empty(results);

                // Verify document content retrieval
                Assert.Equal("look at my beautiful cat", searcher.GetDocument(0));
            }

            // Cleanup
            if (Directory.Exists(indexName)) Directory.Delete(indexName, true);
        }
    }
}
