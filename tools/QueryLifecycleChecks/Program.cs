// Runs the unmodified production QueryBase source against controlled API substitutes.
// These are scheduling/ownership/array tests, not a Unity or GPU implementation.
using System;
using System.Collections.Generic;
using System.Linq;
using Crest;
using UnityEngine;
using UnityEngine.Rendering;

static class Program
{
    static int Main()
    {
        var tests = new (string name, Action run)[]
        {
            ("bounded backlog keeps every pending request and resumes with latest positions", Backlog),
            ("non-head errors release their own maps only", NonHeadError),
            ("current and previous result maps survive repeated errors", RetainedResults),
            ("late old completions cannot rewind results", LateCompletion),
            ("one callback drains the newest completed result without discarding pending work", CompletionBatch),
            ("synchronous success and failure do not leak a reserved slot", SynchronousCompletion),
            ("Update drains a completed request before its callback", Polling),
            ("cleanup is idempotent and late callbacks release only this instance", Cleanup),
            ("quitting drains pending callbacks and releases the provider exactly once", Quitting),
            ("quitting also drains providers already awaiting asynchronous cleanup", QuittingAfterCleanup),
            ("shutdown waits only for its own requests even without callbacks", ShutdownIsolation),
            ("dispatch and readback exceptions roll back the reserved next batch", SubmissionFailure),
            ("persistent compaction moves query positions with registration ranges", PersistentCompaction),
            ("dictionary reuse cannot overwrite another persistent query", ReorderedCompaction),
            ("persistent normal queries carry all three coordinates and minimum grid size", PersistentNormals),
            ("clear and remove affect only the writable batch", ClearAndRemove),
            ("capacity counts normal samples and allows full-buffer owner updates", Capacity),
            ("two retained results and seven pending requests fit the fixed pool", FullPool),
            ("expired readback data is never accessed", ExpiredReadback),
            ("velocity uses each result's own compacted owner ranges", VelocityRanges),
            ("completion order is independent of a rewound simulation clock", RewoundClock),
            ("resized output cannot read a neighboring owner's retained result", ResizedOutput),
            ("invalid output shapes leave every output untouched and partial reads remain valid", OutputShapes),
            ("velocity output cannot exceed either retained owner range", VelocityBounds),
        };
        var failed = 0;
        foreach (var test in tests)
        {
            Reset();
            try
            {
                test.run();
                Console.WriteLine("PASS: " + test.name);
            }
            catch (Exception exception)
            {
                failed++;
                Console.WriteLine("FAIL: " + test.name + ": " + exception.Message);
            }
        }
        Console.WriteLine($"Query lifecycle: {tests.Length - failed}/{tests.Length} passed; {failed} failed.");
        Console.WriteLine("Executes production C# with deterministic managed substitutes; no Unity assembly, GPU, driver, or window validation.");
        return failed == 0 ? 0 : 1;
    }

    static void Reset(int capacity = 64)
    {
        Application.Reset();
        AsyncGPUReadback.Reset();
        ComputeBuffer.All.Clear();
        ComputeShader.DispatchCount = 0;
        ComputeShader.ThrowOnDispatch = false;
        Debug.Errors.Clear();
        Time.frameCount = 0;
        Time.time = 0f;
        OceanRenderer.Instance = new OceanRenderer();
        OceanRenderer.Instance._lodDataAnimWaves.Settings.MaxQueryCount = capacity;
    }

    static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    static void Near(float actual, float expected, string message)
    {
        Check(Math.Abs(actual - expected) < 0.00001f, $"{message}: expected {expected}, got {actual}");
    }

    static Vector3[] Points(params float[] x) => x.Select(value => new Vector3(value, 0f, value + 100f)).ToArray();

    static void Frame(int index)
    {
        Time.frameCount = index;
        Time.time = index;
    }

    static int Submit(TestQuery query, int frame, int owner, params float[] x)
    {
        Frame(frame);
        Check(query.Post(owner, Points(x)), "query registration failed");
        var index = AsyncGPUReadback.Requests.Count;
        query.UpdateQueries();
        Check(AsyncGPUReadback.Requests.Count == index + 1, "expected exactly one GPU readback");
        return index;
    }

    static void Result(TestQuery query, int owner, params float[] x)
    {
        var result = new Vector3[x.Length];
        Check(query.Result(owner, result), $"missing result for owner {owner}");
        for (int i = 0; i < x.Length; i++) Near(result[i].x, x[i], $"owner {owner}, point {i}");
    }

