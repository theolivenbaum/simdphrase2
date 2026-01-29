using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace SimdPhrase2.Roaringish.Intersect
{
    public static class GallopIntersectFirst
    {
        public static void Intersect(
            bool first,
            ReadOnlySpan<ulong> lhs,
            ReadOnlySpan<ulong> rhs,
            AlignedBuffer<ulong> packedResult,
            ref int i,
            ulong addToGroup,
            ushort lhsLen,
            ushort lsbMask,
            Stats stats
        )
        {
            var sw = Stopwatch.StartNew();

            int lhsI = 0;
            int rhsI = 0;

            ulong extraAdd = first ? 0 : RoaringishPacked.ADD_ONE_GROUP;

            while (lhsI < lhs.Length && rhsI < rhs.Length)
            {
                int lhsDelta = 1;
                int rhsDelta = 1;

                while (lhsI < lhs.Length &&
                       (RoaringishPacked.ClearValues(lhs[lhsI]) + addToGroup + extraAdd) < RoaringishPacked.ClearValues(rhs[rhsI]))
                {
                    lhsI += lhsDelta;
                    lhsDelta *= 2;
                }
                lhsI -= lhsDelta / 2;

                while (rhsI < rhs.Length &&
                       RoaringishPacked.ClearValues(rhs[rhsI]) <
                       (RoaringishPacked.ClearValues(lhs[lhsI]) + addToGroup + extraAdd))
                {
                    rhsI += rhsDelta;
                    rhsDelta *= 2;
                }
                rhsI -= rhsDelta / 2;

                ulong lhsPacked = lhs[lhsI] + addToGroup + extraAdd;
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

                    ulong intersection;
                    if (first)
                    {
                        intersection = (ulong)((lhsValues << lhsLen) & rhsValues);
                    }
                    else
                    {
                        // Rotate left 16-bit value: (x << n) | (x >> (16-n))
                        // But we are working with ushort in ulong container.
                        // Rust rotate_left on u16 (promoted to u32 effectively?)
                        // "lhs_values.rotate_left(lhs_len as u32) & lsb_mask & rhs_values"
                        // C# ushort doesn't have RotateLeft.
                        int shift = lhsLen;
                        uint val = lhsValues;
                        uint rotated = (val << shift) | (val >> (16 - shift));
                        // Since val is uint, << shift might go beyond 16 bits.
                        // We need to mask it back to 16 bits?
                        // (val << shift) produces bits up to bit 31.
                        // (val >> (16 - shift)) uses lower bits.
                        // We strictly want 16-bit rotation.
                        // Example: val = 0x8000 (bit 15 set). shift = 1.
                        // val << 1 = 0x10000.
                        // val >> 15 = 0x1.
                        // result = 0x10001.
                        // We only want 0x0001.
                        // So we should mask with 0xFFFF.

                        intersection = (ulong)((rotated & 0xFFFF) & lsbMask & rhsValues);
                    }

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
            stats.Add(ref stats.FirstIntersectGallop, micros);
        }
    }
}
