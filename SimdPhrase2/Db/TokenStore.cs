using System;
using System.Collections.Generic;
using System.IO;

namespace SimdPhrase2.Db
{
    public struct FileOffset
    {
        public long Begin;
        public long Length;
        public int DocCount;
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
                    int docCount = 0;
                    // Backward compatibility check could be here, but we are rewriting.
                    // Assuming fresh index or rebuilt index.
                    // For safety, checking stream position? No, binary format doesn't support versioning yet.
                    // We assume the user will re-index.
                    try
                    {
                        docCount = reader.ReadInt32();
                    }
                    catch (EndOfStreamException)
                    {
                         // If we are reading an old index, we might hit EOF here if we are strict.
                         // But for now we just proceed. Old indices will be incompatible.
                         docCount = 0;
                    }

                    _map[token] = new FileOffset { Begin = begin, Length = len, DocCount = docCount };
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
                writer.Write(kvp.Value.DocCount);
            }
            _dirty = false;
        }

        public void Add(string token, long begin, long length, int docCount)
        {
            _map[token] = new FileOffset { Begin = begin, Length = length, DocCount = docCount };
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
