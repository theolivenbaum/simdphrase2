using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SimdPhrase2.Segments
{
    // A compact roaring bitmap for storing a set of uint32 values.
    // Splits each value into a 16-bit high key and 16-bit low value. Each high key
    // maps to a container that stores the low values. Two container kinds are used:
    //   - ArrayContainer: a sorted ushort[] for sparse data (cardinality <= 4096)
    //   - BitmapContainer: a ulong[1024] (65536 bits) for dense data
    // Containers self-promote / demote when crossing the 4096 cardinality boundary.
    //
    // Used by segments to mark deleted local doc IDs efficiently:
    //   - O(1) Contains for dense ranges, O(log n) for sparse ranges
    //   - Compact on-disk representation
    //   - Efficient bulk merge during segment compaction
    public sealed class RoaringBitmap
    {
        private const int ArrayMax = 4096; // promote to bitmap above this

        private readonly Dictionary<ushort, IContainer> _containers = new();
        private long _cardinality;

        public long Cardinality => _cardinality;
        public bool IsEmpty => _cardinality == 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (ushort hi, ushort lo) Split(uint v) => ((ushort)(v >> 16), (ushort)v);

        public bool Add(uint value)
        {
            var (hi, lo) = Split(value);
            if (!_containers.TryGetValue(hi, out var c))
            {
                c = new ArrayContainer();
                _containers[hi] = c;
            }
            bool added = c.Add(lo);
            if (added)
            {
                _cardinality++;
                if (c is ArrayContainer ac && ac.Cardinality > ArrayMax)
                {
                    _containers[hi] = ac.ToBitmap();
                }
            }
            return added;
        }

        public bool Contains(uint value)
        {
            var (hi, lo) = Split(value);
            return _containers.TryGetValue(hi, out var c) && c.Contains(lo);
        }

        public void UnionWith(RoaringBitmap other)
        {
            foreach (var kvp in other._containers)
            {
                if (!_containers.TryGetValue(kvp.Key, out var existing))
                {
                    _containers[kvp.Key] = kvp.Value.Clone();
                }
                else
                {
                    long before = existing.Cardinality;
                    var merged = existing.Or(kvp.Value);
                    _containers[kvp.Key] = merged;
                    _cardinality += merged.Cardinality - before;
                    continue;
                }
                _cardinality += kvp.Value.Cardinality;
            }
        }

        public IEnumerable<uint> Iterate()
        {
            // Iterate in sorted order of high keys for predictable output.
            var keys = new List<ushort>(_containers.Keys);
            keys.Sort();
            foreach (var hi in keys)
            {
                uint baseVal = (uint)hi << 16;
                foreach (ushort lo in _containers[hi].Iterate())
                {
                    yield return baseVal | lo;
                }
            }
        }

        public void Save(Stream stream)
        {
            using var bw = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            bw.Write((byte)1); // version
            bw.Write(_containers.Count);
            // Save in sorted order so reads are deterministic.
            var keys = new List<ushort>(_containers.Keys);
            keys.Sort();
            foreach (var hi in keys)
            {
                bw.Write(hi);
                _containers[hi].Save(bw);
            }
        }

        public static RoaringBitmap Load(Stream stream)
        {
            var bm = new RoaringBitmap();
            if (stream.Length == 0) return bm;
            using var br = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            byte version = br.ReadByte();
            if (version != 1) throw new IOException($"Unsupported RoaringBitmap version: {version}");
            int count = br.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                ushort hi = br.ReadUInt16();
                IContainer c = IContainer.Load(br);
                bm._containers[hi] = c;
                bm._cardinality += c.Cardinality;
            }
            return bm;
        }

        // Convenience byte[] helpers for callers (e.g. RocksDB-backed stores)
        // that already have the bitmap as a flat byte[] - avoids wrapping in a
        // MemoryStream at every call site.
        public static RoaringBitmap LoadBytes(byte[] bytes)
        {
            using var ms = new MemoryStream(bytes, writable: false);
            return Load(ms);
        }

        public byte[] SaveToBytes()
        {
            using var ms = new MemoryStream();
            Save(ms);
            return ms.ToArray();
        }

        // ---------------- Containers ----------------

        internal interface IContainer
        {
            int Cardinality { get; }
            bool Add(ushort v);
            bool Contains(ushort v);
            IContainer Or(IContainer other);
            IContainer Clone();
            IEnumerable<ushort> Iterate();
            void Save(BinaryWriter bw);

            static IContainer Load(BinaryReader br)
            {
                byte kind = br.ReadByte();
                if (kind == 0)
                {
                    int n = br.ReadInt32();
                    var arr = new ushort[n];
                    for (int i = 0; i < n; i++) arr[i] = br.ReadUInt16();
                    return new ArrayContainer(arr);
                }
                if (kind == 1)
                {
                    int card = br.ReadInt32();
                    var bits = new ulong[1024];
                    var bytes = MemoryMarshal.AsBytes(bits.AsSpan());
                    int read = br.Read(bytes);
                    if (read != bytes.Length) throw new EndOfStreamException();
                    return new BitmapContainer(bits, card);
                }
                throw new IOException($"Unknown container kind: {kind}");
            }
        }

        internal sealed class ArrayContainer : IContainer
        {
            private ushort[] _data;
            private int _count;

            public ArrayContainer() { _data = new ushort[8]; }
            public ArrayContainer(ushort[] sorted) { _data = sorted; _count = sorted.Length; }

            public int Cardinality => _count;

            public bool Contains(ushort v)
            {
                int idx = Array.BinarySearch(_data, 0, _count, v);
                return idx >= 0;
            }

            public bool Add(ushort v)
            {
                int idx = Array.BinarySearch(_data, 0, _count, v);
                if (idx >= 0) return false;
                int insert = ~idx;
                if (_count == _data.Length)
                {
                    Array.Resize(ref _data, Math.Max(_data.Length * 2, 8));
                }
                if (insert < _count)
                {
                    Array.Copy(_data, insert, _data, insert + 1, _count - insert);
                }
                _data[insert] = v;
                _count++;
                return true;
            }

            public IContainer Or(IContainer other)
            {
                if (other is ArrayContainer ac)
                {
                    // Merge two sorted arrays.
                    var merged = new List<ushort>(_count + ac._count);
                    int i = 0, j = 0;
                    while (i < _count && j < ac._count)
                    {
                        ushort a = _data[i], b = ac._data[j];
                        if (a == b) { merged.Add(a); i++; j++; }
                        else if (a < b) { merged.Add(a); i++; }
                        else { merged.Add(b); j++; }
                    }
                    while (i < _count) merged.Add(_data[i++]);
                    while (j < ac._count) merged.Add(ac._data[j++]);
                    if (merged.Count > ArrayMax)
                    {
                        var bm = new BitmapContainer();
                        foreach (var v in merged) bm.Add(v);
                        return bm;
                    }
                    return new ArrayContainer(merged.ToArray());
                }
                else
                {
                    // Bitmap | array: clone bitmap, add each
                    var clone = (BitmapContainer)other.Clone();
                    for (int k = 0; k < _count; k++) clone.Add(_data[k]);
                    return clone;
                }
            }

            public IContainer Clone()
            {
                var copy = new ushort[_count];
                Array.Copy(_data, copy, _count);
                return new ArrayContainer(copy);
            }

            public IEnumerable<ushort> Iterate()
            {
                for (int i = 0; i < _count; i++) yield return _data[i];
            }

            internal BitmapContainer ToBitmap()
            {
                var bm = new BitmapContainer();
                for (int i = 0; i < _count; i++) bm.Add(_data[i]);
                return bm;
            }

            public void Save(BinaryWriter bw)
            {
                bw.Write((byte)0);
                bw.Write(_count);
                for (int i = 0; i < _count; i++) bw.Write(_data[i]);
            }
        }

        internal sealed class BitmapContainer : IContainer
        {
            private readonly ulong[] _bits;
            private int _count;

            public BitmapContainer() { _bits = new ulong[1024]; }
            public BitmapContainer(ulong[] bits, int card) { _bits = bits; _count = card; }

            public int Cardinality => _count;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Contains(ushort v)
            {
                return ((_bits[v >> 6] >> (v & 63)) & 1UL) != 0UL;
            }

            public bool Add(ushort v)
            {
                int word = v >> 6;
                ulong mask = 1UL << (v & 63);
                if ((_bits[word] & mask) != 0UL) return false;
                _bits[word] |= mask;
                _count++;
                return true;
            }

            public IContainer Or(IContainer other)
            {
                var newBits = new ulong[1024];
                Array.Copy(_bits, newBits, 1024);
                int newCount = _count;
                if (other is BitmapContainer bc)
                {
                    for (int i = 0; i < 1024; i++)
                    {
                        ulong before = newBits[i];
                        ulong after = before | bc._bits[i];
                        newBits[i] = after;
                        newCount += System.Numerics.BitOperations.PopCount(after & ~before);
                    }
                }
                else if (other is ArrayContainer ac)
                {
                    foreach (var v in ac.Iterate())
                    {
                        int word = v >> 6;
                        ulong mask = 1UL << (v & 63);
                        if ((newBits[word] & mask) == 0UL)
                        {
                            newBits[word] |= mask;
                            newCount++;
                        }
                    }
                }
                return new BitmapContainer(newBits, newCount);
            }

            public IContainer Clone()
            {
                var copy = new ulong[1024];
                Array.Copy(_bits, copy, 1024);
                return new BitmapContainer(copy, _count);
            }

            public IEnumerable<ushort> Iterate()
            {
                for (int word = 0; word < 1024; word++)
                {
                    ulong w = _bits[word];
                    while (w != 0UL)
                    {
                        int bit = System.Numerics.BitOperations.TrailingZeroCount(w);
                        yield return (ushort)((word << 6) | bit);
                        w &= w - 1;
                    }
                }
            }

            public void Save(BinaryWriter bw)
            {
                bw.Write((byte)1);
                bw.Write(_count);
                bw.Write(MemoryMarshal.AsBytes(_bits.AsSpan()));
            }
        }
    }
}
