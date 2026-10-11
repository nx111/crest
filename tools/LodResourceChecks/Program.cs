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
        var tests = new List<(string name, Action run)>();
        foreach (var kind in new[] { "Foam", "DynamicWaves" })
            foreach (var slot in new[] { "current", "previous", "temporary" })
            {
                var k = kind; var s = slot;
                tests.Add(($"{k} lost {s} allocation rebuilds the complete history", () => LostTexture(k, s)));
            }
        foreach (var slot in new[] { "current", "waves", "combine" })
        {
            var s = slot;
            tests.Add(($"animated waves lost {s} allocation is included in recovery", () => LostTexture("AnimatedWaves", s)));
        }
        tests.AddRange(new (string, Action)[]
        {
            ("empty flow redraws its cleared state after losing the texture", LostFlow),
            ("unsuccessful allocation stops after one recovery attempt", FailedAllocation),
            ("an initialization failure with no managers cannot continue the update", EarlyFailure),
            ("a newly added invalid simulation stops before simulation dispatch", NewManagerFailure),
            ("the first publication initializes every cascade and instance history", FirstHistory),
            ("later frame publication preserves the actual previous frame", LaterHistory),
            ("ordinary disable and enable preserve valid history", RetainedEnable),
            ("rebuild releases the previous command buffer exactly once", BuilderRelease),
            ("repeated cleanup does not retain or release a command buffer twice", RepeatedCleanup),
            ("a paused negative runtime clock survives the whole resource rebuild", PreservedClock),
            ("rebuild resets its guard after an initialization exception", ClockGuardFinally),
            ("rebuild restores an already active guard after an exception", NestedGuardFinally),
            ("fresh enable retains its default and inspector clock behavior", NormalClockInitialization),
            ("rebuild with an empty provider stack installs a fallback", EmptyClockStack),
        });
        int failed = 0;
        foreach (var test in tests)
        {
            RenderTexture.FailCreation = false; Debug.Errors.Clear();
            Graphics.ExecuteCount = CommandBuffer.TotalDispatches = 0;
            try { test.run(); Console.WriteLine("PASS: " + test.name); }
            catch (Exception error) { failed++; Console.WriteLine("FAIL: " + test.name + ": " + error.Message); }
            finally
            {
                RenderTexture.FailCreation = false;
                OceanRenderer.LastConstructed?.DisposeForChecks();
            }
        }
        Console.WriteLine($"LOD resources: {tests.Count - failed}/{tests.Count} passed; {failed} failed.");
        Console.WriteLine("Actual managers/builder and extracted Ocean lifecycle methods. Scene/native APIs are managed substitutes; no Unity or GPU validation.");
        return failed == 0 ? 0 : 1;
    }

    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static T Field<T>(object owner, Type type, string field)
        => (T)type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner);
    static RenderTexture Target(LodDataMgr manager, string slot)
    {
        if (slot == "current") return manager.DataTexture;
        if (slot == "previous") return manager.GetDataTexture(1);
        var owner = manager is LodDataMgrPersistent ? typeof(LodDataMgrPersistent) : typeof(LodDataMgrAnimWaves);
        return Field<RenderTexture>(manager, owner, slot == "temporary" ? "_sources" : slot == "waves" ? "_waveBuffers" : "_combineBuffer");
    }
    static void LostTexture(string kind, string slot)
    {
        var ocean = OceanRenderer.CreateForChecks(kind);
        ocean.Tick();
        var oldManager = ocean.Managers[0];
        Target(oldManager, slot).Release();
        var clock = new TestClock(-4f, kind == "Foam" ? .1f : .05f);
        ocean.PushTimeProvider(clock);
        ocean.Tick();
        Check(ocean.Initializations == 2, "lost allocation did not cause exactly one rebuild");
        Check(!ReferenceEquals(oldManager, ocean.Managers[0]), "old manager retained lost resource state");
        Check(Target(ocean.Managers[0], slot).IsCreated(), "rebuilt resource was not created");
        Check(ocean.enabled && ocean.IsInitialized, "successful recovery disabled the ocean");
        Check(Debug.Errors.Count == 0, "healthy recovery reported failure");
    }
    static void LostFlow()
    {
        var ocean = OceanRenderer.CreateForChecks("Flow");
        ocean.Tick();
        Check(ocean.Managers[0].DataTexture.Initialized, "initial empty flow was not cleared");
        ocean.Managers[0].DataTexture.Release();
        ocean.Tick();
        Check(ocean.Initializations == 2, "empty-flow cached clear state suppressed recovery");
        Check(ocean.Managers[0].DataTexture.IsCreated() && ocean.Managers[0].DataTexture.Initialized,
            "new flow texture was not recreated and cleared");
    }
    static void FailedAllocation()
    {
        var ocean = OceanRenderer.CreateForChecks("Foam");
        ocean.Tick(); ocean.Managers[0].DataTexture.Release();
        var dispatches = CommandBuffer.TotalDispatches;
        RenderTexture.FailCreation = true;
        ocean.CleanResourcesOnDisable = true;
        ocean.Tick(); ocean.Tick();
        Check(ocean.Initializations == 2, "failed allocation retried more than once");
        Check(!ocean.enabled, "failed allocation left the ocean enabled");
        Check(CommandBuffer.TotalDispatches == dispatches, "failed recovery submitted simulation work");
        Check(Debug.Errors.Count == 1 && Debug.Errors[0].Contains("Foam"), "expected one specific allocation failure");
    }
    static void EarlyFailure()
    {
        var ocean = OceanRenderer.CreateForChecks("Flow");
        ocean.Tick();
        ocean.SettingsHash++; // Also exercises the pre-existing settings rebuild path.
        ocean.FailEarlyInitialization = true;
        ocean.CleanResourcesOnDisable = true;
        var submitted = Graphics.ExecuteCount;
        ocean.Tick();
        Check(!ocean.enabled && !ocean.IsInitialized, "incomplete initialization remained active");
        Check(ocean.Managers.Count == 0 && Graphics.ExecuteCount == submitted, "empty manager list was accepted as successful recovery");
        Check(Debug.Errors.Count == 1, "initialization failure was not reported once");
    }
    static void NewManagerFailure()
    {
        var ocean = OceanRenderer.CreateForChecks("Flow");
        ocean.Tick();
        ocean.AddInvalidManagerOnUpdate = true;
        var submitted = Graphics.ExecuteCount;
        ocean.Tick();
        Check(!ocean.enabled && Graphics.ExecuteCount == submitted, "new invalid simulation reached dispatch");
        Check(ocean.Initializations == 1 && Debug.Errors.Count == 1, "new allocation entered a retry loop");
    }
    static void FirstHistory()
    {
        var ocean = OceanRenderer.CreateForChecks("Flow", buffers: 3);
        ocean.Tick();
        for (int frame = 1; frame < ocean.BufferSize; frame++)
        {
            var history = ocean.Cascades.Previous(frame);
            for (int lod = 0; lod <= ocean.CurrentLodCount; lod++)
            {
                Check(history[lod].Equals(ocean.Cascades.Current[lod]), "cascade history not fully initialized including sentinel");
                Check(history[lod]._textureRes > 0 && history[lod]._texelWidth > 0, "source cascade has a zero denominator");
            }
            Check(ocean.Instances.Previous(frame).SequenceEqual(ocean.Instances.Current), "instance history not initialized");
        }
        Check(((OceanRenderer.CascadeParams[])ocean._bufCascadeDataSrc.Data).SequenceEqual(ocean.Cascades.Current), "first uploaded source did not contain initialized cascades");
        Check(((OceanRenderer.PerCascadeInstanceData[])ocean._bufPerCascadeInstanceDataSource.Data).SequenceEqual(ocean.Instances.Current), "first uploaded instance source was stale");
    }
    static void LaterHistory()
    {
        var ocean = OceanRenderer.CreateForChecks("Flow");
        ocean.Tick(); var previous = (OceanRenderer.CascadeParams[])ocean.Cascades.Current.Clone();
        var instancePrevious = (OceanRenderer.PerCascadeInstanceData[])ocean.Instances.Current.Clone();
        ocean.Scale = 2f; ocean.Tick();
        Check(ocean.Cascades.Previous(1).SequenceEqual(previous), "later frame overwrote valid cascade history");
        Check(ocean.Instances.Previous(1).SequenceEqual(instancePrevious), "later frame overwrote valid instance history");
        Check(ocean.Cascades.Current[0]._scale == 2f && ocean.Initializations == 1, "normal update unexpectedly rebuilt");
    }
    static void RetainedEnable()
    {
        var ocean = OceanRenderer.CreateForChecks("Flow");
        ocean.Tick(); var previous = (OceanRenderer.CascadeParams[])ocean.Cascades.Current.Clone();
        ocean.enabled = false; ocean.enabled = true; ocean.EnableForChecks();
        ocean.Scale = 2f; ocean.Tick();
        Check(ocean.Initializations == 1, "retained enable rebuilt resources");
        Check(ocean.Cascades.Previous(1).SequenceEqual(previous), "retained enable reseeded already valid history");
    }
    static void BuilderRelease()
    {
        var ocean = OceanRenderer.CreateForChecks("Flow"); ocean.Tick();
        var buffer = Field<CommandBuffer>(ocean.Builder, typeof(BuildCommandBuffer), "_buf");
        ocean.Rebuild();
        Check(buffer.Released && buffer.ReleaseCount == 1, "rebuild did not release the old native command buffer once");
    }
    static void RepeatedCleanup()
    {
        var ocean = OceanRenderer.CreateForChecks("Flow"); ocean.Tick();
        var buffer = Field<CommandBuffer>(ocean.Builder, typeof(BuildCommandBuffer), "_buf");
        ocean.DisposeForChecks(); ocean.DisposeForChecks();
        Check(buffer.ReleaseCount == 1 && ocean.Builder == null, "cleanup retained or double-released the builder");
    }
    static void PreservedClock()
    {
        var ocean = OceanRenderer.CreateForChecks("Foam");
        var first = new TestClock(12f, .2f); var paused = new TestClock(-19f, 0f);
        ocean.PushTimeProvider(first); ocean.PushTimeProvider(paused);
        var stack = ocean._timeProviderStack; var providers = stack.ToArray();
        // Invoke the actual rebuild independently of loss detection, so the old
        // code demonstrates clock replacement rather than failing on a lost RT.
        ocean.Rebuild();
        Check(ocean.Initializations == 2, "clock test did not enter resource recovery");
        Check(ReferenceEquals(stack, ocean._timeProviderStack) && providers.SequenceEqual(stack), "rebuild replaced the provider list or its order");
        Check(ocean.InitializationTimes.Last() == -19f && ocean.InitializationDeltas.Last() == 0f,
            "initialization used a temporary default clock");
        Check(ocean.CurrentTime == -19f && ocean.DeltaTime == 0f, "rebuild changed the paused clock");
        Check(!ocean.IsRebuilding, "rebuild guard remained set");
    }
    static void ClockGuardFinally() => ExceptionGuard(false);
    static void NestedGuardFinally() => ExceptionGuard(true);
    static void ExceptionGuard(bool prior)
    {
        var ocean = OceanRenderer.CreateForChecks("Flow");
        ocean.SetRebuildingForChecks(prior); ocean.ThrowDuringInitialization = true;
        var threw = false;
        try { ocean.Rebuild(); } catch (InvalidOperationException) { threw = true; }
        Check(threw && ocean.IsRebuilding == prior, "exception did not restore the original rebuild guard");
    }
    static void NormalClockInitialization()
    {
        var ocean = new OceanRenderer();
        var runtime = new TestClock(-10, 0); var inspector = new TestClock(20, .1f);
        ocean._timeProviderStack.Add(runtime); ocean.InspectorClock = inspector;
        ocean.EnableForChecks();
        Check(ocean._timeProviderStack.Count == 2 && ReferenceEquals(ocean.TimeProvider, inspector)
            && !ocean._timeProviderStack.Contains(runtime), "normal enable changed default/inspector clock initialization");
    }
    static void EmptyClockStack()
    {
        var ocean = OceanRenderer.CreateForChecks("Flow"); ocean._timeProviderStack.Clear();
        ocean.Rebuild();
        Check(ocean._timeProviderStack.Count == 1 && ocean.TimeProvider is TimeProviderDefault, "empty rebuild stack did not receive a fallback");
    }
}

