"""Rank actual-game frame changes for visual review; this does not certify absence of flicker."""
import argparse
import csv
import io
import json
from pathlib import Path
import zipfile

import numpy as np
from PIL import Image

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("archive", type=Path)
parser.add_argument("output", type=Path)
parser.add_argument("--count", type=int, default=12)
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=True)

with zipfile.ZipFile(args.archive) as archive:
    rows = list(csv.DictReader(io.StringIO(archive.read("frames.csv").decode("utf-8-sig"))))
    names = ["frame-" + row["frame"].zfill(7) + ".jpg" for row in rows]
    if len(rows) < 3:
        parser.error("At least three captured game frames are required")

    def read_pixels(index):
        with Image.open(io.BytesIO(archive.read(names[index]))) as image:
            return np.asarray(image.convert("RGB"), dtype=np.float32) / 255

    candidates = []
    previous_deltas = []
    pose_fields = ["camera_x", "camera_y", "camera_z", "camera_qx", "camera_qy", "camera_qz", "camera_qw"]
    previous, current = read_pixels(0), read_pixels(1)
    for i in range(1, len(rows) - 1):
        following = read_pixels(i + 1)
        before = float(np.abs(current - previous).mean())
        after = float(np.abs(current - following).mean())
        surrounding = float(np.abs(previous - following).mean())
        same_camera = all(rows[i - 1]["scene"] == row["scene"] for row in rows[i:i + 2]) and all(
            abs(float(rows[i - 1][key]) - float(row[key])) < 1e-5
            for row in rows[i:i + 2] for key in pose_fields)
        adjacent = int(rows[i]["frame"]) == int(rows[i - 1]["frame"]) + 1 and int(rows[i + 1]["frame"]) == int(rows[i]["frame"]) + 1
        if same_camera and adjacent:
            candidates.append({"index": i, "frame": int(rows[i]["frame"]), "seconds": float(rows[i]["seconds"]),
                               "scene": rows[i]["scene"], "phase": rows[i]["phase"],
                               "out_and_back": max(0.0, min(before, after) - surrounding),
                               "before_delta": before, "after_delta": after, "surrounding_delta": surrounding})
            previous_deltas.append(before)
        previous, current = current, following

    candidates.sort(key=lambda item: item["out_and_back"], reverse=True)
    selected = candidates[:args.count]
    for rank, item in enumerate(selected):
        for offset, label in [(-1, "before"), (0, "candidate"), (1, "after")]:
            name = names[item["index"] + offset]
            (args.output / f"{rank:02d}-{item['frame']:07d}-{label}.jpg").write_bytes(archive.read(name))

    phases = {}
    for row in rows:
        phases[row["phase"]] = phases.get(row["phase"], 0) + 1
    summary = {
        "archive": str(args.archive.resolve()), "frames": len(rows), "first_frame": int(rows[0]["frame"]),
        "last_frame": int(rows[-1]["frame"]), "seconds": float(rows[-1]["seconds"]) - float(rows[0]["seconds"]),
        "frame_gaps": sum(int(b["frame"]) - int(a["frame"]) != 1 for a, b in zip(rows, rows[1:])),
        "same_camera_triplets": len(candidates), "phase_frames": phases,
        "same_camera_delta_median": float(np.median(previous_deltas)) if previous_deltas else None,
        "same_camera_delta_p99": float(np.percentile(previous_deltas, 99)) if previous_deltas else None,
        "review_candidates": selected,
        "interpretation": "Ranked diagnostic evidence only. Scene/camera cuts are excluded; moving waves, boats and intentional rebuilds can still cause image changes. Review the selected three-frame sequences and phase metadata.",
    }
    (args.output / "summary.json").write_text(json.dumps(summary, indent=2, ensure_ascii=False), encoding="utf-8")
    print(json.dumps({key: value for key, value in summary.items() if key != "review_candidates"}, ensure_ascii=False))
    for item in selected:
        print(json.dumps(item, ensure_ascii=False))
