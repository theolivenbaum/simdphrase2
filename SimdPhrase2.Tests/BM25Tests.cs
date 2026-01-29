using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace SimdPhrase2.Tests
{
    public class BM25Tests : IDisposable
    {
        private string _indexName;

        public BM25Tests()
        {
            _indexName = Path.Combine(Path.GetTempPath(), "SimdPhrase2_BM25Tests_" + Guid.NewGuid());
        }

        public void Dispose()
        {
            if (Directory.Exists(_indexName)) Directory.Delete(_indexName, true);
        }

        [Fact]
        public void VerifyBM25Ranking()
        {
            // Doc 0: "apple banana" (Length 2)
            // Doc 1: "apple banana cherry" (Length 3)
            // Doc 2: "apple" (Length 1)

            // Query: "apple banana"
            // Both 0 and 1 contain both terms.
            // 0 is shorter, so it should rank higher than 1 due to length normalization.
            // 2 contains only "apple". It might rank lower than 0 and 1 because it misses "banana".

            var docs = new List<(string, uint)>
            {
                ("apple banana", 0),
                ("apple banana cherry", 1),
                ("apple", 2)
            };

            using (var indexer = new Indexer(_indexName))
            {
                indexer.Index(docs);
            }

            using (var searcher = new Searcher(_indexName))
            {
                var results = searcher.SearchBM25("apple banana");

                Assert.Equal(3, results.Count);

                // Expect: 0, 1, 2 (or 0, 2, 1 if banana weight is low? No, banana is rarer than apple).
                // apple: df=3. idf low.
                // banana: df=2. idf higher.
                // Doc 0 has both. Doc 1 has both but longer. Doc 2 misses banana.
                // So 0 > 1.
                // 1 vs 2: 1 has banana (high value), 2 doesn't. 1 likely > 2.

                Assert.Equal(0u, results[0].DocId);
                Assert.Equal(1u, results[1].DocId);
                Assert.Equal(2u, results[2].DocId);

                // Assertions will implicitly use float comparison which is fine here since scores should differ significantly
                Assert.True(results[0].Score > results[1].Score);
                Assert.True(results[1].Score > results[2].Score);
            }
        }
    }
}
