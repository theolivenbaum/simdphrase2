using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using SimdPhrase2.Db;
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

            // Verify TokenStore counts
            using (var tokenStore = new TokenStore(_indexName))
            {
                Assert.True(tokenStore.TryGet("hello", out var offset));
                Assert.Equal(3, offset.DocCount);

                Assert.True(tokenStore.TryGet("world", out offset));
                Assert.Equal(2, offset.DocCount);

                Assert.True(tokenStore.TryGet("universe", out offset));
                Assert.Equal(1, offset.DocCount);
            }
        }
    }
}