    static void NoErrors() => Check(Debug.Errors.Count == 0, string.Join(" | ", Debug.Errors));

    static void Backlog()
    {
        var query = new TestQuery();
        for (int frame = 0; frame < 100; frame++)
        {
            Frame(frame);
            Check(query.Post(1, Points(frame)), "stalled current batch stopped accepting updates");
            query.UpdateQueries();
            Check(query.RequestCount == Math.Min(frame + 1, 7), "pending accounting changed");
            Check(AsyncGPUReadback.Requests.Count == Math.Min(frame + 1, 7), "GPU work exceeded seven pending requests");
        }
        Check(ComputeShader.DispatchCount == 7, "dispatch occurred during backpressure");
        AsyncGPUReadback.Complete(6);
        Check(query.RequestCount == 6, "completion discarded older pending requests");
        Result(query, 1, 6);
        Frame(100);
        query.UpdateQueries();
        Check(query.RequestCount == 7, "failed to resume after a slot became free");
        Near(AsyncGPUReadback.Requests[7].Data[0].x, 99, "resumed batch was not the latest position");
        AsyncGPUReadback.Complete(7);
        for (int i = 5; i >= 0; i--) AsyncGPUReadback.Complete(i);
        Check(query.RequestCount == 0, "late requests were not retired");
        Result(query, 1, 99);
        NoErrors();
    }

    static void NonHeadError()
    {
        var query = new TestQuery();
        Submit(query, 0, 100, 111);
        query.CompactQueryStorage();
        Submit(query, 1, 200, 222);
        query.CompactQueryStorage();
        Submit(query, 2, 300, 333);
        AsyncGPUReadback.Complete(1, error: true);
        Check(query.RequestCount == 2, "non-head error changed another request");
        for (int i = 3; i < 30; i++)
        {
            query.CompactQueryStorage();
            var index = Submit(query, i, i + 1000, i);
            AsyncGPUReadback.Complete(index, error: true);
        }
        AsyncGPUReadback.Complete(0);
        Result(query, 100, 111);
        AsyncGPUReadback.Complete(2);
        Result(query, 300, 333);
        Check(query.RequestCount == 0, "error recovery left phantom requests");
        NoErrors();
    }

    static void RetainedResults()
    {
        var query = new TestQuery();
        AsyncGPUReadback.Complete(Submit(query, 0, 31, 5));
        AsyncGPUReadback.Complete(Submit(query, 1, 31, 9));
        for (int frame = 2; frame < 40; frame++)
        {
            query.CompactQueryStorage();
            AsyncGPUReadback.Complete(Submit(query, frame, frame + 1000, frame), error: true);
        }
        Result(query, 31, 9);
        var velocity = new Vector3[1];
        Check(query.Velocity(31, velocity) == 0, "previous result mapping was recycled");
        Near(velocity[0].x, 4f, "retained velocity");
        NoErrors();
    }

    static void LateCompletion()
    {
        var query = new TestQuery();
        Submit(query, 1, 1, 10);
        Submit(query, 2, 1, 20);
        Submit(query, 3, 1, 30);
        AsyncGPUReadback.Complete(2);
        Check(query.RequestCount == 2, "new result dropped pending predecessors");
        AsyncGPUReadback.Complete(0);
        AsyncGPUReadback.Complete(1);
        Result(query, 1, 30);
        Check(query.Velocity(1, new Vector3[1]) != 0, "late older frame became velocity history");
        NoErrors();
    }

    static void CompletionBatch()
    {
        var query = new TestQuery();
        Submit(query, 1, 1, 10);
        Submit(query, 2, 1, 20);
        Submit(query, 3, 1, 30);
        Submit(query, 4, 1, 40);
        AsyncGPUReadback.Complete(0, notify: false);
        AsyncGPUReadback.Complete(2, notify: false);
        AsyncGPUReadback.Notify(0);
        Result(query, 1, 30);
        Check(query.RequestCount == 2, "a completed prefix incorrectly removed a pending middle request");
        Check(AsyncGPUReadback.Requests[0].GetDataCount == 0 && AsyncGPUReadback.Requests[2].GetDataCount == 1,
            "did not read only the newest available result");
        AsyncGPUReadback.Complete(1);
        Result(query, 1, 30);
        AsyncGPUReadback.Complete(3);
        Result(query, 1, 40);
        NoErrors();
    }

