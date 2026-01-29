using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace SimdPhrase2.Roaringish.Intersect
{
    public static class GallopIntersectSecond
    {
        public static void Intersect(
            ReadOnlySpan<ulong> lhs,
            ReadOnlySpan<ulong> rhs,
            AlignedBuffer<ulong> packedResult,
            ref int i,
            ushort lhsLen,
            ushort lsbMask,
            Stats stats
        )
        {
            var sw = Stopwatch.StartNew();

            int lhsI = 0;
            int rhsI = 0;

            while (lhsI < lhs.Length && rhsI < rhs.Length)
            {
                int lhsDelta = 1;
                int rhsDelta = 1;

                while (lhsI < lhs.Length &&
                       RoaringishPacked.ClearValues(lhs[lhsI]) < RoaringishPacked.ClearValues(rhs[rhsI]))
                {
                    lhsI += lhsDelta;
                    lhsDelta *= 2;
                }
                lhsI -= lhsDelta / 2;

                while (rhsI < rhs.Length &&
                       RoaringishPacked.ClearValues(rhs[rhsI]) < RoaringishPacked.ClearValues(lhs[lhsI]))
                {
                    rhsI += rhsDelta;
                    rhsDelta *= 2;
                }
                rhsI -= rhsDelta / 2;

                ulong lhsPacked = lhs[lhsI];
                ulong rhsPacked = rhs[rhsI];

                ulong lhsDocIdGroup = RoaringishPacked.ClearValues(lhsPacked);
                ulong rhsDocIdGroup = RoaringishPacked.ClearValues(rhsPacked);

                if (lhsDocIdGroup < rhsDocIdGroup)
                {
                    lhsI++;
                }
                else if (lhsDocIdGroup > rhsDocIdGroup)
                {
                    rhsI++;
                }
                else
                {
                    ushort lhsValues = RoaringishPacked.UnpackValues(lhsPacked);
                    ushort rhsValues = RoaringishPacked.UnpackValues(rhsPacked);

                    int shift = lhsLen;
                    uint val = lhsValues;
                    uint rotated = (val << shift) | (val >> (16 - shift));

                    ulong intersection = (ulong)((rotated & 0xFFFF) & lsbMask & rhsValues);

                    if (intersection > 0)
                    {
                        packedResult[i++] = lhsDocIdGroup | intersection;
                    }

                    lhsI++;
                    rhsI++;
                }
            }

            sw.Stop();
            long micros = sw.ElapsedTicks / (TimeSpan.TicksPerMillisecond / 1000);
            if (micros == 0 && sw.ElapsedTicks > 0) micros = 1;
            stats.Add(ref stats.SecondIntersectGallop, micros);
        }
    }
}
