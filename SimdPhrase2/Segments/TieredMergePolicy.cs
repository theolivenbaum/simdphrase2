using System;
using System.Collections.Generic;
using System.Linq;

namespace SimdPhrase2.Segments
{
    // A merge policy inspired by Lucene's TieredMergePolicy but adapted to this index.
    //
    // Goals:
    //   - Keep no more than ~SegmentsPerTier segments at a similar size scale
    //   - Prefer merging similarly sized segments together (so each merge halves the
    //     count without doing wasted work on a giant segment)
    //   - Preempt merges for segments that have a high fraction of deletes, so deletes
    //     get reclaimed even when the size scale is balanced
    //   - Bound any single merge to at most MaxMergeAtOnce segments to keep merge work
    //     and IO bursts predictable
    //
    // We deliberately do NOT replicate Lucene's full scoring function - we only need a
    // reasonable cascading merge for this codebase. The intersect / SIMD search path
    // is unaffected by this policy.
    public sealed class TieredMergePolicy
    {
        public int MaxMergeAtOnce { get; set; } = 10;
        public int SegmentsPerTier { get; set; } = 10;

        // Segments smaller than this size (bytes) are floored to this size for tier
        // calculation, so a swarm of tiny segments still gets coalesced cleanly.
        public long FloorSegmentBytes { get; set; } = 2L * 1024 * 1024;

        // If a segment has more than this fraction of deletes it is eligible to be
        // merged on its own to reclaim the space.
        public double DeletesPctAllowed { get; set; } = 0.10;

        // Find the next merge to run, or null if the index is balanced.
        // Returns segments to merge (at least 2 unless purely a deletes-compaction).
        public List<SegmentInfo> FindMerge(IReadOnlyList<SegmentInfo> segments)
        {
            if (segments.Count == 0) return null;

            // Deletes compaction: prefer to compact a single segment with many deletes.
            // Pick the worst offender; this produces a smaller, deletes-free segment.
            SegmentInfo worstDeletes = null;
            double worstRatio = 0;
            foreach (var s in segments)
            {
                if (s.DocCount <= 0) continue;
                double ratio = (double)s.DeleteCount / s.DocCount;
                if (ratio > DeletesPctAllowed && ratio > worstRatio)
                {
                    worstRatio = ratio;
                    worstDeletes = s;
                }
            }
            if (worstDeletes != null) return new List<SegmentInfo> { worstDeletes };

            if (segments.Count <= SegmentsPerTier) return null;

            // Tier-based selection: sort by size asc, then look for the best window of
            // size MaxMergeAtOnce among the smaller segments. We prefer windows where
            // the largest segment isn't much bigger than the smallest, because that
            // makes the merge work proportional to the total useful output.
            var sorted = segments
                .Select(s => (info: s, size: Math.Max(s.SizeInBytes, FloorSegmentBytes)))
                .OrderBy(t => t.size)
                .ToList();

            // Excess segments above the desired count drive how aggressively we merge.
            int excess = segments.Count - SegmentsPerTier;
            int windowSize = Math.Min(MaxMergeAtOnce, Math.Max(2, excess + 1));

            // Iterate possible windows starting from the smallest segments.
            List<SegmentInfo> best = null;
            double bestScore = double.MaxValue;

            for (int start = 0; start + 2 <= sorted.Count; start++)
            {
                int end = Math.Min(start + windowSize, sorted.Count);
                long totalSize = 0;
                long maxSize = 0;
                for (int i = start; i < end; i++)
                {
                    totalSize += sorted[i].size;
                    if (sorted[i].size > maxSize) maxSize = sorted[i].size;
                }
                // Score: lower is better. Penalize windows where the largest segment
                // dominates (max/total close to 1 means we'd be doing a huge merge for
                // little structural gain).
                double score = (double)maxSize / Math.Max(1, totalSize);
                // Slight preference for larger windows (more structural reduction).
                score /= Math.Max(2, end - start);
                if (score < bestScore && (end - start) >= 2)
                {
                    bestScore = score;
                    best = new List<SegmentInfo>(end - start);
                    for (int i = start; i < end; i++) best.Add(sorted[i].info);
                }
            }

            return best;
        }

        // Returns the full list to merge in a force-merge to a single segment, or null
        // if there's already only one (or zero) segments.
        public List<SegmentInfo> FindForceMerge(IReadOnlyList<SegmentInfo> segments)
        {
            if (segments.Count <= 1) return null;
            return new List<SegmentInfo>(segments);
        }
    }
}
