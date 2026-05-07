using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SimdPhrase2.Db;
using SimdPhrase2.Roaringish;
using SimdPhrase2.Storage;

namespace SimdPhrase2
{
    /// <summary>
    /// Options for configuring an <see cref="Indexer"/>.
    /// </summary>
    public class IndexerOptions
    {
        public CommonTokensConfig CommonTokens { get; set; } = CommonTokensConfig.None;
        public int BatchSize { get; set; } = 300_000;
        /// <summary>Number of segments before auto-compaction kicks in on Commit().</summary>
        public int MaxSegmentsBeforeCompact { get; set; } = 10;
        public ITextTokenizer Tokenizer { get; set; }
        public ISimdStorage Storage { get; set; }
        /// <summary>Optional pre-declared field configuration (e.g. boosts).</summary>
        public List<FieldOptions> Fields { get; set; }
        /// <summary>Default field used when calling <see cref="Indexer.AddDocument(string, uint)"/>.</summary>
        public string DefaultField { get; set; } = FieldRegistry.DefaultField;
        /// <summary>Whether to wipe an existing index directory at construction time.</summary>
        public bool ClearExisting { get; set; } = true;
    }

    public class Indexer : IDisposable
    {
        private readonly string _indexName;
        private readonly int _batchSize;
        private readonly int _maxSegmentsBeforeCompact;
        private readonly string _defaultField;

        private int _currentBatchCount;
        private Dictionary<string, RoaringishPacked> _currentBatch;
        private DocLengthsStore _currentLengths;
        private Dictionary<string, ulong> _currentTokensByField;
        private Dictionary<string, HashSet<uint>> _currentDocsByField;

        private DocumentStore _docStore;
        private FieldRegistry _fieldRegistry;
        private SegmentManifest _manifest;

        private CommonTokensConfig _commonTokensConfig;
        private HashSet<string> _commonTokens;
        private List<IndexDocument> _firstBatchBuffer;
        private bool _isFirstBatch;
        private ITextTokenizer _tokenizer;
        private ISimdStorage _storage;

        private LiveDocs _globalDeletes;

        public Indexer(string indexName, IndexerOptions options)
        {
            options ??= new IndexerOptions();
            _indexName = indexName;
            _batchSize = options.BatchSize;
            _maxSegmentsBeforeCompact = Math.Max(2, options.MaxSegmentsBeforeCompact);
            _defaultField = string.IsNullOrEmpty(options.DefaultField) ? FieldRegistry.DefaultField : options.DefaultField;
            _tokenizer = options.Tokenizer ?? new BasicTokenizer();
            _storage = options.Storage ?? new FileSystemStorage();
            _commonTokensConfig = options.CommonTokens ?? CommonTokensConfig.None;

            if (options.ClearExisting && _storage.DirectoryExists(_indexName))
            {
                _storage.DeleteDirectory(_indexName);
            }
            _storage.CreateDirectory(_indexName);
            _storage.CreateDirectory(_storage.Combine(_indexName, "segments"));

            _fieldRegistry = FieldRegistry.Load(_storage, _storage.Combine(_indexName, "field_meta.json"));
            _fieldRegistry.GetOrAdd(_defaultField);
            _fieldRegistry.RegisterAll(options.Fields);

            _manifest = SegmentManifest.Load(_storage, _storage.Combine(_indexName, "segments.json"));
            _docStore = new DocumentStore(_indexName, _storage);
            _globalDeletes = LiveDocs.Load(_storage, _storage.Combine(_indexName, "deleted_docs.bin"));

            _currentBatch = new Dictionary<string, RoaringishPacked>();
            _currentLengths = new DocLengthsStore();
            _currentTokensByField = new Dictionary<string, ulong>();
            _currentDocsByField = new Dictionary<string, HashSet<uint>>();
            _currentBatchCount = 0;

            _firstBatchBuffer = new List<IndexDocument>();
            _isFirstBatch = _manifest.Segments.Count == 0;
            _commonTokens = new HashSet<string>();
        }

        // Backwards-compatible constructor.
        public Indexer(string indexName, CommonTokensConfig commonTokensConfig = null, int batchSize = 300_000, ITextTokenizer tokenizer = null, ISimdStorage storage = null)
            : this(indexName, new IndexerOptions
            {
                CommonTokens = commonTokensConfig,
                BatchSize = batchSize,
                Tokenizer = tokenizer,
                Storage = storage,
            })
        { }

