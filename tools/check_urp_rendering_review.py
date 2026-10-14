#!/usr/bin/env python3
"""Offline URP review checks using extracted production C# and managed substitutes.

Covers pass inputs, camera callback subscriptions, texture declarations, tile
binding, Player overlay transitions and the HLSL fog classification prefix.
The fog prefix is translated to C# types/preprocessor conditions and samples
scalar fixtures; it stops before blending. No Unity compilation, RenderGraph/GPU
execution, rendering, fog blending or native RTHandle validation is done.
Use --ref f405dc301 to run identical assertions against the review baseline.
"""

import argparse
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile

from check_underwater_lifecycle import extract_method


def method(source, name, optional=False):
    # Extend the shared extractor's declaration search without changing the
    # returned production signature/body, including override and protected.
    searchable = re.sub(r"\b(?:override|protected|virtual)\b", lambda m: " " * len(m[0]), source)
    extracted = extract_method(searchable, name, optional=optional)
    if not extracted:
        return ""
    start = searchable.index(extracted)
    return source[start:start + len(extracted)]


def constructor(source, name):
    masked = re.sub(r'//[^\n]*|/\*[\s\S]*?\*/|"(?:\\.|[^"\\])*"',
                    lambda m: re.sub(r"[^\n]", " ", m[0]), source)
    match = re.search(rf"public\s+{name}\s*\([^{{;]*\)\s*{{", masked)
    if match is None:
        raise ValueError(f"Missing constructor {name}")
    depth = 0
    for end in range(match.end() - 1, len(masked)):
        depth += (masked[end] == "{") - (masked[end] == "}")
        if depth == 0:
            return source[match.start():end + 1]
    raise ValueError(f"Unterminated constructor {name}")


def shadow_contract(source):
    clean = lambda text: re.sub(r"//[^\n]*|/\*[\s\S]*?\*/", "", text)
    update = clean(method(source, "UpdateLodData"))
    build = clean(method(source, "BuildCommandBuffer"))
    assert len(re.findall(r"\bFlipBuffers\s*\(", update)) == 1, "shadow update must flip exactly once"
    assert "BuildCommandBuffer(" not in update, "shadow update must not record an extra render pass"
    assert not re.search(r"\bFlipBuffers\s*\(|\bbase\.BuildCommandBuffer\s*\(|\bbuffer\.Clear\s*\(", build), \
        "shadow render pass must neither flip nor clear caller commands"


def fog_prefix(source, constants):
    start = source.index("bool CrestApplyUnderwaterFog(const ")
    end = source.index("half3 lightColor", start)
    prefix = source[start:end]
    prefix = prefix.replace("const ", "").replace("inout half3 color", "ref Vec3 color")
    prefix = re.sub(r"\b(?:float3|half3)\b", "Vec3", prefix)
    prefix = re.sub(r"\bfloat2\b", "Vec2", prefix)
    prefix = re.sub(r"\bhalf\b", "float", prefix)
    prefix = prefix.replace("#if defined(CREST_URP)", "#if true")
    no_fog = re.search(r"(?m)^\s*#define\s+CREST_MASK_NO_FOG\s+([-\d.]+)f?", constants)
    if no_fog is None:
        raise ValueError("Missing production CREST_MASK_NO_FOG constant")
    production = "namespace Crest { public static partial class FogGuards {\n"
    production += f"const float CREST_MASK_NO_FOG = {no_fog[1]}f;\n"
    for name, volume, portal in (("FullScreen", False, False), ("Volume", True, False), ("Portal", True, True)):
        variant = prefix.replace("bool CrestApplyUnderwaterFog", "public static bool " + name)
        variant = re.sub(r"#if CREST_WATER_VOLUME_2D\b", "#if " + str(portal).lower(), variant)
        variant = re.sub(r"#if CREST_WATER_VOLUME\b", "#if " + str(volume).lower(), variant)
        # A sentinel records reaching the omitted blending body. Refraction test
        # cases must all return within the real guard prefix without reaching it.
        production += variant + "ReachedBlending = true; return true;\n}\n"
    return production + "} }\n"


