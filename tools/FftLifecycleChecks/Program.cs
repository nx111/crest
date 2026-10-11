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
            ("exchanging wind speed and turbulence cannot alias FFT conditions", DistinctConditions),
            ("identical public requests share one generator and dispatch", SharedConditions),
            ("different spectrum objects keep independent FFT data", DistinctSpectra),
            ("unchanged-condition notification preserves the existing output", UnchangedConditions),
            ("merging conditions preserves live outputs and final resource ownership", MergedConditions),
            ("a later shape cannot overwrite an earlier shape's output this frame", SameFrameConsumers),
            ("an unused previous-frame generator can be repurposed at a paused time", ReuseIdleGenerator),
            ("repeated merging and diverging conditions reuse two texture sets", RepeatedConditionChanges),
            ("spare selection never rewrites any generator already used this frame", ProtectAllFrameConsumers),
            ("spare selection preserves incompatible resolution and loop caches", PreserveIncompatibleSpare),
            ("global cleanup clears generator ownership as well as all textures", CleanupOwnership),
            ("subsystem reset releases live generators before clearing the cache", SubsystemReset),
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
            OceanRenderer.FrameCount = 0;
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

    static RenderTexture GenerateConditions(CommandBuffer command, OceanWaveSpectrum spectrum, float speed = 10f, float turbulence = .2f)
        => FFTCompute.GenerateDisplacements(command, 16, -1f, turbulence, 0f, speed, 2f, spectrum, false);

    static void ChangeConditions(OceanWaveSpectrum spectrum, float oldSpeed, float newSpeed)
        => FFTCompute.OnGenerationDataUpdated(16, -1f, .2f, 0f, oldSpeed, spectrum, .2f, 0f, newSpeed, spectrum);

    static Object[] OwnedTextures(RenderTexture output)
    {
        var generators = (System.Collections.IDictionary)typeof(FFTCompute)
            .GetField("_generators", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        foreach (FFTCompute generator in generators.Values)
        {
            if (!ReferenceEquals(typeof(FFTCompute).GetField("_waveBuffers", Private).GetValue(generator), output)) continue;
            var textures = typeof(FFTCompute).GetFields(Private)
                .Where(field => typeof(Texture).IsAssignableFrom(field.FieldType))
                .Select(field => (Object)field.GetValue(generator)).ToArray();
            Check(textures.Length == 10 && textures.All(texture => texture != null), "expected eight RTs and two control textures");
            return textures;
        }
        throw new Exception("output has no cached resource owner");
    }

    static void DistinctConditions()
    {
        // Use the actual production Hashy.cs, including when testing an old ref.
        var firstHash = Hashy.CreateHash(); var secondHash = Hashy.CreateHash();
        Hashy.AddFloat(.2f, ref firstHash); Hashy.AddFloat(.75f, ref firstHash);
        Hashy.AddFloat(.75f, ref secondHash); Hashy.AddFloat(.2f, ref secondHash);
        Check(firstHash == secondHash, "negative-control precondition: legacy XOR keys must collide");

        var command = new CommandBuffer(); var spectrum = new OceanWaveSpectrum();
        var first = GenerateConditions(command, spectrum, .75f, .2f);
        var second = GenerateConditions(command, spectrum, .2f, .75f);
        Check(!ReferenceEquals(first, second), "different valid wind conditions reused the same output");
        Check(FFTCompute.GeneratorCount == 2 && command.Dispatches.Count == 8, "second condition was never initialized");
        Check(command.FloatParams[FFTCompute.ShaderIDs.s_WindSpeed] == .2f
            && command.FloatParams[FFTCompute.ShaderIDs.s_Turbulence] == .75f, "incorrect second spectrum parameters");
    }

    static void SharedConditions()
    {
        var command = new CommandBuffer(); var spectrum = new OceanWaveSpectrum();
        var first = GenerateConditions(command, spectrum);
        var second = GenerateConditions(command, spectrum);
        Check(ReferenceEquals(first, second) && FFTCompute.GeneratorCount == 1 && command.Dispatches.Count == 4,
            "identical consumers should share one generated result");
    }

    static void DistinctSpectra()
    {
        var command = new CommandBuffer();
        var first = GenerateConditions(command, new OceanWaveSpectrum());
        var second = GenerateConditions(command, new OceanWaveSpectrum());
        Check(!ReferenceEquals(first, second) && FFTCompute.GeneratorCount == 2 && command.Dispatches.Count == 8,
            "separate spectrum assets shared an output");
    }

    static void UnchangedConditions()
    {
        var command = new CommandBuffer(); var spectrum = new OceanWaveSpectrum();
        var first = GenerateConditions(command, spectrum); command.Clear();
        ChangeConditions(spectrum, 10f, 10f);
        var second = GenerateConditions(command, spectrum);
        Check(ReferenceEquals(first, second) && command.Dispatches.Count == 0, "a no-op notification evicted the live generator");
    }

    static void MergedConditions()
    {
        var command = new CommandBuffer(); var spectrum = new OceanWaveSpectrum();
        var first = GenerateConditions(command, spectrum, 10f);
        var firstTextures = OwnedTextures(first);
        var second = GenerateConditions(command, spectrum, 11f);
        var secondTextures = OwnedTextures(second);
        ChangeConditions(spectrum, 10f, 11f);
        Check(firstTextures.Concat(secondTextures).All(texture => !texture.Destroyed),
            "condition merge destroyed a texture that a recorded command can still reference");
        FFTCompute.CleanUpAll();
        Check(firstTextures.Concat(secondTextures).All(texture => texture.Destroyed),
            "merging into an existing generator orphaned old textures at final cleanup");
    }

    static void SameFrameConsumers()
    {
        var command = new CommandBuffer(); var spectrum = new OceanWaveSpectrum();
        var first = GenerateConditions(command, spectrum, 10f);
        ChangeConditions(spectrum, 10f, 11f);
        var second = GenerateConditions(command, spectrum, 11f);
        Check(!ReferenceEquals(first, second), "second shape rewrote the first shape's texture before animated-wave draws");
        Check(command.Outputs.Count == 2 && ReferenceEquals(command.Outputs[0], first)
            && ReferenceEquals(command.Outputs[1], second), "recorded FFT writes did not retain independent outputs");
        command.Clear();
        Check(ReferenceEquals(first, GenerateConditions(command, spectrum, 10f)) && command.Dispatches.Count == 0,
            "another consumer of the original conditions lost its already generated texture");
    }

    static void ReuseIdleGenerator()
    {
        var command = new CommandBuffer(); var spectrum = new OceanWaveSpectrum();
        var first = GenerateConditions(command, spectrum, 10f); command.Clear();
        OceanRenderer.FrameCount++;
        ChangeConditions(spectrum, 10f, 11f);
        var second = GenerateConditions(command, spectrum, 11f);
        Check(ReferenceEquals(first, second) && FFTCompute.GeneratorCount == 1, "an idle generator was needlessly allocated again");
        Check(command.Dispatches.Count == 4 && command.FloatParams[FFTCompute.ShaderIDs.s_WindSpeed] == 11f,
            "reused storage did not initialize the new spectrum at paused time");
    }

    static void CleanupOwnership()
    {
        var output = GenerateConditions(new CommandBuffer(), new OceanWaveSpectrum());
        var textures = OwnedTextures(output);
        FFTCompute.CleanUpAll();
        Check(textures.All(texture => texture.Destroyed), "cleanup left owned textures alive");
        Check(FFTCompute.GeneratorCount == 0, "cleanup retained obsolete generators and spectrum references");
    }

    static void RepeatedConditionChanges()
    {
        var command = new CommandBuffer(); var spectrum = new OceanWaveSpectrum();
        var oldA = 10f; var oldB = 11f;
        GenerateConditions(command, spectrum, oldA);
        GenerateConditions(command, spectrum, oldB);
        for (var frame = 1; frame <= 120; frame++)
        {
            OceanRenderer.FrameCount = frame;
            command.Clear();
            var newA = oldB; var newB = oldB + 1f;
            ChangeConditions(spectrum, oldA, newA);
            var first = GenerateConditions(command, spectrum, newA);
            ChangeConditions(spectrum, oldB, newB);
            var second = GenerateConditions(command, spectrum, newB);
            Check(!ReferenceEquals(first, second), "merging/diverging shapes overwrote each other at frame " + frame);
            Check(FFTCompute.GeneratorCount == 2, "two active conditions retained " + FFTCompute.GeneratorCount + " texture sets at frame " + frame);
            oldA = newA; oldB = newB;
        }
    }

    static void ProtectAllFrameConsumers()
    {
        var command = new CommandBuffer(); var spectrum = new OceanWaveSpectrum();
        var first = GenerateConditions(command, spectrum, 10f);
        var second = GenerateConditions(command, spectrum, 11f);
        ChangeConditions(spectrum, 11f, 12f);
        var third = GenerateConditions(command, spectrum, 12f);
        Check(!ReferenceEquals(first, third) && !ReferenceEquals(second, third) && FFTCompute.GeneratorCount == 3,
            "spare search reused another live frame consumer");
    }

    static void PreserveIncompatibleSpare()
    {
        var command = new CommandBuffer(); var spectrum = new OceanWaveSpectrum();
        var differentSize = FFTCompute.GenerateDisplacements(command, 32, -1f, .2f, 0f, 10f, 2f, spectrum, false);
        var differentPeriod = FFTCompute.GenerateDisplacements(command, 16, 8f, .2f, 0f, 10f, 2f, spectrum, false);
        OceanRenderer.FrameCount++;
        GenerateConditions(command, spectrum, 10f);
        ChangeConditions(spectrum, 10f, 11f);
        var changed = GenerateConditions(command, spectrum, 11f);
        Check(!ReferenceEquals(changed, differentSize) && !ReferenceEquals(changed, differentPeriod), "retuned incompatible texture storage");
        command.Clear();
        Check(ReferenceEquals(differentSize, FFTCompute.GenerateDisplacements(command, 32, -1f, .2f, 0f, 10f, 2f, spectrum, false))
            && ReferenceEquals(differentPeriod, FFTCompute.GenerateDisplacements(command, 16, 8f, .2f, 0f, 10f, 2f, spectrum, false)),
            "spare search discarded incompatible cached conditions");
        Check(command.Dispatches.Count == 0, "incompatible caches were needlessly regenerated");
    }

    static void SubsystemReset()
    {
        var output = GenerateConditions(new CommandBuffer(), new OceanWaveSpectrum());
        var textures = OwnedTextures(output);
        typeof(FFTCompute).GetMethod("InitStatics", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
        Check(textures.All(texture => texture.Destroyed), "subsystem reset discarded live resource owners without cleanup");
        Check(FFTCompute.GeneratorCount == 0, "subsystem reset retained cached generators");
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
        public readonly List<Texture> Outputs = new List<Texture>();
        public void Clear() { Dispatches.Clear(); FloatParams.Clear(); Outputs.Clear(); }
        public void SetComputeIntParam(ComputeShader shader, int id, int value) { }
        public void SetComputeFloatParam(ComputeShader shader, int id, float value) => FloatParams[id] = value;
        public void SetComputeVectorParam(ComputeShader shader, int id, Vector2 value) { }
        public void SetComputeTextureParam(ComputeShader shader, int kernel, int id, Texture texture)
        {
            if (texture == null || texture is RenderTexture target && !target.IsCreated())
                throw new Exception("bound a missing or released texture");
            if (id == FFTCompute.ShaderIDs.s_Output) Outputs.Add(texture);
        }
        public void DispatchCompute(ComputeShader shader, int kernel, int x, int y, int z) => Dispatches.Add((shader.name, shader.KernelName(kernel)));
    }
}
namespace Crest
{
    public class OceanRenderer { public static OceanRenderer Instance = new OceanRenderer(); public static int FrameCount; public float Gravity = 9.81f; }
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
    public static class OceanDebugGUI { public static void DrawTextureArray(Texture texture, int x, float scale, float y) { } }
}
