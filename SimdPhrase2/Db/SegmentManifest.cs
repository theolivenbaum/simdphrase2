using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using SimdPhrase2.Storage;

namespace SimdPhrase2.Db
{
    /// <summary>
    /// Per-segment metadata persisted in the manifest.
    /// </summary>
    public class SegmentInfo
    {
        public string Id { get; set; }
        public uint TotalDocs { get; set; }
        public ulong TotalTokens { get; set; }
        public Dictionary<string, ulong> TokensByField { get; set; } = new();
        public Dictionary<string, uint> DocsByField { get; set; } = new();
    }

    /// <summary>
    /// List of active segments in an index. Persisted to segments.json at the index root.
    /// </summary>
    public class SegmentManifest
    {
        public List<SegmentInfo> Segments { get; set; } = new();
        public int NextSegmentNumber { get; set; } = 0;

        public string AllocateSegmentId()
        {
            var id = $"seg_{NextSegmentNumber:D6}";
            NextSegmentNumber++;
            return id;
        }

        public void Save(ISimdStorage storage, string path)
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = false });
            storage.WriteAllText(path, json);
        }

        public static SegmentManifest Load(ISimdStorage storage, string path)
        {
            if (!storage.FileExists(path)) return new SegmentManifest();
            try
            {
                var json = storage.ReadAllText(path);
                return JsonSerializer.Deserialize<SegmentManifest>(json) ?? new SegmentManifest();
            }
            catch
            {
                return new SegmentManifest();
            }
        }
    }
}
