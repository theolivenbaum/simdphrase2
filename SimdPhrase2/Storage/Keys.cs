using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace SimdPhrase2.Storage
{
    /// <summary>
    /// Key encoders for the column families in <see cref="SimdPhraseDb"/>. All
    /// numeric prefixes are written big-endian so that lexicographic byte order
    /// matches numeric order - this is what makes RocksDB prefix iteration over
    /// a (segId) prefix yield postings in (field, token) order.
    /// </summary>
    internal static class Keys
    {
        // 8-byte BE encoding of a uint64 (segment id).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteSegId(Span<byte> dest, ulong segId)
            => BinaryPrimitives.WriteUInt64BigEndian(dest, segId);

        // 4-byte BE encoding of a uint32 (doc id).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteDocId(Span<byte> dest, uint docId)
            => BinaryPrimitives.WriteUInt32BigEndian(dest, docId);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] DocIdKey(uint docId)
        {
            var buf = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(buf, docId);
            return buf;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] SegIdKey(ulong segId)
        {
            var buf = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(buf, segId);
            return buf;
        }

        // Posting list key: [segId 8 BE][field 1][token utf-8].
        public static byte[] PostingsKey(ulong segId, byte field, string token)
        {
            int tokenByteLen = Encoding.UTF8.GetByteCount(token);
            var buf = new byte[8 + 1 + tokenByteLen];
            BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(0, 8), segId);
            buf[8] = field;
            Encoding.UTF8.GetBytes(token, 0, token.Length, buf, 9);
            return buf;
        }

        // Posting list key prefix scoped to a single segment. Used to iterate all
        // (field, token) postings in a segment in sorted order.
        public static byte[] PostingsSegmentPrefix(ulong segId)
        {
            var buf = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(buf, segId);
            return buf;
        }

        // Parse a postings key back into its (segId, field, token) components.
        // Used when iterating per-segment posting lists during segment merges.
        public static void ParsePostingsKey(ReadOnlySpan<byte> key, out ulong segId, out byte field, out string token)
        {
            segId = BinaryPrimitives.ReadUInt64BigEndian(key);
            field = key[8];
            token = Encoding.UTF8.GetString(key.Slice(9));
        }
    }
}
