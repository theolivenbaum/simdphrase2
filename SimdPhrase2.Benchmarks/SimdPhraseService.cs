using System;
using System.Collections.Generic;
using System.IO;
using SimdPhrase2;

namespace SimdPhrase2.Benchmarks
{
    public class SimdPhraseService : IDisposable
    {
        private readonly string _indexPath;
        private Searcher _searcher;

        public SimdPhraseService(string indexPath)
        {
            _indexPath = indexPath;
        }

        public void Index(IEnumerable<(string content, uint docId)> docs)
        {
            // Indexer clears the directory in constructor
            using (var indexer = new Indexer(_indexPath))
            {
                indexer.Index(docs);
            }
        }

        public void PrepareSearcher()
        {
            if (_searcher == null)
            {
                _searcher = new Searcher(_indexPath);
            }
        }

        public int Search(string query)
        {
            if (_searcher == null) PrepareSearcher();
            var results = _searcher.Search(query);
            return results.Count;
        }

        public void Dispose()
        {
            _searcher?.Dispose();
        }
    }
}
