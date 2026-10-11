#!/usr/bin/env python3
"""Execute production LOD resource recovery, frame publication and cleanup offline.

The complete production LOD managers and BuildCommandBuffer are compiled. Ocean
RunUpdate, Rebuild, CleanUp, validation and frame-publication methods are extracted
verbatim. OnEnable's production clock/fast-enable prefix precedes a managed scene
initialization substitute; its fresh-buffer flag reset is copied from the source.
The substitutes model ownership, failed creation and uploaded arrays, not Unity
native allocations, renderer callbacks or GPU execution. --ref compares old code.
"""

import argparse
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

from check_underwater_lifecycle import extract_method


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ref", help="read production code from this Git revision")
    parser.add_argument("--dotnet", default=shutil.which("dotnet"), help=".NET 10 SDK executable")
    args = parser.parse_args()
    if not args.dotnet:
        parser.error(".NET 10 SDK not found; pass --dotnet /path/to/dotnet")
    root = Path(__file__).resolve().parents[1]

    def read(relative):
        data = (subprocess.check_output(["git", "show", f"{args.ref}:{relative}"], cwd=root)
                if args.ref else (root / relative).read_bytes())
        return data.decode("utf-8-sig")

    ocean = read("crest/Assets/Crest/Crest/Scripts/OceanRenderer.cs")
    methods = [extract_method(ocean, name) for name in (
        "RunUpdate", "Rebuild", "CleanUp", "WritePerFrameMaterialParams",
        "WritePerCascadeInstanceData", "PushTimeProvider", "PopTimeProvider",
    )]
    methods += [extract_method(ocean, name, optional=True) for name in (
        "TryGetInvalidLodData", "ValidateLodDataTextures",
    )]
    on_enable = extract_method(ocean, "OnEnable")
    # Keep the complete early-return and clock selection logic. Unity scene and
    # buffer construction are performed by the fixture, with the real managers.
    clock_prefix = on_enable[:on_enable.index("            if (!_primaryLight")]
    flag_reset = "            _hasWrittenPerFrameData = false;" if "_hasWrittenPerFrameData = false;" in on_enable else ""
    methods.append(clock_prefix + flag_reset + "\n            InitializeSceneSubstitute();\n        }")
    production = "using System;\nusing UnityEngine;\nusing UnityEngine.Rendering;\nnamespace Crest { public partial class OceanRenderer {\n"
    production += "\n\n".join(methods) + "\n} public partial class LodTransform {\n"
    production += extract_method(read("crest/Assets/Crest/Crest/Scripts/LodData/LodTransform.cs"), "WriteCascadeParams") + "\n} }\n"

    common = (root / "tools/LodSimulationChecks/Program.cs").read_text(encoding="utf-8")
    # Share the managed API substitutes, without the fixed-step program entry.
    substitutes = common[:common.index("static class Program")] + common[common.index("namespace UnityEngine.Experimental.Rendering"):]

    env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1")
    dotnet = str(Path(args.dotnet).resolve())
    with tempfile.TemporaryDirectory(prefix="crest-lod-resources-") as folder:
        project = Path(folder)
        for name in ("LodDataMgr", "LodDataMgrPersistent", "LodDataMgrDynWaves", "LodDataMgrFoam", "LodDataMgrFlow", "LodDataMgrAnimWaves"):
            (project / f"{name}.cs").write_text(read(f"crest/Assets/Crest/Crest/Scripts/LodData/{name}.cs"), encoding="utf-8")
        (project / "BuildCommandBuffer.cs").write_text(read("crest/Assets/Crest/Crest/Scripts/BuildCommandBuffer.cs"), encoding="utf-8")
        (project / "ProductionMethods.cs").write_text(production, encoding="utf-8")
        (project / "Substitutes.cs").write_text(substitutes, encoding="utf-8")
        shutil.copyfile(root / "tools/LodResourceChecks/Program.cs", project / "Program.cs")
        (project / "Checks.csproj").write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
            '<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>'
            '<LangVersion>9.0</LangVersion><EnableNETAnalyzers>false</EnableNETAnalyzers>'
            '<DefineConstants>LOD_RESOURCE_CHECKS</DefineConstants>'
            '</PropertyGroup></Project>\n', encoding="utf-8")
        (project / "NuGet.Config").write_text('<configuration><packageSources><clear /></packageSources></configuration>\n', encoding="utf-8")
        build = subprocess.run([dotnet, "build", "--nologo", "--verbosity", "quiet"], cwd=project,
                               env=env, text=True, encoding="utf-8", errors="replace",
                               stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        if build.returncode:
            print(build.stdout, end="")
            return build.returncode
        print(f"Production LOD resource lifecycle: {args.ref or 'working tree'}", flush=True)
        return subprocess.run([dotnet, str(project / "bin/Debug/net10.0/Checks.dll")], cwd=project, env=env).returncode


if __name__ == "__main__":
    raise SystemExit(main())
