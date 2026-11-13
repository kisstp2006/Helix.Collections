using System;
using Helix.Jobs;

namespace Helix.Collections
{
    /// <summary>Allocators as handles (the built in ones; custom allocators come later).</summary>
    public static class AllocatorManager
    {
        /// <summary>An allocator: Index is the Allocator value of a built in one.</summary>
        public struct AllocatorHandle : IEquatable<AllocatorHandle>
        {
            public ushort Index;
            public ushort Version;

            public static implicit operator AllocatorHandle(Allocator a) => new AllocatorHandle { Index = (ushort)a };

            public readonly Allocator ToAllocator => (Allocator)Index;
            public readonly bool IsValid => Index > (ushort)Allocator.None;

            public readonly bool Equals(AllocatorHandle other) => Index == other.Index && Version == other.Version;
            public override readonly bool Equals(object obj) => obj is AllocatorHandle other && Equals(other);
            public override readonly int GetHashCode() => (Version << 16) | Index;
            public static bool operator ==(AllocatorHandle lhs, AllocatorHandle rhs) => lhs.Equals(rhs);
            public static bool operator !=(AllocatorHandle lhs, AllocatorHandle rhs) => !lhs.Equals(rhs);
        }

        public static readonly AllocatorHandle Invalid = Allocator.Invalid;
        public static readonly AllocatorHandle None = Allocator.None;
        public static readonly AllocatorHandle Temp = Allocator.Temp;
        public static readonly AllocatorHandle TempJob = Allocator.TempJob;
        public static readonly AllocatorHandle Persistent = Allocator.Persistent;
    }

    /// <summary>A container that can be disposed after a job.</summary>
    public interface INativeDisposable : IDisposable
    {
        JobHandle Dispose(JobHandle inputDeps);
    }

    /// <summary>A container of elements by index.</summary>
    public interface IIndexable<T> where T : unmanaged
    {
        int Length { get; set; }
        ref T ElementAt(int index);
    }

    /// <summary>A resizable container of elements by index.</summary>
    public interface INativeList<T> : IIndexable<T> where T : unmanaged
    {
        int Capacity { get; set; }
        bool IsEmpty { get; }
        T this[int index] { get; set; }
        void Clear();
    }
}
