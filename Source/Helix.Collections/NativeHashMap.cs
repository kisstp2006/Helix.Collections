using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Helix.Collections.LowLevel.Unsafe;
using Helix.Jobs;

namespace Helix.Collections
{
    /// <summary>A key and its value in a hash map.</summary>
    public unsafe struct KVPair<TKey, TValue> where TKey : unmanaged, IEquatable<TKey> where TValue : unmanaged
    {
        internal TKey* m_Key;
        internal TValue* m_Value;

        public readonly TKey Key => *m_Key;
        public readonly ref TValue Value => ref *m_Value;

        public readonly bool GetKeyValue(out TKey key, out TValue value)
        {
            key = *m_Key;
            value = *m_Value;
            return true;
        }
    }

    /// <summary>The keys and values of a hash map side by side.</summary>
    public struct NativeKeyValueArrays<TKey, TValue> : INativeDisposable where TKey : unmanaged where TValue : unmanaged
    {
        public NativeArray<TKey> Keys;
        public NativeArray<TValue> Values;

        public NativeKeyValueArrays(int length, AllocatorManager.AllocatorHandle allocator, NativeArrayOptions options)
        {
            Keys = new NativeArray<TKey>(length, allocator.ToAllocator, options);
            Values = new NativeArray<TValue>(length, allocator.ToAllocator, options);
        }

        public readonly int Length => Keys.Length;

        public void Dispose()
        {
            Keys.Dispose();
            Values.Dispose();
        }

        public JobHandle Dispose(JobHandle inputDeps) => JobHandle.CombineDependencies(Keys.Dispose(inputDeps), Values.Dispose(inputDeps));
    }

    // The table of a hash map: entries in arrays, chains of entry indices per bucket, a free list.
    [StructLayout(LayoutKind.Sequential)]
    unsafe struct HashMapData
    {
        public byte* keys;
        public byte* values;
        public int* next;
        public int* buckets;
        public int count;
        public int capacity;
        public int bucketMask;
        public int allocatedIndex;
        public int firstFree;
        public AllocatorManager.AllocatorHandle allocator;
    }

