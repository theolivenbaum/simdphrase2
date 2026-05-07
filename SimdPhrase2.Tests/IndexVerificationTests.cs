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

            // IndexStats lives at the index root and aggregates across segments.
            var statsPath = Path.Combine(_indexName, "index_stats.json");
            Assert.True(File.Exists(statsPath));
            var stats = IndexStats.Load(new FileSystemStorage(), statsPath);
            Assert.Equal(3u, stats.TotalDocs);
            Assert.Equal(7ul, stats.TotalTokens);

            // The new on-disk layout puts segment files under segments/seg_<id>/.
            var segmentsRoot = Path.Combine(_indexName, "segments");
            Assert.True(Directory.Exists(segmentsRoot));
            var segDirs = Directory.GetDirectories(segmentsRoot);
            Assert.Single(segDirs);
            var segDir = segDirs[0];

            // Per-segment doc lengths file exists.
            Assert.True(File.Exists(Path.Combine(segDir, "doc_lengths.bin")));

            var lengths = DocLengthsStore.Load(new FileSystemStorage(), Path.Combine(segDir, "doc_lengths.bin"));
            Assert.Equal(2, lengths.Get(0u, FieldRegistry.DefaultField));
            Assert.Equal(2, lengths.Get(1u, FieldRegistry.DefaultField));
            Assert.Equal(3, lengths.Get(2u, FieldRegistry.DefaultField));

            // TokenStore stores field-prefixed tokens.
            using (var tokenStore = new TokenStore(segDir))
            {
                Assert.True(tokenStore.TryGet(FieldRegistry.EncodeToken(FieldRegistry.DefaultField, "hello"), out var offset));
                Assert.Equal(3, offset.DocCount);

                Assert.True(tokenStore.TryGet(FieldRegistry.EncodeToken(FieldRegistry.DefaultField, "world"), out offset));
                Assert.Equal(2, offset.DocCount);

                Assert.True(tokenStore.TryGet(FieldRegistry.EncodeToken(FieldRegistry.DefaultField, "universe"), out offset));
                Assert.Equal(1, offset.DocCount);
            }
        }
    }
}
