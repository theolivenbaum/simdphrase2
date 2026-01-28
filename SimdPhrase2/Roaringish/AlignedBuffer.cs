using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SimdPhrase2.Roaringish
{
    public unsafe class AlignedBuffer<T> : IDisposable where T : unmanaged
    {
        private T* _ptr;
        private nuint _capacity;
        private nuint _length;
        private const nuint Alignment = 64;

        public AlignedBuffer(int capacity = 0)
        {
             if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
             _capacity = (nuint)Math.Max(capacity, 4);
             nuint byteCount = _capacity * (nuint)sizeof(T);
             _ptr = (T*)NativeMemory.AlignedAlloc(byteCount, Alignment);
             // Zero initialize for safety
             NativeMemory.Clear(_ptr, byteCount);
             _length = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(T item)
        {
            if (_length >= _capacity)
            {
                Grow();
            }
            _ptr[_length++] = item;
        }

        private void Grow()
        {
            nuint newCapacity = _capacity == 0 ? 4 : _capacity * 2;
            Reserve((int)newCapacity);
        }

        public void Reserve(int newCapacity)
        {
             if ((nuint)newCapacity <= _capacity) return;

             nuint newByteCount = (nuint)newCapacity * (nuint)sizeof(T);
             _ptr = (T*)NativeMemory.AlignedRealloc(_ptr, newByteCount, Alignment);

             // Zero out new memory
             nuint oldByteCount = _capacity * (nuint)sizeof(T);
             NativeMemory.Clear((byte*)_ptr + oldByteCount, newByteCount - oldByteCount);

             _capacity = (nuint)newCapacity;
        }

        public Span<T> AsSpan() => new Span<T>(_ptr, (int)_length);

        public Span<T> AsSpan(int start) => new Span<T>(_ptr + start, (int)_length - start);

        public Span<T> AsSpan(int start, int length)
        {
            if (start + length > (int)_length) throw new ArgumentOutOfRangeException();
            return new Span<T>(_ptr + start, length);
        }

        public ref T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if ((uint)index >= (uint)_length) throw new IndexOutOfRangeException();
                return ref _ptr[index];
            }
        }

        public int Length => (int)_length;
        public int Capacity => (int)_capacity;

        public ref T Last()
        {
            if (_length == 0) throw new InvalidOperationException("Buffer is empty");
            return ref _ptr[_length - 1];
        }

        public void SetLength(int length)
        {
            if (length > (int)_capacity)
            {
                 // Maybe resize? Or just throw. Rust's unsafe usage implies we own the memory.
                 // For safety here, let's Grow if needed or throw.
                 // But typically SetLength is used when we know we have capacity.
                 if (length < 0) throw new ArgumentOutOfRangeException();
                 Reserve(length);
            }
            _length = (nuint)length;
        }

        public void Clear()
        {
            _length = 0;
        }

        public void Dispose()
        {
            if (_ptr != null)
            {
                NativeMemory.AlignedFree(_ptr);
                _ptr = null;
            }
            GC.SuppressFinalize(this);
        }

        ~AlignedBuffer()
        {
             Dispose();
        }

        public T* Ptr => _ptr;
    }
}
