#!/usr/bin/env python3
"""Compile selected Crest shader variants with DXC and real Unity/URP headers.

This checks HLSL syntax, signatures and code generation, not Unity import, shader
stripping, Render Graph execution, D3D11 driver compilation or rendered quality.
Supply the headers from the project's pinned Unity/URP versions; no stubs are used.
"""
import argparse
from concurrent.futures import ThreadPoolExecutor
import json
from pathlib import Path
import re
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]
CREST = Path("crest/Assets/Crest/Crest/Shaders")
EXAMPLES = Path("crest/Assets/Crest/Crest-Examples")


def programs(path):
    text = path.read_text(encoding="utf-8-sig")
    blocks = re.findall(r"\b(HLSLINCLUDE|HLSLPROGRAM|CGINCLUDE|CGPROGRAM)\b(.*?)\bEND(?:HLSL|CG)\b", text, re.S)
    shared = "\n".join(text for kind, text in blocks if kind.endswith("INCLUDE"))
    return [shared + "\n" + text for kind, text in blocks if kind.endswith("PROGRAM")]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dxc", required=True, type=Path, help="DXC executable")
    parser.add_argument("--urp-headers", required=True, type=Path, help="Directory containing Packages/com.unity.render-pipelines.*")
    parser.add_argument("--unity-includes", required=True, type=Path, help="Unity CGIncludes directory")
    parser.add_argument("--output", type=Path, help="Keep generated sources, DXIL and logs here")
    parser.add_argument("--ref", help="Compile this Git revision instead of the working tree")
    parser.add_argument("--jobs", type=int, default=4)
    args = parser.parse_args()
    for path in (args.dxc, args.urp_headers / "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl",
                 args.unity_includes / "HLSLSupport.cginc"):
        if not path.exists():
            parser.error(f"Missing required compiler/header: {path}")

    temporary = tempfile.TemporaryDirectory(prefix="crest-dxc-")
    output = (args.output or Path(temporary.name)).resolve()
    output.mkdir(parents=True, exist_ok=True)
    source_root = ROOT
    if args.ref:
        # Extract a complete text snapshot so baseline programs cannot accidentally
        # include changed working-tree helpers. Do not touch the user's checkout.
        source_root = output / "source"
        paths = subprocess.check_output(["git", "ls-tree", "-r", "--name-only", args.ref, str(CREST), str(EXAMPLES)], cwd=ROOT, text=True).splitlines()
        for name in paths:
            if Path(name).suffix not in (".shader", ".hlsl", ".compute", ".cginc"):
                continue
            target = source_root / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(subprocess.check_output(["git", "show", f"{args.ref}:{name}"], cwd=ROOT))

    features = ["_APPLYNORMALMAPPING_ON", "_COMPUTEDIRECTIONALLIGHT_ON", "_TRANSPARENCY_ON", "_CAUSTICS_ON",
                "_FOAM_ON", "_FOAM3DLIGHTING_ON", "_SHADOWS_ON", "_SUBSURFACESCATTERING_ON",
                "_SUBSURFACESHALLOWCOLOUR_ON", "_UNDERWATER_ON"]
    shadows = ["_MAIN_LIGHT_SHADOWS_CASCADE", "_SHADOWS_SOFT_HIGH"]
    local = ["_ADDITIONAL_LIGHTS", "_ADDITIONAL_LIGHT_SHADOWS", "_LIGHT_COOKIES", "_LIGHT_LAYERS"]
    configurations = {
        "minimal": [],
        "surface": features + shadows,
        "flow": features + shadows + ["_FLOW_ON", "CREST_FLOATING_ORIGIN", "_DIRECTIONALLIGHTVARYROUGHNESS_ON"],
        "local-lights": features + shadows + local,
        "vertex-lights": features + shadows + ["_ADDITIONAL_LIGHTS_VERTEX"],
        "clustered-probes": features + shadows + local + ["_CLUSTER_LIGHT_LOOP", "_REFLECTION_PROBE_ATLAS", "_REFLECTION_PROBE_BLENDING", "_REFLECTION_PROBE_BOX_PROJECTION"],
        "screen-shadows": features + ["_MAIN_LIGHT_SHADOWS_SCREEN", "_SHADOWS_SOFT_MEDIUM"],
        "reflection-overrides": features + shadows + ["_PLANARREFLECTIONS_ON", "_OVERRIDEREFLECTIONCUBEMAP_ON"],
        "planar-probes": features + shadows + ["_PLANARREFLECTIONS_ON"],
        "planar-flow": features + shadows + ["_PLANARREFLECTIONS_ON", "_FLOW_ON", "CREST_FLOATING_ORIGIN"],
        "planar-stereo": features + shadows + ["_PLANARREFLECTIONS_ON", "STEREO_INSTANCING_ON"],
        "procedural-sky": features + ["_PROCEDURALSKY_ON"],
        "early-underwater": features + shadows + ["CREST_UNDERWATER_BEFORE_TRANSPARENT"],
        "orthographic": features + shadows + ["_PROJECTION_ORTHOGRAPHIC"],
        "perspective": features + shadows + ["_PROJECTION_PERSPECTIVE"],
        "volume": features + shadows + ["CREST_WATER_VOLUME", "CREST_UNDERWATER_BEFORE_TRANSPARENT"],
        "clipping": features + shadows + ["_CLIPSURFACE_ON", "_CLIPUNDERTERRAIN_ON", "_ALBEDO_ON", "FOG_LINEAR"],
        "stereo": features + shadows + ["STEREO_INSTANCING_ON"],
    }
    cases = []

    def add_shader(path, settings):
        full_path = source_root / path
        for index, program in enumerate(programs(full_path)):
            for label, keywords in settings.items():
                for stage, profile, macro in (("vertex", "vs_6_0", "SHADER_STAGE_VERTEX"), ("fragment", "ps_6_0", "SHADER_STAGE_FRAGMENT")):
                    entry = re.search(r"#pragma\s+" + stage + r"\s+(\w+)", program)
                    if entry is not None:
                        name = f"{path.stem}-{index}-{label}-{stage}"
                        cases.append((name, program, full_path.parent, profile, entry[1], keywords + [macro]))

    add_shader(CREST / "Ocean.shader", configurations)
    # The fullscreen effect does not declare surface-only keywords such as
    # _TRANSPARENCY_ON. Enabling those would construct impossible Unity variants.
    underwater_features = ["_CAUSTICS_ON", "_SHADOWS_ON", "_SUBSURFACESCATTERING_ON", "_SUBSURFACESHALLOWCOLOUR_ON"]
    underwater_settings = {"minimal": [], "caustics": underwater_features + shadows + ["CREST_FLOATING_ORIGIN", "CREST_MENISCUS"],
                           "early": underwater_features + shadows + ["CREST_UNDERWATER_BEFORE_TRANSPARENT"]}
    add_shader(CREST / "Underwater/Resources/UnderwaterEffect.shader", underwater_settings)
    add_shader(CREST / "Underwater/UnderwaterCurtain.shader", {"minimal": [], "caustics": features + shadows})
    for path in (EXAMPLES / "Examples/Shaders/Masked.shader", EXAMPLES / "Shared/Shaders/ExampleUnderwaterTransparentSurfaceShader.shader"):
        add_shader(path, {"minimal": [], "lighting": shadows + local + ["_SUBSURFACESCATTERING_ON", "_CAUSTICS_ON"]})
    # Cover height reconstruction and each consumer of the revised shallow-water
    # precision path, including both users of the shared Gerstner implementation.
    inputs = CREST / "OceanInputs"
    add_shader(inputs / "Resources/CopyDepthIntoCache.shader", {"minimal": []})
    add_shader(inputs / "AnimWavesSpectrum.shader", {"minimal": [], "attenuated-blend": ["_ATTENUATIONAFFECTSBLENDWEIGHT_ON"]})
    for path in (inputs / "Resources/AnimWavesGerstner.shader", inputs / "Resources/AnimWavesGerstnerGeometry.shader"):
        add_shader(path, {"minimal": []})
    add_shader(inputs / "Resources/AnimWavesGerstnerBatch.shader", {"minimal": [], "directed": ["CREST_DIRECT_TOWARDS_POINT_INTERNAL"]})
    add_shader(inputs / "AnimWavesGerstnerBatchGeometry.shader", {"minimal": [], "directed-feather": ["CREST_DIRECT_TOWARDS_POINT_INTERNAL", "_WEIGHTFROMVERTEXCOLOURRED_ON", "_FEATHERATUVEXTENTS_ON"]})
    for path in (inputs / "AnimWavesRemoveGeometry.shader", inputs / "AnimWavesSetHeightToGeometry.shader",
                 inputs / "Resources/AnimWavesSetBaseWaterHeightUsingGeometry.shader",
                 inputs / "Resources/ClipSurfaceConvexHull.shader", inputs / "Resources/ClipSurfaceSignedDistance.shader",
                 CREST / "OceanSurfaceAlpha.shader", CREST / "Underwater/Resources/UnderwaterMask.shader"):
        add_shader(path, {"minimal": []})
    add_shader(CREST / "Resources/UpdateShadow.shader", {"minimal": [], "cascade": shadows, "screen-shadow": ["_MAIN_LIGHT_SHADOWS_SCREEN"]})
    compute_path = source_root / CREST / "Resources/UpdateFoam.compute"
    cases.append(("UpdateFoam", compute_path.read_text(encoding="utf-8-sig"), compute_path.parent,
                  "cs_6_0", "UpdateFoam", ["SHADER_STAGE_COMPUTE"]))

    def compile_case(case):
        name, program, includes, profile, entry, keywords = case
        source = output / (name + ".hlsl")
        source.write_text(program, encoding="utf-8")
        command = [str(args.dxc.resolve()), "-T", profile, "-E", entry, "-HV", "2016", "-Gec",
                   "-I", str(includes), "-I", str(args.urp_headers.resolve()), "-I", str(args.unity_includes.resolve()),
                   "-D", "SHADER_API_D3D11=1", "-D", "SHADER_TARGET=45", "-D", "UNITY_VERSION=600062",
                   "-D", "UNITY_COMPILER_DXC=1"]
        for keyword in keywords:
            command += ["-D", keyword + "=1"]
        command += [str(source), "-Fo", str(output / (name + ".dxil"))]
        result = subprocess.run(command, capture_output=True, text=True)
        log = result.stdout + result.stderr
        (output / (name + ".log")).write_text(log, encoding="utf-8")
        return {"case": name, "exit_code": result.returncode, "warnings": len(re.findall(r"\bwarning:", log)),
                "errors": log if result.returncode else ""}

    with ThreadPoolExecutor(max_workers=max(args.jobs, 1)) as pool:
        results = list(pool.map(compile_case, cases))
    failed = [result for result in results if result["exit_code"]]
    report = {"ref": args.ref or "working-tree", "passed": len(results) - len(failed), "total": len(results), "results": results}
    (output / "results.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(f"Offline DXC: {report['passed']}/{report['total']} selected shader stages/variants passed.")
    for result in failed:
        print(f"FAIL {result['case']}\n{result['errors']}")
    if args.output:
        print(f"Generated sources, DXIL and diagnostics: {output}")
    print("No Unity import, player build, Render Graph execution or GPU image validation performed.")
    temporary.cleanup()
    return bool(failed)


if __name__ == "__main__":
    raise SystemExit(main())
