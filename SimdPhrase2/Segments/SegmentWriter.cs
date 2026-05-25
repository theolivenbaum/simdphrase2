using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using RocksDbSharp;
using SimdPhrase2.Db;
using SimdPhrase2.Roaringish;
using SimdPhrase2.Storage;

namespace SimdPhrase2.Segments
{
    // Builds an immutable segment in RocksDB from one of:
    //   1. An in-memory dictionary of (FieldToken -> RoaringishPacked) - typical commit path.
    //   2. Multiple existing segments (used by the merge policy).
    //
    // Writes - all via a single WriteBatch so a segment becomes visible atomically:
    //   - postings CF:      one entry per (segId, field, token) -> raw posting bytes
    //   - seg_tokens CF:    token map for the segment
    //   - seg_meta CF:      SegmentInfo (doc count, size, etc.)
    //   - seg_live_docs CF: bitmap of doc ids actually present in the segment
    //   - meta CF:          next_segment_id counter (so the allocated id is persisted)
    public static class SegmentWriter
    {
        public static SegmentInfo Write(
            SimdPhraseDb db,
            ulong segmentId,
            Dictionary<FieldToken, RoaringishPacked> inMemoryBatch,
            WriteBatch sharedBatch = null,
            HashSet<uint> dropDocs = null)
        {
            var readers = new List<ISegmentInputReader>();
            try
            {
                if (inMemoryBatch != null && inMemoryBatch.Count > 0)
                {
                    readers.Add(new InMemoryReader(inMemoryBatch, readers.Count));
                }
                return MergeReadersIntoSegment(db, segmentId, readers, sharedBatch, dropDocs);
            }
            finally
            {
                foreach (var r in readers) r.Dispose();
            }
        }

        // Produce a new segment by merging existing source segments.
        public static SegmentInfo Merge(
            SimdPhraseDb db,
            ulong newSegmentId,
            IReadOnlyList<SegmentReader> sources,
            WriteBatch sharedBatch = null)
        {
            var readers = new List<ISegmentInputReader>();
            try
            {
                for (int i = 0; i < sources.Count; i++)
                {
                    readers.Add(new SegmentPostingsIteratorReader(db, sources[i], i));
                }
                var info = MergeReadersIntoSegment(db, newSegmentId, readers, sharedBatch, dropDocs: null);
                info.MergedSegment = true;
                info.Id = newSegmentId;
                return info;
            }
            finally
            {
                foreach (var r in readers) r.Dispose();
            }
        }

        private static SegmentInfo MergeReadersIntoSegment(
            SimdPhraseDb db,
            ulong segmentId,
            List<ISegmentInputReader> readers,
            WriteBatch sharedBatch,
            HashSet<uint> dropDocs)
        {
            bool ownBatch = sharedBatch == null;
            var batch = sharedBatch ?? new WriteBatch();
            try
            {
                var pq = new PriorityQueue<ISegmentInputReader, (FieldToken, int)>(Comparer<(FieldToken, int)>.Create((a, b) =>
                {
                    int cmp = a.Item1.CompareTo(b.Item1);
                    return cmp != 0 ? cmp : a.Item2.CompareTo(b.Item2);
                }));

                foreach (var r in readers)
                {
                    if (!r.Finished) pq.Enqueue(r, (r.CurrentKey, r.Order));
                }

                var tokenStore = new TokenStore();
                var liveDocs = new RoaringBitmap();
                long totalBytes = 0;

                // Reused per token, growable.
                var mergedBuffer = new List<ulong>(64);
                var chunkList = new List<byte[]>(4);

                while (pq.Count > 0)
                {
                    var reader = pq.Dequeue();
                    FieldToken key = reader.CurrentKey;

                    chunkList.Clear();
                    chunkList.Add(reader.CurrentData);
                    reader.Next();
                    if (!reader.Finished) pq.Enqueue(reader, (reader.CurrentKey, reader.Order));

                    while (pq.Count > 0 && pq.Peek().CurrentKey.Equals(key))
                    {
                        var nextReader = pq.Dequeue();
                        chunkList.Add(nextReader.CurrentData);
                        nextReader.Next();
                        if (!nextReader.Finished) pq.Enqueue(nextReader, (nextReader.CurrentKey, nextReader.Order));
                    }

                    byte[] finalBytes;
                    int docCount;
                    if (chunkList.Count == 1 && dropDocs == null)
                    {
                        finalBytes = chunkList[0];
                        docCount = CountDocs(finalBytes, liveDocs);
                    }
                    else
                    {
                        mergedBuffer.Clear();
                        KWayMergePacked(chunkList, mergedBuffer, dropDocs, liveDocs, out docCount);
                        // Allocate exactly one final buffer.
                        finalBytes = new byte[mergedBuffer.Count * 8];
                        MemoryMarshal.Cast<ulong, byte>(CollectionsMarshal.AsSpan(mergedBuffer)).CopyTo(finalBytes);
                    }

                    if (finalBytes.Length == 0)
                    {
                        // entirely-dropped token (e.g. all docs deleted) - skip emit
                        continue;
                    }

                    // Write the posting list to the postings CF.
                    var pkKey = Keys.PostingsKey(segmentId, key.Field, key.Token);
                    batch.Put(pkKey, finalBytes, db.Postings);

                    tokenStore.Add(key.Field, key.Token, finalBytes.Length / 8, docCount);
                    totalBytes += finalBytes.Length;
                }

                // Persist the token map (single value per segment).
                tokenStore.AddToBatch(batch, db.SegTokens, segmentId);

                // Persist the live-docs bitmap.
                batch.Put(Keys.SegIdKey(segmentId), liveDocs.SaveToBytes(), db.SegLiveDocs);

                var info = new SegmentInfo
                {
                    Id = segmentId,
                    SizeInBytes = totalBytes,
                    DocCount = (int)liveDocs.Cardinality,
                };
                batch.Put(Keys.SegIdKey(segmentId), info.Serialize(), db.SegMeta);

                if (ownBatch)
                {
                    db.Db.Write(batch);
                }
                return info;
            }
            finally
            {
                if (ownBatch) batch.Dispose();
            }
        }

