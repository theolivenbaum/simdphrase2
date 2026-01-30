using Xunit;
using SimdPhrase2.Roaringish;
using SimdPhrase2.Roaringish.Intersect;
using System.Linq;

namespace SimdPhrase2.Tests
{
    public class NaiveIntersectTests
    {
        [Fact]
        public void NaiveIntersect_BasicMatch_FirstPass()
        {
            // Setup
            var naive = new NaiveIntersect();
            var stats = new Stats();

            using var lhs = new RoaringishPacked();
            using var rhs = new RoaringishPacked();

            // Doc 1, positions 0, 1
            // 0 -> group 0, val 0. 1 -> group 0, val 1.
            lhs.Push(1, 0, 1);
            // Doc 1, positions 1, 2
            // 1 -> group 0, val 1. 2 -> group 0, val 2.
            rhs.Push(1, 1, 2);

            int lhsLen = 1;

            var lhsSpan = lhs.AsSpan();
            var rhsSpan = rhs.AsSpan();

            using var packedResult = new AlignedBuffer<ulong>(10);
            packedResult.SetLength(10);
            using var msbResult = new AlignedBuffer<ulong>(10);
            msbResult.SetLength(10);

            int lhsI = 0, rhsI = 0, i = 0, j = 0;

            naive.InnerIntersect(
                first: true,
                lhs: lhsSpan,
                rhs: rhsSpan,
                lhsI: ref lhsI,
                rhsI: ref rhsI,
                packedResult: packedResult,
                i: ref i,
                msbPackedResult: msbResult,
                j: ref j,
                addToGroup: 0,
                lhsLen: (ushort)lhsLen,
                msbMask: 0,
                lsbMask: 0,
                stats: stats
            );

            Assert.Equal(1, i);
            ulong result = packedResult[0];

            uint docId = RoaringishPacked.UnpackDocId(result);
            ushort values = RoaringishPacked.UnpackValues(result);

            Assert.Equal(1u, docId);

            // lhs values: bits 0, 1 set => 3.
            // rhs values: bits 1, 2 set => 6.
            // (3 << 1) = 6.
            // 6 & 6 = 6.
            Assert.Equal(6, values);
        }

        [Fact]
        public void NaiveIntersect_NoMatch()
        {
            var naive = new NaiveIntersect();
            var stats = new Stats();

            using var lhs = new RoaringishPacked();
            using var rhs = new RoaringishPacked();

            lhs.Push(1, 0); // bit 0
            rhs.Push(1, 5); // bit 5

            int lhsLen = 1; // shift 1

            var lhsSpan = lhs.AsSpan();
            var rhsSpan = rhs.AsSpan();

            using var packedResult = new AlignedBuffer<ulong>(10);
            packedResult.SetLength(10);
            using var msbResult = new AlignedBuffer<ulong>(10);
            msbResult.SetLength(10);

            int lhsI = 0, rhsI = 0, i = 0, j = 0;

            naive.InnerIntersect(
                first: true,
                lhs: lhsSpan,
                rhs: rhsSpan,
                lhsI: ref lhsI,
                rhsI: ref rhsI,
                packedResult: packedResult,
                i: ref i,
                msbPackedResult: msbResult,
                j: ref j,
                addToGroup: 0,
                lhsLen: (ushort)lhsLen,
                msbMask: 0,
                lsbMask: 0,
                stats: stats
            );

            Assert.Equal(1, i); // It matches DocID, so it writes a result, but intersection is 0.

            ulong result = packedResult[0];
            ushort values = RoaringishPacked.UnpackValues(result);

            // (1 << 1) = 2 (bit 1).
            // rhs has bit 5.
            // 2 & 32 = 0.
            Assert.Equal(0, values);
        }
    }
}
