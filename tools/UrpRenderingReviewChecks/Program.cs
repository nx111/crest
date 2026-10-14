// Instrumented managed substitutes for extracted production C#. No Unity/GPU emulation.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Crest;
using Crest.Examples;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

namespace UnityEngine.Experimental.Rendering
{
    public enum GraphicsFormat { None, Color }
}
namespace UnityEngine
{
    public enum CameraType { Game, SceneView, Preview, Reflection }
    public enum RenderTextureFormat { DefaultHDR, Depth, RHalf, RFloat }
    public struct LayerMask { public int value; }
    public struct Vector3 { public float y; }
    public class Transform { public Vector3 position; }
    public class Camera
    {
        public static Camera main;
        public static event Action<Camera> onPreRender;
        public CameraType cameraType;
        public int cullingMask = -1;
        public Transform transform = new Transform();
        public void AddCommandBuffer(CameraEvent at, CommandBuffer command) { }
        public void RemoveCommandBuffer(CameraEvent at, CommandBuffer command) { }
        public static void Reset() { main = null; onPreRender = null; }
    }
    public struct RenderTextureDescriptor
    {
        public int width, height, volumeDepth, msaaSamples, depthBufferBits;
        public bool bindMS, useDynamicScale, enableRandomWrite;
        public RenderTextureFormat colorFormat;
        public Experimental.Rendering.GraphicsFormat graphicsFormat;
    }
    public sealed class RenderTexture
    {
        RenderTextureDescriptor _descriptor;
        bool _created;
        public readonly string Name;
        public int width => _descriptor.width;
        public int height => _descriptor.height;
        public int volumeDepth => _descriptor.volumeDepth;
        public bool useDynamicScale => _descriptor.useDynamicScale;
        public RenderTexture(string name) { Name = name; }
        public RenderTextureDescriptor descriptor
        {
            get => _descriptor;
            set { if (_created) throw new Exception("descriptor changed before Release"); _descriptor = value; }
        }
        public bool IsCreated() => _created;
        public void Release() => _created = false;
        public void Create()
        {
            if (width <= 0 || height <= 0 || volumeDepth <= 0) throw new Exception("created a zero-size placeholder: " + Name);
            _created = true;
        }
    }
    public static class Debug
    {
        public static readonly List<string> Warnings = new List<string>();
        public static void LogWarning(string message, object owner) => Warnings.Add(message);
    }
}
namespace UnityEngine.Rendering
{
    public enum CameraEvent
    {
        BeforeDepthTexture, BeforeDepthNormalsTexture, BeforeForwardOpaque,
        AfterDepthTexture, AfterDepthNormalsTexture, AfterForwardOpaque,
        BeforeForwardAlpha, AfterForwardAlpha, AfterEverything,
    }
    public sealed class CommandBuffer
    {
        public string name;
        public bool Released;
        public void Release() => Released = true;
    }
    public struct ScriptableRenderContext { }
    public static class GraphicsSettings { public static object currentRenderPipeline; }
    public sealed class RTHandle
    {
        public readonly RenderTexture Texture;
        public readonly RenderTextureDescriptor DescriptorAtAllocation;
        public RTHandle(RenderTexture texture) { Texture = texture; DescriptorAtAllocation = texture.descriptor; }
    }
    public static class RTHandles
    {
        public static readonly List<RTHandle> Allocations = new List<RTHandle>();
        public static RTHandle Alloc(RenderTexture texture)
        {
            var handle = new RTHandle(texture); Allocations.Add(handle); return handle;
        }
    }
}
namespace UnityEngine.Rendering.Universal
{
    [Flags] public enum ScriptableRenderPassInput { None = 0, Depth = 1, Color = 2 }
    public enum CameraRenderType { Base, Overlay }
    public enum RenderPassEvent
    {
        BeforeRenderingPrePasses = 150, AfterRenderingPrePasses = 200,
        AfterRenderingOpaques = 300, BeforeRenderingTransparents = 450, BeforeRenderingPostProcessing = 550,
    }
    public struct CameraData
    {
        public Camera camera;
        public RenderTextureDescriptor cameraTargetDescriptor;
        public CameraRenderType renderType;
    }
    public struct RenderingData { public CameraData cameraData; }
    public abstract class ScriptableRendererFeature
    {
        public abstract void Create();
        public abstract void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData data);
    }
    public class ScriptableRenderPass
    {
        public ScriptableRenderPassInput Input;
        public RenderPassEvent renderPassEvent;
        public bool requiresIntermediateTexture;
        public string Name;
        public void ConfigureInput(ScriptableRenderPassInput input) => Input = input;
    }
    public sealed class ScriptableRenderer
    {
        public readonly List<ScriptableRenderPass> Passes = new List<ScriptableRenderPass>();
        public void EnqueuePass(ScriptableRenderPass pass) => Passes.Add(pass);
    }
}
namespace UnityEngine.Rendering.RenderGraphModule
{
    public enum AccessFlags { Read, ReadWrite }
    public sealed class TextureHandle
    {
        public readonly RTHandle Wrapper;
        public readonly RenderTextureDescriptor Descriptor;
        public TextureHandle(RTHandle handle = null) { Wrapper = handle; Descriptor = handle == null ? default : handle.Texture.descriptor; }
    }
    public sealed class RenderGraph
    {
        public sealed class Defaults { public readonly TextureHandle blackTextureXR = new TextureHandle(); }
        public readonly Defaults defaultResources = new Defaults();
        public readonly List<TextureHandle> Imports = new List<TextureHandle>();
        public TextureHandle ImportTexture(RTHandle handle)
        {
            if (handle == null) throw new Exception("imported a missing RTHandle");
            var result = new TextureHandle(handle); Imports.Add(result); return result;
        }
    }
    public interface IUnsafeRenderGraphBuilder
    {
        void UseTexture(TextureHandle handle, AccessFlags access);
        void SetGlobalTextureAfterPass(TextureHandle handle, int id);
    }
    public sealed class Builder : IUnsafeRenderGraphBuilder
    {
        public readonly List<(TextureHandle handle, AccessFlags access)> Uses = new List<(TextureHandle, AccessFlags)>();
        public readonly Dictionary<int, TextureHandle> Globals = new Dictionary<int, TextureHandle>();
        public void UseTexture(TextureHandle handle, AccessFlags access) => Uses.Add((handle, access));
        public void SetGlobalTextureAfterPass(TextureHandle handle, int id) => Globals[id] = handle;
    }
}
namespace Crest
{
    public sealed class OceanRenderer
    {
        public static OceanRenderer Instance;
        public readonly List<OceanChunkRenderer> Tiles = new List<OceanChunkRenderer>();
    }
    public static partial class Helpers
    {
        public static bool MaskIncludesLayer(int mask, int layer) => (mask & (1 << layer)) != 0;
        public static bool IsIntelGPU() => false;
    }
    public sealed class SampleHeightHelper
    {
        public void Init(Vector3 position, float length, bool allowMultiple) { }
        public void Sample(out float height) => height = 0f;
    }
    public static class CrestRenderRequest { public static Camera ReflectionCamera; }
    public sealed class CrestReflectionCullPass : ScriptableRenderPass
    {
        public CrestReflectionCullPass(bool invert) { Name = invert ? "reflection-invert" : "reflection-restore"; }
    }
    public sealed class CrestRefractionPass : ScriptableRenderPass
    {
        public LayerMask layers;
        public CrestRefractionPass(bool copy, RenderPassEvent at) { Name = copy ? "copy" : "objects"; renderPassEvent = at; }
    }
    public partial class CrestUniversalRendererFeature : ScriptableRendererFeature
    {
        LayerMask _refractedLayers;
        CrestPass _prepare, _before, _after;
        CrestRefractionPass _refractionObjects, _refractionCopy;
        CrestCallbackPass _afterDepth, _afterOpaque;
        CrestReflectionCullPass _reflectionInvert, _reflectionRestore;
        public static event Action<CommandBuffer, Camera, RenderTextureDescriptor> BeforeSceneRendering, AfterDepthRendering, AfterOpaqueRendering;
        public static void ResetEvents() { BeforeSceneRendering = AfterDepthRendering = AfterOpaqueRendering = null; }
        public static int Subscribers(int stage)
            => (stage == 0 ? BeforeSceneRendering : stage == 1 ? AfterDepthRendering : AfterOpaqueRendering)?.GetInvocationList().Length ?? 0;
        public static void Fire(int stage, CommandBuffer command, Camera camera, RenderTextureDescriptor descriptor)
            => (stage == 0 ? BeforeSceneRendering : stage == 1 ? AfterDepthRendering : AfterOpaqueRendering)?.Invoke(command, camera, descriptor);
        public void SetLayers(int mask) => _refractedLayers.value = mask;
        partial class CrestPass : ScriptableRenderPass { readonly int _stage; }
        sealed class CrestCallbackPass : ScriptableRenderPass
        {
            public CrestCallbackPass(bool depth, RenderPassEvent at) { Name = depth ? "callback-depth" : "callback-opaque"; renderPassEvent = at; }
        }
        public ScriptableRenderer Record(Camera camera, CameraRenderType type = CameraRenderType.Base, bool valid = true)
        {
            var data = new RenderingData { cameraData = new CameraData { camera = camera, renderType = type,
                cameraTargetDescriptor = Program.Descriptor(valid: valid) } };
            var renderer = new ScriptableRenderer(); AddRenderPasses(renderer, ref data); return renderer;
        }
        public ScriptableRenderPass Before => _before;
        public ScriptableRenderPass After => _after;
    }
    public partial class OceanChunkRenderer
    {
        static Camera _currentCamera;
        public bool Visible;
        public readonly List<Camera> BoundCameras = new List<Camera>();
        internal void BindOceanData(Camera camera) => BoundCameras.Add(camera);
        public static void Begin(Camera camera) => BeginCameraRendering(default, camera);
        public static Camera Current => _currentCamera;
    }
    public partial class UnderwaterRenderer
    {
        public enum Mode { FullScreen, Portal, Volume, VolumeFlyThrough }
        public Mode _mode;
        public bool _overlay, isActiveAndEnabled = true, IsActive = true;
        public object _volumeGeometry;
        public int _layer = 4;
        bool _enableShaderAPI, _currentEnableShaderAPI, _currentOverlay;
        public bool EnableShaderAPI { get => _enableShaderAPI; set => _enableShaderAPI = value; }
        public static UnderwaterRenderer Instance;
        public static readonly Dictionary<Camera, UnderwaterRenderer> Cameras = new Dictionary<Camera, UnderwaterRenderer>();
        public static UnderwaterRenderer Get(Camera camera) => Cameras.TryGetValue(camera, out var result) ? result : null;
        readonly RenderTexture _maskRT = new RenderTexture("mask"), _depthRT = new RenderTexture("depth"),
            _volumeFrontFaceRT = new RenderTexture("front"), _volumeBackFaceRT = new RenderTexture("back");
        RTHandle _urpMask, _urpDepth, _urpVolumeFront, _urpVolumeBack;
        readonly Camera _camera = new Camera();
        readonly SampleHeightHelper _sampleHeightHelper = new SampleHeightHelper();
        float _heightAboveWater;
        CommandBuffer _underwaterEffectCommandBuffer;
        public int Disables, Enables;
        public bool MaskEnabled;
        public readonly List<string> Lifecycle = new List<string>();
        void UpdateInstance() => Instance = this;
        void OnDisable() { Disables++; Lifecycle.Add("disable"); MaskEnabled = false; }
        void OnEnable() { Enables++; Lifecycle.Add("enable"); MaskEnabled = !_overlay; }
        public void InitOverlay(bool state) { _overlay = _currentOverlay = state; MaskEnabled = !state; }
        public void Tick() => LateUpdate();
        public RTHandle FrontHandle => _urpVolumeFront;
        public RTHandle BackHandle => _urpVolumeBack;
        public RenderTexture Front => _volumeFrontFaceRT;
        public RenderTexture Back => _volumeBackFaceRT;
        public static class ShaderIDs
        {
            public const int s_CrestOceanMaskTexture = 1, s_CrestOceanMaskDepthTexture = 2,
                s_CrestWaterVolumeFrontFaceTexture = 3, s_CrestWaterVolumeBackFaceTexture = 4;
        }
    }
    public static partial class FogGuards
    {
        public struct Vec2 { public float x, y; }
        public struct Vec3 { public float x, y, z; }
        struct Sample { public float r; }
        static Sample _CrestOceanMaskTexture, _CrestWaterVolumeFrontFaceTexture;
        static int _CrestRefractionPass;
        public static bool ReachedBlending;
        static Sample UNITY_SAMPLE_SCREENSPACE_TEXTURE(Sample texture, Vec2 uv) => texture;
        public static bool Classify(string variant, float mask, float front, float deviceDepth, ref Vec3 color)
        {
            _CrestOceanMaskTexture.r = mask; _CrestWaterVolumeFrontFaceTexture.r = front;
            _CrestRefractionPass = 1; ReachedBlending = false;
            var positionNDC = new Vec2 { x = .25f, y = .75f }; var positionWS = new Vec3 { x = 1f, y = 2f, z = 3f };
            if (variant == "Portal") return Portal(positionNDC, positionWS, deviceDepth, .5f, ref color);
            if (variant == "Volume") return Volume(positionNDC, positionWS, deviceDepth, .5f, ref color);
            return FullScreen(positionNDC, positionWS, deviceDepth, .5f, ref color);
        }
    }
}
namespace Crest.Examples
{
    public abstract partial class CustomPassForCameraBase
    {
        protected abstract int Event { get; }
        int _currentEvent;
        CommandBuffer _buffer;
        void OnBeforeRender(Camera camera) { }
        void CleanCameras() { }
        protected abstract void Execute(CommandBuffer buffer, Camera camera, RenderTextureDescriptor descriptor);
        protected abstract void Clear(CommandBuffer buffer, Camera camera);
        public void Enable() => typeof(CustomPassForCameraBase).GetMethod("OnEnable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(this, null);
        public void Disable() => typeof(CustomPassForCameraBase).GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(this, null);
        public bool Tick()
        {
            var method = typeof(CustomPassForCameraBase).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null) return false;
            method.Invoke(this, null); return true;
        }
    }
    public sealed class ProbePass : CustomPassForCameraBase
    {
        public CameraEvent Selected;
        protected override int Event => (int)Selected;
        public readonly List<(CommandBuffer command, Camera camera, RenderTextureDescriptor descriptor)> Executions
            = new List<(CommandBuffer, Camera, RenderTextureDescriptor)>();
        public readonly List<Camera> Clears = new List<Camera>();
        protected override void Execute(CommandBuffer command, Camera camera, RenderTextureDescriptor descriptor)
            => Executions.Add((command, camera, descriptor));
        protected override void Clear(CommandBuffer command, Camera camera) => Clears.Add(camera);
    }
}

