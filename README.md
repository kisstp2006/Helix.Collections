# Helix.Collections

The native containers of the Helix engine's C# script API beyond `NativeArray`: lists, hash maps, queues and single
values in native memory, for jobs and for C++ code. They use the core module's allocators (`Temp`, `TempJob`,
`Persistent`), its job system and, in development builds, its safety system (use after Dispose, jobs using a container
without a dependency, `[ReadOnly]`) and leak detection. A C++ header gives the memory layouts of the containers, so
C++ code reads their elements as Helix.Core spans where they are.

## Contents

| Path | Content |
|---|---|
| `Source/Helix.Collections/NativeList.cs` | `NativeList<T>` (its `ParallelWriter`, `AsArray`, `AsDeferredJobArray`), `UnsafeList<T>` (the list's header), `NativeListExtensions`, `NativeSortExtension` |
| `Source/Helix.Collections/NativeHashMap.cs` | `NativeHashMap<TKey, TValue>` (`ReadOnly`, `Enumerator`), `KVPair`, `NativeKeyValueArrays` |
| `Source/Helix.Collections/NativeQueue.cs` | `NativeQueue<T>` (`ParallelWriter`, `ReadOnly`), `NativeReference<T>` (`ReadOnly`) |
| `Source/Helix.Collections/Allocators.cs` | `AllocatorManager.AllocatorHandle` (the built in allocators), `INativeDisposable`, `INativeList<T>`, `IIndexable<T>` |
| `Source/Helix/Collections/NativeContainers.h` | C++: the layouts of `NativeArray<T>`, `NativeSlice<T>`, `UnsafeList<T>` and `NativeList<T>` with `AsSpan()` |
| `Tests/` | The containers on the main thread and in jobs (parallel writers, a deferred array, read only views), C++ code summing them as spans; `Tests/Api/Helix.Collections.csv` is the inventory of the public surface |

## Use

```csharp
using Helix.Collections;
using Helix.Jobs;

struct Collect : IJobParallelFor
{
    public NativeList<int>.ParallelWriter output;
    public void Execute(int index) { if (index % 3 == 0) output.AddNoResize(index); }
}

var list = new NativeList<int>(1000, Allocator.TempJob);
new Collect { output = list.AsParallelWriter() }.Schedule(1000, 64).Complete();
list.Dispose();
```

```cpp
#include <Helix/Collections/NativeContainers.h>

// A native function C# passes a NativeArray<int> to (by pointer or by value).
i64 Sum(const helix::collections::NativeArray<helix::i32>* array)
{
    helix::i64 sum = 0;
    for (helix::i32 v : array->AsSpan())
        sum += v;
    return sum;
}
```

- Copies of a container struct share its memory; dispose it once (`Dispose()`, or `Dispose(JobHandle)` after a job).
- `NativeHashMap` is for one writer at a time; its order is not specified.
- `NativeQueue.ParallelWriter` and `NativeList.ParallelWriter` may be used by parallel jobs (the list does not grow
  from them: `AddNoResize` within its capacity).

## Build

```
git clone https://github.com/kisstp2006/Helix.Collections
git -C Helix.Collections submodule update --init
cmake --preset windows-debug -S Helix.Collections
cmake --build Helix.Collections/build/windows-debug
ctest --test-dir Helix.Collections/build/windows-debug
```

The .NET 11 SDK is needed (`HELIX_DOTNET_ROOT` or `DOTNET_ROOT`).

## License

GPL-3.0, see `LICENSE`.
