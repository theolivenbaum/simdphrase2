using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using SimdPhrase2.Storage;

namespace SimdPhrase2.Segments
{
    public sealed class SegmentInfo
    {
        public string Id { get; set; } = "";
        // Approximate size in bytes (sum of packed file length + token map). Used by merge policy.
        public long SizeInBytes { get; set; }
        // Number of unique docs in the segment.
        public int DocCount { get; set; }
        // Number of deletes recorded against this segment.
        public int DeleteCount { get; set; }
        // True if this segment was produced by a merge (used as a hint by the merge policy).
        public bool MergedSegment { get; set; }

        public int LiveDocCount => DocCount - DeleteCount;
    }

    public sealed class SegmentManifest
    {
        public int Version { get; set; } = 1;
        public int NextSegmentId { get; set; }
        public List<SegmentInfo> Segments { get; set; } = new();

        public static string ManifestPath(ISimdStorage storage, string indexPath)
            => storage.Combine(indexPath, "segments.json");

        public static SegmentManifest Load(ISimdStorage storage, string indexPath)
        {
            var path = ManifestPath(storage, indexPath);
            if (!storage.FileExists(path)) return new SegmentManifest();
            try
            {
                var json = storage.ReadAllText(path);
                return JsonSerializer.Deserialize<SegmentManifest>(json) ?? new SegmentManifest();
            }
            catch (JsonException)
            {
                return new SegmentManifest();
            }
        }

        public void Save(ISimdStorage storage, string indexPath)
        {
            var path = ManifestPath(storage, indexPath);
            var json = JsonSerializer.Serialize(this);
            storage.WriteAllText(path, json);
        }

        public string AllocateSegmentId()
        {
            int id = NextSegmentId++;
            return $"seg_{id:D6}";
        }

        public static string SegmentDirectory(ISimdStorage storage, string indexPath, string segmentId)
            => storage.Combine(indexPath, storage.Combine("segments", segmentId));
    }
}
