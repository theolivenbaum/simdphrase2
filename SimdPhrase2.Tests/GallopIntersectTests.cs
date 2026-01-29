using Xunit;
using SimdPhrase2.Roaringish;
using SimdPhrase2.Roaringish.Intersect;
using System;
using System.Linq;

namespace SimdPhrase2.Tests
{
    public class GallopIntersectTests
    {
        [Fact]
        public void GallopFirst_ShouldMatchNaive()
        {
            var naive = new NaiveIntersect();
            var stats = new Stats();

            using var lhs = new RoaringishPacked();
            using var rhs = new RoaringishPacked();

            // LHS: 0, 100, 200, ...
            for (uint i = 0; i < 2000; i += 100)
            {
                lhs.Push(i, new uint[] { 0, 1 });
            }

            // RHS: 0..2000
            for (uint i = 0; i < 2000; i++)
            {
                rhs.Push(i, new uint[] { 1, 2 });
            }

            ushort lhsLen = 1;
            ulong addToGroup = 0;

            ushort msbMask = (ushort)(~((ushort)ushort.MaxValue >> lhsLen));
            ushort lsbMask = (ushort)(~((ushort)ushort.MaxValue << lhsLen));

            using var pResultNaive = new AlignedBuffer<ulong>(2000); pResultNaive.SetLength(2000);
            using var mResultNaive = new AlignedBuffer<ulong>(2000); mResultNaive.SetLength(2000);

            using var pResultGallop = new AlignedBuffer<ulong>(2000); pResultGallop.SetLength(2000);
            using var mResultGallop = new AlignedBuffer<ulong>(2000); mResultGallop.SetLength(2000);

            // Naive
            int lhsI_n = 0, rhsI_n = 0, i_n = 0, j_n = 0;
            naive.InnerIntersect(true, lhs.AsSpan(), rhs.AsSpan(), ref lhsI_n, ref rhsI_n, pResultNaive, ref i_n, mResultNaive, ref j_n, addToGroup, lhsLen, msbMask, lsbMask, stats);

            // Gallop
            int i_g = 0;
            GallopIntersectFirst.Intersect(true, lhs.AsSpan(), rhs.AsSpan(), pResultGallop, ref i_g, addToGroup, lhsLen, lsbMask, stats);

            int j_g = 0;
            GallopIntersectFirst.Intersect(false, lhs.AsSpan(), rhs.AsSpan(), mResultGallop, ref j_g, addToGroup, lhsLen, lsbMask, stats);

            // Compare results filtering out 0-value intersections from Naive
            var naiveResults = pResultNaive.AsSpan(0, i_n).ToArray().Where(x => RoaringishPacked.UnpackValues(x) > 0).ToArray();
            var gallopResults = pResultGallop.AsSpan(0, i_g).ToArray().Where(x => RoaringishPacked.UnpackValues(x) > 0).ToArray();

            Assert.True(naiveResults.SequenceEqual(gallopResults), "Packed results differ");

            // MSB results from Naive might also contain 0s?
            // Naive First Pass MSB Logic:
            // if ((lhsValues & msbMask) > 0) j++;
            // So it only adds if > 0.
            // But let's check.
            // msbPackedResult[j] = lhsPacked + RoaringishPacked.ADD_ONE_GROUP;
            // It writes anyway?
            // "msbPackedResult[j] = ...; if (..) j++;"
            // It writes to [j], then increments j only if condition met.
            // So subsequent write overwrites it.
            // So Naive MSB results are dense and filtered?
            // But wait, "lhs < rhs":
            // "msbPackedResult[j] = ...; if (..) j++;"
            // Yes.

            Assert.Equal(j_n, j_g);
            Assert.True(mResultNaive.AsSpan(0, j_n).SequenceEqual(mResultGallop.AsSpan(0, j_g)), "MSB results differ");
        }

        [Fact]
        public void GallopSecond_ShouldMatchNaive()
        {
            var naive = new NaiveIntersect();
            var stats = new Stats();

            using var lhs = new RoaringishPacked(); // Treated as MSB packed result from first pass
            using var rhs = new RoaringishPacked();

            // Construct mock MSB data
            for (uint i = 0; i < 2000; i += 50)
            {
                lhs.Push(i, new uint[] { 1 });
            }

            for (uint i = 0; i < 2000; i++)
            {
                rhs.Push(i, new uint[] { 2 });
            }

            ushort lhsLen = 1;
            ulong addToGroup = 0;

            ushort msbMask = (ushort)(~((ushort)ushort.MaxValue >> lhsLen));
            ushort lsbMask = (ushort)(~((ushort)ushort.MaxValue << lhsLen));

            using var pResultNaive = new AlignedBuffer<ulong>(2000); pResultNaive.SetLength(2000);
            using var mResultNaive = new AlignedBuffer<ulong>(2000); mResultNaive.SetLength(2000);

            using var pResultGallop = new AlignedBuffer<ulong>(2000); pResultGallop.SetLength(2000);

            // Naive Second Pass
            int lhsI_n = 0, rhsI_n = 0, i_n = 0, j_n = 0;
            naive.InnerIntersect(false, lhs.AsSpan(), rhs.AsSpan(), ref lhsI_n, ref rhsI_n, pResultNaive, ref i_n, mResultNaive, ref j_n, addToGroup, lhsLen, msbMask, lsbMask, stats);

            // Gallop Second Pass
            int i_g = 0;
            GallopIntersectSecond.Intersect(lhs.AsSpan(), rhs.AsSpan(), pResultGallop, ref i_g, lhsLen, lsbMask, stats);

            // Filter 0-value intersections from Naive (Gallop already filters)
            var naiveResults = pResultNaive.AsSpan(0, i_n).ToArray().Where(x => RoaringishPacked.UnpackValues(x) > 0).ToArray();
            var gallopResults = pResultGallop.AsSpan(0, i_g).ToArray();

            Assert.True(naiveResults.SequenceEqual(gallopResults), "Results differ");
        }
    }
}
