using System;
using System.Collections.Generic;
using System.IO;
using SimdPhrase2.Storage;

namespace SimdPhrase2.Db
{
    /// <summary>
    /// Read-only access to one segment on disk: token map, posting lists,
    /// per-field doc lengths, common tokens, and live docs (deletions).
    /// Owned by Searcher; opened on construction, closed on Dispose.
    /// </summary>
    public class SegmentReader : IDisposable
    {
        public string SegmentDir { get; }
        public TokenStore Tokens { get; }
        public DocLengthsStore DocLengths { get; }
        public LiveDocs Live { get; }
        public HashSet<string> CommonTokens { get; }
        public Stream PackedStream { get; private set; }

        public SegmentReader(string segmentDir, ISimdStorage storage)
        {
            SegmentDir = segmentDir;
            Tokens = new TokenStore(segmentDir, storage);
            DocLengths = DocLengthsStore.Load(storage, storage.Combine(segmentDir, "doc_lengths.bin"));
            Live = LiveDocs.Load(storage, storage.Combine(segmentDir, "live_docs.bin"));
            CommonTokens = CommonTokensPersistence.Load(storage, storage.Combine(segmentDir, "common_tokens.bin"));

            string packedPath = storage.Combine(segmentDir, "roaringish_packed.bin");
            if (storage.FileExists(packedPath))
            {
                PackedStream = storage.OpenRead(packedPath);
            }
        }

        public void Dispose()
        {
            Tokens?.Dispose();
            PackedStream?.Dispose();
        }
    }
}