        private static int CountDocs(byte[] data, RoaringBitmap liveDocs)
        {
            var span = MemoryMarshal.Cast<byte, ulong>(data);
            uint lastDocId = uint.MaxValue;
            int count = 0;
            for (int i = 0; i < span.Length; i++)
            {
                uint docId = RoaringishPacked.UnpackDocId(span[i]);
                if (docId != lastDocId)
                {
                    count++;
                    lastDocId = docId;
                }
                liveDocs.Add(docId);
            }
            return count;
        }

        // K-way merge of multiple packed byte arrays into `output`. Each ulong
        // entry holds [docId(32) | group(16) | values(16)]. We sort by (docId,group)
        // and OR overlapping value bitmaps. Entries belonging to docs in `dropDocs`
        // (used during compaction to drop deletes) are filtered out.
        private static void KWayMergePacked(
            List<byte[]> chunks,
            List<ulong> output,
            HashSet<uint> dropDocs,
            RoaringBitmap liveDocs,
            out int docCount)
        {
            int n = chunks.Count;
            var indices = new int[n];
            var spans = new ulong[n][];
            for (int i = 0; i < n; i++)
            {
                spans[i] = new ulong[chunks[i].Length / 8];
                Buffer.BlockCopy(chunks[i], 0, spans[i], 0, chunks[i].Length);
            }

            ulong currentKey = 0;
            ulong currentValues = 0;
            bool hasCurrent = false;
            uint lastDoc = uint.MaxValue;
            int docs = 0;

            while (true)
            {
                int minR = -1;
                ulong minKey = ulong.MaxValue;
                ulong minVal = 0;
                for (int r = 0; r < n; r++)
                {
                    if (indices[r] >= spans[r].Length) continue;
                    ulong p = spans[r][indices[r]];
                    ulong k = RoaringishPacked.ClearValues(p);
                    if (minR == -1 || k < minKey)
                    {
                        minR = r; minKey = k; minVal = (ulong)RoaringishPacked.UnpackValues(p);
                    }
                }
                if (minR == -1) break;
                indices[minR]++;

                // Drop entries belonging to deleted docs.
                if (dropDocs != null)
                {
                    uint docId = RoaringishPacked.UnpackDocId(minKey);
                    if (dropDocs.Contains(docId)) continue;
                }

                if (!hasCurrent) { currentKey = minKey; currentValues = minVal; hasCurrent = true; }
                else if (minKey == currentKey) { currentValues |= minVal; }
                else
                {
                    Emit(currentKey | currentValues, output, liveDocs, ref lastDoc, ref docs);
                    currentKey = minKey; currentValues = minVal;
                }
            }
            if (hasCurrent)
            {
                Emit(currentKey | currentValues, output, liveDocs, ref lastDoc, ref docs);
            }
            docCount = docs;
        }

        private static void Emit(ulong packed, List<ulong> output, RoaringBitmap liveDocs, ref uint lastDoc, ref int docs)
        {
            output.Add(packed);
            uint docId = RoaringishPacked.UnpackDocId(packed);
            if (docId != lastDoc)
            {
                docs++;
                lastDoc = docId;
            }
            liveDocs.Add(docId);
        }

        // ---------- Input readers ----------

        internal interface ISegmentInputReader : IDisposable
        {
            int Order { get; }
            FieldToken CurrentKey { get; }
            byte[] CurrentData { get; }
            bool Finished { get; }
            void Next();
        }

