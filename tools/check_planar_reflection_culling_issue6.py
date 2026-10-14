#!/usr/bin/env python3
"""Issue 6: BIRP guards, URP 17.6 ordering, and executable request/pass contracts.

The C# checks compile the real request and culling pass against small managed API
substitutes. They verify recording/execution and request lifetime, not GPU output.
--ref checks the original reflection/depth-cache guards to demonstrate the bug.
"""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

from check_urp_regressions import body
from check_underwater_lifecycle import extract_method

ROOT = Path(__file__).resolve().parents[1]
SCRIPTS = "crest/Assets/Crest/Crest/Scripts/"
SOURCE_REF = None


def source(relative):
    if SOURCE_REF:
        text = subprocess.check_output(["git", "show", f"{SOURCE_REF}:{relative}"], cwd=ROOT).decode("utf-8-sig")
    else:
        text = (ROOT / relative).read_text(encoding="utf-8-sig")
    return text


class SettingsGuards(unittest.TestCase):
    def test_parent_feature_enqueues_both_passes_before_reflection_return(self):
        feature = source(SCRIPTS + "URP/CrestUniversalRendererFeature.cs").replace("public override ", "public ")
        create = extract_method(feature, "Create")
        self.assertIn("_reflectionInvert = new CrestReflectionCullPass(true);", create)
        self.assertIn("_reflectionRestore = new CrestReflectionCullPass(false);", create)
        add = extract_method(feature, "AddRenderPasses")
        guard = body(add[add.index("if (camera == CrestRenderRequest.ReflectionCamera)"):], "if")
        for pass_name in ("_reflectionInvert", "_reflectionRestore"):
            enqueue = f"renderer.EnqueuePass({pass_name});"
            self.assertIn(enqueue, guard)
            self.assertLess(add.index(enqueue), add.index("if (OceanRenderer.Instance == null"))

    def test_reflection_mutations_and_restore_are_birp_only(self):
        late = body(extract_method(source(SCRIPTS + "Reflection/OceanPlanarReflection.cs"), "LateUpdate"), "LateUpdate")
        self.assertIn("var builtIn = GraphicsSettings.currentRenderPipeline == null;", late)
        # Find the actual built-in guard, not one of LateUpdate's earlier guards.
        setup = body(late[late.index("if (builtIn)"):], "if")
        restore = late[late.index("finally"):]
        restore = body(restore[restore.index("if (builtIn)"):], "if")
        for text in (setup, restore):
            self.assertIn("GL.invertCulling =", text)
            self.assertIn("QualitySettings.pixelLightCount =", text)
            self.assertIn("QualitySettings.shadows =", text)
        for statement in ("GL.invertCulling =", "QualitySettings.pixelLightCount =", "QualitySettings.shadows ="):
            self.assertEqual(late.count(statement), setup.count(statement) + restore.count(statement))
        self.assertIn("!_disableShadows, invertCulling: true", late)

    def test_depth_shadow_distance_mutations_and_restore_are_birp_only(self):
        populate = body(extract_method(source(SCRIPTS + "LodData/OceanDepthCache.cs"), "PopulateCacheInternal"), "PopulateCacheInternal")
        self.assertIn("var builtIn = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline == null;", populate)
        setup = body(populate[populate.index("if (builtIn)"):], "if")
        restore = populate[populate.index("finally"):]
        restore = body(restore[restore.index("if (builtIn)"):], "if")
        self.assertIn("QualitySettings.shadowDistance = 0f;", setup)
        self.assertIn("QualitySettings.shadowDistance = oldShadowDistance;", restore)
        self.assertEqual(populate.count("QualitySettings.shadowDistance ="), 2)
        self.assertIn("shadows: false", populate)

    def test_installed_urp_shadow_and_prepass_order(self):
        packages = ROOT / "crest/Library/PackageCache"
        matches = [p for p in packages.glob("com.unity.render-pipelines.universal@*")
                   if json.loads((p / "package.json").read_text())["version"] == "17.6.0"]
        self.assertEqual(len(matches), 1, "URP 17.6.0 source is required for this check")
        runtime = matches[0] / "Runtime"
        renderer = (runtime / "UniversalRendererRenderGraph.cs").read_text(encoding="utf-8-sig")
        before = body(extract_method(renderer, "OnBeforeRendering"), "OnBeforeRendering")
        event = "RecordCustomRenderGraphPasses(renderGraph, RenderPassEvent.AfterRenderingShadows)"
        for shadow in ("m_MainLightShadowCasterPass.Render", "m_AdditionalLightsShadowCasterPass.Render"):
            self.assertLess(before.index(shadow), before.index(event))
        main = body(extract_method(renderer, "OnMainRendering"), "OnMainRendering")
        self.assertLess(main.index("RenderPassEvent.BeforeRenderingPrePasses"), main.index("m_DepthPrepass"))
        pipeline = (runtime / "UniversalRenderPipeline.cs").read_text(encoding="utf-8-sig")
        camera = body(extract_method(pipeline, "InitializeAdditionalCameraData"), "InitializeAdditionalCameraData")
        self.assertIn("additionalCameraData.renderShadows", camera)
        self.assertIn("settings.shadowDistance", camera)
        self.assertNotIn("QualitySettings.shadowDistance", camera)

    def test_global_state_pass_cannot_be_culled_in_installed_core(self):
        core = ROOT / "crest/Library/PackageCache"
        matches = [p for p in core.glob("com.unity.render-pipelines.core@*")
                   if json.loads((p / "package.json").read_text())["version"] == "17.6.0"]
        self.assertEqual(len(matches), 1)
        builders = (matches[0] / "Runtime/RenderGraph/RenderGraphBuilders.cs").read_text(encoding="utf-8-sig")
        allow = body(extract_method(builders, "AllowGlobalStateModification"), "AllowGlobalStateModification")
        guard = body(allow[allow.index("if (value)"):], "if")
        self.assertIn("AllowPassCulling(false);", guard)


def executable_checks():
    dotnet = shutil.which("dotnet")
    if not dotnet:
        raise RuntimeError(".NET 10 SDK is required")
    env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1")
    with tempfile.TemporaryDirectory(prefix="crest-reflection-issue6-") as folder:
        project = Path(folder)
        for name in ("CrestRenderRequest.cs", "CrestReflectionCullPass.cs"):
            shutil.copyfile(ROOT / SCRIPTS / "URP" / name, project / name)
        shutil.copyfile(ROOT / "tools/PlanarReflectionCullingIssue6Checks/Program.cs", project / "Program.cs")
        (project / "Checks.csproj").write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
            '<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>'
            '<LangVersion>9.0</LangVersion><EnableNETAnalyzers>false</EnableNETAnalyzers>'
            '</PropertyGroup></Project>\n', encoding="utf-8")
        (project / "NuGet.Config").write_text(
            '<configuration><packageSources><clear /></packageSources></configuration>\n', encoding="utf-8")
        build = subprocess.run([dotnet, "build", "--nologo", "--verbosity", "quiet"], cwd=project, env=env,
                               text=True, encoding="utf-8", errors="replace", stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        if build.returncode:
            print(build.stdout, end="")
            return build.returncode
        return subprocess.run([dotnet, str(project / "bin/Debug/net10.0/Checks.dll")], cwd=project, env=env).returncode


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ref")
    args = parser.parse_args()
    SOURCE_REF = args.ref
    result = unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(SettingsGuards))
    if not result.wasSuccessful():
        raise SystemExit(1)
    if not SOURCE_REF:
        raise SystemExit(executable_checks())
