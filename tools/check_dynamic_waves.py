#!/usr/bin/env python3
"""Run the production dynamic-wave kernel with CPU HLSL type/sampling adapters.

Only include directives and compute entry-point metadata are removed; arithmetic
and branches are compiled from the actual kernel and coordinate helper sources.
This is a boundary/time-step regression, not GPU filtering, half precision, Unity
integration, or performance validation. --ref tests a previous git revision.
"""

import argparse
from pathlib import Path
import re
import shutil
import subprocess
import tempfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ref", help="read shader sources from this git revision")
    parser.add_argument("--cxx", default=shutil.which("c++"), help="C++17 compiler for the CPU adapter")
    args = parser.parse_args()
    if not args.cxx:
        parser.error("C++17 compiler not found; pass --cxx /path/to/c++")
    root = Path(__file__).resolve().parents[1]

    def source(name):
        path = "crest/Assets/Crest/Crest/Shaders/" + name
        data = subprocess.check_output(["git", "show", f"{args.ref}:{path}"], cwd=root) if args.ref else (root / path).read_bytes()
        return data.decode("utf-8-sig")

    kernel = source("Resources/UpdateDynWaves.compute")
    kernel = re.sub(r"^\s*#(?:include|pragma)\b[^\n]*", "", kernel, flags=re.M)
    kernel = re.sub(r"\[numthreads\([^\]]+\)\]", "", kernel)
    kernel = re.sub(r"\s*:\s*SV_DispatchThreadID\b", "", kernel)
    helpers = source("OceanHelpersNew.hlsl")
    coordinates = re.findall(r"(?m)^float[23]\s+(?:WorldToUV|UVToWorld|IDtoUV)\([^{}]*\)\s*\{[^{}]*\}", helpers)
    if len(coordinates) != 4:
        raise RuntimeError("Expected both WorldToUV overloads plus UVToWorld and IDtoUV")
    coordinates = re.sub(r"\bin\s+", "", "\n".join(coordinates))
    with tempfile.TemporaryDirectory(prefix="crest-dynamic-waves-") as directory:
        project = Path(directory)
        (project / "UpdateDynWaves.hlsl").write_text(kernel, encoding="utf-8")
        (project / "OceanCoordinates.hlsl").write_text(coordinates, encoding="utf-8")
        shutil.copyfile(root / "tools/DynamicWaveChecks/Program.cpp", project / "Program.cpp")
        binary = project / "dynamic-wave-checks"
        build = subprocess.run([args.cxx, "-std=c++17", "-O0", "-fno-fast-math", str(project / "Program.cpp"), "-o", str(binary)],
                               text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        if build.returncode:
            print(build.stdout, end="")
            return build.returncode
        print(f"Production UpdateDynWaves: {args.ref or 'working tree'}", flush=True)
        return subprocess.run([str(binary)]).returncode


if __name__ == "__main__":
    raise SystemExit(main())
