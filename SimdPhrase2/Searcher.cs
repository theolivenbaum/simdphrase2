using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using SimdPhrase2.Db;
using SimdPhrase2.Queries;
using SimdPhrase2.Roaringish;
using SimdPhrase2.Roaringish.Intersect;
using SimdPhrase2.Segments;
using SimdPhrase2.Storage;

namespace SimdPhrase2
{
    public class Searcher : IDisposable
    {
        private readonly string _indexPath;
        private DocumentStore _docStore;
        private DocLengthStore _docLengthStore;
        private IIntersect _intersect;
        private Stats _stats;
        private HashSet<string> _commonTokens;
        private ITextTokenizer _tokenizer;

        // RocksDB handle - this Searcher owns it unless the caller passed one in.
        private readonly SimdPhraseDb _db;
        private readonly bool _ownsDb;

        private List<SegmentReader> _segments;
        // Aggregated total length per (field, token) across all segments, lazily filled and cached.
        // Used by MergeAndMinimizeTokens for cost estimation.
        private Dictionary<FieldToken, long> _tokenLengthCache;

        // BM25 / Boolean support
        private IndexStats _indexStats;
        private readonly int _fieldCount;
        private readonly float[] _avgDocLengthPerField;

        public int FieldCount => _fieldCount;
        public SimdPhraseDb Db => _db;

        public Searcher(string indexPath, bool forceNaive = false, ITextTokenizer tokenizer = null, SimdPhraseDb db = null)
        {
            _indexPath = indexPath;
            _tokenizer = tokenizer ?? new BasicTokenizer();
            _intersect = forceNaive ? new NaiveIntersect() : new SimdIntersect();
            _stats = new Stats();
            _tokenLengthCache = new Dictionary<FieldToken, long>();

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
            _docStore = new DocumentStore(_db);
            _commonTokens = CommonTokensPersistence.Load(_db);

            _indexStats = IndexStats.Load(_db);
            _fieldCount = _indexStats.FieldCount > 0 ? _indexStats.FieldCount : 1;
            _docLengthStore = new DocLengthStore(_db, _fieldCount);
            _avgDocLengthPerField = new float[_fieldCount];
            if (_indexStats.TotalDocs > 0)
            {
                for (int i = 0; i < _fieldCount; i++)
                {
                    ulong fieldTokens = (_indexStats.TotalTokensPerField != null && i < _indexStats.TotalTokensPerField.Length)
                        ? _indexStats.TotalTokensPerField[i]
                        : 0UL;
                    _avgDocLengthPerField[i] = (float)fieldTokens / _indexStats.TotalDocs;
                }
            }

            _segments = new List<SegmentReader>();
            var manifest = SegmentManifest.Load(_db);
            foreach (var seg in manifest.Segments)
            {
                _segments.Add(new SegmentReader(_db, seg));
            }
        }

        // Aggregate (field, token) length across all segments. Used only for query planning.
        private bool TryGetAggregateLength(byte field, string token, out long totalLength)
        {
            var key = new FieldToken(field, token);
            if (_tokenLengthCache.TryGetValue(key, out totalLength))
            {
                return totalLength > 0;
            }
            long sum = 0;
            bool found = false;
            foreach (var seg in _segments)
            {
                if (seg.Tokens.TryGet(field, token, out var off))
                {
                    found = true;
                    sum += off.Length;
                }
            }
            _tokenLengthCache[key] = found ? sum : -1;
            totalLength = sum;
            return found;
        }

        // Returns true if the (field, token) exists in at least one segment.
        private bool TokenExists(byte field, string token) => TryGetAggregateLength(field, token, out _);

