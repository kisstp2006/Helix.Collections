using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Helix.Collections;
using Helix.Collections.LowLevel.Unsafe;
using Helix.Jobs;
using Helix.Scripting;

namespace Tests
{
    // A list filled in parallel, then read by a job whose array is the list's when it runs.
    struct FillListJob : IJobParallelFor
    {
        public NativeList<int>.ParallelWriter list;
        public void Execute(int index) => list.AddNoResize(index);
    }

    struct SumDeferredJob : IJob
    {
        [ReadOnly] public NativeArray<int> items;
        public NativeReference<long> sum;
        public void Execute()
        {
            long s = 0;
            for (int i = 0; i < items.Length; i++)
                s += items[i];
            sum.Value = s;
        }
    }

    struct EnqueueJob : IJobParallelFor
    {
        public NativeQueue<int>.ParallelWriter queue;
        public void Execute(int index) => queue.Enqueue(index);
    }

    struct CountJob : IJob
    {
        [ReadOnly] public NativeHashMap<int, int>.ReadOnly map;
        public NativeReference<int> found;
        public void Execute()
        {
            int n = 0;
            for (int i = 0; i < 100; i++)
                if (map.ContainsKey(i))
                    n++;
            found.Value = n;
        }
    }

    struct Point
    {
        public float x, y;
    }

    static unsafe class Program
    {
        static int s_Failed;

        static void Note(string text)
        {
            var note = (delegate* unmanaged[Cdecl]<byte*, int, void>)Host.FindExport("CollectionsTests.Note");
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            fixed (byte* p = bytes)
                note(p, bytes.Length);
        }

        static void Check(bool condition, string what)
        {
            if (condition)
                return;
            s_Failed++;
            Note("FAILED: " + what);
        }

