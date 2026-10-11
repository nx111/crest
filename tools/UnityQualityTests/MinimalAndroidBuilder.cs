// Install alone in Assets/Editor of a separate empty project (no Crest assets or packages).
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

public static class MinimalAndroidBuilder
{
    public static void Build()
    {
        try
        {
            GraphicsSettings.defaultRenderPipeline = null;
            for (int level = 0; level < QualitySettings.names.Length; level++)
            { QualitySettings.SetQualityLevel(level); QualitySettings.renderPipeline = null; }
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "com.crestwater.runtimeaudit.empty");
            PlayerSettings.productName = "Unity Android Startup Control";
            PlayerSettings.companyName = "CrestValidation";
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.applicationEntry = AndroidApplicationEntry.Activity;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel26;
            PlayerSettings.Android.targetSdkVersion = (AndroidSdkVersions)36;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
            EditorUserBuildSettings.buildAppBundle = false;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var camera = new GameObject("Empty control camera").AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.cyan;
            new GameObject("Empty control lifetime").AddComponent<MinimalAndroidLifetime>();
            EditorSceneManager.SaveScene(scene, "Assets/Empty.unity");
            var root = Path.GetDirectoryName(Application.dataPath);
            var apk = Path.Combine(root, "MinimalPlayer", "Empty.apk");
            Directory.CreateDirectory(Path.GetDirectoryName(apk));
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { "Assets/Empty.unity" },
                locationPathName = apk, target = BuildTarget.Android, options = BuildOptions.Development });
            File.WriteAllText(Path.Combine(root, "minimal-build-result.txt"), "MINIMAL_BUILD " + report.summary.result
                + "; errors=" + report.summary.totalErrors + "; warnings=" + report.summary.totalWarnings);
            EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
        }
        catch (Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); }
    }
}