        private List<string> MergeAndMinimizeTokens(byte field, List<string> tokens)
        {
            if (_commonTokens.Count == 0) return tokens;

            int n = tokens.Count;
            long[] dp = new long[n + 1];
            string[] choice = new string[n];
            int[] nextIndex = new int[n];

            for (int i = 0; i <= n; i++) dp[i] = long.MaxValue;
            dp[n] = 0;

            for (int i = n - 1; i >= 0; i--)
            {
                string t = tokens[i];
                if (TryGetAggregateLength(field, t, out long aggLen))
                {
                    long cost = aggLen / 8;
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

                bool isFirstRare = !_commonTokens.Contains(t);
                string currentMerged = t;

                int maxWindow = 3;
                for (int j = 1; j < maxWindow && (i + j) < n; j++)
                {
                    string nextToken = tokens[i + j];
                    bool isNextRare = !_commonTokens.Contains(nextToken);
                    if (isFirstRare && isNextRare) break;
                    currentMerged += " " + nextToken;

                    if (TryGetAggregateLength(field, currentMerged, out long mergedLen))
                    {
                        if (dp[i + j + 1] != long.MaxValue)
                        {
                            long cost = (mergedLen / 8) + dp[i + j + 1];
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

        // Tokenize a query string, collapsing dual-emitted alternate surface forms.
        private List<string> TokenizeQuery(string query)
        {
            var rawTokens = new List<string>();
            var enumerator = _tokenizer.Tokenize(query.AsSpan()).GetEnumerator();
            uint lastIndex = uint.MaxValue;
            while (enumerator.MoveNext())
            {
                string tokenStr = enumerator.Current.ToString();
                if (rawTokens.Count > 0 && enumerator.CurrentIndex == lastIndex)
                {
                    rawTokens[rawTokens.Count - 1] = tokenStr;
                }
                else
                {
                    rawTokens.Add(tokenStr);
                    lastIndex = enumerator.CurrentIndex;
                }
            }
            return rawTokens;
        }

        // Legacy single-field phrase search (field 0).
        public List<uint> Search(string query) => SearchField(0, query);

        // Phrase search restricted to a single field.
        public List<uint> SearchField(byte field, string query)
        {
            if (_segments.Count == 0) return new List<uint>();

            var rawTokens = TokenizeQuery(query);
            if (rawTokens.Count == 0) return new List<uint>();

            var tokens = MergeAndMinimizeTokens(field, rawTokens);

            // Early exit: any token absent from every segment means zero results.
            foreach (var t in tokens)
            {
                if (!TokenExists(field, t)) return new List<uint>();
            }

            // Fast path: exactly one segment - identical to the previous single-index
            // implementation, no extra allocation, preserves SIMD performance.
            if (_segments.Count == 1)
            {
                return SearchSegment(_segments[0], field, tokens);
            }

            // Multi-segment path: run the same phrase-intersect per segment, union the
            // doc ids directly into a single aggregate buffer.
            var aggregate = new List<uint>();
            foreach (var seg in _segments)
            {
                SearchSegment(seg, field, tokens, aggregate);
            }
            return aggregate;
        }

        private List<uint> SearchSegment(SegmentReader seg, byte field, List<string> tokens)
        {
            var output = new List<uint>();
            SearchSegment(seg, field, tokens, output);
            return output;
        }

        private void SearchSegment(SegmentReader seg, byte field, List<string> tokens, List<uint> output)
        {
            var packedTokens = new List<(string Token, RoaringishPacked Packed)>();
            try
            {
                foreach (var token in tokens)
                {
                    if (!seg.Tokens.TryGet(field, token, out var offset))
                    {
                        return;
                    }
                    packedTokens.Add((token, seg.LoadPacked(field, token, offset)));
                }

                if (packedTokens.Count == 1)
                {
                    AppendDocIdsFiltered(packedTokens[0].Packed, seg.Deletes, output);
                    return;
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

                AppendDocIdsFiltered(result, seg.Deletes, output);
                result.Dispose();
            }
            finally
            {
                foreach (var pt in packedTokens) pt.Packed.Dispose();
            }
        }

        // Appends the unique doc ids from `packed` into `output`, skipping any doc id
        // present in `deletes`.
        private static void AppendDocIdsFiltered(RoaringishPacked packed, RoaringBitmap deletes, List<uint> output)
        {
            if (deletes.IsEmpty)
            {
                packed.GetDocIds(output);
                return;
            }

            var span = packed.AsSpan();
            if (span.Length == 0) return;

            uint lastDocId = RoaringishPacked.UnpackDocId(span[0]);
            if (!deletes.Contains(lastDocId)) output.Add(lastDocId);

            for (int i = 1; i < span.Length; i++)
            {
                uint docId = RoaringishPacked.UnpackDocId(span[i]);
                if (docId == lastDocId) continue;
                lastDocId = docId;
                if (!deletes.Contains(docId)) output.Add(docId);
            }
        }

        public RoaringishPacked Intersect(RoaringishPacked lhs, RoaringishPacked rhs, ushort lhsLenFull)
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

            int packedLen1 = 0;
            int msbLen1 = 0;

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
                var src = packedResult.AsSpan(0, packedLen1);
                var ret = new RoaringishPacked(packedLen1);
                var dst = ret.Buffer;
                for (int k = 0; k < src.Length; k++)
                {
                    if (RoaringishPacked.UnpackValues(src[k]) > 0)
                    {
                        dst.Add(src[k]);
                    }
                }
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

        public void Dispose()
        {
            foreach (var s in _segments) s.Dispose();
            if (_ownsDb) _db.Dispose();
        }

        public string GetDocument(uint docId) => _docStore.GetDocument(docId);

        // --- BM25 Implementation ---

        // Per-field doc length lookup. Backed by the doc_lengths column family.
        private int GetDocLength(uint docId, byte field) => _docLengthStore.GetLength(docId, field);

        // Legacy single-field BM25 search: tokens, no Query AST.
        public List<(uint DocId, float Score)> SearchBM25(string query, int k = 10, float k1 = 1.2f, float b = 0.75f)
            => SearchBM25(new TermsBM25Query(0, TokenizeQuery(query)), k, k1, b);

        public List<(uint DocId, float Score)> SearchBM25(Queries.Query query, int k = 10, float k1 = 1.2f, float b = 0.75f)
        {
            if (_segments.Count == 0) return new List<(uint, float)>();

            var ctx = new ScoringContext
            {
                K1 = k1,
                B = b,
                TotalDocs = _indexStats.TotalDocs,
            };

            var result = EvaluateScore(query, ctx, 1.0f);
            if (result == null) return new List<(uint, float)>();

            return result.OrderByDescending(kvp => kvp.Value).Take(k).Select(kvp => (kvp.Key, kvp.Value)).ToList();
        }

        // Boolean (non-scored) search over a composable Query tree.
        public List<uint> SearchBoolean(Queries.Query query)
        {
            if (query == null) return new List<uint>();
            var docs = EvaluateBoolean(query);
            return docs.OrderBy(x => x).ToList();
        }

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
            var results = EvaluateLegacyBoolean(root);
            return results.OrderBy(x => x).ToList();
        }

        public List<(uint DocId, float Score)> SearchBooleanBM25(string query, int k = 10, float k1 = 1.2f, float b = 0.75f)
        {
            var parser = new BooleanQueryParser();
            var root = parser.Parse(query);
            if (root == null) return new List<(uint, float)>();
            return SearchBooleanBM25(root, k, k1, b);
        }

        public List<(uint DocId, float Score)> SearchBooleanBM25(QueryNode root, int k = 10, float k1 = 1.2f, float b = 0.75f)
        {
            if (root == null) return new List<(uint, float)>();
            var ast = ConvertLegacyNode(root);
            return SearchBM25(ast, k, k1, b);
        }

        private Queries.Query ConvertLegacyNode(QueryNode node, byte field = 0)
        {
            switch (node)
            {
                case TermNode t:
                {
                    var tokens = TokenizeQuery(t.Term);
                    if (tokens.Count == 0) return new Queries.TermQuery(field, t.Term);
                    if (tokens.Count == 1) return new Queries.TermQuery(field, tokens[0]);
                    var subs = new Queries.Query[tokens.Count];
                    for (int i = 0; i < tokens.Count; i++) subs[i] = new Queries.TermQuery(field, tokens[i]);
                    return new Queries.AndQuery(subs);
                }
                case AndNode a:
                    return new Queries.AndQuery(ConvertLegacyNode(a.Left, field), ConvertLegacyNode(a.Right, field));
                case OrNode o:
                    return new Queries.OrQuery(ConvertLegacyNode(o.Left, field), ConvertLegacyNode(o.Right, field));
                case NotNode n:
                    return new Queries.NotQuery(ConvertLegacyNode(n.Child, field));
            }
            throw new ArgumentException($"Unknown QueryNode type: {node?.GetType().Name}");
        }

        private IEnumerable<uint> EvaluateLegacyBoolean(QueryNode node)
        {
            if (node is TermNode t) return Search(t.Term);
            if (node is AndNode a) return EvaluateLegacyBoolean(a.Left).Intersect(EvaluateLegacyBoolean(a.Right));
            if (node is OrNode o) return EvaluateLegacyBoolean(o.Left).Union(EvaluateLegacyBoolean(o.Right));
            if (node is NotNode n)
            {
                var childDocs = new HashSet<uint>(EvaluateLegacyBoolean(n.Child));
                return Enumerable.Range(0, (int)_indexStats.TotalDocs).Select(i => (uint)i).Where(id => !childDocs.Contains(id));
            }
            return Enumerable.Empty<uint>();
        }

        // ---- Query AST evaluation ----

        private HashSet<uint> EvaluateBoolean(Queries.Query query)
        {
            switch (query)
            {
                case Queries.TermQuery tq:
                    return new HashSet<uint>(SearchField(tq.Field, tq.Term));
                case Queries.PhraseQuery pq:
                    return new HashSet<uint>(SearchField(pq.Field, pq.Phrase));
                case Queries.AndQuery aq:
                {
                    if (aq.Clauses.Count == 0) return new HashSet<uint>();
                    HashSet<uint> running = null;
                    var negations = new List<Queries.NotQuery>();
                    foreach (var clause in aq.Clauses)
                    {
                        if (clause is Queries.NotQuery nq) { negations.Add(nq); continue; }
                        var docs = EvaluateBoolean(clause);
                        if (running == null) running = docs;
                        else running.IntersectWith(docs);
                        if (running.Count == 0) return running;
                    }
                    running ??= new HashSet<uint>();
                    foreach (var nq in negations)
                    {
                        var negDocs = EvaluateBoolean(nq.Child);
                        running.ExceptWith(negDocs);
                        if (running.Count == 0) return running;
                    }
                    return running;
                }
                case Queries.OrQuery oq:
                {
                    var union = new HashSet<uint>();
                    foreach (var clause in oq.Clauses) union.UnionWith(EvaluateBoolean(clause));
                    return union;
                }
                case Queries.NotQuery topNot:
                {
                    var negDocs = EvaluateBoolean(topNot.Child);
                    var result = new HashSet<uint>();
                    int total = (int)_indexStats.TotalDocs;
                    for (int i = 0; i < total; i++)
                    {
                        uint id = (uint)i;
                        if (!negDocs.Contains(id)) result.Add(id);
                    }
                    return result;
                }
                case Queries.BoostQuery bq:
                    return EvaluateBoolean(bq.Child);
            }
            return new HashSet<uint>();
        }

        private sealed class ScoringContext
        {
            public float K1;
            public float B;
            public uint TotalDocs;
        }

        private sealed class TermsBM25Query : Queries.Query
        {
            public byte Field { get; }
            public List<string> Terms { get; }
            public TermsBM25Query(byte field, List<string> terms) { Field = field; Terms = terms; }
            public override IEnumerable<Queries.Query> Children => Array.Empty<Queries.Query>();
        }

        private Dictionary<uint, float> EvaluateScore(Queries.Query query, ScoringContext ctx, float boost)
        {
            switch (query)
            {
                case TermsBM25Query terms:
                {
                    var scores = new Dictionary<uint, float>();
                    foreach (var t in terms.Terms)
                    {
                        ScorePhraseAsTokens(terms.Field, new List<string> { t }, boost, scores, ctx, restrictToDocs: null);
                    }
                    return scores;
                }
                case Queries.TermQuery tq:
                {
                    var scores = new Dictionary<uint, float>();
                    ScorePhraseAsTokens(tq.Field, new List<string> { tq.Term }, boost, scores, ctx, restrictToDocs: null);
                    return scores;
                }
                case Queries.PhraseQuery pq:
                {
                    var raw = TokenizeQuery(pq.Phrase);
                    if (raw.Count == 0) return new Dictionary<uint, float>();
                    var tokens = MergeAndMinimizeTokens(pq.Field, raw);
                    var scores = new Dictionary<uint, float>();
                    var matched = new HashSet<uint>(SearchField(pq.Field, pq.Phrase));
                    if (matched.Count == 0) return scores;
                    ScorePhraseAsTokens(pq.Field, tokens, boost, scores, ctx, restrictToDocs: matched);
                    return scores;
                }
                case Queries.BoostQuery bq:
                    return EvaluateScore(bq.Child, ctx, boost * bq.Boost);
                case Queries.AndQuery aq:
                {
                    var docSet = EvaluateBoolean(aq);
                    if (docSet.Count == 0) return new Dictionary<uint, float>();
                    var combined = new Dictionary<uint, float>();
                    foreach (var clause in aq.Clauses)
                    {
                        if (clause is Queries.NotQuery) continue;
                        var sub = EvaluateScore(clause, ctx, boost);
                        if (sub == null) continue;
                        foreach (var kvp in sub)
                        {
                            if (!docSet.Contains(kvp.Key)) continue;
                            ref float v = ref CollectionsMarshal.GetValueRefOrAddDefault(combined, kvp.Key, out _);
                            v += kvp.Value;
                        }
                    }
                    return combined;
                }
                case Queries.OrQuery oq:
                {
                    var combined = new Dictionary<uint, float>();
                    foreach (var clause in oq.Clauses)
                    {
                        var sub = EvaluateScore(clause, ctx, boost);
                        if (sub == null) continue;
                        foreach (var kvp in sub)
                        {
                            ref float v = ref CollectionsMarshal.GetValueRefOrAddDefault(combined, kvp.Key, out _);
                            v += kvp.Value;
                        }
                    }
                    return combined;
                }
                case Queries.NotQuery:
                    return new Dictionary<uint, float>();
            }
            return new Dictionary<uint, float>();
        }

        private void ScorePhraseAsTokens(byte field, List<string> tokens, float boost, Dictionary<uint, float> scores, ScoringContext ctx, HashSet<uint> restrictToDocs)
        {
            long N = ctx.TotalDocs;
            float avgDocLength = field < _avgDocLengthPerField.Length ? _avgDocLengthPerField[field] : 0f;
            if (avgDocLength <= 0) avgDocLength = 1f;

            foreach (var t in tokens)
            {
                int totalDocCount = 0;
                foreach (var seg in _segments)
                {
                    if (seg.Tokens.TryGet(field, t, out var off)) totalDocCount += off.DocCount;
                }
                if (totalDocCount == 0) continue;

                float idf = MathF.Log(1f + (N - totalDocCount + 0.5f) / (totalDocCount + 0.5f));
                if (idf < 0) idf = 0;

                foreach (var seg in _segments)
                {
                    if (!seg.Tokens.TryGet(field, t, out var offset)) continue;
                    using var packed = seg.LoadPacked(field, t, offset);
                    var freqs = packed.GetDocIdsAndFreqs();
                    foreach (var (docId, tf) in freqs)
                    {
                        if (!seg.Deletes.IsEmpty && seg.Deletes.Contains(docId)) continue;
                        if (restrictToDocs != null && !restrictToDocs.Contains(docId)) continue;

                        int docLen = GetDocLength(docId, field);
                        float norm = (tf + ctx.K1 * (1f - ctx.B + ctx.B * (docLen / avgDocLength)));
                        float score = boost * idf * (tf * (ctx.K1 + 1f)) / norm;

                        ref float scoreVal = ref CollectionsMarshal.GetValueRefOrAddDefault(scores, docId, out _);
                        scoreVal += score;
                    }
                }
            }
        }
    }
}