        static string Fail(Action a)
        {
            try
            {
                a();
                return "none";
            }
            catch (Exception e)
            {
                return e.GetType().Name + ": " + e.Message;
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static int Run()
        {
            try
            {
                Lists();
                Maps();
                Queues();
                References();
                Spans();
            }
            catch (Exception e)
            {
                s_Failed++;
                Note("EXCEPTION: " + e);
            }
            return s_Failed;
        }

        static void Lists()
        {
            var list = new NativeList<int>(Allocator.Temp);
            for (int i = 0; i < 10; i++)
                list.Add(i);
            list.RemoveAt(0);
            list.RemoveAtSwapBack(0);
            list.InsertRange(1, 2);
            list[1] = 100;
            list[2] = 200;
            Check(list.Length == 10 && list[0] == 9 && list[1] == 100 && list[3] == 2 && list.Contains(200) && list.IndexOf(9) == 0, "NativeList edits: " + string.Join(",", list.AsArray().ToArray()));
            list.Sort();
            Check(list[0] == 2 && list[9] == 200 && list.BinarySearch(100) == 8, "Sort and BinarySearch");
            list.Resize(12, NativeArrayOptions.ClearMemory);
            Check(list.Length == 12 && list[11] == 0 && list.Capacity >= 12, "Resize");
            list.Clear();
            Check(list.IsEmpty, "Clear");
            list.Dispose();

            // Parallel writers, then a job reading the list through a deferred array.
            var filled = new NativeList<int>(1000, Allocator.TempJob);
            var sum = new NativeReference<long>(Allocator.TempJob);
            JobHandle fill = new FillListJob { list = filled.AsParallelWriter() }.Schedule(1000, 16);
            JobHandle total = new SumDeferredJob { items = filled.AsDeferredJobArray(), sum = sum }.Schedule(fill);
            total.Complete();
            Check(filled.Length == 1000 && sum.Value == 499500, "parallel writer and deferred array: " + filled.Length + " " + sum.Value);
            var small = new NativeList<int>(1, Allocator.Temp);
            Check(Fail(() => small.AsParallelWriter().AddNoResize(1)) == "none" && Fail(() => small.AsParallelWriter().AddNoResize(2)).StartsWith("InvalidOperationException"),
                  "AddNoResize past the capacity throws");
            small.Dispose();
            sum.Dispose();
            filled.Dispose();
        }

        static void Maps()
        {
            var map = new NativeHashMap<int, int>(4, Allocator.TempJob);
            for (int i = 0; i < 100; i += 2)
                map.Add(i, i * 10);
            Check(map.Count == 50 && map[10] == 100 && map.ContainsKey(98) && !map.ContainsKey(3), "NativeHashMap Add and lookups");
            Check(Fail(() => map.Add(10, 1)) == "ArgumentException: An item with the same key has already been added: 10", "a key twice");
            Check(Fail(() => { int x = map[3]; }) == "ArgumentException: Key: 3 is not present.", "a missing key");
            map[3] = 30;
            Check(map.Remove(10) && !map.Remove(10) && map.Count == 50 && map.TryGetValue(3, out int v) && v == 30, "Remove and the setter");
            long keys = 0;
            foreach (KVPair<int, int> pair in map)
                keys += pair.Key;
            NativeArray<int> values = map.GetValueArray(Allocator.Temp);
            Check(keys == 2450 - 10 + 3 && values.Length == 50, "enumeration and arrays");
            values.Dispose();
            var found = new NativeReference<int>(Allocator.TempJob);
            new CountJob { map = map.AsReadOnly(), found = found }.Schedule().Complete();
            Check(found.Value == 50, "ReadOnly in a job: " + found.Value);
            found.Dispose();
            map.Dispose();
        }

        static void Queues()
        {
            var queue = new NativeQueue<int>(Allocator.TempJob);
            for (int i = 0; i < 40; i++)
                queue.Enqueue(i);
            Check(queue.Dequeue() == 0 && queue.Peek() == 1 && queue.Count == 39, "NativeQueue order");
            queue.Clear();
            new EnqueueJob { queue = queue.AsParallelWriter() }.Schedule(5000, 64).Complete();
            long sum = 0;
            int n = queue.Count;
            while (queue.TryDequeue(out int item))
                sum += item;
            Check(n == 5000 && sum == 12497500 && queue.IsEmpty(), "parallel enqueue: " + n + " " + sum);
            Check(Fail(() => queue.Dequeue()) == "InvalidOperationException: Trying to dequeue from an empty queue", "an empty queue");
            queue.Dispose();
        }

        static void References()
        {
            var a = new NativeReference<int>(5, Allocator.Temp);
            var b = new NativeReference<int>(Allocator.Temp);
            b.CopyFrom(a);
            NativeReference<int>.ReadOnly ro = a;
            NativeReference<int> same = a;
            Check(b.Value == 5 && ro.Value == 5 && a != b && a == same, "NativeReference");
            a.Dispose();
            b.Dispose();
        }

        static void Spans()
        {
            var sumArray = (delegate* unmanaged[Cdecl]<NativeArray<int>*, long>)Host.FindExport("CollectionsTests.SumArray");
            var sumList = (delegate* unmanaged[Cdecl]<NativeList<int>*, long>)Host.FindExport("CollectionsTests.SumList");
            var sumSlice = (delegate* unmanaged[Cdecl]<NativeSlice<float>*, float>)Host.FindExport("CollectionsTests.SumSlice");
            var doubled = (delegate* unmanaged[Cdecl]<NativeArray<int>, void>)Host.FindExport("CollectionsTests.Doubled");
            var array = new NativeArray<int>(new[] { 1, 2, 3, 4 }, Allocator.Temp);
            var list = new NativeList<int>(Allocator.Temp);
            list.Add(10);
            list.Add(20);
            var points = new NativeArray<Point>(3, Allocator.Temp);
            for (int i = 0; i < 3; i++)
                points[i] = new Point { x = i, y = 100 };
            NativeSlice<float> ys = new NativeSlice<Point>(points).SliceWithStride<float>(4);
            // By value only off the Web: Mono's WebAssembly interpreter passes no struct of several fields by value to
            // native code (pointers to containers work everywhere).
            if (!OperatingSystem.IsBrowser())
                doubled(array);
            else
                for (int i = 0; i < array.Length; i++)
                    array[i] *= 2;
            Check(sumArray(&array) == 20 && sumList(&list) == 30 && sumSlice(&ys) == 300, "C++ reads the containers as spans");
            array.Dispose();
            list.Dispose();
            points.Dispose();
        }
    }
}
