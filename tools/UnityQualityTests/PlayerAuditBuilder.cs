// Copy into Assets/Editor in the disposable validation project.
using System;
using System.IO;
using System.Linq;
using Crest;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

public static class PlayerAuditBuilder
{
    public static void Build()
    {
        try
        {
            CrestURPSetup.ValidatePipeline();
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11, GraphicsDeviceType.Direct3D12 });
            PlayerSettings.enableFrameTimingStats = true;
            var root = Path.GetDirectoryName(Application.dataPath);
            var path = Path.Combine(root, "WindowsPlayer", "CrestAudit.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var scenes = new[] { "Main/Scenes/main", "BoatDev/Scenes/boat", "BoatDev/Scenes/BoatWakes", "BoatDev/Scenes/threeboats",
                "Examples/Scenes/Examples", "LakesAndRivers/Scenes/LakesAndRivers", "Whirlpool/Scenes/whirlpool" }
                .Select(scene => "Assets/Crest/Crest-Examples/" + scene + ".unity").ToArray();
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes, locationPathName = path, target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.Development,
            });
            var summary = "CREST_PLAYER_BUILD " + report.summary.result + "; errors=" + report.summary.totalErrors
                + "; warnings=" + report.summary.totalWarnings + "; size=" + report.summary.totalSize;
            File.WriteAllText(Path.Combine(root, "player-build-result.txt"), summary);
            Debug.Log(summary);
            EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
        }
        catch (Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); }
    }
}
