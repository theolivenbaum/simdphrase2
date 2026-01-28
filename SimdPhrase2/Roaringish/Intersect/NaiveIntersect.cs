using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace SimdPhrase2.Roaringish.Intersect
{
    public class NaiveIntersect : IIntersect
    {
        public void InnerIntersect(
            bool first,
            ReadOnlySpan<ulong> lhs,
            ReadOnlySpan<ulong> rhs,
            ref int lhsI,
            ref int rhsI,
            AlignedBuffer<ulong> packedResult,
            ref int i,
            AlignedBuffer<ulong> msbPackedResult,
            ref int j,
            ulong addToGroup,
            ushort lhsLen,
            ushort msbMask,
            ushort lsbMask,
            Stats stats
        )
        {
            var sw = Stopwatch.StartNew();

            while (lhsI < lhs.Length && rhsI < rhs.Length)
            {
                ulong lhsPacked = lhs[lhsI] + (first ? addToGroup : 0);
                ulong lhsDocIdGroup = RoaringishPacked.ClearValues(lhsPacked);
                ushort lhsValues = RoaringishPacked.UnpackValues(lhsPacked);

                ulong rhsPacked = rhs[rhsI];
                ulong rhsDocIdGroup = RoaringishPacked.ClearValues(rhsPacked);
                ushort rhsValues = RoaringishPacked.UnpackValues(rhsPacked);

                if (lhsDocIdGroup == rhsDocIdGroup)
                {
                    if (first)
                    {
                        // (lhs_values << lhs_len) & rhs_values
                        ushort intersection = (ushort)((lhsValues << lhsLen) & rhsValues);

                        packedResult[i] = lhsDocIdGroup | (ulong)intersection;

                        msbPackedResult[j] = lhsPacked + RoaringishPacked.ADD_ONE_GROUP;

                        if ((lhsValues & msbMask) > 0)
                        {
                            j++;
                        }
                    }
                    else
                    {
                        ushort rotated = RotateLeft(lhsValues, lhsLen);
                        ushort intersection = (ushort)(rotated & lsbMask & rhsValues);

                        packedResult[i] = lhsDocIdGroup | (ulong)intersection;
                    }
                    i++;
                    lhsI++;
                    rhsI++;
                }
                else if (lhsDocIdGroup > rhsDocIdGroup)
                {
                    rhsI++;
                }
                else // lhs < rhs
                {
                    if (first)
                    {
                        msbPackedResult[j] = lhsPacked + RoaringishPacked.ADD_ONE_GROUP;
                        if ((lhsValues & msbMask) > 0)
                        {
                            j++;
                        }
                    }
                    lhsI++;
                }
            }

            sw.Stop();
            long micros = sw.ElapsedTicks / (TimeSpan.TicksPerMillisecond / 1000);
            if (micros == 0 && sw.ElapsedTicks > 0) micros = 1; // avoid 0 if it was fast but not 0

            if (first)
                stats.Add(ref stats.FirstIntersectNaive, micros);
            else
                stats.Add(ref stats.SecondIntersectNaive, micros);
        }

        public int IntersectionBufferSize(int lhsLen, int rhsLen)
        {
            return Math.Min(lhsLen, rhsLen);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort RotateLeft(ushort value, int count)
        {
            count &= 0xF;
            return (ushort)((value << count) | (value >> (16 - count)));
        }
    }
}
