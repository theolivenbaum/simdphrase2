using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
        private FileStream _packedFile;
        private IIntersect _intersect;
        private Stats _stats;

        public Searcher(string indexName)
        {
            _indexName = indexName;
            _tokenStore = new TokenStore(indexName);
            _docStore = new DocumentStore(indexName);
            string packedPath = Path.Combine(indexName, "roaringish_packed.bin");
            if (File.Exists(packedPath))
            {
                _packedFile = File.OpenRead(packedPath);
            }
            _intersect = new SimdIntersect();
            _stats = new Stats();
        }

        public List<uint> Search(string query)
        {
            if (_packedFile == null) return new List<uint>();

            string normalized = Utils.Normalize(query);
            var tokens = Utils.Tokenize(normalized).ToList();

            if (tokens.Count == 0) return new List<uint>();

            var packedTokens = new List<(string Token, RoaringishPacked Packed)>();

            try
            {
                foreach (var token in tokens)
                {
                    if (!_tokenStore.TryGet(token, out var offset))
                    {
                        Console.WriteLine($"Token not found: {token}");
                        return new List<uint>();
                    }
                    Console.WriteLine($"Token found: {token}, Length: {offset.Length}");

                    int ulongCount = (int)(offset.Length / 8);
                    var buffer = new AlignedBuffer<ulong>(ulongCount);
                    buffer.SetLength(ulongCount);

                    _packedFile.Seek(offset.Begin, SeekOrigin.Begin);

                    Span<byte> byteSpan = MemoryMarshal.Cast<ulong, byte>(buffer.AsSpan());
                    _packedFile.ReadExactly(byteSpan);

                    packedTokens.Add((token, new RoaringishPacked(buffer, takeOwnership: true)));
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
            _intersect.InnerIntersect(true, lhs.AsSpan(), rhs.AsSpan(), ref lhsI, ref rhsI, packedResult, ref i, msbPackedResult, ref j, addToGroup, lhsLen, msbMask, lsbMask, _stats);

            int packedLen1 = i;
            int msbLen1 = j;

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

            int lhsI2 = 0, rhsI2 = 0, i2 = 0, j2 = 0;

            _intersect.InnerIntersect(false, msbPackedResult.AsSpan(0, msbLen1), rhs.AsSpan(), ref lhsI2, ref rhsI2, msbResult2, ref i2, dummy, ref j2, addToGroup, lhsLen, msbMask, lsbMask, _stats);

            int msbLen2 = i2;

            // Merge
            return RoaringishPacked.MergeResults(packedResult, packedLen1, msbResult2, msbLen2);
        }

        public void Dispose()
        {
            _tokenStore.Dispose();
            _docStore.Dispose();
            _packedFile?.Dispose();
        }

        public string GetDocument(uint docId) => _docStore.GetDocument(docId);
    }
}
