using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using RocksDbSharp;
using SimdPhrase2.Db;
using SimdPhrase2.Roaringish;
using SimdPhrase2.Segments;
using SimdPhrase2.Storage;

namespace SimdPhrase2
{
    public class Indexer : IDisposable
    {
        private readonly string _indexPath;
        private readonly int _batchSize;
        private int _currentBatchCount;
        // In-memory token batch keyed by (field, token). All fields share a single
        // segment posting set per commit - per-field separation is purely at the
        // key level.
        private Dictionary<FieldToken, RoaringishPacked> _currentBatch;
        private DocLengthStore _docLengthStore;

        private CommonTokensConfig _commonTokensConfig;
        private HashSet<string> _commonTokens;
        // First-batch buffer holds per-field contents so common-token detection can
        // see the full document text.
        private List<(string[] fieldContents, uint docId)> _firstBatchBuffer;
        private bool _isFirstBatch;
        private ITextTokenizer _tokenizer;

        // RocksDB handle - this Indexer owns it unless the caller passed one in.
        private readonly SimdPhraseDb _db;
        private readonly bool _ownsDb;

        // Segments
        private SegmentManifest _manifest;
        private TieredMergePolicy _mergePolicy;

        // Stats (global - aggregated across segments)
        private uint _totalDocs;
        private ulong _totalTokens;
        private ulong[] _totalTokensPerField;
        private readonly int _fieldCount;
        private readonly object _lock = new object();

        // Pending deletes for this commit. Deletes are applied to existing segments
        // when Commit() is called.
        private readonly HashSet<uint> _pendingDeletes = new();

        // Buffered doc-length writes accumulated since last commit. Written into
        // the same WriteBatch as the new segment so a crash mid-commit never leaves
        // length-without-segment (or vice versa).
        private readonly List<(uint docId, int[] lengths)> _pendingDocLengths = new();

        // Buffered raw document bytes accumulated since last commit. Written
        // atomically with the new segment in Commit().
        private readonly List<(uint docId, byte[] bytes)> _pendingDocs = new();

        public int FieldCount => _fieldCount;
        public SimdPhraseDb Db => _db;

