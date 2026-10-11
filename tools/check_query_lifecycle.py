#!/usr/bin/env python3
"""Execute production QueryBase.cs with deterministic, managed Unity API substitutes.

Requires the .NET 10 SDK, but no Unity installation or NuGet packages. The substitutes
model queued readbacks and expose their completion order; they do not execute GPU
work, validate Unity assembly binding, or reproduce a driver/window scheduling bug.
Use --ref to run the identical behavioral tests against a previous git revision.
"""

import argparse
import os
from pathlib import Path
import shutil
import subprocess
import tempfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ref", help="read production QueryBase.cs from this git revision")
    parser.add_argument("--dotnet", default=shutil.which("dotnet"), help="path to the .NET 10 SDK executable")
    args = parser.parse_args()
    if not args.dotnet:
        parser.error(".NET 10 SDK not found; pass --dotnet /path/to/dotnet")

    root = Path(__file__).resolve().parents[1]
    source_path = "crest/Assets/Crest/Crest/Scripts/Collision/QueryBase.cs"
    source = (
        subprocess.check_output(["git", "show", f"{args.ref}:{source_path}"], cwd=root)
        if args.ref else (root / source_path).read_bytes()
    )
    dotnet = str(Path(args.dotnet).resolve())
    with tempfile.TemporaryDirectory(prefix="crest-query-lifecycle-") as folder:
        project = Path(folder)
        (project / "QueryBase.cs").write_bytes(source)
        shutil.copyfile(root / "tools/QueryLifecycleChecks/Program.cs", project / "Program.cs")
        (project / "QueryLifecycleChecks.csproj").write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
            '<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>'
            '<LangVersion>9.0</LangVersion><EnableNETAnalyzers>false</EnableNETAnalyzers>'
            '</PropertyGroup></Project>\n', encoding="utf-8"
        )
        (project / "NuGet.Config").write_text(
            '<configuration><packageSources><clear /></packageSources></configuration>\n',
            encoding="utf-8"
        )
        env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1")
        build = subprocess.run(
            [dotnet, "build", "--nologo", "--verbosity", "quiet"],
            cwd=project, env=env, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
        )
        if build.returncode:
            print(build.stdout, end="")
            return build.returncode
        print(f"Production QueryBase: {args.ref or 'working tree'}", flush=True)
        return subprocess.run(
            [dotnet, str(project / "bin/Debug/net10.0/QueryLifecycleChecks.dll")],
            cwd=project, env=env,
        ).returncode


if __name__ == "__main__":
    raise SystemExit(main())
