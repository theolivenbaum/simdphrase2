using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SimdPhrase2.Db;
using SimdPhrase2.Roaringish;
using SimdPhrase2.Storage;

namespace SimdPhrase2.Segments
{
    // Builds an immutable segment on disk from one of:
    //   1. An in-memory dictionary of (token -> RoaringishPacked) - typical commit path.
    //   2. Multiple existing batch files written by the indexer.
    //   3. Multiple existing segments (used by the merge policy).
    //
    // Writes:
    //   - <segmentDir>/roaringish_packed.bin
    //   - <segmentDir>/token_map.bin
    //   - <segmentDir>/deletes.bin (only if deletes are present)
    public static class SegmentWriter
    {
        // Write a segment from an in-memory token dictionary plus optional staged batch
        // files. Each batch file uses the same format produced by the existing indexer's
        // FlushBatch, so we reuse that format here. After writing, the batch files are
        // deleted by the caller.
        public static SegmentInfo Write(
            ISimdStorage storage,
            string indexPath,
            string segmentId,
            Dictionary<FieldToken, RoaringishPacked> inMemoryBatch,
            IReadOnlyList<string> stagedBatchFiles)
        {
            var dir = SegmentManifest.SegmentDirectory(storage, indexPath, segmentId);
            storage.CreateDirectory(dir);

            // We feed the merge from a sorted in-memory enumerator + each staged batch file.
            var readers = new List<ISegmentInputReader>();
            try
            {
                if (inMemoryBatch != null && inMemoryBatch.Count > 0)
                {
                    readers.Add(new InMemoryReader(inMemoryBatch, readers.Count));
                }
                if (stagedBatchFiles != null)
                {
                    for (int i = 0; i < stagedBatchFiles.Count; i++)
                    {
                        readers.Add(new BatchFileReader(storage, stagedBatchFiles[i], readers.Count));
                    }
                }

                return MergeReadersIntoSegment(storage, dir, readers);
            }
            finally
            {
                foreach (var r in readers) r.Dispose();
            }
        }

        // Produce a new segment by merging existing source segments. Source segments'
        // packed posting lists already use global doc IDs and are already sorted, so
        // we simply concatenate per token (preserving sorted token order via a priority
        // queue). Deleted doc IDs are dropped via a streaming filter; the resulting
        // segment has no deletes.
        public static SegmentInfo Merge(
            ISimdStorage storage,
            string indexPath,
            string newSegmentId,
            IReadOnlyList<SegmentReader> sources)
        {
            var dir = SegmentManifest.SegmentDirectory(storage, indexPath, newSegmentId);
            storage.CreateDirectory(dir);

            var readers = new List<ISegmentInputReader>();
            try
            {
                for (int i = 0; i < sources.Count; i++)
                {
                    readers.Add(new SegmentTokenReader(sources[i], i));
                }
                var info = MergeReadersIntoSegment(storage, dir, readers);
                info.MergedSegment = true;
                info.Id = newSegmentId;
                return info;
            }
            finally
            {
                foreach (var r in readers) r.Dispose();
            }
        }

        // Merge any number of token-stream readers into a single segment directory.
        // All readers expose entries of ((field,token), packedBytes) sorted by
        // (field, token). Returns SegmentInfo with Id unset, but DocCount set to the
        // true number of unique doc ids written, and SizeInBytes set to the packed
        // file size. Also writes a doc_ids.bin roaring bitmap of the doc ids
        // actually present in this segment so that delete tracking is precise even
        // after merges.
        private static SegmentInfo MergeReadersIntoSegment(ISimdStorage storage, string dir, List<ISegmentInputReader> readers)
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

            using var tokenStore = new TokenStore(dir, storage);
            using var packedFile = storage.OpenWrite(storage.Combine(dir, "roaringish_packed.bin"));

            // Track all unique doc ids seen across the segment (cheap roaring bitmap).
            var liveDocs = new RoaringBitmap();

