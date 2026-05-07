using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Buffers.Binary;
using SimdPhrase2.Db;
using SimdPhrase2.Roaringish;
using SimdPhrase2.Roaringish.Intersect;
using SimdPhrase2.Segments;
using SimdPhrase2.Storage;

namespace SimdPhrase2
{
    public class Searcher : IDisposable
    {
        private readonly string _indexName;
        private DocumentStore _docStore;
        private IIntersect _intersect;
        private Stats _stats;
        private HashSet<string> _commonTokens;
        private ITextTokenizer _tokenizer;
        private ISimdStorage _storage;

        private List<SegmentReader> _segments;
        // Aggregated total length per token across all segments, lazily filled and cached.
        // Used by MergeAndMinimizeTokens for cost estimation.
        private Dictionary<string, long> _tokenLengthCache;

        // BM25 / Boolean support
        private Stream? _docLengthsStream;
        private IndexStats _indexStats;
        private float _avgDocLength;

        public Searcher(string indexName, bool forceNaive = false, ITextTokenizer tokenizer = null, ISimdStorage storage = null)
        {
            _indexName = indexName;
            _tokenizer = tokenizer ?? new BasicTokenizer();
            _storage = storage ?? new FileSystemStorage();
            _docStore = new DocumentStore(indexName, _storage);
            _intersect = forceNaive ? new NaiveIntersect() : new SimdIntersect();
            _stats = new Stats();
            _commonTokens = CommonTokensPersistence.Load(_storage, _storage.Combine(indexName, "common_tokens.bin"));
            _tokenLengthCache = new Dictionary<string, long>();

            string docLengthsPath = _storage.Combine(indexName, "doc_lengths.bin");
            if (_storage.FileExists(docLengthsPath))
                _docLengthsStream = _storage.OpenRead(docLengthsPath);

            string statsPath = _storage.Combine(indexName, "index_stats.json");
            _indexStats = IndexStats.Load(_storage, statsPath);
            if (_indexStats.TotalDocs > 0)
                _avgDocLength = (float)_indexStats.TotalTokens / _indexStats.TotalDocs;

            _segments = new List<SegmentReader>();
            var manifest = SegmentManifest.Load(_storage, _indexName);
            foreach (var seg in manifest.Segments)
            {
                _segments.Add(new SegmentReader(_storage, _indexName, seg));
            }
        }

        // Aggregate token length across all segments. Used only for query planning.
        private bool TryGetAggregateLength(string token, out long totalLength)
        {
            if (_tokenLengthCache.TryGetValue(token, out totalLength))
            {
                return totalLength > 0;
            }
            long sum = 0;
            bool found = false;
            foreach (var seg in _segments)
            {
                if (seg.Tokens.TryGet(token, out var off))
                {
                    found = true;
                    sum += off.Length;
                }
            }
            _tokenLengthCache[token] = found ? sum : -1;
            totalLength = sum;
            return found;
        }

        // Returns true if the token exists in at least one segment.
        private bool TokenExists(string token) => TryGetAggregateLength(token, out _);

        private List<string> MergeAndMinimizeTokens(List<string> tokens)
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
                if (TryGetAggregateLength(t, out long aggLen))
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

                    if (TryGetAggregateLength(currentMerged, out long mergedLen))
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

        public List<uint> Search(string query)
        {
            if (_segments.Count == 0) return new List<uint>();

            var rawTokens = new List<string>();
            foreach (var t in _tokenizer.Tokenize(query.AsSpan()))
            {
                rawTokens.Add(t.ToString());
            }
            if (rawTokens.Count == 0) return new List<uint>();

            var tokens = MergeAndMinimizeTokens(rawTokens);

            // Early exit: any token absent from every segment means zero results.
            foreach (var t in tokens)
            {
                if (!TokenExists(t)) return new List<uint>();
            }

            // Fast path: exactly one segment - identical to the previous single-index
            // implementation, no extra allocation, preserves SIMD performance.
            if (_segments.Count == 1)
            {
                return SearchSegment(_segments[0], tokens);
            }

            // Multi-segment path: run the same phrase-intersect per segment, union the
            // doc ids. Tokens missing from a particular segment contribute zero docs
            // for that segment (intersect against an empty list is empty).
            var aggregate = new List<uint>();
            foreach (var seg in _segments)
            {
                var docs = SearchSegment(seg, tokens);
                if (docs.Count == 0) continue;
                if (seg.Deletes.IsEmpty)
                {
                    aggregate.AddRange(docs);
                }
                else
                {
                    foreach (var d in docs)
                    {
                        if (!seg.Deletes.Contains(d)) aggregate.Add(d);
                    }
                }
            }
            return aggregate;
        }

