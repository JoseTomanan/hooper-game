"""Package #430's unaltered gameplay view, source-timed GIFs and trace provenance.

Requires Pillow. Input folders are run_pivot_capture.py role folders. The GIFs
keep the full viewport; no crop, camera adjustment, pose editing or interpolation.
"""
import argparse
import gzip
import hashlib
import json
from pathlib import Path
import re

from PIL import Image


def sha(data):
    return hashlib.sha256(data).hexdigest()


def package(directory, output, label):
    manifest = json.loads((directory / "manifest.json").read_text(encoding="utf-8-sig"))
    if manifest["result"] != "captured" or manifest["completedCycles"] != 3:
        raise ValueError(f"incomplete capture: {directory}")
    rows = [json.loads(line) for line in (directory / "trace.jsonl").read_text(encoding="utf-8-sig").splitlines()]
    final = {row["physicsFrame"]: row for row in rows}
    by_sample = {row["sample"]: row for row in final.values()}
    pivot = [row for row in final.values() if row["cycle"] == 1 and row["measuring"] and row["displayPivoting"]]
    first = pivot[0]["physicsFrame"]
    frames, index, selected = [], [], {}
    for path in sorted(directory.glob("frame-*-pose-*.png")):
        match = re.fullmatch(r"frame-\d+-pose-(\d+)\.png", path.name)
        row = by_sample.get(int(match[1]))
        if row is None or row["cycle"] != 1 or not first - 6 <= row["physicsFrame"] <= first + 38:
            continue
        selected[row["physicsFrame"]] = (path, row)
    for path, row in selected.values():
        with Image.open(path) as source:
            frames.append(source.convert("RGB"))
        index.append({"file": path.name, "sha256": sha(path.read_bytes()),
                      "sample": row["sample"], "physicsFrame": row["physicsFrame"],
                      "sourceTick": row["presentationTick"], "pivot": row["displayPivoting"]})
    if len(frames) < 20 or not any(item["physicsFrame"] < first for item in index):
        raise ValueError(f"insufficient rendered boundary coverage: {directory}")
    # GIF has 10 ms units. Round cumulative 60 Hz time, rather than accumulating
    # a 20 ms/frame error. Remote duplicates/holds retain their observed duration.
    ticks = [item["physicsFrame"] - index[0]["physicsFrame"] for item in index]
    ends = ticks[1:] + [ticks[-1] + 1]
    durations = [max(10, (round(b * 100 / 60) - round(a * 100 / 60)) * 10)
                 for a, b in zip(ticks, ends)]
    destination = output / (label + ".gif")
    frames[0].save(destination, save_all=True, append_images=frames[1:], duration=durations, loop=0, optimize=False)
    frames[0].save(output / (label + "-gameplay-camera.png"))
    trace_files = {}
    for name in ("manifest.json", "trace.jsonl"):
        raw = (directory / name).read_bytes()
        packed = gzip.compress(raw, mtime=0)
        target = label + "-" + name + ".gz"
        (output / target).write_bytes(packed)
        trace_files[target] = {"sourceSHA256": sha(raw), "gzipSHA256": sha(packed)}
    return {"commit": manifest["commit"], "role": manifest["role"],
            "camera": manifest["camera"], "resolution": list(frames[0].size),
            "timing": "60 Hz observed physics frames, cumulative rounding to GIF's 10 ms units; normal source speed",
            "remoteTiming": manifest["remoteTiming"], "fixedFps": manifest["fixedFps"],
            "gifSHA256": sha(destination.read_bytes()), "frames": index, "traces": trace_files}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for label in ("before-local", "before-remote", "after-local", "after-remote"):
        parser.add_argument("--" + label, type=Path, required=True)
    parser.add_argument("--historical-local", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    index = {label: package(getattr(args, label.replace("-", "_")), args.output, label)
             for label in ("before-local", "before-remote", "after-local", "after-remote")}
    if args.historical_local:
        index["historical-local"] = package(args.historical_local, args.output, "historical-local")
    (args.output / "frame-index.json").write_text(json.dumps(index, indent=2) + "\n", encoding="utf-8")
    print(f"Packaged {len(index)} full-viewport source-timed clips in {args.output}")


if __name__ == "__main__":
    main()
