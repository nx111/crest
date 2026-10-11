// Managed substitutes for selected production lifecycle methods. No Unity/GPU emulation.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityEngine
{
    public class Object { public bool Destroyed; }
    public class Material : Object { }
    public class ComputeShader : Object { }
    public class RenderTexture : Object
    {
        public bool Created = true;
        public void Release()
        {
            if (Destroyed) throw new Exception("Releasing a destroyed texture");
            Created = false;
        }
    }
    public class Camera : Object
    {
        public Crest.UnderwaterRenderer Renderer;
        public bool IsEditorCamera;
        public bool TryGetComponent<T>(out T component) where T : class
        {
            component = Renderer as T;
            return component != null;
        }
    }
}

namespace UnityEngine.Rendering
{
    public class CommandBuffer
    {
        public int Releases;
        public void Release()
        {
            if (++Releases != 1) throw new Exception("Command buffer released twice");
        }
    }
    public class RTHandle
    {
        public static readonly List<RTHandle> All = new List<RTHandle>();
        public readonly RenderTexture Texture;
        public int Releases;
        public RTHandle(RenderTexture texture) { Texture = texture; All.Add(this); }
        public void Release()
        {
            if (++Releases != 1) throw new Exception("RTHandle released twice");
        }
    }
}

namespace Crest
{
    public static class Helpers
    {
        public static void Destroy(UnityEngine.Object value)
        {
            if (value == null) return;
            if (value.Destroyed) throw new Exception("Native object destroyed twice");
            if (value is RenderTexture texture && RTHandle.All.Any(handle => handle.Texture == texture && handle.Releases == 0))
                throw new Exception("Texture destroyed before its RTHandle was released");
            value.Destroyed = true;
        }
    }
    public class PropertyWrapperMaterial { public Material material = new Material(); }
    public class OceanRenderer
    {
        public static OceanRenderer Instance;
        public Camera ViewCameraExcludingSceneCamera;
    }
    public partial class UnderwaterRenderer
    {
        public static UnderwaterRenderer Instance { get; private set; }
        internal static Camera s_PrimaryCamera;
        public bool isActiveAndEnabled = true;
        public int EditorEnables, EditorDisables;
        public static int KeywordResets;

        CommandBuffer _oceanMaskCommandBuffer, _underwaterEffectCommandBuffer;
        PropertyWrapperMaterial _oceanMaskMaterial, _underwaterEffectMaterial;
        Material _volumeMaterial, _currentOceanMaterial;
        ComputeShader _fixMaskComputeShader;
        RenderTexture _maskRT, _depthRT, _volumeFrontFaceRT, _volumeBackFaceRT;
        RTHandle _urpMask, _urpDepth, _urpVolumeFront, _urpVolumeBack;

        static bool IsActiveForEditorCamera(Camera camera, UnderwaterRenderer renderer) => camera?.IsEditorCamera == true;
        static void DisableOceanMaskKeywords() => KeywordResets++;
        void EnableEditMode()
        {
            if (Instance != this) throw new Exception("New instance must be assigned before editor registration");
            EditorEnables++;
        }
        void DisableEditMode()
        {
            if (Instance != this) throw new Exception("Old instance must be removed before changing editor ownership");
            EditorDisables++;
        }

        public void Tick() => UpdateInstance();
        public void DisableMask() => OnDisableMask();
        public void DestroyComponent()
        {
            // Older revisions lack OnDestroy entirely; absence must fail the ownership assertions, not compilation.
            typeof(UnderwaterRenderer).GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(this, null);
        }
        public static void Reset()
        {
            Instance = null;
            s_PrimaryCamera = null;
            OceanRenderer.Instance = new OceanRenderer();
            KeywordResets = 0;
            RTHandle.All.Clear();
        }

