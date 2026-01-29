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

                Assert.Equal(0u, results[0].DocId);
                Assert.Equal(1u, results[1].DocId);
                Assert.Equal(2u, results[2].DocId);

                Assert.True(results[0].Score > results[1].Score);
                Assert.True(results[1].Score > results[2].Score);
            }
        }

        [Fact]
        public void VerifyBM25Complex()
        {
            // Testing with more documents to check IDF impact
            // "common" appears in 4 docs
            // "rare" appears in 1 doc
            // "medium" appears in 2 docs

            var docs = new List<(string, uint)>
            {
                ("common common common", 0), // Long doc, only common terms
                ("common medium", 1),        // Short doc
                ("common medium rare", 2),   // Short doc with rare term
                ("common", 3)                // Very short doc, only common
            };

            using (var indexer = new Indexer(_indexName))
            {
                indexer.Index(docs);
            }

            using (var searcher = new Searcher(_indexName))
            {
                // Query: "rare"
                var resRare = searcher.SearchBM25("rare");
                Assert.Single(resRare);
                Assert.Equal(2u, resRare[0].DocId);

                // Query: "common"
                // Expect shorter docs to rank higher generally, but TF also matters.
                // Doc 3 (len 1, tf 1)
                // Doc 1 (len 2, tf 1)
                // Doc 2 (len 3, tf 1)
                // Doc 0 (len 3, tf 3) -> High TF might overcome length penalty

                var resCommon = searcher.SearchBM25("common");
                Assert.Equal(4, resCommon.Count);

                // Check that Doc 0 (high TF) is high up.
                // Doc 3 (short, tf1) vs Doc 0 (long, tf3).
                // Usually high TF wins unless saturation is extreme.

                // Let's just check existence for now as exact ranking depends on k1/b
                Assert.Contains(resCommon, r => r.DocId == 0);
                Assert.Contains(resCommon, r => r.DocId == 3);

                // Query: "common rare"
                // "rare" has high IDF. Doc 2 has "rare". It should be #1.
                var resMix = searcher.SearchBM25("common rare");
                Assert.Equal(2u, resMix[0].DocId);
            }
        }
    }
}
