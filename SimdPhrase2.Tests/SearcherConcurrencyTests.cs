using Xunit;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
using SimdPhrase2;
using System.Linq;

namespace SimdPhrase2.Tests
{
    public class SearcherConcurrencyTests
    {
        [Fact]
        public void Search_ConcurrentAccess_ShouldReturnCorrectResults()
        {
            string indexName = "test_concurrency_index";
            if (Directory.Exists(indexName)) Directory.Delete(indexName, true);

            // 1. Create Index
            using (var indexer = new Indexer(indexName))
            {
                indexer.AddDocument("the quick brown fox jumps over the lazy dog", 1);
                indexer.AddDocument("hello world from concurrent test", 2);
                indexer.AddDocument("parallel search is important for scale", 3);
                indexer.Commit();
            }

            // 2. Search Concurrently
            using (var searcher = new Searcher(indexName))
            {
                // We will run many searches in parallel
                var tasks = new List<Task>();

                for (int i = 0; i < 100; i++)
                {
                    tasks.Add(Task.Run(() =>
                    {
                        // Search 1
                        var results1 = searcher.Search("brown fox");
                        Assert.Contains(1u, results1);

                        // Search 2
                        var results2 = searcher.Search("hello world");
                        Assert.Contains(2u, results2);

                        // Search 3 (BM25)
                        var results3 = searcher.SearchBM25("parallel scale");
                        Assert.Contains(3u, results3.Select(x => x.DocId));
                    }));
                }

                Task.WaitAll(tasks.ToArray());
            }

            if (Directory.Exists(indexName)) Directory.Delete(indexName, true);
        }
    }
}
