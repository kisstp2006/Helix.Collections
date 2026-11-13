#include <Helix/Collections/NativeContainers.h>

#include <Helix/Engine/Jobs/ManagedJobs.h>
#include <Helix/Scripting/Bindings/Interop.h>
#include <Helix/Scripting/Host/DotnetHost.h>
#include <Helix/Scripting/Types/Type.h>
#include <Helix/Testing/Test.h>

#include <bit>
#include <cstdio>

using namespace helix;

namespace
{
    // C++ code given containers by C#: their elements as spans.
    i64 SumArray(const collections::NativeArray<i32>* array)
    {
        i64 sum = 0;
        for (i32 v : array->AsSpan())
            sum += v;
        return sum;
    }

    i64 SumList(const collections::NativeList<i32>* list)
    {
        i64 sum = 0;
        for (i32 v : list->AsSpan())
            sum += v;
        return sum;
    }

    f32 SumSlice(const collections::NativeSlice<f32>* slice)
    {
        f32 sum = 0;
        for (usize i = 0; i < slice->Size(); i++)
            sum += (*slice)[i];
        return sum;
    }

    void Doubled(collections::NativeArray<i32> array)
    {
        for (i32& v : array.AsSpan())
            v *= 2;
    }

    void Note(const char* text, i32 size) { std::printf("    %.*s\n", int(size), text); }

    // The .NET host with the engine's C# side and the test assembly, once per process.
    bool Start(i32 (*&run)())
    {
        using namespace scripting;
        static i32 (*s_run)() = nullptr;
        static int state = 0;
        if (state != 0)
        {
            run = s_run;
            return state > 0;
        }
        state = -1;
        RegisterNativeExport("CollectionsTests.SumArray", std::bit_cast<const void*>(&SumArray));
        RegisterNativeExport("CollectionsTests.SumList", std::bit_cast<const void*>(&SumList));
        RegisterNativeExport("CollectionsTests.SumSlice", std::bit_cast<const void*>(&SumSlice));
        RegisterNativeExport("CollectionsTests.Doubled", std::bit_cast<const void*>(&Doubled));
        RegisterNativeExport("CollectionsTests.Note", std::bit_cast<const void*>(&Note));
        String error;
        DotnetHostDesc desc;
        desc.runtimeAssembly = HX_TEST_RUNTIME_ASSEMBLY;
        if (!DotnetHost::Start(desc, error))
        {
            std::printf("    %.*s\n", int(error.Size()), error.Data());
            return false;
        }
        (void)FindType("HelixEngine.Object");
        AssemblyHandle core = DotnetHost::LoadAssembly(HX_TEST_CORE_MODULE, error);
        auto start = std::bit_cast<i32 (*)()>(DotnetHost::GetFunction(core, "HelixEngine.Internal.ScriptBridge", "Start", error));
        if (!start || start() != 0)
        {
            std::printf("    the engine's C# side did not start: %.*s\n", int(error.Size()), error.Data());
            return false;
        }
        AssemblyHandle tests = DotnetHost::LoadAssembly(HX_TEST_ASSEMBLY, error);
        s_run = std::bit_cast<i32 (*)()>(DotnetHost::GetFunction(tests, "Tests.Program", "Run", error));
        if (!s_run)
        {
            std::printf("    %.*s\n", int(error.Size()), error.Data());
            return false;
        }
        run = s_run;
        state = 1;
        return true;
    }
}

HX_TEST(Collections, TheContainersInJobsAndAsSpans)
{
    i32 (*run)() = nullptr;
    HX_REQUIRE(Start(run));
    // The C# checks (each failure is printed); 0 failures.
    HX_CHECK(run() == 0);
    engine::JobsNative::EndFrame();
}
