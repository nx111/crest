#!/usr/bin/env python3
"""Review #7 regressions: production FFT and extracted graphics combine method.

Reuses the existing managed FFT substitutes without changing that harness.
Checks command parameters, cache ownership and material keywords; no Unity/GPU
execution. --ref runs the same checks against an earlier production revision.
"""

import argparse
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

from check_underwater_lifecycle import extract_method


FFT_TESTS = r'''
    static void GravityPausedSameFrame()
    {
        var command = new CommandBuffer(); var spectrum = new OceanWaveSpectrum();
        var first = GenerateConditions(command, spectrum);
        var owned = OwnedTextures(first);
        OceanRenderer.Instance.Gravity = 3.71f;
        command.Clear();
        var second = GenerateConditions(command, spectrum);
        Check(!ReferenceEquals(first, second) && FFTCompute.GeneratorCount == 2,
            "changed gravity reused a live same-frame output");
        Check(command.Dispatches.Count == 4 && command.GravityDispatches.All(g => g == 3.71f),
            "paused-time gravity change did not initialize and update with new gravity");
        command.Clear();
        Check(ReferenceEquals(second, GenerateConditions(command, spectrum)) && command.Dispatches.Count == 0,
            "identical new-gravity conditions did not share their cache");
        OceanRenderer.Instance.Gravity = 9.81f;
        Check(ReferenceEquals(first, GenerateConditions(command, spectrum)) && command.Dispatches.Count == 0,
            "return to earlier gravity lost its live same-frame cache");
        FFTCompute.CleanUpAll();
        Check(owned.All(t => t.Destroyed), "old-gravity resource ownership was lost");
    }

    static void GravityIdleReuse()
    {
        var command = new CommandBuffer(); var spectrum = new OceanWaveSpectrum();
        var first = GenerateConditions(command, spectrum);
        for (var frame = 1; frame <= 120; frame++)
        {
            OceanRenderer.FrameCount = frame;
            OceanRenderer.Instance.Gravity = frame * .1f;
            command.Clear();
            Check(ReferenceEquals(first, GenerateConditions(command, spectrum)) && FFTCompute.GeneratorCount == 1,
                "gravity-only animation retained extra texture sets at frame " + frame);
            Check(command.Dispatches.Count == 4 && command.GravityDispatches.All(g => g == frame * .1f),
                "idle reuse failed to rebuild at paused time with current gravity");
        }
    }

    static void GravityWindNotification()
    {
        var command = new CommandBuffer(); var spectrum = new OceanWaveSpectrum();
        var first = GenerateConditions(command, spectrum);
        OceanRenderer.FrameCount++;
        OceanRenderer.Instance.Gravity = 1.62f;
        ChangeConditions(spectrum, 10f, 11f);
        command.Clear();
        var second = GenerateConditions(command, spectrum, 11f);
        Check(ReferenceEquals(first, second) && FFTCompute.GeneratorCount == 1,
            "simultaneous gravity and wind changes did not reuse idle storage");
        Check(command.Dispatches.Count == 4 && command.GravityDispatches.All(g => g == 1.62f),
            "notification retuned wind but retained previous gravity");
    }

    static void GravityMergeOwnership()
    {
        var command = new CommandBuffer(); var spectrum = new OceanWaveSpectrum();
        var first = GenerateConditions(command, spectrum);
        var firstOwned = OwnedTextures(first);
        OceanRenderer.Instance.Gravity = 3.71f;
        var second = GenerateConditions(command, spectrum, 11f);
        var secondOwned = OwnedTextures(second);
        ChangeConditions(spectrum, 10f, 11f);
        Check(firstOwned.Concat(secondOwned).All(t => !t.Destroyed), "merge destroyed recorded outputs");
        command.Clear();
        Check(ReferenceEquals(second, GenerateConditions(command, spectrum, 11f)) && command.Dispatches.Count == 0,
            "notification failed to find the existing current-gravity cache");
        FFTCompute.CleanUpAll();
        Check(firstOwned.Concat(secondOwned).All(t => t.Destroyed), "merge orphaned gravity-specific resources");
    }

    static void GravitySnapshotAndRebind()
    {
        var first = Generator();
        OceanRenderer.Instance.Gravity = 3.71f;
        var second = Generator();
        var command = new CommandBuffer();
        RequireGenerated(first, command, 2f);
        Check(command.GravityDispatches.All(g => g == 9.81f), "initialization did not use constructor gravity snapshot");
        command.Clear(); RequireGenerated(second, command, 2f);
        command.Clear(); Generate(first, command, 3f);
        Check(command.Dispatches.Count == 3 && command.GravityDispatches.Single() == 9.81f,
            "cached spectrum update inherited another generator's shared shader gravity");
        first.Release(); second.Release();
    }

    static void GravityProtectFrameAndCompatibility()
    {
        var command = new CommandBuffer(); var spectrum = new OceanWaveSpectrum();
        var differentSize = FFTCompute.GenerateDisplacements(command, 32, -1f, .2f, 0f, 10f, 2f, spectrum, false);
        var differentPeriod = FFTCompute.GenerateDisplacements(command, 16, 8f, .2f, 0f, 10f, 2f, spectrum, false);
        OceanRenderer.FrameCount++;
        var live = GenerateConditions(command, spectrum);
        OceanRenderer.Instance.Gravity = 0f;
        command.Clear();
        var zero = GenerateConditions(command, spectrum);
        Check(!ReferenceEquals(zero, live) && !ReferenceEquals(zero, differentSize) && !ReferenceEquals(zero, differentPeriod),
            "gravity change reused live or incompatible storage");
        Check(command.Dispatches.Count == 4 && command.GravityDispatches.All(g => g == 0f),
            "zero gravity did not receive a new initial spectrum");
    }
'''


