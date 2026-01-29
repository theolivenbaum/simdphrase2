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
    }
}
