// Execute production FFTCompute.cs with managed resource and command substitutes.
// No Unity assembly binding, shader execution, or GPU resource validation.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Crest;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

static class Program
{
    static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    static readonly MethodInfo GenerateMethod = typeof(FFTCompute).GetMethod("GenerateDisplacementsInternal", Private);

    static int Main()
    {
        var tests = new List<(string name, Action run)>
        {
            ("first generation at time -1 still initializes all spectrum data", FirstNegativeTime),
            ("valid same-time calls reuse one generated texture without dispatching", SharedCache),
            ("explicit spectrum updates bypass a same-time cache", ForcedUpdate),
            ("Release then same-time generation recreates and initializes resources", ReleasedSameTime),
            ("Release then a later time initializes the newly allocated spectrum", ReleasedLaterTime),
            ("CleanUpAll then a paused-time request recreates a valid wave texture", CleanupSameTime),
            ("changed conditions at a paused time rebuild the spectrum", ChangedConditions),
        };
        foreach (var field in new[] { "_spectrumInit", "_spectrumHeight", "_spectrumDisplaceX", "_spectrumDisplaceZ",
                                      "_tempFFT1", "_tempFFT2", "_tempFFT3", "_waveBuffers" })
        {
            var captured = field;
            tests.Add(("lost render texture invalidates same-time cache: " + captured, () => LostTexture(captured)));
        }
        foreach (var field in new[] { "_texButterfly", "_texSpectrumControls" })
        {
            var captured = field;
            tests.Add(("destroyed control texture invalidates same-time cache: " + captured, () => DestroyedControl(captured)));
        }
        var failures = 0;
        foreach (var test in tests)
        {
            FFTCompute.CleanUpAll();
            typeof(FFTCompute).GetMethod("InitStatics", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
            try { test.run(); Console.WriteLine("PASS: " + test.name); }
            catch (Exception exception)
            {
                failures++;
                Console.WriteLine("FAIL: " + test.name + ": " + (exception.InnerException ?? exception).Message);
            }
        }
        FFTCompute.CleanUpAll();
        Console.WriteLine($"FFT lifecycle: {tests.Count - failures}/{tests.Count} passed; {failures} failed.");
        Console.WriteLine("Production C# with managed resource/command substitutes; no Unity or GPU validation.");
        return failures == 0 ? 0 : 1;
    }

    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static FFTCompute Generator() => new FFTCompute(16, -1f, 10f, 0.2f, 0f, new OceanWaveSpectrum());
    static RenderTexture Generate(FFTCompute generator, CommandBuffer command, float time, bool update = false)
        => (RenderTexture)GenerateMethod.Invoke(generator, new object[] { command, time, update });
    static RenderTexture RequireGenerated(FFTCompute generator, CommandBuffer command, float time, bool update = false)
    {
        var result = Generate(generator, command, time, update);
        Check(result != null && result.IsCreated(), "missing or released wave output");
        Check(command.Dispatches.Count == 4, "expected spectrum initialization, spectrum update and two FFT dispatches; got " + command.Dispatches.Count);
        Check(command.Dispatches[0].kernel == "SpectrumInitalize", "new spectrum was not initialized before FFT");
        return result;
    }

    static void FirstNegativeTime() => RequireGenerated(Generator(), new CommandBuffer(), -1f);
    static void SharedCache()
    {
        var generator = Generator(); var command = new CommandBuffer();
        var first = RequireGenerated(generator, command, 0f); command.Clear();
        var second = Generate(generator, command, 0f);
        Check(ReferenceEquals(first, second) && command.Dispatches.Count == 0, "valid cache was not reused");
    }
    static void ForcedUpdate()
    {
        var generator = Generator(); var command = new CommandBuffer();
        RequireGenerated(generator, command, 0f); command.Clear();
        RequireGenerated(generator, command, 0f, true);
    }
    static void ReleasedSameTime()
    {
        var generator = Generator(); var command = new CommandBuffer();
        RequireGenerated(generator, command, 4f); generator.Release(); command.Clear();
        RequireGenerated(generator, command, 4f);
    }
    static void ReleasedLaterTime()
    {
        var generator = Generator(); var command = new CommandBuffer();
        RequireGenerated(generator, command, 4f); generator.Release(); command.Clear();
        RequireGenerated(generator, command, 5f);
    }
    static void CleanupSameTime()
    {
        var command = new CommandBuffer(); var spectrum = new OceanWaveSpectrum();
        FFTCompute.GenerateDisplacements(command, 16, -1f, .2f, 0f, 10f, 2f, spectrum, false);
        FFTCompute.CleanUpAll(); command.Clear();
        var result = FFTCompute.GenerateDisplacements(command, 16, -1f, .2f, 0f, 10f, 2f, spectrum, false);
        Check(result != null && result.IsCreated() && command.Dispatches.Count == 4,
            "global cleanup reused a released cached output at the paused timestamp");
    }
    static void ChangedConditions()
    {
        var command = new CommandBuffer(); var spectrum = new OceanWaveSpectrum();
        FFTCompute.GenerateDisplacements(command, 16, -1f, .2f, 0f, 10f, 2f, spectrum, false);
        FFTCompute.OnGenerationDataUpdated(16, -1f, .2f, 0f, 10f, spectrum, .2f, 0f, 11f, spectrum);
        command.Clear();
        var result = FFTCompute.GenerateDisplacements(command, 16, -1f, .2f, 0f, 11f, 2f, spectrum, false);
        Check(result != null && command.Dispatches.Count == 4, "updated conditions did not bypass cached time");
        Check(command.FloatParams[FFTCompute.ShaderIDs.s_WindSpeed] == 11f, "new wind speed was not bound to initialization");
    }
    static void LostTexture(string field)
    {
        var generator = Generator(); var command = new CommandBuffer();
        RequireGenerated(generator, command, 4f);
        ((RenderTexture)typeof(FFTCompute).GetField(field, Private).GetValue(generator)).Release(); command.Clear();
        RequireGenerated(generator, command, 4f);
        var target = (RenderTexture)typeof(FFTCompute).GetField(field, Private).GetValue(generator);
        Check(target != null && target.IsCreated(), "lost texture was not recreated: " + field);
    }
    static void DestroyedControl(string field)
    {
        var generator = Generator(); var command = new CommandBuffer();
        RequireGenerated(generator, command, 4f);
        var previous = (Object)typeof(FFTCompute).GetField(field, Private).GetValue(generator);
        Helpers.Destroy(previous); command.Clear();
        RequireGenerated(generator, command, 4f);
        Check((Object)typeof(FFTCompute).GetField(field, Private).GetValue(generator) != null, "destroyed control was not recreated");
    }
}

namespace UnityEditor { }
namespace UnityEngine.Experimental.Rendering
{
    public enum GraphicsFormat { R32G32B32A32_SFloat, R32G32_SFloat, R16G16B16A16_SFloat }
}
namespace UnityEngine
{
    public class Object
    {
        static int NextId;
        public readonly int Id = ++NextId;
        public bool Destroyed;
        public string name;
        public static bool operator ==(Object a, Object b)
        {
            var aNull = ReferenceEquals(a, null) || a.Destroyed;
            var bNull = ReferenceEquals(b, null) || b.Destroyed;
            return aNull || bNull ? aNull == bNull : ReferenceEquals(a, b);
        }
        public static bool operator !=(Object a, Object b) => !(a == b);
        public override bool Equals(object obj) => ReferenceEquals(this, obj);
        public override int GetHashCode() => Id;
    }
    public class Texture : Object { }
    public enum TextureFormat { RGBAFloat, RFloat }
    public enum TextureWrapMode { Repeat }
    public enum FilterMode { Bilinear }
    public class Texture2D : Texture
    {
        public Texture2D(int width, int height, TextureFormat format, bool mips, bool linear) { }
        public void SetPixels(Color[] colors) { if (Destroyed) throw new Exception("write to destroyed controls"); }
        public void Apply() { }
    }
    public struct RenderTextureDescriptor
    {
        public int width, height, depthBufferBits, volumeDepth, msaaSamples;
        public bool enableRandomWrite;
        public UnityEngine.Rendering.TextureDimension dimension;
        public UnityEngine.Experimental.Rendering.GraphicsFormat graphicsFormat;
    }
    public class RenderTexture : Texture
    {
        bool Created;
        public RenderTextureDescriptor descriptor;
        public TextureWrapMode wrapMode;
        public FilterMode filterMode;
        public int antiAliasing, anisoLevel, volumeDepth;
        public bool useMipMap, enableRandomWrite;
        public UnityEngine.Rendering.TextureDimension dimension;
        public RenderTexture(RenderTextureDescriptor value) { descriptor = value; }
        public RenderTexture(int width, int height, int depth, UnityEngine.Experimental.Rendering.GraphicsFormat format)
        { descriptor = new RenderTextureDescriptor { width = width, height = height, depthBufferBits = depth, graphicsFormat = format }; }
        public bool Create() { if (Destroyed) throw new Exception("create a destroyed render texture"); Created = true; return true; }
        public void Release() { Created = false; }
        public bool IsCreated() => !Destroyed && Created;
    }
    public class ComputeShader : Object
    {
        readonly Dictionary<string, int> Kernels = new Dictionary<string, int>();
        public int FindKernel(string name)
        { if (!Kernels.TryGetValue(name, out var index)) Kernels[name] = index = Kernels.Count; return index; }
        public string KernelName(int index) => Kernels.FirstOrDefault(pair => pair.Value == index).Key ?? index.ToString();
    }
    public static class Resources
    {
        static readonly Dictionary<string, ComputeShader> Shaders = new Dictionary<string, ComputeShader>();
        public static T Load<T>(string path) where T : Object
        {
            if (!Shaders.TryGetValue(path, out var shader)) Shaders[path] = shader = new ComputeShader { name = path };
            return (T)(Object)shader;
        }
    }
    public static class Shader
    {
        static readonly Dictionary<string, int> Ids = new Dictionary<string, int>();
        public static int PropertyToID(string name)
        { if (!Ids.TryGetValue(name, out var index)) Ids[name] = index = Ids.Count; return index; }
    }
    public struct Color
    {
        public Color(float r, float g, float b, float a) { }
        public static Color white => new Color(1, 1, 1, 1);
        public static Color operator *(float x, Color color) => color;
    }
    public struct Vector2 { public Vector2(float x, float y) { } }
    public struct Rect { public Rect(float x, float y, float w, float h) { } }
    public static class GUI { public static void DrawTexture(Rect rect, Texture texture) { } }
    public static class Debug { public static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); } }
    public static class Mathf
    {
        public const float PI = (float)Math.PI;
        public static int RoundToInt(float x) => (int)Math.Round(x);
        public static float Log(float x, float y) => (float)Math.Log(x, y);
        public static float Cos(float x) => (float)Math.Cos(x);
        public static float Sin(float x) => (float)Math.Sin(x);
        public static float Pow(float x, float y) => (float)Math.Pow(x, y);
        public static float Abs(float x) => Math.Abs(x);
        public static int NextPowerOfTwo(int x) { int value = 1; while (value < x) value <<= 1; return value; }
    }
    public enum RuntimeInitializeLoadType { SubsystemRegistration }
    public class RuntimeInitializeOnLoadMethodAttribute : Attribute { public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType type) { } }
}
namespace UnityEngine.Rendering
{
    public enum TextureDimension { Tex2DArray }
    public class CommandBuffer
    {
        public readonly List<(string shader, string kernel)> Dispatches = new List<(string, string)>();
        public readonly Dictionary<int, float> FloatParams = new Dictionary<int, float>();
        public void Clear() { Dispatches.Clear(); FloatParams.Clear(); }
        public void SetComputeIntParam(ComputeShader shader, int id, int value) { }
        public void SetComputeFloatParam(ComputeShader shader, int id, float value) => FloatParams[id] = value;
        public void SetComputeVectorParam(ComputeShader shader, int id, Vector2 value) { }
        public void SetComputeTextureParam(ComputeShader shader, int kernel, int id, Texture texture)
        {
            if (texture == null || texture is RenderTexture target && !target.IsCreated())
                throw new Exception("bound a missing or released texture");
        }
        public void DispatchCompute(ComputeShader shader, int kernel, int x, int y, int z) => Dispatches.Add((shader.name, shader.KernelName(kernel)));
    }
}
namespace Crest
{
    public class OceanRenderer { public static OceanRenderer Instance = new OceanRenderer(); public float Gravity = 9.81f; }
    public class OceanWaveSpectrum : Object
    {
        public const int NUM_OCTAVES = 14;
        public bool[] _powerDisabled = new bool[NUM_OCTAVES];
        public float[] _powerLog = new float[NUM_OCTAVES];
        public float _multiplier = 1f, _gravityScale = 1f, _chop = 1f;
    }
    public static class Helpers
    {
        public static readonly int s_DataGraphicsFormatUsage = 0;
        public static void Destroy(Object value) { if (!ReferenceEquals(value, null)) value.Destroyed = true; }
        public static void SafeCreateRenderTexture(ref RenderTexture texture, RenderTextureDescriptor descriptor)
        { if (texture == null) texture = new RenderTexture(descriptor); else texture.descriptor = descriptor; }
        public static UnityEngine.Experimental.Rendering.GraphicsFormat GetCompatibleTextureFormat(
            UnityEngine.Experimental.Rendering.GraphicsFormat format, int usage, string label, bool randomWrite) => format;
    }
    public static class Hashy
    {
        public static int CreateHash() => 17;
        public static void AddInt(int value, ref int hash) => hash = unchecked(hash * 31 + value);
        public static void AddFloat(float value, ref int hash) => AddInt(value.GetHashCode(), ref hash);
        public static void AddObject(Object value, ref int hash) => AddInt(value.GetHashCode(), ref hash);
    }
    public static class OceanDebugGUI { public static void DrawTextureArray(Texture texture, int x, float scale, float y) { } }
}