        public FieldRegistry Fields => _fieldRegistry;

        public void Index(IEnumerable<(string content, uint docId)> docs)
        {
            foreach (var (content, docId) in docs)
            {
                AddDocument(content, docId);
            }
            Commit();
        }

        public void Index(IEnumerable<IndexDocument> docs)
        {
            foreach (var doc in docs)
            {
                AddDocument(doc);
            }
            Commit();
        }

        public void AddDocument(string content, uint docId)
        {
            var doc = new IndexDocument(docId).Add(_defaultField, content);
            AddDocument(doc);
        }

        public void AddDocument(IndexDocument doc)
        {
            // Store the document. Use the joined-content representation so that
            // existing GetDocument() returns the original concatenated text.
            string stored = string.Join("\n", doc.Fields.Select(f => f.Value ?? string.Empty));
            _docStore.AddDocument(doc.Id, stored);

            // Store fields as JSON-like blob alongside? For now, we expose retrieval through GetField().
            // We rely on doc_offsets for length-by-field retrieval; if needed, callers can use the
            // Searcher.GetField API which uses the joined string + per-field metadata. To keep things
            // simple, multi-field stored values are concatenated with a delimiter.

            if (_isFirstBatch)
            {
                _firstBatchBuffer.Add(doc);
                _currentBatchCount++;

                if (_currentBatchCount >= _batchSize)
                {
                    FlushBatch();
                }
            }
            else
            {
                IndexDocumentInternal(doc);
                _currentBatchCount++;
                if (_currentBatchCount >= _batchSize)
                {
                    FlushBatch();
                }
            }
        }

        public void DeleteDocument(uint docId)
        {
            _globalDeletes.MarkDeleted(docId);
        }

        private void IndexDocumentInternal(IndexDocument doc)
        {
            var docTokens = new Dictionary<string, List<uint>>();

            foreach (var (field, value) in doc.Fields)
            {
                if (string.IsNullOrEmpty(value)) continue;
                _fieldRegistry.GetOrAdd(field);

                var perFieldTokens = new List<(string token, uint index)>();
                int tokensCount = 0;
                uint lastIndex = uint.MaxValue;

                var enumerator = _tokenizer.Tokenize(value.AsSpan()).GetEnumerator();
                while (enumerator.MoveNext())
                {
                    perFieldTokens.Add((enumerator.Current.ToString(), enumerator.CurrentIndex));
                    if (enumerator.CurrentIndex != lastIndex) tokensCount++;
                    lastIndex = enumerator.CurrentIndex;
                }

                _currentLengths.Set(doc.Id, field, tokensCount);
                _currentTokensByField.TryGetValue(field, out var existingTokens);
                _currentTokensByField[field] = existingTokens + (ulong)tokensCount;
                if (!_currentDocsByField.TryGetValue(field, out var docSet))
                {
                    docSet = new HashSet<uint>();
                    _currentDocsByField[field] = docSet;
                }
                docSet.Add(doc.Id);

                for (int i = 0; i < perFieldTokens.Count; i++)
                {
                    var token = perFieldTokens[i];
                    string encoded = FieldRegistry.EncodeToken(field, token.token);

                    ref List<uint> list = ref CollectionsMarshal.GetValueRefOrAddDefault(docTokens, encoded, out var exists);
                    if (!exists) list = new List<uint>();
                    list.Add(token.index);

                    if (_commonTokens.Count > 0)
                    {
                        bool isFirstRare = !_commonTokens.Contains(token.token);
                        int maxWindow = 3;
                        string currentMerged = token.token;
                        for (int j = 1; j < maxWindow && (i + j) < perFieldTokens.Count; j++)
                        {
                            var nextToken = perFieldTokens[i + j];
                            if (nextToken.index == token.index)
                            {
                                maxWindow++;
                                continue;
                            }
                            bool isNextRare = !_commonTokens.Contains(nextToken.token);
                            if (isFirstRare && isNextRare) break;
                            currentMerged += " " + nextToken.token;

                            string encodedMerged = FieldRegistry.EncodeToken(field, currentMerged);
                            list = ref CollectionsMarshal.GetValueRefOrAddDefault(docTokens, encodedMerged, out exists);
                            if (!exists) list = new List<uint>();
                            list.Add(token.index);

                            if (isNextRare) break;
                        }
                    }
                }
            }

            foreach (var (encodedToken, positions) in docTokens)
            {
                if (!_currentBatch.TryGetValue(encodedToken, out var packed))
                {
                    packed = new RoaringishPacked();
                    _currentBatch[encodedToken] = packed;
                }
                packed.Push(doc.Id, positions);
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
                foreach (var doc in _firstBatchBuffer)
                {
                    foreach (var (_, value) in doc.Fields)
                    {
                        if (string.IsNullOrEmpty(value)) continue;
                        foreach (var tokenSpan in _tokenizer.Tokenize(value.AsSpan()))
                        {
                            string token = tokenSpan.ToString();
                            freq[token] = freq.GetValueOrDefault(token, 0) + 1;
                        }
                    }
                }
                var top = freq.OrderByDescending(kvp => kvp.Value).Take(fixedNumConfig.Num).Select(kvp => kvp.Key);
                _commonTokens = new HashSet<string>(top);
            }
            else if (_commonTokensConfig is CommonTokensConfig.PercentageConfig percentageConfig)
            {
                var freq = new Dictionary<string, int>();
                foreach (var doc in _firstBatchBuffer)
                {
                    foreach (var (_, value) in doc.Fields)
                    {
                        if (string.IsNullOrEmpty(value)) continue;
                        foreach (var tokenSpan in _tokenizer.Tokenize(value.AsSpan()))
                        {
                            string token = tokenSpan.ToString();
                            freq[token] = freq.GetValueOrDefault(token, 0) + 1;
                        }
                    }
                }
                int count = (int)(freq.Count * percentageConfig.Percentage);
                var top = freq.OrderByDescending(kvp => kvp.Value).Take(count).Select(kvp => kvp.Key);
                _commonTokens = new HashSet<string>(top);
            }
        }

