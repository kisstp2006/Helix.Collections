using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Helix.Collections.LowLevel.Unsafe;
using Helix.Jobs;
using SysUnsafe = System.Runtime.CompilerServices.Unsafe;

namespace Helix.Collections.LowLevel.Unsafe
{
    /// <summary>The header of a list: its elements, length and capacity (what NativeList points at; C++ reads it).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct UnsafeList<T> where T : unmanaged
    {
        [NativeDisableUnsafePtrRestriction] public T* Ptr;
        public int m_length;
        public int m_capacity;
        public AllocatorManager.AllocatorHandle Allocator;
        readonly int padding;
    }

    public static unsafe class NativeListUnsafeUtility
    {
        public static T* GetUnsafePtr<T>(this NativeList<T> list) where T : unmanaged
        {
            AtomicSafetyHandle.CheckWriteAndThrow(list.m_Safety);
            return list.m_ListData->Ptr;
        }

        public static T* GetUnsafeReadOnlyPtr<T>(this NativeList<T> list) where T : unmanaged
        {
            AtomicSafetyHandle.CheckReadAndThrow(list.m_Safety);
            return list.m_ListData->Ptr;
        }
    }

    // Allocations of the containers of this assembly (tracked for leaks as NativeArray's are).
    static unsafe class Memory
    {
        internal static void* Allocate(long size, int alignment, AllocatorManager.AllocatorHandle allocator)
        {
            Allocator a = allocator.ToAllocator;
            if (a <= Allocator.None)
                throw new ArgumentException("Allocator must be Temp, TempJob or Persistent", nameof(allocator));
            void* p = UnsafeUtility.Malloc(size > 0 ? size : 1, alignment, a);
            return p;
        }

        internal static void Free(void* p, AllocatorManager.AllocatorHandle allocator)
        {
            if (p != null)
                UnsafeUtility.Free(p, allocator.ToAllocator);
        }

        internal static void Track(void* p, AllocatorManager.AllocatorHandle allocator) => LeakTracker.Track(p, allocator.ToAllocator);
        internal static void Untrack(void* p) => LeakTracker.Untrack(p);

        internal static void CheckElement<T>()
        {
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                throw new InvalidOperationException($"{typeof(T)} must be unmanaged (contain no managed types).");
        }
    }

    // Containers freed by a job (Dispose(JobHandle)).
    unsafe struct FreeJob : IJob
    {
        [NativeDisableUnsafePtrRestriction] public void* first;
        [NativeDisableUnsafePtrRestriction] public void* second;
        public Allocator allocator;

        public void Execute()
        {
            if (second != null)
                UnsafeUtility.Free(second, allocator);
            if (first != null)
                UnsafeUtility.Free(first, allocator);
        }
    }
}

namespace Helix.Collections
{
    /// <summary>A resizable list in native memory (copies of the struct share it).</summary>
    [StructLayout(LayoutKind.Sequential)]
    [NativeContainer]
    public unsafe struct NativeList<T> : INativeDisposable, INativeList<T>, IEnumerable<T> where T : unmanaged
    {
        [NativeDisableUnsafePtrRestriction] internal UnsafeList<T>* m_ListData;
        internal AtomicSafetyHandle m_Safety;

        static readonly int s_StaticSafetyId = AtomicSafetyHandle.NewStaticSafetyId<NativeList<T>>();

        public NativeList(AllocatorManager.AllocatorHandle allocator) : this(1, allocator) { }