        // Run the phrase intersection for one segment. Mirrors the previous in-process
        // Search() body but scoped to a single segment's posting lists. The actual
        // SIMD intersect kernel (Intersect()) is unchanged.
        private List<uint> SearchSegment(SegmentReader seg, List<string> tokens)
        {
            var packedTokens = new List<(string Token, RoaringishPacked Packed)>();
            try
            {
                foreach (var token in tokens)
                {
                    if (!seg.Tokens.TryGet(token, out var offset))
                    {
                        return new List<uint>();
                    }
                    packedTokens.Add((token, seg.LoadPacked(offset)));
                }

                if (packedTokens.Count == 1)
                {
                    var docs = packedTokens[0].Packed.GetDocIds();
                    if (!seg.Deletes.IsEmpty)
                    {
                        docs = docs.Where(d => !seg.Deletes.Contains(d)).ToList();
                    }
                    return docs;
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
                if (!seg.Deletes.IsEmpty)
                {
                    docIds = docIds.Where(d => !seg.Deletes.Contains(d)).ToList();
                }
                return docIds;
            }
            finally
            {
                foreach (var pt in packedTokens) pt.Packed.Dispose();
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

        public void Dispose()
        {
            foreach (var s in _segments) s.Dispose();
            _docStore.Dispose();
            _docLengthsStream?.Dispose();
        }

        public string GetDocument(uint docId) => _docStore.GetDocument(docId);

        // --- BM25 Implementation ---

        private int GetDocLength(uint docId)
        {
            if (_docLengthsStream == null) return 0;
            long pos = (long)docId * 4;
            if (pos >= _docLengthsStream.Length) return 0;
            _docLengthsStream.Seek(pos, SeekOrigin.Begin);
            Span<byte> buffer = stackalloc byte[4];
            int read = _docLengthsStream.Read(buffer);
            if (read < 4) return 0;
            return BinaryPrimitives.ReadInt32LittleEndian(buffer);
        }

        public List<(uint DocId, float Score)> SearchBM25(string query, int k = 10, float k1 = 1.2f, float b = 0.75f)
        {
            if (_segments.Count == 0) return new List<(uint, float)>();

            var tokens = new List<string>();
            foreach (var t in _tokenizer.Tokenize(query.AsSpan()))
            {
                tokens.Add(t.ToString());
            }
            if (tokens.Count == 0) return new List<(uint, float)>();

            var scores = new Dictionary<uint, float>();
            long N = _indexStats.TotalDocs;
            float avgDocLength = _avgDocLength;

            foreach (var t in tokens)
            {
                // Aggregate doc count across segments.
                int totalDocCount = 0;
                foreach (var seg in _segments)
                {
                    if (seg.Tokens.TryGet(t, out var off)) totalDocCount += off.DocCount;
                }
                if (totalDocCount == 0) continue;

                float idf = MathF.Log(1f + (N - totalDocCount + 0.5f) / (totalDocCount + 0.5f));
                if (idf < 0) idf = 0;

                foreach (var seg in _segments)
                {
                    if (!seg.Tokens.TryGet(t, out var offset)) continue;
                    using var packed = seg.LoadPacked(offset);
                    var freqs = packed.GetDocIdsAndFreqs();
                    foreach (var (docId, tf) in freqs)
                    {
                        if (!seg.Deletes.IsEmpty && seg.Deletes.Contains(docId)) continue;
                        int docLen = GetDocLength(docId);
                        float score = idf * (tf * (k1 + 1f)) / (tf + k1 * (1f - b + b * (docLen / avgDocLength)));

                        ref float scoreVal = ref CollectionsMarshal.GetValueRefOrAddDefault(scores, docId, out _);
                        scoreVal += score;
                    }
                }
            }

            return scores.OrderByDescending(kvp => kvp.Value).Take(k).Select(kvp => (kvp.Key, kvp.Value)).ToList();
        }

        // --- Boolean Implementation ---

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
            if (node is TermNode t) return Search(t.Term);
            if (node is AndNode a) return Evaluate(a.Left).Intersect(Evaluate(a.Right));
            if (node is OrNode o) return Evaluate(o.Left).Union(Evaluate(o.Right));
            if (node is NotNode n)
            {
                var childDocs = new HashSet<uint>(Evaluate(n.Child));
                return Enumerable.Range(0, (int)_indexStats.TotalDocs).Select(i => (uint)i).Where(id => !childDocs.Contains(id));
            }
            return Enumerable.Empty<uint>();
        }
    }
}