def example_contracts(setup, asset):
    create = method(setup, "CreatePipeline")
    validate = method(setup, "ValidatePipeline")
    return [
        ("example asset does not force global opaque color", bool(re.search(r"(?m)^\s*m_RequireOpaqueTexture:\s*0\s*$", asset))),
        ("example creation disables global opaque color", bool(re.search(r"pipeline\.supportsCameraOpaqueTexture\s*=\s*false\s*;", create))),
        ("validation requires depth without requiring opaque color", "supportsCameraOpaqueTexture" not in validate
            and bool(re.search(r"if\s*\(\s*!pipeline\.supportsCameraDepthTexture\s*\)", validate))),
    ]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ref", help="read production source from this git revision")
    parser.add_argument("--dotnet", default=shutil.which("dotnet"))
    args = parser.parse_args()
    if not args.dotnet:
        parser.error(".NET 10 SDK not found; pass --dotnet /path/to/dotnet")
    root = Path(__file__).resolve().parents[1]

    def read(relative):
        data = (subprocess.check_output(["git", "show", f"{args.ref}:{relative}"], cwd=root)
                if args.ref else (root / relative).read_bytes())
        return data.decode("utf-8-sig")

    scripts = "crest/Assets/Crest/Crest/Scripts/"
    feature = read(scripts + "URP/CrestUniversalRendererFeature.cs")
    callback = read("crest/Assets/Crest/Crest-Examples/Examples/Scripts/CustomPassForCamera.cs")
    urp = read(scripts + "URP/UnderwaterRenderer.URP.cs")
    underwater = read(scripts + "Underwater/UnderwaterRenderer.cs")
    mask = read(scripts + "Underwater/UnderwaterRenderer.Mask.cs")
    helpers = read(scripts + "Helpers/Helpers.cs")
    production = """using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;
namespace Crest { public partial class CrestUniversalRendererFeature {
"""
    production += method(feature, "Create") + "\n" + method(feature, "AddRenderPasses")
    production += "\npartial class CrestPass {\n" + constructor(feature, "CrestPass") + "\n} }\n"
    production += "public partial class UnderwaterRenderer {\n"
    production += method(urp, "DeclareURPTextures") + "\n" + method(underwater, "LateUpdate")
    for name in ("SetUpMaskTextures", "GetMaskColorRTD", "GetMaskDepthRTD", "SetUpVolumeTextures"):
        production += "\n" + method(mask, name)
    production += "\n} public static partial class Helpers {\n" + method(helpers, "RenderTargetTextureNeedsUpdating") + "\n}\n"
    production += "public partial class OceanChunkRenderer {\n" + method(read(scripts + "OceanChunkRenderer.cs"), "BeginCameraRendering") + "\n} }\n"
    production += "namespace Crest.Examples { public abstract partial class CustomPassForCameraBase {\n"
    for name in ("OnEnable", "SubscribeURP", "UnsubscribeURP", "LateUpdate", "ExecuteURP", "OnURPBeforeScene", "OnDisable"):
        production += method(callback, name, optional=True) + "\n"
    production += "} }\n"
    production += fog_prefix(read("crest/Assets/Crest/Crest/Shaders/Underwater/UnderwaterEffectIncludes.hlsl"),
                             read("crest/Assets/Crest/Crest/Shaders/OceanConstants.hlsl"))

    print(f"URP rendering review: {args.ref or 'working tree'} / Player (UNITY_EDITOR undefined)", flush=True)
    contracts = example_contracts(read(scripts + "Editor/CrestURPSetup.cs"), read("crest/Assets/CrestURPSettings/CrestPipeline.asset"))
    try:
        shadow_contract(read(scripts + "LodData/Shadows/LodDataMgrShadow.cs"))
        contracts.append(("shadow #1: UpdateLodData owns the flip; BuildCommandBuffer neither clears nor flips", True))
    except AssertionError as error:
        contracts.append(("shadow #1 source contract: " + str(error), False))
    for name, success in contracts:
        print(("PASS: " if success else "FAIL: ") + name, flush=True)
    contract_failures = sum(not success for _, success in contracts)
    print(f"Source/asset contracts: {len(contracts) - contract_failures}/{len(contracts)} passed", flush=True)
    with tempfile.TemporaryDirectory(prefix="crest-urp-rendering-review-") as folder:
        project = Path(folder)
        (project / "ProductionMethods.cs").write_text(production, encoding="utf-8")
        shutil.copyfile(root / "tools/UrpRenderingReviewChecks/Program.cs", project / "Program.cs")
        (project / "Checks.csproj").write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
            '<TargetFramework>net10.0</TargetFramework><LangVersion>9.0</LangVersion>'
            '<EnableNETAnalyzers>false</EnableNETAnalyzers></PropertyGroup></Project>', encoding="utf-8")
        (project / "NuGet.Config").write_text(
            '<configuration><packageSources><clear /></packageSources></configuration>', encoding="utf-8")
        env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1")
        dotnet = str(Path(args.dotnet).resolve())
        build = subprocess.run([dotnet, "build", "--nologo", "--verbosity", "quiet"], cwd=project, env=env,
                               text=True, encoding="utf-8", errors="replace", stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        if build.returncode:
            print(build.stdout, end="")
            return build.returncode
        result = subprocess.run([dotnet, str(project / "bin/Debug/net10.0/Checks.dll")], cwd=project, env=env)
        return int(bool(result.returncode or contract_failures))


if __name__ == "__main__":
    raise SystemExit(main())
