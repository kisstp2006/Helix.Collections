using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Helix.Collections.LowLevel.Unsafe;
using Helix.Jobs;

namespace Helix.Collections
{
    // A ring buffer that grows; parallel writers take a spin lock.
    [StructLayout(LayoutKind.Sequential)]
    unsafe struct QueueData
    {
        public byte* buffer;
        public int head;
        public int count;
        public int capacity;
        public int spin;
        public AllocatorManager.AllocatorHandle allocator;
    }

    /// <summary>A first in, first out queue in native memory (parallel jobs may enqueue).</summary>
    [StructLayout(LayoutKind.Sequential)]
    [NativeContainer]
    public unsafe struct NativeQueue<T> : INativeDisposable where T : unmanaged
    {
        [NativeDisableUnsafePtrRestriction] internal QueueData* m_Data;
        internal AtomicSafetyHandle m_Safety;

        static readonly int s_StaticSafetyId = AtomicSafetyHandle.NewStaticSafetyId<NativeQueue<T>>();

        public NativeQueue(AllocatorManager.AllocatorHandle allocator)
        {
            m_Data = (QueueData*)Memory.Allocate(sizeof(QueueData), 16, allocator);
            *m_Data = default;
            m_Data->allocator = allocator;
            m_Safety = AtomicSafetyHandle.Create();
            AtomicSafetyHandle.SetStaticSafetyId(ref m_Safety, s_StaticSafetyId);
            Memory.Track(m_Data, allocator);
            Grow(m_Data, 16);
        }

        public readonly bool IsCreated => m_Data != null;

        public readonly int Count
        {
            get
            {
                AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
                return m_Data->count;
            }
        }

        public readonly bool IsEmpty() => !IsCreated || Count == 0;

        static void Grow(QueueData* d, int capacity)
        {
            var buffer = (byte*)Memory.Allocate((long)capacity * sizeof(T), UnsafeUtility.AlignOf<T>(), d->allocator);
            for (int i = 0; i < d->count; i++)
                ((T*)buffer)[i] = ((T*)d->buffer)[(d->head + i) % d->capacity];
            Memory.Free(d->buffer, d->allocator);
            d->buffer = buffer;
            d->head = 0;
            d->capacity = capacity;
        }

        internal static void Push(QueueData* d, T value)
        {
            if (d->count == d->capacity)
                Grow(d, d->capacity * 2);
            ((T*)d->buffer)[(d->head + d->count) % d->capacity] = value;
            d->count++;
        }

        public void Enqueue(T value)
        {
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
            Push(m_Data, value);
        }

        public bool TryDequeue(out T item)
        {
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
            if (m_Data->count == 0)
            {
                item = default;
                return false;
            }
            item = ((T*)m_Data->buffer)[m_Data->head];
            m_Data->head = (m_Data->head + 1) % m_Data->capacity;
            m_Data->count--;
            return true;
        }

        public T Dequeue()
        {
            if (!TryDequeue(out T item))
                throw new InvalidOperationException("Trying to dequeue from an empty queue");
            return item;
        }

        public T Peek()
        {
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
            if (m_Data->count == 0)
                throw new InvalidOperationException("Trying to peek into an empty queue");
            return ((T*)m_Data->buffer)[m_Data->head];
        }

        public void Clear()
        {
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
            m_Data->head = 0;
            m_Data->count = 0;
        }

        public NativeArray<T> ToArray(AllocatorManager.AllocatorHandle allocator)
        {
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
            var result = new NativeArray<T>(m_Data->count, allocator.ToAllocator, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < m_Data->count; i++)
                result[i] = ((T*)m_Data->buffer)[(m_Data->head + i) % m_Data->capacity];
            return result;
        }

        public ParallelWriter AsParallelWriter() => new ParallelWriter(m_Data, m_Safety);
        public ReadOnly AsReadOnly() => new ReadOnly(m_Data, m_Safety);

        public void Dispose()
        {
            if (!IsCreated)
                throw new ObjectDisposedException(null, "The NativeQueue is already disposed.");
            AtomicSafetyHandle.CheckDeallocateAndThrow(m_Safety);
            AtomicSafetyHandle.Release(m_Safety);
            Memory.Untrack(m_Data);
            AllocatorManager.AllocatorHandle allocator = m_Data->allocator;
            Memory.Free(m_Data->buffer, allocator);
            Memory.Free(m_Data, allocator);
            m_Data = null;
        }

