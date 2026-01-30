using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace SimdPhrase2.Roaringish
{
    public class RoaringishPacked : IDisposable
    {
        private AlignedBuffer<ulong> _buffer;
        private bool _ownsBuffer;

        public const uint MAX_VALUE = 16u * ushort.MaxValue;
        public const ulong ADD_ONE_GROUP = (ulong)ushort.MaxValue + 1;

        public RoaringishPacked()
        {
            _buffer = new AlignedBuffer<ulong>();
            _ownsBuffer = true;
        }

        public RoaringishPacked(int capacity)
        {
            _buffer = new AlignedBuffer<ulong>(capacity);
            _ownsBuffer = true;
        }

        public RoaringishPacked(AlignedBuffer<ulong> buffer, bool takeOwnership = false)
        {
            _buffer = buffer;
            _ownsBuffer = takeOwnership;
        }

        public void Push(uint docId, params IEnumerable<uint> positions)
        {
            ulong packedDocId = PackDocId(docId);

            using var enumerator = positions.GetEnumerator();
            if (!enumerator.MoveNext()) return;

            uint p = enumerator.Current;
            (ushort group, ushort value) = GetGroupAndValue(p);
            ulong packed = Pack(packedDocId, group, value);
            _buffer.Add(packed);

            while (enumerator.MoveNext())
            {
                p = enumerator.Current;
                (group, value) = GetGroupAndValue(p);
                ulong docIdGroup = PackDocIdGroup(packedDocId, group);
                ulong val = PackValue(value);
                packed = docIdGroup | val;

                ulong lastPacked = _buffer.Last();
                ulong lastDocIdGroup = ClearValues(lastPacked);

                if (lastDocIdGroup == docIdGroup)
                {
                    _buffer.Last() |= val;
                }
                else
                {
                    _buffer.Add(packed);
                }
            }
        }

        public int Length => _buffer.Length;
        public AlignedBuffer<ulong> Buffer => _buffer;
        public Span<ulong> AsSpan() => _buffer.AsSpan();

        public void Dispose()
        {
            if (_ownsBuffer)
            {
                _buffer.Dispose();
            }
        }

        // Static Helpers

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort Group(uint val) => (ushort)(val / 16);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort Value(uint val) => (ushort)(val % 16);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (ushort, ushort) GetGroupAndValue(uint val) => (Group(val), Value(val));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong PackDocId(uint docId) => (ulong)docId << 32;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong PackGroup(ushort group) => (ulong)group << 16;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong PackValue(ushort value) => 1UL << value;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong PackDocIdGroup(ulong packedDocId, ushort group) => packedDocId | PackGroup(group);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Pack(ulong packedDocId, ushort group, ushort value) => PackDocIdGroup(packedDocId, group) | PackValue(value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong ClearValues(ulong packed) => packed & ~0xFFFFUL;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong ClearGroupValues(ulong packed) => packed & ~0xFFFFFFFFUL;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint UnpackDocId(ulong packed) => (uint)(packed >> 32);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort UnpackGroup(ulong packed) => (ushort)(packed >> 16);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort UnpackValues(ulong packed) => (ushort)packed;

        public List<uint> GetDocIds()
        {
            var list = new List<uint>();
            if (_buffer.Length == 0) return list;

            var span = _buffer.AsSpan();
            uint lastDocId = UnpackDocId(span[0]);
            list.Add(lastDocId);

            for (int i = 1; i < span.Length; i++)
            {
                uint docId = UnpackDocId(span[i]);
                if (docId != lastDocId)
                {
                    list.Add(docId);
                    lastDocId = docId;
                }
            }
            return list;
        }

        public List<(uint DocId, int Freq)> GetDocIdsAndFreqs()
        {
            var list = new List<(uint DocId, int Freq)>();
            if (_buffer.Length == 0) return list;

            var span = _buffer.AsSpan();
            uint lastDocId = UnpackDocId(span[0]);
            int currentFreq = 0;

            for (int i = 0; i < span.Length; i++)
            {
                uint docId = UnpackDocId(span[i]);
                ushort values = UnpackValues(span[i]);
                int count = System.Numerics.BitOperations.PopCount(values);

                if (docId != lastDocId)
                {
                    list.Add((lastDocId, currentFreq));
                    lastDocId = docId;
                    currentFreq = count;
                }
                else
                {
                    currentFreq += count;
                }
            }
            list.Add((lastDocId, currentFreq));
            return list;
        }

        public static RoaringishPacked MergeResults(AlignedBuffer<ulong> packed, int packedLen, AlignedBuffer<ulong> msbPacked, int msbLen)
        {
             int capacity = packedLen + msbLen;
             var result = new RoaringishPacked(capacity);

             var pSpan = packed.AsSpan(0, packedLen);
             var mSpan = msbPacked.AsSpan(0, msbLen);

             int i = 0, j = 0;
             // Using iterators or indices.
             // pSpan is packedResult.
             // mSpan is msbPackedResult.

             // Iterate through packed
             while (i < packedLen)
             {
                 ulong pack = pSpan[i];
                 ulong docIdGroup = ClearValues(pack);
                 ushort values = UnpackValues(pack);

                 // write from msb while it's smaller
                 while (j < msbLen)
                 {
                     ulong msbPack = mSpan[j];
                     ulong msbDocIdGroup = ClearValues(msbPack);
                     ushort msbValues = UnpackValues(msbPack);

                     if (msbDocIdGroup >= docIdGroup) break;

                     j++;
                     if (msbValues > 0) result._buffer.Add(msbPack);
                 }

                 // Check overlap with current j
                 ulong msbPackOverlap = 0;
                 bool hasOverlap = false;

                 if (j < msbLen)
                 {
                     ulong msbPack = mSpan[j];
                     if (ClearValues(msbPack) == docIdGroup)
                     {
                         msbPackOverlap = msbPack;
                         hasOverlap = true;
                         j++;
                     }
                 }

                 bool write = values > 0;
                 if (write)
                 {
                     result._buffer.Add(pack);
                     if (hasOverlap)
                     {
                         result._buffer.Last() |= (ulong)UnpackValues(msbPackOverlap);
                     }
                 }
                 else if (hasOverlap && UnpackValues(msbPackOverlap) > 0)
                 {
                     result._buffer.Add(msbPackOverlap);
                 }

                 i++;
             }

             // Finish msb
             while (j < msbLen)
             {
                 if (UnpackValues(mSpan[j]) > 0) result._buffer.Add(mSpan[j]);
                 j++;
             }

             return result;
        }
    }
}
