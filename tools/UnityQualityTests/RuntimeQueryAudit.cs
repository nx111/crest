// Install outside Editor only in a disposable test project, with the diagnostic compute in Resources.
using System;
using System.Collections;
using System.Reflection;
using Crest;
using Unity.Collections;
using UnityEngine;

public static class RuntimeQueryAudit
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    sealed class Coordinates : QueryBase
    {
        protected override string QueryShaderName => "AuditQueryCoordinates";
        protected override string QueryKernelName => "CSMain";
        protected override void BindInputsAndOutputs(PropertyWrapperComputeStandalone wrapper, ComputeBuffer results)
            => ShaderProcessQueries.SetBuffer(_kernelHandle, "_AuditResults", results);
        public bool Post(int owner, params Vector3[] points) => UpdateQueryPoints(owner, 4, points, null);
        public bool Read(int owner, Vector3[] output) => RetrieveResults(owner, output, null, null);
    }
    static object Field(QueryBase provider, string name) => typeof(QueryBase).GetField(name, Private).GetValue(provider);
    static bool Released(QueryBase provider) => Field(provider, "_computeBufQueries") == null && Field(provider, "_computeBufResults") == null;
    static bool CpuReleased(QueryBase provider) => !((NativeArray<Vector3>)Field(provider, "_queryResults")).IsCreated
        && !((NativeArray<Vector3>)Field(provider, "_queryResultsLast")).IsCreated;
    static bool Finite(Vector3 v) => !float.IsNaN(v.x) && !float.IsInfinity(v.x) && !float.IsNaN(v.y)
        && !float.IsInfinity(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.z);

    // Leave both a live provider and a cleaned-up provider with real readbacks at immediate quit.
    // Subscribe after their production handlers to inspect resources before the native shutdown.
    [UnityEngine.Scripting.Preserve]
    public static void PrepareShutdown(Action<bool, string> check, Action<string> record, Action<bool> completed)
    {
        var active = new Coordinates();
        var dying = new Coordinates();
        foreach (var provider in new QueryBase[] { active, dying })
        {
            var coordinates = (Coordinates)provider;
            for (int step = 0; step < 100; step++)
            {
                coordinates.Post(810990, new Vector3(step, 0, step * 2));
                coordinates.UpdateQueries();
            }
            check(coordinates.RequestCount > 0 && coordinates.RequestCount <= 7,
                "Immediate shutdown begins with native readbacks pending=" + coordinates.RequestCount);
        }
        dying.CleanUp();
        bool inspected = false;
        Action inspect = () =>
        {
            if (inspected) return;
            inspected = true;
            bool released = Released(active) && Released(dying) && CpuReleased(active) && CpuReleased(dying)
                && active.RequestCount == 0 && dying.RequestCount == 0;
            record((released ? "PASS" : "FAIL") + ": Immediate shutdown releases live and already-cleaned providers before native teardown");
            Debug.Log("CREST_QUERY_QUIT_AUDIT " + (released ? "PASS" : "FAIL"));
            completed(released);
        };
        Application.quitting += inspect;
#if UNITY_EDITOR
        UnityEditor.EditorApplication.quitting += inspect;
#endif
    }

    [UnityEngine.Scripting.Preserve]
    public static IEnumerator Run(Action<bool, string> check, Action<string> record)
    {
        check(SystemInfo.supportsAsyncGPUReadback, "Native async GPU readback supported");
        var ocean = OceanRenderer.Instance;
        check(ocean != null, "Query audit has a live OceanRenderer");
        var coordinates = new Coordinates();
        const int a = 810001, b = 810002;
        try
        {
            int peak = 0;
            for (int step = 0; step < 100; step++)
            {
                coordinates.Post(a, new Vector3(step, 0, step * 2));
                coordinates.UpdateQueries();
                peak = Math.Max(peak, coordinates.RequestCount);
            }
            record("Native query burst: 100 same-frame submissions; peak pending=" + peak + "; pending now=" + coordinates.RequestCount);
            check(peak == 7, "Native readback backlog reaches and respects the seven-request limit");
            var output = new Vector3[1];
            bool latest = false;
            for (int frame = 0; frame < 240; frame++)
            {
                yield return null;
                coordinates.UpdateQueries();
                if (coordinates.Read(a, output) && (output[0] - new Vector3(99, 198, 1)).sqrMagnitude < 1e-8f)
                { latest = true; break; }
            }
            check(latest, "Native backlog resumes with the latest pending coordinates");

            coordinates.CompactQueryStorage();
            coordinates.Post(a, new Vector3(11, 0, 21));
            coordinates.Post(b, new Vector3(37, 0, 49));
            coordinates.UpdateQueries();
            // First completion still uses the old B range. Wait for a batch submitted after
            // PrepareNextBatch has moved B to slot zero before accepting its coordinates.
            var compactedSequence = (long)Field(coordinates, "_nextRequestSequence") + 2;
            coordinates.RemoveQueryPoints(a);
            bool compacted = false;
            for (int frame = 0; frame < 120; frame++)
            {
                yield return null;
                coordinates.UpdateQueries();
                if ((long)Field(coordinates, "_queryResultsSequence") >= compactedSequence
                    && coordinates.Read(b, output) && (output[0] - new Vector3(37, 49, 1)).sqrMagnitude < 1e-8f)
                { compacted = true; break; }
            }
            check(compacted, "Native GPU coordinates follow the surviving owner through compaction");
            var sentinel = new Vector3(-999, -999, -999);
            var grown = new[] { sentinel, sentinel, sentinel };
            coordinates.Post(b, Vector3.zero, Vector3.one, Vector3.right);
            check(!coordinates.Read(b, grown) && grown[0] == sentinel && grown[1] == sentinel && grown[2] == sentinel,
                "Growing an owner output cannot read a neighboring retained range and preserves outputs on failure");

            for (int cycle = 0; cycle < 12; cycle++)
            {
                coordinates.CompactQueryStorage();
                for (int owner = 0; owner < 8; owner++) coordinates.Post(a + owner, new Vector3(cycle + owner, 0, owner));
                for (int burst = 0; burst < 14; burst++) coordinates.UpdateQueries();
                yield return null;
                coordinates.UpdateQueries();
                check(coordinates.RequestCount <= 7, "Repeated native owner batches remain bounded, cycle=" + cycle);
            }
        }
        finally { coordinates.CleanUp(); }
        check(CpuReleased(coordinates), "Cleanup immediately disposes native CPU result arrays");
        for (int frame = 0; frame < 240 && !Released(coordinates); frame++) yield return null;
        check(Released(coordinates) && coordinates.RequestCount == 0, "Late native callbacks finish and release both GPU buffers");

        for (int cycle = 0; cycle < 8; cycle++)
        {
            var dying = new Coordinates();
            dying.Post(a, new Vector3(cycle, 0, 13));
            dying.UpdateQueries();
            var pending = dying.RequestCount;
            dying.CleanUp(); dying.CleanUp(); dying.UpdateQueries();
            check(CpuReleased(dying), "In-flight cleanup is idempotent, cycle=" + cycle + ", pending at cleanup=" + pending);
            for (int frame = 0; frame < 240 && !Released(dying); frame++) yield return null;
            check(Released(dying) && dying.RequestCount == 0, "Disposed instance releases native GPU resources, cycle=" + cycle);
        }

        // Test the shipped wave kernel, not the coordinate diagnostic.
        var production = new QueryDisplacements();
        try
        {
            var points = new[] { new Vector3(17, 0, -28), new Vector3(-14, 0, -46), new Vector3(60, 0, -22) };
            var displacements = new Vector3[3]; var normals = new Vector3[3]; var velocities = new Vector3[3];
            var invalid = new Vector3(float.NaN, float.NaN, float.NaN);
            bool valid = false;
            for (int frame = 0; frame < 180; frame++)
            {
                // A failed or partial query must not pass using zero-initialized or earlier outputs.
                for (int point = 0; point < points.Length; point++)
                    displacements[point] = normals[point] = velocities[point] = invalid;
                var status = production.Query(a, 2, points, displacements, normals, velocities);
                production.UpdateQueries();
                // RetrieveSucceeded alone does not reject invalid or unavailable velocity data.
                if (status == (int)QueryBase.QueryStatus.OK && frame > 3)
                {
                    valid = true;
                    foreach (var value in displacements) valid &= Finite(value);
                    foreach (var value in normals) valid &= Finite(value) && Math.Abs(value.magnitude - 1) < 0.001f;
                    foreach (var value in velocities) valid &= Finite(value);
                    if (valid) break;
                }
                yield return null;
            }
            check(valid, "Production wave compute returns finite displacements/velocities and unit normals through native readbacks");
        }
        finally { production.CleanUp(); }
        for (int frame = 0; frame < 240 && !Released(production); frame++) yield return null;
        check(Released(production), "Production query provider releases GPU buffers after its pending native request completes");

        // Explicit destruction on disable; ordinary disable keeps resources by design.
        var debug = typeof(OceanRenderer).GetField("_debug", Private).GetValue(ocean);
        var destroySetting = debug.GetType().GetField("_destroyResourcesInOnDisable");
        var previousSetting = (bool)destroySetting.GetValue(debug);
        destroySetting.SetValue(debug, true);
        try
        {
            for (int cycle = 0; cycle < 4; cycle++)
            {
                var previous = ocean.CollisionProvider as QueryBase;
                check(previous != null, "Ocean uses the production compute collision provider before rebuild " + cycle);
                previous.Query(a, 1, new[] { Vector3.zero }, new Vector3[1], null, null);
                previous.UpdateQueries();
                var pending = previous.RequestCount;
                ocean.enabled = false;
                check(CpuReleased(previous), "Ocean teardown disposes old provider CPU arrays; pending=" + pending);
                yield return null;
                ocean.enabled = true;
                for (int frame = 0; frame < 240 && (!Released(previous) || ocean.CollisionProvider == null); frame++) yield return null;
                check(Released(previous) && previous.RequestCount == 0 && ocean.CollisionProvider != previous,
                    "Ocean rebuild creates a fresh provider and releases the old pending GPU resources, cycle=" + cycle);
                for (int frame = 0; frame < 8; frame++) yield return null;
            }
        }
        finally { destroySetting.SetValue(debug, previousSetting); ocean.enabled = true; }
    }
}