            while (pq.Count > 0)
            {
                var reader = pq.Dequeue();
                FieldToken key = reader.CurrentKey;

                // 64-byte align for SIMD loads.
                long currentPos = packedFile.Position;
                long alignedPos = (currentPos + 63) & ~63;
                if (alignedPos > currentPos)
                {
                    packedFile.Write(new byte[alignedPos - currentPos]);
                }

                long startOffset = packedFile.Position;
                long totalLength = 0;
                int docCount = 0;
                uint lastDocId = uint.MaxValue;

                // Collect all chunks for this key across readers in input order.
                var chunks = new List<byte[]>();
                chunks.Add(reader.CurrentData);
                reader.Next();
                if (!reader.Finished) pq.Enqueue(reader, (reader.CurrentKey, reader.Order));

                while (pq.Count > 0 && pq.Peek().CurrentKey.Equals(key))
                {
                    var nextReader = pq.Dequeue();
                    chunks.Add(nextReader.CurrentData);
                    nextReader.Next();
                    if (!nextReader.Finished) pq.Enqueue(nextReader, (nextReader.CurrentKey, nextReader.Order));
                }

                // The chunks are individually sorted by global doc id (each batch builds
                // packed lists in order of incoming docs). When merging segments, all
                // global doc ids are unique across segments because each commit only
                // produces docs newer than prior segments. We still tolerate generic
                // input by k-way merging by docId+group key.
                if (chunks.Count == 1)
                {
                    var data = chunks[0];
                    packedFile.Write(data);
                    totalLength += data.Length;
                    CountDocsInPacked(data, ref lastDocId, ref docCount, liveDocs);
                }
                else
                {
                    KWayMergePackedToFile(chunks, packedFile, ref totalLength, ref lastDocId, ref docCount, liveDocs);
                }

                tokenStore.Add(key.Field, key.Token, startOffset, totalLength, docCount);
            }

            packedFile.Flush();

            // Persist the per-segment live-doc-ids bitmap. Used by the indexer when
            // applying pending deletes (only counts deletes that match a doc actually
            // in this segment) and by the searcher as a quick "any docs?" probe.
            using (var s = storage.OpenWrite(storage.Combine(dir, "doc_ids.bin")))
            {
                liveDocs.Save(s);
            }

            return new SegmentInfo
            {
                SizeInBytes = SafePackedSize(storage, dir),
                DocCount = (int)liveDocs.Cardinality,
            };
        }

        private static long SafePackedSize(ISimdStorage storage, string dir)
        {
            var path = storage.Combine(dir, "roaringish_packed.bin");
            if (!storage.FileExists(path)) return 0;
            using var s = storage.OpenRead(path);
            return s.Length;
        }

        private static void CountDocsInPacked(byte[] data, ref uint lastDocId, ref int docCount, RoaringBitmap liveDocs)
        {
            var span = MemoryMarshal.Cast<byte, ulong>(data);
            for (int i = 0; i < span.Length; i++)
            {
                uint docId = RoaringishPacked.UnpackDocId(span[i]);
                if (docId != lastDocId)
                {
                    docCount++;
                    lastDocId = docId;
                }
                liveDocs.Add(docId);
            }
        }

        // K-way merge of multiple packed byte arrays into the output file. Each ulong
        // entry holds [docId(32) | group(16) | values(16)]. We sort by (docId,group) and
        // OR overlapping value bitmaps.
        private static void KWayMergePackedToFile(
            List<byte[]> chunks, Stream output,
            ref long totalLength, ref uint lastDocId, ref int docCount, RoaringBitmap liveDocs)
        {
            int n = chunks.Count;
            var spans = new ulong[n][];
            var idx = new int[n];
            for (int i = 0; i < n; i++)
            {
                spans[i] = new ulong[chunks[i].Length / 8];
                Buffer.BlockCopy(chunks[i], 0, spans[i], 0, chunks[i].Length);
            }

            ulong currentKey = 0;
            ulong currentValues = 0;
            bool hasCurrent = false;

            const int BufBytes = 1 << 16;
            var buf = new byte[BufBytes];
            int bufPos = 0;
            long bytesWritten = 0;
            uint lastDoc = lastDocId;
            int docs = docCount;

            while (true)
            {
                int minR = -1;
                ulong minKey = ulong.MaxValue;
                ulong minVal = 0;
                for (int r = 0; r < n; r++)
                {
                    if (idx[r] >= spans[r].Length) continue;
                    ulong p = spans[r][idx[r]];
                    ulong key = RoaringishPacked.ClearValues(p);
                    if (minR == -1 || key < minKey)
                    {
                        minR = r; minKey = key; minVal = (ulong)RoaringishPacked.UnpackValues(p);
                    }
                }
                if (minR == -1) break;
                idx[minR]++;
                if (!hasCurrent) { currentKey = minKey; currentValues = minVal; hasCurrent = true; }
                else if (minKey == currentKey) { currentValues |= minVal; }
                else
                {
                    EmitEntry(currentKey | currentValues, output, buf, ref bufPos, ref bytesWritten, ref lastDoc, ref docs, liveDocs);
                    currentKey = minKey; currentValues = minVal;
                }
            }
            if (hasCurrent)
            {
                EmitEntry(currentKey | currentValues, output, buf, ref bufPos, ref bytesWritten, ref lastDoc, ref docs, liveDocs);
            }
            if (bufPos > 0) { output.Write(buf, 0, bufPos); bytesWritten += bufPos; bufPos = 0; }

            totalLength += bytesWritten;
            lastDocId = lastDoc;
            docCount = docs;
        }

