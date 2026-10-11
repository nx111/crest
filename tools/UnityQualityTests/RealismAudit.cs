// Copy beside QualityRunner.cs in Assets/Editor of a disposable project.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Crest;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;
using Stopwatch = System.Diagnostics.Stopwatch;
using Half = Unity.Mathematics.half;

public static partial class QualityRunner
{
    static IEnumerable<object> RunRealismAudit()
    {
        var load = EditorSceneManager.LoadSceneAsyncInPlayMode(Main, new LoadSceneParameters(LoadSceneMode.Single));
        while (!load.isDone) yield return null;
        foreach (var unused in Wait(60)) yield return unused;
        PrepareCamera(true);
        foreach (var unused in Wait(10)) yield return unused;
        AuditGpuFunctions();
        AuditBakedSampling();
        foreach (var unused in AuditProvider()) yield return unused;
        foreach (var unused in AuditSurfaceAndReflections()) yield return unused;
        AuditFftGpu();
        AuditFoamGpuDecay();
        if (Environment.GetCommandLineArgs().Contains("-crestQueryAudit"))
        {
            var queries = RuntimeQueryAudit.Run(Check, Results.Add);
            while (queries.MoveNext()) yield return queries.Current;
        }
    }

    static Color AuditPixel(Material material)
    {
        var target = new RenderTexture(1, 1, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
        var staging = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
        var saved = RenderTexture.active;
        using (var command = new CommandBuffer())
        {
            try
            {
                target.Create();
                command.SetRenderTarget(target);
                command.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3);
                Graphics.ExecuteCommandBuffer(command);
                RenderTexture.active = target;
                staging.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
                staging.Apply();
                return staging.GetPixel(0, 0);
            }
            finally
            {
                RenderTexture.active = saved;
                target.Release();
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(staging);
            }
        }
    }

    static void AuditGpuFunctions()
    {
        var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Editor/RealismAudit.shader");
        Check(shader != null && shader.isSupported, "GPU realism contract shader is supported");
        var material = new Material(shader);
        try
        {
            foreach (var slope in new[] { 0f, 30f, 60f })
            {
                var macro = new Vector3(Mathf.Sin(slope * Mathf.Deg2Rad), Mathf.Cos(slope * Mathf.Deg2Rad), 0);
                material.SetVector("_AuditBase", macro);
                foreach (var detail in new[] { new Vector2(0.5f, 0), new Vector2(-0.5f, 0), new Vector2(0, 0.5f), Vector2.zero })
                {
                    material.SetVector("_AuditDetail", new Vector4(detail.x, detail.y, 0, 0));
                    var pixel = AuditPixel(material);
                    var normal = new Vector3(pixel.r, pixel.g, pixel.b);
                    var expected = Mathf.Atan(detail.magnitude) * Mathf.Rad2Deg;
                    Check(Finite(normal.x) && Finite(normal.y) && Finite(normal.z)
                        && Mathf.Abs(normal.magnitude - 1) < 0.0001f
                        && Mathf.Abs(Vector3.Angle(macro, normal) - expected) < 0.025f,
                        "GPU RNM: macro " + slope + " degrees; detail " + detail + "; deviation " + Vector3.Angle(macro, normal));
                }
            }
            material.SetInt("_AuditCase", 1);
            material.SetFloat("_AdditionalLightIntensity", 1);
            material.SetFloat("_WaveFoamLightScale", 1);
            material.SetFloat("_WaveFoamSpecularBoost", 0);
            var tilted = new Vector3(1, 1, 0).normalized;
            material.SetVector("_AuditNormal0", tilted);
            material.SetVector("_AuditNormal1", new Vector3(-1, 1, 0).normalized);
            material.SetVector("_AuditLight", Vector3.right);
            material.SetVector("_AuditWeights", new Vector4(1, 0, 0, 0));
            var onePhase = AuditPixel(material).r;
            Check(Mathf.Abs(onePhase - Mathf.Sqrt(0.5f)) < 0.002f, "GPU local foam diffuse uses foam relief, including horizontal side light: " + onePhase);
            material.SetVector("_AuditNormal1", Vector3.right);
            material.SetVector("_AuditVariance", new Vector4(0, 0.02f, 0, 0));
            Check(Mathf.Abs(AuditPixel(material).r - onePhase) < 0.0001f, "GPU empty Flow phase contributes no foam light");
            material.SetVector("_AuditWeights", new Vector4(0.5f, 0.5f, 0, 0));
            material.SetVector("_AuditNormal1", new Vector3(-1, 1, 0).normalized);
            Check(Mathf.Abs(AuditPixel(material).r - 0.5f * onePhase) < 0.002f, "GPU Flow phases are shaded separately before coverage blending");
            material.SetVector("_AuditWeights", Vector4.zero);
            Check(AuditPixel(material).r == 0, "GPU zero foam coverage produces zero local foam contribution");
            material.SetVector("_AuditWeights", new Vector4(1, 0, 0, 0));
            material.SetVector("_AuditNormal0", Vector3.up);
            material.SetVector("_AuditLight", Vector3.up);
            material.SetFloat("_WaveFoamSpecularBoost", 1);
            material.SetVector("_AuditVariance", Vector4.zero);
            var unfiltered = AuditPixel(material).r;
            material.SetVector("_AuditVariance", new Vector4(0.01f, 0, 0, 0));
            var filtered = AuditPixel(material).r;
            Check(unfiltered > 1.9f && filtered > 1 && filtered < unfiltered * 0.6f,
                "GPU local foam highlight consumes its own normal variance: " + unfiltered + " -> " + filtered);
            Check(!ShaderUtil.GetShaderMessages(shader).Any(m => m.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error), "GPU contract shader compiled without errors");
        }
        finally { Object.DestroyImmediate(material); }
    }