        public NativeList(int initialCapacity, AllocatorManager.AllocatorHandle allocator)
        {
            if (initialCapacity < 0)
                throw new ArgumentOutOfRangeException(nameof(initialCapacity), "Capacity must be >= 0");
            Memory.CheckElement<T>();
            m_ListData = (UnsafeList<T>*)Memory.Allocate(sizeof(UnsafeList<T>), 16, allocator);
            *m_ListData = default;
            m_ListData->Allocator = allocator;
            m_Safety = AtomicSafetyHandle.Create();
            AtomicSafetyHandle.SetStaticSafetyId(ref m_Safety, s_StaticSafetyId);
            AtomicSafetyHandle.SetBumpSecondaryVersionOnScheduleWrite(m_Safety, true);
            Memory.Track(m_ListData, allocator);
            SetCapacityUnchecked(Math.Max(initialCapacity, 1));
        }

        public readonly bool IsCreated => m_ListData != null;

        public readonly bool IsEmpty => !IsCreated || Length == 0;

        public int Length
        {
            readonly get
            {
                AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
                return m_ListData->m_length;
            }
            set => Resize(value, NativeArrayOptions.ClearMemory);
        }

        public int Capacity
        {
            readonly get
            {
                AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
                return m_ListData->m_capacity;
            }
            set => SetCapacity(value);
        }

        public T this[int index]
        {
            readonly get
            {
                AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
                CheckIndex(index);
                return m_ListData->Ptr[index];
            }
            set
            {
                AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
                CheckIndex(index);
                m_ListData->Ptr[index] = value;
            }
        }

        public ref T ElementAt(int index)
        {
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
            CheckIndex(index);
            return ref m_ListData->Ptr[index];
        }

        readonly void CheckIndex(int index)
        {
            if ((uint)index >= (uint)m_ListData->m_length)
                throw new IndexOutOfRangeException($"Index {index} is out of range in NativeList of '{m_ListData->m_length}' Length.");
        }

        void Grow(int needed)
        {
            if (needed > m_ListData->m_capacity)
                SetCapacityUnchecked(Math.Max(needed, m_ListData->m_capacity * 2));
        }

        void SetCapacityUnchecked(int capacity)
        {
            if (capacity < m_ListData->m_length)
                throw new ArgumentOutOfRangeException(nameof(capacity), $"Capacity {capacity} must be larger than Length {m_ListData->m_length}.");
            if (capacity == m_ListData->m_capacity)
                return;
            var p = (T*)Memory.Allocate((long)capacity * sizeof(T), UnsafeUtility.AlignOf<T>(), m_ListData->Allocator);
            if (m_ListData->Ptr != null)
            {
                UnsafeUtility.MemCpy(p, m_ListData->Ptr, (long)m_ListData->m_length * sizeof(T));
                Memory.Free(m_ListData->Ptr, m_ListData->Allocator);
            }
            m_ListData->Ptr = p;
            m_ListData->m_capacity = capacity;
            // Arrays of the old buffer are no longer valid.
            AtomicSafetyHandle.CheckWriteAndBumpSecondaryVersion(m_Safety);
        }

        public void SetCapacity(int capacity)
        {
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
            SetCapacityUnchecked(Math.Max(capacity, 1));
        }

        public void TrimExcess()
        {
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
            SetCapacityUnchecked(Math.Max(m_ListData->m_length, 1));
        }

        public void Add(in T value)
        {
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
            Grow(m_ListData->m_length + 1);
            m_ListData->Ptr[m_ListData->m_length++] = value;
        }

        public void AddNoResize(T value)
        {
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
            CheckSufficientCapacity(1);
            m_ListData->Ptr[m_ListData->m_length++] = value;
        }

        void CheckSufficientCapacity(int count)
        {
            if (m_ListData->m_length + count > m_ListData->m_capacity)
                throw new InvalidOperationException($"Length {m_ListData->m_length + count} exceeds Capacity {m_ListData->m_capacity}");
        }

        public void AddRange(NativeArray<T> array) => AddRange(NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(array), array.Length);

