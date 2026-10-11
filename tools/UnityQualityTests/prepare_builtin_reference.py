"""Extract the original ocean shader/includes into a disposable validation project.

Only the reference shader's name changes. No upstream runtime scripts or materials
are substituted: the comparison deliberately isolates surface shading on the same
simulated geometry, camera and material settings.
"""
import argparse
from pathlib import Path
import subprocess

REPOSITORY = Path(__file__).resolve().parents[2]
PREFIX = "crest/Assets/Crest/Crest/Shaders/"
BASE = "db0658ff0b2e93e4a9e28cc2867509658b0ecc00"

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("project", type=Path, help="Disposable Unity project, never the source checkout")
args = parser.parse_args()
project = args.project.resolve()
if project == (REPOSITORY / "crest").resolve() or not (project / "Assets").is_dir():
    parser.error("Use an existing disposable Unity project copy")
destination = project / "Assets" / "BuiltinSurfaceReference"
if destination.exists():
    parser.error("Reference directory exists; use a fresh project copy or retain the existing reference")
paths = subprocess.check_output(["git", "ls-tree", "-r", "--name-only", BASE, PREFIX], cwd=REPOSITORY, text=True).splitlines()
count = 0
for path in paths:
    relative = Path(path[len(PREFIX):])
    if relative.suffix not in (".hlsl", ".cginc") and relative.as_posix() != "Ocean.shader":
        continue
    content = subprocess.check_output(["git", "show", f"{BASE}:{path}"], cwd=REPOSITORY)
    if relative.as_posix() == "Ocean.shader":
        content = content.replace(b'Shader "Crest/Ocean"', b'Shader "Crest/Ocean Builtin Reference"', 1)
    target = destination / relative
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(content)
    count += 1
print(f"Extracted {count} shader/include files from {BASE} to {destination}")