        public ResourceSnapshot Allocate(bool partial = false)
        {
            _underwaterEffectCommandBuffer = new CommandBuffer();
            _underwaterEffectMaterial = new PropertyWrapperMaterial();
            _currentOceanMaterial = new Material();
            _fixMaskComputeShader = new ComputeShader();
            if (!partial)
            {
                _oceanMaskCommandBuffer = new CommandBuffer();
                _oceanMaskMaterial = new PropertyWrapperMaterial();
                _volumeMaterial = new Material();
                _maskRT = new RenderTexture();
                _depthRT = new RenderTexture();
                _volumeFrontFaceRT = new RenderTexture();
                _volumeBackFaceRT = new RenderTexture();
                _urpMask = new RTHandle(_maskRT);
                _urpDepth = new RTHandle(_depthRT);
                _urpVolumeFront = new RTHandle(_volumeFrontFaceRT);
                _urpVolumeBack = new RTHandle(_volumeBackFaceRT);
            }
            return new ResourceSnapshot
            {
                Owned = new UnityEngine.Object[] { _oceanMaskMaterial?.material, _underwaterEffectMaterial.material, _volumeMaterial,
                    _maskRT, _depthRT, _volumeFrontFaceRT, _volumeBackFaceRT }.Where(value => value != null).ToArray(),
                Shared = new UnityEngine.Object[] { _currentOceanMaterial, _fixMaskComputeShader },
                Buffers = new[] { _oceanMaskCommandBuffer, _underwaterEffectCommandBuffer }.Where(value => value != null).ToArray(),
                Handles = RTHandle.All.ToArray(),
            };
        }
    }

    public class ResourceSnapshot
    {
        public UnityEngine.Object[] Owned, Shared;
        public CommandBuffer[] Buffers;
        public RTHandle[] Handles;
        public void AssertDestroyed()
        {
            Check.That(Owned.All(value => value.Destroyed), "Owned material/texture object leaked");
            Check.That(Owned.OfType<RenderTexture>().All(value => !value.Created), "Texture allocation not released");
            Check.That(Buffers.All(value => value.Releases == 1), "Owned command buffer leaked");
            Check.That(Handles.All(value => value.Releases == 1), "RTHandle leaked");
            Check.That(Shared.All(value => !value.Destroyed), "Shared ocean material or compute shader destroyed");
        }
    }
    public static class Check
    {
        public static void That(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
    }
    static class Program
    {
        static UnderwaterRenderer SetPrimary(out Camera camera)
        {
            var renderer = new UnderwaterRenderer();
            camera = new Camera { Renderer = renderer };
            OceanRenderer.Instance.ViewCameraExcludingSceneCamera = camera;
            renderer.Tick();
            return renderer;
        }

