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

            using (var db = SimdPhraseDb.Open(_indexName))
            {
                // Verify IndexStats persisted in the meta CF.
                var stats = IndexStats.Load(db);
                Assert.Equal(3u, stats.TotalDocs);
                Assert.Equal(7ul, stats.TotalTokens);

                // Verify DocLengths in the doc_lengths CF.
                var lens = new DocLengthStore(db, stats.FieldCount);
                Assert.Equal(2, lens.GetLength(0, 0));
                Assert.Equal(2, lens.GetLength(1, 0));
                Assert.Equal(3, lens.GetLength(2, 0));

                // Verify TokenStore counts (tokens now live in segments).
                var manifest = SegmentManifest.Load(db);
                Assert.Single(manifest.Segments);
                int helloDocs = 0, worldDocs = 0, universeDocs = 0;
                foreach (var seg in manifest.Segments)
                {
                    using var sr = new SegmentReader(db, seg);
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
}
