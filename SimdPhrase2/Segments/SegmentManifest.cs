using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using RocksDbSharp;
using SimdPhrase2.Storage;

namespace SimdPhrase2.Segments
{
    public sealed class SegmentInfo
    {
        // Numeric, monotonically increasing per index. Used as the 8-byte BE key
        // prefix in all per-segment column families.
        public ulong Id { get; set; }
        // Approximate size in bytes (sum of packed list bytes). Used by merge policy.
        public long SizeInBytes { get; set; }
        // Number of unique docs in the segment.
        public int DocCount { get; set; }
        // Number of deletes recorded against this segment.
        public int DeleteCount { get; set; }
        // True if this segment was produced by a merge (used as a hint by the merge policy).
        public bool MergedSegment { get; set; }

        public int LiveDocCount => DocCount - DeleteCount;

        // Compact binary serialisation written to seg_meta CF:
        //   [byte version=1][int64 sizeInBytes][int32 docCount][int32 deleteCount][byte mergedSegment]
        public byte[] Serialize()
        {
            var buf = new byte[1 + 8 + 4 + 4 + 1];
            var span = buf.AsSpan();
            span[0] = 1;
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(1, 8), SizeInBytes);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(9, 4), DocCount);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(13, 4), DeleteCount);
            span[17] = (byte)(MergedSegment ? 1 : 0);
            return buf;
        }

        public static SegmentInfo Deserialize(ulong id, ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < 18) throw new InvalidOperationException("Truncated SegmentInfo.");
            byte version = bytes[0];
            if (version != 1) throw new InvalidOperationException($"Unsupported SegmentInfo version {version}.");
            return new SegmentInfo
            {
                Id = id,
                SizeInBytes = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(1, 8)),
                DocCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(9, 4)),
                DeleteCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(13, 4)),
                MergedSegment = bytes[17] != 0,
            };
        }
    }

    // Loaded once at Indexer / Searcher open. Holds the live segment list and the
    // next-segment-id counter. Persisted across the meta and seg_meta CFs - this
    // class is a thin in-memory view, not the storage of record.
    public sealed class SegmentManifest
    {
        public ulong NextSegmentId { get; set; }
        public List<SegmentInfo> Segments { get; set; } = new();

        public static SegmentManifest Load(SimdPhraseDb db)
        {
            var manifest = new SegmentManifest();

            var nextBytes = db.Db.Get(Encoding.UTF8.GetBytes(SimdPhraseDb.MetaKeyNextSegmentId), db.Meta);
            if (nextBytes != null && nextBytes.Length == 8)
            {
                manifest.NextSegmentId = BinaryPrimitives.ReadUInt64LittleEndian(nextBytes);
            }

            using var it = db.Db.NewIterator(db.SegMeta);
            it.SeekToFirst();
            while (it.Valid())
            {
                var key = it.GetKeySpan();
                if (key.Length == 8)
                {
                    ulong id = BinaryPrimitives.ReadUInt64BigEndian(key);
                    var info = SegmentInfo.Deserialize(id, it.GetValueSpan());
                    manifest.Segments.Add(info);
                }
                it.Next();
            }
            // Segments come out sorted by id (BE encoded key); preserve that order.
            return manifest;
        }

        public ulong AllocateSegmentId()
        {
            return NextSegmentId++;
        }

        // Saves only the manifest-level counter. Per-segment SegmentInfo blobs are
        // expected to be written separately, normally as part of the same WriteBatch.
        public void Save(SimdPhraseDb db)
        {
            Span<byte> buf = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(buf, NextSegmentId);
            db.Db.Put(Encoding.UTF8.GetBytes(SimdPhraseDb.MetaKeyNextSegmentId), buf.ToArray(), db.Meta);
        }

        public void AddNextIdToBatch(WriteBatch batch, ColumnFamilyHandle metaCf)
        {
            var buf = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(buf, NextSegmentId);
            batch.Put(Encoding.UTF8.GetBytes(SimdPhraseDb.MetaKeyNextSegmentId), buf, metaCf);
        }
    }
}
