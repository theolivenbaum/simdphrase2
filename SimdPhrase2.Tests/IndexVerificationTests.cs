using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using SimdPhrase2.Db;
using SimdPhrase2.Segments;
using SimdPhrase2.Storage;

namespace SimdPhrase2.Tests
{
    public class IndexVerificationTests : IDisposable
    {
        private string _indexName;

        public IndexVerificationTests()
        {
            _indexName = Path.Combine(Path.GetTempPath(), "SimdPhrase2_IndexVerification_" + Guid.NewGuid());
        }

        public void Dispose()
        {
            if (Directory.Exists(_indexName)) Directory.Delete(_indexName, true);
        }

        [Fact]
        public void VerifyIndexStatsAndDocLengths()
        {
            var docs = new List<(string content, uint docId)>
            {
                ("hello world", 0),
                ("hello universe", 1),
                ("hello world world", 2)
            };

            using (var indexer = new Indexer(_indexName))
            {
                indexer.Index(docs);
            }

            // Verify IndexStats
            var statsPath = Path.Combine(_indexName, "index_stats.json");
            Assert.True(File.Exists(statsPath));
            var stats = IndexStats.Load(new FileSystemStorage(), statsPath);
            Assert.Equal(3u, stats.TotalDocs);
            Assert.Equal(7ul, stats.TotalTokens);

            // Verify DocLengths
            var docLengthsPath = Path.Combine(_indexName, "doc_lengths.bin");
            Assert.True(File.Exists(docLengthsPath));
            using (var fs = File.OpenRead(docLengthsPath))
            using (var br = new BinaryReader(fs))
            {
                Assert.Equal(12, fs.Length); // 3 docs * 4 bytes
                Assert.Equal(2, br.ReadInt32());
                Assert.Equal(2, br.ReadInt32());
                Assert.Equal(3, br.ReadInt32());
            }

            // Verify TokenStore counts (tokens now live in segments)
            var storage = new FileSystemStorage();
            var manifest = SegmentManifest.Load(storage, _indexName);
            Assert.Single(manifest.Segments);
            int helloDocs = 0, worldDocs = 0, universeDocs = 0;
            foreach (var seg in manifest.Segments)
            {
                using var sr = new SegmentReader(storage, _indexName, seg);
                if (sr.Tokens.TryGet("hello", out var off1)) helloDocs += off1.DocCount;
                if (sr.Tokens.TryGet("world", out var off2)) worldDocs += off2.DocCount;
                if (sr.Tokens.TryGet("universe", out var off3)) universeDocs += off3.DocCount;
            }
            Assert.Equal(3, helloDocs);
            Assert.Equal(2, worldDocs);
            Assert.Equal(1, universeDocs);
        }
    }
}