        private void FlushBatch()
        {
            if (_isFirstBatch)
            {
                GenerateCommonTokens();
                foreach (var doc in _firstBatchBuffer)
                {
                    IndexDocumentInternal(doc);
                }
                _firstBatchBuffer.Clear();
                _isFirstBatch = false;
            }

            if (_currentBatch.Count == 0 && _currentBatchCount == 0) return;

            string segId = _manifest.AllocateSegmentId();
            string segDir = _storage.Combine(_storage.Combine(_indexName, "segments"), segId);
            _storage.CreateDirectory(segDir);

            // Write posting lists, build TokenStore for this segment.
            using (var tokenStore = new TokenStore(segDir, _storage))
            using (var packedFile = _storage.OpenWrite(_storage.Combine(segDir, "roaringish_packed.bin")))
            {
                var sortedTokens = _currentBatch.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
                foreach (var token in sortedTokens)
                {
                    long currentPos = packedFile.Position;
                    long alignedPos = (currentPos + 63) & ~63;
                    if (alignedPos > currentPos)
                    {
                        packedFile.Write(new byte[alignedPos - currentPos]);
                    }
                    long startOffset = packedFile.Position;

                    var packed = _currentBatch[token];
                    var span = packed.AsSpan();
                    long byteLen = span.Length * 8L;
                    byte[] bytes = new byte[byteLen];
                    MemoryMarshal.Cast<ulong, byte>(span).CopyTo(bytes);
                    packedFile.Write(bytes);

                    int docCount = CountDocsInSpan(span);
                    tokenStore.Add(token, startOffset, byteLen, docCount);
                }
            }

            // Save doc lengths.
            _currentLengths.Save(_storage, _storage.Combine(segDir, "doc_lengths.bin"));

            // Save common tokens for this segment.
            if (_commonTokens.Count > 0)
            {
                CommonTokensPersistence.Save(_storage, _storage.Combine(segDir, "common_tokens.bin"), _commonTokens);
            }

            // Empty live-docs file (no per-segment deletions yet).
            new LiveDocs().Save(_storage, _storage.Combine(segDir, "live_docs.bin"));

            // Build segment info.
            var info = new SegmentInfo
            {
                Id = segId,
                TotalDocs = (uint)_currentBatchCount,
                TotalTokens = _currentTokensByField.Values.Aggregate(0UL, (a, b) => a + b),
            };
            foreach (var (field, total) in _currentTokensByField) info.TokensByField[field] = total;
            foreach (var (field, set) in _currentDocsByField) info.DocsByField[field] = (uint)set.Count;

            _manifest.Segments.Add(info);

            // Cleanup in-memory.
            foreach (var p in _currentBatch.Values) p.Dispose();
            _currentBatch.Clear();
            _currentLengths = new DocLengthsStore();
            _currentTokensByField.Clear();
            _currentDocsByField.Clear();
            _currentBatchCount = 0;
        }

