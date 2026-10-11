// Run only in a disposable copy of the example project. See README.md beside this file.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Crest;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

[InitializeOnLoad]
public static class QualityRunner
{
    const string Running = "CrestQualityRunning";
    const string SpecularAA = "CrestQualitySpecularAA";
    const string Main = "Assets/Crest/Crest-Examples/Main/Scenes/main.unity";
    static readonly string Output = Path.Combine(Path.GetDirectoryName(Application.dataPath), "QualityCaptures");
    static readonly List<string> Results = new List<string>();
    static readonly List<string> Errors = new List<string>();
    static IEnumerator _routine;
    static int _frame = -1;
    static Camera _camera;
    static OceanRenderer _ocean;
    static Material _water;
    static UniversalRenderPipelineAsset _pipeline;
    static UniversalRendererData _renderer;
    static CrestUniversalRendererFeature _feature;

    static QualityRunner()
    {
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Running, false))
            {
                Application.runInBackground = true;
                ShaderUtil.allowAsyncCompilation = false;
                Directory.CreateDirectory(Output);
                Application.logMessageReceived += Log;
                _routine = Run();
                EditorApplication.update += Tick;
            }
        };
    }

    public static void Start()
    {
        CrestURPSetup.ValidatePipeline();
        SessionState.SetBool(Running, true);
        SessionState.SetBool(SpecularAA, Environment.GetCommandLineArgs().Contains("-crestSpecularAA"));
        EditorSceneManager.OpenScene(Main);
        EditorApplication.EnterPlaymode();
    }

    static void Log(string message, string stack, LogType type)
    {
        if (stack.Contains("UnityEditor.Search.SearchDatabase"))
        {
            Results.Add("EDITOR: Unity Search indexing error (outside rendering): " + message);
            return;
        }
        if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) Errors.Add(message);
    }

    static void Tick()
    {
        if (!EditorApplication.isPlaying || Time.frameCount == _frame) return;
        _frame = Time.frameCount;
        try
        {
            if (_routine.MoveNext()) return;
            Check(Errors.Count == 0, "No runtime/shader/render graph errors: " + string.Join("\n", Errors));
            Finish(0);
        }
        catch (Exception exception)
        {
            Results.Add("FAIL: " + exception);
            Debug.LogException(exception);
            Finish(1);
        }
    }

    static void Finish(int exit)
    {
        SessionState.SetBool(Running, false);
        File.WriteAllLines(Path.Combine(Output, "results.txt"), Results);
        Debug.Log("CREST_QUALITY_RESULT " + (exit == 0 ? "PASS" : "FAIL") + "\n" + string.Join("\n", Results));
        EditorApplication.update -= Tick;
        EditorApplication.Exit(exit);
    }

    static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Results.Add("PASS: " + message);
    }

    static void SetAsset(Object asset, string property, int value)
    {
        var serialized = new SerializedObject(asset);
        var field = serialized.FindProperty(property);
        if (field.propertyType == SerializedPropertyType.Boolean) field.boolValue = value != 0;
        else field.intValue = value;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        _renderer.SetDirty();
    }

    static void PrepareCamera(bool useOriginalPose = false)
    {
        var original = OceanRenderer.Instance != null ? OceanRenderer.Instance.ViewCameraExcludingSceneCamera : Camera.main;
        var originalPosition = original != null ? original.transform.position : Vector3.zero;
        var originalRotation = original != null ? original.transform.rotation : Quaternion.identity;
        foreach (var camera in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None)) camera.enabled = false;
        _ocean = OceanRenderer.Instance;
        Check(_ocean != null && _ocean.Tiles.Count > 0, "Ocean simulation and tiles exist");
        _camera = new GameObject("Quality Capture Camera").AddComponent<Camera>();
        _camera.enabled = false;
        _camera.tag = "MainCamera";
        _camera.allowHDR = true;
        _camera.allowMSAA = true;
        _camera.nearClipPlane = 0.1f;
        _camera.farClipPlane = 1000;
        _camera.clearFlags = CameraClearFlags.SolidColor;
        _camera.backgroundColor = Color.black;
        _camera.transform.position = new Vector3(0, _ocean.SeaLevel + 15, -8);
        _camera.transform.LookAt(new Vector3(0, _ocean.SeaLevel, 15));
        if (useOriginalPose && original != null)
        {
            _camera.transform.SetPositionAndRotation(originalPosition, originalRotation);
            _camera.clearFlags = original.clearFlags;
        }
        var data = _camera.GetUniversalAdditionalCameraData();
        data.renderPostProcessing = false;
        data.allowXRRendering = false;
        _ocean.ViewCamera = _camera;
        _ocean.Viewpoint = _camera.transform;
        var time = _ocean.gameObject.AddComponent<TimeProviderCustom>();
        time._overrideTime = time._overrideDeltaTime = time._paused = true;
        time._time = 12;
        time._deltaTime = 0;
        _ocean.PushTimeProvider(time);
        _water = new Material(_ocean.OceanMaterial);
        _ocean.OceanMaterial = _water;
        foreach (var tile in _ocean.Tiles) tile.Rend.sharedMaterial = _water;
    }

    static Color[] Capture(string name, int samples = 1)
    {
        var target = new RenderTexture(960, 540, 24, RenderTextureFormat.ARGBHalf) { antiAliasing = samples };
        target.Create();
        var saved = RenderTexture.active;
        var image = new Texture2D(target.width, target.height, TextureFormat.RGBAFloat, false, true);
        try
        {
            if (GraphicsSettings.currentRenderPipeline != null)
                RenderPipeline.SubmitRenderRequest(_camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
            else
            {
                var originalTarget = _camera.targetTexture;
                try { _camera.targetTexture = target; _camera.Render(); }
                finally { _camera.targetTexture = originalTarget; }
            }
            RenderTexture.active = target;
            image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            image.Apply();
            var pixels = image.GetPixels();
            Check(pixels.All(c => Finite(c.r) && Finite(c.g) && Finite(c.b) && Finite(c.a)),
                name + " has only finite HDR pixels");
            Results.Add(name + ": luminance range " + pixels.Min(c => c.grayscale) + ".." + pixels.Max(c => c.grayscale));
            var preview = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
            preview.SetPixels(pixels.Select(c => c.gamma).ToArray());
            preview.Apply();
            File.WriteAllBytes(Path.Combine(Output, name + ".png"), preview.EncodeToPNG());
            Object.DestroyImmediate(preview);
            foreach (var shader in new[] { _water.shader, Shader.Find("Hidden/Crest/Underwater/Underwater Effect") })
            {
                var messages = ShaderUtil.GetShaderMessages(shader).Where(m => m.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error).ToArray();
                Check(messages.Length == 0, name + " shader compilation: " + string.Join(";", messages.Select(m => m.message)));
            }
            var pink = pixels.Count(c => c.r > 0.95f && c.b > 0.95f && c.g < 0.05f);
            Check(pink < 25, name + " has no error-shader pixels");
            return pixels;
        }
        finally
        {
            RenderTexture.active = saved;
            Object.DestroyImmediate(image);
            target.Release();
            Object.DestroyImmediate(target);
        }
    }

    static double Difference(Color[] a, Color[] b) => a.Zip(b, (x, y) =>
        (double)(Mathf.Abs(x.r - y.r) + Mathf.Abs(x.g - y.g) + Mathf.Abs(x.b - y.b))).Average();

    static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    static IEnumerable<object> Wait(int count) { for (var i = 0; i < count; i++) yield return null; }

    static IEnumerable<object> CaptureSpecularAA(Light sun)
    {
        Check(_water.HasProperty("_SpecularAAVariance") && _water.HasProperty("_SpecularAAThreshold"),
            "Water exposes specular antialiasing controls");
        var savedWater = new Material(_water);
        var savedKeywords = _water.shaderKeywords;
        var savedSunIntensity = sun.intensity;
        var savedSunColor = sun.color;
        var savedSunRotation = sun.transform.rotation;
        var normals = new Texture2D(128, 128, TextureFormat.RGBA32, true, true)
        {
            name = "Quality high frequency normals",
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Trilinear,
        };
        var environment = new Cubemap(16, TextureFormat.RGBA32, true);
        try
        {
            // A linear, mipmapped normal texture avoids dependencies on the sample's imported texture.
            // Alpha=1 supports both RGB and RG-or-AG unpacking used by Unity's target backends.
            var normalPixels = new Color[normals.width * normals.height];
            for (var y = 0; y < normals.height; y++)
            for (var x = 0; x < normals.width; x++)
            {
                var u = 2 * Mathf.PI * x / normals.width;
                var v = 2 * Mathf.PI * y / normals.height;
                var nx = 0.35f * Mathf.Sin(8 * u) + 0.12f * Mathf.Sin(13 * v);
                var ny = 0.35f * Mathf.Cos(7 * v);
                var nz = Mathf.Sqrt(1 - nx * nx - ny * ny);
                normalPixels[y * normals.width + x] = new Color(0.5f + 0.5f * nx, 0.5f + 0.5f * ny, 0.5f + 0.5f * nz, 1);
            }
            normals.SetPixels(normalPixels);
            normals.Apply();
            for (var face = 0; face < 6; face++)
                environment.SetPixels(Enumerable.Repeat(Color.black, 256).ToArray(), (CubemapFace)face);
            environment.Apply();

            foreach (var keyword in new[] { "_TRANSPARENCY_ON", "_FOAM_ON", "_ALBEDO_ON", "_SHADOWS_ON",
                "_CAUSTICS_ON", "_SUBSURFACESCATTERING_ON", "_PROCEDURALSKY_ON", "_PLANARREFLECTIONS_ON",
                "_DIRECTIONALLIGHTVARYROUGHNESS_ON", "_FLOW_ON" }) _water.DisableKeyword(keyword);
            _water.EnableKeyword("_APPLYNORMALMAPPING_ON");
            _water.EnableKeyword("_COMPUTEDIRECTIONALLIGHT_ON");
            _water.EnableKeyword("_OVERRIDEREFLECTIONCUBEMAP_ON");
            _water.SetTexture("_ReflectionCubemapOverride", environment);
            _water.SetTexture("_Normals", normals);
            _water.SetFloat("_NormalsStrengthOverall", 1);
            _water.SetFloat("_NormalsStrength", 0.65f);
            _water.SetFloat("_NormalsScale", 20);
            _water.SetFloat("_AdditionalLightIntensity", 0);
            _water.SetFloat("_Specular", 1);
            _water.SetFloat("_DirectionalLightBoost", 20);
            _water.SetFloat("_DirectionalLightFallOff", 4096);
            _water.SetColor("_Diffuse", Color.black);
            _water.SetColor("_DiffuseGrazing", Color.black);
            var view = (_camera.transform.position - new Vector3(0, _ocean.SeaLevel, 15)).normalized;
            var sunDirection = Vector3.Reflect(-view, Vector3.up);
            sun.transform.rotation = Quaternion.LookRotation(-sunDirection);
            sun.intensity = 1;
            sun.color = Color.white;
            _water.SetFloat("_SpecularAAThreshold", 0.2f);
            _water.SetFloat("_SpecularAAVariance", 0);
            foreach (var unused in Wait(5)) yield return unused;
            var off = Capture("11d-specular-aa-off");
            Check(off.Max(c => c.grayscale) > 0.001f, "Specular AA diagnostic contains a visible sun highlight");
            _water.SetFloat("_SpecularAAVariance", 0.15f);
            var on = Capture("11e-specular-aa-on");
            var delta = Difference(off, on);
            Check(delta > 0.000001, "Specular AA changes high frequency water highlights; mean RGB delta " + delta);
            _water.SetFloat("_SpecularAAVariance", 0);
            var restored = Capture("11f-specular-aa-off-restored");
            Check(Difference(off, restored) < 0.00001,
                "Zero specular variance restores the unfiltered highlight at frozen simulation time");
            Results.Add("NOTE: Specular AA captures test integration and reversibility, not temporal stability or supersampled accuracy.");
        }
        finally
        {
            _water.CopyPropertiesFromMaterial(savedWater);
            _water.shaderKeywords = savedKeywords;
            sun.intensity = savedSunIntensity;
            sun.color = savedSunColor;
            sun.transform.rotation = savedSunRotation;
            Object.DestroyImmediate(savedWater);
            Object.DestroyImmediate(normals);
            Object.DestroyImmediate(environment);
        }
    }

    static IEnumerator Run()
    {
        Results.Add("Unity " + Application.unityVersion + "; GPU " + SystemInfo.graphicsDeviceName + "; " + SystemInfo.graphicsDeviceType);
        _pipeline = (UniversalRenderPipelineAsset)GraphicsSettings.currentRenderPipeline;
        _renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/CrestURPSettings/CrestRenderer.asset");
        _feature = _renderer.rendererFeatures.OfType<CrestUniversalRendererFeature>().Single();
        foreach (var unused in Wait(90)) yield return unused;
        PrepareCamera(true);
        foreach (var unused in Wait(20)) yield return unused;
        var day = Capture("01-daylight");
        Check(day.Select(c => c.grayscale).Max() - day.Select(c => c.grayscale).Min() > 0.01f, "Daylight frame has visible scene detail");

        // Isolate ocean lighting: no opaque surfaces can account for these image differences.
        foreach (var renderer in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            if (!renderer.GetComponent<OceanChunkRenderer>()) renderer.enabled = false;
        foreach (var light in Object.FindObjectsByType<Light>(FindObjectsSortMode.None)) light.enabled = false;
        var sun = new GameObject("Zero intensity sun").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.intensity = 0;
        RenderSettings.sun = sun;
        _ocean._primaryLight = sun;
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = Color.black;
        RenderSettings.ambientIntensity = 0;
        RenderSettings.fog = false;
        _camera.clearFlags = CameraClearFlags.SolidColor;
        _camera.transform.position = new Vector3(0, _ocean.SeaLevel + 15, -8);
        _camera.transform.LookAt(new Vector3(0, _ocean.SeaLevel, 15));
        _water.DisableKeyword("_TRANSPARENCY_ON");
        _water.DisableKeyword("_FOAM_ON");
        _water.DisableKeyword("_ALBEDO_ON");
        _water.DisableKeyword("_SHADOWS_ON");
        _water.DisableKeyword("_CAUSTICS_ON");
        _water.SetFloat("_Specular", 0);
        // Neutral tint prevents a pure red test light being absorbed by the example's nearly zero red water tint.
        _water.SetColor("_Diffuse", new Color(0.2f, 0.2f, 0.2f));
        _water.SetColor("_DiffuseGrazing", new Color(0.2f, 0.2f, 0.2f));
        _water.SetColor("_SubSurfaceColour", new Color(0.3f, 0.3f, 0.3f));
        _water.DisableKeyword("_SUBSURFACESHALLOWCOLOUR_ON");
        _water.SetFloat("_AdditionalLightIntensity", 1);
        foreach (var unused in Wait(15)) yield return unused;
        var dark = Capture("02-lights-off");
        var point = new GameObject("Quality point light").AddComponent<Light>();
        point.type = LightType.Point;
        point.color = Color.red;
        point.range = 50;
        point.intensity = 80;
        point.transform.position = new Vector3(0, _ocean.SeaLevel + 4, 12);
        var lit = Capture("03-point-light");
        var pointDelta = Difference(dark, lit);
        Check(pointDelta > 0.0001, "Point light illuminates only-ocean frame; mean RGB delta " + pointDelta);
        _water.SetFloat("_AdditionalLightIntensity", 0);
        var zero = Capture("04-zero-intensity");
        Check(Difference(dark, zero) < 0.00001, "Zero additional intensity preserves the original water appearance");
        _water.SetFloat("_AdditionalLightIntensity", 1);
        _water.DisableKeyword("_COMPUTEDIRECTIONALLIGHT_ON");
        _water.SetFloat("_Specular", 1);
        _water.SetFloat("_AdditionalLightBoost", 7);
        _water.SetFloat("_AdditionalLightFallOff", 80);
        _water.SetColor("_Diffuse", Color.black);
        _water.SetColor("_DiffuseGrazing", Color.black);
        _water.SetColor("_SubSurfaceColour", Color.black);
        point.enabled = false;
        var noHighlight = Capture("04b-additional-highlight-off");
        point.enabled = true;
        var highlight = Capture("04c-additional-highlight-on");
        Check(Difference(noHighlight, highlight) > 0.00001, "Local water highlights work with directional highlights disabled");
        _water.SetFloat("_Specular", 0);
        _water.EnableKeyword("_COMPUTEDIRECTIONALLIGHT_ON");
        _water.SetColor("_Diffuse", new Color(0.2f, 0.2f, 0.2f));
        _water.SetColor("_DiffuseGrazing", new Color(0.2f, 0.2f, 0.2f));
        _water.SetColor("_SubSurfaceColour", new Color(0.3f, 0.3f, 0.3f));
        point.transform.position = new Vector3(0, 200, 12);
        var far = Capture("05-point-out-of-range");
        Check(Difference(dark, far) < pointDelta * 0.05, "Point light fades outside its range");
        point.enabled = false;
        var spot = new GameObject("Quality spot light").AddComponent<Light>();
        spot.type = LightType.Spot;
        spot.range = 60;
        spot.spotAngle = 75;
        spot.intensity = 120;
        spot.color = Color.cyan;
        spot.transform.position = new Vector3(0, _ocean.SeaLevel + 8, 15);
        spot.transform.rotation = Quaternion.Euler(90, 0, 0);
        var spotlight = Capture("06-spot-light");
        var spotDelta = Difference(dark, spotlight);
        Check(spotDelta > 0.0001, "Spot light illuminates the sea; mean RGB delta " + spotDelta);
        spot.transform.rotation = Quaternion.Euler(-90, 0, 0);
        var away = Capture("07-spot-away");
        Check(Difference(dark, away) < spotDelta * 0.05, "Spot light cone rejects water behind the light");
        spot.transform.rotation = Quaternion.Euler(90, 0, 0);
        var caster = GameObject.CreatePrimitive(PrimitiveType.Cube);
        caster.transform.position = new Vector3(0, _ocean.SeaLevel + 4, 15);
        caster.transform.localScale = new Vector3(6, 2, 6);
        var black = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        black.SetColor("_BaseColor", Color.black);
        black.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
        caster.GetComponent<Renderer>().sharedMaterial = black;
        spot.shadows = LightShadows.None;
        var noShadow = Capture("07b-spot-no-shadow");
        spot.shadows = LightShadows.Hard;
        var withShadow = Capture("07c-spot-shadow");
        Check(Difference(noShadow, withShadow) > 0.0001 && withShadow.Average(c => c.grayscale) < noShadow.Average(c => c.grayscale),
            "Additional spot shadow darkens the water under a caster");
        Object.Destroy(caster);
        spot.enabled = false;

        point.transform.position = new Vector3(0, _ocean.SeaLevel + 4, 12);
        point.enabled = true;
        SetAsset(_pipeline, "m_AdditionalLightsRenderingMode", (int)LightRenderingMode.PerVertex);
        foreach (var unused in Wait(5)) yield return unused;
        var vertexOn = Capture("08-per-vertex-point");
        point.enabled = false;
        var vertexOff = Capture("09-per-vertex-off");
        Check(Difference(vertexOn, vertexOff) > 0.0001, "Per-vertex URP asset retains local water illumination");
        SetAsset(_pipeline, "m_AdditionalLightsRenderingMode", (int)LightRenderingMode.PerPixel);
        _renderer.renderingMode = RenderingMode.ForwardPlus;
        point.enabled = true;
        foreach (var unused in Wait(5)) yield return unused;
        var clusterOn = Capture("10-forward-plus-point");
        point.enabled = false;
        var clusterOff = Capture("11-forward-plus-off");
        Check(Difference(clusterOn, clusterOff) > 0.0001, "Forward+ clustered water light loop works");
        _renderer.renderingMode = RenderingMode.Forward;
        foreach (var unused in Wait(5)) yield return unused;

        foreach (var probe in Object.FindObjectsByType<ReflectionProbe>(FindObjectsSortMode.None)) probe.enabled = false;
        var cubemap = new Cubemap(16, TextureFormat.RGBA32, false);
        var faces = new[] { Color.red, Color.green, Color.white, Color.black, Color.blue, Color.cyan };
        for (var face = 0; face < faces.Length; face++)
            cubemap.SetPixels(Enumerable.Repeat(faces[face], 16 * 16).ToArray(), (CubemapFace)face);
        cubemap.Apply();
        var reflection = new GameObject("Box projection regression probe").AddComponent<ReflectionProbe>();
        reflection.transform.position = new Vector3(-8, _ocean.SeaLevel + 8, 12);
        reflection.size = new Vector3(100, 40, 100);
        reflection.mode = ReflectionProbeMode.Custom;
        reflection.customBakedTexture = cubemap;
        reflection.boxProjection = true;
        reflection.blendDistance = 0;
        reflection.intensity = 1;
        _water.DisableKeyword("_PROCEDURALSKY_ON");
        _water.DisableKeyword("_PLANARREFLECTIONS_ON");
        _water.DisableKeyword("_OVERRIDEREFLECTIONCUBEMAP_ON");
        _water.SetFloat("_Specular", 1);
        _water.SetFloat("_Roughness", 0);
        _water.SetFloat("_DirectionalLightBoost", 0);
        SetAsset(_pipeline, "m_ReflectionProbeBoxProjection", 0);
        foreach (var unused in Wait(5)) yield return unused;
        var noBox = Capture("11b-probe-no-box-projection");
        SetAsset(_pipeline, "m_ReflectionProbeBoxProjection", 1);
        foreach (var unused in Wait(5)) yield return unused;
        var box = Capture("11c-probe-box-projection");
        Check(Difference(noBox, box) > 0.0001, "Reflection probe box projection changes the reflected local environment");
        reflection.enabled = false;
        _water.SetFloat("_Specular", 0);

        if (SessionState.GetBool(SpecularAA, false))
            foreach (var unused in CaptureSpecularAA(sun)) yield return unused;
        else
            Results.Add("SKIP: Specular AA capture diagnostic; add -crestSpecularAA to enable it.");

        // A regular URP transparent material, behind the depth-writing sea, needs no Crest shader.
        _water.EnableKeyword("_TRANSPARENCY_ON");
        _water.SetVector("_DepthFogDensity", Vector4.zero);
        _water.SetFloat("_RefractionStrength", 1);
        _water.SetFloat("_NormalsStrengthOverall", 2);
        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "Opaque refraction depth reference";
        floor.transform.position = new Vector3(0, _ocean.SeaLevel - 6, 12);
        floor.transform.localScale = new Vector3(20, 1, 20);
        floor.GetComponent<Renderer>().sharedMaterial = black;
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = "Ordinary underwater transparent";
        cube.layer = LayerMask.NameToLayer("Crest Refracted");
        cube.transform.position = new Vector3(0, _ocean.SeaLevel - 2.5f, 12);
        cube.transform.localScale = new Vector3(8, 2, 3);
        var transparent = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
        transparent.SetColor("_BaseColor", new Color(1, 0.2f, 0.05f, 0.65f));
        transparent.SetFloat("_Surface", 1);
        transparent.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        transparent.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        transparent.SetFloat("_ZWrite", 0);
        transparent.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        transparent.renderQueue = 3000;
        cube.GetComponent<Renderer>().sharedMaterial = transparent;
        var layerMask = 1 << cube.layer;
        _feature.RefractedLayers = 0;
        _renderer.transparentLayerMask = -1;
        _feature.Create();
        foreach (var unused in Wait(5)) yield return unused;
        var opaqueOnly = Capture("12-opaque-only-refraction");
        Check(Shader.GetGlobalInt("_CrestRefractionEnabled") == 0, "Opaque-only fallback resets per camera");
        _feature.RefractedLayers = layerMask;
        _renderer.transparentLayerMask = ~layerMask;
        _feature.Create();
        foreach (var unused in Wait(5)) yield return unused;
        _water.SetFloat("_RefractionStrength", 0);
        var undistorted = Capture("13a-transparent-no-distortion");
        _water.SetFloat("_RefractionStrength", 1);
        var refracted = Capture("13-transparent-refraction");
        Check(Shader.GetGlobalInt("_CrestRefractionEnabled") == 1, "Pre-water capture bound to this camera");
        Check(Difference(opaqueOnly, refracted) > 0.0001, "Ordinary underwater transparent appears in refraction");
        Check(Difference(undistorted, refracted) > 0.00001, "Wave normals distort the ordinary transparent behind water");
        Check(refracted.Count(c => c.r > c.g * 2 && c.r > c.b * 2 && c.r > 0.1f) >
            opaqueOnly.Count(c => c.r > c.g * 2 && c.r > c.b * 2 && c.r > 0.1f) + 50,
            "Refracted transparent contributes visible color through the sea");

        var underwater = _camera.gameObject.AddComponent<UnderwaterRenderer>();
        RenderSettings.ambientLight = new Color(0.2f, 0.2f, 0.2f);
        RenderSettings.ambientIntensity = 1;
        sun.intensity = 1;
        sun.transform.rotation = Quaternion.Euler(50, 20, 0);
        _water.SetFloat("_SubSurfaceBase", 0.6f);
        _water.SetFloat("_SubSurfaceSun", 0.6f);
        _water.SetVector("_DepthFogDensity", new Vector4(0.07f, 0.04f, 0.02f, 0));
        _camera.transform.position = new Vector3(0, _ocean.SeaLevel - 1, -8);
        _camera.transform.LookAt(new Vector3(0, _ocean.SeaLevel - 1, 15));
        foreach (var unused in Wait(15)) yield return unused;
        var late = Capture("14-underwater-late");
        Check(late.Average(c => c.grayscale) > 0.001, "Late underwater fog produces visible color");
        underwater.EnableShaderAPI = true;
        foreach (var unused in Wait(15)) yield return unused;
        var early = Capture("15-underwater-early");
        Check(early.Average(c => c.grayscale) > 0.001, "Early underwater fog produces visible color");
        foreach (var samples in new[] { 1, 2, 4, 8 })
        {
            _pipeline.msaaSampleCount = samples;
            foreach (var unused in Wait(5)) yield return unused;
            Capture("16-msaa-" + samples, samples);
            Results.Add("MSAA requested " + samples + "; waterline offset " + Shader.GetGlobalInt("_CrestDepthTextureOffset"));
        }
        _pipeline.msaaSampleCount = 1;
        _pipeline.renderScale = 0.75f;
        foreach (var unused in Wait(5)) yield return unused;
        Capture("17-render-scale-075");
        _pipeline.renderScale = 1;
        _camera.transform.position = new Vector3(0, _ocean.SeaLevel + 15, -8);
        _camera.transform.LookAt(new Vector3(0, _ocean.SeaLevel, 15));
        _camera.orthographic = true;
        _camera.orthographicSize = 15;
        foreach (var unused in Wait(10)) yield return unused;
        Capture("18-orthographic");

        // Smoke-check all shipped examples with the current shader variants and refraction enabled.
        foreach (var scene in new[] { "BoatDev/Scenes/boat", "BoatDev/Scenes/BoatWakes", "BoatDev/Scenes/threeboats",
            "Examples/Scenes/Examples", "LakesAndRivers/Scenes/LakesAndRivers", "Main/Scenes/main", "Whirlpool/Scenes/whirlpool" })
        {
            var operation = EditorSceneManager.LoadSceneAsyncInPlayMode("Assets/Crest/Crest-Examples/" + scene + ".unity", new LoadSceneParameters(LoadSceneMode.Single));
            while (!operation.isDone) yield return null;
            foreach (var unused in Wait(50)) yield return unused;
            PrepareCamera(true);
            foreach (var unused in Wait(10)) yield return unused;
            foreach (var material in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None)
                .SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct())
                for (var pass = 0; pass < material.passCount; pass++) ShaderUtil.CompilePass(material, pass, true);
            Capture("scene-" + Path.GetFileName(scene));
        }

        var referenceShader = Shader.Find("Crest/Ocean Builtin Reference");
        if (referenceShader == null)
        {
            Results.Add("SKIP: Controlled Built-in surface comparison; prepare_builtin_reference.py was not run.");
            yield break;
        }
        var main = EditorSceneManager.LoadSceneAsyncInPlayMode(Main, new LoadSceneParameters(LoadSceneMode.Single));
        while (!main.isDone) yield return null;
        foreach (var unused in Wait(50)) yield return unused;
        PrepareCamera();
        foreach (var renderer in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            if (!renderer.GetComponent<OceanChunkRenderer>()) renderer.enabled = false;
        foreach (var planar in Object.FindObjectsByType<OceanPlanarReflection>(FindObjectsSortMode.None)) planar.enabled = false;
        foreach (var light in Object.FindObjectsByType<Light>(FindObjectsSortMode.None)) light.enabled = false;
        var referenceSun = new GameObject("Reference sun").AddComponent<Light>();
        referenceSun.type = LightType.Directional;
        referenceSun.intensity = 1;
        referenceSun.transform.rotation = Quaternion.Euler(50, 20, 0);
        RenderSettings.sun = _ocean._primaryLight = referenceSun;
        RenderSettings.fog = false;
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = Color.gray;
        foreach (var keyword in new[] { "_TRANSPARENCY_ON", "_FOAM_ON", "_ALBEDO_ON", "_SHADOWS_ON", "_CAUSTICS_ON",
            "_SUBSURFACESHALLOWCOLOUR_ON", "_PLANARREFLECTIONS_ON", "_PROCEDURALSKY_ON", "_CLIPSURFACE_ON", "_CLIPUNDERTERRAIN_ON" })
            _water.DisableKeyword(keyword);
        _water.SetFloat("_AdditionalLightIntensity", 0);
        _water.SetFloat("_SubSurfaceBase", 0.6f);
        _water.SetFloat("_SubSurfaceSun", 0.6f);
        _water.EnableKeyword("_OVERRIDEREFLECTIONCUBEMAP_ON");
        var environment = new Cubemap(16, TextureFormat.RGBA32, false);
        for (var face = 0; face < 6; face++)
            environment.SetPixels(Enumerable.Repeat(new Color(0.05f, 0.1f, 0.2f), 256).ToArray(), (CubemapFace)face);
        environment.Apply();
        _water.SetTexture("_ReflectionCubemapOverride", environment);
        var pipeline = GraphicsSettings.defaultRenderPipeline;
        var qualityPipeline = QualitySettings.renderPipeline;
        var waterShader = _water.shader;
        var waterKeywords = _water.shaderKeywords;
        var specularAAVariance = _water.GetFloat("_SpecularAAVariance");
        try
        {
            // The original Built-in shader has no specular AA. Compare equivalent shading options.
            _water.SetFloat("_SpecularAAVariance", 0);
            foreach (var unused in Wait(20)) yield return unused;
            var urp = Capture("reference-urp-surface");
            _water.shader = referenceShader;
            // Shader assignment can restore previously inactive local keywords in Unity. Match the exact variant.
            _water.shaderKeywords = waterKeywords;
            GraphicsSettings.defaultRenderPipeline = null;
            QualitySettings.renderPipeline = null;
            _camera.depthTextureMode = DepthTextureMode.Depth;
            foreach (var unused in Wait(10)) yield return unused;
            Check(!_water.IsKeywordEnabled("_FOAM_ON") && !_water.IsKeywordEnabled("_TRANSPARENCY_ON"),
                "Built-in reference uses the same foam/refraction settings");
            var builtin = Capture("reference-builtin-surface");
            var delta = Difference(urp, builtin);
            Results.Add("Controlled original Built-in / URP surface mean RGB delta: " + delta);
            Check(delta < 0.015, "Original Built-in and URP surface shading agree in the controlled single-sun case");
        }
        finally
        {
            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = qualityPipeline;
            _water.shader = waterShader;
            _water.shaderKeywords = waterKeywords;
            _water.SetFloat("_SpecularAAVariance", specularAAVariance);
        }
    }
}