GRAPHICS_HARNESS = r'''
using System;
using System.Collections.Generic;
using System.Linq;
using Crest.Internal;
using UnityEngine;
using UnityEngine.Rendering;
namespace UnityEngine
{
    public class Material
    {
        readonly HashSet<string> _keywords = new HashSet<string>();
        public void EnableKeyword(string keyword) => _keywords.Add(keyword);
        public void DisableKeyword(string keyword) => _keywords.Remove(keyword);
        public bool IsKeywordEnabled(string keyword) => _keywords.Contains(keyword);
    }
    public struct Matrix4x4 { public static Matrix4x4 identity => default; }
    public enum CubemapFace { Unknown }
    public enum MeshTopology { Triangles }
}
namespace UnityEngine.Rendering
{
    public class CommandBuffer
    {
        public readonly List<(int pass, bool flow, bool dynamic)> Draws = new List<(int, bool, bool)>();
        public void SetRenderTarget(object target, int mip = 0, CubemapFace face = CubemapFace.Unknown, int slice = 0) { }
        public void DrawProcedural(Matrix4x4 matrix, Material material, int pass, MeshTopology topology, int count)
            => Draws.Add((pass, material.IsKeywordEnabled(Crest.LodDataMgrFlow.FLOW_KEYWORD),
                material.IsKeywordEnabled(Crest.LodDataMgrDynWaves.DYNWAVES_KEYWORD)));
    }
}
namespace Crest.Internal { static class Extensions { __SET_KEYWORD__ } }
namespace Crest
{
    public class PropertyWrapperMaterial
    {
        public Material material = new Material();
        public void SetInt(int id, int value) { }
        public void SetTexture(int id, object value) { }
        public void SetBuffer(int id, object value) { }
    }
    public class LodDataMgrFlow { public const string FLOW_KEYWORD = "CREST_FLOW_ON_INTERNAL"; }
    public class LodDataMgrDynWaves
    {
        public const string DYNWAVES_KEYWORD = "CREST_DYNAMIC_WAVE_SIM_ON_INTERNAL";
        public void BindCopySettings(PropertyWrapperMaterial properties) { }
    }
    public class OceanRenderer
    {
        public static OceanRenderer Instance = new OceanRenderer();
        public int CurrentLodCount = 3;
        public LodDataMgrFlow _lodDataFlow;
        public LodDataMgrDynWaves _lodDataDynWaves;
        public object _bufCascadeDataTgt, _bufPerCascadeInstanceData;
        public static int sp_cascadeData, sp_perCascadeInstanceData;
    }
    static class TextureArrayHelpers { public static object BlackTextureArray; }
    class Targets { public object Current; }
    class GraphicsChecks
    {
        readonly PropertyWrapperMaterial[] _combineMaterial = Enumerable.Range(0, 3).Select(_ => new PropertyWrapperMaterial()).ToArray();
        readonly Targets _targets = new Targets();
        object _combineBuffer;
        int sp_LD_SliceIndex, sp_CombineBuffer;
        bool _shapeCombinePass = true;
        void BindWaveBuffer(PropertyWrapperMaterial properties) { }
        void Bind(PropertyWrapperMaterial properties) { }
        int GetParamIdSampler() => 0;
        __COMBINE_METHOD__
        public static int Main()
        {
            var checks = new GraphicsChecks();
            var materials = checks._combineMaterial.Select(p => p.material).ToArray();
            foreach (var combine in new[] { true, false })
            foreach (var state in new[] { (false, false), (true, true), (true, false), (false, true), (false, false), (true, true) })
            {
                checks._shapeCombinePass = combine;
                OceanRenderer.Instance._lodDataFlow = state.Item1 ? new LodDataMgrFlow() : null;
                OceanRenderer.Instance._lodDataDynWaves = state.Item2 ? new LodDataMgrDynWaves() : null;
                var command = new CommandBuffer();
                checks.CombinePassPingPong(command);
                if (command.Draws.Count != 6 || command.Draws.Any(d => d.flow != state.Item1 || d.dynamic != state.Item2))
                    throw new Exception("stale per-LOD keywords: flow=" + state.Item1 + ", dynamic=" + state.Item2);
                if (checks._combineMaterial.Where((p, i) => !ReferenceEquals(p.material, materials[i])).Any())
                    throw new Exception("toggle rebuilt materials instead of updating them");
            }
            Console.WriteLine("PASS: graphics combine records current keywords on every LOD across 12 runtime state transitions");
            Console.WriteLine("Extracted production graphics combine method and keyword setter; managed substitutes, no Unity/GPU.");
            return 0;
        }
    }
}
'''


