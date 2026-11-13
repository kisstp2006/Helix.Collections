#pragma once

#include <Helix/Core/Basics/Span.h>

// The memory layouts of the C# containers (Helix.Collections' NativeArray<T>, NativeSlice<T>, NativeList<T>), so C++
// code given one (a pointer to the struct, or the struct by value) reads its elements as a Helix.Core span where they
// are. The C# side checks its use in development builds; C++ code reads and writes the elements while it owns the
// container (on the main thread, or in the job that has it).
namespace helix::collections
{
    struct SafetyHandle
    {
        void* node;
        i32 version;
        i32 flags;
    };

    template <class T>
    struct NativeArray
    {
        T* buffer;
        i32 length;
        i32 allocator;
        i32 minIndex;
        i32 maxIndex;
        SafetyHandle safety;

        Span<T> AsSpan() const { return Span<T>(buffer, usize(length)); }
        usize Size() const { return usize(length); }
        T& operator[](usize i) const { return buffer[i]; }
    };

    // Elements `stride` bytes apart (a field of the elements of an array, or part of an array).
    template <class T>
    struct NativeSlice
    {
        u8* buffer;
        i32 stride;
        i32 length;
        i32 minIndex;
        i32 maxIndex;
        SafetyHandle safety;

        usize Size() const { return usize(length); }
        T& operator[](usize i) const { return *reinterpret_cast<T*>(buffer + i * usize(stride)); }
        // Contiguous slices only (stride == sizeof(T)).
        Span<T> AsSpan() const { return stride == i32(sizeof(T)) ? Span<T>(reinterpret_cast<T*>(buffer), usize(length)) : Span<T>(); }
    };

    template <class T>
    struct UnsafeList
    {
        T* ptr;
        i32 length;
        i32 capacity;
        u16 allocatorIndex;
        u16 allocatorVersion;
        i32 padding;
    };

    template <class T>
    struct NativeList
    {
        UnsafeList<T>* list;
        SafetyHandle safety;

        Span<T> AsSpan() const { return list ? Span<T>(list->ptr, usize(list->length)) : Span<T>(); }
        usize Size() const { return list ? usize(list->length) : 0; }
    };

    static_assert(sizeof(SafetyHandle) == 16 && sizeof(NativeArray<i32>) == 40 && sizeof(NativeSlice<i32>) == 40);
    static_assert(sizeof(UnsafeList<i32>) == 24 && sizeof(NativeList<i32>) == 24);
}
