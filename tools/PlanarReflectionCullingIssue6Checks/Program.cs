using System;
using System.Collections.Generic;
using Crest;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

static class Program
{
    static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static RenderGraph Record(Camera camera, bool geometry)
    {
        var graph = new RenderGraph();
        var frame = new ContextContainer { data = new UniversalCameraData { camera = camera } };
        graph.passes.Add(() => Require(!graph.inverted, "Main-light shadows must keep normal culling"));
        graph.passes.Add(() => Require(!graph.inverted, "Additional-light shadows must keep normal culling"));
        new CrestReflectionCullPass(true).RecordRenderGraph(graph, frame);
        if (geometry)
            foreach (var phase in new[] { "Depth", "Depth normals", "GBuffer", "Opaque", "Skybox", "Refracted transparent", "Transparent" })
                graph.passes.Add(() => Require(graph.inverted, phase + " must use reflected culling"));
        new CrestReflectionCullPass(false).RecordRenderGraph(graph, frame);
        graph.passes.Add(() => Require(!graph.inverted, "Post-processing must see restored culling"));
        graph.passes.Add(() => Require(!graph.inverted, "Next ordinary camera's shadows and scene draws must keep normal culling"));
        return graph;
    }

    static void Main()
    {
        GraphicsSettings.currentRenderPipeline = new object();
        var reflection = new Camera { cameraType = CameraType.Reflection };
        var destination = new RenderTexture();
        var begin = new CrestReflectionCullPass(true);
        var end = new CrestReflectionCullPass(false);
        Require(begin.renderPassEvent == RenderPassEvent.AfterRenderingShadows, "Begin must precede prepasses and follow shadows");
        Require(end.renderPassEvent == RenderPassEvent.AfterRenderingTransparents, "End must follow all transparent draws");

        foreach (var shadows in new[] { false, true })
        foreach (var geometry in new[] { false, true })
        {
            RenderGraph recorded = null;
            RenderPipeline.submit = (camera, request) =>
            {
                Require(ReferenceEquals(CrestRenderRequest.ReflectionCamera, reflection), "Request must be tagged while recording");
                Require(ReferenceEquals(request.destination, destination), "Request must retain its destination");
                Require(camera.data.renderShadows == shadows, "Shadows must remain camera-scoped");
                recorded = Record(camera, geometry);
                // A reflection probe must not inherit the planar camera's mirrored culling.
                var probe = new Camera { cameraType = CameraType.Reflection };
                var probeGraph = new RenderGraph();
                var probeFrame = new ContextContainer { data = new UniversalCameraData { camera = probe } };
                begin.RecordRenderGraph(probeGraph, probeFrame);
                end.RecordRenderGraph(probeGraph, probeFrame);
                Require(probeGraph.passes.Count == 0, "Unrelated reflection probe must not be reversed");
            };
            CrestRenderRequest.Render(reflection, destination, shadows, invertCulling: true);
            Require(CrestRenderRequest.ReflectionCamera == null, "Request marker must be cleared");
            Require(recorded.globalPasses == 2, "Both state passes must be preserved even for an empty scene");
            // Exercise pass-data capture after the CPU request marker has been cleared.
            foreach (var pass in recorded.passes) pass();
        }
        Console.WriteLine("PASS: shadows on/off, prepasses through transparency, empty scene, probe isolation, next camera, delayed pass execution");

        RenderPipeline.submit = (camera, request) =>
        {
            Require(CrestRenderRequest.ReflectionCamera == null, "Depth request must not be tagged as a reflection");
            Require(!camera.data.renderShadows, "Depth request must disable URP shadows");
            var graph = new RenderGraph();
            begin.RecordRenderGraph(graph, new ContextContainer { data = new UniversalCameraData { camera = camera } });
            Require(graph.passes.Count == 0, "Depth request must not reverse culling");
        };
        CrestRenderRequest.Render(new Camera(), destination, shadows: false);
        Console.WriteLine("PASS: depth-cache request uses camera-scoped shadows without reflection culling");

        RenderPipeline.submit = (camera, request) => throw new InvalidOperationException("render failed");
        try { CrestRenderRequest.Render(reflection, destination, true, invertCulling: true); }
        catch (InvalidOperationException) { }
        Require(CrestRenderRequest.ReflectionCamera == null, "Exception must clear the request marker");
        Console.WriteLine("PASS: throwing render request clears marker");

        RenderPipeline.submit = (camera, request) =>
        {
            var outer = CrestRenderRequest.ReflectionCamera;
            RenderPipeline.submit = (inner, target) => Require(CrestRenderRequest.ReflectionCamera == null, "Nested ordinary request must be untagged");
            CrestRenderRequest.Render(new Camera(), destination, false);
            Require(ReferenceEquals(CrestRenderRequest.ReflectionCamera, outer), "Nested request must restore outer marker");
        };
        CrestRenderRequest.Render(reflection, destination, true, invertCulling: true);
        Require(CrestRenderRequest.ReflectionCamera == null, "Outer marker must be cleared");
        Console.WriteLine("PASS: nested request restores outer camera marker");

        RenderPipeline.supported = false;
        bool unsupportedThrew = false;
        try { CrestRenderRequest.Render(reflection, destination, true, invertCulling: true); }
        catch (InvalidOperationException) { unsupportedThrew = true; }
        Require(unsupportedThrew && CrestRenderRequest.ReflectionCamera == null, "Unsupported request must fail without leaving a marker");
        GraphicsSettings.currentRenderPipeline = null;
        var builtin = new Camera();
        CrestRenderRequest.Render(builtin, destination, false, invertCulling: true);
        Require(builtin.renderCalls == 1 && builtin.data == null, "BIRP must use Camera.Render without adding URP camera data");
        Console.WriteLine("PASS: unsupported SRP fails explicitly; BIRP uses Camera.Render");
    }
}

