#!/usr/bin/env python3
"""Execute selected production UnderwaterRenderer lifecycle methods without Unity.

The method bodies are extracted verbatim from Crest, then compiled with managed
camera/resource substitutes in Player and Editor configurations. This checks state
transitions and ownership, not Unity assembly binding, native allocations or GPU
rendering. --ref runs the same behavioral checks against an older git revision.
Requires .NET 10; no NuGet packages or Unity installation are required.
"""

import argparse
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile


def extract_method(source, name, optional=False):
    # Mask comments and literals without changing offsets so braces inside them
    # cannot truncate a method. The selected lifecycle methods use plain literals.
    masked = re.sub(
        r'//[^\n]*|/\*[\s\S]*?\*/|"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'',
        lambda match: re.sub(r"[^\n]", " ", match.group()), source,
    )
    match = re.search(
        rf"^[ \t]*(?:(?:internal|public|private|static)\s+)*[\w<>]+\s+{name}\s*\([^;{{}}]*\)\s*{{",
        masked, re.MULTILINE,
    )
    if match is None:
        if optional:
            return ""
        raise ValueError(f"Production method not found: {name}")
    start = match.end() - 1
    depth = 0
    for end in range(start, len(masked)):
        depth += (masked[end] == "{") - (masked[end] == "}")
        if depth == 0:
            return source[match.start():end + 1]
    raise ValueError(f"Unterminated production method: {name}")


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

    source = read("crest/Assets/Crest/Crest/Scripts/Underwater/UnderwaterRenderer.cs")
    mask = read("crest/Assets/Crest/Crest/Scripts/Underwater/UnderwaterRenderer.Mask.cs")
    urp = read("crest/Assets/Crest/Crest/Scripts/URP/UnderwaterRenderer.URP.cs")
    methods = [extract_method(source, name) for name in ("UpdateInstance", "Get")]
    methods += [extract_method(source, name, optional=True) for name in ("OnDestroy", "DestroyMaskTexture")]
    methods += [extract_method(mask, "OnDisableMask"), extract_method(urp, "ReleaseURPHandles")]
    production = "using UnityEngine;\nusing UnityEngine.Rendering;\nnamespace Crest { public partial class UnderwaterRenderer {\n" + "\n\n".join(methods) + "\n} }\n"

    result = 0
    dotnet = str(Path(args.dotnet).resolve())
    env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1")
    with tempfile.TemporaryDirectory(prefix="crest-underwater-lifecycle-") as folder:
        for configuration in ("Player", "Editor"):
            project = Path(folder) / configuration
            project.mkdir()
            (project / "ProductionMethods.cs").write_text(production, encoding="utf-8")
            shutil.copyfile(root / "tools/UnderwaterLifecycleChecks/Program.cs", project / "Program.cs")
            defines = "UNITY_EDITOR" if configuration == "Editor" else ""
            (project / "UnderwaterLifecycleChecks.csproj").write_text(
                '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
                '<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>'
                '<LangVersion>9.0</LangVersion><EnableNETAnalyzers>false</EnableNETAnalyzers>'
                f'<DefineConstants>{defines}</DefineConstants>'
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
            print(f"Production underwater lifecycle: {args.ref or 'working tree'} / {configuration}", flush=True)
            run = subprocess.run([dotnet, str(project / "bin/Debug/net10.0/UnderwaterLifecycleChecks.dll")], cwd=project, env=env)
            result |= run.returncode
    return result


if __name__ == "__main__":
    raise SystemExit(main())