def run_project(dotnet, sources):
    with tempfile.TemporaryDirectory(prefix="crest-review7-") as folder:
        project = Path(folder)
        for name, content in sources.items():
            (project / name).write_text(content, encoding="utf-8")
        (project / "Checks.csproj").write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
            '<TargetFramework>net10.0</TargetFramework><LangVersion>9.0</LangVersion>'
            '<EnableNETAnalyzers>false</EnableNETAnalyzers></PropertyGroup></Project>', encoding="utf-8")
        (project / "NuGet.Config").write_text(
            '<configuration><packageSources><clear /></packageSources></configuration>', encoding="utf-8")
        env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1")
        build = subprocess.run([dotnet, "build", "--nologo", "--verbosity", "quiet"], cwd=project, env=env,
                               text=True, encoding="utf-8", errors="replace", stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        if build.returncode:
            print(build.stdout, end="")
            return build.returncode
        return subprocess.run([dotnet, str(project / "bin/Debug/net10.0/Checks.dll")], cwd=project, env=env).returncode


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ref")
    parser.add_argument("--dotnet", default=shutil.which("dotnet"))
    args = parser.parse_args()
    if not args.dotnet:
        parser.error(".NET 10 SDK not found")
    root = Path(__file__).resolve().parents[1]

    def read(path):
        return (subprocess.check_output(["git", "show", f"{args.ref}:{path}"], cwd=root).decode("utf-8-sig")
                if args.ref else (root / path).read_text(encoding="utf-8-sig"))

    harness = (root / "tools/FftLifecycleChecks/Program.cs").read_text(encoding="utf-8")
    names = ("GravityPausedSameFrame", "GravityIdleReuse", "GravityWindNotification", "GravityMergeOwnership",
             "GravitySnapshotAndRebind", "GravityProtectFrameAndCompatibility")
    harness = harness.replace('("first generation at time -1',
                              ''.join(f'("{name}", {name}),\n            ' for name in names) + '("first generation at time -1', 1)
    harness = harness.replace("    static void Check(bool value", FFT_TESTS + "\n    static void Check(bool value", 1)
    harness = harness.replace("OceanRenderer.FrameCount = 0;", "OceanRenderer.FrameCount = 0; OceanRenderer.Instance.Gravity = 9.81f;")
    harness = harness.replace("public readonly Dictionary<int, float> FloatParams",
                              "public readonly List<float> GravityDispatches = new List<float>();\n        public readonly Dictionary<int, float> FloatParams")
    harness = harness.replace("Dispatches.Clear(); FloatParams.Clear();", "Dispatches.Clear(); GravityDispatches.Clear(); FloatParams.Clear();")
    harness = harness.replace("=> Dispatches.Add((shader.name, shader.KernelName(kernel)));",
                              '{ Dispatches.Add((shader.name, shader.KernelName(kernel))); '
                              'if (shader.name == "FFT/FFTSpectrum") GravityDispatches.Add(FloatParams[FFTCompute.ShaderIDs.s_Gravity]); }')
    print(f"Review #7 source: {args.ref or 'working tree'}", flush=True)
    dotnet = str(Path(args.dotnet).resolve())
    fft_result = run_project(dotnet, {
        "FFTCompute.cs": read("crest/Assets/Crest/Crest/Scripts/Shapes/FFT/FFTCompute.cs"),
        "Hashy.cs": read("crest/Assets/Crest/Crest/Scripts/Helpers/Hashy.cs"),
        "Program.cs": harness,
    })
    graphics = GRAPHICS_HARNESS.replace("__COMBINE_METHOD__", extract_method(
        read("crest/Assets/Crest/Crest/Scripts/LodData/LodDataMgrAnimWaves.cs"), "CombinePassPingPong"))
    graphics = graphics.replace("__SET_KEYWORD__", extract_method(
        read("crest/Assets/Crest/Crest/Scripts/Helpers/Helpers.cs"), "SetKeyword"))
    graphics_result = run_project(dotnet, {"Program.cs": graphics})
    return int(bool(fft_result or graphics_result))


if __name__ == "__main__":
    raise SystemExit(main())
