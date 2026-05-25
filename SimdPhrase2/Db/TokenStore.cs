using System;
using System.Collections.Generic;
using System.IO;
using SimdPhrase2.Storage;

namespace SimdPhrase2.Db
{
    public struct FileOffset
    {
        public long Begin;
        public long Length;
        public int DocCount;
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

    public class TokenStore : IDisposable
    {
        private const int Version = 2;

        private readonly string _path;
        private Dictionary<FieldToken, FileOffset> _map;
        private bool _dirty;
        private readonly ISimdStorage _storage;

        public TokenStore(string basePath, ISimdStorage storage = null)
        {
            _storage = storage ?? new FileSystemStorage();
            _path = _storage.Combine(basePath, "token_map.bin");
            _map = new Dictionary<FieldToken, FileOffset>();
            Load();
        }

        private void Load()
        {
            if (!_storage.FileExists(_path)) return;
            using var fs = _storage.OpenRead(_path);
            using var reader = new BinaryReader(fs);

            try
            {
                int version = reader.ReadInt32();
                if (version != Version)
                {
                    // Incompatible on-disk format - the caller is expected to rebuild the index.
                    throw new InvalidDataException($"TokenStore version mismatch: file is {version}, code expects {Version}.");
                }
                int count = reader.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    byte field = reader.ReadByte();
                    string token = reader.ReadString();
                    long begin = reader.ReadInt64();
                    long len = reader.ReadInt64();
                    int docCount = reader.ReadInt32();
                    _map[new FieldToken(field, token)] = new FileOffset { Begin = begin, Length = len, DocCount = docCount };
                }
            }
            catch (EndOfStreamException)
            {
                // partial write - tolerate
            }
        }

        public void Save()
        {
            if (!_dirty) return;
            using var fs = _storage.OpenWrite(_path);
            using var writer = new BinaryWriter(fs);

            writer.Write(Version);
            writer.Write(_map.Count);
            foreach (var kvp in _map)
            {
                writer.Write(kvp.Key.Field);
                writer.Write(kvp.Key.Token);
                writer.Write(kvp.Value.Begin);
                writer.Write(kvp.Value.Length);
                writer.Write(kvp.Value.DocCount);
            }
            _dirty = false;
        }

        public void Add(byte field, string token, long begin, long length, int docCount)
        {
            _map[new FieldToken(field, token)] = new FileOffset { Begin = begin, Length = length, DocCount = docCount };
            _dirty = true;
        }

        // Backward-compatible: defaults to field 0.
        public void Add(string token, long begin, long length, int docCount)
            => Add(0, token, begin, length, docCount);

        public bool TryGet(byte field, string token, out FileOffset offset)
        {
            return _map.TryGetValue(new FieldToken(field, token), out offset);
        }

        // Backward-compatible shorthand: looks up in field 0.
        public bool TryGet(string token, out FileOffset offset) => TryGet(0, token, out offset);

        public IEnumerable<FieldToken> GetAllEntries() => _map.Keys;

        // Legacy enumeration of token strings (assumes field 0). Kept for tests
        // that exercise the field-less surface.
        public IEnumerable<string> GetAllTokens()
        {
            foreach (var k in _map.Keys)
                if (k.Field == 0) yield return k.Token;
        }

        public void Dispose()
        {
            Save();
        }
    }
}
