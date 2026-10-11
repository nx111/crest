// Copy to Assets/PlayerAudit.cs, outside Editor, only in a disposable project.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Crest;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

public sealed partial class PlayerAudit : MonoBehaviour
{
    readonly List<string> _results = new List<string>();
    readonly List<string> _errors = new List<string>();
    string _output;

    static string[] Arguments()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        // Read the same "unity" Intent extra that supplies the native Player's flags.
        using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
        using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
        using (var intent = activity.Call<AndroidJavaObject>("getIntent"))
        {
            var value = intent.Call<string>("getStringExtra", "unity");
            return value == null ? Array.Empty<string>() : value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }
#else
        return Environment.GetCommandLineArgs();
#endif
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Initialize()
    {
        var args = Arguments();
        if (!args.Contains("-crestRuntimeSmoke")) return;
        if (args.Contains("-crestLeakStacks"))
            Unity.Collections.NativeLeakDetection.Mode = Unity.Collections.NativeLeakDetectionMode.EnabledWithStackTrace;
        var runner = new GameObject("Crest standalone rendering audit").AddComponent<PlayerAudit>();
        Object.DontDestroyOnLoad(runner.gameObject);
    }

    void Start()
    {
        var args = Arguments();
        var index = Array.IndexOf(args, "-crestCapturePath");
        _output = index >= 0 && index + 1 < args.Length ? args[index + 1] : Path.Combine(Application.persistentDataPath, "CrestAudit");
        Directory.CreateDirectory(_output);
        Application.runInBackground = true;
        Screen.sleepTimeout = SleepTimeout.NeverSleep;
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 60;
        Application.logMessageReceived += Log;
        _results.Add("Standalone Unity " + Application.unityVersion + "; GPU " + SystemInfo.graphicsDeviceName + "; API "
            + SystemInfo.graphicsDeviceType + "; CPU " + SystemInfo.processorType + "; resolution " + Screen.width + "x" + Screen.height);
        StartTemporalAudit();
        StartCoroutine(Guard(Run()));
    }

    partial void StartTemporalAudit();
    partial void FinishTemporalAudit(ref int code);
    partial void MarkTemporalPhase(string phase);

    void Log(string message, string stack, LogType type)
    {
        if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) _errors.Add(message);
    }

    IEnumerator Guard(IEnumerator routine)
    {
        while (true)
        {
            bool next = false;
            Exception failure = null;
            try { next = routine.MoveNext(); }
            catch (Exception exception) { failure = exception; }
            if (failure != null) { _results.Add("FAIL: " + failure); Finish(1); yield break; }
            if (!next) break;
            yield return routine.Current;
        }
        if (_errors.Count > 0) { _results.Add("FAIL: runtime/render errors: " + string.Join("\n", _errors)); Finish(1); }
        else { _results.Add("PASS: No standalone runtime/shader/Render Graph errors"); Finish(0); }
    }

    void Finish(int code)
    {
        FinishTemporalAudit(ref code);
        if (code == 0 && Arguments().Contains("-crestQuitAudit"))
        {
            RuntimeQueryAudit.PrepareShutdown(Check, _results.Add, passed =>
            {
                if (!passed) _results.Add("RESULT: FAIL");
                File.WriteAllLines(Path.Combine(_output, "results.txt"), _results);
            });
        }
        _results.Add("RESULT: " + (code == 0 ? "PASS" : "FAIL"));
        File.WriteAllLines(Path.Combine(_output, "results.txt"), _results);
        Debug.Log("CREST_PLAYER_AUDIT " + (code == 0 ? "PASS" : "FAIL") + "\n" + string.Join("\n", _results));
        Application.logMessageReceived -= Log;
        Application.Quit(code);
    }

    void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _results.Add("PASS: " + message);
    }

    static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    void CaptureScreen(string name)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        // Android always prepends persistentDataPath, even to an absolute filename.
        // The host uses /sdcard, an alias of the canonical app storage directory.
        var output = _output.Replace('\\', '/');
        var persistent = Application.persistentDataPath.TrimEnd('/') + "/";
        var alias = "/sdcard/Android/data/" + Application.identifier + "/files/";
        if (output.StartsWith(persistent, StringComparison.Ordinal)) output = output.Substring(persistent.Length);
        else if (output.StartsWith(alias, StringComparison.Ordinal)) output = output.Substring(alias.Length);
        else throw new InvalidOperationException("Android screen audit output must be inside this app's persistent files directory.");
        ScreenCapture.CaptureScreenshot(Path.Combine(output, name));