        static int Main()
        {
            var cases = new (string Name, Action Test)[]
            {
                ("Switch to a camera without an underwater renderer clears the primary instance", () =>
                {
                    var old = SetPrimary(out _);
                    var next = new Camera();
                    OceanRenderer.Instance.ViewCameraExcludingSceneCamera = next;
                    old.Tick();
                    Check.That(UnderwaterRenderer.Instance == null && UnderwaterRenderer.Get(next) == null, "Old camera renderer leaked into new camera");
#if UNITY_EDITOR
                    Check.That(old.EditorDisables == 1, "Old editor callbacks remain registered");
#endif
                }),
                ("Missing primary camera clears the primary instance", () =>
                {
                    var renderer = SetPrimary(out _);
                    OceanRenderer.Instance.ViewCameraExcludingSceneCamera = null;
                    renderer.Tick();
                    Check.That(UnderwaterRenderer.Instance == null, "Missing primary camera kept its renderer");
                }),
                ("Removing the ocean clears cached camera and renderer", () =>
                {
                    var renderer = SetPrimary(out _);
                    OceanRenderer.Instance = null;
                    renderer.Tick();
                    Check.That(UnderwaterRenderer.Instance == null && UnderwaterRenderer.s_PrimaryCamera == null, "Ocean removal left stale primary state");
                }),
                ("A disabled primary renderer is not selected", () =>
                {
                    var renderer = new UnderwaterRenderer { isActiveAndEnabled = false };
                    OceanRenderer.Instance.ViewCameraExcludingSceneCamera = new Camera { Renderer = renderer };
                    new UnderwaterRenderer().Tick();
                    Check.That(UnderwaterRenderer.Instance == null, "Disabled renderer selected as primary");
                }),
                ("Adding a renderer to the same primary camera is discovered", () =>
                {
                    var camera = new Camera();
                    OceanRenderer.Instance.ViewCameraExcludingSceneCamera = camera;
                    var observer = new UnderwaterRenderer();
                    observer.Tick();
                    var renderer = camera.Renderer = new UnderwaterRenderer();
                    observer.Tick();
                    Check.That(UnderwaterRenderer.Get(camera) == renderer, "Primary camera retained negative component lookup");
                }),
                ("Removing a renderer from the same primary camera is discovered", () =>
                {
                    var renderer = SetPrimary(out var camera);
                    camera.Renderer = null;
                    new UnderwaterRenderer().Tick();
                    Check.That(UnderwaterRenderer.Instance == null, "Removed component remained the primary renderer");
                }),
                ("Valid primary switches preserve editor callback ordering and unchanged frames are stable", () =>
                {
                    var old = SetPrimary(out _);
                    old.Tick();
                    var next = new UnderwaterRenderer();
                    var camera = new Camera { Renderer = next };
                    OceanRenderer.Instance.ViewCameraExcludingSceneCamera = camera;
                    old.Tick();
                    next.Tick();
                    Check.That(UnderwaterRenderer.Get(camera) == next, "Valid primary switch failed");
#if UNITY_EDITOR
                    Check.That(old.EditorEnables == 1 && old.EditorDisables == 1 && next.EditorEnables == 1 && next.EditorDisables == 0,
                        "Editor subscriptions repeated or were not transferred");
#endif
                }),
                ("Secondary camera lookup remains independent", () =>
                {
                    SetPrimary(out _);
                    var renderer = new UnderwaterRenderer();
                    var camera = new Camera { Renderer = renderer };
                    Check.That(UnderwaterRenderer.Get(camera) == renderer, "Secondary camera inherited the primary renderer");
                }),
                ("Destroy releases all owned resources and preserves shared assets", () =>
                {
                    var renderer = new UnderwaterRenderer();
                    var resources = renderer.Allocate();
                    renderer.DestroyComponent();
                    resources.AssertDestroyed();
                    Check.That(UnderwaterRenderer.KeywordResets == 0, "Destroy changed another camera's global mask keywords");
                }),
                ("Disable permits resource reuse; subsequent destroy is idempotent", () =>
                {
                    var renderer = new UnderwaterRenderer();
                    var resources = renderer.Allocate();
                    renderer.DisableMask();
                    Check.That(resources.Owned.All(value => !value.Destroyed) && resources.Buffers.All(value => value.Releases == 0), "Disable destroyed reusable objects");
                    Check.That(resources.Owned.OfType<RenderTexture>().All(value => !value.Created), "Disable kept texture hardware allocations");
                    renderer.DestroyComponent();
                    renderer.DestroyComponent();
                    resources.AssertDestroyed();
                    Check.That(UnderwaterRenderer.KeywordResets == 1, "Destroy repeated global keyword teardown");
                }),
                ("Partially initialized and empty renderers destroy safely", () =>
                {
                    var renderer = new UnderwaterRenderer();
                    var resources = renderer.Allocate(partial: true);
                    renderer.DestroyComponent();
                    renderer.DestroyComponent();
                    resources.AssertDestroyed();
                    new UnderwaterRenderer().DestroyComponent();
                }),
            };

            int passed = 0;
            foreach (var item in cases)
            {
                UnderwaterRenderer.Reset();
                try { item.Test(); passed++; Console.WriteLine("PASS " + item.Name); }
                catch (Exception exception) { Console.WriteLine("FAIL " + item.Name + ": " + (exception.InnerException ?? exception).Message); }
            }
            Console.WriteLine($"{passed}/{cases.Length} underwater lifecycle checks passed.");
            return passed == cases.Length ? 0 : 1;
        }
    }
}