        public void Commit()
        {
            FlushBatch();

            // Auto-compact if too many segments.
            if (_manifest.Segments.Count > _maxSegmentsBeforeCompact)
            {
                CompactAll();
            }

            // Persist manifest, fields, deletes, stats.
            _fieldRegistry.Save(_storage, _storage.Combine(_indexName, "field_meta.json"));
            _manifest.Save(_storage, _storage.Combine(_indexName, "segments.json"));
            _globalDeletes.Save(_storage, _storage.Combine(_indexName, "deleted_docs.bin"));

            // Aggregated stats for backwards-compat (excludes deletions for simplicity).
            var stats = new IndexStats();
            foreach (var s in _manifest.Segments)
            {
                stats.TotalDocs += s.TotalDocs;
                stats.TotalTokens += s.TotalTokens;
            }
            // Subtract globally deleted docs (best-effort, assumes each delete is unique).
            if (stats.TotalDocs > _globalDeletes.DeletedCount)
                stats.TotalDocs -= (uint)_globalDeletes.DeletedCount;
            else
                stats.TotalDocs = 0;
            IndexStats.Save(_storage, _storage.Combine(_indexName, "index_stats.json"), stats);
        }

        /// <summary>Force compaction of all segments into one. Public for tests/maintenance.</summary>
        public void CompactAll()
        {
            if (_manifest.Segments.Count == 0) return;
            CompactSegments(_manifest.Segments.ToList());
        }

        private void CompactSegments(List<SegmentInfo> segments)
        {
            // Strategy: stream-merge token posting lists across segments, dropping deleted
            // docs. Output goes to a brand-new segment directory; old segments are deleted.
            string newId = _manifest.AllocateSegmentId();
            string newDir = _storage.Combine(_storage.Combine(_indexName, "segments"), newId);
            _storage.CreateDirectory(newDir);

            var readers = new List<SegmentReader>();
            foreach (var s in segments)
            {
                string dir = _storage.Combine(_storage.Combine(_indexName, "segments"), s.Id);
                readers.Add(new SegmentReader(dir, _storage));
            }

            // Gather union of all tokens across segments.
            var allTokens = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var r in readers)
            {
                foreach (var t in r.Tokens.GetAllTokens()) allTokens.Add(t);
            }

            // Merged doc lengths and stats.
            var mergedLengths = new DocLengthsStore();
            var mergedDocs = new HashSet<uint>();
            var mergedCommonTokens = new HashSet<string>();
            var tokensByField = new Dictionary<string, ulong>();
            var docsByField = new Dictionary<string, HashSet<uint>>();

            // Build merged lengths from each segment, skipping deleted docs.
            foreach (var r in readers)
            {
                foreach (var d in r.DocLengths.AllDocIds())
                {
                    if (!_globalDeletes.IsLive(d)) continue;
                    foreach (var f in r.DocLengths.Fields)
                    {
                        int len = r.DocLengths.Get(d, f);
                        if (len > 0)
                        {
                            mergedLengths.Set(d, f, len);
                            tokensByField.TryGetValue(f, out var sum);
                            tokensByField[f] = sum + (ulong)len;
                            if (!docsByField.TryGetValue(f, out var ds))
                            {
                                ds = new HashSet<uint>();
                                docsByField[f] = ds;
                            }
                            ds.Add(d);
                        }
                    }
                    mergedDocs.Add(d);
                }
                foreach (var ct in r.CommonTokens) mergedCommonTokens.Add(ct);
            }

