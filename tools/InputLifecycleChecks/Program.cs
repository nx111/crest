// Complete production lifecycle classes and unchanged derived members are compiled
// by check_input_lifecycle.py. Unity objects, scene traversal and mesh generation
// below are controlled substitutes, not Unity/GPU or real geometry validation.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Crest;
using UnityEngine;
using UnityEngine.Rendering;

static class Program
{
    static readonly BindingFlags Fields = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Near(float actual, float expected, string message)
        => Check(Math.Abs(actual - expected) < 0.00005f, $"{message}: expected {expected}, got {actual}");
    internal static void Invoke(object target, Type type, string method, params object[] args)
    {
        try { type.GetMethod(method, Fields).Invoke(target, args); }
        catch (TargetInvocationException error) { throw error.InnerException; }
    }

    static int Main()
    {
        var tests = new (string name, Action run)[]
        {
            ("FFT resources are released before the last Gerstner is destroyed", FftFirst),
            ("swell resources are released before the last FFT is destroyed", GerstnerFirst),
            ("FFT subclasses retain the shared family until its last user", SubclassFamily),
            ("global materials are reused on enable and released on destroy", GlobalMaterial),
            ("generated spline material and mesh are released on destroy", SplineResources),
            ("borrowed renderer material and mesh survive wave destruction", BorrowedResources),
            ("adding a spline never mutates a borrowed renderer mesh", SplineOwnsMesh),
            ("adding an invalid spline never submits a borrowed mesh as spline geometry", InvalidSplineWithRenderer),
            ("deferred spline generation tolerates no mesh and retains an existing owned mesh", DeferredSplineGeneration),
            ("invalid spline releases its mesh and removes old wave inputs", InvalidSpline),
            ("removing a spline releases only its mesh and resumes global waves", RemovedSpline),
            ("global queue sentinel sorts before a local blend at queue zero", GlobalQueue),
            ("negative local queues remain after globals and before queue zero", NegativeQueues),
            ("extreme user queues cannot wrap across the global reservation", ExtremeQueues),
            ("ordinary queue and sibling ordering is unchanged", OrdinaryQueues),
            ("an ocean-child Gerstner receives exactly one phase compensation", ChildGerstner),
            ("an external Gerstner still receives phase compensation", ExternalGerstner),
            ("null and duplicate override receivers do not repeat shifts", OverrideReceivers),
            ("each subsequent origin shift notifies the receiver again", SuccessiveShifts),
        };
        int failed = 0;
        foreach (var test in tests)
        {
            Reset();
            try { test.run(); Console.WriteLine("PASS: " + test.name); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL: " + test.name + ": " + error.Message); }
        }
        Console.WriteLine($"Input lifecycle: {tests.Length - failed}/{tests.Length} passed; {failed} failed.");
        Console.WriteLine("Managed substitutes for Unity resources, scene lookup and spline generation; no Unity/GPU execution.");
        return failed == 0 ? 0 : 1;
    }

    static void Reset()
    {
        UnityEngine.Object.All.Clear();
        FFTCompute.CleanupCount = 0;
        ShapeGerstner.ResetSpectrum();
        typeof(ShapeWaves).GetField("s_InstanceCount", Fields).SetValue(null, 0);
        typeof(ShapeWaves).GetField("s_WindSpectrum", Fields).SetValue(null, null);
        if (typeof(ShapeWaves).GetField("s_SharedResourceUsers", Fields)?.GetValue(null) is IDictionary users) users.Clear();
        RegisterLodDataInputBase.GetRegistrar(typeof(LodDataMgrAnimWaves)).Clear();
        OceanChunkRenderer.DisplacementReporters.Clear();
        OceanRenderer.Instance = new GameObject("Ocean").AddComponent<OceanRenderer>();
    }

    static T Wave<T>(bool spline = false, Material material = null, Mesh mesh = null) where T : ProbeWaves, new()
    {
        var go = new GameObject(typeof(T).Name);
        if (spline) go.AddComponent<Crest.Spline.Spline>();
        if (material != null)
        {
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
        }
        var wave = go.AddComponent<T>();
        wave.Begin();
        return wave;
    }

    static void FftFirst()
    {
        var fft = Wave<ShapeFFT>();
        var gerstner = Wave<ShapeGerstner>();
        var wind = fft.Wind;
        var swell = ShapeGerstner.Swell;
        fft.End();
        Check(FFTCompute.CleanupCount == 1, "FFT cleanup was skipped while Gerstner remained");
        Check(!wind.Destroyed && !swell.Destroyed, "another wave family's live spectrum was destroyed");
        gerstner.End();
        Check(wind.Destroyed && swell.Destroyed, "last spectra were not released");
        Check(FFTCompute.CleanupCount == 1, "FFT cleanup repeated after its last owner");
    }

    static void GerstnerFirst()
    {
        var fft = Wave<ShapeFFT>();
        var gerstner = Wave<ShapeGerstner>();
        var swell = ShapeGerstner.Swell;
        gerstner.End();
        Check(swell.Destroyed, "swell cleanup was skipped while FFT remained");
        Check(FFTCompute.CleanupCount == 0, "FFT was released while still used");
        fft.End();
        Check(FFTCompute.CleanupCount == 1, "last FFT did not release its family");
    }

    static void SubclassFamily()
    {
        var fft = Wave<ShapeFFT>();
        var child = Wave<DerivedFFT>();
        var other = Wave<ShapeGerstner>();
        fft.End();
        Check(FFTCompute.CleanupCount == 0, "subclass still needed the FFT resources");
        child.End();
        Check(FFTCompute.CleanupCount == 1, "last FFT subclass was not counted with its resource family");
        other.End();
    }

    static void GlobalMaterial()
    {
        var wave = Wave<ShapeFFT>();
        var material = wave.Material;
        wave.Toggle();
        Check(ReferenceEquals(material, wave.Material) && !material.Destroyed, "enable recreated or destroyed a reusable material");
        wave.End();
        Check(material.Destroyed && material.DestroyCount == 1, "owned global material was not released exactly once");
    }

    static void SplineResources()
    {
        var wave = Wave<ShapeGerstner>(spline: true);
        var mesh = wave.Mesh;
        var material = wave.Material;
        wave.End();
        Check(mesh.Destroyed && material.Destroyed, "owned spline mesh/material leaked on destroy");
    }

    static void BorrowedResources()
    {
        var mesh = new Mesh();
        var material = new Material(Shader.Find("External wave material"));
        var wave = Wave<ShapeFFT>(material: material, mesh: mesh);
        Check(ReferenceEquals(mesh, wave.Mesh), "renderer mesh was not selected");
        wave.End();
        Check(!mesh.Destroyed && !material.Destroyed, "externally owned renderer assets were destroyed");
    }

    static void SplineOwnsMesh()
    {
        var mesh = new Mesh();
        var material = new Material(Shader.Find("External wave material"));
        var wave = Wave<ShapeGerstner>(material: material, mesh: mesh);
        wave.gameObject.AddComponent<Crest.Spline.Spline>();
        wave.OnSplineChange();
        wave.CrestUpdate(new CommandBuffer());
        Check(mesh.GenerationWrites == 0 && !ReferenceEquals(wave.Mesh, mesh), "spline generation wrote into the renderer's shared mesh");
        var owned = wave.Mesh;
        wave.End();
        Check(owned.Destroyed && !mesh.Destroyed && !material.Destroyed, "spline/renderer resource ownership was mixed");
    }

    static void InvalidSpline()
    {
        var wave = Wave<ShapeFFT>(spline: true);
        var mesh = wave.Mesh;
        wave.GetComponent<Crest.Spline.Spline>().Valid = false;
        wave.OnSplineChange();
        wave.CrestUpdate(new CommandBuffer());
        Check(mesh.Destroyed && wave.Mesh == null, "invalid spline retained its generated mesh");
        Check(RegisterLodDataInputBase.GetRegistrar(typeof(LodDataMgrAnimWaves)).Count == 0, "invalid spline kept a registered wave draw");
        wave.End();
    }

    static void InvalidSplineWithRenderer()
    {
        var mesh = new Mesh();
        var material = new Material(Shader.Find("External wave material"));
        var wave = Wave<ShapeFFT>(material: material, mesh: mesh);
        wave.gameObject.AddComponent<Crest.Spline.Spline>().Valid = false;
        wave.OnSplineChange();
        wave.CrestUpdate(new CommandBuffer());
        Check(wave.Mesh == null && RegisterLodDataInputBase.GetRegistrar(typeof(LodDataMgrAnimWaves)).Count == 0,
            "invalid spline submitted the previous renderer mesh as generated geometry");
        Check(!mesh.Destroyed && mesh.GenerationWrites == 0 && !material.Destroyed, "invalid spline changed borrowed assets");
        wave.End();
        Check(!mesh.Destroyed && !material.Destroyed, "destroying the failed spline destroyed borrowed assets");
    }

    static void DeferredSplineGeneration()
    {
        var go = new GameObject("Deferred spline");
        var spline = go.AddComponent<Crest.Spline.Spline>();
        // Actual GenerateMeshFromSpline returns true before mesh creation while
        // UNITY_EDITOR Undo.isProcessing. Model just that early-return contract.
        spline.DeferGeneration = true;
        var wave = go.AddComponent<ShapeFFT>();
        wave.Begin();
        Check(wave.Mesh == null && RegisterLodDataInputBase.GetRegistrar(typeof(LodDataMgrAnimWaves)).Count == 0,
            "a deferred generation with no mesh registered an input");
        spline.DeferGeneration = false;
        wave.OnSplineChange();
        wave.CrestUpdate(new CommandBuffer());
        var owned = wave.Mesh;
        Check(owned != null, "spline did not recover after generation resumed");
        spline.DeferGeneration = true;
        wave.OnSplineChange();
        wave.CrestUpdate(new CommandBuffer());
        Check(ReferenceEquals(owned, wave.Mesh) && !owned.Destroyed && owned.GenerationWrites == 1,
            "deferred update discarded an already generated owned mesh");
        wave.End();
        Check(owned.Destroyed, "deferred spline retained owned mesh after destroy");
    }

    static void RemovedSpline()
    {
        var wave = Wave<ShapeFFT>(spline: true);
        var mesh = wave.Mesh;
        var oldMaterial = wave.Material;
        wave.gameObject.Components.Remove(wave.GetComponent<Crest.Spline.Spline>());
        wave.OnSplineChange();
        wave.CrestUpdate(new CommandBuffer());
        Check(mesh.Destroyed && wave.Mesh == null, "removed spline retained its generated mesh");
        var globalMaterial = wave.Material;
        Check(!ReferenceEquals(oldMaterial, globalMaterial), "removed spline did not switch to global input");
        wave.End();
        Check(oldMaterial.Destroyed && globalMaterial.Destroyed, "one cached material mode leaked on destroy");
    }

    static string[] Queue(params (string name, int queue, int sibling)[] inputs)
    {
        var registrar = RegisterLodDataInputBase.GetRegistrar(typeof(LodDataMgrAnimWaves));
        registrar.Clear();
        foreach (var input in inputs) RegisterLodDataInput<LodDataMgrAnimWaves>.RegisterInput(new TestInput(input.name), input.queue, input.sibling);
        return registrar.Select(item => ((TestInput)item.Value).Name).ToArray();
    }
    static void Order(string[] actual, params string[] expected)
        => Check(actual.SequenceEqual(expected), "draw order: " + string.Join(",", actual));
    static void GlobalQueue() => Order(Queue(("local", 0, 0), ("global", int.MinValue, 99)), "global", "local");
    static void NegativeQueues() => Order(Queue(("local", 0, 0), ("negative", -1, 0), ("global", int.MinValue, 999)), "global", "negative", "local");
    static void OrdinaryQueues() => Order(Queue(("last", 5000, 0), ("second", 2000, 12), ("first", 2000, 3), ("zero", 0, 0)), "zero", "first", "second", "last");
    static void ExtremeQueues() => Order(Queue(("high", int.MaxValue, int.MaxValue), ("low", int.MinValue + 1, -1),
                                              ("middle", 3000, 0), ("global", int.MinValue, int.MaxValue)), "global", "low", "middle", "high");

    static ShapeGerstnerBatched Gerstner(bool child)
    {
        var go = new GameObject("Legacy Gerstner");
        if (child) go.transform.parent = OceanRenderer.Instance.transform;
        var result = go.AddComponent<ShapeGerstnerBatched>();
        result._phases = new[] { 0.2f };
        result._wavelengths = new[] { 3000f };
        result._angleDegs = new[] { 0f };
        return result;
    }
    static ShiftingOrigin Origin() => new GameObject("Origin").AddComponent<ShiftingOrigin>();
    static void Shift(ShiftingOrigin origin, float x) => Invoke(origin, typeof(ShiftingOrigin), "MoveOriginOcean", new Vector3(x, 0, 0));
    static void CheckPhase(ShapeGerstnerBatched wave, float totalShift)
    {
        var k = 2f * Mathf.PI / wave._wavelengths[0];
        Near(Mathf.Cos(k * 2f + wave._phases[0]), Mathf.Cos(k * (2f + totalShift) + 0.2f), "phase continuity after origin shift");
    }
    static void ChildGerstner()
    {
        var wave = Gerstner(true);
        var receiver = new GameObject("Receiver").AddComponent<OtherReceiver>();
        receiver.transform.parent = OceanRenderer.Instance.transform;
        Shift(Origin(), 2048f);
        CheckPhase(wave, 2048f);
        Check(receiver.Calls == 1, "ordinary receiver was notified more than once");
    }
    static void ExternalGerstner() { var wave = Gerstner(false); Shift(Origin(), 2048f); CheckPhase(wave, 2048f); }
    static void OverrideReceivers()
    {
        var child = Gerstner(true);
        var external = Gerstner(false);
        var origin = Origin();
        typeof(ShiftingOrigin).GetField("_overrideGerstnerList", Fields).SetValue(origin, new[] { child, child, null, external, external });
        Shift(origin, 2048f);
        CheckPhase(child, 2048f);
        CheckPhase(external, 2048f);
    }
    static void SuccessiveShifts()
    {
        var wave = Gerstner(true);
        var origin = Origin();
        Shift(origin, 2048f);
        Shift(origin, -4096f);
        CheckPhase(wave, -2048f);
    }
}

namespace UnityEngine
{
    public class Object
    {
        public static readonly List<Object> All = new List<Object>();
        public string name;
        public bool Destroyed;
        public int DestroyCount;
        public Object() { All.Add(this); }
        public static implicit operator bool(Object value) => !ReferenceEquals(value, null) && !value.Destroyed;
        public static bool operator ==(Object a, Object b) => ReferenceEquals(a, b) || (!a && !b);
        public static bool operator !=(Object a, Object b) => !(a == b);
        public override bool Equals(object other) => ReferenceEquals(this, other);
        public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    }
    public class Component : Object
    {
        public GameObject gameObject;
        public Transform transform => gameObject.transform;
        public bool TryGetComponent<T>(out T value) where T : class => gameObject.TryGetComponent(out value);
        public T GetComponent<T>() where T : class { TryGetComponent(out T value); return value; }
        public T[] GetComponentsInChildren<T>() => gameObject.ComponentsInChildren<T>().ToArray();
    }
    public class MonoBehaviour : Component { public bool enabled = true; }
    public class GameObject : Object
    {
        public readonly List<Component> Components = new List<Component>();
        public readonly List<GameObject> Children = new List<GameObject>();
        public Transform transform;
        public GameObject(string label) { name = label; transform = new Transform { gameObject = this }; Components.Add(transform); }
        public T AddComponent<T>() where T : Component { var component = (T)Activator.CreateInstance(typeof(T)); component.gameObject = this; Components.Add(component); return component; }
        public bool TryGetComponent<T>(out T value) where T : class { value = Components.OfType<T>().FirstOrDefault(); return value != null; }
        public IEnumerable<T> ComponentsInChildren<T>() => Components.OfType<T>().Concat(Children.SelectMany(child => child.ComponentsInChildren<T>()));
    }
    public class Transform : Component
    {
        Transform _parent;
        public Transform parent { get => _parent; set { _parent?.gameObject.Children.Remove(gameObject); _parent = value; value?.gameObject.Children.Add(gameObject); } }
        public Vector3 position, lossyScale = Vector3.one;
        public Quaternion rotation;
        public Matrix4x4 localToWorldMatrix => Matrix4x4.identity;
        public int Sibling;
        public int GetSiblingIndex() => Sibling;
    }
    public class ScriptableObject : Object { public static T CreateInstance<T>() where T : ScriptableObject, new() => new T(); }
    public class Material : Object
    {
        public Shader shader;
        public int renderQueue;
        public Material(Shader shader) { this.shader = shader; }
        public void SetFloat(int id, float value) { }
    }
    public class Shader : Object
    {
        public static Shader Find(string name) => new Shader { name = name };
        public static int PropertyToID(string name) => name.GetHashCode();
        public static void SetGlobalVector(int id, Vector3 value) { }
        public static void SetGlobalInteger(int id, int value) { }
        public static void EnableKeyword(string keyword) { }
        public static void DisableKeyword(string keyword) { }
    }
    public class Mesh : Object { public int GenerationWrites; }
    public class MeshFilter : Component { public Mesh sharedMesh; }
    public class Renderer : Component
    {
        public Material sharedMaterial;
        public bool enabled = true, forceRenderingOff;
        public Bounds bounds;
        public void GetSharedMaterials(List<Material> materials) { materials.Clear(); materials.Add(sharedMaterial); }
    }
    public class MeshRenderer : Renderer { }
    public class TrailRenderer : Renderer { }
    public class LineRenderer : Renderer { }
    public class Rigidbody : Component { public float sleepThreshold; }
    public enum ParticleSystemSimulationSpace { Local, World }
    public class ParticleSystem : Component
    {
        public struct Particle { public Vector3 position; }
        public struct MainModule { public int maxParticles; public ParticleSystemSimulationSpace simulationSpace; }
        public MainModule main;
        public bool isPaused, isPlaying;
        public void Pause() { }
        public void Play() { }
        public int GetParticles(Particle[] particles) => 0;
        public void SetParticles(Particle[] particles, int count) { }
    }
    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static Vector2 one => new Vector2(1, 1);
    }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => default;
        public static Vector3 one => new Vector3(1, 1, 1);
        public float sqrMagnitude => x * x + y * y + z * z;
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator *(Vector3 a, float b) => new Vector3(a.x * b, a.y * b, a.z * b);
        public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static bool operator ==(Vector3 a, Vector3 b) => a.x == b.x && a.y == b.y && a.z == b.z;
        public static bool operator !=(Vector3 a, Vector3 b) => !(a == b);
        public override bool Equals(object other) => other is Vector3 value && this == value;
        public override int GetHashCode() => HashCode.Combine(x, y, z);
    }
    public struct Quaternion { }
    public struct Matrix4x4 { public static Matrix4x4 identity => default; }
    public struct Bounds { public Vector3 min, max; }
    public struct Rect { public static Rect MinMaxRect(float a, float b, float c, float d) => default; public bool Overlaps(Rect other, bool inverse) => false; }
    public struct Color { public Color(float r, float g, float b, float a = 1) { } }
    public enum MeshTopology { Triangles }
    public static class GeometryUtility { public static Bounds CalculateBounds(Vector3[] points, Matrix4x4 matrix) => default; }
    public static class Mathf
    {
        public const float PI = MathF.PI, Deg2Rad = MathF.PI / 180;
        public static float Sin(float value) => MathF.Sin(value);
        public static float Cos(float value) => MathF.Cos(value);
        public static int Min(int a, int b) => Math.Min(a, b);
        public static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);
        public static float Abs(float value) => MathF.Abs(value);
        public static float Floor(float value) => MathF.Floor(value);
        public static float Repeat(float value, float period) => Math.Clamp(value - Floor(value / period) * period, 0, period);
    }
    public static class Resources { public static T GetBuiltinResource<T>(string path) where T : new() => new T(); }
    public static class Time { public static int frameCount; }
    public static class Debug { public static void Log(string value) { } public static void AssertFormat(bool condition, Object context, string message) { if (!condition) throw new Exception(message); } }
    public static class Gizmos { public static Color color; public static void DrawWireMesh(Mesh mesh, params object[] arguments) { } }
    public enum RuntimeInitializeLoadType { SubsystemRegistration }
    public class RuntimeInitializeOnLoadMethodAttribute : Attribute { public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType type) { } }
    public class SerializeField : Attribute { }
    public class HideInInspector : Attribute { }
    public class TooltipAttribute : Attribute { public TooltipAttribute(string text) { } }
    public class AddComponentMenuAttribute : Attribute { public AddComponentMenuAttribute(string text) { } }
    public class HelpURLAttribute : Attribute { public HelpURLAttribute(string text) { } }
    public class HeaderAttribute : Attribute { public HeaderAttribute(string text) { } }
    public class SpaceAttribute : Attribute { public SpaceAttribute(float value) { } }
    public class MinAttribute : Attribute { public MinAttribute(float value) { } }
    public class DelayedAttribute : Attribute { }
}
namespace UnityEngine.Rendering
{
    public class CommandBuffer
    {
        public void SetGlobalInt(int id, int value) { }
        public void SetGlobalFloat(int id, float value) { }
        public void SetGlobalVector(int id, Vector3 value) { }
        public void DrawMesh(Mesh mesh, Matrix4x4 matrix, Material material, int submeshIndex = 0, int shaderPass = -1) { }
        public void DrawRenderer(Renderer renderer, Material material, int submeshIndex = 0, int shaderPass = -1) { }
        public void DrawProcedural(Matrix4x4 matrix, Material material, int pass, MeshTopology topology, int count) { }
    }
}
namespace UnityEngine.Experimental.Rendering { }
namespace UnityEditor { }
namespace Crest
{
    public class CustomMonoBehaviour : MonoBehaviour { }
    public class ExecuteDuringEditModeAttribute : Attribute { public enum Include { None } public ExecuteDuringEditModeAttribute(Include include = Include.None) { } }
    public class EmbeddedAttribute : Attribute { }
    public class RangeAttribute : Attribute { public RangeAttribute(float min, float max, float power = 1) { } }
    public class DecoratedFieldAttribute : Attribute { }
    public class OnChangeAttribute : Attribute { public OnChangeAttribute(string name) { } }
    public class PredicatedAttribute : Attribute { public PredicatedAttribute(params object[] args) { } }
    public static class Helpers
    {
        public static readonly object WaitForEndOfFrame = new object();
        public static int DuplicateComparison(int a, int b) => a == b ? 1 : a.CompareTo(b);
        public static void Destroy(UnityEngine.Object value) { if (ReferenceEquals(value, null)) return; value.DestroyCount++; value.Destroyed = true; }
        public static T[] FindObjectsByType<T>() => UnityEngine.Object.All.OfType<T>().Where(value => !(value is UnityEngine.Object obj) || !obj.Destroyed).ToArray();
    }
    public class OceanWaveSpectrum : ScriptableObject { }
    public class OceanRenderer : Component
    {
        public static OceanRenderer Instance;
        public static int FrameCount;
        public float WindDirectionAngle, WindSpeedKPH = 20, Gravity = 9.81f;
        public LodDataMgrAnimWaves _lodDataAnimWaves = new LodDataMgrAnimWaves();
        public LodTransform _lodTransform = new LodTransform();
    }
    public class LodTransform { public void SetOrigin(Vector3 origin) { } }
    public class LodDataMgr { }
    public class LodDataMgrAnimWaves : LodDataMgr
    {
        public class SettingsData { public float WaveResolutionMultiplier = 1, MaximumAttenuationDepth = 100; }
        public SettingsData Settings = new SettingsData();
        public interface IShapeUpdatable { void CrestUpdate(CommandBuffer buffer); }
        public static void RegisterUpdatable(IShapeUpdatable shape) { }
        public static void DeregisterUpdatable(IShapeUpdatable shape) { }
    }
    public interface IReportsDisplacement { bool ReportDisplacement(ref Rect bounds, ref float horizontal, ref float vertical); }
    public static class OceanChunkRenderer { public static readonly List<IReportsDisplacement> DisplacementReporters = new List<IReportsDisplacement>(); }
    public class SampleHeightHelper
    {
        public void Init(Vector3 point, float i_minLength, bool allowMultipleCallsPerFrame, UnityEngine.Object context) { }
        public bool Sample(out Vector3 displacement, out Vector3 normal, out Vector3 velocity) { displacement = normal = velocity = default; return true; }
    }
    public abstract class ProbeWaves : ShapeWaves
    {
        DebugFields _debug = new DebugFields();
        protected override DebugFields DebugSettings => _debug;
        protected override int MinimumResolution => 8;
        protected override int MaximumResolution => 64;
        protected override void ReportMaxDisplacement() { }
        public override float MinWavelength(int cascade) => 1;
        public Material Material => _matGenerateWaves;
        public Mesh Mesh => _meshForDrawingWaves;
        public OceanWaveSpectrum Wind => WindSpectrum;
        public void Begin() { Program.Invoke(this, typeof(ShapeWaves), "Awake"); base.OnEnable(); _firstCascade = _lastCascade = 0; CrestUpdate(new CommandBuffer()); }
        public void Toggle() { base.OnDisable(); base.OnEnable(); CrestUpdate(new CommandBuffer()); }
        public void End() { base.OnDisable(); Program.Invoke(this, typeof(ShapeWaves), "OnDestroy"); }
    }
    public class DerivedFFT : ShapeFFT { }
    public static class FFTCompute { public static int CleanupCount; public static void CleanUpAll() { CleanupCount++; } }
    public partial class ShapeGerstner
    {
        static OceanWaveSpectrum s_SwellSpectrum;
        public static OceanWaveSpectrum Swell => s_SwellSpectrum ??= new OceanWaveSpectrum();
        public static void ResetSpectrum() { s_SwellSpectrum = null; }
    }
    public partial class ShapeGerstnerBatched
    {
        public float _windDirectionAngle;
        public float[] _phases, _angleDegs, _wavelengths;
    }
    public class OtherReceiver : CustomMonoBehaviour, IShiftingOrigin { public int Calls; public void SetOrigin(Vector3 origin) { Calls++; } }
    public class TestInput : ILodDataInput
    {
        public readonly string Name;
        public TestInput(string name) { Name = name; }
        public float Wavelength => 0;
        public bool Enabled => true;
        public bool IgnoreTransitionWeight => false;
        public void Draw(LodDataMgr data, CommandBuffer buffer, float weight, int transition, int lod) { }
    }
    public static class ShapeGerstnerSplineHandling
    {
        public static bool GenerateMeshFromSpline<T>(Spline.Spline spline, Transform transform, int subdivisions, float radius, Vector2 defaults,
            ref Mesh mesh, out float minHeight, out float maxHeight, ref Vector3[] points)
        {
            minHeight = maxHeight = 0;
            if (!spline.Valid) return false;
            if (spline.DeferGeneration) return true;
            mesh ??= new Mesh();
            mesh.GenerationWrites++;
            points = new Vector3[3];
            return true;
        }
    }
}
namespace Crest.Spline
{
    public class Spline : CustomMonoBehaviour { public bool Valid = true, DeferGeneration; public float Radius = 20; public int Subdivisions = 1; }
    public interface ISplinePointCustomData { }
    public interface ISplinePointCustomDataSetup { bool AttachDataToSplinePoint(GameObject point); }
    public interface IReceiveSplineChangeMessages { void OnSplineChange(); }
    public class SplinePointDataWaves : CustomMonoBehaviour, ISplinePointCustomData { }
    public class SplinePointDataNone : CustomMonoBehaviour, ISplinePointCustomData { }
}
namespace Crest.Internal
{
    public static class Constants { public const string HELP_URL_BASE_USER = "", HELP_URL_RP = "", MENU_PREFIX_SCRIPTS = "", MENU_SCRIPTS = ""; }
}
