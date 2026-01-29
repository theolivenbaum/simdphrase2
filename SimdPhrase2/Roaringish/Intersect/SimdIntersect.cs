using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Numerics;

namespace SimdPhrase2.Roaringish.Intersect
{
    public unsafe class SimdIntersect : IIntersect
    {
        private const int N = 8;

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
            if (!Avx512F.IsSupported)
            {
                new NaiveIntersect().InnerIntersect(first, lhs, rhs, ref lhsI, ref rhsI, packedResult, ref i, msbPackedResult, ref j, addToGroup, lhsLen, msbMask, lsbMask, stats);
                return;
            }

            var sw = Stopwatch.StartNew();

            Vector512<ulong> simdMsbMask = Vector512.Create((ulong)msbMask);
            Vector512<ulong> simdLsbMask = Vector512.Create((ulong)lsbMask);
            Vector512<ulong> simdAddToGroup = Vector512.Create(addToGroup);

            int endLhs = (lhs.Length / N) * N;
            int endRhs = (rhs.Length / N) * N;

            bool needToAnalyzeMsb = false;

            fixed (ulong* pLhsStart = lhs)
            fixed (ulong* pRhsStart = rhs)
            {
                while (lhsI < endLhs && rhsI < endRhs)
                {
                    ulong* pLhs = pLhsStart + lhsI;
                    ulong* pRhs = pRhsStart + rhsI;

                    ulong lhsLast = RoaringishPacked.ClearValues(pLhs[N - 1]) + (first ? addToGroup : 0);
                    ulong rhsLast = RoaringishPacked.ClearValues(pRhs[N - 1]);

                    Vector512<ulong> lhsPack = Vector512.Load(pLhs);
                    Vector512<ulong> rhsPack = Vector512.Load(pRhs);

                    if (first)
                    {
                        lhsPack = Avx512F.Add(lhsPack, simdAddToGroup);
                    }

                    Vector512<ulong> lhsDocIdGroup = ClearValuesSimd(lhsPack);
                    Vector512<ulong> rhsDocIdGroup = ClearValuesSimd(rhsPack);
                    Vector512<ulong> rhsValues = UnpackValuesSimd(rhsPack);

                    (byte lhsMask, byte rhsMask) = Vp2IntersectFallback(lhsDocIdGroup, rhsDocIdGroup);

                    if (first || lhsMask > 0)
                    {
                        Vector512<ulong> lhsPackCompress;
                        Vector512<ulong> rhsValuesCompress;

                        // Avx512Vbmi2 might be missing in some environments or versions, using fallback.
                        lhsPackCompress = CompressFallback(lhsPack, lhsMask);
                        rhsValuesCompress = CompressFallback(rhsValues, rhsMask);

                        Vector512<ulong> docIdGroupCompress = ClearValuesSimd(lhsPackCompress);
                        Vector512<ulong> lhsValuesCompress = UnpackValuesSimd(lhsPackCompress);

                        Vector512<ulong> intersection;
                        if (first)
                        {
                            intersection = Avx512F.And(
                                Avx512F.ShiftLeftLogical(lhsValuesCompress, (byte)lhsLen),
                                rhsValuesCompress
                            );
                        }
                        else
                        {
                             Vector512<ulong> rotated = RotlU16(lhsValuesCompress, lhsLen);
                             intersection = Avx512F.And(
                                 Avx512F.And(rotated, simdLsbMask),
                                 rhsValuesCompress
                             );
                        }

                        Vector512<ulong> result = Avx512F.Or(docIdGroupCompress, intersection);

                        // We must ensure we don't write OOB.
                        // i is index. buffer capacity is unknown here but we assume caller provided enough.
                        // We should store safely.
                        // Since we can write up to 8 values, and mask tells how many are valid.
                        // We can use StoreUnsafe if we are sure buffer is large enough.
                        // Or we should handle partial store?
                        // Rust stores 512 bits.
                        result.StoreUnsafe(ref packedResult[i]);

                        i += BitOperations.PopCount(lhsMask);
                    }

                    if (first)
                    {
                        if (lhsLast <= rhsLast)
                        {
                            AnalyzeMsb(lhsPack, msbPackedResult, ref j, simdMsbMask);
                            lhsI += N;
                        }
                    }
                    else
                    {
                         lhsI += N * (lhsLast <= rhsLast ? 1 : 0);
                    }
                    rhsI += N * (rhsLast <= lhsLast ? 1 : 0);
                    needToAnalyzeMsb = rhsLast < lhsLast;
                }

                 if (first && needToAnalyzeMsb && !(lhsI < lhs.Length && rhsI < rhs.Length))
                 {
                     if (lhsI < endLhs)
                     {
                        Vector512<ulong> lhsPack = Vector512.Load(pLhsStart + lhsI);
                        AnalyzeMsb(Avx512F.Add(lhsPack, simdAddToGroup), msbPackedResult, ref j, simdMsbMask);
                     }
                 }
            }

