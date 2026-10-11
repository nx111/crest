// Executes unmodified production registration/provider code. The known affine
// sampler isolates coordinate ownership; it does not substitute for FFT validation.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Crest;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

static class Program
{
    static int Main()
    {
        var tests = new (string name, Action run)[]
        {
            ("persistent queries carry coordinates across input buffers", PersistentCoordinates),
            ("expired owners compact coordinates without changing survivors", ExpiredOwner),
            ("quad packing respects the exact capacity after a flip", ExactCapacity),
            ("growing output cannot consume another owner's quad", CrossOwnerOutput),
            ("growing output cannot consume unused SIMD lanes", PaddingOutput),
            ("a missing output channel preserves all output arrays", AtomicOutputs),
            ("partial outputs use the correct retained coordinates", PartialOutputs),
            ("capacity failure returns TooManyQueries and preserves registration", CapacityFailure),
            ("displacements normals and velocities use persistent coordinates", AllChannels),
            ("cleanup completes deferred work and is idempotent", RepeatedCleanup),
            ("queries and updates after cleanup do not access disposed storage", QueryAfterCleanup),
        };
        int failed = 0;
        foreach (var test in tests)
        {
            Frame(0); Debug.Errors.Clear(); JobHandle.ScheduledCounts.Clear();
            try { test.run(); Console.WriteLine("PASS: " + test.name); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL: " + test.name + ": " + error.Message); }
        }
        Console.WriteLine($"Baked query lifecycle: {tests.Length-failed}/{tests.Length} passed; {failed} failed.");
        Console.WriteLine("Production C# provider; managed deferred-job/container substitutes and a known affine sampling field. No Unity/Burst/GPU validation.");
        return failed == 0 ? 0 : 1;
    }

    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Near(float actual, float expected, string message)
        => Check(Math.Abs(actual - expected) < .001f, $"{message}: expected {expected}, got {actual}");
    static void Frame(int frame) { Time.frameCount = frame; OceanRenderer.Instance.CurrentTime = frame; }
    static Vector3[] Points(params float[] values) => values.Select(x => new Vector3(x, 0, x + 10)).ToArray();
    static CollProviderBakedFFT Provider() => new CollProviderBakedFFT(new FFTBakedData());
    static void Advance(CollProviderBakedFFT provider, int frame) { Frame(frame); provider.UpdateQueries(); }
    static float Height(float x, int time) => 2*x + 3*(x+10) + time + OceanRenderer.Instance.SeaLevel;
    static int Status(string name) => (int)Enum.Parse(typeof(CollProviderBakedFFT).GetNestedType("QueryStatus", BindingFlags.NonPublic), name);
    static void NoErrors() => Check(Debug.Errors.Count == 0, string.Join(" | ", Debug.Errors));

    static void PersistentCoordinates()
    {
        var provider = Provider();
        try
        {
            var points = Points(2, 5, 9); var heights = new float[3];
            provider.Query(1, 0, points, heights, null, null);
            Advance(provider, 0); Advance(provider, 1); Advance(provider, 2);
            Check(provider.RetrieveSucceeded(provider.Query(1, 0, points, heights, null, null)), "persistent result unavailable");
            for (int i = 0; i < points.Length; i++) Near(heights[i], Height(points[i].x, 1), "persistent point " + i);
            NoErrors();
        }
        finally { provider.CleanUp(); }
    }

    static void ExpiredOwner()
    {
        var provider = Provider();
        try
        {
            provider.Query(1, 0, Points(100), new float[1], null, null);
            provider.Query(2, 0, Points(20), new float[1], null, null);
            Advance(provider, 0); Advance(provider, 1);
            Frame(9); provider.Query(2, 0, Points(20), new float[1], null, null);
            Advance(provider, 9); Advance(provider, 10); Advance(provider, 11); Advance(provider, 12);
            var result = new float[1];
            Check(provider.RetrieveSucceeded(provider.Query(2, 0, Points(20), result, null, null)), "survivor unavailable");
            Near(result[0], Height(20, 11), "compacted survivor");
            NoErrors();
        }
        finally { provider.CleanUp(); }
    }

    static void ExactCapacity()
    {
        var provider = Provider();
        try
        {
            var points = Enumerable.Repeat(new Vector3(2, 0, 12), 8192 * 4).ToArray();
            provider.Query(1, 0, points, new float[points.Length], null, null);
            Advance(provider, 0); Advance(provider, 1);
            Check(JobHandle.ScheduledCounts.Count == 2 && JobHandle.ScheduledCounts.All(count => count == 8192),
                "exclusive endpoint introduced an extra scheduled quad: " + string.Join(",", JobHandle.ScheduledCounts));
            NoErrors();
        }
        finally { provider.CleanUp(); }
    }

    static void CrossOwnerOutput() => OversizedOutput(1, 5);
    static void PaddingOutput() => OversizedOutput(3, 4);
    static void OversizedOutput(int originalCount, int requestedCount)
    {
        var provider = Provider();
        try
        {
            var original = Points(Enumerable.Range(1, originalCount).Select(x => (float)x).ToArray());
            provider.Query(1, 0, original, new float[originalCount], null, null);
            provider.Query(2, 0, Points(50, 51, 52, 53), new float[4], null, null);
            Advance(provider, 0); Advance(provider, 1);
            var outputs = Enumerable.Repeat(-999f, requestedCount).ToArray();
            var points = Points(Enumerable.Range(1, requestedCount).Select(x => (float)x).ToArray());
            var status = provider.Query(1, 0, points, outputs, null, null);
            Check(!provider.RetrieveSucceeded(status), "oversized retained result was accepted");
            Check(outputs.All(value => value == -999f), "failed output was partially overwritten");
            NoErrors();
        }
        finally { provider.CleanUp(); }
    }

    static void AtomicOutputs()
    {
        var provider = Provider();
        try
        {
            var points = Points(2, 5);
            provider.Query(1, 0, points, new float[2], null, null);
            Advance(provider, 0); Advance(provider, 1);
            var heights = new[] { -999f, -999f };
            var normals = new[] { new Vector3(-9, -8, -7), new Vector3(-9, -8, -7) };
            Check(!provider.RetrieveSucceeded(provider.Query(1, 0, points, heights, normals, null)), "missing normals accepted");
            Check(heights.All(value => value == -999) && normals.All(value => value.x == -9), "missing channel partially wrote other outputs");
        }
        finally { provider.CleanUp(); }
    }

    static void PartialOutputs()
    {
        var provider = Provider();
        try
        {
            var points = Points(2, 5, 9);
            provider.Query(1, 0, points, new float[3], null, null);
            Advance(provider, 0); Advance(provider, 1);
            var heights = new float[1];
            Check(provider.RetrieveSucceeded(provider.Query(1, 0, points, heights, null, null)), "partial output rejected");
            Near(heights[0], Height(2, 0), "partial output origin");
            NoErrors();
        }
        finally { provider.CleanUp(); }
    }

    static void CapacityFailure()
    {
        var provider = Provider();
        try
        {
            var original = Enumerable.Repeat(new Vector3(2, 0, 12), 8191 * 4).ToArray();
            provider.Query(1, 0, original, new float[original.Length], null, null);
            provider.Query(2, 0, Points(50), new float[1], null, null);
            var tooLarge = Enumerable.Repeat(new Vector3(8, 0, 18), 8192 * 4).ToArray();
            var status = provider.Query(1, 0, tooLarge, new float[tooLarge.Length], null, null);
            Check(status == Status("TooManyQueries"), "registration failure did not propagate TooManyQueries");
            Debug.Errors.Clear();
            Advance(provider, 0); Advance(provider, 1);
            var result = new float[1];
            Check(provider.RetrieveSucceeded(provider.Query(1, 0, original, result, null, null)), "capacity failure destroyed existing registration");
            Near(result[0], Height(2, 0), "capacity rollback changed original data");
            NoErrors();
        }
        finally { provider.CleanUp(); }
    }

    static void AllChannels()
    {
        var provider = Provider();
        try
        {
            var points = Points(2, 5, 9);
            var disps = new Vector3[3]; var norms = new Vector3[3]; var vels = new Vector3[3];
            provider.Query(1, 0, points, disps, norms, vels);
            Advance(provider, 0); Advance(provider, 1); Advance(provider, 2);
            Check(provider.RetrieveSucceeded(provider.Query(1, 0, points, disps, norms, vels)), "vector channels unavailable");
            for (int i=0; i<points.Length; i++)
            {
                Near(disps[i].x, points[i].x*.1f, "displacement x");
                Near(disps[i].y, Height(points[i].x, 1)-OceanRenderer.Instance.SeaLevel, "displacement y");
                Near(vels[i].y, 1f, "velocity");
                Near(norms[i].y, 1f/MathF.Sqrt(14f), "normal");
            }
            NoErrors();
        }
        finally { provider.CleanUp(); }
    }

    static void RepeatedCleanup()
    {
        var provider = Provider();
        provider.Query(1, 0, Points(2), new float[1], null, null);
        Advance(provider, 0);
        provider.CleanUp();
        provider.CleanUp();
        NoErrors();
    }

    static void QueryAfterCleanup()
    {
        var provider = Provider();
        provider.Query(1, 0, Points(2), new float[1], null, null);
        Advance(provider, 0); Advance(provider, 1);
        provider.CleanUp();
        var output = new[] { -999f };
        Check(!provider.RetrieveSucceeded(provider.Query(1, 0, Points(2), output, null, null)), "disposed provider returned success");
        Near(output[0], -999f, "disposed provider changed output");
        var scheduled = JobHandle.ScheduledCounts.Count;
        provider.UpdateQueries();
        Check(JobHandle.ScheduledCounts.Count == scheduled, "disposed provider scheduled work");
        NoErrors();
    }
}

namespace UnityEngine
{
    public struct Vector3
    {
        public float x,y,z;
        public Vector3(float x, float y, float z) { this.x=x; this.y=y; this.z=z; }
        public static Vector3 zero => default;
    }
    public static class Time { public static int frameCount; }
    public static class Debug
    {
        public static List<string> Errors = new List<string>();
        public static void LogError(string message) => Errors.Add(message);
        public static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    }
}
namespace Unity.Burst { public class BurstCompileAttribute : Attribute { public bool CompileSynchronously; } }
namespace Unity.Collections
{
    public enum Allocator { Persistent }
    public class ReadOnlyAttribute : Attribute { }
    public class WriteOnlyAttribute : Attribute { }
    sealed class Store<T> { public T[] Data; public bool Disposed; }
    public struct NativeArray<T>
    {
        Store<T> _store;
        public NativeArray(int count, Allocator allocator) { _store = new Store<T> { Data = new T[count] }; }
        public bool IsCreated => _store != null && !_store.Disposed;
        T[] Data { get { if (!IsCreated) throw new Exception("disposed native array accessed"); return _store.Data; } }
        public int Length => Data.Length;
        public T this[int index] { get => Data[index]; set => Data[index]=value; }
        public void Dispose() { if (!IsCreated) throw new Exception("native array double disposal"); _store.Disposed=true; }
        public static void Copy(NativeArray<T> source, int sourceIndex, NativeArray<T> destination, int destinationIndex, int length)
            => Array.Copy(source.Data, sourceIndex, destination.Data, destinationIndex, length);
    }
}
namespace Unity.Jobs
{
    public interface IJobParallelFor { void Execute(int index); }
    sealed class JobState { public Action Run; public bool Done; }
    public struct JobHandle
    {
        JobState _state;
        public static readonly List<int> ScheduledCounts = new List<int>();
        public bool IsCompleted => _state == null || _state.Done;
        public void Complete() { if (!IsCompleted) { _state.Run(); _state.Done=true; } }
        internal static JobHandle Create(Action action) => new JobHandle { _state = new JobState { Run=action } };
        public static JobHandle CombineDependencies(JobHandle first, JobHandle second) => Create(() => { first.Complete(); second.Complete(); });
        public static void ScheduleBatchedJobs() { }
    }
    public static class Extensions
    {
        public static JobHandle Schedule<T>(this T job, int count, int batchSize) where T : struct, IJobParallelFor
        {
            JobHandle.ScheduledCounts.Add(count);
            return JobHandle.Create(() => { for (int i=0; i<count; i++) job.Execute(i); });
        }
    }
}
namespace Unity.Mathematics
{
    public struct half { }
    public struct int3
    {
        public int x,y,z;
        public int3(int x,int y,int z) { this.x=x;this.y=y;this.z=z; }
        public int this[int index] => index==0?x:index==1?y:z;
    }
    public struct float3
    {
        public float x,y,z;
        public float3(float x,float y,float z) { this.x=x;this.y=y;this.z=z; }
        public float this[int index] => index==0?x:index==1?y:z;
    }
    public struct float4
    {
        public float x,y,z,w;
        public float4(float x,float y,float z,float w) { this.x=x;this.y=y;this.z=z;this.w=w; }
        public float this[int index] { get => index==0?x:index==1?y:index==2?z:w; set { if(index==0)x=value;else if(index==1)y=value;else if(index==2)z=value;else w=value; } }
        public static implicit operator float4(float value) => new float4(value,value,value,value);
        public static float4 operator +(float4 a,float4 b) => new float4(a.x+b.x,a.y+b.y,a.z+b.z,a.w+b.w);
        public static float4 operator -(float4 a,float4 b) => new float4(a.x-b.x,a.y-b.y,a.z-b.z,a.w-b.w);
        public static float4 operator *(float4 a,float4 b) => new float4(a.x*b.x,a.y*b.y,a.z*b.z,a.w*b.w);
        public static float4 operator /(float4 a,float4 b) => new float4(a.x/b.x,a.y/b.y,a.z/b.z,a.w/b.w);
    }
    public static class math
    {
        public static float3 normalize(float3 v) { var size=MathF.Sqrt(v.x*v.x+v.y*v.y+v.z*v.z); return new float3(v.x/size,v.y/size,v.z/size); }
    }
}
namespace Crest
{
    public class OceanRenderer
    {
        public static OceanRenderer Instance = new OceanRenderer();
        public float CurrentTime;
        public float SeaLevel = 7f;
    }
    public struct FFTBakedDataParameters { }
    public class FFTBakedData
    {
        public FFTBakedDataParameters _parameters;
        public NativeArray<half> _framesFlattenedNative = new NativeArray<half>(1, Allocator.Persistent);
        public static float4 SampleHeightXZT(float4 x, float4 z, float t, FFTBakedDataParameters parameters, in NativeArray<half> frames)
            => 2f*x + 3f*z + t;
        public static void SampleDisplacementXZTWithInvert(float4 x,float4 z,float t,FFTBakedDataParameters parameters,in NativeArray<half> frames,
            out float4 dx,out float4 dy,out float4 dz) { dx=x*.1f;dy=SampleHeightXZT(x,z,t,parameters,in frames);dz=z*.2f; }
    }
}
