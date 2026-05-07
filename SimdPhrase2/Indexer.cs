using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Buffers.Binary;
using SimdPhrase2.Db;
using SimdPhrase2.Roaringish;
using SimdPhrase2.Segments;
using SimdPhrase2.Storage;

namespace SimdPhrase2
{
    public class Indexer : IDisposable
    {
        private readonly string _indexName;
        private readonly int _batchSize;
        private int _currentBatchCount;
        private Dictionary<string, RoaringishPacked> _currentBatch;
        // List of staged batch files spilled to disk during this commit (only filled
        // when one commit produces more than _batchSize docs).
        private List<string> _stagedBatchFiles;
        private int _spillId;
        private DocumentStore _docStore;

        private CommonTokensConfig _commonTokensConfig;
        private HashSet<string> _commonTokens;
        private List<(string content, uint docId)> _firstBatchBuffer;
        private bool _isFirstBatch;
        private ITextTokenizer _tokenizer;
        private ISimdStorage _storage;

        // Segments
        private SegmentManifest _manifest;
        private TieredMergePolicy _mergePolicy;

        // Stats (global - aggregated across segments)
        private uint _totalDocs;
        private ulong _totalTokens;
        private Stream _docLengthsStream;
        private readonly object _lock = new object();

        // Pending deletes for this commit. Deletes are applied to existing segments
        // when Commit() is called (the docId is added to that segment's deletes
        // bitmap; deletes for docs added in this same commit are applied to the new
        // segment before it is sealed).
        private readonly HashSet<uint> _pendingDeletes = new();