        public void AddRange(void* ptr, int count)
        {
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count), "Value must be >= 0");
            Grow(m_ListData->m_length + count);
            UnsafeUtility.MemCpy(m_ListData->Ptr + m_ListData->m_length, ptr, (long)count * sizeof(T));
            m_ListData->m_length += count;
        }

        public void AddRangeNoResize(void* ptr, int count)
        {
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
            CheckSufficientCapacity(count);
            UnsafeUtility.MemCpy(m_ListData->Ptr + m_ListData->m_length, ptr, (long)count * sizeof(T));
            m_ListData->m_length += count;
        }

        public void AddRangeNoResize(NativeList<T> list) => AddRangeNoResize(list.m_ListData->Ptr, list.Length);

        public void AddReplicate(in T value, int count)
        {
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
            Grow(m_ListData->m_length + count);
            for (int i = 0; i < count; i++)
                m_ListData->Ptr[m_ListData->m_length + i] = value;
            m_ListData->m_length += count;
        }

        public void InsertRange(int index, int count) => InsertRangeWithBeginEnd(index, index + count);

        public void InsertRangeWithBeginEnd(int begin, int end)
        {
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
            if (begin > end)
                throw new ArgumentException($"Value for begin {begin} index must less or equal to end {end}.");
            if (begin < 0 || begin > m_ListData->m_length)
                throw new ArgumentOutOfRangeException(nameof(begin), $"Value for begin {begin} must be positive and less or equal to length {m_ListData->m_length}.");
            int items = end - begin;
            int oldLength = m_ListData->m_length;
            Grow(oldLength + items);
            UnsafeUtility.MemMove(m_ListData->Ptr + end, m_ListData->Ptr + begin, (long)(oldLength - begin) * sizeof(T));
            m_ListData->m_length = oldLength + items;
        }

        public void RemoveAt(int index) => RemoveRange(index, 1);

        public void RemoveRange(int index, int count)
        {
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
            CheckRange(index, count);
            int tail = m_ListData->m_length - index - count;
            UnsafeUtility.MemMove(m_ListData->Ptr + index, m_ListData->Ptr + index + count, (long)tail * sizeof(T));
            m_ListData->m_length -= count;
        }

        public void RemoveAtSwapBack(int index) => RemoveRangeSwapBack(index, 1);

        public void RemoveRangeSwapBack(int index, int count)
        {
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
            CheckRange(index, count);
            int copyFrom = Math.Max(m_ListData->m_length - count, index + count);
            UnsafeUtility.MemCpy(m_ListData->Ptr + index, m_ListData->Ptr + copyFrom, (long)(m_ListData->m_length - copyFrom) * sizeof(T));
            m_ListData->m_length -= count;
        }

        void CheckRange(int index, int count)
        {
            if (index < 0 || count < 0 || index + count > m_ListData->m_length)
                throw new ArgumentOutOfRangeException(nameof(index), $"Index {index} and count {count} are out of range in NativeList of '{m_ListData->m_length}' Length.");
        }

        public void Resize(int length, NativeArrayOptions options)
        {
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
            if (length < 0)
                throw new ArgumentOutOfRangeException(nameof(length), "Length must be >= 0");
            int oldLength = m_ListData->m_length;
            Grow(length);
            m_ListData->m_length = length;
            if (options == NativeArrayOptions.ClearMemory && length > oldLength)
                UnsafeUtility.MemClear(m_ListData->Ptr + oldLength, (long)(length - oldLength) * sizeof(T));
        }

        public void ResizeUninitialized(int length) => Resize(length, NativeArrayOptions.UninitializedMemory);

        public void Clear()
        {
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
            m_ListData->m_length = 0;
        }

        public void CopyFrom(in NativeArray<T> other)
        {
            Resize(other.Length, NativeArrayOptions.UninitializedMemory);
            UnsafeUtility.MemCpy(m_ListData->Ptr, NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(other), (long)other.Length * sizeof(T));
        }

        public void CopyFrom(in NativeList<T> other)
        {
            AtomicSafetyHandle.CheckReadAndThrow(other.m_Safety);
            Resize(other.m_ListData->m_length, NativeArrayOptions.UninitializedMemory);
            UnsafeUtility.MemCpy(m_ListData->Ptr, other.m_ListData->Ptr, (long)other.m_ListData->m_length * sizeof(T));
        }

        public void CopyFrom(in UnsafeList<T> other)
        {
            Resize(other.m_length, NativeArrayOptions.UninitializedMemory);
            UnsafeUtility.MemCpy(m_ListData->Ptr, other.Ptr, (long)other.m_length * sizeof(T));
        }

        /// <summary>The elements as a NativeArray (invalid once the list grows).</summary>
        public NativeArray<T> AsArray()
        {
            AtomicSafetyHandle.CheckGetSecondaryDataPointerAndThrow(m_Safety);
            var array = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<T>(m_ListData->Ptr, m_ListData->m_length, Allocator.None);
            AtomicSafetyHandle safety = m_Safety;
            if (!AtomicSafetyHandle.IsDefaultValue(safety))
                AtomicSafetyHandle.UseSecondaryVersion(ref safety);
            NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref array, safety);
            return array;
        }

        /// <summary>A NativeArray for a job whose length is the list's when the job runs (a job before it may fill the list).</summary>
        public NativeArray<T> AsDeferredJobArray()
        {
            var array = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<T>((byte*)m_ListData + 1, 0, Allocator.None);
            NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref array, m_Safety);
            return array;
        }

        public NativeArray<T>.ReadOnly AsReadOnly() => AsArray().AsReadOnly();
        public NativeArray<T>.ReadOnly AsParallelReader() => AsReadOnly();

        public ParallelWriter AsParallelWriter() => new ParallelWriter(m_ListData, m_Safety);

        public NativeArray<T> ToArray(AllocatorManager.AllocatorHandle allocator)
        {
            var result = new NativeArray<T>(Length, allocator.ToAllocator, NativeArrayOptions.UninitializedMemory);
            UnsafeUtility.MemCpy(NativeArrayUnsafeUtility.GetUnsafePtr(result), m_ListData->Ptr, (long)Length * sizeof(T));
            return result;
        }

        [Obsolete("Implicit cast from `NativeList<T>` to `NativeArray<T>` has been deprecated; Use '.AsArray()' method to do explicit cast instead.", false)]
        public static implicit operator NativeArray<T>(NativeList<T> nativeList) => nativeList.AsArray();

        public UnsafeList<T>* GetUnsafeList() => m_ListData;

        public void Dispose()
        {
            if (!IsCreated)
                throw new ObjectDisposedException(null, "The NativeList is already disposed.");
            AtomicSafetyHandle.CheckDeallocateAndThrow(m_Safety);
            AtomicSafetyHandle.Release(m_Safety);
            Memory.Untrack(m_ListData);
            AllocatorManager.AllocatorHandle allocator = m_ListData->Allocator;
            Memory.Free(m_ListData->Ptr, allocator);
            Memory.Free(m_ListData, allocator);
            m_ListData = null;
        }

        public JobHandle Dispose(JobHandle inputDeps)
        {
            if (!IsCreated)
                throw new ObjectDisposedException(null, "The NativeList is already disposed.");
            AtomicSafetyHandle.CheckDeallocateAndThrow(m_Safety);
            AtomicSafetyHandle.Release(m_Safety);
            Memory.Untrack(m_ListData);
            JobHandle h = new FreeJob { first = m_ListData, second = m_ListData->Ptr, allocator = m_ListData->Allocator.ToAllocator }.Schedule(inputDeps);
            m_ListData = null;
            return h;
        }

        public NativeArray<T>.Enumerator GetEnumerator()
        {
            NativeArray<T> array = AsArray();
            return new NativeArray<T>.Enumerator(ref array);
        }

        IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>Adds from parallel jobs into room the list already has.</summary>
        [NativeContainer]
        [NativeContainerIsAtomicWriteOnly]
        [StructLayout(LayoutKind.Sequential)]
        public struct ParallelWriter
        {
            [NativeDisableUnsafePtrRestriction] public UnsafeList<T>* ListData;
            internal AtomicSafetyHandle m_Safety;

            internal ParallelWriter(UnsafeList<T>* list, AtomicSafetyHandle safety)
            {
                ListData = list;
                m_Safety = safety;
            }

            public readonly void* Ptr => ListData->Ptr;

            public void AddNoResize(T value)
            {
                AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
                int index = Interlocked.Increment(ref ListData->m_length) - 1;
                CheckCapacity(index, 1);
                ListData->Ptr[index] = value;
            }

            public void AddRangeNoResize(void* ptr, int count)
            {
                AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
                int index = Interlocked.Add(ref ListData->m_length, count) - count;
                CheckCapacity(index, count);
                UnsafeUtility.MemCpy(ListData->Ptr + index, ptr, (long)count * sizeof(T));
            }

            public void AddRangeNoResize(UnsafeList<T> list) => AddRangeNoResize(list.Ptr, list.m_length);
            public void AddRangeNoResize(NativeList<T> list) => AddRangeNoResize(list.m_ListData->Ptr, list.m_ListData->m_length);

            readonly void CheckCapacity(int index, int count)
            {
                if (index + count > ListData->m_capacity)
                    throw new InvalidOperationException($"AddNoResize assumes that list capacity is sufficient (Capacity {ListData->m_capacity}, Length {index + count})!");
            }
        }
    }

    public static class NativeListExtensions
    {
        public static bool Contains<T, U>(this NativeList<T> list, U value) where T : unmanaged, IEquatable<U> => IndexOf(list, value) != -1;

        public static int IndexOf<T, U>(this NativeList<T> list, U value) where T : unmanaged, IEquatable<U>
        {
            for (int i = 0; i < list.Length; i++)
                if (list[i].Equals(value))
                    return i;
            return -1;
        }
    }

    /// <summary>Sorting and binary search of native arrays and lists.</summary>
    public static unsafe class NativeSortExtension
    {
        struct DefaultComparer<T> : IComparer<T> where T : unmanaged, IComparable<T>
        {
            public int Compare(T x, T y) => x.CompareTo(y);
        }

        public static void Sort<T>(this NativeArray<T> array) where T : unmanaged, IComparable<T> => Sort(array, new DefaultComparer<T>());

        public static void Sort<T, U>(this NativeArray<T> array, U comp) where T : unmanaged where U : IComparer<T> =>
            MemoryExtensions.Sort(array.AsSpan(), comp);

        public static void Sort<T>(this NativeList<T> list) where T : unmanaged, IComparable<T> => Sort(list, new DefaultComparer<T>());

        public static void Sort<T, U>(this NativeList<T> list, U comp) where T : unmanaged where U : IComparer<T> =>
            MemoryExtensions.Sort(new Span<T>(list.GetUnsafePtr(), list.Length), comp);

        public static int BinarySearch<T>(this NativeArray<T> array, T value) where T : unmanaged, IComparable<T> => BinarySearch(array, value, new DefaultComparer<T>());

        public static int BinarySearch<T, U>(this NativeArray<T> array, T value, U comp) where T : unmanaged where U : IComparer<T> =>
            MemoryExtensions.BinarySearch(array.AsReadOnlySpan(), value, comp);

        public static int BinarySearch<T>(this NativeList<T> list, T value) where T : unmanaged, IComparable<T> => BinarySearch(list, value, new DefaultComparer<T>());

        public static int BinarySearch<T, U>(this NativeList<T> list, T value, U comp) where T : unmanaged where U : IComparer<T> =>
            MemoryExtensions.BinarySearch(new ReadOnlySpan<T>(list.GetUnsafeReadOnlyPtr(), list.Length), value, comp);
    }
}