    static FFTBakedData AuditData()
    {
        const int resolution = 128;
        var frames = new Half[2 * resolution * resolution * 4];
        for (var frame = 0; frame < 2; frame++)
        for (var z = 0; z < resolution; z++)
        for (var x = 0; x < resolution; x++)
        {
            var phase = 2 * Mathf.PI * (x + 0.5f) / resolution + frame * 0.5f * Mathf.PI;
            var index = ((frame * resolution + z) * resolution + x) * 4;
            frames[index] = new Half(Mathf.Cos(phase));
            frames[index + 1] = new Half(Mathf.Sin(phase));
        }
        var data = ScriptableObject.CreateInstance<FFTBakedData>();
        data.Initialize(16, resolution, 5, 1, 10, 2, new Half(-1), new Half(1), frames);
        return data;
    }

    [BurstCompile(CompileSynchronously = true)]
    struct AuditSamplingJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<Half> Frames;
        public FFTBakedDataParameters Parameters;
        public bool Invert;
        [WriteOnly] public NativeArray<float4> Output;
        public void Execute(int index)
        {
            var x = new float4(-16, 0.0625f - 0.00000001f, 16, index * 0.0137f);
            var t = new float4(-16, -0.000000001f, 8, index * 0.017f);
            float4 y;
            if (Invert)
                FFTBakedData.SampleDisplacementXZTWithInvert(x, -16, t, Parameters, in Frames, out _, out y, out _);
            else
                FFTBakedData.SampleDisplacementXZT(x, -16, t, Parameters, in Frames, out _, out y, out _);
            Output[index] = y;
        }
    }

    static void AuditBakedSampling()
    {
        var data = AuditData();
        var output = new NativeArray<float4>(2048, Allocator.Persistent);
        try
        {
            var parameters = data._parameters;
            var frames = data._framesFlattenedNative;
            var coordinates = new[] { -32f, -16f, -0.00000001f, 0, 0.0625f - 0.00000001f, 0.0625f, 16, 32, 10000 };
            var times = new[] { -32f, -16f, -0.000000001f, 0, 0.000000001f, 7.999f, 8, 8.001f, 15.999f, 16, 32 };
            var maximumDelta = 0f;
            foreach (var x in coordinates)
            foreach (var t in times)
            {
                var height = AuditManagedHeight(x, -16, t, parameters, frames);
                AuditManagedInvert(x, -16, t, parameters, frames, out var dx, out var dy, out var dz);
                CheckFiniteQuad(dx); CheckFiniteQuad(dy); CheckFiniteQuad(dz);
                maximumDelta = Mathf.Max(maximumDelta, math.cmax(math.abs(height - dy)));
            }
            Check(maximumDelta < 0.000001f, "Unity native FFT height/displacement consistency and coordinate/time boundaries: 99 SIMD cases; max delta " + maximumDelta);
            AuditManagedRaw(0, 0, 0, parameters, frames, out _, out var raw, out _);
            var heightAtOrigin = AuditManagedHeight(0, 0, 0, parameters, frames);
            Check(Mathf.Abs(raw.x) < 0.001f && Mathf.Abs(heightAtOrigin.x + 0.35892f) < 0.001f,
                "Actual Unity half-array query reproduces horizontal-chop correction: raw Y " + raw.x + "; world height " + heightAtOrigin.x);
            Check(math.cmax(math.abs(AuditManagedHeight(0.25f, 0, 3.25f, parameters, frames)
                - AuditManagedHeight(16.25f, -16, 19.25f, parameters, frames))) < 0.00001f,
                "Unity FFT spatial and temporal periodic seams agree");
            var job = new AuditSamplingJob { Frames = frames, Parameters = parameters, Invert = true, Output = output };
            job.Schedule(output.Length, 8).Complete();
            Check(BurstCompiler.IsEnabled, "Unity Burst compilation is enabled for actual sampling jobs");
            var maximumBurstDelta = 0f;
            for (var i = 0; i < output.Length; i++)
            {
                var expected = AuditManagedHeight(new float4(-16, 0.0625f - 0.00000001f, 16, i * 0.0137f), -16,
                    new float4(-16, -0.000000001f, 8, i * 0.017f), parameters, frames);
                maximumBurstDelta = Mathf.Max(maximumBurstDelta, math.cmax(math.abs(expected - output[i])));
            }
            Check(maximumBurstDelta < 0.0001f, "Burst and managed production FFT sampling agree over 8192 points: max delta " + maximumBurstDelta);
            foreach (var invert in new[] { false, true })
            {
                job.Invert = invert;
                job.Schedule(output.Length, 8).Complete();
                var milliseconds = new List<double>();
                for (var repeat = 0; repeat < 20; repeat++)
                {
                    var watch = Stopwatch.StartNew();
                    job.Schedule(output.Length, 8).Complete();
                    milliseconds.Add(watch.Elapsed.TotalMilliseconds);
                }
                milliseconds.Sort();
                Results.Add("CPU synthetic FFT Burst schedule+complete, 8192 points, inverted=" + invert
                    + ": median " + milliseconds[10] + " ms; p95 " + milliseconds[18] + " ms. Not a fleet/whole-frame benchmark.");
            }
        }
        finally { output.Dispose(); Object.DestroyImmediate(data); }
    }

    // Keep the production AggressiveInlining chains out of the large Mono test
    // frame. The Burst job above still calls the production entry points directly.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static float4 AuditManagedHeight(float4 x, float4 z, float4 t, FFTBakedDataParameters parameters, NativeArray<Half> frames)
        => FFTBakedData.SampleHeightXZT(x, z, t, parameters, in frames);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void AuditManagedInvert(float4 x, float4 z, float4 t, FFTBakedDataParameters parameters, NativeArray<Half> frames,
        out float4 dx, out float4 dy, out float4 dz)
        => FFTBakedData.SampleDisplacementXZTWithInvert(x, z, t, parameters, in frames, out dx, out dy, out dz);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void AuditManagedRaw(float4 x, float4 z, float4 t, FFTBakedDataParameters parameters, NativeArray<Half> frames,
        out float4 dx, out float4 dy, out float4 dz)
        => FFTBakedData.SampleDisplacementXZT(x, z, t, parameters, in frames, out dx, out dy, out dz);

    static void CheckFiniteQuad(float4 values)
    {
        if (!math.all(math.isfinite(values))) throw new InvalidOperationException("Nonfinite FFT samples");
    }

    static IEnumerable<object> AuditProvider()
    {
        var data = AuditData();
        var provider = new CollProviderBakedFFT(data);
        var time = _ocean.GetComponent<TimeProviderCustom>();
        var savedTime = time._time;
        var points = new[] { new Vector3(0, 0, 0), new Vector3(-16, 0, -16), new Vector3(0.0625f, 0, 0), new Vector3(16, 0, 0) };
        var heights = new float[4]; var displacement = new Vector3[4]; var normals = new Vector3[4]; var velocities = new Vector3[4];
        try
        {
            foreach (var queryTime in new[] { -16f, -0.000000001f, 0f, 8f, 16f })
            {
                time._time = queryTime;
                var heightStatus = -1; var displacementStatus = -1;
                for (var repeat = 0; repeat < 5; repeat++)
                {
                    heightStatus = provider.Query(7101, 0, points, heights, null, null);
                    displacementStatus = provider.Query(7102, 0, points, displacement, normals, velocities);
                    provider.UpdateQueries();
                    yield return null;
                }
                Check(provider.RetrieveSucceeded(heightStatus) && provider.RetrieveSucceeded(displacementStatus), "Actual baked FFT provider jobs return results at t=" + queryTime);
                Check(Enumerable.Range(0, 4).All(i => Mathf.Abs(heights[i] - (_ocean.SeaLevel + displacement[i].y)) < 0.00001f
                    && Finite(velocities[i].y) && Mathf.Abs(normals[i].magnitude - 1) < 0.001f),
                    "Actual baked FFT provider heights, displacements, normals and negative-history velocities agree at t=" + queryTime);
            }
        }
        finally { time._time = savedTime; provider.CleanUp(); Object.DestroyImmediate(data); }
    }

    static Half[] AuditGpuFftFrame(float time, float period, OceanWaveSpectrum spectrum)
    {
        const int resolution = 32, firstLod = 5, lodCount = 2;
        var target = new RenderTexture(resolution, resolution * lodCount, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear) { enableRandomWrite = true };
        var staging = new Texture2D(target.width, target.height, TextureFormat.RGBAHalf, false, true);
        var saved = RenderTexture.active;
        using (var command = new CommandBuffer())
        {
            try
            {
                target.Create();
                var raw = FFTCompute.GenerateDisplacements(command, resolution, period, 0.145f, 0, 10, time, spectrum, true);
                var bake = Resources.Load<ComputeShader>("FFT/FFTBake");
                var kernel = bake.FindKernel("FFTBakeMultiRes");
                command.SetComputeIntParam(bake, FFTBaker.ShaderIDs.s_MinSlice, firstLod);
                command.SetComputeFloatParam(bake, FFTBaker.ShaderIDs.s_BakeTime, time);
                command.SetComputeTextureParam(bake, kernel, FFTBaker.ShaderIDs.s_InFFTWaves, raw);
                command.SetComputeTextureParam(bake, kernel, FFTBaker.ShaderIDs.s_OutDisplacements, target);
                command.DispatchCompute(bake, kernel, resolution / 8, target.height / 8, 1);
                Graphics.ExecuteCommandBuffer(command);
                RenderTexture.active = target;
                staging.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0, false);
                var result = staging.GetRawTextureData<Half>().ToArray();
                if (!result.All(value => Finite((float)value))) throw new InvalidOperationException("Nonfinite GPU FFT readback");
                return result;
            }
            finally { RenderTexture.active = saved; target.Release(); Object.DestroyImmediate(target); Object.DestroyImmediate(staging); }
        }
    }

    static void AuditFftGpu()
    {
        var spectrum = ScriptableObject.CreateInstance<OceanWaveSpectrum>();
        try
        {
            var first = AuditGpuFftFrame(0, 16, spectrum);
            var last = AuditGpuFftFrame(16, 16, spectrum);
            var otherPeriod = AuditGpuFftFrame(0, 32, spectrum);
            Check(first.Select(value => Mathf.Abs((float)value)).Max() > 0.001f, "Production FFT compute and bake kernels generate a nonflat half-precision collision frame");
            var seamDelta = first.Zip(last, (a, b) => Mathf.Abs((float)a - (float)b)).Max();
            var energyDelta = first.Zip(otherPeriod, (a, b) => Mathf.Abs((float)a - (float)b)).Max();
            Check(seamDelta < 0.005f, "GPU FFT loop closes at 16 seconds: max half readback delta " + seamDelta);
            Check(energyDelta < 0.0001f, "GPU initial FFT field is independent of loop period: max delta " + energyDelta);
            foreach (var scale in new[] { 0f, 0.5f, 1f, 1.3f })
            {
                spectrum._gravityScale = scale;
                first = AuditGpuFftFrame(0, 16, spectrum);
                last = AuditGpuFftFrame(16, 16, spectrum);
                seamDelta = first.Zip(last, (a, b) => Mathf.Abs((float)a - (float)b)).Max();
                Check(seamDelta < 0.005f, "GPU FFT closes in external time with Time Scale=" + scale + "; max delta " + seamDelta);
            }
            Check(!Resources.FindObjectsOfTypeAll<RenderTexture>().Any(t => t.name == "CrestFFTBakedWaves"), "GPU readback test releases its temporary bake target");
        }
        finally { Object.DestroyImmediate(spectrum); }
    }

    static void AuditFoamGpuDecay()
    {
        const int size = 32;
        var foam = Resources.Load<ComputeShader>("UpdateFoam");
        var kernel = foam.FindKernel("UpdateFoam");
        var cascade = (ComputeBuffer)typeof(OceanRenderer).GetField("_bufCascadeDataTgt", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_ocean);
        var zero = new Texture2DArray(size, size, 1, TextureFormat.RGBAFloat, false, true);
        var initial = new Texture2DArray(size, size, 1, TextureFormat.RGBAFloat, false, true);
        zero.SetPixels(Enumerable.Repeat(Color.clear, size * size).ToArray(), 0); zero.Apply();
        initial.SetPixels(Enumerable.Repeat(Color.white, size * size).ToArray(), 0); initial.Apply();
        try
        {
            foreach (var format in new[] { RenderTextureFormat.RFloat, RenderTextureFormat.RHalf })
            foreach (var frequency in new[] { 15, 30, 60, 200 })
            {
                var targets = Enumerable.Range(0, 2).Select(_ => new RenderTexture(size, size, 0, format, RenderTextureReadWrite.Linear)
                    { dimension = TextureDimension.Tex2DArray, volumeDepth = 1, enableRandomWrite = true }).ToArray();
                try
                {
                    foreach (var target in targets) target.Create();
                    using (var command = new CommandBuffer())
                    {
                        command.SetComputeBufferParam(foam, kernel, "_CrestCascadeData", cascade);
                        command.SetComputeBufferParam(foam, kernel, "_CrestCascadeDataSource", cascade);
                        command.SetComputeTextureParam(foam, kernel, "_LD_TexArray_Flow", zero);
                        command.SetComputeTextureParam(foam, kernel, "_LD_TexArray_AnimatedWaves", zero);
                        command.SetComputeTextureParam(foam, kernel, "_LD_TexArray_SeaFloorDepth", zero);
                        command.SetComputeFloatParam(foam, "_SimDeltaTime", 1f / frequency);
                        command.SetComputeFloatParam(foam, "_SimDeltaTimePrev", 1f / frequency);
                        command.SetComputeFloatParam(foam, "_FoamFadeRate", 0.8f);
                        command.SetComputeFloatParam(foam, "_WaveFoamStrength", 0);
                        command.SetComputeFloatParam(foam, "_ShorelineFoamStrength", 0);
                        command.SetComputeFloatParam(foam, "_ShorelineFoamMaxDepth", 1);
                        command.SetComputeFloatParam(foam, "_WaveFoamCoverage", 0);
                        command.SetComputeFloatParam(foam, "_LODChange", 0);
                        command.SetComputeIntParam(foam, "_NeedsPrewarming", 0);
                        command.SetComputeIntParam(foam, "_CrestMinimumWavesSlice", 0);
                        for (var step = 0; step < 2 * frequency; step++)
                        {
                            command.SetComputeTextureParam(foam, kernel, "_LD_TexArray_Foam_Source", step == 0 ? (Texture)initial : targets[(step - 1) % 2]);
                            command.SetComputeTextureParam(foam, kernel, "_LD_TexArray_Target", targets[step % 2]);
                            command.DispatchCompute(foam, kernel, size / 8, size / 8, 1);
                        }
                        Graphics.ExecuteCommandBuffer(command);
                    }
                    var readback = AsyncGPUReadback.Request(targets[(2 * frequency - 1) % 2], 0, TextureFormat.RFloat);
                    readback.WaitForCompletion();
                    Check(!readback.hasError, "Production foam kernel supports " + format + " readback at " + frequency + " Hz");
                    var value = readback.GetData<float>()[size / 2 * size + size / 2];
                    var relativeError = Mathf.Abs(value / Mathf.Exp(-0.8f * 2) - 1);
                    Check(Finite(value) && (format != RenderTextureFormat.RFloat || relativeError < 0.001f),
                        "Production foam decay is finite" + (format == RenderTextureFormat.RFloat ? " and accurate" : " (half-target accuracy measured separately)")
                        + ", " + format + ", " + frequency + " Hz, 2 seconds: " + value + "; relative error " + relativeError);
                }
                finally { foreach (var target in targets) { target.Release(); Object.DestroyImmediate(target); } }
            }
        }
        finally { Object.DestroyImmediate(zero); Object.DestroyImmediate(initial); }
    }

    static IEnumerable<object> AuditSurfaceAndReflections()
    {
        var time = _ocean.GetComponent<TimeProviderCustom>();
        _camera.clearFlags = CameraClearFlags.Skybox;
        _camera.transform.position = new Vector3(0, _ocean.SeaLevel + 8, -12);
        _camera.transform.LookAt(new Vector3(0, _ocean.SeaLevel, 35));
        // The main island occludes water at the low camera pose. Isolate the sea
        // for the detail comparison, then restore geometry for planar reflection.
        var opaque = Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None)
            .Where(renderer => renderer.enabled && !renderer.GetComponent<OceanChunkRenderer>()).ToArray();
        foreach (var renderer in opaque) renderer.enabled = false;
        _water.EnableKeyword("_APPLYNORMALMAPPING_ON");
        _water.DisableKeyword("_FOAM_ON");
        _water.DisableKeyword("_FLOW_ON");
        _water.SetFloat("_NormalsStrengthOverall", 1);
        _water.SetFloat("_NormalsStrength", 1);
        foreach (var unused in Wait(10)) yield return unused;
        var current = Capture("audit-detail-current");
        var previous = Shader.Find("Crest/Ocean Before Detail");
        Check(previous != null, "Previous-commit surface shader is present for same-geometry comparison");
        var shader = _water.shader; var keywords = _water.shaderKeywords;
        try
        {
            _water.shader = previous; _water.shaderKeywords = keywords;
            var before = Capture("audit-detail-before");
            var difference = Difference(current, before);
            Check(difference > 0.000001, "RNM detail changes the visible sea versus 5700e21b, identical geometry/time/material: mean RGB delta " + difference);
        }
        finally { _water.shader = shader; _water.shaderKeywords = keywords; }
        _water.EnableKeyword("_FOAM_ON"); _water.EnableKeyword("_FOAM3DLIGHTING_ON"); _water.EnableKeyword("_FLOW_ON");
        foreach (var light in Object.FindObjectsByType<Light>(FindObjectsSortMode.None)) light.enabled = false;
        var point = new GameObject("Foam side-light audit").AddComponent<Light>();
        point.type = LightType.Point; point.range = 55; point.intensity = 15;
        point.transform.position = new Vector3(2, _ocean.SeaLevel + 2, 12);
        _water.SetFloat("_AdditionalLightIntensity", 1);
        foreach (var t in new[] { 11.99f, 12f, 12.01f, 12.99f, 13f, 13.01f })
        {
            time._time = t;
            foreach (var unused in Wait(3)) yield return unused;
            Capture("audit-foam-flow-" + t.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
        }
        point.enabled = false;
        foreach (var renderer in opaque) if (renderer != null) renderer.enabled = true;
        foreach (var light in Object.FindObjectsByType<Light>(FindObjectsSortMode.None)) if (light.type == LightType.Directional) light.enabled = true;
        _water.EnableKeyword("_PLANARREFLECTIONS_ON");
        var planar = _camera.gameObject.AddComponent<OceanPlanarReflection>();
        var textureField = typeof(OceanPlanarReflection).GetField("_reflectionTexture", BindingFlags.Instance | BindingFlags.NonPublic);
        foreach (var unused in Wait(5)) yield return unused;
        Capture("audit-planar-default");
        var texture = (RenderTexture)textureField.GetValue(planar);
        Check(texture != null && texture.IsCreated() && texture.mipmapCount > 1, "Actual planar reflection publishes a generated mip chain");
        _water.SetFloat("_Roughness", 0.7f);
        _water.SetFloat("_PlanarReflectionRoughnessScale", 0);
        Capture("audit-planar-filter-off");
        _water.SetFloat("_PlanarReflectionRoughnessScale", 1);
        Capture("audit-planar-filter-on");
        foreach (var mode in new[] { 0, 1 })
        {
            var serialized = new SerializedObject(planar);
            serialized.FindProperty("_useMipMaps").boolValue = mode != 0;
            serialized.FindProperty("_textureSize").intValue = mode == 0 ? 128 : 512;
            serialized.FindProperty("_hdr").boolValue = mode != 0;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            foreach (var unused in Wait(3)) yield return unused;
            Capture("audit-planar-recreated-" + mode);
            texture = (RenderTexture)textureField.GetValue(planar);
            Check(texture != null && texture.IsCreated() && texture.width == (mode == 0 ? 128 : 512)
                && (texture.mipmapCount > 1) == (mode != 0), "Planar reflection rebuilds when size/HDR/mip settings change, mode=" + mode);
        }
        texture.Release();
        foreach (var unused in Wait(3)) yield return unused;
        Capture("audit-planar-resource-recovery");
        texture = (RenderTexture)textureField.GetValue(planar);
        Check(texture != null && texture.IsCreated(), "Planar reflection recovers a released GPU resource");
        planar.enabled = false; planar.enabled = true;
        foreach (var unused in Wait(3)) yield return unused;
        Capture("audit-planar-reenabled");
        texture = (RenderTexture)textureField.GetValue(planar);
        Check(texture != null && texture.IsCreated(), "Planar reflection recovers after disable/enable");
        time._time = 12;
    }
}
