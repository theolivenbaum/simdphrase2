using Xunit;
using SimdPhrase2.Roaringish;
using System.Collections.Generic;
using System.Linq;

namespace SimdPhrase2.Tests
{
    public class RoaringishPackedTests
    {
        [Fact]
        public void PackingHelpers_ShouldWork()
        {
            uint docId = 12345;
            ushort group = 10;
            ushort value = 5;

            ulong packedDocId = RoaringishPacked.PackDocId(docId);
            Assert.Equal((ulong)docId << 32, packedDocId);

            ulong packedGroup = RoaringishPacked.PackGroup(group);
            Assert.Equal((ulong)group << 16, packedGroup);

            ulong packedValue = RoaringishPacked.PackValue(value);
            Assert.Equal(1UL << value, packedValue);

            ulong packed = RoaringishPacked.Pack(packedDocId, group, value);

            Assert.Equal(docId, RoaringishPacked.UnpackDocId(packed));
            Assert.Equal(group, RoaringishPacked.UnpackGroup(packed));
            // UnpackValues returns the bitmap
            Assert.Equal(1UL << value, (ulong)RoaringishPacked.UnpackValues(packed));
        }

        [Fact]
        public void Push_ShouldPackCorrectly()
        {
            using var packed = new RoaringishPacked();
            uint docId = 1;
            var positions = new uint[] { 0, 1, 16, 32 };

            // 0 -> group 0, value 0 (bit 0)
            // 1 -> group 0, value 1 (bit 1)
            // 16 -> group 1, value 0 (bit 0)
            // 32 -> group 2, value 0 (bit 0)

            packed.Push(docId, positions);

            Assert.Equal(3, packed.Length);

            var span = packed.AsSpan();

            // First u64: docId 1, group 0, value 1 | 2 = 3
            ulong p0 = span[0];
            Assert.Equal(1u, RoaringishPacked.UnpackDocId(p0));
            Assert.Equal(0, RoaringishPacked.UnpackGroup(p0));
            Assert.Equal(3, RoaringishPacked.UnpackValues(p0));

            // Second u64: docId 1, group 1, value 1
            ulong p1 = span[1];
            Assert.Equal(1u, RoaringishPacked.UnpackDocId(p1));
            Assert.Equal(1, RoaringishPacked.UnpackGroup(p1));
            Assert.Equal(1, RoaringishPacked.UnpackValues(p1));

            // Third u64: docId 1, group 2, value 1
            ulong p2 = span[2];
            Assert.Equal(1u, RoaringishPacked.UnpackDocId(p2));
            Assert.Equal(2, RoaringishPacked.UnpackGroup(p2));
            Assert.Equal(1, RoaringishPacked.UnpackValues(p2));
        }
    }
}
