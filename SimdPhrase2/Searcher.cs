using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Buffers.Binary;
using SimdPhrase2.Db;
using SimdPhrase2.Roaringish;
using SimdPhrase2.Roaringish.Intersect;
using SimdPhrase2.QueryModel;

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
        private ITextTokenizer _tokenizer;

        // BM25 / Boolean support
        internal FileStream? _docLengthsStream;
        internal IndexStats _indexStats;
        internal float _avgDocLength;

        public long TotalDocs => _indexStats.TotalDocs;
        public float AvgDocLength => _avgDocLength;

        public Searcher(string indexName, bool forceNaive = false, ITextTokenizer tokenizer = null)
        {
            _indexName = indexName;
            _tokenizer = tokenizer ?? new BasicTokenizer();
            _tokenStore = new TokenStore(indexName);
            _docStore = new DocumentStore(indexName, readOnly: true);
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
                _avgDocLength = (float)_indexStats.TotalTokens / _indexStats.TotalDocs;
        }

        private static void ReadExactlyAtOffset(FileStream fs, Span<byte> buffer, long offset)
        {
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                int read = RandomAccess.Read(fs.SafeFileHandle, buffer.Slice(totalRead), offset + totalRead);
                if (read == 0)
                    throw new EndOfStreamException();
                totalRead += read;
            }
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

             Span<byte> byteSpan = MemoryMarshal.Cast<ulong, byte>(buffer.AsSpan());
             ReadExactlyAtOffset(_packedFile!, byteSpan, offset.Begin);

             return new RoaringishPacked(buffer, takeOwnership: true);
        }

        public List<uint> Search(string query)
        {
            if (_packedFile == null) return new List<uint>();

            var rawTokens = new List<string>();
            foreach (var t in _tokenizer.Tokenize(query.AsSpan()))
            {
                rawTokens.Add(t.ToString());
            }

            if (rawTokens.Count == 0) return new List<uint>();

            var tokens = MergeAndMinimizeTokens(rawTokens);

            // Use PhraseQuery logic
            var q = new PhraseQuery(tokens);
            // SearchAll returns all docIds
            return SearchAll(q);
        }

        public List<(uint DocId, float Score)> Search(Query query, int n)
        {
            var results = new List<(uint DocId, float Score)>();
            if (_packedFile == null) return results;

            using var weight = query.CreateWeight(this, true);
            using var scorer = weight.GetScorer();

            if (scorer == null) return results;

            var pq = new PriorityQueue<(uint DocId, float Score), float>(n, Comparer<float>.Create((a, b) => a.CompareTo(b)));

            while (scorer.NextDoc() != Scorer.NO_MORE_DOCS)
            {
                float score = scorer.Score();
                uint docId = (uint)scorer.DocID();

                if (pq.Count < n)
                {
                    pq.Enqueue((docId, score), score);
                }
                else
                {
                    if (score > pq.Peek().Score)
                    {
                        pq.Dequeue();
                        pq.Enqueue((docId, score), score);
                    }
                }
            }

            while(pq.Count > 0)
            {
                results.Add(pq.Dequeue());
            }

            results.Reverse();
            return results;
        }

        public List<uint> SearchAll(Query query)
        {
            var results = new List<uint>();
            if (_packedFile == null) return results;

            using var weight = query.CreateWeight(this, false);
            using var scorer = weight.GetScorer();

            if (scorer == null) return results;

            while (scorer.NextDoc() != Scorer.NO_MORE_DOCS)
            {
                results.Add((uint)scorer.DocID());
            }
            return results;
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

        internal RoaringishPacked? GetPackedForTerm(string term, out long docCount)
        {
            docCount = 0;
            if (_tokenStore.TryGet(term, out var offset))
            {
                docCount = offset.DocCount;
                return LoadPacked(offset);
            }
            return null;
        }

        // --- BM25 Implementation ---

        internal int GetDocLength(uint docId)
        {
            if (_docLengthsStream == null) return 0;
            // docLengths is int32 array.
            long pos = (long)docId * 4;
            if (pos >= _docLengthsStream.Length) return 0;

            Span<byte> buffer = stackalloc byte[4];
            // Read exactly 4 bytes
            try
            {
                ReadExactlyAtOffset(_docLengthsStream, buffer, pos);
                return BinaryPrimitives.ReadInt32LittleEndian(buffer);
            }
            catch(EndOfStreamException)
            {
                return 0;
            }
        }

        public List<(uint DocId, float Score)> SearchBM25(string query, int k = 10, float k1 = 1.2f, float b = 0.75f)
        {
            if (_packedFile == null) return new List<(uint, float)>();

            var tokens = new List<string>();
            foreach(var t in _tokenizer.Tokenize(query.AsSpan()))
            {
                tokens.Add(t.ToString());
            }
            if (tokens.Count == 0) return new List<(uint, float)>();

            var bq = new BooleanQuery();
            foreach(var t in tokens)
            {
                bq.Add(new TermQuery(t), Occur.SHOULD);
            }

            return Search(bq, k);
        }

        // --- Boolean Implementation ---

        public List<uint> SearchBoolean(string query)
        {
             var parser = new BooleanQueryParser();
             var root = parser.Parse(query);
             if (root == null) return new List<uint>();
             return SearchAll(root);
        }
    }
}
