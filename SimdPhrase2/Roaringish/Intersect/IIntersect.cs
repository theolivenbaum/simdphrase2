using System;
using SimdPhrase2.Roaringish;

namespace SimdPhrase2.Roaringish.Intersect
{
    public interface IIntersect
    {
        void InnerIntersect(
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
        );

        int IntersectionBufferSize(int lhsLen, int rhsLen);
    }
}
