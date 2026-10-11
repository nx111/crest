#!/usr/bin/env python3
"""Execute production CollProviderBakedFFT with managed NativeArray/job substitutes.

This isolates registration, deferred jobs, output spans and disposal. The sampler
is a known affine field, not an FFT implementation. Use check_fft_queries.py for
the production sampling equations. No Unity, Burst, or worker threads are run.
"""

import argparse
import os
from pathlib import Path
import shutil
import subprocess
import tempfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ref", help="Read production files from this Git revision")
    parser.add_argument("--dotnet", default=shutil.which("dotnet"))
    args = parser.parse_args()
    if not args.dotnet:
        parser.error(".NET 10 SDK not found; pass --dotnet /path/to/dotnet")
    root = Path(__file__).resolve().parents[1]
    dotnet = str(Path(args.dotnet).resolve())
    with tempfile.TemporaryDirectory(prefix="crest-baked-query-") as folder:
        project = Path(folder)
        for name in ("CollProviderBakedFFT.cs", "CollProvider.cs"):
            path = "crest/Assets/Crest/Crest/Scripts/Collision/" + name
            source = subprocess.check_output(["git", "show", f"{args.ref}:{path}"], cwd=root) if args.ref else (root / path).read_bytes()
            (project / name).write_bytes(source)
        shutil.copyfile(root / "tools/BakedQueryLifecycleChecks/Program.cs", project / "Program.cs")
        (project / "Checks.csproj").write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
            '<TargetFramework>net10.0</TargetFramework><LangVersion>9.0</LangVersion>'
            '<DefineConstants>CREST_UNITY_MATHEMATICS</DefineConstants>'
            '<EnableNETAnalyzers>false</EnableNETAnalyzers></PropertyGroup></Project>\n', encoding="utf-8")
        (project / "NuGet.Config").write_text('<configuration><packageSources><clear /></packageSources></configuration>\n', encoding="utf-8")
        env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1")
        build = subprocess.run([dotnet, "build", "--nologo", "--verbosity", "quiet"], cwd=project, env=env,
                               text=True, encoding="utf-8", errors="replace", stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        if build.returncode:
            print(build.stdout, end="")
            return build.returncode
        print(f"Production CollProviderBakedFFT: {args.ref or 'working tree'}", flush=True)
        return subprocess.run([dotnet, str(project / "bin/Debug/net10.0/Checks.dll")], cwd=project, env=env).returncode


if __name__ == "__main__":
    raise SystemExit(main())
