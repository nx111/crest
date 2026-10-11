// Copy to Assets/Editor only in the disposable validation project.
using System;
using System.IO;
using System.Linq;
using Crest;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

public static class AndroidAuditBuilder
{
    public const string Package = "com.crestwater.runtimeaudit";

    public static void Build()
    {
        try
        {
            CrestURPSetup.ValidatePipeline();
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel26;
            PlayerSettings.Android.targetSdkVersion = (AndroidSdkVersions)36;
            PlayerSettings.Android.applicationEntry = AndroidApplicationEntry.Activity;
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, Package);
            PlayerSettings.companyName = "CrestValidation";
            PlayerSettings.productName = "Crest Runtime Audit";
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
            PlayerSettings.enableFrameTimingStats = true;
            EditorUserBuildSettings.buildAppBundle = false;
            var root = Path.GetDirectoryName(Application.dataPath);
            var path = Path.Combine(root, "AndroidPlayer", "CrestAudit.apk");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var scenes = new[] { "Main/Scenes/main", "BoatDev/Scenes/boat", "BoatDev/Scenes/BoatWakes", "BoatDev/Scenes/threeboats",
                "Examples/Scenes/Examples", "LakesAndRivers/Scenes/LakesAndRivers", "Whirlpool/Scenes/whirlpool" }
                .Select(scene => "Assets/Crest/Crest-Examples/" + scene + ".unity").ToArray();
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes, locationPathName = path, target = BuildTarget.Android,
                options = BuildOptions.Development,
            });
            var summary = "CREST_ANDROID_BUILD " + report.summary.result + "; errors=" + report.summary.totalErrors
                + "; warnings=" + report.summary.totalWarnings + "; size=" + report.summary.totalSize;
            File.WriteAllText(Path.Combine(root, "android-build-result.txt"), summary);
            Debug.Log(summary);
            EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
        }
        catch (Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); }
    }
}