        private static void EmitEntry(ulong packed, Stream output, byte[] buf, ref int bufPos, ref long bytesWritten, ref uint lastDoc, ref int docs, RoaringBitmap liveDocs)
        {
            if (bufPos + 8 > buf.Length)
            {
                output.Write(buf, 0, bufPos);
                bytesWritten += bufPos;
                bufPos = 0;
            }
            BitConverter.TryWriteBytes(buf.AsSpan(bufPos, 8), packed);
            bufPos += 8;
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
                var bytes = new byte[span.Length * 8];
                MemoryMarshal.Cast<ulong, byte>(span).CopyTo(bytes);
                CurrentData = bytes;
            }

            public void Dispose() { /* RoaringishPacked instances are owned by the caller */ }
        }

        // Reader over a batch_<n>.bin file produced by the indexer.
        // Format: repeated [byte field][string token][int len][len*8 bytes].
        private sealed class BatchFileReader : ISegmentInputReader
        {
            public int Order { get; }
            public FieldToken CurrentKey { get; private set; }
            public byte[] CurrentData { get; private set; }
            public bool Finished { get; private set; }

            private readonly Stream _fs;
            private readonly BinaryReader _br;

            public BatchFileReader(ISimdStorage storage, string path, int order)
            {
                Order = order;
                _fs = storage.OpenRead(path);
                _br = new BinaryReader(_fs);
                Next();
            }

            public void Next()
            {
                if (_fs.Position >= _fs.Length) { Finished = true; CurrentKey = default; CurrentData = null; return; }
                try
                {
                    byte field = _br.ReadByte();
                    string token = _br.ReadString();
                    int len = _br.ReadInt32();
                    CurrentKey = new FieldToken(field, token);
                    CurrentData = _br.ReadBytes(len * 8);
                }
                catch (EndOfStreamException)
                {
                    Finished = true;
                }
            }

            public void Dispose() { _br.Dispose(); _fs.Dispose(); }
        }

        // Reader over a fully built SegmentReader. Iterates entries in sorted order
        // (by field then token). For each entry, reads the matching slice of the
        // packed file, applying the deletes bitmap by dropping entries that belong
        // to deleted doc IDs.
        private sealed class SegmentTokenReader : ISegmentInputReader
        {
            public int Order { get; }
            public FieldToken CurrentKey { get; private set; }
            public byte[] CurrentData { get; private set; }
            public bool Finished { get; private set; }

            private readonly SegmentReader _segment;
            private readonly List<FieldToken> _keys;
            private int _pos;

            public SegmentTokenReader(SegmentReader segment, int order)
            {
                Order = order;
                _segment = segment;
                _keys = new List<FieldToken>(segment.Tokens.GetAllEntries());
                _keys.Sort();
                Next();
            }

            public void Next()
            {
                while (_pos < _keys.Count)
                {
                    var key = _keys[_pos++];
                    if (!_segment.Tokens.TryGet(key.Field, key.Token, out var offset)) continue;
                    using var packed = _segment.LoadPacked(offset);
                    var data = ApplyDeletes(packed.AsSpan(), _segment.Deletes);
                    if (data.Length == 0) continue; // entirely deleted
                    CurrentKey = key;
                    CurrentData = data;
                    return;
                }
                Finished = true;
                CurrentKey = default;
                CurrentData = null;
            }

            public void Dispose() { /* SegmentReader is owned externally */ }

            // Filter out entries belonging to deleted docs. Each ulong is one packed
            // group of up to 16 positions for one (docId, group) pair, so we either
            // keep the whole group (live doc) or drop it (deleted doc). This means
            // deleted doc IDs leave no trace in the merged segment.
            private static byte[] ApplyDeletes(Span<ulong> packed, RoaringBitmap deletes)
            {
                if (deletes.IsEmpty)
                {
                    var bytes = new byte[packed.Length * 8];
                    MemoryMarshal.Cast<ulong, byte>(packed).CopyTo(bytes);
                    return bytes;
                }

                int kept = 0;
                for (int i = 0; i < packed.Length; i++)
                {
                    uint docId = RoaringishPacked.UnpackDocId(packed[i]);
                    if (!deletes.Contains(docId)) kept++;
                }
                if (kept == 0) return Array.Empty<byte>();

                var outArr = new ulong[kept];
                int j = 0;
                for (int i = 0; i < packed.Length; i++)
                {
                    uint docId = RoaringishPacked.UnpackDocId(packed[i]);
                    if (!deletes.Contains(docId)) outArr[j++] = packed[i];
                }
                var bytes2 = new byte[outArr.Length * 8];
                Buffer.BlockCopy(outArr, 0, bytes2, 0, bytes2.Length);
                return bytes2;
            }
        }
    }
}
