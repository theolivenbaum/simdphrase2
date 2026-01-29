using System;
using System.IO;
using System.Text.Json;

namespace SimdPhrase2.Db
{
    public class IndexStats
    {
        public uint TotalDocs { get; set; }
        public ulong TotalTokens { get; set; }

        public static void Save(string path, IndexStats stats)
        {
            var json = JsonSerializer.Serialize(stats);
            File.WriteAllText(path, json);
        }

        public static IndexStats Load(string path)
        {
            if (!File.Exists(path)) return new IndexStats();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<IndexStats>(json) ?? new IndexStats();
        }
    }
}