    static void SynchronousCompletion()
    {
        var query = new TestQuery();
        for (int i = 0; i < 25; i++)
        {
            AsyncGPUReadback.CompleteInsideRequest = true;
            AsyncGPUReadback.ErrorInsideRequest = i % 2 != 0;
            Submit(query, i, 1, i);
            Check(query.RequestCount == 0, "synchronous completion was not drained");
            Result(query, 1, i % 2 == 0 ? i : i - 1);
        }
        NoErrors();
    }

    static void Polling()
    {
        var query = new TestQuery();
        Submit(query, 0, 1, 12);
        AsyncGPUReadback.Complete(0, notify: false);
        query.CompactQueryStorage();
        Frame(1);
        query.UpdateQueries();
        Check(query.RequestCount == 0, "Update did not drain completed work");
        Result(query, 1, 12);
        AsyncGPUReadback.Notify(0);
        Result(query, 1, 12);
        NoErrors();
    }

    static void Cleanup()
    {
        var query = new TestQuery();
        var ownedBuffers = ComputeBuffer.All.ToArray();
        AsyncGPUReadback.Complete(Submit(query, 0, 1, 1));
        AsyncGPUReadback.Complete(Submit(query, 1, 1, 2));
        Submit(query, 2, 1, 3);
        Submit(query, 3, 1, 4);
        var other = new TestQuery();
        var otherIndex = Submit(other, 4, 2, 20);
        query.CleanUp();
        query.CleanUp();
        Check(query.RequestCount == 2, "cleanup forgot outstanding requests");
        Check(AsyncGPUReadback.Requests.All(request => request.WaitCount == 0), "normal cleanup blocked on the GPU");
        Check(ownedBuffers.All(buffer => !buffer.Disposed), "GPU source released before outstanding readbacks");
        Check(!query.Post(1, Points(55)), "disposed provider accepted a query");
        Check(!query.Result(1, new Vector3[1]), "disposed provider exposed CPU result memory");
        var dispatches = ComputeShader.DispatchCount;
        query.UpdateQueries();
        Check(ComputeShader.DispatchCount == dispatches, "disposed provider submitted GPU work");
        AsyncGPUReadback.Complete(2);
        Check(ownedBuffers.All(buffer => !buffer.Disposed), "first late callback released other pending work");
        AsyncGPUReadback.Complete(3, error: true);
        Check(query.RequestCount == 0, "cleanup callback did not retire work");
        Check(ownedBuffers.All(buffer => buffer.Disposed && buffer.DisposeCount == 1), "buffers were not disposed exactly once");
        Check(AsyncGPUReadback.Requests[2].GetDataCount == 0, "late callback accessed disposed CPU results");
        AsyncGPUReadback.Notify(0);
        query.CleanUp();
        AsyncGPUReadback.Complete(otherIndex);
        Result(other, 2, 20);
        other.CleanUp();
        Check(ComputeBuffer.All.All(buffer => buffer.DisposeCount == 1), "cleanup affected another provider");
        NoErrors();
    }

    static void Quitting()
    {
        var query = new TestQuery();
        Submit(query, 0, 1, 1);
        Submit(query, 1, 1, 2);
        Check(Application.HandlerCount == 1, "provider did not register shutdown cleanup");
        Application.RaiseQuitting();
        Check(query.RequestCount == 0, "quitting left pending requests");
        Check(AsyncGPUReadback.Requests.All(request => request.WaitCount == 1 && request.GetDataCount == 0),
            "quitting did not wait once per request or accessed disposed CPU results");
        Check(ComputeBuffer.All.All(buffer => buffer.DisposeCount == 1), "quitting leaked or double-disposed GPU buffers");
        Check(Application.HandlerCount == 0, "disposed provider retained its shutdown handler");
        Application.RaiseQuitting();
        query.CleanUp();
        NoErrors();
    }

    static void QuittingAfterCleanup()
    {
        var query = new TestQuery();
        Submit(query, 0, 1, 1);
        query.CleanUp();
        Check(Application.HandlerCount == 1, "pending cleanup lost its shutdown handler");
        Check(AsyncGPUReadback.Requests[0].WaitCount == 0, "normal cleanup blocked on a readback");
        Application.RaiseQuitting();
        Check(query.RequestCount == 0 && Application.HandlerCount == 0, "quitting lost an already disposed provider");
        Check(ComputeBuffer.All.All(buffer => buffer.DisposeCount == 1), "pending cleanup leaked GPU buffers at quit");
        AsyncGPUReadback.Notify(0);
        NoErrors();
    }

