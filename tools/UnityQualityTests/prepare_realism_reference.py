"""Extract the pre-detail water shader for same-geometry GPU image comparisons."""
import argparse
from pathlib import Path
import subprocess

REPOSITORY = Path(__file__).resolve().parents[2]
PREFIX = "crest/Assets/Crest/Crest/Shaders/"
BASE = "5700e21b93a6ae325d049a5f1ec0bc408a721dc7"

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("project", type=Path)
args = parser.parse_args()
project = args.project.resolve()
if project == (REPOSITORY / "crest").resolve() or not (project / "Assets").is_dir():
    parser.error("Use an existing disposable Unity project copy")
destination = project / "Assets" / "BeforeDetailReference"
if destination.exists():
    parser.error("Reference directory exists; retain it or use a fresh project copy")
paths = subprocess.check_output(["git", "ls-tree", "-r", "--name-only", BASE, PREFIX], cwd=REPOSITORY, text=True).splitlines()
count = 0
for path in paths:
    relative = Path(path[len(PREFIX):])
    if relative.suffix not in (".hlsl", ".cginc") and relative.as_posix() != "Ocean.shader":
        continue
    content = subprocess.check_output(["git", "show", f"{BASE}:{path}"], cwd=REPOSITORY)
    if relative.as_posix() == "Ocean.shader":
        content = content.replace(b'Shader "Crest/Ocean"', b'Shader "Crest/Ocean Before Detail"', 1)
    target = destination / relative
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(content)
    count += 1
print(f"Extracted {count} shader/include files from {BASE} to {destination}")