        public Indexer(string indexPath, CommonTokensConfig commonTokensConfig = null, int batchSize = 300_000, ITextTokenizer tokenizer = null, TieredMergePolicy mergePolicy = null, int fieldCount = 1, SimdPhraseDb db = null)
        {
            if (fieldCount < 1 || fieldCount > 256)
                throw new ArgumentOutOfRangeException(nameof(fieldCount), "fieldCount must be between 1 and 256.");

            _indexPath = indexPath;
            _batchSize = batchSize;
            _tokenizer = tokenizer ?? new BasicTokenizer();
            _commonTokensConfig = commonTokensConfig ?? CommonTokensConfig.None;
            _currentBatch = new Dictionary<FieldToken, RoaringishPacked>();
            _currentBatchCount = 0;
            _mergePolicy = mergePolicy ?? new TieredMergePolicy();
            _fieldCount = fieldCount;
            _totalTokensPerField = new ulong[fieldCount];

            _firstBatchBuffer = new List<(string[], uint)>();
            _isFirstBatch = true;
            _commonTokens = new HashSet<string>();

            if (db != null)
            {
                _db = db;
                _ownsDb = false;
            }
            else
            {
                _db = SimdPhraseDb.Open(indexPath);
                _ownsDb = true;
            }

            _docLengthStore = new DocLengthStore(_db, fieldCount);

            _manifest = SegmentManifest.Load(_db);
            var existing = IndexStats.Load(_db);
            _totalDocs = existing.TotalDocs;
            _totalTokens = existing.TotalTokens;

            // If we already have content (from a previous run), the first batch
            // logic for common tokens should be skipped - the existing common token
            // dictionary applies.
            if (_manifest.Segments.Count > 0)
            {
                _isFirstBatch = false;
                _commonTokens = CommonTokensPersistence.Load(_db);

                // If the existing index already records a field count, refuse to
                // change it on reopen - the on-disk layout is fixed.
                if (existing.FieldCount > 0 && existing.FieldCount != fieldCount)
                {
                    if (_ownsDb) _db.Dispose();
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

        // Legacy single-field add: maps to field 0 only.
        public void AddDocument(string content, uint docId)
        {
            var fields = new string[_fieldCount];
            fields[0] = content ?? string.Empty;
            for (int i = 1; i < _fieldCount; i++) fields[i] = string.Empty;
            AddDocument(docId, fields);
        }

        // Primary multi-field entry point.
        public void AddDocument(uint docId, params string[] fieldContents)
        {
            if (fieldContents == null) throw new ArgumentNullException(nameof(fieldContents));
            if (fieldContents.Length != _fieldCount)
                throw new ArgumentException($"Expected {_fieldCount} field contents, got {fieldContents.Length}.", nameof(fieldContents));

            // Document content, posting lists and doc lengths all get flushed
            // together in Commit()'s WriteBatch - a crash mid-commit leaves none of
            // them visible (vs the original on-disk layout where docs could be
            // written ahead of the segment).
            var joined = JoinForDocStore(fieldContents);
            _pendingDocs.Add((docId, System.Text.Encoding.UTF8.GetBytes(joined)));

            if (_isFirstBatch)
            {
                _firstBatchBuffer.Add(((string[])fieldContents.Clone(), docId));
                _currentBatchCount++;

                if (_currentBatchCount >= _batchSize)
                {
                    AutoCommit();
                }
            }
            else
            {
                IndexDocumentInternal(fieldContents, docId);
                _currentBatchCount++;
                if (_currentBatchCount >= _batchSize)
                {
                    AutoCommit();
                }
            }
        }

        // For the document store we keep the original behavior of storing one blob
        // per doc.
        private static string JoinForDocStore(string[] fieldContents)
        {
            if (fieldContents.Length == 1) return fieldContents[0] ?? string.Empty;
            return string.Join("", fieldContents);
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

                // Buffer per-doc field lengths; they will be persisted alongside the
                // commit-time segment writes.
                _pendingDocLengths.Add((docId, (int[])perFieldLengths.Clone()));
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

        // Implicit-commit when batchSize is exceeded mid-add. We still produce a
        // single segment for the buffered docs (and let auto-merge consolidate later).
        private void AutoCommit() => Commit();

        public void Commit()
        {
            // Commit produces 0 or 1 new segment from this commit's writes, then
            // applies pending deletes against the existing segments, then runs the
            // merge policy. Everything is staged into a single WriteBatch and
            // written atomically.
            using var batch = new WriteBatch();

            FlushBatchToSegment(batch);
            ApplyPendingDeletes(batch);
            PersistGlobalState(batch);

            _db.Db.Write(batch);

            // Auto-merge runs separately (each merge is its own atomic step).
            RunAutoMerge();
        }

        private void FlushBatchToSegment(WriteBatch batch)
        {
            // First-batch logic: if this is still the first batch, materialize it now.
            if (_isFirstBatch)
            {
                GenerateCommonTokens();
                if (_commonTokens.Count > 0)
                {
                    CommonTokensPersistence.AddToBatch(batch, _db.Meta, _commonTokens);
                }
                foreach (var (fields, docId) in _firstBatchBuffer)
                {
                    IndexDocumentInternal(fields, docId);
                }
                _firstBatchBuffer.Clear();
                _isFirstBatch = false;
            }

            // Flush buffered doc-lengths.
            foreach (var (docId, lengths) in _pendingDocLengths)
            {
                _docLengthStore.AddToBatch(batch, docId, lengths);
            }
            _pendingDocLengths.Clear();

            // Flush buffered document content (atomic with segment commit).
            foreach (var (docId, bytes) in _pendingDocs)
            {
                batch.Put(Keys.DocIdKey(docId), bytes, _db.Docs);
            }
            _pendingDocs.Clear();

            if (_currentBatch.Count == 0) return;

            ulong segId = _manifest.AllocateSegmentId();
            var info = SegmentWriter.Write(_db, segId, _currentBatch, batch);
            info.Id = segId;
            _manifest.Segments.Add(info);

            // Release the in-memory posting buffers.
            foreach (var p in _currentBatch.Values) p.Dispose();
            _currentBatch.Clear();
            _currentBatchCount = 0;
        }

        private void ApplyPendingDeletes(WriteBatch batch)
        {
            if (_pendingDeletes.Count == 0) return;

            // For every segment, OR only the deletes that actually correspond to docs
            // in that segment (cheap probe via the per-segment LiveDocIds bitmap).
            foreach (var seg in _manifest.Segments)
            {
                using var sr = new SegmentReader(_db, seg);
                var bm = sr.Deletes;
                int newDeletes = 0;
                foreach (var d in _pendingDeletes)
                {
                    if (!sr.LiveDocIds.Contains(d)) continue;
                    if (bm.Add(d)) newDeletes++;
                }
                if (newDeletes == 0) continue;
                batch.Put(Keys.SegIdKey(seg.Id), bm.SaveToBytes(), _db.SegDeletes);
                seg.DeleteCount += newDeletes;
                batch.Put(Keys.SegIdKey(seg.Id), seg.Serialize(), _db.SegMeta);
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
                MergeSegments(todo);
            }
        }

        // Force-merge to a single segment.
        public void ForceMerge()
        {
            // First, flush any staged docs.
            if (_currentBatch.Count > 0 || _isFirstBatch && _firstBatchBuffer.Count > 0 || _pendingDeletes.Count > 0 || _pendingDocLengths.Count > 0 || _pendingDocs.Count > 0)
            {
                Commit();
            }
            var todo = _mergePolicy.FindForceMerge(_manifest.Segments);
            if (todo == null) return;
            MergeSegments(todo);

            using var batch = new WriteBatch();
            PersistGlobalState(batch);
            _db.Db.Write(batch);
        }

        private void MergeSegments(List<SegmentInfo> sources)
        {
            ulong newId = _manifest.AllocateSegmentId();
            var sourceReaders = new List<SegmentReader>(sources.Count);
            using var batch = new WriteBatch();
            try
            {
                foreach (var s in sources)
                {
                    sourceReaders.Add(new SegmentReader(_db, s));
                }
                var newInfo = SegmentWriter.Merge(_db, newId, sourceReaders, batch);
                newInfo.Id = newId;
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

            // Delete the rows of merged-away segments from every per-segment CF.
            foreach (var s in sources)
            {
                var key = Keys.SegIdKey(s.Id);
                batch.Delete(key, _db.SegMeta);
                batch.Delete(key, _db.SegTokens);
                batch.Delete(key, _db.SegDeletes);
                batch.Delete(key, _db.SegLiveDocs);
                // Delete the postings rows for this segment via a range delete on
                // [segId, segId+1). The 9-byte upper bound covers any single-byte
                // field index appended to the 8-byte prefix.
                var startKey = Keys.PostingsSegmentPrefix(s.Id);
                var endKey = Keys.PostingsSegmentPrefix(s.Id + 1);
                batch.DeleteRange(startKey, (ulong)startKey.Length, endKey, (ulong)endKey.Length, _db.Postings);
            }

            _manifest.AddNextIdToBatch(batch, _db.Meta);
            _db.Db.Write(batch);
        }

        private void PersistGlobalState(WriteBatch batch)
        {
            _manifest.AddNextIdToBatch(batch, _db.Meta);
            var stats = new IndexStats
            {
                TotalDocs = _totalDocs,
                TotalTokens = _totalTokens,
                FieldCount = _fieldCount,
                TotalTokensPerField = (ulong[])_totalTokensPerField.Clone(),
            };
            stats.AddToBatch(batch, _db.Meta);
        }

        public void Dispose()
        {
            foreach (var p in _currentBatch.Values) p.Dispose();
            if (_ownsDb) _db.Dispose();
        }
    }
}
