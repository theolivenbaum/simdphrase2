using System;
using System.IO;
using System.Runtime.InteropServices;
using SimdPhrase2.Db;
using SimdPhrase2.Roaringish;
using SimdPhrase2.Storage;

namespace SimdPhrase2.Segments
{
    // Read-only view of a single segment. Owns:
    //   - the TokenStore (in-memory token -> offset map for this segment)
    //   - a stream over the segment's roaringish_packed.bin
    //   - the deletes RoaringBitmap (lazily loaded)
    //
    // The packed stream, once opened, is used for stateful Seek+Read to load posting
    // lists. Each Searcher gets its own SegmentReader instances so phrase intersect
    // remains on the existing single-stream fast path - we did not change the SIMD code.
    public sealed class SegmentReader : IDisposable
    {
        public string Id { get; }
        public TokenStore Tokens { get; }
        public RoaringBitmap Deletes { get; private set; }
        public RoaringBitmap LiveDocIds { get; }
        public int DocCount { get; }

        private readonly Stream _packedStream;
        private readonly ISimdStorage _storage;
        private readonly string _segmentDir;

        public SegmentReader(ISimdStorage storage, string indexPath, SegmentInfo info)
        {
            _storage = storage;
            Id = info.Id;
            DocCount = info.DocCount;
            _segmentDir = SegmentManifest.SegmentDirectory(storage, indexPath, info.Id);
            Tokens = new TokenStore(_segmentDir, storage);
            string packedPath = storage.Combine(_segmentDir, "roaringish_packed.bin");
            _packedStream = storage.FileExists(packedPath) ? storage.OpenRead(packedPath) : null;

            string deletesPath = storage.Combine(_segmentDir, "deletes.bin");
            if (storage.FileExists(deletesPath))
            {
                using var s = storage.OpenRead(deletesPath);
                Deletes = RoaringBitmap.Load(s);
            }
            else
            {
                Deletes = new RoaringBitmap();
            }

            string docIdsPath = storage.Combine(_segmentDir, "doc_ids.bin");
            if (storage.FileExists(docIdsPath))
            {
                using var s = storage.OpenRead(docIdsPath);
                LiveDocIds = RoaringBitmap.Load(s);
            }
            else
            {
                LiveDocIds = new RoaringBitmap();
            }
        }

        public bool HasPackedFile => _packedStream != null;

        public RoaringishPacked LoadPacked(FileOffset offset)
        {
            int ulongCount = (int)(offset.Length / 8);
            var buffer = new AlignedBuffer<ulong>(ulongCount);
            buffer.SetLength(ulongCount);
            _packedStream.Seek(offset.Begin, SeekOrigin.Begin);
            Span<byte> byteSpan = MemoryMarshal.Cast<ulong, byte>(buffer.AsSpan());
            _packedStream.ReadExactly(byteSpan);
            return new RoaringishPacked(buffer, takeOwnership: true);
        }

        public void Dispose()
        {
            Tokens.Dispose();
            _packedStream?.Dispose();
        }
    }
}
