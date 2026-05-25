using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace SimdPhrase2.Tests
{
    public class BooleanSearchTests : IDisposable
    {
        private string _indexName;

        public BooleanSearchTests()
        {
            _indexName = Path.Combine(Path.GetTempPath(), "SimdPhrase2_BooleanSearchTests_" + Guid.NewGuid());
        }

        public void Dispose()
        {
            if (Directory.Exists(_indexName)) Directory.Delete(_indexName, true);
        }

        [Fact]
        public void VerifyBooleanLogic()
        {
            // 0: A B
            // 1: B C
            // 2: A C
            // 3: A B C

            var docs = new List<(string, uint)>
            {
                ("A B", 0),
                ("B C", 1),
                ("A C", 2),
                ("A B C", 3)
            };

            using (var indexer = new Indexer(_indexName))
            {
                indexer.Index(docs);
            }

            using (var searcher = new Searcher(_indexName))
            {
                // A AND B -> 0, 3
                var res1 = searcher.SearchBoolean("A AND B");
                Assert.Equal(new uint[] { 0, 3 }, res1.OrderBy(x=>x).ToArray());

                // A OR C -> 0, 1, 2, 3
                var res2 = searcher.SearchBoolean("A OR C");
                Assert.Equal(new uint[] { 0, 1, 2, 3 }, res2.OrderBy(x=>x).ToArray());

                // B AND (NOT C) -> 0
                var res3 = searcher.SearchBoolean("B AND (NOT C)");
                Assert.Equal(new uint[] { 0 }, res3.ToArray());

                // Implicit AND: "A B" -> A AND B -> 0, 3
                var res4 = searcher.SearchBoolean("A B");
                Assert.Equal(new uint[] { 0, 3 }, res4.OrderBy(x=>x).ToArray());

                // Complex: (A AND B) OR C -> {0, 3} U {1, 2, 3} -> {0, 1, 2, 3}
                var res5 = searcher.SearchBoolean("(A AND B) OR C");
                Assert.Equal(new uint[] { 0, 1, 2, 3 }, res5.OrderBy(x=>x).ToArray());

                // NOT A -> 1
                var res6 = searcher.SearchBoolean("NOT A");
                Assert.Equal(new uint[] { 1 }, res6.ToArray());
            }
        }

        [Fact]
        public void BooleanBM25_DocSetMatchesUnscoredBoolean()
        {
            var docs = new List<(string, uint)>
            {
                ("A B",     0),
                ("B C",     1),
                ("A C",     2),
                ("A B C",   3),
            };

            using (var indexer = new Indexer(_indexName))
            {
                indexer.Index(docs);
            }

            using (var searcher = new Searcher(_indexName))
            {
                // Same doc sets as the unscored Boolean path, but now with BM25 scores.
                var queries = new[]
                {
                    "A AND B",
                    "A OR C",
                    "B AND (NOT C)",
                    "A B",
                    "(A AND B) OR C",
                };

                foreach (var q in queries)
                {
                    var unscored = searcher.SearchBoolean(q).OrderBy(x => x).ToArray();
                    // Ask for plenty so we get every match back.
                    var scored = searcher.SearchBooleanBM25(q, k: 100)
                        .Select(r => r.DocId)
                        .OrderBy(x => x)
                        .ToArray();
                    Assert.Equal(unscored, scored);
                }
            }
        }

        [Fact]
        public void BooleanBM25_AndScoresAreSumOfClauseScores()
        {
            // "apple banana" appears in both docs; doc 0 is shorter so each clause
            // scores higher there. The AND score should be the sum of both clause
            // contributions per doc - so the cheapest sanity check is that the
            // ranking matches the BM25 ranking and the scores are strictly larger
            // than the single-term scores for the same docs.

            var docs = new List<(string, uint)>
            {
                ("apple banana",          0),
                ("apple banana cherry",   1),
                ("apple",                 2),
            };

            using (var indexer = new Indexer(_indexName))
            {
                indexer.Index(docs);
            }

            using (var searcher = new Searcher(_indexName))
            {
                var andResults = searcher.SearchBooleanBM25("apple AND banana");
                Assert.Equal(2, andResults.Count);
                // Both AND-matched docs must contain both terms.
                Assert.Contains(andResults, r => r.DocId == 0u);
                Assert.Contains(andResults, r => r.DocId == 1u);
                // Doc 2 only has "apple" - excluded by AND.
                Assert.DoesNotContain(andResults, r => r.DocId == 2u);

                // Shorter doc ranks higher under BM25 length normalisation.
                var doc0Score = andResults.First(r => r.DocId == 0u).Score;
                var doc1Score = andResults.First(r => r.DocId == 1u).Score;
                Assert.True(doc0Score > doc1Score, $"Doc 0 ({doc0Score}) should outrank Doc 1 ({doc1Score}) under BM25.");

                // AND of two clauses should outscore either clause alone on the same doc.
                var appleOnly = searcher.SearchBooleanBM25("apple");
                var doc0Apple = appleOnly.First(r => r.DocId == 0u).Score;
                Assert.True(doc0Score > doc0Apple, $"AND score ({doc0Score}) should exceed single-term score ({doc0Apple}).");
            }
        }

        [Fact]
        public void BooleanBM25_OrIncludesAllMatchingDocs()
        {
            var docs = new List<(string, uint)>
            {
                ("apple",        0),
                ("banana",       1),
                ("apple banana", 2),
                ("cherry",       3),
            };

            using (var indexer = new Indexer(_indexName))
            {
                indexer.Index(docs);
            }

            using (var searcher = new Searcher(_indexName))
            {
                var or = searcher.SearchBooleanBM25("apple OR banana", k: 100);
                var ids = or.Select(r => r.DocId).OrderBy(x => x).ToArray();
                Assert.Equal(new uint[] { 0, 1, 2 }, ids);

                // Doc 2 matches both clauses and is short - it should be the top hit.
                var top = or.OrderByDescending(r => r.Score).First();
                Assert.Equal(2u, top.DocId);
            }
        }

        [Fact]
        public void BooleanBM25_NotClauseFiltersWithoutScoring()
        {
            var docs = new List<(string, uint)>
            {
                ("apple",        0),
                ("apple banana", 1),
                ("apple cherry", 2),
            };

            using (var indexer = new Indexer(_indexName))
            {
                indexer.Index(docs);
            }

            using (var searcher = new Searcher(_indexName))
            {
                var res = searcher.SearchBooleanBM25("apple AND NOT banana");
                var ids = res.Select(r => r.DocId).OrderBy(x => x).ToArray();
                Assert.Equal(new uint[] { 0, 2 }, ids);
                Assert.All(res, r => Assert.True(r.Score > 0f));
            }
        }

        [Fact]
        public void BooleanBM25_RespectsTopK()
        {
            var docs = new List<(string, uint)>
            {
                ("apple",                                0),
                ("apple banana",                         1),
                ("apple banana cherry",                  2),
                ("apple banana cherry date",             3),
                ("apple banana cherry date elderberry",  4),
            };

            using (var indexer = new Indexer(_indexName))
            {
                indexer.Index(docs);
            }

            using (var searcher = new Searcher(_indexName))
            {
                var topAll = searcher.SearchBooleanBM25("apple", k: 100);
                Assert.Equal(5, topAll.Count);

                var top2 = searcher.SearchBooleanBM25("apple", k: 2);
                Assert.Equal(2, top2.Count);
                // Top k must be the highest-scoring slice of the full result.
                Assert.Equal(topAll[0].DocId, top2[0].DocId);
                Assert.Equal(topAll[1].DocId, top2[1].DocId);
                Assert.True(top2[0].Score >= top2[1].Score);
            }
        }

        [Fact]
        public void BooleanBM25_EmptyAndNullQueries()
        {
            var docs = new List<(string, uint)>
            {
                ("apple", 0),
            };

            using (var indexer = new Indexer(_indexName))
            {
                indexer.Index(docs);
            }

            using (var searcher = new Searcher(_indexName))
            {
                Assert.Empty(searcher.SearchBooleanBM25(""));
                Assert.Empty(searcher.SearchBooleanBM25("   "));
                Assert.Empty(searcher.SearchBooleanBM25((QueryNode)null));
            }
        }
    }
}
