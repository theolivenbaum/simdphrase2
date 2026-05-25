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
        // In-memory token batch keyed by (field, token). All fields share a single
        // packed file per segment - per-field separation is purely at the key level.
        private Dictionary<FieldToken, RoaringishPacked> _currentBatch;
        // List of staged batch files spilled to disk during this commit (only filled
        // when one commit produces more than _batchSize docs).
        private List<string> _stagedBatchFiles;
        private int _spillId;
        private DocumentStore _docStore;

        private CommonTokensConfig _commonTokensConfig;
        private HashSet<string> _commonTokens;
        // First-batch buffer holds per-field contents so common-token detection can
        // see the full document text.
        private List<(string[] fieldContents, uint docId)> _firstBatchBuffer;
        private bool _isFirstBatch;
        private ITextTokenizer _tokenizer;
        private ISimdStorage _storage;

        // Segments
        private SegmentManifest _manifest;
        private TieredMergePolicy _mergePolicy;

        // Stats (global - aggregated across segments)
        private uint _totalDocs;
        private ulong _totalTokens;            // sum across fields
        private ulong[] _totalTokensPerField;
        private readonly int _fieldCount;
        private Stream _docLengthsStream;
        private readonly object _lock = new object();

        // Pending deletes for this commit. Deletes are applied to existing segments
        // when Commit() is called (the docId is added to that segment's deletes
        // bitmap; deletes for docs added in this same commit are applied to the new
        // segment before it is sealed).
        private readonly HashSet<uint> _pendingDeletes = new();

        public int FieldCount => _fieldCount;

        public Indexer(string indexName, CommonTokensConfig commonTokensConfig = null, int batchSize = 300_000, ITextTokenizer tokenizer = null, ISimdStorage storage = null, TieredMergePolicy mergePolicy = null, int fieldCount = 1)
        {
            if (fieldCount < 1 || fieldCount > 256)
                throw new ArgumentOutOfRangeException(nameof(fieldCount), "fieldCount must be between 1 and 256.");

            _indexName = indexName;
            _batchSize = batchSize;
            _tokenizer = tokenizer ?? new BasicTokenizer();
            _storage = storage ?? new FileSystemStorage();
            _commonTokensConfig = commonTokensConfig ?? CommonTokensConfig.None;
            _currentBatch = new Dictionary<FieldToken, RoaringishPacked>();
            _currentBatchCount = 0;
            _stagedBatchFiles = new List<string>();
            _spillId = 0;
            _mergePolicy = mergePolicy ?? new TieredMergePolicy();
            _fieldCount = fieldCount;
            _totalTokensPerField = new ulong[fieldCount];

            _firstBatchBuffer = new List<(string[], uint)>();
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

                // If the existing index already records a field count, refuse to
                // change it on reopen - the on-disk layout is fixed.
                if (existing.FieldCount > 0 && existing.FieldCount != fieldCount)
                {
                    throw new InvalidOperationException($"Existing index has fieldCount={existing.FieldCount}, but Indexer was opened with fieldCount={fieldCount}.");
                }
                if (existing.TotalTokensPerField != null && existing.TotalTokensPerField.Length == fieldCount)
                {
                    Array.Copy(existing.TotalTokensPerField, _totalTokensPerField, fieldCount);
                }
            }
        }

        // Legacy single-field indexing: each tuple becomes a single-field document
        // indexed into field 0.
        public void Index(IEnumerable<(string content, uint docId)> docs)
        {
            foreach (var (content, docId) in docs)
            {
                AddDocument(content, docId);
            }
            Commit();
        }

        // Multi-field indexing: caller provides one content per field for each
        // document. Field index is the array index (0..fieldCount-1).
        public void Index(IEnumerable<(string[] fieldContents, uint docId)> docs)
        {
            foreach (var (fieldContents, docId) in docs)
            {
                AddDocument(docId, fieldContents);
            }
            Commit();
        }

        // Legacy single-field add: maps to field 0 only. Valid for any fieldCount,
        // but if fieldCount > 1 the other fields receive empty content.
        public void AddDocument(string content, uint docId)
        {
            var fields = new string[_fieldCount];
            fields[0] = content ?? string.Empty;
            for (int i = 1; i < _fieldCount; i++) fields[i] = string.Empty;
            AddDocument(docId, fields);
        }

        // Primary multi-field entry point. The fieldContents array length must
        // equal the indexer's fieldCount.
        public void AddDocument(uint docId, params string[] fieldContents)
        {
            if (fieldContents == null) throw new ArgumentNullException(nameof(fieldContents));
            if (fieldContents.Length != _fieldCount)
                throw new ArgumentException($"Expected {_fieldCount} field contents, got {fieldContents.Length}.", nameof(fieldContents));

            _docStore.AddDocument(docId, JoinForDocStore(fieldContents));

            if (_isFirstBatch)
            {
                _firstBatchBuffer.Add(((string[])fieldContents.Clone(), docId));
                _currentBatchCount++;

                if (_currentBatchCount >= _batchSize)
                {
                    SpillBatch();
                }
            }
            else
            {
                IndexDocumentInternal(fieldContents, docId);
                _currentBatchCount++;
                if (_currentBatchCount >= _batchSize)
                {
                    SpillBatch();
                }
            }
        }

        // For the document store we keep the original behavior of storing one blob
        // per doc. With multiple fields we join with a record separator so callers
        // can still round-trip via GetDocument(); they get all fields back.
        private static string JoinForDocStore(string[] fieldContents)
        {
            if (fieldContents.Length == 1) return fieldContents[0] ?? string.Empty;
            return string.Join("", fieldContents);
        }

        // Mark a doc id as deleted. The actual deletion is recorded against existing
        // segments (or the in-progress segment) on the next Commit().
        public void Delete(uint docId)
        {
            _pendingDeletes.Add(docId);
        }

        private void IndexDocumentInternal(string[] fieldContents, uint docId)
        {
            var perFieldLengths = new int[_fieldCount];

            for (int f = 0; f < _fieldCount; f++)
            {
                string content = fieldContents[f] ?? string.Empty;
                IndexFieldInternal((byte)f, content, docId, ref perFieldLengths[f]);
            }

            UpdateStats(docId, perFieldLengths);
        }

        private void IndexFieldInternal(byte field, string content, uint docId, ref int tokensCount)
        {
            var tokens = new List<(string token, uint index)>();

            var enumerator = _tokenizer.Tokenize(content.AsSpan()).GetEnumerator();
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

                        currentMerged += " " + nextToken.token;

                        list = ref CollectionsMarshal.GetValueRefOrAddDefault(docTokens, currentMerged, out exists);
                        if (!exists) list = new List<uint>();
                        list.Add(token.index);

                        if (isNextRare) break;
                    }
                }
            }

            foreach (var (token, positions) in docTokens)
            {
                var key = new FieldToken(field, token);
                if (!_currentBatch.TryGetValue(key, out var packed))
                {
                    packed = new RoaringishPacked();
                    _currentBatch[key] = packed;
                }
                packed.Push(docId, positions);
            }
        }

        private void UpdateStats(uint docId, int[] perFieldLengths)
        {
            int totalLen = 0;
            for (int i = 0; i < perFieldLengths.Length; i++) totalLen += perFieldLengths[i];

            lock (_lock)
            {
                _totalDocs++;
                _totalTokens += (ulong)totalLen;
                for (int i = 0; i < perFieldLengths.Length; i++)
                {
                    _totalTokensPerField[i] += (ulong)perFieldLengths[i];
                }

                // doc_lengths.bin: contiguous slot of fieldCount * 4 bytes per doc id.
                long pos = (long)docId * 4L * _fieldCount;
                if (pos != _docLengthsStream.Position)
                {
                    _docLengthsStream.Seek(pos, SeekOrigin.Begin);
                }
                Span<byte> buffer = stackalloc byte[64];
                int bytes = 4 * _fieldCount;
                if (bytes > buffer.Length) buffer = new byte[bytes];
                for (int i = 0; i < _fieldCount; i++)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(i * 4, 4), perFieldLengths[i]);
                }
                _docLengthsStream.Write(buffer.Slice(0, bytes));
            }
        }

        private void GenerateCommonTokens()
        {
            // Common tokens are computed over the union of all field contents in
            // the first batch. They are then used (globally) to expand multi-token
            // phrases at indexing time, regardless of which field the phrase comes
            // from.
            if (_commonTokensConfig is CommonTokensConfig.ListConfig listConfig)
            {
                _commonTokens = listConfig.Tokens;
            }
            else if (_commonTokensConfig is CommonTokensConfig.FixedNumConfig fixedNumConfig)
            {
                var freq = ComputeFirstBatchFrequencies();
                var top = freq.OrderByDescending(kvp => kvp.Value).Take(fixedNumConfig.Num).Select(kvp => kvp.Key);
                _commonTokens = new HashSet<string>(top);
            }
            else if (_commonTokensConfig is CommonTokensConfig.PercentageConfig percentageConfig)
            {
                var freq = ComputeFirstBatchFrequencies();
                int count = (int)(freq.Count * percentageConfig.Percentage);
                var top = freq.OrderByDescending(kvp => kvp.Value).Take(count).Select(kvp => kvp.Key);
                _commonTokens = new HashSet<string>(top);
            }

            if (_commonTokens.Count > 0)
            {
                CommonTokensPersistence.Save(_storage, _storage.Combine(_indexName, "common_tokens.bin"), _commonTokens);
            }
        }

        private Dictionary<string, int> ComputeFirstBatchFrequencies()
        {
            var freq = new Dictionary<string, int>();
            foreach (var (fieldContents, _) in _firstBatchBuffer)
            {
                foreach (var content in fieldContents)
                {
                    if (string.IsNullOrEmpty(content)) continue;
                    foreach (var tokenSpan in _tokenizer.Tokenize(content.AsSpan()))
                    {
                        string token = tokenSpan.ToString();
                        freq[token] = freq.GetValueOrDefault(token, 0) + 1;
                    }
                }
            }
            return freq;
        }

        // Spill current in-memory batch to a temporary file in the index root. Used
        // when the in-memory dictionary grows past _batchSize during a single commit.
        private void SpillBatch()
        {
            if (_isFirstBatch)
            {
                GenerateCommonTokens();
                foreach (var (fields, docId) in _firstBatchBuffer)
                {
                    IndexDocumentInternal(fields, docId);
                }
                _firstBatchBuffer.Clear();
                _isFirstBatch = false;
            }

            if (_currentBatch.Count == 0) return;

            string tempFile = _storage.Combine(_indexName, $"batch_spill_{_spillId}.bin");
            using (var fs = _storage.OpenWrite(tempFile))
            using (var writer = new BinaryWriter(fs))
            {
                var sortedKeys = _currentBatch.Keys.ToList();
                sortedKeys.Sort();
                foreach (var key in sortedKeys)
                {
                    var packed = _currentBatch[key];
                    writer.Write(key.Field);
                    writer.Write(key.Token);
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
                foreach (var (fields, docId) in _firstBatchBuffer)
                {
                    IndexDocumentInternal(fields, docId);
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
            var stats = new IndexStats
            {
                TotalDocs = _totalDocs,
                TotalTokens = _totalTokens,
                FieldCount = _fieldCount,
                TotalTokensPerField = (ulong[])_totalTokensPerField.Clone(),
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
