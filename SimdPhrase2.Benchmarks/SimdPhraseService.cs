using System;
using System.Collections.Generic;
using System.IO;
using SimdPhrase2;

namespace SimdPhrase2.Benchmarks
{
    public class SimdPhraseService : IDisposable
    {
        private readonly string _indexPath;
        private readonly bool _forceNaive;
        private Searcher _searcher;

        public SimdPhraseService(string indexPath, bool forceNaive)
        {
            _indexPath = indexPath;
            _forceNaive = forceNaive;
        }

        public void Index(IEnumerable<(string content, uint docId)> docs)
        {
            // Indexer clears the directory in constructor
            using (var indexer = new Indexer(_indexPath, CommonTokensConfig.None))
            {
                indexer.Index(docs);
            }
        }

        public void PrepareSearcher()
        {
            if (_searcher == null)
            {
                _searcher = new Searcher(_indexPath, _forceNaive);
            }
        }

        public int Search(string query, List<int> results = null)
        {
            if (_searcher == null) PrepareSearcher();
            var searchResults = _searcher.Search(query);
            foreach (var result in searchResults)
            {
                results?.Add((int)result);
            }

            return searchResults.Count;
        }

        public int SearchBM25(string query, int k, List<int> results = null)
        {
            if (_searcher == null) PrepareSearcher();
            var searchResults = _searcher.SearchBM25(query, k);
            foreach (var result in searchResults)
            {
                results?.Add((int)result.DocId);
            }
            return searchResults.Count;
        }

        public int SearchBoolean(string query, List<int> results = null)
        {
             if (_searcher == null) PrepareSearcher();
             var searchResults = _searcher.SearchBoolean(query);
             foreach(var result in searchResults)
             {
                 results?.Add((int)result);
             }
             return searchResults.Count;
        }

        public void Dispose()
        {
            _searcher?.Dispose();
        }
    }
}