            // Now write merged posting lists.
            using (var newTokens = new TokenStore(newDir, _storage))
            using (var packedFile = _storage.OpenWrite(_storage.Combine(newDir, "roaringish_packed.bin")))
            {
                foreach (var token in allTokens)
                {
                    // Collect all (docId, packed-ulong) entries, drop deleted docs, write.
                    var collected = new List<ulong>();
                    foreach (var r in readers)
                    {
                        if (!r.Tokens.TryGet(token, out var off)) continue;
                        if (off.Length <= 0 || r.PackedStream == null) continue;
                        int n = (int)(off.Length / 8);
                        var buf = new ulong[n];
                        var bytes = MemoryMarshal.AsBytes(buf.AsSpan());
                        r.PackedStream.Seek(off.Begin, SeekOrigin.Begin);
                        r.PackedStream.ReadExactly(bytes);
                        foreach (var p in buf)
                        {
                            uint did = RoaringishPacked.UnpackDocId(p);
                            if (!_globalDeletes.IsLive(did)) continue;
                            collected.Add(p);
                        }
                    }
                    if (collected.Count == 0) continue;

                    // Sort by docId+group, then OR-merge values for matching (docId,group).
                    collected.Sort((a, b) =>
                    {
                        ulong aGroup = RoaringishPacked.ClearValues(a);
                        ulong bGroup = RoaringishPacked.ClearValues(b);
                        return aGroup.CompareTo(bGroup);
                    });
                    var compacted = new List<ulong>(collected.Count);
                    ulong lastGroup = ulong.MaxValue;
                    foreach (var p in collected)
                    {
                        ulong g = RoaringishPacked.ClearValues(p);
                        if (g == lastGroup && compacted.Count > 0)
                        {
                            compacted[compacted.Count - 1] = compacted[compacted.Count - 1] | (p & 0xFFFFUL);
                        }
                        else
                        {
                            compacted.Add(p);
                            lastGroup = g;
                        }
                    }

                    // Align and write.
                    long currentPos = packedFile.Position;
                    long alignedPos = (currentPos + 63) & ~63;
                    if (alignedPos > currentPos) packedFile.Write(new byte[alignedPos - currentPos]);
                    long startOffset = packedFile.Position;
                    var arr = compacted.ToArray();
                    var spanBytes = MemoryMarshal.AsBytes(arr.AsSpan());
                    packedFile.Write(spanBytes);
                    int docCount = CountDocsInSpan(arr);
                    newTokens.Add(token, startOffset, spanBytes.Length, docCount);
                }
            }

            mergedLengths.Save(_storage, _storage.Combine(newDir, "doc_lengths.bin"));
            if (mergedCommonTokens.Count > 0)
                CommonTokensPersistence.Save(_storage, _storage.Combine(newDir, "common_tokens.bin"), mergedCommonTokens);
            new LiveDocs().Save(_storage, _storage.Combine(newDir, "live_docs.bin"));

            // Close & remove old segments.
            foreach (var r in readers) r.Dispose();
            foreach (var s in segments)
            {
                string dir = _storage.Combine(_storage.Combine(_indexName, "segments"), s.Id);
                if (_storage.DirectoryExists(dir)) _storage.DeleteDirectory(dir);
            }

            // Update manifest: remove old, add new.
            var oldIds = new HashSet<string>(segments.Select(s => s.Id));
            _manifest.Segments.RemoveAll(s => oldIds.Contains(s.Id));

            var newInfo = new SegmentInfo
            {
                Id = newId,
                TotalDocs = (uint)mergedDocs.Count,
                TotalTokens = tokensByField.Values.Aggregate(0UL, (a, b) => a + b),
            };
            foreach (var (f, t) in tokensByField) newInfo.TokensByField[f] = t;
            foreach (var (f, ds) in docsByField) newInfo.DocsByField[f] = (uint)ds.Count;
            _manifest.Segments.Add(newInfo);

            // Reset global deletes — they have been applied physically.
            _globalDeletes = new LiveDocs();
        }

        private static int CountDocsInSpan(Span<ulong> span)
        {
            int count = 0;
            uint last = uint.MaxValue;
            for (int i = 0; i < span.Length; i++)
            {
                uint did = RoaringishPacked.UnpackDocId(span[i]);
                if (did != last) { count++; last = did; }
            }
            return count;
        }

        public void Dispose()
        {
            foreach (var p in _currentBatch.Values) p.Dispose();
            _docStore?.Dispose();
        }
    }
}
