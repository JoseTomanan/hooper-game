"""Package #418's unchanged rendered frames into aligned review artifacts.

Pillow only resizes full frames and adds labels outside their bounds. No image
generation, pose correction, cropping, or judgement of feel is performed.
"""
import argparse
import gzip
import hashlib
import json
import math
from functools import lru_cache
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
HANDS = ("right", "left")
VARIANTS = ("normal", "early", "late", "fadeaway")
WIDTH, CELL_WIDTH, CELL_HEIGHT = 1024, 480, 270


@lru_cache(maxsize=None)
def camera_records(directory):
    """Retain source rows needed to reconstruct the older post-draw camera path."""
    manifest = json.loads((directory / "manifest.json").read_text(encoding="utf-8"))
    rows = {}
    previous = None
    for line in (directory / "trace.jsonl").read_text(encoding="utf-8").splitlines():
        row = json.loads(line)
        if "camera" in row:
            state = {"provenance": row["cameraProvenance"], "transform": row["camera"],
                     "transformFormat": "origin XYZ followed by basis X/Y/Z columns",
                     "fov": row["cameraFov"], "projection": row["cameraProjection"],
                     "near": row["cameraNear"], "far": row["cameraFar"], "size": row["cameraSize"]}
        elif manifest["view"] == "gameplay" or previous is None:
            state = {"provenance": "inferred from unchanged Main.tscn camera; no runtime writer",
                     "position": [0, 8, 9.705208],
                     "basisColumns": [[1, 0, 0], [0, .7071068, .7071068], [0, -.7071068, .7071068]],
                     "fov": 75, "projection": "perspective"}
        else:
            # The old capture path sets this camera AFTER drawing, so the prior
            # row's root/heading determines the image currently being persisted.
            yaw = previous["heading"]
            target = [previous["root"][0], previous["root"][1] + 1.4, previous["root"][2]]
            direction = ([math.cos(yaw), 0, -math.sin(yaw)] if manifest["view"] == "side"
                         else [-math.sin(yaw), 0, -math.cos(yaw)])
            state = {"provenance": "reconstructed from committed capture path and previous post-draw row; not directly observed",
                     "position": [a + 4*b for a, b in zip(target, direction)], "lookAt": target,
                     "up": [0, 1, 0], "fov": 75, "projection": "perspective"}
        state["viewportPixels"] = [960, 540]  # checked against the actual PNG below
        rows[row["sample"]] = state
        previous = {"heading": row["heading"], "root": row["root"]}
    return rows


def required_keys(variant):
    startup = {"early": 3, "late": 11}.get(variant, 17)
    keys = [("Startup", i) for i in range(1, startup + 1)]
    if variant in ("normal", "fadeaway"):
        keys += [("Active", i) for i in range(4)]
        keys += [("Recovery", i) for i in range(20)]
    else:
        keys += [("Recovery", i) for i in range(13, 20)]
    return keys


def read_capture(directory, remote):
    manifest = json.loads((directory / "manifest.json").read_text(encoding="utf-8"))
    if manifest.get("result") != "captured" or not manifest.get("images"):
        raise ValueError(f"capture incomplete or has no images: {directory}")
    raw = [json.loads(line) for line in (directory / "trace.jsonl").read_text(encoding="utf-8").splitlines()]
    rows, seen = [], set()
    for row in raw:
        pose = row.get("pose", {})
        if not pose or not row.get("file"):
            continue
        if remote:
            stamp = row["selectedPresentationTick"]
            if stamp < 0 or not (stamp == row["ballPresentationTick"] == pose.get("presentationTick")):
                continue
        else:
            if not row.get("poseMatchesPhysics"):
                continue
            stamp = row["physicsFrame"]
        if stamp in seen:
            continue
        seen.add(stamp)
        if not (directory / row["file"]).is_file():
            raise ValueError(f"missing rendered frame {directory / row['file']}")
        rows.append(row)
    keys = required_keys(manifest["variant"])
    indexed = {}
    for i, row in enumerate(rows):
        pose = row["pose"]
        if pose.get("move") == "jumpshot":
            indexed.setdefault((pose["phase"], pose["frame"]), i)
    missing = [key for key in keys if key not in indexed]
    if missing:
        raise ValueError(f"incomplete exact rendered phases in {directory}: {missing}")
    first, last = indexed[keys[0]], indexed[keys[-1]]
    before = [r for r in rows[:first] if r["pose"].get("phase") == "Inactive"]
    after = [r for r in rows[last + 1:] if r["pose"].get("phase") == "Inactive"]
    if not before or len(after) < 8:
        raise ValueError(f"missing entry or eight neutral poses: {directory}")
    aligned = [("entry", before[-1])]
    aligned += [(f"{phase}:{frame}", rows[indexed[phase, frame]]) for phase, frame in keys]
    aligned += [(f"neutral:{i + 1}", r) for i, r in enumerate(after[:8])]
    return manifest, aligned


def frame_reference(directory, row):
    path = directory / row["file"]
    return {"source": str(path.resolve().relative_to(ROOT)).replace("\\", "/"),
            "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
            "sample": row["sample"], "tick": row["tick"],
            "physicsFrame": row["physicsFrame"], "pose": {k: v for k, v in row["pose"].items() if k != "bones"},
            "selectedPresentationTick": row["selectedPresentationTick"],
            "ballPresentationTick": row["ballPresentationTick"],
            "releaseCount": row["releaseCount"], "camera": camera_records(directory)[row["sample"]]}