    static void ShutdownIsolation()
    {
        var query = new TestQuery();
        var ownedBuffers = ComputeBuffer.All.ToArray();
        Submit(query, 0, 1, 1);
        var other = new TestQuery();
        var otherIndex = Submit(other, 1, 2, 20);
        AsyncGPUReadback.NotifyOnWait = false;
        Check(Application.HandlerCount == 2, "providers did not register independent shutdown handlers");
        Application.RaiseFirstQuitting();
        Check(query.RequestCount == 0 && ownedBuffers.All(buffer => buffer.DisposeCount == 1),
            "shutdown required a future callback to release its own buffers");
        Check(!AsyncGPUReadback.Requests[otherIndex].Done && AsyncGPUReadback.Requests[otherIndex].WaitCount == 0,
            "shutdown waited on a different provider");
        AsyncGPUReadback.Complete(otherIndex);
        Result(other, 2, 20);
        other.CleanUp();
        Check(Application.HandlerCount == 0, "normal completion retained a shutdown handler");
        NoErrors();
    }

    static void SubmissionFailure()
    {
        var query = new TestQuery();
        Check(query.Post(1, Points(12)), "registration failed");
        for (int i = 0; i < 24; i++)
        {
            ComputeShader.ThrowOnDispatch = i % 2 == 0;
            AsyncGPUReadback.ThrowOnRequest = !ComputeShader.ThrowOnDispatch;
            var threw = false;
            try { query.UpdateQueries(); }
            catch (InvalidOperationException) { threw = true; }
            Check(threw, "submission exception was swallowed or a reserved slot leaked");
            Check(query.RequestCount == 0, "failed submission became a pending request");
        }
        ComputeShader.ThrowOnDispatch = false;
        AsyncGPUReadback.ThrowOnRequest = false;
        query.UpdateQueries();
        Check(query.RequestCount == 1, "could not submit after rollback");
        AsyncGPUReadback.Complete(0);
        Result(query, 1, 12);
        NoErrors();
    }

    static void PersistentCompaction()
    {
        var query = new TestQuery();
        Frame(0);
        Check(query.Post(1, Points(10)), "first registration failed");
        Frame(1);
        Check(query.Post(2, Points(20)), "second registration failed");
        Frame(10);
        query.UpdateQueries();
        AsyncGPUReadback.Complete(0);
        Result(query, 1, 10);
        Result(query, 2, 20);
        Frame(11);
        query.UpdateQueries();
        AsyncGPUReadback.Complete(1);
        Result(query, 2, 20);
        Check(!query.Result(1, new Vector3[1]), "expired query was retained");
        NoErrors();
    }

    static void ReorderedCompaction()
    {
        var query = new TestQuery();
        Check(query.Post(1, Points(10)), "A failed");
        Check(query.Post(2, Points(20)), "B failed");
        Check(query.Post(3, Points(30)), "C failed");
        query.RemoveQueryPoints(1);
        Check(query.Post(4, Points(40, 41, 42)), "D failed");
        // Dictionary entry reuse can put D before B and C, despite its later array range.
        query.UpdateQueries();
        AsyncGPUReadback.Complete(0);
        Frame(1);
        query.UpdateQueries();
        AsyncGPUReadback.Complete(1);
        Result(query, 4, 40, 41, 42);
        Result(query, 2, 20);
        Result(query, 3, 30);
        NoErrors();
    }

    static void PersistentNormals()
    {
        var query = new TestQuery();
        Check(query.Post(1, Points(100)), "expired owner failed");
        Frame(1);
        var point = new[] { new Vector3(5, 0, 7) };
        Check(query.Post(2, point, point, 8f), "normal registration failed");
        Frame(10);
        query.UpdateQueries();
        AsyncGPUReadback.Complete(0);
        Frame(11);
        query.UpdateQueries();
        var data = AsyncGPUReadback.Requests[1].Data;
        Near(data[0].x, 5f, "point moved with normal range");
        Near(data[1].x, 5f, "normal center x");
        Near(data[1].y, 7f, "normal center z");
        Near(data[2].x, 5.1f, "normal x neighbor");
        Near(data[3].y, 7.1f, "normal z neighbor");
        for (int i = 0; i < 4; i++) Near(data[i].z, 2f, "minimum grid size");
        NoErrors();
    }