    /// <summary>An unordered map from keys to values in native memory (one writer at a time).</summary>
    [StructLayout(LayoutKind.Sequential)]
    [NativeContainer]
    public unsafe struct NativeHashMap<TKey, TValue> : INativeDisposable, IEnumerable<KVPair<TKey, TValue>>
        where TKey : unmanaged, IEquatable<TKey> where TValue : unmanaged
    {
        [NativeDisableUnsafePtrRestriction] internal HashMapData* m_Data;
        internal AtomicSafetyHandle m_Safety;

        static readonly int s_StaticSafetyId = AtomicSafetyHandle.NewStaticSafetyId<NativeHashMap<TKey, TValue>>();

        public NativeHashMap(int initialCapacity, AllocatorManager.AllocatorHandle allocator)
        {
            if (initialCapacity < 0)
                throw new ArgumentOutOfRangeException(nameof(initialCapacity), "Capacity must be >= 0");
            m_Data = (HashMapData*)Memory.Allocate(sizeof(HashMapData), 16, allocator);
            *m_Data = default;
            m_Data->allocator = allocator;
            m_Data->firstFree = -1;
            m_Safety = AtomicSafetyHandle.Create();
            AtomicSafetyHandle.SetStaticSafetyId(ref m_Safety, s_StaticSafetyId);
            Memory.Track(m_Data, allocator);
            Resize(m_Data, Math.Max(initialCapacity, 4));
        }

        public readonly bool IsCreated => m_Data != null;
        public readonly bool IsEmpty => !IsCreated || Count == 0;

        public readonly int Count
        {
            get
            {
                AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
                return m_Data->count;
            }
        }

        public int Capacity
        {
            readonly get
            {
                AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
                return m_Data->capacity;
            }
            set
            {
                AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
                Resize(m_Data, Math.Max(value, m_Data->count));
            }
        }

        public TValue this[TKey key]
        {
            readonly get
            {
                AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
                int i = Find(m_Data, key);
                if (i < 0)
                    throw new ArgumentException($"Key: {key} is not present.");
                return ((TValue*)m_Data->values)[i];
            }
            set
            {
                AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
                int i = Find(m_Data, key);
                if (i >= 0)
                    ((TValue*)m_Data->values)[i] = value;
                else
                    Insert(m_Data, key, value);
            }
        }

        public bool TryAdd(TKey key, TValue item)
        {
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
            if (Find(m_Data, key) >= 0)
                return false;
            Insert(m_Data, key, item);
            return true;
        }

        public void Add(TKey key, TValue item)
        {
            if (!TryAdd(key, item))
                throw new ArgumentException($"An item with the same key has already been added: {key}");
        }

        public bool Remove(TKey key)
        {
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
            HashMapData* d = m_Data;
            int bucket = key.GetHashCode() & d->bucketMask;
            int prev = -1;
            for (int i = d->buckets[bucket]; i >= 0; prev = i, i = d->next[i])
                if (((TKey*)d->keys)[i].Equals(key))
                {
                    if (prev < 0)
                        d->buckets[bucket] = d->next[i];
                    else
                        d->next[prev] = d->next[i];
                    d->next[i] = d->firstFree;
                    d->firstFree = i;
                    d->count--;
                    return true;
                }
            return false;
        }

        public readonly bool TryGetValue(TKey key, out TValue item)
        {
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
            int i = Find(m_Data, key);
            item = i >= 0 ? ((TValue*)m_Data->values)[i] : default;
            return i >= 0;
        }

        public readonly bool ContainsKey(TKey key)
        {
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
            return Find(m_Data, key) >= 0;
        }

        public void Clear()
        {
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
            Clear(m_Data);
        }

        public void TrimExcess()
        {
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
            Resize(m_Data, Math.Max(m_Data->count, 4));
        }

        public readonly NativeArray<TKey> GetKeyArray(AllocatorManager.AllocatorHandle allocator)
        {
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
            return Keys(m_Data, allocator);
        }

        public readonly NativeArray<TValue> GetValueArray(AllocatorManager.AllocatorHandle allocator)
        {
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
            return Values(m_Data, allocator);
        }

        public readonly NativeKeyValueArrays<TKey, TValue> GetKeyValueArrays(AllocatorManager.AllocatorHandle allocator)
        {
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
            return Pairs(m_Data, allocator);
        }

        public readonly ReadOnly AsReadOnly() => new ReadOnly(m_Data, m_Safety);

        public void Dispose()
        {
            if (!IsCreated)
                throw new ObjectDisposedException(null, "The NativeHashMap is already disposed.");
            AtomicSafetyHandle.CheckDeallocateAndThrow(m_Safety);
            AtomicSafetyHandle.Release(m_Safety);
            Memory.Untrack(m_Data);
            AllocatorManager.AllocatorHandle allocator = m_Data->allocator;
            Memory.Free(m_Data->keys, allocator);
            Memory.Free(m_Data, allocator);
            m_Data = null;
        }

        public JobHandle Dispose(JobHandle inputDeps)
        {
            if (!IsCreated)
                throw new ObjectDisposedException(null, "The NativeHashMap is already disposed.");
            AtomicSafetyHandle.CheckDeallocateAndThrow(m_Safety);
            AtomicSafetyHandle.Release(m_Safety);
            Memory.Untrack(m_Data);
            JobHandle h = new FreeJob { first = m_Data, second = m_Data->keys, allocator = m_Data->allocator.ToAllocator }.Schedule(inputDeps);
            m_Data = null;
            return h;
        }

        public Enumerator GetEnumerator()
        {
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
            return new Enumerator(m_Data, m_Safety);
        }

        IEnumerator<KVPair<TKey, TValue>> IEnumerable<KVPair<TKey, TValue>>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        // ------------------------------------------------------------------------------------------- the table

        internal static int Find(HashMapData* d, TKey key)
        {
            for (int i = d->buckets[key.GetHashCode() & d->bucketMask]; i >= 0; i = d->next[i])
                if (((TKey*)d->keys)[i].Equals(key))
                    return i;
            return -1;
        }

        static void Insert(HashMapData* d, TKey key, TValue value)
        {
            if (d->count >= d->capacity)
                Resize(d, d->capacity * 2);
            int i;
            if (d->firstFree >= 0)
            {
                i = d->firstFree;
                d->firstFree = d->next[i];
            }
            else
                i = d->allocatedIndex++;
            ((TKey*)d->keys)[i] = key;
            ((TValue*)d->values)[i] = value;
            int bucket = key.GetHashCode() & d->bucketMask;
            d->next[i] = d->buckets[bucket];
            d->buckets[bucket] = i;
            d->count++;
        }

        static void Clear(HashMapData* d)
        {
            for (int b = 0; b <= d->bucketMask; b++)
                d->buckets[b] = -1;
            d->count = 0;
            d->allocatedIndex = 0;
            d->firstFree = -1;
        }

        // One block: keys, values, next indices, buckets; live entries inserted again.
        static void Resize(HashMapData* d, int capacity)
        {
            int buckets = 1;
            while (buckets < capacity * 2)
                buckets <<= 1;
            long keyBytes = (long)capacity * sizeof(TKey), valueBytes = (long)capacity * sizeof(TValue);
            long keyAt = 0, valueAt = Align(keyAt + keyBytes), nextAt = Align(valueAt + valueBytes), bucketAt = Align(nextAt + (long)capacity * 4);
            byte* block = (byte*)Memory.Allocate(bucketAt + (long)buckets * 4, 16, d->allocator);
            HashMapData old = *d;
            d->keys = block + keyAt;
            d->values = block + valueAt;
            d->next = (int*)(block + nextAt);
            d->buckets = (int*)(block + bucketAt);
            d->capacity = capacity;
            d->bucketMask = buckets - 1;
            Clear(d);
            if (old.keys != null)
            {
                for (int b = 0; b <= old.bucketMask; b++)
                    for (int i = old.buckets[b]; i >= 0; i = old.next[i])
                        Insert(d, ((TKey*)old.keys)[i], ((TValue*)old.values)[i]);
                Memory.Free(old.keys, d->allocator);
            }
        }

        static long Align(long x) => (x + 15) & ~15L;

        internal static NativeArray<TKey> Keys(HashMapData* d, AllocatorManager.AllocatorHandle allocator)
        {
            var result = new NativeArray<TKey>(d->count, allocator.ToAllocator, NativeArrayOptions.UninitializedMemory);
            int n = 0;
            for (int b = 0; b <= d->bucketMask; b++)
                for (int i = d->buckets[b]; i >= 0; i = d->next[i])
                    result[n++] = ((TKey*)d->keys)[i];
            return result;
        }

        internal static NativeArray<TValue> Values(HashMapData* d, AllocatorManager.AllocatorHandle allocator)
        {
            var result = new NativeArray<TValue>(d->count, allocator.ToAllocator, NativeArrayOptions.UninitializedMemory);
            int n = 0;
            for (int b = 0; b <= d->bucketMask; b++)
                for (int i = d->buckets[b]; i >= 0; i = d->next[i])
                    result[n++] = ((TValue*)d->values)[i];
            return result;
        }

        internal static NativeKeyValueArrays<TKey, TValue> Pairs(HashMapData* d, AllocatorManager.AllocatorHandle allocator)
        {
            var result = new NativeKeyValueArrays<TKey, TValue>(d->count, allocator, NativeArrayOptions.UninitializedMemory);
            int n = 0;
            for (int b = 0; b <= d->bucketMask; b++)
                for (int i = d->buckets[b]; i >= 0; i = d->next[i])
                {
                    result.Keys[n] = ((TKey*)d->keys)[i];
                    result.Values[n] = ((TValue*)d->values)[i];
                    n++;
                }
            return result;
        }

        /// <summary>Iterates the pairs (in no particular order).</summary>
        public struct Enumerator : IEnumerator<KVPair<TKey, TValue>>
        {
            HashMapData* m_Data;
            AtomicSafetyHandle m_Safety;
            int m_Bucket;
            int m_Index;

            internal Enumerator(HashMapData* data, AtomicSafetyHandle safety)
            {
                m_Data = data;
                m_Safety = safety;
                m_Bucket = -1;
                m_Index = -1;
            }

            public void Dispose() { }

            public bool MoveNext()
            {
                AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
                if (m_Index >= 0)
                    m_Index = m_Data->next[m_Index];
                while (m_Index < 0)
                {
                    if (++m_Bucket > m_Data->bucketMask)
                        return false;
                    m_Index = m_Data->buckets[m_Bucket];
                }
                return true;
            }

            public void Reset()
            {
                m_Bucket = -1;
                m_Index = -1;
            }

            public KVPair<TKey, TValue> Current => new KVPair<TKey, TValue> { m_Key = (TKey*)m_Data->keys + m_Index, m_Value = (TValue*)m_Data->values + m_Index };
            object IEnumerator.Current => Current;
        }

        /// <summary>A read only view (jobs may read it at the same time).</summary>
        [NativeContainer]
        [NativeContainerIsReadOnly]
        [StructLayout(LayoutKind.Sequential)]
        public struct ReadOnly : IEnumerable<KVPair<TKey, TValue>>
        {
            [NativeDisableUnsafePtrRestriction] internal HashMapData* m_Data;
            internal AtomicSafetyHandle m_Safety;

            internal ReadOnly(HashMapData* data, AtomicSafetyHandle safety)
            {
                m_Data = data;
                m_Safety = safety;
            }

            public readonly bool IsCreated => m_Data != null;
            public readonly bool IsEmpty => !IsCreated || Count == 0;

            public readonly int Count
            {
                get
                {
                    AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
                    return m_Data->count;
                }
            }

            public readonly int Capacity
            {
                get
                {
                    AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
                    return m_Data->capacity;
                }
            }

            public readonly TValue this[TKey key]
            {
                get
                {
                    AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
                    int i = Find(m_Data, key);
                    if (i < 0)
                        throw new ArgumentException($"Key: {key} is not present.");
                    return ((TValue*)m_Data->values)[i];
                }
            }

            public readonly bool ContainsKey(TKey key)
            {
                AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
                return Find(m_Data, key) >= 0;
            }

            public readonly bool TryGetValue(TKey key, out TValue item)
            {
                AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
                int i = Find(m_Data, key);
                item = i >= 0 ? ((TValue*)m_Data->values)[i] : default;
                return i >= 0;
            }

            public readonly NativeArray<TKey> GetKeyArray(AllocatorManager.AllocatorHandle allocator)
            {
                AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
                return Keys(m_Data, allocator);
            }

            public readonly NativeArray<TValue> GetValueArray(AllocatorManager.AllocatorHandle allocator)
            {
                AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
                return Values(m_Data, allocator);
            }

            public readonly NativeKeyValueArrays<TKey, TValue> GetKeyValueArrays(AllocatorManager.AllocatorHandle allocator)
            {
                AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
                return Pairs(m_Data, allocator);
            }

            public readonly Enumerator GetEnumerator()
            {
                AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
                return new Enumerator(m_Data, m_Safety);
            }

            IEnumerator<KVPair<TKey, TValue>> IEnumerable<KVPair<TKey, TValue>>.GetEnumerator() => GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
