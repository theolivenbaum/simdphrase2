using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SimdPhrase2.Db;
using SimdPhrase2.Roaringish;

namespace SimdPhrase2
{
    public class Indexer : IDisposable
    {
        private readonly string _indexName;
        private readonly int _batchSize;
        private int _currentBatchCount;
        private Dictionary<string, RoaringishPacked> _currentBatch;
        private int _batchId;
        private DocumentStore _docStore;

        public Indexer(string indexName, int batchSize = 300_000)
        {
            _indexName = indexName;
            _batchSize = batchSize;
            _currentBatch = new Dictionary<string, RoaringishPacked>();
            _currentBatchCount = 0;
            _batchId = 0;

            if (Directory.Exists(_indexName)) Directory.Delete(_indexName, true);
            Directory.CreateDirectory(_indexName);

            _docStore = new DocumentStore(_indexName);
        }

        public void Index(IEnumerable<(string content, uint docId)> docs)
        {
            foreach (var (content, docId) in docs)
            {
                AddDocument(content, docId);
            }
            Commit();
        }

        public void AddDocument(string content, uint docId)
        {
            _docStore.AddDocument(docId, content);

            string normalized = Utils.Normalize(content);

            var docTokens = new Dictionary<string, List<uint>>();

            uint pos = 0;
            foreach (var token in Utils.Tokenize(normalized))
            {
                if (!docTokens.TryGetValue(token, out var list))
                {
                    list = new List<uint>();
                    docTokens[token] = list;
                }
                list.Add(pos);
                pos++;
            }

            foreach (var kvp in docTokens)
            {
                string token = kvp.Key;
                List<uint> positions = kvp.Value;

                if (!_currentBatch.TryGetValue(token, out var packed))
                {
                    packed = new RoaringishPacked();
                    _currentBatch[token] = packed;
                }
                packed.Push(docId, positions);
            }

            _currentBatchCount++;
            if (_currentBatchCount >= _batchSize)
            {
                FlushBatch();
            }
        }

        private void FlushBatch()
        {
            if (_currentBatch.Count == 0) return;

            Console.WriteLine($"Flushing batch {_batchId} with {_currentBatchCount} docs and {_currentBatch.Count} unique tokens.");

            string tempFile = Path.Combine(_indexName, $"batch_{_batchId}.bin");
            using (var fs = new FileStream(tempFile, FileMode.Create))
            using (var writer = new BinaryWriter(fs))
            {
                var sortedTokens = _currentBatch.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

                foreach (var token in sortedTokens)
                {
                    var packed = _currentBatch[token];
                    writer.Write(token);

                    var span = packed.AsSpan();
                    writer.Write(packed.Length);

                    byte[] bytes = new byte[packed.Length * 8];
                    MemoryMarshal.Cast<ulong, byte>(span).CopyTo(bytes);
                    writer.Write(bytes);
                }
            }

            foreach(var p in _currentBatch.Values) p.Dispose();
            _currentBatch.Clear();
            _currentBatchCount = 0;
            _batchId++;
        }

        public void Commit()
        {
            FlushBatch();
            MergeBatches();
        }

        private void MergeBatches()
        {
            if (_batchId == 0) return;

            Console.WriteLine("Merging batches...");

            var readers = new List<BatchReader>();
            for (int i = 0; i < _batchId; i++)
            {
                string path = Path.Combine(_indexName, $"batch_{i}.bin");
                readers.Add(new BatchReader(path, i));
            }

            var pq = new PriorityQueue<BatchReader, (string, int)>(Comparer<(string, int)>.Create((a, b) => {
                int cmp = string.CompareOrdinal(a.Item1, b.Item1);
                if (cmp != 0) return cmp;
                return a.Item2.CompareTo(b.Item2);
            }));

            foreach (var r in readers)
            {
                if (!r.Finished)
                    pq.Enqueue(r, (r.CurrentToken, r.BatchIndex));
            }

            using var tokenStore = new TokenStore(_indexName);
            using var packedFile = new FileStream(Path.Combine(_indexName, "roaringish_packed.bin"), FileMode.Create);

            while (pq.Count > 0)
            {
                var reader = pq.Dequeue();
                string token = reader.CurrentToken;

                // Align file position to 64 bytes
                long currentPos = packedFile.Position;
                long alignedPos = (currentPos + 63) & ~63;
                if (alignedPos > currentPos)
                {
                    packedFile.Write(new byte[alignedPos - currentPos]);
                }

                long startOffset = packedFile.Position;
                long totalLength = 0;

                // Process first segment
                packedFile.Write(reader.CurrentData);
                totalLength += reader.CurrentData.Length;

                reader.Next();
                if (!reader.Finished)
                    pq.Enqueue(reader, (reader.CurrentToken, reader.BatchIndex));

                // Process subsequent segments for same token
                while (pq.Count > 0 && pq.Peek().CurrentToken == token)
                {
                    var nextReader = pq.Dequeue();
                    packedFile.Write(nextReader.CurrentData);
                    totalLength += nextReader.CurrentData.Length;

                    nextReader.Next();
                    if (!nextReader.Finished)
                        pq.Enqueue(nextReader, (nextReader.CurrentToken, nextReader.BatchIndex));
                }

                tokenStore.Add(token, startOffset, totalLength);
            }

            foreach (var r in readers) r.Dispose();

            for (int i = 0; i < _batchId; i++)
            {
                File.Delete(Path.Combine(_indexName, $"batch_{i}.bin"));
            }
        }

        public void Dispose()
        {
            foreach(var p in _currentBatch.Values) p.Dispose();
            _docStore.Dispose();
        }

        private class BatchReader : IDisposable
        {
            private FileStream _fs;
            private BinaryReader _br;
            public string CurrentToken;
            public byte[] CurrentData;
            public bool Finished;
            public int BatchIndex;

            public BatchReader(string path, int index)
            {
                _fs = File.OpenRead(path);
                _br = new BinaryReader(_fs);
                BatchIndex = index;
                Next();
            }

            public void Next()
            {
                if (_fs.Position >= _fs.Length)
                {
                    Finished = true;
                    CurrentToken = null;
                    CurrentData = null;
                    return;
                }
                try
                {
                    CurrentToken = _br.ReadString();
                    int len = _br.ReadInt32(); // number of ulongs
                    CurrentData = _br.ReadBytes(len * 8);
                }
                catch (EndOfStreamException)
                {
                    Finished = true;
                }
            }

            public void Dispose()
            {
                _br.Dispose();
                _fs.Dispose();
            }
        }
    }
}
