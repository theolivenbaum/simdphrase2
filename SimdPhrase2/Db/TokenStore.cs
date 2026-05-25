using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using RocksDbSharp;
using SimdPhrase2.Storage;

namespace SimdPhrase2.Db
{
    // Metadata about a single posting list for a (field, token). The actual posting
    // data lives in the `postings` column family, keyed by (segId, field, token).
    public struct FileOffset
    {
        // Number of ulong entries in the posting list (i.e. value byte length / 8).
        public int UlongCount;
        // Number of unique doc ids represented by this posting list. Used by BM25.
        public int DocCount;

        // Backwards-compatible accessors kept for the few callers that still talk
        // in byte terms.
        public long Begin => 0;
        public long Length => (long)UlongCount * 8;
    }

    // Composite key identifying a posting list inside a segment: the field index
    // (0..255) plus the textual token. Stored as a struct (and not a string-prefixed
    // form) so the field byte and the token retain their own types throughout the
    // hot paths - the token comparisons that drive the k-way merge stay on the bare
    // string, and the field byte is a simple ordinal.
    public readonly struct FieldToken : IEquatable<FieldToken>, IComparable<FieldToken>
    {
        public readonly byte Field;
        public readonly string Token;

        public FieldToken(byte field, string token)
        {
            Field = field;
            Token = token;
        }

        public bool Equals(FieldToken other) => Field == other.Field && string.Equals(Token, other.Token, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is FieldToken o && Equals(o);
        public override int GetHashCode() => HashCode.Combine(Field, Token);

        public int CompareTo(FieldToken other)
        {
            int cmp = Field.CompareTo(other.Field);
            if (cmp != 0) return cmp;
            return string.CompareOrdinal(Token, other.Token);
        }

        public override string ToString() => $"[{Field}]{Token}";
    }

    // In-memory token map for a single segment. Persisted as one blob in the
    // seg_tokens column family - segId(8 BE) -> packed list of (field, token,
    // ulongCount, docCount). Loaded once at segment open and kept resident; the
    // actual posting data is loaded on demand from the `postings` CF.
    public sealed class TokenStore
    {
        private const int Version = 1;

        private readonly Dictionary<FieldToken, FileOffset> _map;

        public TokenStore()
        {
            _map = new Dictionary<FieldToken, FileOffset>();
        }

        public int Count => _map.Count;

        public void Add(byte field, string token, int ulongCount, int docCount)
        {
            _map[new FieldToken(field, token)] = new FileOffset { UlongCount = ulongCount, DocCount = docCount };
        }

        public bool TryGet(byte field, string token, out FileOffset offset)
            => _map.TryGetValue(new FieldToken(field, token), out offset);

        // Backward-compatible shorthand: looks up in field 0.
        public bool TryGet(string token, out FileOffset offset) => TryGet(0, token, out offset);

        public IEnumerable<FieldToken> GetAllEntries() => _map.Keys;

        // Legacy enumeration of token strings (assumes field 0).
        public IEnumerable<string> GetAllTokens()
        {
            foreach (var k in _map.Keys)
                if (k.Field == 0) yield return k.Token;
        }

        // Serialize the in-memory token map to a single byte blob suitable for
        // storage as a value in the seg_tokens CF.
        public byte[] Serialize()
        {
            // Predict size: int32 version, int32 count, then per entry: byte field,
            // int32 tokenByteLen, utf-8 bytes, int32 ulongCount, int32 docCount.
            int size = 4 + 4;
            foreach (var kvp in _map)
            {
                size += 1 + 4 + Encoding.UTF8.GetByteCount(kvp.Key.Token) + 4 + 4;
            }
            var buf = new byte[size];
            var span = buf.AsSpan();
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(0, 4), Version);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(4, 4), _map.Count);
            int pos = 8;
            foreach (var kvp in _map)
            {
                buf[pos++] = kvp.Key.Field;
                int n = Encoding.UTF8.GetByteCount(kvp.Key.Token);
                BinaryPrimitives.WriteInt32LittleEndian(span.Slice(pos, 4), n);
                pos += 4;
                Encoding.UTF8.GetBytes(kvp.Key.Token, 0, kvp.Key.Token.Length, buf, pos);
                pos += n;
                BinaryPrimitives.WriteInt32LittleEndian(span.Slice(pos, 4), kvp.Value.UlongCount);
                pos += 4;
                BinaryPrimitives.WriteInt32LittleEndian(span.Slice(pos, 4), kvp.Value.DocCount);
                pos += 4;
            }
            return buf;
        }

        public static TokenStore Deserialize(ReadOnlySpan<byte> bytes)
        {
            var store = new TokenStore();
            if (bytes.Length < 8) return store;
            int version = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(0, 4));
            if (version != Version) throw new InvalidOperationException($"TokenStore version mismatch: file is {version}, code expects {Version}.");
            int count = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(4, 4));
            int pos = 8;
            for (int i = 0; i < count; i++)
            {
                if (pos + 1 + 4 > bytes.Length) break;
                byte field = bytes[pos++];
                int n = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(pos, 4));
                pos += 4;
                if (pos + n + 8 > bytes.Length) break;
                string token = Encoding.UTF8.GetString(bytes.Slice(pos, n));
                pos += n;
                int ulongCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(pos, 4));
                pos += 4;
                int docCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(pos, 4));
                pos += 4;
                store._map[new FieldToken(field, token)] = new FileOffset { UlongCount = ulongCount, DocCount = docCount };
            }
            return store;
        }

        public static TokenStore Load(SimdPhraseDb db, ulong segId)
        {
            var key = Keys.SegIdKey(segId);
            var bytes = db.Db.Get(key, db.SegTokens);
            if (bytes == null) return new TokenStore();
            return Deserialize(bytes);
        }

        public void AddToBatch(WriteBatch batch, ColumnFamilyHandle segTokensCf, ulong segId)
        {
            var key = Keys.SegIdKey(segId);
            batch.Put(key, Serialize(), segTokensCf);
        }
    }
}
