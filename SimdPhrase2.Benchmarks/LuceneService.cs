using System;
using System.Collections.Generic;
using System.IO;
using Lucene.Net.Analysis.Util;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;

namespace SimdPhrase2.Benchmarks
{
    public class LuceneService : IDisposable
    {
        private readonly string _indexPath;
        private FSDirectory _directory;
        private StandardAnalyzer _analyzer;
        private IndexWriter _writer;
        private DirectoryReader _reader;
        private IndexSearcher _searcher;

        public LuceneService(string indexPath)
        {
            _indexPath = indexPath;
            _directory = FSDirectory.Open(_indexPath);
            _analyzer = new StandardAnalyzer(LuceneVersion.LUCENE_48, CharArraySet.EMPTY_SET);
        }

        public void Index(IEnumerable<(string content, uint docId)> docs)
        {
            var config = new IndexWriterConfig(LuceneVersion.LUCENE_48, _analyzer);
            config.OpenMode = OpenMode.CREATE;
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
            }
        }

        public int Search(string queryStr)
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
            var topDocs = _searcher.Search(query, 1000000); // Request many to ensure full enumeration

            // Enumerate results to match SimdPhrase behavior
            int count = 0;
            foreach (var scoreDoc in topDocs.ScoreDocs)
            {
                // Accessing doc id is trivial, but let's simulate "getting" the result
                var id = scoreDoc.Doc;
                count++;
            }

            return count; // Should match TotalHits usually
        }

        public void Dispose()
        {
            _writer?.Dispose();
            _reader?.Dispose();
            _directory?.Dispose();
        }
    }
}
