using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Buffers.Binary;
using SimdPhrase2.Db;
using SimdPhrase2.Roaringish;
using SimdPhrase2.Roaringish.Intersect;

namespace SimdPhrase2
{
    public class Searcher : IDisposable
    {
        private readonly string _indexName;
        private TokenStore _tokenStore;
        private DocumentStore _docStore;
        private FileStream? _packedFile;
        private IIntersect _intersect;
        private Stats _stats;
        private HashSet<string> _commonTokens;

        // BM25 / Boolean support
        private FileStream? _docLengthsStream;
        private IndexStats _indexStats;
        private double _avgDocLength;

        public Searcher(string indexName, bool forceNaive = false)
        {
            _indexName = indexName;
            _tokenStore = new TokenStore(indexName);
            _docStore = new DocumentStore(indexName);
            string packedPath = Path.Combine(indexName, "roaringish_packed.bin");
            if (File.Exists(packedPath))
            {
                _packedFile = File.OpenRead(packedPath);
            }
            _intersect = forceNaive ? new NaiveIntersect() :  new SimdIntersect();
            _stats = new Stats();
            _commonTokens = CommonTokensPersistence.Load(Path.Combine(indexName, "common_tokens.bin"));

            string docLengthsPath = Path.Combine(indexName, "doc_lengths.bin");
            if (File.Exists(docLengthsPath))
                _docLengthsStream = File.OpenRead(docLengthsPath);

            string statsPath = Path.Combine(indexName, "index_stats.json");
            _indexStats = IndexStats.Load(statsPath);
            if (_indexStats.TotalDocs > 0)
                _avgDocLength = (double)_indexStats.TotalTokens / _indexStats.TotalDocs;
        }

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
                // Try single token
                string t = tokens[i];
                if (_tokenStore.TryGet(t, out var offset))
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
                     // Token not found.
                     // This means searching will fail anyway.
                     // We prefer to fail fast with this token.
                     dp[i] = 0;
                     choice[i] = t;
                     nextIndex[i] = i + 1;
                }

                // Try merging
                bool isFirstRare = !_commonTokens.Contains(t);
                string currentMerged = t;