        public Indexer(string indexName, CommonTokensConfig commonTokensConfig = null, int batchSize = 300_000, ITextTokenizer tokenizer = null, ISimdStorage storage = null, TieredMergePolicy mergePolicy = null)
        {
            _indexName = indexName;
            _batchSize = batchSize;
            _tokenizer = tokenizer ?? new BasicTokenizer();
            _storage = storage ?? new FileSystemStorage();
            _commonTokensConfig = commonTokensConfig ?? CommonTokensConfig.None;
            _currentBatch = new Dictionary<string, RoaringishPacked>();
            _currentBatchCount = 0;
            _stagedBatchFiles = new List<string>();
            _spillId = 0;
            _mergePolicy = mergePolicy ?? new TieredMergePolicy();

            _firstBatchBuffer = new List<(string, uint)>();
            _isFirstBatch = true;
            _commonTokens = new HashSet<string>();

            // Important: do NOT delete the index directory here. That breaks the
            // segmented model. New documents are appended as new segments. Callers
            // who need a clean index should remove the directory themselves.
            _storage.CreateDirectory(_indexName);
            _storage.CreateDirectory(_storage.Combine(_indexName, "segments"));

            _docStore = new DocumentStore(_indexName, _storage);
            _docLengthsStream = _storage.OpenReadWrite(_storage.Combine(_indexName, "doc_lengths.bin"));

            _manifest = SegmentManifest.Load(_storage, _indexName);
            var statsPath = _storage.Combine(_indexName, "index_stats.json");
            var existing = IndexStats.Load(_storage, statsPath);
            _totalDocs = existing.TotalDocs;
            _totalTokens = existing.TotalTokens;

            // If we already have content (from a previous run), the first batch
            // logic for common tokens should be skipped - the existing common token
            // dictionary applies.
            if (_manifest.Segments.Count > 0)
            {
                _isFirstBatch = false;
                _commonTokens = CommonTokensPersistence.Load(_storage, _storage.Combine(_indexName, "common_tokens.bin"));
            }
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
                    SpillBatch();
                }
            }
            else
            {
                IndexDocumentInternal(content, docId);
                _currentBatchCount++;
                if (_currentBatchCount >= _batchSize)
                {
                    SpillBatch();
                }
            }
        }

        // Mark a doc id as deleted. The actual deletion is recorded against existing
        // segments (or the in-progress segment) on the next Commit().
        public void Delete(uint docId)
        {
            _pendingDeletes.Add(docId);
        }

        private void IndexDocumentInternal(string content, uint docId)
        {
            var tokens = new List<(string token, uint index)>();

            var enumerator = _tokenizer.Tokenize(content.AsSpan()).GetEnumerator();
            int tokensCount = 0;
            uint lastIndex = uint.MaxValue;

            while (enumerator.MoveNext())
            {
                tokens.Add((enumerator.Current.ToString(), enumerator.CurrentIndex));
                if (enumerator.CurrentIndex != lastIndex)
                {
                    tokensCount++;
                }
                lastIndex = enumerator.CurrentIndex;
            }

            UpdateStats(docId, tokensCount);

            var docTokens = new Dictionary<string, List<uint>>();

            for (int i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];

                ref List<uint> list = ref CollectionsMarshal.GetValueRefOrAddDefault(docTokens, token.token, out var exists);
                if (!exists) list = new List<uint>();
                list.Add(token.index);

                if (_commonTokens.Count > 0)
                {
                    bool isFirstRare = !_commonTokens.Contains(token.token);
                    int maxWindow = 3;

                    string currentMerged = token.token;
                    for (int j = 1; j < maxWindow && (i + j) < tokens.Count; j++)
                    {
                        var nextToken = tokens[i + j];
                        if (nextToken.index == token.index) { maxWindow++; continue; }

                        bool isNextRare = !_commonTokens.Contains(nextToken.token);
                        if (isFirstRare && isNextRare) break;

                        currentMerged += " " + nextToken;

                        list = ref CollectionsMarshal.GetValueRefOrAddDefault(docTokens, currentMerged, out exists);
                        if (!exists) list = new List<uint>();
                        list.Add(token.index);

                        if (isNextRare) break;
                    }
                }
            }

            foreach (var (token, positions) in docTokens)
            {
                if (!_currentBatch.TryGetValue(token, out var packed))
                {
                    packed = new RoaringishPacked();
                    _currentBatch[token] = packed;
                }
                packed.Push(docId, positions);
            }
        }

        private void UpdateStats(uint docId, int docLen)
        {
            lock (_lock)
            {
                _totalDocs++;
                _totalTokens += (ulong)docLen;

                long pos = (long)docId * 4;
                if (pos != _docLengthsStream.Position)
                {
                    _docLengthsStream.Seek(pos, SeekOrigin.Begin);
                }
                Span<byte> buffer = stackalloc byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(buffer, docLen);
                _docLengthsStream.Write(buffer);
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
                        string token = tokenSpan.ToString();
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
                        string token = tokenSpan.ToString();
                        freq[token] = freq.GetValueOrDefault(token, 0) + 1;
                    }
                }
                int count = (int)(freq.Count * percentageConfig.Percentage);
                var top = freq.OrderByDescending(kvp => kvp.Value).Take(count).Select(kvp => kvp.Key);
                _commonTokens = new HashSet<string>(top);
            }

            if (_commonTokens.Count > 0)
            {
                CommonTokensPersistence.Save(_storage, _storage.Combine(_indexName, "common_tokens.bin"), _commonTokens);
            }
        }

        // Spill current in-memory batch to a temporary file in the index root. Used
        // when the in-memory dictionary grows past _batchSize during a single commit.
        private void SpillBatch()
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

            string tempFile = _storage.Combine(_indexName, $"batch_spill_{_spillId}.bin");
            using (var fs = _storage.OpenWrite(tempFile))
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

            foreach (var p in _currentBatch.Values) p.Dispose();
            _currentBatch.Clear();
            _stagedBatchFiles.Add(tempFile);
            _spillId++;
        }

        public void Commit()
        {
            // Commit produces 0 or 1 new segment from this commit's writes, then
            // applies pending deletes against the existing segments, then runs the
            // merge policy.
            FlushBatchToSegment();
            ApplyPendingDeletes();
            RunAutoMerge();
            PersistGlobalState();
        }

        private void FlushBatchToSegment()
        {
            // First-batch logic: if this is still the first batch, materialize it now.
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

            bool hasInMemory = _currentBatch.Count > 0;
            bool hasStaged = _stagedBatchFiles.Count > 0;
            if (!hasInMemory && !hasStaged) return;

            string segId = _manifest.AllocateSegmentId();
            int docsInSegment = _currentBatchCount;

            var info = SegmentWriter.Write(_storage, _indexName, segId, _currentBatch, _stagedBatchFiles);
            info.Id = segId;
            info.DocCount = docsInSegment;

            _manifest.Segments.Add(info);

            // Cleanup spill files and in-memory state.
            foreach (var p in _currentBatch.Values) p.Dispose();
            _currentBatch.Clear();
            foreach (var f in _stagedBatchFiles) _storage.DeleteFile(f);
            _stagedBatchFiles.Clear();
            _spillId = 0;
            _currentBatchCount = 0;
        }

        private void ApplyPendingDeletes()
        {
            if (_pendingDeletes.Count == 0) return;

            // For every segment, OR only the deletes that actually correspond to docs
            // in that segment (cheap probe via the per-segment LiveDocIds bitmap).
            // This keeps DeleteCount accurate so the merge policy doesn't fire on
            // phantom deletes.
            foreach (var seg in _manifest.Segments)
            {
                using var sr = new SegmentReader(_storage, _indexName, seg);
                var bm = sr.Deletes;
                int newDeletes = 0;
                foreach (var d in _pendingDeletes)
                {
                    if (!sr.LiveDocIds.Contains(d)) continue;
                    if (bm.Add(d)) newDeletes++;
                }
                if (newDeletes == 0) continue;
                string path = _storage.Combine(SegmentManifest.SegmentDirectory(_storage, _indexName, seg.Id), "deletes.bin");
                using var s = _storage.OpenWrite(path);
                bm.Save(s);
                seg.DeleteCount += newDeletes;
            }
            _pendingDeletes.Clear();
        }

        private void RunAutoMerge()
        {
            // Cascade merges: keep merging while the policy finds work.
            for (int safety = 0; safety < 100; safety++)
            {
                var todo = _mergePolicy.FindMerge(_manifest.Segments);
                if (todo == null) break;
                if (todo.Count == 1)
                {
                    // Singleton compaction - just rewrite to drop deletes.
                    MergeSegments(todo);
                }
                else
                {
                    MergeSegments(todo);
                }
            }
        }

        // Force-merge to a single segment.
        public void ForceMerge()
        {
            // First, flush any staged docs.
            if (_currentBatch.Count > 0 || _stagedBatchFiles.Count > 0 || _isFirstBatch && _firstBatchBuffer.Count > 0 || _pendingDeletes.Count > 0)
            {
                Commit();
            }
            var todo = _mergePolicy.FindForceMerge(_manifest.Segments);
            if (todo == null) return;
            MergeSegments(todo);
            PersistGlobalState();
        }

        private void MergeSegments(List<SegmentInfo> sources)
        {
            string newId = _manifest.AllocateSegmentId();
            var sourceReaders = new List<SegmentReader>(sources.Count);
            try
            {
                foreach (var s in sources)
                {
                    sourceReaders.Add(new SegmentReader(_storage, _indexName, s));
                }
                var newInfo = SegmentWriter.Merge(_storage, _indexName, newId, sourceReaders);
                newInfo.Id = newId;
                // DocCount is set by SegmentWriter from the actual emitted unique doc ids.
                newInfo.DeleteCount = 0;
                newInfo.MergedSegment = true;

                // Insert the new segment, remove the old ones.
                foreach (var s in sources) _manifest.Segments.Remove(s);
                _manifest.Segments.Add(newInfo);
            }
            finally
            {
                foreach (var sr in sourceReaders) sr.Dispose();
            }

            // Delete files of merged-away segments.
            foreach (var s in sources)
            {
                var dir = SegmentManifest.SegmentDirectory(_storage, _indexName, s.Id);
                _storage.DeleteFile(_storage.Combine(dir, "roaringish_packed.bin"));
                _storage.DeleteFile(_storage.Combine(dir, "token_map.bin"));
                _storage.DeleteFile(_storage.Combine(dir, "deletes.bin"));
                _storage.DeleteFile(_storage.Combine(dir, "doc_ids.bin"));
                _storage.DeleteDirectory(dir);
            }
        }

        private void PersistGlobalState()
        {
            // Save manifest
            _manifest.Save(_storage, _indexName);
            // Save aggregated stats - reflect live docs.
            // _totalDocs and _totalTokens are tracked in-memory; we persist them.
            var stats = new IndexStats
            {
                TotalDocs = _totalDocs,
                TotalTokens = _totalTokens
            };
            IndexStats.Save(_storage, _storage.Combine(_indexName, "index_stats.json"), stats);
        }

        public void Dispose()
        {
            foreach (var p in _currentBatch.Values) p.Dispose();
            _docStore.Dispose();
            _docLengthsStream?.Dispose();
        }
    }
}