        public JobHandle Dispose(JobHandle inputDeps)
        {
            if (!IsCreated)
                throw new ObjectDisposedException(null, "The NativeQueue is already disposed.");
            AtomicSafetyHandle.CheckDeallocateAndThrow(m_Safety);
            AtomicSafetyHandle.Release(m_Safety);
            Memory.Untrack(m_Data);
            JobHandle h = new FreeJob { first = m_Data, second = m_Data->buffer, allocator = m_Data->allocator.ToAllocator }.Schedule(inputDeps);
            m_Data = null;
            return h;
        }

        /// <summary>Enqueues from parallel jobs.</summary>
        [NativeContainer]
        [NativeContainerIsAtomicWriteOnly]
        [StructLayout(LayoutKind.Sequential)]
        public struct ParallelWriter
        {
            [NativeDisableUnsafePtrRestriction] internal QueueData* m_Data;
            internal AtomicSafetyHandle m_Safety;

            internal ParallelWriter(QueueData* data, AtomicSafetyHandle safety)
            {
                m_Data = data;
                m_Safety = safety;
            }

            public void Enqueue(T value)
            {
                AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
                while (Interlocked.CompareExchange(ref m_Data->spin, 1, 0) != 0)
                    Thread.SpinWait(8);
                try
                {
                    Push(m_Data, value);
                }
                finally
                {
                    Volatile.Write(ref m_Data->spin, 0);
                }
            }
        }

        /// <summary>A read only view of the queue's elements, front first.</summary>
        [NativeContainer]
        [NativeContainerIsReadOnly]
        [StructLayout(LayoutKind.Sequential)]
        public struct ReadOnly : IEnumerable<T>
        {
            [NativeDisableUnsafePtrRestriction] internal QueueData* m_Data;
            internal AtomicSafetyHandle m_Safety;

            internal ReadOnly(QueueData* data, AtomicSafetyHandle safety)
            {
                m_Data = data;
                m_Safety = safety;
            }

            public readonly bool IsCreated => m_Data != null;

            public readonly int Count
            {
                get
                {
                    AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
                    return m_Data->count;
                }
            }

            public readonly bool IsEmpty() => Count == 0;

            public readonly T this[int index]
            {
                get
                {
                    AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
                    if ((uint)index >= (uint)m_Data->count)
                        throw new IndexOutOfRangeException($"Index {index} is out of bounds [0-{m_Data->count}].");
                    return ((T*)m_Data->buffer)[(m_Data->head + index) % m_Data->capacity];
                }
            }

            public readonly Enumerator GetEnumerator() => new Enumerator(this);
            IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        public struct Enumerator : IEnumerator<T>
        {
            ReadOnly m_Queue;
            int m_Index;

            internal Enumerator(ReadOnly queue)
            {
                m_Queue = queue;
                m_Index = -1;
            }

            public void Dispose() { }
            public bool MoveNext() => ++m_Index < m_Queue.Count;
            public void Reset() => m_Index = -1;
            public T Current => m_Queue[m_Index];
            object IEnumerator.Current => Current;
        }
    }

    /// <summary>One value in native memory, shared by the copies (a job's result).</summary>
    [StructLayout(LayoutKind.Sequential)]
    [NativeContainer]
    public unsafe struct NativeReference<T> : INativeDisposable, IEquatable<NativeReference<T>> where T : unmanaged
    {
        [NativeDisableUnsafePtrRestriction] internal void* m_Data;
        internal AllocatorManager.AllocatorHandle m_AllocatorLabel;
        internal AtomicSafetyHandle m_Safety;

        static readonly int s_StaticSafetyId = AtomicSafetyHandle.NewStaticSafetyId<NativeReference<T>>();

