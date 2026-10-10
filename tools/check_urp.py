#!/usr/bin/env python3
"""Deterministic source checks; does not claim Unity compilation or GPU validation."""
from pathlib import Path
import json
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "crest/Assets"
errors = []
checked_includes = 0
shader_files = list(ASSETS.rglob("*.shader")) + list(ASSETS.rglob("*.hlsl")) + list(ASSETS.rglob("*.compute"))

for file in shader_files:
    text = file.read_text(encoding="utf-8-sig")
    text_without_comments = re.sub(r"/\*.*?\*/|//[^\n]*", "", text, flags=re.S)
    stack = []
    for number, line in enumerate(text_without_comments.splitlines(), 1):
        directive = re.match(r"\s*#\s*(if|ifdef|ifndef|elif|else|endif)\b", line)
        if directive:
            kind = directive[1]
            if kind in ("if", "ifdef", "ifndef"):
                stack.append(number)
            elif not stack:
                errors.append(f"{file.relative_to(ROOT)}:{number}: unmatched #{kind}")
            elif kind == "endif":
                stack.pop()
        for target in re.findall(r'#\s*include(?:_with_pragmas)?\s+"([^"]+)"', line):
            if target.startswith("Packages/") or target.endswith(".cginc"):
                continue  # Unity/package-owned includes are checked by Unity locally.
            resolved = (file.parent / target).resolve()
            checked_includes += 1
            if not resolved.is_file():
                errors.append(f"{file.relative_to(ROOT)}:{number}: missing include {target}")
    if stack:
        errors.append(f"{file.relative_to(ROOT)}: unclosed preprocessor blocks {stack}")
    if file.suffix == ".shader":
        blocks = re.findall(r"\b(?:HLSLPROGRAM|HLSLINCLUDE|CGPROGRAM|CGINCLUDE|ENDHLSL|ENDCG)\b", text_without_comments)
        open_block = None
        for token in blocks:
            if token.startswith("END"):
                expected = "ENDHLSL" if open_block and open_block.startswith("HLSL") else "ENDCG"
                if open_block is None or token != expected:
                    errors.append(f"{file.relative_to(ROOT)}: shader block mismatch {open_block}/{token}")
                open_block = None
            elif open_block:
                errors.append(f"{file.relative_to(ROOT)}: nested shader program")
            else:
                open_block = token
        if open_block:
            errors.append(f"{file.relative_to(ROOT)}: unclosed {open_block}")
        if re.search(r"\bGrabPass\b|#pragma\s+surface\b", text_without_comments):
            errors.append(f"{file.relative_to(ROOT)}: unsupported GrabPass or surface shader")

assemblies = list(ASSETS.rglob("*.asmdef"))
for file in assemblies:
    data = json.loads(file.read_text(encoding="utf-8-sig"))
    if len(data.get("references", [])) != len(set(data.get("references", []))):
        errors.append(f"{file.relative_to(ROOT)}: duplicate assembly references")
manifest = json.loads((ROOT / "crest/Packages/manifest.json").read_text())
if manifest["dependencies"].get("com.unity.render-pipelines.universal") != "17.6.0":
    errors.append("Expected the pinned URP 17.6.0 package")

new_asset_roots = [ASSETS / "Crest/Crest/Scripts/URP", ASSETS / "Crest/Crest/Shaders/Helpers/URP"]
new_assets = [ASSETS / "Crest/Crest/Scripts/Editor/CrestURPSetup.cs"]
for folder in new_asset_roots:
    new_assets += [folder] + [p for p in folder.iterdir() if p.suffix in (".cs", ".hlsl")]
guids = set()
for file in new_assets:
    meta = Path(str(file) + ".meta")
    if not meta.exists():
        errors.append(f"Missing Unity metadata: {meta.relative_to(ROOT)}")
        continue
    match = re.search(r"^guid: ([0-9a-f]{32})$", meta.read_text(), re.M)
    if match is None or match[1] in guids:
        errors.append(f"Invalid/duplicate new GUID: {meta.relative_to(ROOT)}")
    else:
        guids.add(match[1])

print(f"Shader source: {len(shader_files)} files; {checked_includes} local include references.")
print(f"JSON: {len(assemblies)} assembly definitions and package manifest. New Unity assets: {len(new_assets)}.")
for error in errors:
    print(error, file=sys.stderr)
print(f"Static source checks: {len(errors)} errors. No Unity compilation or GPU execution performed.")
sys.exit(bool(errors))
