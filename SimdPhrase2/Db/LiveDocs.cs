using System;
using System.Collections.Generic;
using System.IO;
using SimdPhrase2.Storage;

namespace SimdPhrase2.Db
{
    /// <summary>
    /// Tracks deleted document ids using a sparse hash set. Inverted from a
    /// "live" bitset for simplicity and to support sparse user-supplied doc ids.
    /// </summary>
    public class LiveDocs
    {
        private readonly HashSet<uint> _deleted = new();
        public int DeletedCount => _deleted.Count;

        public bool IsLive(uint docId) => !_deleted.Contains(docId);

        public bool MarkDeleted(uint docId) => _deleted.Add(docId);

        public IEnumerable<uint> DeletedDocs => _deleted;

        public void Save(ISimdStorage storage, string path)
        {
            using var fs = storage.OpenWrite(path);
            using var bw = new BinaryWriter(fs);
            bw.Write(_deleted.Count);
            foreach (var id in _deleted) bw.Write(id);
        }

        public static LiveDocs Load(ISimdStorage storage, string path)
        {
            var ld = new LiveDocs();
            if (!storage.FileExists(path)) return ld;
            using var fs = storage.OpenRead(path);
            using var br = new BinaryReader(fs);
            try
            {
                int count = br.ReadInt32();
                for (int i = 0; i < count; i++) ld._deleted.Add(br.ReadUInt32());
            }
            catch (EndOfStreamException) { }
            return ld;
        }
    }
}