    static void ClearAndRemove()
    {
        var query = new TestQuery();
        Check(query.Post(1, Points(10)), "A failed");
        Check(query.Post(2, Points(20)), "B failed");
        query.UpdateQueries();
        query.RemoveQueryPoints(1);
        query.CompactQueryStorage();
        Check(query.Post(3, Points(30)), "C failed");
        AsyncGPUReadback.Complete(0);
        Result(query, 1, 10);
        Result(query, 2, 20);
        Frame(1);
        query.UpdateQueries();
        AsyncGPUReadback.Complete(1);
        Result(query, 3, 30);
        Check(!query.Result(1, new Vector3[1]), "clear did not clear writable batch");
        NoErrors();
    }

    static void Capacity()
    {
        Reset(8);
        var query = new TestQuery();
        var points = Points(10, 20);
        Check(query.Post(1, points, points), "exact capacity rejected");
        points = Points(11, 21);
        Check(query.Post(1, points, points), "full-buffer owner update rejected");
        Check(!query.Post(2, Points(30)), "capacity overflow accepted");
        Check(!query.Post(1, Points(1, 2, 3), Points(1, 2, 3)), "normal sample overflow accepted");
        query.UpdateQueries();
        AsyncGPUReadback.Complete(0);
        Result(query, 1, 11, 21);
        query.CompactQueryStorage();
        Check(query.Post(3, null, Points(1, 2)), "normal-only query rejected");
        query.CompactQueryStorage();
        Check(!query.Post(4, null, Points(1, 2, 3)), "normal-only overflow accepted");
        Check(Debug.Errors.Count == 3, "expected only the three rejected capacity errors");
    }

    static void FullPool()
    {
        var query = new TestQuery();
        AsyncGPUReadback.Complete(Submit(query, 0, 1, 10));
        AsyncGPUReadback.Complete(Submit(query, 1, 1, 20));
        for (int i = 2; i < 9; i++) Submit(query, i, 1, i * 10);
        Check(query.RequestCount == 7, "could not fill pending capacity with two retained results");
        Frame(9);
        Check(query.Post(1, Points(90)), "current full-pool registration rejected");
        query.UpdateQueries();
        Check(AsyncGPUReadback.Requests.Count == 9, "full pool caused an extra request");
        Result(query, 1, 20);
        var velocity = new Vector3[1];
        Check(query.Velocity(1, velocity) == 0, "full pool invalidated velocity history");
        Near(velocity[0].x, 10f, "full-pool velocity");
        AsyncGPUReadback.Complete(5, error: true);
        Frame(10);
        query.UpdateQueries();
        Check(AsyncGPUReadback.Requests.Count == 10 && query.RequestCount == 7, "exact slot release failed");
        AsyncGPUReadback.Complete(9);
        Result(query, 1, 90);
        NoErrors();
    }

    static void ExpiredReadback()
    {
        var query = new TestQuery();
        Submit(query, 0, 1, 10);
        Submit(query, 1, 1, 20);
        AsyncGPUReadback.Complete(0, notify: false);
        AsyncGPUReadback.Requests[0].Error = true; // Models Unity disposing fulfilled data on the following frame.
        AsyncGPUReadback.Complete(1);
        Check(AsyncGPUReadback.Requests[0].GetDataCount == 0, "expired request data accessed");
        Result(query, 1, 20);
        NoErrors();
    }

    static void VelocityRanges()
    {
        var query = new TestQuery();
        Check(query.Post(1, Points(10)), "A failed");
        Check(query.Post(2, Points(20)), "B failed");
        query.UpdateQueries();
        AsyncGPUReadback.Complete(0);
        Frame(1);
        query.RemoveQueryPoints(1);
        Check(query.Post(2, Points(22)), "B update failed");
        Check(query.Post(1, Points(15)), "A moved range failed");
        query.UpdateQueries();
        AsyncGPUReadback.Complete(1);
        var velocity = new Vector3[1];
        Check(query.Velocity(1, velocity) == 0, "A velocity failed");
        Near(velocity[0].x, 5f, "A velocity used incorrect offsets");
        Check(query.Velocity(2, velocity) == 0, "B velocity failed");
        Near(velocity[0].x, 2f, "B velocity used incorrect offsets");
        NoErrors();
    }