        // Reader over an in-memory dictionary (sorted on first Next).
        private sealed class InMemoryReader : ISegmentInputReader
        {
            public int Order { get; }
            public FieldToken CurrentKey { get; private set; }
            public byte[] CurrentData { get; private set; }
            public bool Finished { get; private set; }

            private readonly List<KeyValuePair<FieldToken, RoaringishPacked>> _items;
            private int _pos;

            public InMemoryReader(Dictionary<FieldToken, RoaringishPacked> dict, int order)
            {
                Order = order;
                _items = new List<KeyValuePair<FieldToken, RoaringishPacked>>(dict.Count);
                foreach (var kvp in dict) _items.Add(kvp);
                _items.Sort((a, b) => a.Key.CompareTo(b.Key));
                Next();
            }

            public void Next()
            {
                if (_pos >= _items.Count) { Finished = true; CurrentKey = default; CurrentData = null; return; }
                var kvp = _items[_pos++];
                CurrentKey = kvp.Key;
                var span = kvp.Value.AsSpan();
                // Allocate exactly the bytes we need, in one chunk.
                var bytes = new byte[span.Length * 8];
                MemoryMarshal.Cast<ulong, byte>(span).CopyTo(bytes);
                CurrentData = bytes;
            }

            public void Dispose() { }
        }

        // Reader over an existing segment's `postings` rows, iterating the postings
        // CF with a prefix scoped to the segment id - this yields entries in
        // (field, token) order without ever materializing the whole segment.
        // Entries for deleted doc ids are filtered out at emit time.
        private sealed class SegmentPostingsIteratorReader : ISegmentInputReader
        {
            public int Order { get; }
            public FieldToken CurrentKey { get; private set; }
            public byte[] CurrentData { get; private set; }
            public bool Finished { get; private set; }

            private readonly SimdPhraseDb _db;
            private readonly SegmentReader _segment;
            private readonly Iterator _iter;
            private readonly byte[] _prefix;

            public SegmentPostingsIteratorReader(SimdPhraseDb db, SegmentReader segment, int order)
            {
                _db = db;
                _segment = segment;
                Order = order;
                _prefix = Keys.PostingsSegmentPrefix(segment.Id);
                _iter = db.Db.NewIterator(db.Postings);
                _iter.Seek(_prefix);
                Advance();
            }

            private void Advance()
            {
                while (_iter.Valid())
                {
                    var keySpan = _iter.GetKeySpan();
                    if (keySpan.Length < _prefix.Length || !keySpan.Slice(0, _prefix.Length).SequenceEqual(_prefix))
                    {
                        // moved past the segment.
                        Finished = true;
                        CurrentKey = default;
                        CurrentData = null;
                        return;
                    }
                    Keys.ParsePostingsKey(keySpan, out _, out byte field, out string token);

                    var valSpan = _iter.GetValueSpan();
                    byte[] data;
                    if (_segment.Deletes.IsEmpty)
                    {
                        data = valSpan.ToArray();
                    }
                    else
                    {
                        data = ApplyDeletes(valSpan, _segment.Deletes);
                    }
                    _iter.Next();

                    if (data.Length == 0) continue; // entirely deleted

                    CurrentKey = new FieldToken(field, token);
                    CurrentData = data;
                    return;
                }
                Finished = true;
                CurrentKey = default;
                CurrentData = null;
            }

            public void Next() => Advance();

            public void Dispose() => _iter.Dispose();

            // Filter out entries belonging to deleted docs. Each ulong is one packed
            // group of up to 16 positions for one (docId, group) pair, so we either
            // keep the whole group (live doc) or drop it (deleted doc).
            private static byte[] ApplyDeletes(ReadOnlySpan<byte> packedBytes, RoaringBitmap deletes)
            {
                var packed = MemoryMarshal.Cast<byte, ulong>(packedBytes);
                int kept = 0;
                for (int i = 0; i < packed.Length; i++)
                {
                    uint docId = RoaringishPacked.UnpackDocId(packed[i]);
                    if (!deletes.Contains(docId)) kept++;
                }
                if (kept == 0) return Array.Empty<byte>();
                if (kept == packed.Length)
                {
                    // No deletes hit this list: just copy the bytes through.
                    var copy = new byte[packedBytes.Length];
                    packedBytes.CopyTo(copy);
                    return copy;
                }

                var outArr = new byte[kept * 8];
                Span<ulong> outUlong = MemoryMarshal.Cast<byte, ulong>(outArr.AsSpan());
                int j = 0;
                for (int i = 0; i < packed.Length; i++)
                {
                    uint docId = RoaringishPacked.UnpackDocId(packed[i]);
                    if (!deletes.Contains(docId)) outUlong[j++] = packed[i];
                }
                return outArr;
            }
        }
    }
}
