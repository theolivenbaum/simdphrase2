using Xunit;
using SimdPhrase2.Roaringish;
using SimdPhrase2.Roaringish.Intersect;
using System;
using System.Linq;

namespace SimdPhrase2.Tests
{
    public class SimdIntersectTests
    {
        [Fact]
        public void SimdIntersect_ShouldMatchNaive_Basic()
        {
            var simd = new SimdIntersect();
            var naive = new NaiveIntersect();
            var stats = new Stats();

            using var lhs = new RoaringishPacked();
            using var rhs = new RoaringishPacked();

            // Populate with enough data to trigger SIMD block (need > 8 elements to be sure)
            // Let's add 20 elements.

            for (uint i = 0; i < 20; i++)
            {
                 lhs.Push(i, 0, 1); // doc i, pos 0, 1
                 rhs.Push(i, 1, 2); // doc i, pos 1, 2
            }

            int lhsLen = 1;

            using var pResultSimd = new AlignedBuffer<ulong>(100); pResultSimd.SetLength(100);
            using var mResultSimd = new AlignedBuffer<ulong>(100); mResultSimd.SetLength(100);

            using var pResultNaive = new AlignedBuffer<ulong>(100); pResultNaive.SetLength(100);
            using var mResultNaive = new AlignedBuffer<ulong>(100); mResultNaive.SetLength(100);

            int lhsI_s = 0, rhsI_s = 0, i_s = 0, j_s = 0;
            simd.InnerIntersect(true, lhs.AsSpan(), rhs.AsSpan(), ref lhsI_s, ref rhsI_s, pResultSimd, ref i_s, mResultSimd, ref j_s, 0, (ushort)lhsLen, 0, 0, stats);

            int lhsI_n = 0, rhsI_n = 0, i_n = 0, j_n = 0;
            naive.InnerIntersect(true, lhs.AsSpan(), rhs.AsSpan(), ref lhsI_n, ref rhsI_n, pResultNaive, ref i_n, mResultNaive, ref j_n, 0, (ushort)lhsLen, 0, 0, stats);

            Assert.Equal(i_n, i_s);
            Assert.Equal(j_n, j_s);

            var sSpan = pResultSimd.AsSpan(0, i_s);
            var nSpan = pResultNaive.AsSpan(0, i_n);

            Assert.True(sSpan.SequenceEqual(nSpan), "Packed results differ");
        }

        [Fact]
        public void SimdIntersect_MixedMatches()
        {
             var simd = new SimdIntersect();
            var naive = new NaiveIntersect();
            var stats = new Stats();

            using var lhs = new RoaringishPacked();
            using var rhs = new RoaringishPacked();

            // 0..9 match
            for (uint i = 0; i < 10; i++)
            {
                 lhs.Push(i, 0);
                 rhs.Push(i, 1);
            }
            // 10..19 lhs only
            for (uint i = 10; i < 20; i++)
            {
                 lhs.Push(i, 0);
            }
             // 20..29 rhs only
            for (uint i = 20; i < 30; i++)
            {
                 rhs.Push(i, 1);
            }
            // 30..39 match
            for (uint i = 30; i < 40; i++)
            {
                 lhs.Push(i, 0);
                 rhs.Push(i, 1);
            }

             int lhsLen = 1;

            using var pResultSimd = new AlignedBuffer<ulong>(100); pResultSimd.SetLength(100);
            using var mResultSimd = new AlignedBuffer<ulong>(100); mResultSimd.SetLength(100);

            using var pResultNaive = new AlignedBuffer<ulong>(100); pResultNaive.SetLength(100);
            using var mResultNaive = new AlignedBuffer<ulong>(100); mResultNaive.SetLength(100);

            int lhsI_s = 0, rhsI_s = 0, i_s = 0, j_s = 0;
            simd.InnerIntersect(true, lhs.AsSpan(), rhs.AsSpan(), ref lhsI_s, ref rhsI_s, pResultSimd, ref i_s, mResultSimd, ref j_s, 0, (ushort)lhsLen, 0, 0, stats);

            int lhsI_n = 0, rhsI_n = 0, i_n = 0, j_n = 0;
            naive.InnerIntersect(true, lhs.AsSpan(), rhs.AsSpan(), ref lhsI_n, ref rhsI_n, pResultNaive, ref i_n, mResultNaive, ref j_n, 0, (ushort)lhsLen, 0, 0, stats);

            Assert.Equal(i_n, i_s);
            Assert.Equal(j_n, j_s);

            var sSpan = pResultSimd.AsSpan(0, i_s);
            var nSpan = pResultNaive.AsSpan(0, i_n);

            Assert.True(sSpan.SequenceEqual(nSpan), "Packed results differ");
        }
    }
}
