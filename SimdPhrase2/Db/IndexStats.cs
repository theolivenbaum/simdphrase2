using System;
using System.IO;
using System.Text.Json;
using SimdPhrase2.Storage;

namespace SimdPhrase2.Db
{
    public class IndexStats
    {
        public uint TotalDocs { get; set; }
        public ulong TotalTokens { get; set; }

        public static void Save(ISimdStorage storage, string path, IndexStats stats)
        {
            var json = JsonSerializer.Serialize(stats);
            storage.WriteAllText(path, json);
        }

        public static IndexStats Load(ISimdStorage storage, string path)
        {
            if (!storage.FileExists(path)) return new IndexStats();
            var json = storage.ReadAllText(path);
            return JsonSerializer.Deserialize<IndexStats>(json) ?? new IndexStats();
        }
    }
}
