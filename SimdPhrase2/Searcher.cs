using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SimdPhrase2.Db;
using SimdPhrase2.Roaringish;
using SimdPhrase2.Roaringish.Intersect;
using SimdPhrase2.Storage;

namespace SimdPhrase2
{
    public class SearcherOptions
    {
        public bool ForceNaive { get; set; } = false;
        public ITextTokenizer Tokenizer { get; set; }
        public ISimdStorage Storage { get; set; }
    }

    public class Searcher : IDisposable
    {
        private readonly string _indexName;
        private readonly List<SegmentReader> _segments;
        private DocumentStore _docStore;
        private FieldRegistry _fieldRegistry;
        private LiveDocs _globalDeletes;
        private SegmentManifest _manifest;
        private IIntersect _intersect;
        private Stats _stats;
        private ITextTokenizer _tokenizer;
        private ISimdStorage _storage;

        private IndexStats _indexStats;
        private float _avgDocLength;
        private Dictionary<string, float> _avgDocLengthByField;

        public Searcher(string indexName, SearcherOptions options)
        {
            options ??= new SearcherOptions();
            _indexName = indexName;
            _tokenizer = options.Tokenizer ?? new BasicTokenizer();
            _storage = options.Storage ?? new FileSystemStorage();
            _intersect = options.ForceNaive ? new NaiveIntersect() : new SimdIntersect();
            _stats = new Stats();
            _segments = new List<SegmentReader>();

            _fieldRegistry = FieldRegistry.Load(_storage, _storage.Combine(indexName, "field_meta.json"));
            _manifest = SegmentManifest.Load(_storage, _storage.Combine(indexName, "segments.json"));
            _docStore = new DocumentStore(indexName, _storage);
            _globalDeletes = LiveDocs.Load(_storage, _storage.Combine(indexName, "deleted_docs.bin"));

            foreach (var s in _manifest.Segments)
            {
                string dir = _storage.Combine(_storage.Combine(indexName, "segments"), s.Id);
                if (_storage.DirectoryExists(dir))
                {
                    _segments.Add(new SegmentReader(dir, _storage));
                }
            }

            _indexStats = IndexStats.Load(_storage, _storage.Combine(indexName, "index_stats.json"));
            if (_indexStats.TotalDocs > 0)
                _avgDocLength = (float)_indexStats.TotalTokens / _indexStats.TotalDocs;

            // Per-field avg doc length.
            _avgDocLengthByField = new Dictionary<string, float>();
            var totalTokensByField = new Dictionary<string, ulong>();
            var totalDocsByField = new Dictionary<string, uint>();
            foreach (var s in _manifest.Segments)
            {
                foreach (var (f, t) in s.TokensByField)
                {
                    totalTokensByField.TryGetValue(f, out var sum);
                    totalTokensByField[f] = sum + t;
                }
                foreach (var (f, d) in s.DocsByField)
                {
                    totalDocsByField.TryGetValue(f, out var sum);
                    totalDocsByField[f] = sum + d;
                }
            }
            foreach (var (f, t) in totalTokensByField)
            {
                if (totalDocsByField.TryGetValue(f, out var d) && d > 0)
                    _avgDocLengthByField[f] = (float)t / d;
            }
        }

        public Searcher(string indexName, bool forceNaive = false, ITextTokenizer tokenizer = null, ISimdStorage storage = null)
            : this(indexName, new SearcherOptions { ForceNaive = forceNaive, Tokenizer = tokenizer, Storage = storage })
        { }

        public FieldRegistry Fields => _fieldRegistry;

        public void Dispose()
        {
            foreach (var s in _segments) s.Dispose();
            _docStore?.Dispose();
        }

        public string GetDocument(uint docId) => _docStore.GetDocument(docId);

        // ---- Search APIs ----

        public List<uint> Search(string query) => Search(query, FieldRegistry.DefaultField);

        public List<uint> Search(string query, string field)
        {
            var rawTokens = TokenizeRaw(query);
            if (rawTokens.Count == 0) return new List<uint>();

            var seen = new HashSet<uint>();
            var result = new List<uint>();
            foreach (var seg in _segments)
            {
                var docIds = SearchInSegment(seg, rawTokens, field);
                foreach (var d in docIds)
                {
                    if (!_globalDeletes.IsLive(d)) continue;
                    if (seen.Add(d)) result.Add(d);
                }
            }
            return result;
        }

