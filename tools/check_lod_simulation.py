#!/usr/bin/env python3
"""Execute production LOD simulation management with managed texture/command substitutes.

The checks exercise fixed-step scheduling, frame history and substep ping-pong.
They do not execute Unity, shader arithmetic, resource allocation on a driver, or
a GPU. Use --ref for an identical comparison against a prior source revision.
"""

import argparse
import os
from pathlib import Path
import shutil
import subprocess
import tempfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ref", help="read production managers from this Git revision")
    parser.add_argument("--dotnet", default=shutil.which("dotnet"), help=".NET 10 SDK executable")
    args = parser.parse_args()
    if not args.dotnet:
        parser.error(".NET 10 SDK not found; pass --dotnet /path/to/dotnet")

    root = Path(__file__).resolve().parents[1]
    with tempfile.TemporaryDirectory(prefix="crest-lod-simulation-") as folder:
        project = Path(folder)
        for name in ("LodDataMgr", "LodDataMgrPersistent", "LodDataMgrDynWaves", "LodDataMgrFoam", "LodDataMgrFlow"):
            path = f"crest/Assets/Crest/Crest/Scripts/LodData/{name}.cs"
            source = (subprocess.check_output(["git", "show", f"{args.ref}:{path}"], cwd=root)
                      if args.ref else (root / path).read_bytes())
            (project / f"{name}.cs").write_bytes(source)
        shutil.copyfile(root / "tools/LodSimulationChecks/Program.cs", project / "Program.cs")
        (project / "Checks.csproj").write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
            '<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>'
            '<LangVersion>9.0</LangVersion><EnableNETAnalyzers>false</EnableNETAnalyzers>'
            '</PropertyGroup></Project>\n', encoding="utf-8")
        (project / "NuGet.Config").write_text(
            '<configuration><packageSources><clear /></packageSources></configuration>\n', encoding="utf-8")
        env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1")
        dotnet = str(Path(args.dotnet).resolve())
        build = subprocess.run([dotnet, "build", "--nologo", "--verbosity", "quiet"], cwd=project,
                               env=env, text=True, encoding="utf-8", errors="replace",
                               stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        if build.returncode:
            print(build.stdout, end="")
            return build.returncode
        print(f"Production LOD simulation managers: {args.ref or 'working tree'}", flush=True)
        return subprocess.run([dotnet, str(project / "bin/Debug/net10.0/Checks.dll")], cwd=project, env=env).returncode


if __name__ == "__main__":
    raise SystemExit(main())