namespace Crest
{
    public interface ITimeProvider { float CurrentTime { get; } float DeltaTime { get; } float DeltaTimeDynamics { get; } }
    public class TimeProviderDefault : ITimeProvider
    {
        public float CurrentTime => 100f;
        public float DeltaTime => 1f / 60f;
        public float DeltaTimeDynamics => DeltaTime;
    }
    public class TestClock : ITimeProvider
    {
        public TestClock(float time, float delta) { CurrentTime = time; DeltaTime = delta; }
        public float CurrentTime { get; }
        public float DeltaTime { get; }
        public float DeltaTimeDynamics => DeltaTime;
    }
    public class SimSettingsAnimatedWaves : SimSettingsBase
    {
        public UnityEngine.Experimental.Rendering.GraphicsFormat _renderTextureGraphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat;
        public float AttenuationInShallows = 1f;
        public bool PingPongCombinePass;
    }
    public class Provider { public void UpdateQueries() { } public void CleanUp() { } }
    public static class UnderwaterRenderer { public static bool SkipSurfaceSelfIntersectionFixMode; }
    public static class OceanBuilder { public static void CleanUp(OceanRenderer ocean) { } }
    public static class ShiftingOrigin { public static bool HasTeleportedThisFrame; }
    public partial class OceanRenderer
    {
        public static OceanRenderer LastConstructed;
        public OceanRenderer() { LastConstructed = this; }
        public bool CleanResourcesOnDisable, FailEarlyInitialization, ThrowDuringInitialization, AddInvalidManagerOnUpdate;
        bool _enabled = true;
        public bool enabled
        {
            get => _enabled;
            set
            {
                var wasEnabled = _enabled; _enabled = value;
                if (wasEnabled && !value) { if (CleanResourcesOnDisable) CleanUp(); Disable(); }
            }
        }
        public static OceanRenderer CreateForChecks(string kind, int buffers = 2)
        {
            var ocean = new OceanRenderer { _desiredBuffers = buffers };
            ocean.Kinds.Add(kind); ocean.OnEnable(); return ocean;
        }
        public void Tick() { if (enabled) { FrameCount++; RunUpdate(); } }
        public void DisposeForChecks() => CleanUp();
        public void EnableForChecks() => OnEnable();
        public bool IsInitialized => _isInitialized;
        public bool IsRebuilding => _isRebuilding;
        public void SetRebuildingForChecks(bool value) { _isRebuilding = value; }
        public int Initializations;
        public readonly List<float> InitializationTimes = new List<float>(), InitializationDeltas = new List<float>();
        readonly List<string> Kinds = new List<string>();
        public readonly List<LodDataMgr> _lodDatas = new List<LodDataMgr>();
        public List<LodDataMgr> Managers => _lodDatas;
        public List<ITimeProvider> _timeProviderStack = new List<ITimeProvider>();
        ITimeProvider _timeProvider;
        public ITimeProvider InspectorClock { set => _timeProvider = value; }
        public ITimeProvider TimeProvider => _timeProviderStack[_timeProviderStack.Count - 1];
        public float CurrentTime => TimeProvider.CurrentTime;
        bool _isInitialized, _isRebuilding, _hasWrittenPerFrameData, _isFirstFrameSinceEnabled;
        int _desiredBuffers = 2;
        public int BufferSize { get; private set; }
        public int SettingsHash, _generatedSettingsHash;
        int CalculateSettingsHash() => SettingsHash;
        BuildCommandBuffer _commandbufferBuilder;
        public BuildCommandBuffer Builder => _commandbufferBuilder;
        public LodDataMgrAnimWaves _lodDataAnimWaves;
        public SimSettingsAnimatedWaves _simSettingsAnimatedWaves;
        public LodDataMgr _lodDataClipSurface, _lodDataSeaDepths, _lodDataShadow, _lodDataAlbedo;
        public Provider CollisionProvider, FlowProvider;
        public bool _fixFlickeringParticleInput;
        public Material OceanMaterial;
        public Camera ViewCamera;
        public GameObject Container;
        readonly List<int> _oceanChunkRenderers = new List<int>();
        public class DebugFields { public bool _disableFollowViewpoint = true; }
        DebugFields _debug = new DebugFields();
        public enum DefaultClippingState { EverythingClipped, NothingClipped }
        public DefaultClippingState _defaultClippingState;
        public enum SurfaceSelfIntersectionFixMode { Off, On, Automatic }
        SurfaceSelfIntersectionFixMode _surfaceSelfIntersectionFixMode;
        float _lodAlphaBlackPointFade, _lodAlphaBlackPointWhitePointFade, ViewerHeightAboveWater;
        public float ViewerAltitudeLevelAlpha, Scale = 1f;
        public bool ScaleCouldIncrease => false;
        public bool ScaleCouldDecrease => false;
        int _lodDataResolution => LodDataResolution;
        int _geometryDownSampleFactor = 1;
        public float CalcLodScale(int index) => Scale * (1 << index);
        void LateUpdatePosition() { }
        void LateUpdateViewerHeight() { }
        void LateUpdateScale() { }
        void LateUpdateTiles() { }
        void LateUpdateResetMaxDisplacementFromShape() { }
        void LateUpdateLods()
        {
            foreach (var manager in _lodDatas) manager.UpdateLodData();
        }
        void Enable() { Instance = this; }
        void Disable() { Instance = null; }
        void CreateDestroySubSystems()
        {
            if (AddInvalidManagerOnUpdate)
            {
                AddInvalidManagerOnUpdate = false;
                var manager = new LodDataMgrFlow(this); manager.DataTexture.Release();
                _lodDatas.Add(manager);
            }
        }
        void InitializeSceneSubstitute()
        {
            Initializations++;
            InitializationTimes.Add(CurrentTime); InitializationDeltas.Add(DeltaTime);
            if (ThrowDuringInitialization) throw new InvalidOperationException("simulated initialization exception");
            if (FailEarlyInitialization) { enabled = false; return; }
            Instance = this; Container = new GameObject(); Root = new Transform(); _lodTransform = new LodTransform();
            BufferSize = _desiredBuffers;
            _cascadeParams = new BufferedData<CascadeParams[]>(BufferSize, () => new CascadeParams[LodDataMgr.MAX_LOD_COUNT + 1]);
            _perCascadeInstanceData = new BufferedData<PerCascadeInstanceData[]>(BufferSize, () => new PerCascadeInstanceData[LodDataMgr.MAX_LOD_COUNT]);
            _bufCascadeDataTgt = new ComputeBuffer(); _bufCascadeDataSrc = new ComputeBuffer();
            _bufPerCascadeInstanceData = new ComputeBuffer(); _bufPerCascadeInstanceDataSource = new ComputeBuffer();
            foreach (var kind in Kinds)
            {
                LodDataMgr manager;
                if (kind == "Foam") manager = _lodDataFoam = new LodDataMgrFoam(this);
                else if (kind == "DynamicWaves") manager = _lodDataDynWaves = new LodDataMgrDynWaves(this);
                else if (kind == "AnimatedWaves") manager = _lodDataAnimWaves = new LodDataMgrAnimWaves(this);
                else manager = _lodDataFlow = new LodDataMgrFlow(this);
                _lodDatas.Add(manager); manager.OnEnable();
            }
            _commandbufferBuilder = new BuildCommandBuffer(); _generatedSettingsHash = SettingsHash;
            _isInitialized = true;
        }
        public struct CascadeParams
        {
            public Vector2 _posSnapped;
            public float _scale, _textureRes, _oneOverTextureRes, _texelWidth, _weight, _maxWavelength;
        }
        public struct PerCascadeInstanceData
        {
            public float _meshScaleLerp, _farNormalsWeight, _geoGridWidth;
            public Vector2 _normalScrollSpeeds;
            public Vector3 __padding;
        }
        BufferedData<CascadeParams[]> _cascadeParams;
        BufferedData<PerCascadeInstanceData[]> _perCascadeInstanceData;
        public BufferedData<CascadeParams[]> Cascades => _cascadeParams;
        public BufferedData<PerCascadeInstanceData[]> Instances => _perCascadeInstanceData;
        public ComputeBuffer _bufPerCascadeInstanceData, _bufPerCascadeInstanceDataSource;
        public static int sp_crestTime = Shader.PropertyToID("_CrestTime"), sp_sliceCount = Shader.PropertyToID("_SliceCount"),
            sp_clipByDefault = Shader.PropertyToID("_ClipByDefault"), sp_lodAlphaBlackPointFade = Shader.PropertyToID("_LodAlphaBlackPointFade"),
            sp_lodAlphaBlackPointWhitePointFade = Shader.PropertyToID("_LodAlphaBlackPointWhitePointFade"), sp_meshScaleLerp = Shader.PropertyToID("_MeshScaleLerp"),
            sp_CrestForceUnderwater = Shader.PropertyToID("_CrestForceUnderwater"), sp_perCascadeInstanceData = Shader.PropertyToID("_CrestPerCascadeInstanceData");
    }
}