static class Program
{
    internal static RenderTextureDescriptor Descriptor(int width = 1280, int height = 720, int slices = 1, bool dynamic = false, bool valid = true)
        => new RenderTextureDescriptor { width = width, height = height, volumeDepth = slices, useDynamicScale = dynamic,
            graphicsFormat = valid ? UnityEngine.Experimental.Rendering.GraphicsFormat.Color : UnityEngine.Experimental.Rendering.GraphicsFormat.None,
            colorFormat = RenderTextureFormat.DefaultHDR, msaaSamples = 4, bindMS = true };
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static bool Color(ScriptableRenderPass pass) => (pass.Input & ScriptableRenderPassInput.Color) != 0;
    static CrestUniversalRendererFeature Feature(int layers = 1)
    {
        var feature = new CrestUniversalRendererFeature(); feature.Create(); feature.SetLayers(layers); return feature;
    }
    static UnderwaterRenderer Water(Camera camera, bool early = true)
    {
        var water = new UnderwaterRenderer { EnableShaderAPI = early };
        UnderwaterRenderer.Cameras[camera] = water; return water;
    }
    static void Inputs(int layers, bool early, bool active, bool enabled, bool included, bool color, bool late)
    {
        var camera = new Camera { cullingMask = included ? -1 : 0 };
        var water = Water(camera, early); water.IsActive = active; water.isActiveAndEnabled = enabled;
        var feature = Feature(layers); var passes = feature.Record(camera).Passes;
        Check(passes.Contains(feature.Before) && (feature.Before.Input & ScriptableRenderPassInput.Depth) != 0, "missing ocean depth input");
        Check(Color(feature.Before) == color, "opaque Color input does not match refraction and early fog requirements");
        Check(passes.Contains(feature.After) == late, "incorrect late underwater pass selection");
        if (late) Check(!Color(feature.After), "late underwater pass unnecessarily requests opaque Color");
    }
    static void CallbackMapping(CameraEvent at, int expected)
    {
        var camera = Camera.main = new Camera();
        var probe = new ProbePass { Selected = at }; probe.Enable();
        for (var stage = 0; stage < 3; stage++)
        {
            Check(CrestUniversalRendererFeature.Subscribers(stage) == (stage == expected ? 1 : 0), "wrong callback subscription for " + at);
            CrestUniversalRendererFeature.Fire(stage, new CommandBuffer(), camera, Descriptor());
        }
        Check(probe.Executions.Count == 1, "callback did not execute exactly once at its stage");
        probe.Disable();
        Check(Enumerable.Range(0, 3).All(i => CrestUniversalRendererFeature.Subscribers(i) == 0), "disable retained a callback");
    }
    static void CallbackReconfigure()
    {
        var camera = Camera.main = new Camera();
        var probe = new ProbePass { Selected = CameraEvent.BeforeDepthTexture }; probe.Enable();
        foreach (var change in new[] { (CameraEvent.AfterDepthTexture, 1), (CameraEvent.BeforeForwardAlpha, 2), (CameraEvent.BeforeForwardOpaque, 0) })
        {
            probe.Selected = change.Item1;
            Check(probe.Tick(), "production has no LateUpdate callback reconfiguration");
            probe.Tick();
            var before = probe.Executions.Count;
            for (var stage = 0; stage < 3; stage++)
            {
                Check(CrestUniversalRendererFeature.Subscribers(stage) == (stage == change.Item2 ? 1 : 0), "old subscription survived event change");
                CrestUniversalRendererFeature.Fire(stage, new CommandBuffer(), camera, Descriptor());
            }
            Check(probe.Executions.Count == before + 1, "event change duplicated execution");
        }
        probe.Disable();
    }
    static void CallbackCameras()
    {
        var main = Camera.main = new Camera(); var scene = new Camera { cameraType = CameraType.SceneView };
        var secondary = new Camera(); var reflection = new Camera { cameraType = CameraType.Reflection };
        var probe = new ProbePass { Selected = CameraEvent.BeforeDepthTexture }; probe.Enable();
        var command = new CommandBuffer(); var descriptor = Descriptor(640, 360);
        foreach (var camera in new[] { main, scene, secondary, reflection }) CrestUniversalRendererFeature.Fire(0, command, camera, descriptor);
        Check(probe.Executions.Select(x => x.camera).SequenceEqual(new[] { main, scene }), "main/SceneView filtering failed");
        Check(probe.Clears.SequenceEqual(new[] { secondary, reflection }), "other cameras did not clear");
        Check(probe.Executions.All(x => ReferenceEquals(x.command, command) && x.descriptor.width == 640 && x.descriptor.height == 360),
            "callback lost command buffer or camera descriptor");
        probe.Disable();
    }
    static (RenderGraph graph, Builder builder) Declare(UnderwaterRenderer water, RenderTextureDescriptor descriptor, bool write = true)
    {
        var graph = new RenderGraph(); var builder = new Builder(); water.DeclareURPTextures(graph, builder, descriptor, write); return (graph, builder);
    }
    static void NoPlaceholders() => Check(RTHandles.Allocations.All(h => h.DescriptorAtAllocation.width > 0 && h.DescriptorAtAllocation.height > 0),
        "wrapped an unconfigured zero-size volume placeholder");
    static void Black(Builder builder, RenderGraph graph, params int[] ids)
    {
        foreach (var id in ids) Check(builder.Globals.TryGetValue(id, out var binding) && ReferenceEquals(binding, graph.defaultResources.blackTextureXR),
            "inactive texture global was not zeroed: " + id);
    }
    static void ImportsCurrent(RenderGraph graph, RenderTextureDescriptor expected)
    {
        foreach (var handle in graph.Imports)
        {
            var d = handle.Descriptor;
            Check(d.width == expected.width && d.height == expected.height && d.volumeDepth == expected.volumeDepth && d.useDynamicScale == expected.useDynamicScale,
                "import retained a stale descriptor for " + handle.Wrapper.Texture.Name);
            Check(handle.Wrapper.Texture.IsCreated(), "imported uncreated target");
            Check(d.msaaSamples == 1 && !d.bindMS, "imported a multisampled sampled target");
        }
    }
    static void LateVolume()
    {
        var water = new UnderwaterRenderer(); Declare(water, Descriptor());
        Check(water.FrontHandle == null && water.BackHandle == null && RTHandles.Allocations.Count == 2, "FullScreen eagerly wrapped volume targets");
        water._mode = UnderwaterRenderer.Mode.Volume; water._volumeGeometry = new object();
        var declared = Declare(water, Descriptor());
        Check(declared.graph.Imports.Count == 4 && water.FrontHandle != null && water.BackHandle != null, "late volume failed to acquire both wrappers");
        NoPlaceholders(); ImportsCurrent(declared.graph, Descriptor());
    }
    static void PortalToVolume(UnderwaterRenderer.Mode mode)
    {
        var water = new UnderwaterRenderer { _mode = UnderwaterRenderer.Mode.Portal, _volumeGeometry = new object() };
        var portal = Declare(water, Descriptor());
        Check(portal.graph.Imports.Count == 3 && water.BackHandle == null && RTHandles.Allocations.Count == 3, "Portal wrapped unused back target");
        Black(portal.builder, portal.graph, 4); NoPlaceholders();
        water._mode = mode;
        var volume = Declare(water, Descriptor());
        Check(volume.graph.Imports.Count == 4 && water.BackHandle != null, "Portal-to-volume did not wrap its newly required back face");
        ImportsCurrent(volume.graph, Descriptor()); NoPlaceholders();
    }
    static void ResizeThroughPortal()
    {
        var water = new UnderwaterRenderer { _mode = UnderwaterRenderer.Mode.Volume, _volumeGeometry = new object() };
        Declare(water, Descriptor());
        var front = water.FrontHandle; var back = water.BackHandle;
        var next = Descriptor(1920, 1080, 2, true);
        water._mode = UnderwaterRenderer.Mode.Portal;
        ImportsCurrent(Declare(water, next).graph, next);
        water._mode = UnderwaterRenderer.Mode.Volume;
        var returned = Declare(water, next); ImportsCurrent(returned.graph, next);
        Check(ReferenceEquals(front.Texture, water.Front) && ReferenceEquals(back.Texture, water.Back), "wrapper no longer owns the configured target identity");
        Check(returned.builder.Globals[3].Wrapper.Texture == water.Front && returned.builder.Globals[4].Wrapper.Texture == water.Back,
            "volume globals do not reference current face targets");
    }
    static void InactiveGlobals(bool missingGeometry)
    {
        var water = new UnderwaterRenderer { _mode = UnderwaterRenderer.Mode.Volume, _volumeGeometry = new object() };
        Declare(water, Descriptor());
        if (missingGeometry) water._volumeGeometry = null; else water._mode = UnderwaterRenderer.Mode.FullScreen;
        var inactive = Declare(water, Descriptor());
        Check(inactive.graph.Imports.Count == 2, "inactive volume still imported its faces");
        Black(inactive.builder, inactive.graph, 3, 4);
    }
    static void OverlayGlobals()
    {
        var water = new UnderwaterRenderer { _mode = UnderwaterRenderer.Mode.Volume, _volumeGeometry = new object() };
        Declare(water, Descriptor()); water._overlay = true;
        var allocations = RTHandles.Allocations.Count;
        var overlay = Declare(water, Descriptor());
        Check(overlay.graph.Imports.Count == 0 && RTHandles.Allocations.Count == allocations, "overlay allocated or imported masks");
        Black(overlay.builder, overlay.graph, 1, 2, 3, 4);
    }
    static void OverlayTransition(bool initial)
    {
        var water = new UnderwaterRenderer(); water.InitOverlay(initial); water.Tick();
        Check(water.Disables == 0 && water.Enables == 0, "unchanged overlay triggered lifecycle");
        water._overlay = !initial; water.Tick();
        Check(water.Disables == 1 && water.Enables == 1 && water.MaskEnabled == initial, "Player overlay transition did not update mask enablement");
        Check(water.Lifecycle.SequenceEqual(new[] { "disable", "enable" }), "overlay lifecycle order changed");
        water.Tick(); water.Tick();
        Check(water.Disables == 1 && water.Enables == 1, "unchanged overlay repeated lifecycle");
    }
    static void FogClassification(string variant, float mask, float front, float depth, bool underwater)
    {
        var color = new FogGuards.Vec3 { x = .1f, y = .2f, z = .3f }; var original = color;
        var classified = FogGuards.Classify(variant, mask, front, depth, ref color);
        Check(classified == underwater, "incorrect underwater classification / MixFog suppression");
        Check(color.Equals(original) && !FogGuards.ReachedBlending, "refraction guard changed color or reached deferred fog blending");
    }
    static int Main()
    {
        var tests = new List<(string name, Action test)>
        {
            ("refraction copy without any underwater component requests Depth only", () =>
            {
                var feature = Feature(); feature.Record(new Camera());
                Check(feature.Before.Input == ScriptableRenderPassInput.Depth, "surface-only refraction still requests opaque Color");
            }),
            ("refraction copy without early fog requests Depth only", () => Inputs(1, false, true, true, true, false, true)),
            ("active early shader fog retains Color input", () => Inputs(1, true, true, true, true, true, false)),
            ("no refraction copy requests opaque Color", () => Inputs(0, false, true, true, true, true, true)),
            ("inactive early fog does not request opaque Color", () => Inputs(1, true, false, true, true, false, false)),
            ("disabled early fog does not request opaque Color", () => Inputs(1, true, true, false, true, false, false)),
            ("excluded underwater layer does not request opaque Color", () => Inputs(1, true, true, true, false, false, false)),
            ("input requirements reset between cameras using the same feature", () =>
            {
                var feature = Feature(); var camera = new Camera(); var water = Water(camera);
                feature.Record(camera); Check(Color(feature.Before), "early fog lacks Color");
                water.EnableShaderAPI = false; feature.Record(camera);
                Check(!Color(feature.Before) && !Color(feature.After), "Color input remained after early fog was disabled");
            }),
            ("preview and invalid-target cameras enqueue no passes", () =>
            {
                var feature = Feature(); Check(feature.Record(new Camera { cameraType = CameraType.Preview }).Passes.Count == 0, "Preview recorded passes");
                Check(feature.Record(new Camera(), valid: false).Passes.Count == 0, "invalid graphics format recorded passes");
            }),
            ("ordinary reflection probes do not acquire planar culling passes", () =>
            {
                var passes = Feature().Record(new Camera { cameraType = CameraType.Reflection }).Passes;
                Check(passes.Count == 1 && passes[0].Name == "objects", "unmarked probe acquired water/planar passes");
            }),
            ("marked reflection acquires culling passes before the ocean-null check", () =>
            {
                OceanRenderer.Instance = null;
                var camera = CrestRenderRequest.ReflectionCamera = new Camera { cameraType = CameraType.Reflection };
                var names = Feature().Record(camera).Passes.Select(p => p.Name);
                Check(names.SequenceEqual(new[] { "reflection-invert", "reflection-restore", "objects" }), "marked camera lacks ordered planar culling markers");
            }),
            ("overlay camera retains refracted objects but skips ocean passes", () =>
                Check(Feature().Record(new Camera(), CameraRenderType.Overlay).Passes.Select(p => p.Name).SequenceEqual(new[] { "objects" }), "overlay pass selection failed")),
            ("callback reconfiguration replaces subscriptions without duplicates", CallbackReconfigure),
            ("callbacks execute main/SceneView and clear secondary/reflection cameras", CallbackCameras),
            ("unsupported callback event warns and subscribes nowhere", () =>
            {
                var probe = new ProbePass { Selected = CameraEvent.AfterEverything }; probe.Enable();
                Check(Enumerable.Range(0, 3).All(i => CrestUniversalRendererFeature.Subscribers(i) == 0) && Debug.Warnings.Count == 1, "unsupported event silently selected a stage");
                probe.Disable();
            }),
            ("active callback stages are enqueued at depth/opaque events", () =>
            {
                var depth = new ProbePass { Selected = CameraEvent.AfterDepthTexture }; var opaque = new ProbePass { Selected = CameraEvent.BeforeForwardAlpha };
                depth.Enable(); opaque.Enable(); var passes = Feature().Record(new Camera()).Passes;
                Check(passes.Any(p => p.Name == "callback-depth" && p.renderPassEvent == RenderPassEvent.AfterRenderingPrePasses)
                    && passes.Any(p => p.Name == "callback-opaque" && p.renderPassEvent == RenderPassEvent.AfterRenderingOpaques), "callback passes missing or scheduled at wrong events");
                depth.Disable(); opaque.Disable();
            }),
            ("late volume lazily acquires configured face wrappers", LateVolume),
            ("Portal-to-Volume wraps the previously unused back target", () => PortalToVolume(UnderwaterRenderer.Mode.Volume)),
            ("Portal-to-VolumeFlyThrough wraps the previously unused back target", () => PortalToVolume(UnderwaterRenderer.Mode.VolumeFlyThrough)),
            ("resize/array-depth/dynamic-scale changes reach imported volume faces", ResizeThroughPortal),
            ("FullScreen zeroes former volume globals", () => InactiveGlobals(false)),
            ("missing geometry zeroes former volume globals", () => InactiveGlobals(true)),
            ("overlay zeroes all globals without importing targets", OverlayGlobals),
            ("read declarations use Read access and do not publish globals", () =>
            {
                var declared = Declare(new UnderwaterRenderer { _mode = UnderwaterRenderer.Mode.Volume, _volumeGeometry = new object() }, Descriptor(), false);
                Check(declared.graph.Imports.Count == 4 && declared.builder.Uses.All(x => x.access == AccessFlags.Read)
                    && declared.builder.Globals.Count == 0, "read-only declaration modified access/global publication"); NoPlaceholders();
            }),
            ("Player overlay true-to-false enables the mask exactly once", () => OverlayTransition(true)),
            ("Player overlay false-to-true disables the mask exactly once", () => OverlayTransition(false)),
            ("BeginCameraRendering binds every tile including invisible tiles for each camera", () =>
            {
                var a = new Camera(); var b = new Camera { cameraType = CameraType.SceneView };
                var visible = new OceanChunkRenderer { Visible = true }; var hidden = new OceanChunkRenderer();
                OceanRenderer.Instance.Tiles.AddRange(new[] { visible, hidden });
                OceanChunkRenderer.Begin(a); OceanChunkRenderer.Begin(b);
                Check(visible.BoundCameras.SequenceEqual(new[] { a, b }) && hidden.BoundCameras.SequenceEqual(new[] { a, b })
                    && OceanChunkRenderer.Current == b, "camera callback did not bind all tiles before renderer lists");
            }),
            ("BeginCameraRendering handles no active ocean", () =>
            {
                OceanRenderer.Instance = null; var camera = new Camera(); OceanChunkRenderer.Begin(camera);
                Check(OceanChunkRenderer.Current == camera, "current camera was not updated without an ocean");
            }),
            ("refracted zero-mask pixel permits atmospheric fog", () => FogClassification("FullScreen", 0f, 0f, .5f, false)),
            ("refracted positive-mask pixel permits atmospheric fog", () => FogClassification("FullScreen", 1f, 0f, .5f, false)),
            ("refracted submerged pixel suppresses MixFog without changing color", () => FogClassification("FullScreen", -1f, 0f, .5f, true)),
            ("refracted Portal with no visible front remains above water", () => FogClassification("Portal", -1f, 0f, .5f, false)),
            ("refracted Portal fragment before its front remains above water", () => FogClassification("Portal", -1f, .2f, .8f, false)),
            ("refracted Portal fragment behind its front suppresses MixFog", () => FogClassification("Portal", -1f, .8f, .2f, true)),
            ("refracted Volume fragment before its front permits atmospheric fog", () => FogClassification("Volume", -1f, .2f, .8f, false)),
            ("refracted Volume submerged pixel without visible front suppresses MixFog", () => FogClassification("Volume", -1f, 0f, .5f, true)),
        };
        foreach (var mapping in new[] {
            (CameraEvent.BeforeDepthTexture, 0), (CameraEvent.BeforeDepthNormalsTexture, 0), (CameraEvent.BeforeForwardOpaque, 0),
            (CameraEvent.AfterDepthTexture, 1), (CameraEvent.AfterDepthNormalsTexture, 1),
            (CameraEvent.AfterForwardOpaque, 2), (CameraEvent.BeforeForwardAlpha, 2) })
        {
            var value = mapping; tests.Add(("callback event maps to correct stage: " + value.Item1, () => CallbackMapping(value.Item1, value.Item2)));
        }
        var passed = 0;
        foreach (var test in tests)
        {
            Camera.Reset(); CrestUniversalRendererFeature.ResetEvents(); UnderwaterRenderer.Cameras.Clear(); RTHandles.Allocations.Clear(); Debug.Warnings.Clear();
            OceanRenderer.Instance = new OceanRenderer(); CrestRenderRequest.ReflectionCamera = null;
            GraphicsSettings.currentRenderPipeline = new object();
            try { test.test(); passed++; Console.WriteLine("PASS: " + test.name); }
            catch (Exception error) { Console.WriteLine("FAIL: " + test.name + ": " + (error.InnerException ?? error).Message); }
        }
        Console.WriteLine($"URP rendering review: {passed}/{tests.Count} behavioral checks passed; {tests.Count - passed} failed.");
        Console.WriteLine("Player: extracted production methods/constructor and translated HLSL classification prefix with instrumented managed APIs. No Unity compilation, native RTHandle validation, RenderGraph/GPU execution or actual fog blending.");
        return passed == tests.Count ? 0 : 1;
    }
}