#else
        ScreenCapture.CaptureScreenshot(Path.Combine(_output, name));
#endif
    }

    Color[] Capture(Camera camera, string name)
    {
        MarkTemporalPhase("offscreen-" + name);
        var target = new RenderTexture(960, 540, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
        var staging = new Texture2D(960, 540, TextureFormat.RGBAFloat, false, true);
        var saved = RenderTexture.active;
        try
        {
            target.Create();
            RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
            RenderTexture.active = target;
            staging.ReadPixels(new Rect(0, 0, 960, 540), 0, 0);
            staging.Apply();
            var pixels = staging.GetPixels();
            Check(pixels.All(c => Finite(c.r) && Finite(c.g) && Finite(c.b) && Finite(c.a)), name + ": finite HDR pixels including alpha");
            Check(pixels.Count(c => c.r > 0.95f && c.b > 0.95f && c.g < 0.05f) < 25, name + ": no error-shader pixels");
            var preview = new Texture2D(960, 540, TextureFormat.RGB24, false);
            preview.SetPixels(pixels.Select(c => c.gamma).ToArray());
            preview.Apply();
            File.WriteAllBytes(Path.Combine(_output, name + ".png"), preview.EncodeToPNG());
            Object.Destroy(preview);
            return pixels;
        }
        finally { RenderTexture.active = saved; target.Release(); Object.Destroy(target); Object.Destroy(staging); }
    }

    IEnumerator Run()
    {
        // Unity accepts one screen-capture operation per frame. The temporal recorder
        // already includes the frames around the offscreen capture.
        var screenAudit = Arguments().Contains("-crestScreenAudit") && !Arguments().Contains("-crestTemporalAudit");
        Check(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset, "Player uses the URP pipeline");
        for (var sceneIndex = 0; sceneIndex < SceneManager.sceneCountInBuildSettings; sceneIndex++)
        {
            if (sceneIndex != 0)
            {
                var load = SceneManager.LoadSceneAsync(sceneIndex);
                while (!load.isDone) yield return null;
            }
            MarkTemporalPhase("scene-" + SceneManager.GetActiveScene().name + "-warmup");
            for (var frame = 0; frame < 90; frame++) yield return null;
            var scene = SceneManager.GetActiveScene().name;
            var ocean = OceanRenderer.Instance;
            Check(ocean != null && ocean.Tiles.Count > 0, scene + ": standalone ocean simulation and tiles exist");
            var camera = ocean.ViewCameraExcludingSceneCamera;
            if (camera == null) camera = Camera.main;
            Check(camera != null, scene + ": scene camera exists");
            if (sceneIndex == 0 && Arguments().Contains("-crestTemporalAudit"))
            {
                MarkTemporalPhase("main-steady-gameplay");
                for (int frame = 0; frame < 360; frame++) yield return null;
            }
            yield return new WaitForEndOfFrame();
            if (sceneIndex == 0 && screenAudit)
            {
                Debug.Log("CREST_SCREEN_AUDIT_BEFORE_OFFSCREEN");
                CaptureScreen("screen-main-before-offscreen.png");
                for (int frame = 0; frame < 4; frame++) yield return null;
                yield return new WaitForEndOfFrame();
            }
            var pixels = Capture(camera, "scene-" + scene);
            if (sceneIndex == 0 && screenAudit)
            {
                for (int frame = 0; frame < 4; frame++) yield return null;
                yield return new WaitForEndOfFrame();
                Debug.Log("CREST_SCREEN_AUDIT_AFTER_OFFSCREEN");
                CaptureScreen("screen-main-after-offscreen.png");
                var before = Path.Combine(_output, "screen-main-before-offscreen.png");
                var after = Path.Combine(_output, "screen-main-after-offscreen.png");
                for (int frame = 0; frame < 120 && (!File.Exists(before) || !File.Exists(after)); frame++) yield return null;
                Check(File.Exists(before) && File.Exists(after), "Actual Main screens before and after offscreen rendering are saved");
            }
            Check(pixels.Max(c => c.grayscale) - pixels.Min(c => c.grayscale) > 0.01f, scene + ": visible scene detail");
            var frameTimes = new List<float>();
            var gpuTimes = new List<double>();
            var timing = new FrameTiming[1];
            var timestamps = new HashSet<ulong>();
            for (var frame = 0; frame < 60; frame++)
            {
                FrameTimingManager.CaptureFrameTimings();
                yield return new WaitForEndOfFrame();
                frameTimes.Add(Time.unscaledDeltaTime * 1000);
                if (FrameTimingManager.GetLatestTimings(1, timing) > 0 && timing[0].gpuFrameTime > 0
                    && timestamps.Add(timing[0].frameStartTimestamp)) gpuTimes.Add(timing[0].gpuFrameTime);
            }
            frameTimes.Sort(); gpuTimes.Sort();
            _results.Add(scene + ": 60 gameplay frames, total frame interval median " + frameTimes[30] + " ms, p95 " + frameTimes[57]
                + " ms; GPU median " + (gpuTimes.Count == 0 ? "unavailable" : gpuTimes[gpuTimes.Count / 2] + " ms")
                + "; unique valid GPU samples=" + gpuTimes.Count + ". Development Player, 60 fps cap; absolute smoke sample, no baseline cost claim.");
            if (sceneIndex == 0)
            {
                foreach (var behaviour in camera.GetComponents<MonoBehaviour>())
                    if (!(behaviour is UnderwaterRenderer) && !(behaviour is UniversalAdditionalCameraData)
                        && !(behaviour is OceanPlanarReflection)) behaviour.enabled = false;
                var underwater = camera.GetComponent<UnderwaterRenderer>();
                if (underwater == null) underwater = camera.gameObject.AddComponent<UnderwaterRenderer>();
                var originalPosition = camera.transform.position; var originalRotation = camera.transform.rotation;
                MarkTemporalPhase("camera-switch-underwater-late");
                camera.transform.position = new Vector3(0, ocean.SeaLevel - 2, -8);
                camera.transform.LookAt(new Vector3(0, ocean.SeaLevel - 2, 15));
                underwater.EnableShaderAPI = false;
                for (var frame = 0; frame < 12; frame++) yield return null;
                yield return new WaitForEndOfFrame();
                Capture(camera, "underwater-late");
                MarkTemporalPhase("camera-switch-underwater-early");
                underwater.EnableShaderAPI = true;
                for (var frame = 0; frame < 12; frame++) yield return null;
                yield return new WaitForEndOfFrame();
                Capture(camera, "underwater-early");
                camera.transform.SetPositionAndRotation(originalPosition, originalRotation);
                MarkTemporalPhase("camera-restored-query-and-rebuild-audit");
                if (Arguments().Contains("-crestQueryAudit"))
                {
                    Debug.Log("CREST_QUERY_AUDIT_BEGIN");
                    var queries = RuntimeQueryAudit.Run(Check, _results.Add);
                    while (queries.MoveNext()) yield return queries.Current;
                    Debug.Log("CREST_QUERY_AUDIT_COMPLETE");
                }
                if (Arguments().Contains("-crestFoamStorage")) RunFoamStorage();
            }
        }
        if (Arguments().Contains("-crestGpuContracts"))
        {
            MarkTemporalPhase("gpu-numeric-contracts");
            var audit = typeof(PlayerAudit).GetMethod("RunGpuContracts", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Check(audit != null, "PlayerGpuContracts.cs was installed for the optional GPU contract audit");
            audit.Invoke(this, null);
        }
        if (Arguments().Contains("-crestPerformanceAB"))
        {
            var performance = RunPerformanceAB();
            while (performance.MoveNext()) yield return performance.Current;
        }
        if (Arguments().Contains("-crestBackgroundAudit"))
        {
            Debug.Log("CREST_BACKGROUND_READY");
            var end = Time.realtimeSinceStartup + 15;
            int peak = 0;
            while (Time.realtimeSinceStartup < end)
            {
                var provider = OceanRenderer.Instance.CollisionProvider as QueryBase;
                provider.Query(810999, 1, new[] { Vector3.zero }, new Vector3[1], null, null);
                peak = Math.Max(peak, provider.RequestCount);
                yield return null;
            }
            var resume = OceanRenderer.Instance.CollisionProvider as QueryBase;
            bool recovered = false;
            for (int frame = 0; frame < 180; frame++)
            {
                var result = resume.Query(810999, 1, new[] { Vector3.zero }, new Vector3[1], null, null);
                if (resume.RetrieveSucceeded(result)) { recovered = true; break; }
                yield return null;
            }
            Check(recovered && peak <= 7 && _pauseTransitions >= 2, "Background/resume returns valid production wave queries; peak pending="
                + peak + "; pause transitions=" + _pauseTransitions);
        }
    }

    int _pauseTransitions;
    void OnApplicationPause(bool paused) { if (Time.frameCount > 1) _pauseTransitions++; }
}