        public NativeReference(AllocatorManager.AllocatorHandle allocator, NativeArrayOptions options = NativeArrayOptions.ClearMemory)
        {
            m_Data = Memory.Allocate(sizeof(T), UnsafeUtility.AlignOf<T>(), allocator);
            m_AllocatorLabel = allocator;
            m_Safety = AtomicSafetyHandle.Create();
            AtomicSafetyHandle.SetStaticSafetyId(ref m_Safety, s_StaticSafetyId);
            Memory.Track(m_Data, allocator);
            if (options == NativeArrayOptions.ClearMemory)
                UnsafeUtility.MemClear(m_Data, sizeof(T));
        }

        public NativeReference(T value, AllocatorManager.AllocatorHandle allocator) : this(allocator, NativeArrayOptions.UninitializedMemory) => *(T*)m_Data = value;

        public readonly bool IsCreated => m_Data != null;

        public T Value
        {
            readonly get
            {
                AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
                return *(T*)m_Data;
            }
            set
            {
                AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
                *(T*)m_Data = value;
            }
        }

        public void CopyFrom(NativeReference<T> reference) => Copy(this, reference);
        public readonly void CopyTo(NativeReference<T> reference) => Copy(reference, this);

        public static void Copy(NativeReference<T> dst, NativeReference<T> src)
        {
            AtomicSafetyHandle.CheckReadAndThrow(src.m_Safety);
            AtomicSafetyHandle.CheckWriteAndThrow(dst.m_Safety);
            *(T*)dst.m_Data = *(T*)src.m_Data;
        }

        public readonly ReadOnly AsReadOnly() => new ReadOnly(m_Data, m_Safety);
        public static implicit operator ReadOnly(NativeReference<T> nativeReference) => nativeReference.AsReadOnly();

        public void Dispose()
        {
            if (!IsCreated)
                throw new ObjectDisposedException(null, "The NativeReference is already disposed.");
            AtomicSafetyHandle.CheckDeallocateAndThrow(m_Safety);
            AtomicSafetyHandle.Release(m_Safety);
            Memory.Untrack(m_Data);
            Memory.Free(m_Data, m_AllocatorLabel);
            m_Data = null;
        }

        public JobHandle Dispose(JobHandle inputDeps)
        {
            if (!IsCreated)
                throw new ObjectDisposedException(null, "The NativeReference is already disposed.");
            AtomicSafetyHandle.CheckDeallocateAndThrow(m_Safety);
            AtomicSafetyHandle.Release(m_Safety);
            Memory.Untrack(m_Data);
            JobHandle h = new FreeJob { first = m_Data, allocator = m_AllocatorLabel.ToAllocator }.Schedule(inputDeps);
            m_Data = null;
            return h;
        }

        public bool Equals(NativeReference<T> other) => m_Data == other.m_Data;
        public override bool Equals(object obj) => obj is NativeReference<T> other && Equals(other);
        public override int GetHashCode() => ((long)m_Data).GetHashCode();
        public static bool operator ==(NativeReference<T> left, NativeReference<T> right) => left.Equals(right);
        public static bool operator !=(NativeReference<T> left, NativeReference<T> right) => !left.Equals(right);

        [NativeContainer]
        [NativeContainerIsReadOnly]
        [StructLayout(LayoutKind.Sequential)]
        public struct ReadOnly
        {
            [NativeDisableUnsafePtrRestriction] internal void* m_Data;
            internal AtomicSafetyHandle m_Safety;

            internal ReadOnly(void* data, AtomicSafetyHandle safety)
            {
                m_Data = data;
                m_Safety = safety;
            }

            public readonly bool IsCreated => m_Data != null;

            public readonly T Value
            {
                get
                {
                    AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
                    return *(T*)m_Data;
                }
            }
        }
    }

    public static unsafe class NativeReferenceUnsafeUtility
    {
        public static void* GetUnsafePtr<T>(this NativeReference<T> reference) where T : unmanaged
        {
            AtomicSafetyHandle.CheckWriteAndThrow(reference.m_Safety);
            return reference.m_Data;
        }

        public static void* GetUnsafeReadOnlyPtr<T>(this NativeReference<T> reference) where T : unmanaged
        {
            AtomicSafetyHandle.CheckReadAndThrow(reference.m_Safety);
            return reference.m_Data;
        }

        public static void* GetUnsafePtrWithoutChecks<T>(NativeReference<T> reference) where T : unmanaged => reference.m_Data;
    }
}
