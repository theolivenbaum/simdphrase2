using System;
using System.Runtime.InteropServices;
using RocksDbSharp;
using SimdPhrase2.Db;
using SimdPhrase2.Roaringish;
using SimdPhrase2.Storage;

namespace SimdPhrase2.Segments
{
    // Read-only view of a single segment. Owns:
    //   - the TokenStore (in-memory token -> offset map for this segment, loaded
    //     from the seg_tokens CF)
    //   - the deletes RoaringBitmap (loaded from seg_deletes CF)
    //   - the live-docs RoaringBitmap (loaded from seg_live_docs CF)
    //
    // Posting lists themselves are not cached: each LoadPacked reads the bytes
    // from the `postings` CF and copies them into a fresh 64-byte-aligned
    // AlignedBuffer<ulong> so AVX-512 loads see the aligned layout the SIMD
    // intersect kernel expects.
    public sealed class SegmentReader : IDisposable
    {
        public ulong Id { get; }
        public TokenStore Tokens { get; }
        public RoaringBitmap Deletes { get; private set; }
        public RoaringBitmap LiveDocIds { get; }
        public int DocCount { get; }

        private readonly SimdPhraseDb _db;

        public SegmentReader(SimdPhraseDb db, SegmentInfo info)
        {
            _db = db;
            Id = info.Id;
            DocCount = info.DocCount;
            Tokens = TokenStore.Load(db, info.Id);

            var idKey = Keys.SegIdKey(info.Id);

            var deletesBytes = db.Db.Get(idKey, db.SegDeletes);
            Deletes = deletesBytes != null ? RoaringBitmap.LoadBytes(deletesBytes) : new RoaringBitmap();

            var liveBytes = db.Db.Get(idKey, db.SegLiveDocs);
            LiveDocIds = liveBytes != null ? RoaringBitmap.LoadBytes(liveBytes) : new RoaringBitmap();
        }

        // Reads the on-disk posting list bytes into a freshly allocated, 64-byte
        // aligned AlignedBuffer<ulong>. The aligned buffer is what the SIMD
        // intersect kernel wants - it must be aligned for Vector512 loads.
        //
        // Uses RocksDB's ISpanDeserializer to copy directly from the native value
        // buffer into the aligned buffer, skipping the intermediate managed byte[].
        public RoaringishPacked LoadPacked(byte field, string token, FileOffset offset)
        {
            var key = Keys.PostingsKey(Id, field, token);
            var loader = AlignedPackedLoader.Rent();
            try
            {
                var result = _db.Db.Get(key, loader, _db.Postings);
                return result ?? new RoaringishPacked();
            }
            finally
            {
                AlignedPackedLoader.Return(loader);
            }
        }

        // Loads the raw posting list bytes for a (field, token) directly. Used by
        // segment merging where we then re-serialize into the merged segment.
        public byte[] LoadPackedBytes(byte field, string token)
        {
            var key = Keys.PostingsKey(Id, field, token);
            return _db.Db.Get(key, _db.Postings) ?? Array.Empty<byte>();
        }

        public void Dispose() { /* no streams to close - everything is in-memory or on-demand via RocksDB */ }

        // ISpanDeserializer that copies the value bytes directly into a freshly
        // allocated 64-byte aligned AlignedBuffer<ulong>. The deserializer instance
        // itself holds no state across calls; it's pooled to avoid an allocation
        // for the ISpanDeserializer<T> object on every Get.
        private sealed class AlignedPackedLoader : ISpanDeserializer<RoaringishPacked>
        {
            private static readonly System.Collections.Concurrent.ConcurrentBag<AlignedPackedLoader> _pool = new();

            public static AlignedPackedLoader Rent() => _pool.TryTake(out var l) ? l : new AlignedPackedLoader();
            public static void Return(AlignedPackedLoader l) => _pool.Add(l);

            public RoaringishPacked Deserialize(ReadOnlySpan<byte> buffer)
            {
                int ulongCount = buffer.Length / 8;
                var aligned = new AlignedBuffer<ulong>(Math.Max(ulongCount, 1));
                aligned.SetLength(ulongCount);
                if (ulongCount > 0)
                {
                    var dst = MemoryMarshal.Cast<ulong, byte>(aligned.AsSpan());
                    buffer.Slice(0, ulongCount * 8).CopyTo(dst);
                }
                return new RoaringishPacked(aligned, takeOwnership: true);
            }
        }
    }
}
