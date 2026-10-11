"""Sync only changed production files into the two stopped, cached disposable projects."""
import argparse
import hashlib
from pathlib import Path
import shutil
import subprocess
import sys

repo = Path(__file__).resolve().parents[2]
source = repo / "crest"
commit = subprocess.check_output(["git", "rev-parse", "--short=8", "HEAD"], cwd=repo, text=True).strip()
tools = repo / "tools/UnityQualityTests"
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--label", default=commit, help="distinct evidence label, including local fixes")
parser.add_argument("projects", nargs="+", type=Path)
args = parser.parse_args()
if Path(args.label).name != args.label:
    parser.error("label must be one directory name")
code = sorted(p for p in (source / "Assets/Crest").rglob("*") if p.suffix in {".cs", ".shader", ".hlsl", ".compute"})
code += [source / "Assets/Development/Editor/AndroidAssetPackDependency.cs"]
for argument in args.projects:
    target = argument.resolve()
    if target == source.resolve() or not target.name.startswith("crest-validation-") or not (target / "Library").is_dir():
        raise SystemExit("Expected an existing cached disposable crest-validation-* project: " + str(target))
    changed = []
    manifest = []
    for src in code:
        rel = src.relative_to(source)
        dst = target / rel
        digest = hashlib.sha256(src.read_bytes()).hexdigest()
        if not dst.exists() or hashlib.sha256(dst.read_bytes()).hexdigest() != digest:
            dst.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(src, dst)
            changed.append(str(rel))
        assert hashlib.sha256(dst.read_bytes()).hexdigest() == digest
        manifest.append(digest + "  " + rel.as_posix())
        if src.with_suffix(src.suffix + ".meta").exists() and not dst.with_suffix(dst.suffix + ".meta").exists():
            shutil.copy2(src.with_suffix(src.suffix + ".meta"), dst.with_suffix(dst.suffix + ".meta"))
    for name in ["QualityRunner.cs", "RealismAudit.cs", "RealismAudit.shader", "PlayerAuditBuilder.cs", "AndroidAuditBuilder.cs", "RunnerStartupProbe.cs"]:
        if (tools / name).exists(): shutil.copy2(tools / name, target / "Assets/Editor" / name)
    for name in ["PlayerAudit.cs", "PlayerGpuContracts.cs", "RuntimeQueryAudit.cs", "PlayerRetest.cs", "PlayerTemporalAudit.cs"]:
        shutil.copy2(tools / name, target / "Assets" / name)
    shutil.copy2(tools / "AuditQueryCoordinates.compute", target / "Assets/Resources/AuditQueryCoordinates.compute")
    shutil.copy2(tools / "RealismAudit.shader", target / "Assets/Resources/RealismAudit.shader")
    evidence = target / ("Retest-" + args.label)
    evidence.mkdir(exist_ok=True)
    (evidence / "production-sha256.txt").write_text("\n".join(manifest) + "\n", encoding="utf-8")
    captures = target / "QualityCaptures"
    baseline = target / "QualityCaptures-90a93293"
    if captures.exists() and not baseline.exists(): captures.rename(baseline)
    print(f"{target}: {len(code)} production hashes verified; changed {changed}; tools installed; evidence {evidence}")
