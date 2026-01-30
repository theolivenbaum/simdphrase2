using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Buffers.Binary;
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

        private CommonTokensConfig _commonTokensConfig;
        private HashSet<string> _commonTokens;
        private List<(string content, uint docId)> _firstBatchBuffer;
        private bool _isFirstBatch;
        private ITextTokenizer _tokenizer;

        // Stats
        private uint _totalDocs;
        private ulong _totalTokens;
        private FileStream _docLengthsStream;
        private readonly object _lock = new object();

        public Indexer(string indexName, CommonTokensConfig commonTokensConfig = null, int batchSize = 300_000, ITextTokenizer tokenizer = null)
        {
            _indexName = indexName;
            _batchSize = batchSize;
            _tokenizer = tokenizer ?? new BasicTokenizer();
            _commonTokensConfig = commonTokensConfig ?? CommonTokensConfig.None;
            _currentBatch = new Dictionary<string, RoaringishPacked>();
            _currentBatchCount = 0;
            _batchId = 0;

            _firstBatchBuffer = new List<(string, uint)>();
            _isFirstBatch = true;
            _commonTokens = new HashSet<string>();

            if (Directory.Exists(_indexName)) Directory.Delete(_indexName, true);
            Directory.CreateDirectory(_indexName);

            _docStore = new DocumentStore(_indexName);
            _docLengthsStream = new FileStream(Path.Combine(_indexName, "doc_lengths.bin"), FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            _totalDocs = 0;
            _totalTokens = 0;
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

            if (_isFirstBatch)
            {
                _firstBatchBuffer.Add((content, docId));
                _currentBatchCount++;

                if (_currentBatchCount >= _batchSize)
                {
                    FlushBatch();
                }
            }
            else
            {
                IndexDocumentInternal(content, docId);
                _currentBatchCount++;
                if (_currentBatchCount >= _batchSize)
                {
                    FlushBatch();
                }
            }
        }

        private void IndexDocumentInternal(string content, uint docId)
        {
            // Tokenizer returns spans; we handle normalization (lowercasing) here efficiently
            var tokens = new List<string>();
            foreach(var t in _tokenizer.Tokenize(content.AsSpan()))
            {
                tokens.Add(TokenUtils.NormalizeToString(t));
            }

            // Update stats
            int docLen = tokens.Count;
            lock (_lock)
            {
                _totalDocs++; // This assumes we add unique documents.
                _totalTokens += (ulong)docLen;

                // Write doc length (random access to support out-of-order if needed, though usually sequential)
                long pos = (long)docId * 4;
                if (pos != _docLengthsStream.Position)
                {
                    _docLengthsStream.Seek(pos, SeekOrigin.Begin);
                }
                Span<byte> buffer = stackalloc byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(buffer, docLen);
                _docLengthsStream.Write(buffer);
            }

            var docTokens = new Dictionary<string, List<uint>>();

            for (int i = 0; i < tokens.Count; i++)
            {
                string t = tokens[i];
                if (!docTokens.TryGetValue(t, out var list))
                {
                    list = new List<uint>();
                    docTokens[t] = list;
                }
                list.Add((uint)i);

                if (_commonTokens.Count > 0)
                {
                    bool isFirstRare = !_commonTokens.Contains(t);
                    int maxWindow = 3;

                    string currentMerged = t;

                    for (int j = 1; j < maxWindow && (i + j) < tokens.Count; j++)
                    {
                        string nextToken = tokens[i+j];
                        bool isNextRare = !_commonTokens.Contains(nextToken);

                        if (isFirstRare && isNextRare) break;

                        currentMerged += " " + nextToken;

                        if (!docTokens.TryGetValue(currentMerged, out list))
                        {
                            list = new List<uint>();
                            docTokens[currentMerged] = list;
                        }
                        list.Add((uint)i);

                        if (isNextRare) break;
                    }
                }
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
        }

        private void GenerateCommonTokens()
        {
            if (_commonTokensConfig is CommonTokensConfig.ListConfig listConfig)
            {
                _commonTokens = listConfig.Tokens;
            }
            else if (_commonTokensConfig is CommonTokensConfig.FixedNumConfig fixedNumConfig)
            {
                var freq = new Dictionary<string, int>();
                foreach (var (content, _) in _firstBatchBuffer)
                {
                    foreach (var tokenSpan in _tokenizer.Tokenize(content.AsSpan()))
                    {
                        string token = TokenUtils.NormalizeToString(tokenSpan);
                        freq[token] = freq.GetValueOrDefault(token, 0) + 1;
                    }
                }
                var top = freq.OrderByDescending(kvp => kvp.Value).Take(fixedNumConfig.Num).Select(kvp => kvp.Key);
                _commonTokens = new HashSet<string>(top);
            }
             else if (_commonTokensConfig is CommonTokensConfig.PercentageConfig percentageConfig)
            {
                 var freq = new Dictionary<string, int>();
                foreach (var (content, _) in _firstBatchBuffer)
                {
                    foreach (var tokenSpan in _tokenizer.Tokenize(content.AsSpan()))
                    {
                        string token = TokenUtils.NormalizeToString(tokenSpan);
                        freq[token] = freq.GetValueOrDefault(token, 0) + 1;
                    }
                }
                int count = (int)(freq.Count * percentageConfig.Percentage);
                var top = freq.OrderByDescending(kvp => kvp.Value).Take(count).Select(kvp => kvp.Key);
                _commonTokens = new HashSet<string>(top);
            }

            if (_commonTokens.Count > 0)
            {
                CommonTokensPersistence.Save(Path.Combine(_indexName, "common_tokens.bin"), _commonTokens);
            }
        }

        private void FlushBatch()
        {
            if (_isFirstBatch)
            {
                GenerateCommonTokens();
                foreach (var (content, docId) in _firstBatchBuffer)
                {
                    IndexDocumentInternal(content, docId);
                }
                _firstBatchBuffer.Clear();
                _isFirstBatch = false;
            }

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

            // Save Stats
            var stats = new IndexStats
            {
                TotalDocs = _totalDocs,
                TotalTokens = _totalTokens
            };
            IndexStats.Save(Path.Combine(_indexName, "index_stats.json"), stats);
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
                int docCount = 0;
                uint lastDocId = uint.MaxValue; // sentinel

                // Process first segment
                {
                    var data = reader.CurrentData;
                    packedFile.Write(data);
                    totalLength += data.Length;

                    // Count unique docs
                    CountDocsInPacked(data, ref lastDocId, ref docCount);
                }

                reader.Next();
                if (!reader.Finished)
                    pq.Enqueue(reader, (reader.CurrentToken, reader.BatchIndex));

                // Process subsequent segments for same token
                while (pq.Count > 0 && pq.Peek().CurrentToken == token)
                {
                    var nextReader = pq.Dequeue();
                    var data = nextReader.CurrentData;

                    packedFile.Write(data);
                    totalLength += data.Length;

                    // Count unique docs
                    CountDocsInPacked(data, ref lastDocId, ref docCount);

                    nextReader.Next();
                    if (!nextReader.Finished)
                        pq.Enqueue(nextReader, (nextReader.CurrentToken, nextReader.BatchIndex));
                }

                tokenStore.Add(token, startOffset, totalLength, docCount);
            }

            foreach (var r in readers) r.Dispose();

            for (int i = 0; i < _batchId; i++)
            {
                File.Delete(Path.Combine(_indexName, $"batch_{i}.bin"));
            }
        }

        private void CountDocsInPacked(byte[] data, ref uint lastDocId, ref int docCount)
        {
             var span = MemoryMarshal.Cast<byte, ulong>(data);
             for(int i=0; i<span.Length; i++)
             {
                 uint docId = RoaringishPacked.UnpackDocId(span[i]);
                 if (docId != lastDocId)
                 {
                     docCount++;
                     lastDocId = docId;
                 }
             }
        }

        public void Dispose()
        {
            foreach(var p in _currentBatch.Values) p.Dispose();
            _docStore.Dispose();
            _docLengthsStream?.Dispose();
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
