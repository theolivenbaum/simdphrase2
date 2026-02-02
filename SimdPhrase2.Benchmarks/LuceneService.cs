using System;
using System.Collections.Generic;
using System.IO;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Util;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Analysis.NGram;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Search;
using Lucene.Net.Search.Similarities;
using Lucene.Net.Store;
using Lucene.Net.Util;

namespace SimdPhrase2.Benchmarks
{
    public class LuceneService : IDisposable
    {
        private readonly string _indexPath;
        private FSDirectory _directory;
        private Analyzer _analyzer;
        private IndexWriter _writer;
        private DirectoryReader _reader;
        private IndexSearcher _searcher;
        private bool _useBm25;

        public LuceneService(string indexPath, bool useBm25 = false, Analyzer analyzer = null)
        {
            _indexPath = indexPath;
            _directory = FSDirectory.Open(_indexPath);
            _analyzer = analyzer ?? new StandardAnalyzer(LuceneVersion.LUCENE_48, CharArraySet.EMPTY_SET);
            _useBm25 = useBm25;
        }

        public void Index(IEnumerable<(string content, uint docId)> docs)
        {
            var config = new IndexWriterConfig(LuceneVersion.LUCENE_48, _analyzer);
            config.OpenMode = OpenMode.CREATE;
            if (_useBm25)
            {
                 config.Similarity = new BM25Similarity();
            }
            _writer = new IndexWriter(_directory, config);

            foreach (var (content, docId) in docs)
            {
                var doc = new Document();
                doc.Add(new StringField("id", docId.ToString(), Field.Store.YES));
                doc.Add(new TextField("content", content, Field.Store.YES));
                _writer.AddDocument(doc);
            }

            _writer.Commit();
            _writer.Dispose();
            _writer = null;
        }

        public void PrepareSearcher()
        {
            if (_reader == null)
            {
                _reader = DirectoryReader.Open(_directory);
                _searcher = new IndexSearcher(_reader);
                if (_useBm25)
                {
                    _searcher.Similarity = new BM25Similarity();
                }
            }
        }

        public int Search(string queryStr, List<int> results = null)
        {
            if (_searcher == null) PrepareSearcher();

            // Assume phrase search for multiple terms
            // Use QueryParser
            var parser = new QueryParser(LuceneVersion.LUCENE_48, "content", _analyzer);

            // If the query has multiple words, we treat it as a phrase query by wrapping in quotes
            // logic similar to SimdPhrase which seems to enforce adjacency
            string parsedQueryStr = queryStr.Trim();
            if (parsedQueryStr.Contains(" "))
            {
                parsedQueryStr = $"\"{parsedQueryStr}\"";
            }

            var query = parser.Parse(parsedQueryStr);
            var topDocs = _searcher.Search(query, 10_000_000); // Request many to ensure full enumeration

            // Enumerate results to match SimdPhrase behavior
            int count = 0;
            foreach (var scoreDoc in topDocs.ScoreDocs)
            {
                // Accessing doc id is trivial, but let's simulate "getting" the result
                var id = scoreDoc.Doc;
                results?.Add(id);
                count++;
            }

            return count; // Should match TotalHits usually
        }

        public int SearchBM25(string queryStr, int k)
        {
             if (_searcher == null) PrepareSearcher();

             // Standard parsing (not forcing phrase)
             var parser = new QueryParser(LuceneVersion.LUCENE_48, "content", _analyzer);
             var query = parser.Parse(queryStr);

             var topDocs = _searcher.Search(query, k);
             return topDocs.ScoreDocs.Length;
        }

        public int SearchBM25(string queryStr, int k, List<int> results)
        {
             if (_searcher == null) PrepareSearcher();

             // Standard parsing (not forcing phrase)
             var parser = new QueryParser(LuceneVersion.LUCENE_48, "content", _analyzer);
             var query = parser.Parse(queryStr);

             var topDocs = _searcher.Search(query, k);
             foreach(var scoreDoc in topDocs.ScoreDocs)
             {
                 // We need to retrieve the "id" field which corresponds to our generated docId
                 var doc = _searcher.Doc(scoreDoc.Doc);
                 if (int.TryParse(doc.Get("id"), out int realId))
                 {
                     results?.Add(realId);
                 }
             }
             return topDocs.ScoreDocs.Length;
        }

        public int SearchBM25(string queryStr, int k, List<(int docID, float score)> results)
        {
             if (_searcher == null) PrepareSearcher();

             // Standard parsing (not forcing phrase)
             var parser = new QueryParser(LuceneVersion.LUCENE_48, "content", _analyzer);
             var query = parser.Parse(queryStr);

             var topDocs = _searcher.Search(query, k);
             foreach(var scoreDoc in topDocs.ScoreDocs)
             {
                 // We need to retrieve the "id" field which corresponds to our generated docId
                 var doc = _searcher.Doc(scoreDoc.Doc);
                 if (int.TryParse(doc.Get("id"), out int realId))
                 {
                     results?.Add((realId, scoreDoc.Score));
                 }
             }
             return topDocs.ScoreDocs.Length;
        }

        public int SearchBoolean(string queryStr, List<int> results = null)
        {
            if (_searcher == null) PrepareSearcher();

            // Lucene QueryParser handles AND, OR, NOT
            var parser = new QueryParser(LuceneVersion.LUCENE_48, "content", _analyzer);
            var query = parser.Parse(queryStr);

            // To mimic SimdPhrase Boolean, we probably want all hits?
            var topDocs = _searcher.Search(query, 10_000_000);

             foreach(var scoreDoc in topDocs.ScoreDocs)
             {
                 var doc = _searcher.Doc(scoreDoc.Doc);
                 if (int.TryParse(doc.Get("id"), out int realId))
                 {
                     results?.Add(realId);
                 }
             }
             return topDocs.ScoreDocs.Length;
        }

        public void Dispose()
        {
            _writer?.Dispose();
            _reader?.Dispose();
            _directory?.Dispose();
        }
    }

    public class NGramAnalyzer : Analyzer
    {
        private readonly int _minGram;
        private readonly int _maxGram;

        public NGramAnalyzer(int minGram, int maxGram)
        {
            _minGram = minGram;
            _maxGram = maxGram;
        }

        protected override TokenStreamComponents CreateComponents(string fieldName, TextReader reader)
        {
            var tokenizer = new Lucene.Net.Analysis.NGram.NGramTokenizer(LuceneVersion.LUCENE_48, reader, _minGram, _maxGram);
            return new TokenStreamComponents(tokenizer, tokenizer);
        }
    }
}