namespace UnityEngine
{
    public enum CameraType { Game, Reflection }
    public sealed class RenderTexture { }
    public sealed class Camera
    {
        public CameraType cameraType;
        public UniversalAdditionalCameraData data;
        public int renderCalls;
        public void Render() => renderCalls++;
    }
}

namespace UnityEngine.Rendering
{
    public static class GraphicsSettings { public static object currentRenderPipeline; }
    public sealed class ContextContainer
    {
        public object data;
        public T Get<T>() => (T)data;
    }
    public static class RenderPipeline
    {
        public static bool supported = true;
        public static Action<Camera, UniversalRenderPipeline.SingleCameraRequest> submit;
        public static bool SupportsRenderRequest(Camera camera, UniversalRenderPipeline.SingleCameraRequest request) => supported;
        public static void SubmitRenderRequest(Camera camera, UniversalRenderPipeline.SingleCameraRequest request) => submit(camera, request);
    }
}

namespace UnityEngine.Rendering.Universal
{
    public enum CameraRenderType { Base, Overlay }
    public enum CameraOverrideOption { Off, On }
    public enum RenderPassEvent { BeforeRenderingShadows = 50, AfterRenderingShadows = 100, BeforeRenderingPrePasses = 150, AfterRenderingTransparents = 500 }
    public sealed class UniversalAdditionalCameraData
    {
        public CameraRenderType renderType;
        public bool renderPostProcessing, renderShadows, allowXRRendering;
        public CameraOverrideOption requiresColorOption, requiresDepthOption;
    }
    public static class CameraExtensions
    {
        public static UniversalAdditionalCameraData GetUniversalAdditionalCameraData(this Camera camera) => camera.data ??= new UniversalAdditionalCameraData();
    }
    public sealed class UniversalCameraData { public Camera camera; }
    public static class UniversalRenderPipeline
    {
        public sealed class SingleCameraRequest { public RenderTexture destination; }
    }
    public abstract class ScriptableRenderPass
    {
        public RenderPassEvent renderPassEvent;
        public abstract void RecordRenderGraph(RenderGraph graph, ContextContainer frameData);
    }
}

namespace UnityEngine.Rendering.RenderGraphModule
{
    public sealed class RenderGraph
    {
        public readonly List<Action> passes = new List<Action>();
        public bool inverted;
        public int globalPasses;
        public Builder<T> AddUnsafePass<T>(string name, out T data) where T : new()
        {
            data = new T();
            return new Builder<T>(this, data);
        }
    }
    public sealed class Builder<T> : IDisposable
    {
        readonly RenderGraph _graph;
        readonly T _data;
        bool _global;
        public Builder(RenderGraph graph, T data) { _graph = graph; _data = data; }
        public void AllowGlobalStateModification(bool value) { _global = value; if (value) _graph.globalPasses++; }
        public void SetRenderFunc(Action<T, UnsafeGraphContext> render)
        {
            _graph.passes.Add(() => { if (_global) render(_data, new UnsafeGraphContext { cmd = new TestCommandBuffer { graph = _graph } }); });
        }
        public void Dispose() { }
    }
    public sealed class UnsafeGraphContext { public TestCommandBuffer cmd; }
    public sealed class TestCommandBuffer
    {
        public RenderGraph graph;
        public void SetInvertCulling(bool value) => graph.inverted = value;
    }
}