            // Naive fallback for remainder
            new NaiveIntersect().InnerIntersect(first, lhs, rhs, ref lhsI, ref rhsI, packedResult, ref i, msbPackedResult, ref j, addToGroup, lhsLen, msbMask, lsbMask, stats);

            sw.Stop();
             long micros = sw.ElapsedTicks / (TimeSpan.TicksPerMillisecond / 1000);
            if (micros == 0 && sw.ElapsedTicks > 0) micros = 1;

            if (first)
                stats.Add(ref stats.FirstIntersectSimd, micros);
            else
                stats.Add(ref stats.SecondIntersectSimd, micros);
        }

        public int IntersectionBufferSize(int lhsLen, int rhsLen)
        {
             return Math.Min(lhsLen, rhsLen) + 1 + N;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector512<ulong> ClearValuesSimd(Vector512<ulong> packed)
        {
            return Avx512F.And(packed, Vector512.Create(~0xFFFFUL));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector512<ulong> UnpackValuesSimd(Vector512<ulong> packed)
        {
            return Avx512F.And(packed, Vector512.Create(0xFFFFUL));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector512<ulong> RotlU16(Vector512<ulong> a, int i)
        {
             // p0 = a << i
             // p1 = a >> (16 - i)
             // (p0 | p1)
             // Logic: Vector shift works on elements (u64).
             // We want to rotate the lower 16 bits of each u64?
             // No, in Rust rotl_u16 takes Simd<u64> and does << and >>.
             // It operates on u64, but since values are u16 (unpacked), it works fine as long as we mask or don't care about upper bits?
             // Rust: "we don't need to unpack the values, since in the next step we already `and` with mask..."
             // So it treats them as u64 and shifts.
             // But (a << i) where a is u64 shifts all bits.
             // (a >> (16 - i)) where a is u64 shifts all bits.
             // If a had upper bits set (it shouldn't if unpacked), they would shift into the u16 area.
             // But `UnpackValuesSimd` ensures upper bits are 0.
             // So `a << i` moves bits up.
             // `a >> (16-i)` moves bits down? No.
             // If a is `0x00...00VVVV`, `a >> (16-i)` will be 0 unless `16-i` is small?
             // If i=1. `a >> 15`. It might have bits.
             // But wait, `UnpackValuesSimd` guarantees only lower 16 bits are set.
             // So `a` is small.
             // `a >> (16-i)`: if `16-i` < 64, it shifts right.
             // Since `a` has only 16 bits, if `16-i` > 16 (i < 0?), it becomes 0.
             // `i` is `lhsLen` (u16). 0..65535.
             // If `lhsLen` > 16?
             // See Naive implementation discussion.
             // I'll assume i < 16 or behavior mimics Rust.

             return Avx512F.Or(
                 Avx512F.ShiftLeftLogical(a, (byte)i),
                 Avx512F.ShiftRightLogical(a, (byte)(16 - i))
             );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AnalyzeMsb(Vector512<ulong> lhsPack, AlignedBuffer<ulong> msbPackedResult, ref int j, Vector512<ulong> msbMask)
        {
             var masked = Avx512F.And(lhsPack, msbMask);
             // Compare > 0.
             // Avx512F.CompareGreaterThan returns OpMask (mask) on Int64?
             // Actually `Vector512.GreaterThan` returns `Vector512<ulong>` (all ones for true).

             var cmp = Vector512.GreaterThan(masked, Vector512<ulong>.Zero);
             // ExtractMostSignificantBits for Vector512<ulong> returns ulong (bits 0-7 set).
             // Since .NET 8, `ExtractMostSignificantBits` on vector returns appropriate mask type.
             // For Vector512<ulong>, it returns `ulong` but only low 8 bits are relevant?
             // Actually documentation says it packs MSBs.
             // Since there are 8 elements, 8 bits.

             byte mask = (byte)cmp.ExtractMostSignificantBits();

             if (mask > 0)
             {
                 Vector512<ulong> packPlusOne = Avx512F.Add(lhsPack, Vector512.Create(RoaringishPacked.ADD_ONE_GROUP));

                 Vector512<ulong> compress = CompressFallback(packPlusOne, mask);
                 compress.StoreUnsafe(ref msbPackedResult[j]);

                 j += BitOperations.PopCount(mask);
             }
        }

        private static (byte, byte) Vp2IntersectFallback(Vector512<ulong> a, Vector512<ulong> b)
        {
             // Fallback implementation using AlignRight and Shuffle
             // m00 = cmpeq(a, b)
             var m00 = GetMask(Vector512.Equals(a, b));

             // b1 = swap pairs
             var b1 = Avx512F.Shuffle(b.AsInt32(), 0x4E).AsUInt64();
             var m01 = GetMask(Vector512.Equals(a, b1));

             // a1 = alignr 4 (2 ulongs)
             // AlignRight takes count in BYTES? No, documentation says:
             // "Concatenates a and b, and extracts byte-aligned result shifted to the right by count."
             // Wait, `_mm512_alignr_epi32` takes count in 32-bit words (integers).
             // `Avx512BW.AlignRight` takes count in bytes?
             // `Avx512F.AlignRight` takes count in 32-bit words?
             // Actually `Avx512F.AlignRight` is `valignd` (32-bit) or `valignq` (64-bit)?
             // `_mm512_alignr_epi32` is `valignd`.
             // In C#, `Avx512F.AlignRight` for `Vector512<int>` maps to `valignd`.
             // Rust used `alignr_epi32(a, a, 4)`.
             // So I can cast a to int, align right by 4, cast back.

             var aLong = a.AsInt64();

             // Emulate AlignRight(a, a, count) which is Rotate Right by count elements.
             // Count 2: Result[0] = a[2], Result[1] = a[3], ..., Result[6] = a[0], Result[7] = a[1].
             var idx1 = Vector512.Create(2L, 3, 4, 5, 6, 7, 0, 1);
             var idx2 = Vector512.Create(4L, 5, 6, 7, 0, 1, 2, 3);
             var idx3 = Vector512.Create(6L, 7, 0, 1, 2, 3, 4, 5);

             var a1 = Avx512F.PermuteVar8x64(aLong, idx1).AsUInt64();
             var a2 = Avx512F.PermuteVar8x64(aLong, idx2).AsUInt64();
             var a3 = Avx512F.PermuteVar8x64(aLong, idx3).AsUInt64();

             var m10 = GetMask(Vector512.Equals(a1, b));
             var m11 = GetMask(Vector512.Equals(a1, b1));

             var m20 = GetMask(Vector512.Equals(a2, b));
             var m21 = GetMask(Vector512.Equals(a2, b1));

             var m30 = GetMask(Vector512.Equals(a3, b));
             var m31 = GetMask(Vector512.Equals(a3, b1));

             // mask0 = m00 | m01 | (m10 | m11)<<2 | ...
             // Note: rotate_left(2) in Rust implies cyclic shift of the 8-bit mask?
             // Rust: `(m10 | m11).rotate_left(2)`.
             // `m10` is a mask (u8).
             // `u8::rotate_left(2)` is cyclic.

             byte mask0 = (byte)(
                 m00 | m01 |
                 RotateLeft8((byte)(m10 | m11), 2) |
                 RotateLeft8((byte)(m20 | m21), 4) |
                 RotateLeft8((byte)(m30 | m31), 6)
             );

             // m0 = m00 | m10 | m20 | m30
             // m1 = m01 | m11 | m21 | m31
             // mask1 = m0 | ((0x55 & m1) << 1) | ((m1 >> 1) & 0x55)

             byte m0 = (byte)(m00 | m10 | m20 | m30);
             byte m1 = (byte)(m01 | m11 | m21 | m31);
             byte mask1 = (byte)(m0 | ((0x55 & m1) << 1) | ((m1 >> 1) & 0x55));

             return (mask0, mask1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte GetMask(Vector512<ulong> cmp)
        {
            return (byte)cmp.ExtractMostSignificantBits();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte RotateLeft8(byte value, int count)
        {
            return (byte)((value << count) | (value >> (8 - count)));
        }

        // Compress Fallback
        private static Vector512<ulong> CompressFallback(Vector512<ulong> vector, byte mask)
        {
             // Compress: move elements where mask bit is 1 to contiguous lower elements.
             ulong* result = stackalloc ulong[8];
             ulong* vPtr = (ulong*)&vector;

             int idx = 0;
             for (int i = 0; i < 8; i++)
             {
                 if ((mask & (1 << i)) != 0)
                 {
                     result[idx++] = vPtr[i];
                 }
             }
             // Remaining are zeros (stackalloc doesn't zero, but we should)
             for(; idx < 8; idx++) result[idx] = 0;

             return Vector512.Load(result);
        }
    }
}