        private List<string> TokenizeRaw(string query)
        {
            var rawTokens = new List<string>();
            foreach (var t in _tokenizer.Tokenize(query.AsSpan()))
            {
                rawTokens.Add(t.ToString());
            }
            return rawTokens;
        }

        private List<uint> SearchInSegment(SegmentReader seg, List<string> rawTokens, string field)
        {
            // Apply common-tokens optimization within segment to merge tokens.
            var tokens = MergeAndMinimizeTokens(seg, rawTokens, field);

            var packedTokens = new List<(string Token, RoaringishPacked Packed)>();
            try
            {
                foreach (var token in tokens)
                {
                    string encoded = FieldRegistry.EncodeToken(field, token);
                    if (!seg.Tokens.TryGet(encoded, out var offset)) return new List<uint>();
                    packedTokens.Add((token, LoadPacked(seg, offset)));
                }

                if (packedTokens.Count == 1)
                {
                    return packedTokens[0].Packed.GetDocIds();
                }

                int bestIdx = 0;
                long minLen = long.MaxValue;
                for (int i = 0; i < packedTokens.Count - 1; i++)
                {
                    long len = packedTokens[i].Packed.Length + packedTokens[i + 1].Packed.Length;
                    if (len < minLen) { minLen = len; bestIdx = i; }
                }

                var lhsItem = packedTokens[bestIdx];
                var rhsItem = packedTokens[bestIdx + 1];
                var result = Intersect(lhsItem.Packed, rhsItem.Packed, 1);

                int leftI = bestIdx - 1;
                int rightI = bestIdx + 2;
                int resultPhraseLen = 2;

                while (true)
                {
                    RoaringishPacked nextLhs = leftI >= 0 ? packedTokens[leftI].Packed : null;
                    RoaringishPacked nextRhs = rightI < packedTokens.Count ? packedTokens[rightI].Packed : null;
                    if (nextLhs == null && nextRhs == null) break;

                    RoaringishPacked oldResult = result;
                    if (nextLhs != null && (nextRhs == null || nextLhs.Length <= nextRhs.Length))
                    {
                        result = Intersect(nextLhs, result, (ushort)resultPhraseLen);
                        resultPhraseLen++;
                        leftI--;
                    }
                    else
                    {
                        result = Intersect(result, nextRhs, 1);
                        resultPhraseLen++;
                        rightI++;
                    }
                    oldResult.Dispose();
                    if (result.Length == 0) break;
                }

                var docIds = result.GetDocIds();
                result.Dispose();
                return docIds;
            }
            finally
            {
                foreach (var pt in packedTokens) pt.Packed.Dispose();
            }
        }

        private List<string> MergeAndMinimizeTokens(SegmentReader seg, List<string> tokens, string field)
        {
            if (seg.CommonTokens.Count == 0) return tokens;

            int n = tokens.Count;
            long[] dp = new long[n + 1];
            string[] choice = new string[n];
            int[] nextIndex = new int[n];

            for (int i = 0; i <= n; i++) dp[i] = long.MaxValue;
            dp[n] = 0;

            for (int i = n - 1; i >= 0; i--)
            {
                string t = tokens[i];
                string encoded = FieldRegistry.EncodeToken(field, t);
                if (seg.Tokens.TryGet(encoded, out var offset))
                {
                    long cost = (offset.Length / 8);
                    if (dp[i + 1] != long.MaxValue)
                    {
                        cost += dp[i + 1];
                        if (cost < dp[i])
                        {
                            dp[i] = cost;
                            choice[i] = t;
                            nextIndex[i] = i + 1;
                        }
                    }
                }
                else
                {
                    dp[i] = 0;
                    choice[i] = t;
                    nextIndex[i] = i + 1;
                }

                bool isFirstRare = !seg.CommonTokens.Contains(t);
                string currentMerged = t;
                int maxWindow = 3;
                for (int j = 1; j < maxWindow && (i + j) < n; j++)
                {
                    string nextToken = tokens[i + j];
                    bool isNextRare = !seg.CommonTokens.Contains(nextToken);
                    if (isFirstRare && isNextRare) break;
                    currentMerged += " " + nextToken;
                    string encodedMerged = FieldRegistry.EncodeToken(field, currentMerged);
                    if (seg.Tokens.TryGet(encodedMerged, out offset))
                    {
                        if (dp[i + j + 1] != long.MaxValue)
                        {
                            long cost = (offset.Length / 8) + dp[i + j + 1];
                            if (cost < dp[i])
                            {
                                dp[i] = cost;
                                choice[i] = currentMerged;
                                nextIndex[i] = i + j + 1;
                            }
                        }
                    }
                    if (isNextRare) break;
                }
            }

            var result = new List<string>();
            int curr = 0;
            while (curr < n)
            {
                if (choice[curr] == null) return tokens;
                result.Add(choice[curr]);
                curr = nextIndex[curr];
            }
            return result;
        }

