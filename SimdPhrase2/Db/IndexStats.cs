using System;
using System.IO;
using System.Text.Json;
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

        public static void Save(ISimdStorage storage, string path, IndexStats stats)
        {
            // Keep the on-disk representation tolerant of older shapes: only
            // serialise the per-field array when it's non-trivial. (System.Text.Json
            // is happy to round-trip either way; the field check is just so a
            // single-field index doesn't grow a noisy stats file.)
            var json = JsonSerializer.Serialize(stats);
            storage.WriteAllText(path, json);
        }

        public static IndexStats Load(ISimdStorage storage, string path)
        {
            if (!storage.FileExists(path)) return new IndexStats();
            var json = storage.ReadAllText(path);
            var stats = JsonSerializer.Deserialize<IndexStats>(json) ?? new IndexStats();

            if (stats.FieldCount <= 0) stats.FieldCount = 1;
            if (stats.TotalTokensPerField == null || stats.TotalTokensPerField.Length != stats.FieldCount)
            {
                // Older stats files only stored the aggregate. Fold it into field 0.
                var perField = new ulong[stats.FieldCount];
                if (stats.FieldCount > 0) perField[0] = stats.TotalTokens;
                stats.TotalTokensPerField = perField;
            }
            return stats;
        }
    }
}
