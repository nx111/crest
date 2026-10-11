// Device diagnostics, installed beside PlayerAudit only in disposable projects.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Crest;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

public sealed partial class PlayerAudit
{
    void RunFoamStorage()
    {
        const int size = 32;
        var foam = Resources.Load<ComputeShader>("UpdateFoam");
        var kernel = foam.FindKernel("UpdateFoam");
        var cascade = (ComputeBuffer)typeof(OceanRenderer).GetField("_bufCascadeDataTgt", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(OceanRenderer.Instance);
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
                    var request = AsyncGPUReadback.Request(targets[(2 * frequency - 1) % 2], 0, TextureFormat.RFloat);
                    request.WaitForCompletion();
                    Check(!request.hasError, "Device production foam readback: " + format + ", " + frequency + " Hz");
                    var value = request.GetData<float>()[size / 2 * size + size / 2];
                    var error = value / Mathf.Exp(-1.6f) - 1;
                    Check(Finite(value) && (format != RenderTextureFormat.RFloat || Mathf.Abs(error) < 0.001f),
                        "Device foam storage " + format + ", " + frequency + " Hz: value=" + value + "; signed error=" + error);
                }
                finally { foreach (var target in targets) { target.Release(); Object.Destroy(target); } }
            }
        }
        finally { Object.Destroy(zero); Object.Destroy(initial); }
    }

    IEnumerator RunPerformanceAB()
    {
        var pipeline = (UniversalRenderPipelineAsset)GraphicsSettings.currentRenderPipeline;
        var originalScale = pipeline.renderScale;
        var renderer = Resources.FindObjectsOfTypeAll<UniversalRendererData>().First(r => r.rendererFeatures.OfType<CrestUniversalRendererFeature>().Any());
        var feature = renderer.rendererFeatures.OfType<CrestUniversalRendererFeature>().First();
        var originalLayers = feature.RefractedLayers;
        var originalResolution = pipeline.mainLightShadowmapResolution;
        var originalCascades = pipeline.shadowCascadeCount;
        var originalDistance = pipeline.shadowDistance;
        var descriptor = default(RenderTextureDescriptor);
        Camera measuringCamera = null;
        Action<UnityEngine.Rendering.CommandBuffer, Camera, RenderTextureDescriptor> observe = (cmd, camera, target) =>
        { if (camera == measuringCamera) descriptor = target; };
        CrestUniversalRendererFeature.BeforeSceneRendering += observe;
        try
        {
            foreach (var sceneIndex in new[] { 0, 2, 5 })
            {
                var operation = SceneManager.LoadSceneAsync(sceneIndex);
                while (!operation.isDone) yield return null;
                var ocean = OceanRenderer.Instance;
                measuringCamera = ocean.ViewCameraExcludingSceneCamera ?? Camera.main;
                foreach (var behaviour in measuringCamera.GetComponents<MonoBehaviour>())
                    if (!(behaviour is UnderwaterRenderer) && !(behaviour is UniversalAdditionalCameraData)
                        && !(behaviour is OceanPlanarReflection)) behaviour.enabled = false;
                for (var frame = 0; frame < 90; frame++) yield return null;
                var scene = SceneManager.GetActiveScene().name;
                _results.Add("PERF_CONFIG " + scene + "; pipeline=" + pipeline.name + "; screen=" + Screen.width + "x" + Screen.height
                    + "; LOD=" + ocean.LodDataResolution + "; tiles=" + ocean.Tiles.Count + "; terrain=" + Terrain.activeTerrains.Length);
                foreach (var scale in new[] { 1f, 0.75f, 0.5f, 1f })
                {
                    pipeline.renderScale = scale;
                    for (var frame = 0; frame < 45; frame++) yield return null;
                    var sample = SamplePerformance(scene + ",scale=" + scale, ocean, () => descriptor);
                    while (sample.MoveNext()) yield return sample.Current;
                    yield return new WaitForEndOfFrame();
                    Capture(measuringCamera, "ab-" + scene + "-scale-" + scale);
                }
                // Each following variant starts from scale=1 and otherwise preserves the scene.
                pipeline.mainLightShadowmapResolution = 1024; pipeline.shadowCascadeCount = 2; pipeline.shadowDistance = 70;
                for (var frame = 0; frame < 45; frame++) yield return null;
                var shadows = SamplePerformance(scene + ",shadows=1024x2x70", ocean, () => descriptor);
                while (shadows.MoveNext()) yield return shadows.Current;
                pipeline.mainLightShadowmapResolution = originalResolution; pipeline.shadowCascadeCount = originalCascades; pipeline.shadowDistance = originalDistance;
                feature.RefractedLayers = 0;
                for (var frame = 0; frame < 45; frame++) yield return null;
                var copy = SamplePerformance(scene + ",refractedLayers=0", ocean, () => descriptor);
                while (copy.MoveNext()) yield return copy.Current;
                feature.RefractedLayers = originalLayers;
                if (ocean._lodDataDynWaves != null)
                {
                    var settings = ocean._lodDataDynWaves.Settings;
                    var frequency = settings._simulationFrequency;
                    settings._simulationFrequency = 60;
                    for (var frame = 0; frame < 45; frame++) yield return null;
                    var waves = SamplePerformance(scene + ",dynamicWaves=60Hz", ocean, () => descriptor);
                    while (waves.MoveNext()) yield return waves.Current;
                    settings._simulationFrequency = frequency;
                }
                if (sceneIndex == 0 && ocean._lodDataFoam != null)
                {
                    var originalSettings = ocean._simSettingsFoam;
                    // A manager's implicit default settings are destroyed when it rebuilds.
                    // Assign a temporary asset so the requested format survives that rebuild.
                    var settings = Instantiate(ocean._lodDataFoam.Settings);
                    try
                    {
                        ocean._simSettingsFoam = settings;
                        settings._renderTextureGraphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R32_SFloat;
                        for (var frame = 0; frame < 90; frame++) yield return null;
                        var actual = ocean._lodDataFoam.DataTexture.graphicsFormat;
                        Check(ocean._lodDataFoam.Settings == settings && actual == ocean._lodDataFoam.CompatibleTextureFormat,
                            "Foam settings survive rebuild; requested=R32_SFloat, compatible=" + ocean._lodDataFoam.CompatibleTextureFormat + ", actual=" + actual);
                        bool fullPrecision = actual == UnityEngine.Experimental.Rendering.GraphicsFormat.R32_SFloat
                            || actual == UnityEngine.Experimental.Rendering.GraphicsFormat.R32G32_SFloat
                            || actual == UnityEngine.Experimental.Rendering.GraphicsFormat.R32G32B32A32_SFloat;
                        if (fullPrecision)
                        {
                            var foam = SamplePerformance(scene + ",foamHistory=" + actual, ocean, () => descriptor);
                            while (foam.MoveNext()) yield return foam.Current;
                        }
                        else _results.Add("SKIP: Full-scene R32 foam cost: device selected " + actual
                            + " for filtering/read-write compatibility; this is not a 32-bit precision comparison.");
                    }
                    finally { ocean._simSettingsFoam = originalSettings; Destroy(settings); }
                }
                if (sceneIndex == 5)
                {
                    var geometry = typeof(OceanRenderer).GetField("_geometryDownSampleFactor", BindingFlags.Instance | BindingFlags.NonPublic);
                    var resolution = typeof(OceanRenderer).GetField("_lodDataResolution", BindingFlags.Instance | BindingFlags.NonPublic);
                    var originalGeometry = (int)geometry.GetValue(ocean);
                    var originalLod = ocean.LodDataResolution;
                    try
                    {
                        geometry.SetValue(ocean, 4);
                        for (var frame = 0; frame < 90; frame++) yield return null;
                        var mesh = SamplePerformance(scene + ",geometryFactor=4,lod=" + originalLod, ocean, () => descriptor);
                        while (mesh.MoveNext()) yield return mesh.Current;
                        yield return new WaitForEndOfFrame(); Capture(measuringCamera, "ab-" + scene + "-geometry4");
                        // Keep tile grid resolution constant while isolating simulation texel density.
                        resolution.SetValue(ocean, originalLod / 2);
                        geometry.SetValue(ocean, Math.Max(1, originalGeometry / 2));
                        for (var frame = 0; frame < 90; frame++) yield return null;
                        Check(ocean._lodDataAnimWaves.DataTexture.width == originalLod / 2,
                            "Lower-resolution Lakes simulation actually reallocates its LOD texture");
                        var lod = SamplePerformance(scene + ",lod=" + originalLod / 2 + ",geometryFactor=" + Math.Max(1, originalGeometry / 2), ocean, () => descriptor);
                        while (lod.MoveNext()) yield return lod.Current;
                        yield return new WaitForEndOfFrame(); Capture(measuringCamera, "ab-" + scene + "-lod384");
                    }
                    finally { resolution.SetValue(ocean, originalLod); geometry.SetValue(ocean, originalGeometry); }
                }
            }
        }
        finally
        {
            pipeline.renderScale = originalScale;
            pipeline.mainLightShadowmapResolution = originalResolution; pipeline.shadowCascadeCount = originalCascades; pipeline.shadowDistance = originalDistance;
            feature.RefractedLayers = originalLayers;
            CrestUniversalRendererFeature.BeforeSceneRendering -= observe;
        }
    }

    IEnumerator SamplePerformance(string label, OceanRenderer ocean, Func<RenderTextureDescriptor> target)
    {
        var timestamps = new HashSet<ulong>();
        var gpu = new List<double>(); var cpu = new List<double>(); var render = new List<double>(); var wait = new List<double>();
        var intervals = new List<float>(); var substeps = new List<int>(); var timing = new FrameTiming[1];
        int invalid = 0;
        for (var frame = 0; frame < 120; frame++)
        {
            FrameTimingManager.CaptureFrameTimings();
            yield return new WaitForEndOfFrame();
            intervals.Add(Time.unscaledDeltaTime * 1000);
            substeps.Add(ocean._lodDataDynWaves == null ? 0 : ocean._lodDataDynWaves.LastUpdateSubstepCount);
            if (FrameTimingManager.GetLatestTimings(1, timing) > 0 && timestamps.Add(timing[0].frameStartTimestamp))
            {
                if (timing[0].gpuFrameTime > 0 && !double.IsNaN(timing[0].gpuFrameTime)) gpu.Add(timing[0].gpuFrameTime);
                else invalid++;
                cpu.Add(timing[0].cpuMainThreadFrameTime); render.Add(timing[0].cpuRenderThreadFrameTime); wait.Add(timing[0].cpuMainThreadPresentWaitTime);
            }
        }
        intervals.Sort(); substeps.Sort(); gpu.Sort(); cpu.Sort(); render.Sort(); wait.Sort();
        var descriptor = target();
        Check(descriptor.width > 0 && descriptor.height > 0, label + ": actual camera descriptor observed from the Crest render pass");
        Func<List<double>, string> median = values => values.Count > 0 ? values[values.Count / 2].ToString("F3") : "unavailable";
        _results.Add("PERF " + label + "; descriptor=" + descriptor.width + "x" + descriptor.height + "," + descriptor.graphicsFormat
            + ",msaa=" + descriptor.msaaSamples + "; frames=120; intervalMedian=" + intervals[60] + "; intervalP95=" + intervals[114]
            + "; gpuMedian=" + median(gpu) + "; gpuUnique=" + gpu.Count + "; invalid=" + invalid
            + "; cpuMain=" + median(cpu) + "; cpuRender=" + median(render) + "; presentWait=" + median(wait) + "; dynamicSubsteps=" + substeps[60]);
        Debug.Log(_results[_results.Count - 1]);
    }
}