        private RoaringishPacked LoadPacked(SegmentReader seg, FileOffset offset)
        {
            int ulongCount = (int)(offset.Length / 8);
            var buffer = new AlignedBuffer<ulong>(ulongCount);
            buffer.SetLength(ulongCount);

            seg.PackedStream.Seek(offset.Begin, SeekOrigin.Begin);
            Span<byte> byteSpan = MemoryMarshal.Cast<ulong, byte>(buffer.AsSpan());
            seg.PackedStream.ReadExactly(byteSpan);
            return new RoaringishPacked(buffer, takeOwnership: true);
        }

        private RoaringishPacked Intersect(RoaringishPacked lhs, RoaringishPacked rhs, ushort lhsLenFull)
        {
            ulong addToGroup = (ulong)(lhsLenFull / 16) * RoaringishPacked.ADD_ONE_GROUP;
            ushort lhsLen = (ushort)(lhsLenFull % 16);
            ushort msbMask = (ushort)(~((ushort)ushort.MaxValue >> lhsLen));
            ushort lsbMask = (ushort)(~((ushort)ushort.MaxValue << lhsLen));

            int size = _intersect.IntersectionBufferSize(lhs.Length, rhs.Length);
            using var packedResult = new AlignedBuffer<ulong>(size);
            packedResult.SetLength(size);
            using var msbPackedResult = new AlignedBuffer<ulong>(lhs.Length + 1);
            msbPackedResult.SetLength(lhs.Length + 1);

            int lhsI = 0, rhsI = 0, i = 0, j = 0;
            int packedLen1 = 0, msbLen1 = 0;
            int minLen = Math.Min(lhs.Length, rhs.Length);
            int maxLen = Math.Max(lhs.Length, rhs.Length);
            int proportion = minLen > 0 ? maxLen / minLen : 0;

            if (proportion >= 650)
            {
                GallopIntersectFirst.Intersect(true, lhs.AsSpan(), rhs.AsSpan(), packedResult, ref i, addToGroup, lhsLen, lsbMask, _stats);
                packedLen1 = i;
                GallopIntersectFirst.Intersect(false, lhs.AsSpan(), rhs.AsSpan(), msbPackedResult, ref j, addToGroup, lhsLen, lsbMask, _stats);
                msbLen1 = j;
            }
            else
            {
                _intersect.InnerIntersect(true, lhs.AsSpan(), rhs.AsSpan(), ref lhsI, ref rhsI, packedResult, ref i, msbPackedResult, ref j, addToGroup, lhsLen, msbMask, lsbMask, _stats);
                packedLen1 = i;
                msbLen1 = j;
            }

            if (msbLen1 == 0)
            {
                var ret = new RoaringishPacked(packedLen1);
                ret.Buffer.SetLength(packedLen1);
                packedResult.AsSpan(0, packedLen1).CopyTo(ret.Buffer.AsSpan());
                return ret;
            }

            using var msbResult2 = new AlignedBuffer<ulong>(size);
            msbResult2.SetLength(size);
            using var dummy = new AlignedBuffer<ulong>(0);

            int msbLen2 = 0;
            minLen = Math.Min(msbLen1, rhs.Length);
            maxLen = Math.Max(msbLen1, rhs.Length);
            proportion = minLen > 0 ? maxLen / minLen : 0;

            if (proportion >= 120)
            {
                int i2 = 0;
                GallopIntersectSecond.Intersect(msbPackedResult.AsSpan(0, msbLen1), rhs.AsSpan(), msbResult2, ref i2, lhsLen, lsbMask, _stats);
                msbLen2 = i2;
            }
            else
            {
                int lhsI2 = 0, rhsI2 = 0, i2 = 0, j2 = 0;
                _intersect.InnerIntersect(false, msbPackedResult.AsSpan(0, msbLen1), rhs.AsSpan(), ref lhsI2, ref rhsI2, msbResult2, ref i2, dummy, ref j2, addToGroup, lhsLen, msbMask, lsbMask, _stats);
                msbLen2 = i2;
            }

            return RoaringishPacked.MergeResults(packedResult, packedLen1, msbResult2, msbLen2);
        }

