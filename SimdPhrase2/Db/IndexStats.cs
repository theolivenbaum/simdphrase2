using System;
using System.Buffers.Binary;
using System.Text;
using RocksDbSharp;
using SimdPhrase2.Storage;

namespace SimdPhrase2.Db
{
    public class IndexStats
    {
        public uint TotalDocs { get; set; }
        // Sum across all fields. Kept for backward compatibility and as a quick
        // single number for the simple Search code path.
        public ulong TotalTokens { get; set; }

        // Number of fields in the index (1 for a legacy single-content index).
        public int FieldCount { get; set; } = 1;

        // Per-field total tokens. Used to compute the per-field average document
        // length for BM25/BM25F. Length matches FieldCount.
        public ulong[] TotalTokensPerField { get; set; } = new ulong[] { 0 };

        // Compact binary on-disk format (stored as a single meta-CF value):
        //   [byte version=1][uint32 totalDocs][uint64 totalTokens]
        //   [int32 fieldCount][uint64 totalTokensPerField[fieldCount]]
        // Little-endian throughout.

        public byte[] Serialize()
        {
            int size = 1 + 4 + 8 + 4 + 8 * FieldCount;
            var buf = new byte[size];
            var span = buf.AsSpan();
            span[0] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(1, 4), TotalDocs);
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(5, 8), TotalTokens);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(13, 4), FieldCount);
            for (int i = 0; i < FieldCount; i++)
            {
                ulong v = (TotalTokensPerField != null && i < TotalTokensPerField.Length) ? TotalTokensPerField[i] : 0UL;
                BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(17 + i * 8, 8), v);
            }
            return buf;
        }

        public static IndexStats Deserialize(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < 17) return new IndexStats();
            byte version = bytes[0];
            if (version != 1) throw new InvalidOperationException($"Unsupported IndexStats version {version}.");
            uint totalDocs = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(1, 4));
            ulong totalTokens = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(5, 8));
            int fieldCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(13, 4));
            if (fieldCount <= 0) fieldCount = 1;
            var perField = new ulong[fieldCount];
            for (int i = 0; i < fieldCount; i++)
            {
                int off = 17 + i * 8;
                if (off + 8 > bytes.Length) break;
                perField[i] = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(off, 8));
            }
            return new IndexStats
            {
                TotalDocs = totalDocs,
                TotalTokens = totalTokens,
                FieldCount = fieldCount,
                TotalTokensPerField = perField,
            };
        }

        public static IndexStats Load(SimdPhraseDb db)
        {
            var bytes = db.Db.Get(Encoding.UTF8.GetBytes(SimdPhraseDb.MetaKeyStats), db.Meta);
            if (bytes == null) return new IndexStats();
            return Deserialize(bytes);
        }

        public void Save(SimdPhraseDb db)
        {
            db.Db.Put(Encoding.UTF8.GetBytes(SimdPhraseDb.MetaKeyStats), Serialize(), db.Meta);
        }

        public void AddToBatch(WriteBatch batch, ColumnFamilyHandle metaCf)
        {
            batch.Put(Encoding.UTF8.GetBytes(SimdPhraseDb.MetaKeyStats), Serialize(), metaCf);
        }
    }
}