    static void RewoundClock()
    {
        var query = new TestQuery();
        Submit(query, 3, 1, 10);
        Submit(query, 2, 1, 20);
        AsyncGPUReadback.Complete(1);
        AsyncGPUReadback.Complete(0);
        Result(query, 1, 20);
        NoErrors();
    }

    static void ResizedOutput()
    {
        Reset(16);
        var query = new TestQuery();
        Check(query.Post(1, Points(10)), "A failed");
        Check(query.Post(2, Points(20, 21, 22, 23, 24, 25, 26)), "B failed");
        query.UpdateQueries();
        AsyncGPUReadback.Complete(0);
        var output = Points(77, 88);
        var status = query.Query(1, 0f, Points(11, 12), output, null, null);
        Check((status & (int)QueryBase.QueryStatus.PostFailed) == 0, "resized query was not registered");
        Check((status & (int)QueryBase.QueryStatus.RetrieveFailed) != 0, "old A range exposed B's result");
        Near(output[0].x, 77, "failed retrieve wrote first output");
        Near(output[1].x, 88, "failed retrieve wrote neighboring owner's output");
        var tooLarge = Points(Enumerable.Range(1, 17).Select(value => (float)value).ToArray());
        Check(!query.Result(2, tooLarge), "out-of-array result request was accepted");
        for (int i = 0; i < tooLarge.Length; i++) Near(tooLarge[i].x, i + 1, "failed out-of-array read wrote output");
        NoErrors();
    }

    static void OutputShapes()
    {
        var query = new TestQuery();
        var points = Points(10, 20);
        Check(query.Post(1, points, points), "registration failed");
        query.UpdateQueries();
        AsyncGPUReadback.Complete(0);
        var displacements = Points(77, 88);
        var heights = new[] { 99f };
        var normals = Points(111);
        Check(!query.Results(1, displacements, heights, normals), "mismatched displacement/height lengths accepted");
        Near(displacements[0].x, 77, "mismatched outputs changed displacement");
        Near(displacements[1].x, 88, "mismatched outputs changed second displacement");
        Near(heights[0], 99, "mismatched outputs changed height");
        Near(normals[0].x, 111, "mismatched outputs changed normal");
        Check(query.Results(1, null, heights, null), "partial height read rejected");
        Near(heights[0], 110f, "partial height read");
        Check(query.Results(1, null, null, null), "optional/no output read rejected");
        query.CompactQueryStorage();
        Check(query.Post(2, null, points), "normal-only registration failed");
        Frame(1);
        query.UpdateQueries();
        AsyncGPUReadback.Complete(1);
        Check(query.Results(2, null, null, new Vector3[2]), "normal-only result read rejected");
        NoErrors();
    }

    static void VelocityBounds()
    {
        var query = new TestQuery();
        Check(query.Post(1, Points(10)), "A failed");
        Check(query.Post(2, Points(20, 21, 22)), "B failed");
        query.UpdateQueries();
        AsyncGPUReadback.Complete(0);
        Frame(1);
        Check(query.Post(1, Points(13)), "A update failed");
        Check(query.Post(2, Points(21, 22, 23)), "B update failed");
        query.UpdateQueries();
        AsyncGPUReadback.Complete(1);
        var output = Points(77, 88);
        Check(query.Velocity(1, output) == (int)QueryBase.QueryStatus.VelocityDataInvalidated, "velocity read a neighboring owner");
        Near(output[0].x, 77, "failed velocity wrote first output");
        Near(output[1].x, 88, "failed velocity wrote second output");
        output = new Vector3[1];
        Check(query.Velocity(2, output) == 0, "partial velocity read rejected");
        Near(output[0].x, 1f, "partial velocity value");
        NoErrors();
    }
}

sealed class TestQuery : QueryBase
{
    protected override string QueryShaderName => "TestQuery";
    protected override string QueryKernelName => "Main";
    protected override void BindInputsAndOutputs(PropertyWrapperComputeStandalone wrapper, ComputeBuffer results)
        => ShaderProcessQueries.SetBuffer(_kernelHandle, Shader.PropertyToID("_Results"), results);
    public bool Post(int owner, Vector3[] points, Vector3[] normals = null, float minimumLength = 0f)
        => UpdateQueryPoints(owner, minimumLength, points, normals);
    public bool Result(int owner, Vector3[] results) => RetrieveResults(owner, results, null, null);
    public bool Results(int owner, Vector3[] displacements, float[] heights, Vector3[] normals)
        => RetrieveResults(owner, displacements, heights, normals);
    public int Velocity(int owner, Vector3[] results) => CalculateVelocities(owner, results);
}

namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 right => new Vector3(1, 0, 0);
        public static Vector3 forward => new Vector3(0, 0, 1);
        public Vector3 normalized
        {
            get { var length = MathF.Sqrt(x * x + y * y + z * z); return length > 0 ? this / length : default; }
        }
        public static Vector3 operator -(Vector3 a) => new Vector3(-a.x, -a.y, -a.z);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => a + -b;
        public static Vector3 operator *(Vector3 a, float b) => new Vector3(a.x * b, a.y * b, a.z * b);
        public static Vector3 operator /(Vector3 a, float b) => a * (1f / b);
        public static Vector3 Cross(Vector3 a, Vector3 b) => new Vector3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
    }
    public struct Vector3Int { public int x, y, z; }
    public static class Time { public static int frameCount; public static float time; }
    public static class Application
    {
        public static event Action quitting;
        public static int HandlerCount => quitting?.GetInvocationList().Length ?? 0;
        public static void Reset() => quitting = null;
        public static void RaiseQuitting() => quitting?.Invoke();
        public static void RaiseFirstQuitting() => ((Action)quitting.GetInvocationList()[0])();
    }
    public static class Debug
    {
        public static readonly List<string> Errors = new List<string>();
        public static void LogError(object value) => Errors.Add(value.ToString());
        public static void Assert(bool value) { if (!value) throw new Exception("production assertion failed"); }
    }
    public static class Shader
    {
        static readonly Dictionary<string, int> IDs = new Dictionary<string, int>();
        public static int PropertyToID(string name)
        {
            if (!IDs.TryGetValue(name, out var id)) IDs.Add(name, id = IDs.Count + 1);
            return id;
        }
    }
    public enum ComputeBufferType { Default }
    public sealed class ComputeBuffer
    {
        public static readonly List<ComputeBuffer> All = new List<ComputeBuffer>();
        public Vector3[] Data;
        public int CountWritten;
        public bool Disposed;
        public int DisposeCount;
        public ComputeBuffer(int count, int stride, ComputeBufferType type) { Data = new Vector3[count]; All.Add(this); }
        public void SetData(Vector3[] data, int source, int destination, int count)
        {
            if (Disposed) throw new Exception("SetData on a disposed buffer");
            Array.Copy(data, source, Data, destination, count);
            CountWritten = count;
        }
        public void Dispose()
        {
            if (Disposed) throw new Exception("buffer disposed twice");
            Disposed = true;
            DisposeCount++;
        }
    }
    public sealed class ComputeShader
    {
        public static int DispatchCount;
        public static bool ThrowOnDispatch;
        readonly Dictionary<int, ComputeBuffer> _buffers = new Dictionary<int, ComputeBuffer>();
        public int FindKernel(string kernel) => 0;
        public void SetBuffer(int kernel, int name, ComputeBuffer buffer) => _buffers[name] = buffer;
        public void Dispatch(int kernel, int x, int y, int z)
        {
            if (ThrowOnDispatch) throw new InvalidOperationException("injected dispatch failure");
            DispatchCount++;
            var input = _buffers[Shader.PropertyToID("_QueryPositions_MinGridSizes")];
            var output = _buffers[Shader.PropertyToID("_Results")];
            if (input.Disposed || output.Disposed) throw new Exception("dispatch uses disposed resources");
            // Echo the submitted coordinates so expected owner/range mappings are independent of QueryBase.
            Array.Copy(input.Data, output.Data, input.CountWritten);
        }
    }
}