        // ---- BM25 ----

        public List<(uint DocId, float Score)> SearchBM25(string query, int k = 10, float k1 = 1.2f, float b = 0.75f)
            => SearchBM25(query, FieldRegistry.DefaultField, k, k1, b);

        public List<(uint DocId, float Score)> SearchBM25(string query, string field, int k = 10, float k1 = 1.2f, float b = 0.75f)
        {
            var tokens = TokenizeRaw(query);
            if (tokens.Count == 0) return new List<(uint, float)>();

            float boost = _fieldRegistry.GetBoost(field);
            float avgLen = _avgDocLengthByField.TryGetValue(field, out var v) && v > 0 ? v
                          : (_avgDocLength > 0 ? _avgDocLength : 1f);

            // Aggregate doc-frequency (DF) across segments per token.
            long N = 0;
            foreach (var s in _manifest.Segments) N += s.DocsByField.TryGetValue(field, out var d) ? d : 0;
            if (N == 0) N = _indexStats.TotalDocs;

            var df = new Dictionary<string, int>();
            foreach (var t in tokens)
            {
                string encoded = FieldRegistry.EncodeToken(field, t);
                int total = 0;
                foreach (var seg in _segments)
                {
                    if (seg.Tokens.TryGet(encoded, out var off)) total += off.DocCount;
                }
                df[t] = total;
            }

            var scores = new Dictionary<uint, float>();
            foreach (var t in tokens)
            {
                int dfi = df[t];
                if (dfi == 0) continue;
                float idf = MathF.Log(1f + (N - dfi + 0.5f) / (dfi + 0.5f));
                if (idf < 0) idf = 0;

                string encoded = FieldRegistry.EncodeToken(field, t);
                foreach (var seg in _segments)
                {
                    if (!seg.Tokens.TryGet(encoded, out var off)) continue;
                    using var packed = LoadPacked(seg, off);
                    var freqs = packed.GetDocIdsAndFreqs();
                    foreach (var (docId, tf) in freqs)
                    {
                        if (!_globalDeletes.IsLive(docId)) continue;
                        int docLen = seg.DocLengths.Get(docId, field);
                        if (docLen == 0) docLen = (int)avgLen;
                        float score = boost * idf * (tf * (k1 + 1f)) / (tf + k1 * (1f - b + b * (docLen / avgLen)));
                        ref float scoreVal = ref CollectionsMarshal.GetValueRefOrAddDefault(scores, docId, out _);
                        scoreVal += score;
                    }
                }
            }

            return scores.OrderByDescending(kvp => kvp.Value).Take(k).Select(kvp => (kvp.Key, kvp.Value)).ToList();
        }

        // ---- Boolean ----

        public List<uint> SearchBoolean(string query)
        {
            var parser = new BooleanQueryParser();
            var root = parser.Parse(query);
            if (root == null) return new List<uint>();
            return SearchBoolean(root);
        }

        public List<uint> SearchBoolean(QueryNode root)
        {
            if (root == null) return new List<uint>();
            var results = Evaluate(root);
            return results.OrderBy(x => x).ToList();
        }

        private IEnumerable<uint> Evaluate(QueryNode node)
        {
            if (node is TermNode t)
            {
                // Field-prefixed terms: "field:term"
                string term = t.Term;
                string field = FieldRegistry.DefaultField;
                int colon = term.IndexOf(':');
                if (colon > 0)
                {
                    string maybeField = term.Substring(0, colon);
                    if (_fieldRegistry.Contains(maybeField))
                    {
                        field = maybeField;
                        term = term.Substring(colon + 1);
                    }
                }
                return Search(term, field);
            }
            if (node is AndNode a) return Evaluate(a.Left).Intersect(Evaluate(a.Right));
            if (node is OrNode o) return Evaluate(o.Left).Union(Evaluate(o.Right));
            if (node is NotNode n)
            {
                var childDocs = new HashSet<uint>(Evaluate(n.Child));
                return Enumerable.Range(0, (int)_indexStats.TotalDocs)
                    .Select(i => (uint)i)
                    .Where(id => !childDocs.Contains(id) && _globalDeletes.IsLive(id));
            }
            return Enumerable.Empty<uint>();
        }
    }
}
