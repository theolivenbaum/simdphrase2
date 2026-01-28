using System;
using System.Collections.Generic;
using System.IO;

namespace SimdPhrase2.Db
{
    public struct FileOffset
    {
        public long Begin;
        public long Length;
    }

    public class TokenStore : IDisposable
    {
        private readonly string _path;
        private Dictionary<string, FileOffset> _map;
        private bool _dirty;

        public TokenStore(string basePath)
        {
            _path = Path.Combine(basePath, "token_map.bin");
            _map = new Dictionary<string, FileOffset>();
            Load();
        }

        private void Load()
        {
            if (!File.Exists(_path)) return;
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            try
            {
                int count = reader.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    string token = reader.ReadString();
                    long begin = reader.ReadInt64();
                    long len = reader.ReadInt64();
                    _map[token] = new FileOffset { Begin = begin, Length = len };
                }
            }
            catch (EndOfStreamException)
            {
                // corrupted or partial write
            }
        }

        public void Save()
        {
            if (!_dirty) return;
            using var fs = new FileStream(_path, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(fs);

            writer.Write(_map.Count);
            foreach (var kvp in _map)
            {
                writer.Write(kvp.Key);
                writer.Write(kvp.Value.Begin);
                writer.Write(kvp.Value.Length);
            }
            _dirty = false;
        }

        public void Add(string token, long begin, long length)
        {
            _map[token] = new FileOffset { Begin = begin, Length = length };
            _dirty = true;
        }

        public bool TryGet(string token, out FileOffset offset)
        {
            return _map.TryGetValue(token, out offset);
        }

        public IEnumerable<string> GetAllTokens()
        {
            return _map.Keys;
        }

        public void Dispose()
        {
            Save();
        }
    }
}
