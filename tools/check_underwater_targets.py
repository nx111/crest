#!/usr/bin/env python3
"""Execute production water-volume target setup against descriptor transitions.

SetUpVolumeTextures and its production update predicate are extracted verbatim
and compiled with managed RenderTexture/RTHandle identity substitutes. This
checks descriptor initialization and reuse, not Unity APIs, allocation formats,
RenderGraph validation or GPU execution. --ref demonstrates the old behavior.
Requires .NET 10; no Unity installation or NuGet packages are used.
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
    parser.add_argument("--ref", help="read production methods from this git revision")
    parser.add_argument("--dotnet", default=shutil.which("dotnet"))
    args = parser.parse_args()
    if not args.dotnet:
        parser.error(".NET 10 SDK not found; pass --dotnet /path/to/dotnet")
    root = Path(__file__).resolve().parents[1]

    def read(relative):
        data = subprocess.check_output(["git", "show", f"{args.ref}:{relative}"], cwd=root) if args.ref else (root / relative).read_bytes()
        return data.decode("utf-8-sig")

    mask = read("crest/Assets/Crest/Crest/Scripts/Underwater/UnderwaterRenderer.Mask.cs")
    helpers = read("crest/Assets/Crest/Crest/Scripts/Helpers/Helpers.cs")
    production = "using UnityEngine;\nnamespace Crest { public partial class UnderwaterRenderer {\n"
    production += extract_method(mask, "SetUpVolumeTextures")
    production += "\n} public static class Helpers {\n"
    production += extract_method(helpers, "RenderTargetTextureNeedsUpdating") + "\n} }\n"

    env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1")
    dotnet = str(Path(args.dotnet).resolve())
    with tempfile.TemporaryDirectory(prefix="crest-underwater-targets-") as folder:
        project = Path(folder)
        (project / "ProductionMethods.cs").write_text(production, encoding="utf-8")
        shutil.copyfile(root / "tools/UnderwaterTargetChecks/Program.cs", project / "Program.cs")
        (project / "UnderwaterTargetChecks.csproj").write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
            '<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>'
            '<LangVersion>9.0</LangVersion><EnableNETAnalyzers>false</EnableNETAnalyzers>'
            '</PropertyGroup></Project>\n', encoding="utf-8",
        )
        (project / "NuGet.Config").write_text(
            '<configuration><packageSources><clear /></packageSources></configuration>\n', encoding="utf-8",
        )
        build = subprocess.run(
            [dotnet, "build", "--nologo", "--verbosity", "quiet"], cwd=project, env=env,
            text=True, encoding="utf-8", errors="replace", stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
        )
        if build.returncode:
            print(build.stdout, end="")
            return build.returncode
        print(f"Production underwater volume targets: {args.ref or 'working tree'}", flush=True)
        return subprocess.run(
            [dotnet, str(project / "bin/Debug/net10.0/UnderwaterTargetChecks.dll")], cwd=project, env=env,
        ).returncode


if __name__ == "__main__":
    raise SystemExit(main())