def draw_pair(before_dir, after_dir, before, after, title, label):
    canvas = Image.new("RGB", (WIDTH, CELL_HEIGHT + 64), "#111821")
    draw = ImageDraw.Draw(canvas)
    draw.text((16, 7), title + " | " + label, fill="white")
    draw.text((16, 25), "BEFORE: preserved old shot", fill="#b7d8ff")
    draw.text((528, 25), "AFTER: Blender authored right-handed shot", fill="#b7d8ff")
    for x, directory, row in ((16, before_dir, before), (528, after_dir, after)):
        with Image.open(directory / row["file"]) as image:
            if image.size != (960, 540):
                raise ValueError(f"expected uncropped 960x540 source, got {image.size}: {directory / row['file']}")
            # Pillow 12 full-frame resampling and paste (no crop):
            # https://pillow.readthedocs.io/en/12.0.0/reference/Image.html#PIL.Image.Image.resize
            canvas.paste(image.convert("RGB").resize((CELL_WIDTH, CELL_HEIGHT), Image.Resampling.LANCZOS), (x, 48))
    return canvas


def gzip_copy(source, destination):
    # mtime=0 removes packaging-time noise from the compressed raw evidence.
    with destination.open("wb") as output:
        with gzip.GzipFile(fileobj=output, mode="wb", mtime=0) as compressed:
            compressed.write(source.read_bytes())


def package_case(before_root, after_root, output, topology, case, gif=True):
    before_dir, after_dir = before_root / (case + "-1"), after_root / (case + "-1")
    before_manifest, before = read_capture(before_dir, topology == "remote")
    after_manifest, after = read_capture(after_dir, topology == "remote")
    if [key for key, _ in before] != [key for key, _ in after]:
        raise ValueError(f"unalignable phase keys: {topology} {case}")
    stem = f"{topology}-{case}"
    records, frames = [], []
    for i, ((key, old), (_, new)) in enumerate(zip(before, after)):
        frames.append(draw_pair(before_dir, after_dir, old, new, stem, key + " | 2.4x slow motion"))
        records.append({"outputFrame": i, "alignment": key,
                        "before": frame_reference(before_dir, old), "after": frame_reference(after_dir, new)})
    if gif:
        # 40 ms per physics-pose sample vs 16.67 ms native: explicitly 2.4x slow.
        # Pillow GIF API: https://pillow.readthedocs.io/en/12.0.0/handbook/image-file-formats.html#gif
        frames[0].save(output / (stem + ".gif"), save_all=True, append_images=frames[1:],
                       duration=40, loop=0, disposal=2, optimize=False)
    variant = before_manifest["variant"]
    milestones = (["entry", "Startup:8", "Active:0", "Active:2", "Recovery:0", "Recovery:3", "neutral:8"]
                  if variant in ("normal", "fadeaway") else
                  ["entry", "Startup:" + ("3" if variant == "early" else "11"),
                   "Recovery:13", "Recovery:16", "Recovery:19", "neutral:1", "neutral:8"])
    lookup = {record["alignment"]: i for i, record in enumerate(records)}
    sheet = Image.new("RGB", (WIDTH, frames[0].height * len(milestones)), "#111821")
    for i, key in enumerate(milestones):
        sheet.paste(frames[lookup[key]], (0, i * frames[0].height))
    sheet.save(output / (stem + ".png"))
    for prefix, directory in (("before", before_dir), ("after", after_dir)):
        for name in ("manifest.json", "trace.jsonl"):
            gzip_copy(directory / name, output / f"{stem}-{prefix}-{name}.gz")
    return {"case": stem, "gif": stem + ".gif" if gif else None,
            "contactSheet": stem + ".png", "contactSheetAlignment": milestones,
            "beforeManifest": before_manifest, "afterManifest": after_manifest,
            "frames": records}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("before-local", "after-local", "before-remote", "after-remote",
                 "before-front", "after-front", "before-side", "after-side"):
        parser.add_argument("--" + name, type=Path)
    parser.add_argument("--output", type=Path, default=ROOT / "docs/analysis/evidence/0418")
    parser.add_argument("--case", action="append", choices=[f"{h}-{v}" for h in HANDS for v in VARIANTS])
    args = parser.parse_args()
    output = args.output.resolve()
    if not output.is_relative_to(ROOT / "docs/analysis/evidence/0418"):
        parser.error("output must be below docs/analysis/evidence/0418")
    jobs = []
    cases = args.case or [f"{h}-{v}" for h in HANDS for v in VARIANTS]
    for topology in ("local", "remote", "front", "side"):
        before, after = getattr(args, "before_" + topology), getattr(args, "after_" + topology)
        if bool(before) != bool(after):
            parser.error(f"--before-{topology} and --after-{topology} must be supplied together")
        if before:
            jobs += [(before, after, topology, case) for case in cases
                     if topology in ("local", "remote") or case.endswith("-normal")]
    if not jobs:
        parser.error("at least one before/after pair is required")
    # Validate every input before writing any output artifacts.
    for before, after, topology, case in jobs:
        read_capture(before / (case + "-1"), topology == "remote")
        read_capture(after / (case + "-1"), topology == "remote")
    output.mkdir(parents=True, exist_ok=True)
    index = [package_case(before, after, output, topology, case, gif=topology in ("local", "remote"))
             for before, after, topology, case in jobs]
    (output / "frame-index.json").write_text(json.dumps({"schema": "hooper-jumpshot-evidence/v1",
        "imageTreatment": "whole 960x540 source resized to480x270 with Lanczos; labels outside source pixels",
        "gifDurationMs": 40, "playback": "2.4x slow motion for60Hz pose samples; paced remote has no real-time timing claim",
        "alignment": "exact rendered pose.phase/frame; coherent remote player+ball selected tick; repeated selected ticks removed",
        "feelVerdict": "not accepted; review artifacts only", "cases": index}, indent=2) + "\n", encoding="utf-8")
    print(f"PACKAGED {len(index)} aligned cases into {output}")


if __name__ == "__main__":
    main()