                int maxWindow = 3;
                for (int j = 1; j < maxWindow && (i + j) < n; j++)
                {
                    string nextToken = tokens[i+j];
                    bool isNextRare = !_commonTokens.Contains(nextToken);

                    if (isFirstRare && isNextRare) break;

                    currentMerged += " " + nextToken;

                    if (_tokenStore.TryGet(currentMerged, out offset))
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
                if (choice[curr] == null) return tokens; // Should not happen
                result.Add(choice[curr]);
                curr = nextIndex[curr];
            }
            return result;
        }

        private RoaringishPacked LoadPacked(FileOffset offset)
        {
             int ulongCount = (int)(offset.Length / 8);
             var buffer = new AlignedBuffer<ulong>(ulongCount);
             buffer.SetLength(ulongCount);

             _packedFile.Seek(offset.Begin, SeekOrigin.Begin);

             Span<byte> byteSpan = MemoryMarshal.Cast<ulong, byte>(buffer.AsSpan());
             _packedFile.ReadExactly(byteSpan);

             return new RoaringishPacked(buffer, takeOwnership: true);
        }

        public List<uint> Search(string query)
        {
            if (_packedFile == null) return new List<uint>();

            string normalized = Utils.Normalize(query);
            var rawTokens = Utils.Tokenize(normalized).ToList();

            if (rawTokens.Count == 0) return new List<uint>();

            var tokens = MergeAndMinimizeTokens(rawTokens);

            var packedTokens = new List<(string Token, RoaringishPacked Packed)>();

            try
            {
                foreach (var token in tokens)
                {
                    if (!_tokenStore.TryGet(token, out var offset))
                    {
                        // Console.WriteLine($"Token not found: {token}");
                        return new List<uint>();
                    }

                    packedTokens.Add((token, LoadPacked(offset)));
                }

                if (packedTokens.Count == 1)
                {
                     return packedTokens[0].Packed.GetDocIds();
                }

                int bestIdx = 0;
                long minLen = long.MaxValue;

                for (int i = 0; i < packedTokens.Count - 1; i++)
                {
                    long len = packedTokens[i].Packed.Length + packedTokens[i+1].Packed.Length;
                    if (len < minLen)
                    {
                        minLen = len;
                        bestIdx = i;
                    }
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

                    oldResult.Dispose(); // Free intermediate result

                    if (result.Length == 0) break;
                }

                var docIds = result.GetDocIds();
                result.Dispose();
                return docIds;
            }
            finally
            {
                foreach(var pt in packedTokens) pt.Packed.Dispose();
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
            using var msbPackedResult = new AlignedBuffer<ulong>(lhs.Length + 1); // Rust uses lhs.len() + 1
            msbPackedResult.SetLength(lhs.Length + 1);

            int lhsI = 0, rhsI = 0, i = 0, j = 0;

            // First Pass
            int packedLen1 = 0;
            int msbLen1 = 0;

            // Check proportion for Gallop First Pass
            // Avoid division by zero
            int minLen = Math.Min(lhs.Length, rhs.Length);
            int maxLen = Math.Max(lhs.Length, rhs.Length);
            int proportion = minLen > 0 ? maxLen / minLen : 0;

            if (proportion >= 650)
            {
                GallopIntersectFirst.Intersect(true, lhs.AsSpan(), rhs.AsSpan(), packedResult, ref i, addToGroup, lhsLen, lsbMask, _stats);
                packedLen1 = i;

                // We need to run it again for msb part (first=false logic equivalent for GallopFirst)
                // Note: GallopIntersectFirst handles first=false logic too
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

            // Second Pass
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

            // Merge
            return RoaringishPacked.MergeResults(packedResult, packedLen1, msbResult2, msbLen2);
        }

        public void Dispose()
        {
            _tokenStore.Dispose();
            _docStore.Dispose();
            _packedFile?.Dispose();
            _docLengthsStream?.Dispose();
        }

        public string GetDocument(uint docId) => _docStore.GetDocument(docId);

        // --- BM25 Implementation ---

        private int GetDocLength(uint docId)
        {
            if (_docLengthsStream == null) return 0;
            // docLengths is int32 array.
            long pos = (long)docId * 4;
            if (pos >= _docLengthsStream.Length) return 0;

            _docLengthsStream.Seek(pos, SeekOrigin.Begin);
            Span<byte> buffer = stackalloc byte[4];
            int read = _docLengthsStream.Read(buffer);
            if (read < 4) return 0;
            return BinaryPrimitives.ReadInt32LittleEndian(buffer);
        }

        public List<(uint DocId, double Score)> SearchBM25(string query, int k = 10, double k1 = 1.2, double b = 0.75)
        {
            if (_packedFile == null) return new List<(uint, double)>();

            string normalized = Utils.Normalize(query);
            var tokens = Utils.Tokenize(normalized).ToList();
            if (tokens.Count == 0) return new List<(uint, double)>();

            var scores = new Dictionary<uint, double>();
            long N = _stats != null ? _indexStats.TotalDocs : 0; // Using _indexStats

            foreach(var t in tokens)
            {
                if (_tokenStore.TryGet(t, out var offset))
                {
                     double idf = Math.Log(1 + (N - offset.DocCount + 0.5) / (offset.DocCount + 0.5));
                     if (idf < 0) idf = 0;

                     using var packed = LoadPacked(offset);
                     var freqs = packed.GetDocIdsAndFreqs();
                     foreach(var (docId, tf) in freqs)
                     {
                         int docLen = GetDocLength(docId);
                         double score = idf * (tf * (k1 + 1)) / (tf + k1 * (1 - b + b * (docLen / _avgDocLength)));

                         if (!scores.ContainsKey(docId)) scores[docId] = 0;
                         scores[docId] += score;
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

             // Evaluate and sort
             var results = Evaluate(root);
             return results.OrderBy(x => x).ToList();
        }

        private IEnumerable<uint> Evaluate(QueryNode node)
        {
            if (node is TermNode t)
            {
                 // Use Search() to handle phrase search if term is multiple words?
                 // Parser produces single words.
                 // But if we want to support phrases in future, Search() is good.
                 // Also, Search() handles normalization/tokenization of the term properly.
                 return Search(t.Term);
            }
            if (node is AndNode a)
            {
                return Evaluate(a.Left).Intersect(Evaluate(a.Right));
            }
            if (node is OrNode o)
            {
                return Evaluate(o.Left).Union(Evaluate(o.Right));
            }
            if (node is NotNode n)
            {
                 // Calculate AllDocs - Child
                 // We need to materialize child to HashSet for efficient check
                 var childDocs = new HashSet<uint>(Evaluate(n.Child));
                 return Enumerable.Range(0, (int)_indexStats.TotalDocs).Select(i => (uint)i).Where(id => !childDocs.Contains(id));
            }
            return Enumerable.Empty<uint>();
        }
    }
}
