#!/usr/bin/env python3
"""Exercise production input/wave/origin lifecycle code with managed substitutes.

Compiles complete ShapeWaves, RegisterLodDataInput, CrestSortedList and
ShiftingOrigin sources. The derived resource-family/cleanup members and legacy
Gerstner SetOrigin member are copied unchanged into small controlled fixtures.
Unity object destruction, scene lookup and spline generation are substitutes;
these checks do not execute Unity APIs, geometry generation, shaders or a GPU.
"""

import argparse
import os
from pathlib import Path
import shutil
import subprocess
import tempfile


ROOT = "crest/Assets/Crest/Crest/Scripts/"
SOURCES = (
    "Shapes/ShapeWaves.cs",
    "LodData/RegisterLodDataInput.cs",
    "Helpers/CrestSortedList.cs",
    "Helpers/ShiftingOrigin.cs",
)


def method(text, signature):
    start = text.index(signature)
    opening = text.index("{", start)
    depth = 1
    end = opening + 1
    while depth:
        depth += (text[end] == "{") - (text[end] == "}")
        end += 1
    return text[start:end]


def family_members(text):
    key = "protected override System.Type SharedResourcesType"
    member = ""
    if key in text:
        start = text.index(key)
        member = text[start:text.index(";", start) + 1] + "\n"
    return member + method(text, "protected override void DestroySharedResources()")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ref", help="read production files from this Git revision")
    parser.add_argument("--dotnet", default=shutil.which("dotnet"), help=".NET 10 SDK executable")
    args = parser.parse_args()
    if not args.dotnet:
        parser.error(".NET 10 SDK not found; pass --dotnet /path/to/dotnet")

    root = Path(__file__).resolve().parents[1]

    def source(path):
        path = ROOT + path
        return (subprocess.check_output(["git", "show", f"{args.ref}:{path}"], cwd=root).decode("utf-8-sig")
                if args.ref else (root / path).read_text(encoding="utf-8-sig"))

    with tempfile.TemporaryDirectory(prefix="crest-input-lifecycle-") as folder:
        project = Path(folder)
        for path in SOURCES:
            (project / Path(path).name).write_text(source(path), encoding="utf-8")
        derived = "using UnityEngine; namespace Crest {\n"
        derived += "public partial class ShapeFFT : ProbeWaves {\n" + family_members(source("Shapes/FFT/ShapeFFT.cs")) + "\n}\n"
        derived += "public partial class ShapeGerstner : ProbeWaves {\n" + family_members(source("Shapes/ShapeGerstner.cs")) + "\n}\n"
        derived += "public partial class ShapeGerstnerBatched : CustomMonoBehaviour, IShiftingOrigin {\n"
        derived += method(source("Shapes/ShapeGerstnerBatched.cs"), "public void SetOrigin(Vector3 newOrigin)") + "\n}\n}"
        (project / "ProductionMembers.cs").write_text(derived, encoding="utf-8")
        shutil.copyfile(root / "tools/InputLifecycleChecks/Program.cs", project / "Program.cs")
        (project / "InputLifecycleChecks.csproj").write_text(
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
        print(f"Production input/wave/origin lifecycle: {args.ref or 'working tree'}", flush=True)
        return subprocess.run([dotnet, str(project / "bin/Debug/net10.0/InputLifecycleChecks.dll")],
                              cwd=project, env=env).returncode


if __name__ == "__main__":
    raise SystemExit(main())