namespace Unity.Collections
{
    public enum Allocator { Persistent }
    public enum NativeArrayOptions { ClearMemory }
    sealed class Storage<T>
    {
        public T[] Data;
        public bool Disposed;
    }
    public struct NativeArray<T>
    {
        Storage<T> _storage;
        public NativeArray(int length, Allocator allocator, NativeArrayOptions options)
            { _storage = new Storage<T> { Data = new T[length] }; }
        internal NativeArray(T[] data) { _storage = new Storage<T> { Data = data }; }
        public bool IsCreated => _storage != null && !_storage.Disposed;
        public int Length => Data.Length;
        internal T[] Data
        {
            get { if (!IsCreated) throw new Exception("disposed NativeArray accessed"); return _storage.Data; }
        }
        public T this[int index] { get => Data[index]; set => Data[index] = value; }
        public void Dispose()
        {
            if (!IsCreated) throw new Exception("NativeArray disposed twice");
            _storage.Disposed = true;
        }
        public void CopyTo(NativeArray<T> destination)
        {
            if (Data.Length != destination.Data.Length) throw new Exception("incorrect readback length");
            Array.Copy(Data, destination.Data, Data.Length);
        }
        public NativeSlice<T> Slice(int start, int count) => new NativeSlice<T>(Data, start, count);
    }
    public readonly struct NativeSlice<T>
    {
        readonly T[] _data;
        readonly int _start, _count;
        public NativeSlice(T[] data, int start, int count) { _data = data; _start = start; _count = count; }
        public void CopyTo(T[] destination)
        {
            if (_count != destination.Length) throw new Exception("incorrect result count");
            Array.Copy(_data, _start, destination, 0, _count);
        }
    }
}

namespace UnityEngine.Rendering
{
    public sealed class ReadbackState
    {
        public ComputeBuffer Source;
        public Vector3[] Data;
        public bool Done, Error;
        public int GetDataCount, WaitCount;
        public Action<AsyncGPUReadbackRequest> Callback;
    }
    public struct AsyncGPUReadbackRequest
    {
        internal ReadbackState State;
        public bool done => State.Done;
        public bool hasError => State.Error;
        public void WaitForCompletion()
        {
            if (State.Source.Disposed) throw new Exception("waiting on a disposed GPU source");
            State.WaitCount++;
            State.Done = true;
            if (AsyncGPUReadback.NotifyOnWait) State.Callback(this);
        }
        public Unity.Collections.NativeArray<T> GetData<T>()
        {
            if (!done || hasError) throw new Exception("invalid readback data accessed");
            State.GetDataCount++;
            return new Unity.Collections.NativeArray<T>((T[])(object)State.Data);
        }
    }
    public static class AsyncGPUReadback
    {
        public static readonly List<ReadbackState> Requests = new List<ReadbackState>();
        public static bool CompleteInsideRequest, ErrorInsideRequest, ThrowOnRequest;
        public static bool NotifyOnWait;
        public static void Reset()
        {
            Requests.Clear();
            CompleteInsideRequest = ErrorInsideRequest = ThrowOnRequest = false;
            NotifyOnWait = true;
        }
        public static AsyncGPUReadbackRequest Request(ComputeBuffer source, Action<AsyncGPUReadbackRequest> callback)
        {
            if (ThrowOnRequest) throw new InvalidOperationException("injected readback submission failure");
            var state = new ReadbackState { Source = source, Data = (Vector3[])source.Data.Clone(), Callback = callback };
            Requests.Add(state);
            var result = new AsyncGPUReadbackRequest { State = state };
            if (CompleteInsideRequest)
            {
                // Immediate failures may be observable via hasError before done.
                state.Done = !ErrorInsideRequest;
                state.Error = ErrorInsideRequest;
                callback(result);
            }
            return result;
        }
        public static void Complete(int index, bool error = false, bool notify = true)
        {
            var state = Requests[index];
            if (state.Source.Disposed) throw new Exception("GPU source disposed while readback pending");
            state.Done = true;
            state.Error = error;
            if (notify) Notify(index);
        }
        public static void Notify(int index) => Requests[index].Callback(new AsyncGPUReadbackRequest { State = Requests[index] });
    }
}

namespace Crest
{
    public sealed class PropertyWrapperComputeStandalone
    {
        public PropertyWrapperComputeStandalone(ComputeShader shader, int kernel) { }
    }
    public static class ComputeShaderHelpers { public static ComputeShader LoadShader(string name) => new ComputeShader(); }
    public sealed class QuerySettings { public int MaxQueryCount = 64; }
    public sealed class AnimatedWaves { public QuerySettings Settings = new QuerySettings(); }
    public sealed class OceanRenderer
    {
        public static OceanRenderer Instance;
        public AnimatedWaves _lodDataAnimWaves = new AnimatedWaves();
        public float SeaLevel;
    }
    public static class Helpers
    {
        public static void Swap<T>(ref T left, ref T right) { var temporary = left; left = right; right = temporary; }
    }
}
