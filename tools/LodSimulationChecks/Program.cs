// Executes the production LOD manager control flow. Dispatches transport a known
// marker so frame/substep ownership can be verified without emulating wave physics.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Crest;
using UnityEngine;
using UnityEngine.Rendering;

static class Program
{
    static int Main()
    {
        var tests = new (string name, Action run)[]
        {
            ("foam exact three-step batch retains a nonnegative remainder", () => NonnegativeRemainder(false)),
            ("dynamic waves exact three-step batch retains a nonnegative remainder", () => NonnegativeRemainder(true)),
            ("foam pause after three substeps reprojects the last completed frame", () => PauseAfterBatch(false)),
            ("dynamic waves pause after three substeps reprojects the last completed frame", () => PauseAfterBatch(true)),
            ("nonpositive requested steps still reproject without publishing stale history", NegativeRemainder),
            ("a normal substep batch preserves source and target ping-pong", PositiveBatch),
            ("frames below the fixed step preserve accumulated time and history", FractionalFrames),
        };
        int failed = 0;
        foreach (var test in tests)
        {
            OceanRenderer.Instance = new OceanRenderer();
            try { test.run(); Console.WriteLine("PASS: " + test.name); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL: " + test.name + ": " + error.Message); }
        }
        Console.WriteLine($"LOD simulation: {tests.Length - failed}/{tests.Length} passed; {failed} failed.");
        Console.WriteLine("Production C# manager control flow with managed texture/command substitutes; no Unity or GPU execution.");
        return failed == 0 ? 0 : 1;
    }

    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Near(float actual, float expected, string message)
        => Check(Math.Abs(actual - expected) < .000001f, $"{message}: expected {expected}, got {actual}");
    static LodDataMgrPersistent Manager(bool dynamicWaves)
        => dynamicWaves ? new LodDataMgrDynWaves(OceanRenderer.Instance) : new LodDataMgrFoam(OceanRenderer.Instance);
    static float Remaining(LodDataMgrPersistent manager)
        => (float)typeof(LodDataMgrPersistent).GetField("_timeToSimulate", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
    static CommandBuffer Frame(LodDataMgr manager, float dt)
    {
        var ocean = OceanRenderer.Instance;
        ocean.DeltaTime = dt;
        var commands = new CommandBuffer();
        manager.UpdateLodData();
        manager.BuildCommandBuffer(ocean, commands);
        return commands;
    }

    static void NonnegativeRemainder(bool dynamicWaves)
    {
        var manager = Manager(dynamicWaves);
        try
        {
            var commands = Frame(manager, dynamicWaves ? .05f : .1f);
            Check(commands.Dispatches.Count == 3 && manager.LastUpdateSubstepCount == 3, "exact batch did not contain three steps");
            Check(Remaining(manager) >= 0, "subtractions left a negative remainder: " + Remaining(manager).ToString("G9"));
        }
        finally { manager.OnDisable(); }
    }

    static void PauseAfterBatch(bool dynamicWaves)
    {
        var manager = Manager(dynamicWaves);
        try
        {
            Frame(manager, dynamicWaves ? .05f : .1f);
            var previous = manager.DataTexture;
            Near(previous.Marker, 3, "initial completed batch");
            for (int frame = 0; frame < 4; frame++)
            {
                var commands = Frame(manager, 0);
                Check(manager.LastUpdateSubstepCount == 0, "paused frame reported " + manager.LastUpdateSubstepCount + " substeps");
                Check(commands.Dispatches.Count == 1, "paused frame dispatched " + commands.Dispatches.Count + " reprojections");
                var dispatch = commands.Dispatches[0];
                Check(ReferenceEquals(dispatch.Source, previous), "paused frame did not read the last completed output");
                Check(ReferenceEquals(dispatch.Target, manager.DataTexture), "published target was not written");
                Near(dispatch.Dt, 0, "paused simulation advanced");
                Near(manager.DataTexture.Marker, 3, "paused frame lost completed history");
                previous = manager.DataTexture;
            }
        }
        finally { manager.OnDisable(); }
    }

    static void NegativeRemainder()
    {
        var manager = Manager(false);
        try
        {
            // Isolate the defensive scheduling boundary independently of rounding prevention.
            typeof(LodDataMgrPersistent).GetField("_timeToSimulate", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(manager, -1e-8f);
            var commands = Frame(manager, 0);
            Check(commands.Dispatches.Count == 1 && commands.Dispatches[0].Dt == 0, "negative count skipped reprojection");
            Check(manager.LastUpdateSubstepCount == 0, "negative public substep count");
            Check(Remaining(manager) >= 0, "negative remainder was not recovered");
        }
        finally { manager.OnDisable(); }
    }

    static void PositiveBatch()
    {
        var manager = Manager(true);
        try
        {
            Frame(manager, 1f / 60f);
            var previous = manager.DataTexture;
            var commands = Frame(manager, .1f);
            Check(commands.Dispatches.Count == 6 && manager.LastUpdateSubstepCount == 6, "positive batch size changed");
            for (int i = 0; i < commands.Dispatches.Count; i++)
            {
                var dispatch = commands.Dispatches[i];
                Check(ReferenceEquals(dispatch.Source, previous), "substep read the wrong predecessor");
                Near(dispatch.Dt, 1f / 60f, "fixed step changed");
                previous = dispatch.Target;
            }
            Check(ReferenceEquals(manager.DataTexture, previous), "final ping-pong target was not published");
            Near(manager.DataTexture.Marker, 7, "positive dispatch marker count");
        }
        finally { manager.OnDisable(); }
    }

    static void FractionalFrames()
    {
        var manager = Manager(false);
        try
        {
            var accumulated = 0f;
            for (int frame = 0; frame < 3; frame++)
            {
                var commands = Frame(manager, 1f / 120f);
                accumulated += 1f / 120f;
                Check(manager.LastUpdateSubstepCount == 0 && commands.Dispatches.Count == 1, "sub-fixed frame did not reproject");
                Near(commands.Dispatches[0].Dt, 0, "sub-fixed frame advanced physics");
                Near(Remaining(manager), accumulated, "unconsumed time changed");
            }
            var due = Frame(manager, 1f / 120f);
            Check(manager.LastUpdateSubstepCount == 1 && due.Dispatches.Count == 1, "accumulated fixed step was dropped");
            Near(due.Dispatches[0].Dt, 1f / 30f, "accumulated fixed step changed");
            Near(manager.DataTexture.Marker, 1, "fractional frames changed history");
        }
        finally { manager.OnDisable(); }
    }
}

namespace UnityEngine.Experimental.Rendering
{
    public enum GraphicsFormat { R16_SFloat, R16G16_SFloat, R16G16B16A16_SFloat }
    public enum FormatUsage { LoadStore, Sample, Linear }
}
namespace UnityEngine
{
    public class Object
    {
        public string name;
        public bool Destroyed;
        public static bool operator ==(Object a, Object b)
        {
            var an = ReferenceEquals(a, null) || a.Destroyed;
            var bn = ReferenceEquals(b, null) || b.Destroyed;
            return an || bn ? an == bn : ReferenceEquals(a, b);
        }
        public static bool operator !=(Object a, Object b) => !(a == b);
        public static implicit operator bool(Object value) => value != null;
        public override bool Equals(object other) => ReferenceEquals(this, other);
        public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    }
    public class ScriptableObject : Object
    {
        public static T CreateInstance<T>() where T : ScriptableObject => (T)Activator.CreateInstance(typeof(T));
    }
    public class Texture : Object { }
    public class Texture2DArray : Texture { }
    public enum TextureWrapMode { Clamp }
    public enum FilterMode { Bilinear }
    public enum CubemapFace { Unknown }
    public struct RenderTextureDescriptor
    {
        public int width, height;
        public Experimental.Rendering.GraphicsFormat graphicsFormat;
        public RenderTextureDescriptor(int width, int height, Experimental.Rendering.GraphicsFormat format, int depth)
        { this.width = width; this.height = height; graphicsFormat = format; }
    }
    public class RenderTexture : Texture
    {
        public int width, height, volumeDepth, antiAliasing, anisoLevel;
        public bool enableRandomWrite, useMipMap, Initialized;
        public float Marker;
        public object depthBuffer => null;
        public TextureWrapMode wrapMode;
        public FilterMode filterMode;
        public TextureDimension dimension;
        bool _created;
        public RenderTexture(RenderTextureDescriptor descriptor) { width = descriptor.width; height = descriptor.height; }
        public static bool FailCreation;
        public bool Create() { if (Destroyed) throw new Exception("created destroyed texture"); _created = !FailCreation; Initialized = false; return _created; }
        public void Release() { _created = false; Initialized = false; }
        public bool IsCreated() => _created && !Destroyed;
    }
    public class ComputeBuffer
    {
        public Array Data;
        public bool Disposed;
        public void SetData(Array data) { if (Disposed) throw new Exception("uploaded to disposed buffer"); Data = (Array)data.Clone(); }
        public void Dispose() { Disposed = true; }
    }
    public class ComputeShader : Object { public int FindKernel(string name) => 0; }
    public class Shader : Object
    {
        static readonly Dictionary<string, int> Ids = new Dictionary<string, int>();
        public static int PropertyToID(string name) { if (!Ids.TryGetValue(name, out var id)) Ids[name] = id = Ids.Count; return id; }
        public static string PropertyName(int id) => Ids.Single(pair => pair.Value == id).Key;
        public static void SetGlobalTexture(int id, Texture texture) { }
        public static void SetGlobalFloat(int id, float value) { }
        public static Shader Find(string name) => new Shader { name = name };
        public static void EnableKeyword(string keyword) { }
        public static void DisableKeyword(string keyword) { }
    }
    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public float this[int index] { get => index == 0 ? x : y; set { if (index == 0) x = value; else y = value; } }
    }
    public struct Vector3
    {
        public float x, y, z;
        public float this[int index] { get => index == 0 ? x : index == 1 ? y : z; set { if (index == 0) x = value; else if (index == 1) y = value; else z = value; } }
    }
    public struct Vector4 { public static implicit operator Vector4(Vector3 value) => default; }
    public struct Color { public Color(float r, float g, float b, float a) { } public static Color black => default; }
    public class Transform { public Vector3 position; }
    public class GameObject : Object { public void SetActive(bool active) { } }
    public class Camera : Object { public static void SetupCurrent(Camera camera) { } }
    public class Material : Object
    {
        public Material(Shader shader) { }
        public void SetFloat(int id, float value) { }
    }
    public struct Matrix4x4 { public static Matrix4x4 identity => default; }
    public struct Rect
    {
        public float x, y, width, height;
        public Vector2 min => new Vector2(x, y);
        public Vector2 max => new Vector2(x + width, y + height);
        public bool Contains(Vector2 point) => point.x >= x && point.x <= x + width && point.y >= y && point.y <= y + height;
    }
    public static class Graphics
    {
        public static int ExecuteCount;
        public static void ExecuteCommandBuffer(CommandBuffer commands) { if (commands.Released) throw new Exception("executed released commands"); ExecuteCount++; }
    }
    public static class Mathf
    {
        public static int FloorToInt(float value) => (int)Math.Floor(value);
        public static int Max(int a, int b) => Math.Max(a, b);
        public static float Min(float a, float b) => Math.Min(a, b);
        public static float Max(float a, float b) => Math.Max(a, b);
        public static int Clamp(int value, int min, int max) => Math.Min(Math.Max(value, min), max);
        public static float Exp(float value) => (float)Math.Exp(value);
        public static float Pow(float a, float b) => (float)Math.Pow(a, b);
        public static float Log(float value) => (float)Math.Log(value);
    }
    public static class Debug
    {
        public static readonly List<string> Errors = new List<string>();
        public static void Assert(bool condition) { if (!condition) throw new Exception("assertion failed"); }
        public static void Assert(bool condition, string message, object context = null) { if (!condition) throw new Exception(message); }
        public static void LogError(string message, object context = null) => Errors.Add(message);
    }
    public class TooltipAttribute : Attribute { public TooltipAttribute(string text) { } }
    public enum RuntimeInitializeLoadType { SubsystemRegistration }
    public class RuntimeInitializeOnLoadMethodAttribute : Attribute { public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType value) { } }
}
namespace UnityEngine.Rendering
{
    public enum TextureDimension { Tex2DArray, Tex2D }
    public enum MeshTopology { Triangles }
    public sealed class Dispatch
    {
        public RenderTexture Source, Target;
        public float Dt;
    }
    public class CommandBuffer
    {
        public string name;
        public bool Released;
        public int ReleaseCount;
        public static int TotalDispatches;
        public List<Dispatch> Dispatches = new List<Dispatch>();
        public Dictionary<int, Texture> Textures = new Dictionary<int, Texture>();
        public Dictionary<int, float> Floats = new Dictionary<int, float>();
        public void DispatchCompute(ComputeShader shader, int kernel, int x, int y, int z)
        {
            TotalDispatches++;
            if (shader.name == "ShapeCombine")
            {
                foreach (var texture in Textures.Values.OfType<RenderTexture>())
                    if (!texture.IsCreated()) throw new Exception("shape combine used a released texture");
                var result = (RenderTexture)Textures[Shader.PropertyToID("_LD_TexArray_AnimatedWaves_Compute")];
                result.Initialized = true;
                return;
            }
            var source = (RenderTexture)Textures.Single(pair => Shader.PropertyName(pair.Key).EndsWith("_Source")).Value;
            var target = (RenderTexture)Textures[Shader.PropertyToID("_LD_TexArray_Target")];
            if (!source.IsCreated() || !target.IsCreated()) throw new Exception("dispatch used a released texture");
            if (!source.Initialized) throw new Exception("dispatch read uninitialized history");
            if (ReferenceEquals(source, target)) throw new Exception("dispatch aliased source and target");
            var dt = Floats[LodDataMgrPersistent.sp_SimDeltaTime];
            Dispatches.Add(new Dispatch { Source = source, Target = target, Dt = dt });
            target.Marker = source.Marker + (dt > 0 ? 1 : 0);
            target.Initialized = true;
        }
        public void SetGlobalFloat(int id, float value) { }
        public void SetGlobalInt(int id, int value) { }
        RenderTexture _renderTarget;
        public void SetRenderTarget(RenderTexture target, object depth, int mip, CubemapFace face, int slice) => SetRenderTarget(target);
        public void SetRenderTarget(RenderTexture target, int mip, CubemapFace face, int slice) => SetRenderTarget(target);
        public void SetRenderTarget(RenderTexture target)
        { if (!target.IsCreated()) throw new Exception("selected a released render target"); _renderTarget = target; }
        public void ClearRenderTarget(bool depth, bool color, Color clear)
        { if (_renderTarget == null) throw new Exception("clear without target"); _renderTarget.Initialized = true; _renderTarget.Marker = 0; }
        public void DrawProcedural(Matrix4x4 matrix, Material material, int pass, MeshTopology topology, int vertices)
        { if (_renderTarget == null || !_renderTarget.IsCreated()) throw new Exception("drew to invalid target"); _renderTarget.Initialized = true; }
        public void Clear() { if (Released) throw new Exception("cleared released commands"); Dispatches.Clear(); Textures.Clear(); Floats.Clear(); }
        public void Release() { if (Released) throw new Exception("command buffer double release"); Released = true; ReleaseCount++; }
    }
}
namespace Crest
{
    public class SimSettingsBase : ScriptableObject { }
    public class SimSettingsFlow : SimSettingsBase { }
    public class SimSettingsWave : SimSettingsBase
    {
        public float _simulationFrequency = 60, _damping = .05f, _gravityMultiplier = 1, _courantNumber = .7f;
        public float _attenuationInShallows = 1, _horizDisplace = 3, _displaceClamp = .3f, _minGridSize, _maxGridSize;
    }
    public class SimSettingsFoam : SimSettingsBase
    {
        public float _simulationFrequency = 30, _foamFadeRate = .8f, _waveFoamStrength = 1, _waveFoamCoverage = .55f;
        public float _shorelineFoamMaxDepth = .65f, _shorelineFoamStrength = 2;
        public bool _prewarm = true;
        public int FilterWaves;
        public UnityEngine.Experimental.Rendering.GraphicsFormat _renderTextureGraphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16_SFloat;
    }
    public partial class OceanRenderer
    {
        public static OceanRenderer Instance = new OceanRenderer();
        public static int FrameCount;
        public int LodDataResolution = 32, CurrentLodCount = 2;
#if LOD_RESOURCE_CHECKS
        public float DeltaTime => TimeProvider.DeltaTime;
#else
        public float DeltaTime;
#endif
        public float Gravity = 9.81f;
        public bool _hasTeleportedThisFrame;
        public Transform Root = new Transform();
        public LodTransform _lodTransform = new LodTransform();
        public ComputeBuffer _bufCascadeDataSrc = new ComputeBuffer(), _bufCascadeDataTgt = new ComputeBuffer();
        public static int sp_CrestCascadeDataSource = Shader.PropertyToID("_CrestCascadeDataSource");
        public static int sp_cascadeData = Shader.PropertyToID("_CrestCascadeData");
        public static int sp_oceanCenterPosWorld = Shader.PropertyToID("_OceanCenterPosWorld");
        public SimSettingsWave _simSettingsDynamicWaves;
        public SimSettingsFoam _simSettingsFoam;
        public SimSettingsFlow _simSettingsFlow;
        public LodDataMgrDynWaves _lodDataDynWaves;
        public LodDataMgrFoam _lodDataFoam;
        public LodDataMgrFlow _lodDataFlow;
    }
    public partial class LodTransform
    {
        public int ScaleDifferencePow2;
        public class RenderData
        {
            public float _texelWidth = 1, _textureRes = 32, _maxWavelength = 4;
            public Vector3 _posSnapped;
            public Rect RectXZ => new Rect { width = 32, height = 32 };
            public RenderData Validate(int offset, string context) => this;
        }
        public BufferedData<RenderData>[] _renderData = Enumerable.Range(0, 2).Select(_ => new BufferedData<RenderData>(2, () => new RenderData())).ToArray();
        public void SetViewProjectionMatrices(int index, CommandBuffer commands) { }
        public float MaxWavelength(int index) => 4;
    }
#if !LOD_RESOURCE_CHECKS
    public static class BuildCommandBufferBase { public static int _lastUpdateFrame; }
#endif
    public interface ILodDataInput
    {
        bool Enabled { get; }
        bool IgnoreTransitionWeight { get; }
        float Wavelength { get; }
        void Draw(LodDataMgr manager, CommandBuffer commands, float weight, int transition, int slice);
    }
    public static class RegisterLodDataInputBase
    {
        public static Dictionary<int, ILodDataInput> GetRegistrar(Type type) => new Dictionary<int, ILodDataInput>();
    }
    public interface IPropertyWrapper
    {
        void SetFloat(int id, float value);
        void SetInt(int id, int value);
        void SetVector(int id, Vector4 value);
        void SetTexture(int id, Texture value);
    }
    public class PropertyWrapperCompute : IPropertyWrapper
    {
        CommandBuffer _commands;
        public void Initialise(CommandBuffer commands, ComputeShader shader, int kernel) { _commands = commands; _commands.Textures.Clear(); }
        public void SetFloat(int id, float value) => _commands.Floats[id] = value;
        public void SetInt(int id, int value) { }
        public void SetVector(int id, Vector4 value) { }
        public void SetTexture(int id, Texture value) => _commands.Textures[id] = value;
        public void SetBuffer(int id, ComputeBuffer value) { }
    }
    public class PropertyWrapperMaterial : IPropertyWrapper
    {
        public Material material;
        public PropertyWrapperMaterial(Material material) { this.material = material; }
        public void SetFloat(int id, float value) { }
        public void SetInt(int id, int value) { }
        public void SetVector(int id, Vector4 value) { }
        public void SetTexture(int id, Texture value) { }
        public void SetBuffer(int id, ComputeBuffer value) { }
    }
    public static class Helpers
    {
        public static readonly int s_DataGraphicsFormatUsage = 0;
        public static UnityEngine.Experimental.Rendering.GraphicsFormat GetCompatibleTextureFormat(
            UnityEngine.Experimental.Rendering.GraphicsFormat format, int usage, string name, bool randomWrite) => format;
        public static void Destroy(UnityEngine.Object value) { if (!ReferenceEquals(value, null)) value.Destroyed = true; }
        public static void Swap<T>(ref T a, ref T b) { var temporary = a; a = b; b = temporary; }
        public static bool IsMotionVectorsEnabled() => false;
    }
    public static class TextureArrayHelpers
    {
        public static Texture2DArray BlackTextureArray = new Texture2DArray();
        public static void ClearToBlack(RenderTexture target)
        {
            if (!target.IsCreated())
            {
#if LOD_RESOURCE_CHECKS
                // Failed native allocation may already cause an initialization clear
                // attempt. The recovery gate is tested before simulation submission.
                return;
#else
                throw new Exception("cleared a released texture");
#endif
            }
            target.Initialized = true; target.Marker = 0;
        }
    }
    public static class ComputeShaderHelpers { public static ComputeShader LoadShader(string name) => new ComputeShader { name = name }; }
#if !LOD_RESOURCE_CHECKS
    public static class LodDataMgrAnimWaves { public static void Bind(IPropertyWrapper properties) { } }
#endif
    public static class LodDataMgrSeaFloorDepth { public static void Bind(IPropertyWrapper properties) { } }
    public static class SphereWaterInteraction { public static void SubmitDraws(LodDataMgrDynWaves manager, int lod, CommandBuffer commands) { } }
}
