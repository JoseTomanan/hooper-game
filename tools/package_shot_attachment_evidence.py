"""Package #442 full-frame rendered comparisons and separate anatomical overlays.

Only complete offset-zero local episodes are selected: eighteen gameplay cases,
six normal front cases and six normal side cases. Source images remain unchanged.
"""
from __future__ import annotations

import argparse
import hashlib
import itertools
import json
import math
from pathlib import Path

from analyze_jumpshot_capture import distance
from analyze_shot_attachment import load_calibration, vector
from generate_shot_gather_profile import transform
from package_jumpshot_transition_evidence import (
    HEADER, HEIGHT, PANEL, WIDTH, event_clock, frame_reference, gzip_copy,
    metadata_matches, pose_label, read_capture, source_path, source_tick,
)

ROOT = Path(__file__).resolve().parents[1]
EVIDENCE = ROOT / "docs/analysis/evidence/0442"
OVERLAY_POSES = (("Startup", 8), ("Startup", 17), ("Active", 0), ("Active", 1))


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def project_world(row, point):
    """Godot camera-local -Z forward, KEEP_HEIGHT, top-left pixel coordinates.

    Camera3D's documented unproject_position maps world to viewport; FOV is
    vertical and orthographic size is vertical extent with default KEEP_HEIGHT.
    Existing production scenes/capture code never override that default. Newer
    metadata may explicitly record it; a different value fails, never guesses.
    https://docs.godotengine.org/en/4.7/classes/class_camera3d.html#class-camera3d-method-unproject-position
    https://docs.godotengine.org/en/4.7/classes/class_camera3d.html#class-camera3d-property-keep-aspect
    """
    camera = vector(row.get("camera"), "recorded camera transform", 12)
    point = vector(point, "world projection point")
    width, height = vector(row.get("viewportSize"), "recorded viewport size", 2)
    if width <= 0 or height <= 0 or row.get("cameraKeepAspect", "KeepHeight") not in (1, "KeepHeight", "KEEP_HEIGHT"):
        raise ValueError("unsupported recorded viewport/aspect convention")
    delta = [p-c for p, c in zip(point, camera[:3])]
    x, y, z = [camera[i:i+3] for i in (3, 6, 9)]
    cross = lambda a, b: [a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0]]
    dot = lambda a, b: sum(v*w for v, w in zip(a, b))
    determinant = dot(x, cross(y, z))
    if abs(determinant) < 1e-12:
        raise ValueError("singular recorded camera basis")
    local = [dot(delta, cross(a, b)) / determinant for a, b in ((y, z), (z, x), (x, y))]
    depth = -local[2]
    if depth <= 0:
        raise ValueError("overlay point is behind the recorded camera")
    if not row["cameraNear"] <= depth <= row["cameraFar"]:
        raise ValueError("overlay point is outside recorded camera clipping planes")
    if row["cameraProjection"] == "Perspective":
        fov = row["cameraFov"]
        if not 0 < fov < 180:
            raise ValueError("invalid recorded camera FOV")
        scale = height / (2 * depth * math.tan(math.radians(fov) / 2))
    elif row["cameraProjection"] == "Orthogonal":
        if row["cameraSize"] <= 0:
            raise ValueError("invalid recorded orthographic camera size")
        scale = height / row["cameraSize"]
    else:
        raise ValueError("unsupported recorded camera projection")
    pixel = [width / 2 + local[0] * scale, height / 2 - local[1] * scale]
    if not all(math.isfinite(v) for v in pixel) or not (0 <= pixel[0] < width and 0 <= pixel[1] < height):
        raise ValueError("overlay point lies outside the complete source frame")
    return pixel


def overlay_geometry(row, calibration):
    wrist = vector(row["pose"]["bones"]["mixamorig_RightHand"]["world"], "final wrist world transform", 12)
    # Pure weight=1 means wrist * calibrated raw skin vertex is the actual
    # final skinned surface position, independent of the ball/attachment model.
    local = calibration["palms"]["Right"]["wrist_local"]["Right"]["position"]
    palm = transform(wrist, local)
    ball = vector(row.get("ball"), "visible ball center")
    return {"palmWorld": palm, "visibleBallWorld": ball,
            "palmPixel": project_world(row, palm), "visibleBallPixel": project_world(row, ball),
            "contactErrorM": abs(distance(palm, ball) - calibration["radius"]),
            "signedSurfaceGapM": distance(palm, ball) - calibration["radius"],
            "wristCenterDistanceM": distance(wrist[:3], ball), "skinVertex": 11216}


def expected_keys():
    return {("local", view, f"{hand}-{entry}-{variant}-o0-1")
            for view in ("gameplay", "front", "side")
            for hand, entry, variant in itertools.product(
                ("right", "left"), ("held", "dribble", "moving"),
                ("normal", "early", "late") if view == "gameplay" else ("normal",))}


def capture_key(directory):
    manifest = json.loads((directory / "manifest.json").read_text(encoding="utf-8"))
    return (manifest.get("role"), manifest.get("view"), directory.name), manifest


def selected_pairs(inventory_path, after_roots):
    inventory = json.loads(inventory_path.read_text(encoding="utf-8"))
    wanted, before, after = expected_keys(), {}, {}
    for item in inventory["selectedLocal"]:
        directory = Path(item["path"]).resolve()
        key, _ = capture_key(directory)
        if key in wanted:
            if key in before:
                raise ValueError("duplicate selected baseline key: " + str(key))
            before[key] = directory
    for root in after_roots:
        root = root.resolve()
        manifests = [root / "manifest.json"] if (root / "manifest.json").is_file() else sorted(root.glob("**/manifest.json"))
        for path in manifests:
            key, manifest = capture_key(path.parent)
            if key not in wanted:
                continue
            if key in after:
                raise ValueError("duplicate selected after key: " + str(key))
            if manifest.get("result") != "captured":
                raise ValueError("selected after capture is incomplete: " + str(path.parent))
            after[key] = path.parent
    if set(before) != wanted or set(after) != wanted:
        raise ValueError(f"missing selected before keys={sorted(wanted-set(before))}; after keys={sorted(wanted-set(after))}")
    return [(key, before[key], after[key]) for key in sorted(wanted)]


def validate_pair(key, old_dir, new_dir, calibration):
    old_manifest, old, old_holds, old_edges = read_capture(old_dir, before=True)
    new_manifest, new, new_holds, new_edges = read_capture(new_dir)
    metadata_matches(old_manifest, new_manifest)
    if old_edges or new_edges:
        raise ValueError("#442 film cannot erase changed duplicate source poses")
    if old_manifest.get("role") != "local" or old_manifest.get("entryOffset") != 0:
        raise ValueError("packager only accepts the specified local offset-zero scope")
    old_clock, new_clock = event_clock(old_dir, old_manifest), event_clock(new_dir, new_manifest)
    old_first, new_first = source_tick(old[8], False), source_tick(new[8], False)
    if (len(old) != len(new) or [source_tick(r, False)-old_first for r in old] !=
            [source_tick(r, False)-new_first for r in new] or
            old_first-old_clock["beginTick"] != new_first-new_clock["beginTick"]):
        raise ValueError("incomparable independently observed source timelines: " + str(key))
    from PIL import Image
    for directory, rows in ((old_dir, old), (new_dir, new)):
        for row in rows:
            if row.get("poseMatchesPhysics") is not True:
                raise ValueError("source image has a stale evaluated final pose")
            with Image.open(directory / row["file"]) as image:
                if image.size != (960, 540) or image.size != tuple(row["viewportSize"]):
                    raise ValueError("whole-frame 960x540 source premise changed")
        if key[1] in ("front", "side"):
            by_pose = {(r["pose"]["phase"], r["pose"]["frame"]): r for r in rows[8:-8]}
            for pose in OVERLAY_POSES:
                overlay_geometry(by_pose[pose], calibration)  # fail before any artifact is created
    return {"key": key, "oldDirectory": old_dir, "newDirectory": new_dir,
            "beforeManifest": old_manifest, "afterManifest": new_manifest,
            "before": old, "after": new, "beforeClock": old_clock, "afterClock": new_clock,
            "beforeFirst": old_first, "afterFirst": new_first,
            "beforeDuplicateHolds": old_holds, "afterDuplicateHolds": new_holds}


def draw_pair(pair, old, new, relative_tick):
    from PIL import Image, ImageDraw
    role, view, name = pair["key"]
    canvas = Image.new("RGB", (WIDTH, HEIGHT), "#101820")
    draw = ImageDraw.Draw(canvas)
    draw.text((12, 7), f"#442 | {view} {name} | own source tick {relative_tick:+d} from first evaluated Startup", fill="white")
    draw.text((12, 25), "BEFORE: frozen production baseline", fill="#bddcff")
    draw.text((512, 25), "AFTER: #442 shot attachment", fill="#bddcff")
    for x, row in ((12, old), (512, new)):
        draw.text((x, 44), pose_label(row), fill="white")
        draw.text((x, 59), f"physics {row['physicsFrame']} | source {source_tick(row, False)}", fill="white")
    # Pillow12 whole-frame resize; source body/ball/court are never cropped.
    # https://raw.githubusercontent.com/python-pillow/Pillow/12.0.0/docs/reference/Image.rst
    for x, directory, row in ((10, pair["oldDirectory"], old), (510, pair["newDirectory"], new)):
        with Image.open(directory / row["file"]) as image:
            canvas.paste(image.convert("RGB").resize(PANEL, Image.Resampling.LANCZOS), (x, HEADER))
    draw.text((12, HEADER + PANEL[1] + 7), "60Hz source ticks (17/17/16ms); full source panels; appearance/feel unaccepted (#173)", fill="#dde6ed")
    return canvas


def draw_overlay(directory, row, calibration, destination, caption):
    from PIL import Image, ImageDraw
    geometry = overlay_geometry(row, calibration)
    with Image.open(directory / row["file"]) as original:
        image = original.convert("RGB")
    # Separate diagnostic copy, never the original or either comparison panel.
    # https://raw.githubusercontent.com/python-pillow/Pillow/12.0.0/docs/reference/ImageDraw.rst
    draw = ImageDraw.Draw(image)
    draw.rectangle((0, 0, 959, 37), fill="#101820")
    draw.text((8, 4), caption + " | CYAN actual pure-wrist skin vertex 11216 | YELLOW visible ball center", fill="white")
    draw.text((8, 20), f"surface contact error {geometry['contactErrorM']:.6f}m | wrist-center distance {geometry['wristCenterDistanceM']:.6f}m | diagnostic overlay", fill="white")
    for key, color, radius in (("palmPixel", "cyan", 5), ("visibleBallPixel", "yellow", 8)):
        x, y = geometry[key]
        draw.ellipse((x-radius, y-radius, x+radius, y+radius), outline=color, width=2)
        draw.line((x-3, y, x+3, y), fill=color, width=1)
        draw.line((x, y-3, x, y+3), fill=color, width=1)
    image.save(destination)
    return geometry


def milestones(manifest, rows):
    by_pose = {(r["pose"]["phase"], r["pose"]["frame"]): i for i, r in enumerate(rows[8:-8], 8)}
    if manifest["variant"] == "normal":
        keys = (("Startup", 8), ("Startup", 17), ("Active", 0), ("Recovery", 19))
    else:
        keys = (("Startup", 3 if manifest["variant"] == "early" else 11),
                ("Recovery", 13), ("Recovery", 16), ("Recovery", 19))
    return [7, *[by_pose[key] for key in keys], len(rows)-6]


def package_pair(pair, output, calibration):
    from PIL import Image
    role, view, name = pair["key"]
    stem = f"{role}-{view}-{name}"
    frames, references = [], []
    for index, (old, new) in enumerate(zip(pair["before"], pair["after"])):
        relative = source_tick(old, False) - pair["beforeFirst"]
        frames.append(draw_pair(pair, old, new, relative))
        references.append({"outputFrame": index, "relativeSourceTick": relative,
            "before": frame_reference(pair["oldDirectory"], old, False, pair["beforeFirst"], pair["beforeClock"]),
            "after": frame_reference(pair["newDirectory"], new, False, pair["afterFirst"], pair["afterClock"])})
    durations = [(17, 17, 16)[i % 3] for i in range(len(frames))]
    film = output / (stem + "-normal.webp")
    # Pillow12 lossless animation accepts per-frame integer duration lists.
    # https://raw.githubusercontent.com/python-pillow/Pillow/12.0.0/docs/handbook/image-file-formats.rst
    frames[0].save(film, save_all=True, append_images=frames[1:], duration=durations,
                   loop=0, lossless=True, method=6)
    selected = milestones(pair["beforeManifest"], pair["before"])
    sheet = Image.new("RGB", (WIDTH, HEIGHT * 6), "#101820")
    for index, frame in enumerate(selected):
        sheet.paste(frames[frame], (0, HEIGHT * index))
    sheet_path = output / (stem + "-contact.png")
    sheet.save(sheet_path)
    archives, overlays = {}, []
    for prefix, directory, rows, first, clock in (
            ("before", pair["oldDirectory"], pair["before"], pair["beforeFirst"], pair["beforeClock"]),
            ("after", pair["newDirectory"], pair["after"], pair["afterFirst"], pair["afterClock"])):
        archives[prefix] = {}
        for filename in ("manifest.json", "trace.jsonl"):
            destination = output / f"{stem}-{prefix}-{filename}.gz"
            gzip_copy(directory / filename, destination)
            archives[prefix][filename] = {"file": destination.name,
                "source": source_path(directory / filename), "sourceSha256": sha256(directory / filename),
                "archiveSha256": sha256(destination)}
        if view in ("front", "side"):
            by_pose = {(r["pose"]["phase"], r["pose"]["frame"]): r for r in rows[8:-8]}
            for phase, frame in OVERLAY_POSES:
                row = by_pose[(phase, frame)]
                destination = output / f"{stem}-{prefix}-{phase.lower()}{frame}-overlay.png"
                geometry = draw_overlay(directory, row, calibration, destination, f"#442 {prefix.upper()} {view} {phase}/{frame}")
                overlays.append({"file": destination.name, "sha256": sha256(destination),
                    "source": frame_reference(directory, row, False, first, clock), **geometry})
    for frame in frames:
        frame.close()
    return {"case": stem, "key": pair["key"], "normalSpeedFilm": film.name, "filmSha256": sha256(film),
            "contactSheet": sheet_path.name, "contactSheetSha256": sha256(sheet_path),
            "contactSheetFrameIndexes": selected, "normalFrameDurationsMs": durations,
            "beforeManifest": pair["beforeManifest"], "afterManifest": pair["afterManifest"],
            "beforeEventClock": pair["beforeClock"], "afterEventClock": pair["afterClock"],
            "frames": references, "archives": archives, "contactOverlays": overlays}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--before-inventory", type=Path, required=True)
    parser.add_argument("--after", type=Path, action="append", required=True)
    parser.add_argument("--output", type=Path, default=EVIDENCE / "rendered")
    args = parser.parse_args()
    output = args.output.resolve()
    if output == EVIDENCE or not output.is_relative_to(EVIDENCE) or output.exists():
        parser.error("output must be a fresh subdirectory below docs/analysis/evidence/0442")
    calibration = load_calibration()
    pairs = [validate_pair(key, old, new, calibration)
             for key, old, new in selected_pairs(args.before_inventory, args.after)]
    output.mkdir(parents=True, exist_ok=False)
    records = [package_pair(pair, output, calibration) for pair in pairs]
    index = {"schema": "hooper-shot-attachment-rendered-evidence/v1", "cases": records,
             "beforeInventory": {"source": source_path(args.before_inventory), "sha256": sha256(args.before_inventory)},
             "calibration": {"source": "assets/shot_gather_calibration.json", "sha256": sha256(ROOT / "assets/shot_gather_calibration.json"),
                             "rightPalm": calibration["palms"]["Right"]},
             "imageTreatment": "comparison films/sheets retain complete 960x540 source frames resized to 480x270; labels outside source panels; no crop, pose edits or temporal interpolation",
             "overlayTreatment": "separate complete 960x540 diagnostic copies mark actual skin vertex cyan and visible ball center yellow; original source PNGs unchanged",
             "projectionConvention": "recorded camera world transform/projection/FOV/size/near/far; Camera3D KEEP_HEIGHT default retained by production scenes and capture code; behind/clipped/offscreen points rejected",
             "normalPlayback": "60Hz source-tick projection using 17/17/16ms durations; no latency/performance claim",
             "alignment": "own source ticks relative to independently observed Begin; all original camera clocks/playback retained in index and gzip traces",
             "feelVerdict": "unaccepted; human appearance/feel review remains #173"}
    (output / "frame-index.json").write_text(json.dumps(index, indent=2) + "\n", encoding="utf-8")
    lines = ["# #442 shot attachment rendered comparisons", "",
             "Complete source frames appear in two panels at 60Hz (17/17/16ms), with six-milestone contact sheets. No source frame is cropped or retouched. Appearance and feel remain unaccepted for #173.", "",
             "Front/side overlay PNGs are separate diagnostic copies: cyan marks actual calibrated pure-wrist skin vertex 11216, yellow marks the visible ball center. The index retains original PNG hashes, all recorded camera/source clocks, playback positions and unmodified compressed raw traces/manifests.", "",
             "| Case | Normal-speed comparison | Six milestones | Contact overlays |", "|---|---|---|---|"]
    for record in records:
        overlays = ", ".join(f"[PNG{i+1}]({r['file']})" for i, r in enumerate(record["contactOverlays"])) or "not selected"
        lines.append(f"| {record['case']} | [WebP]({record['normalSpeedFilm']}) | [PNG]({record['contactSheet']}) | {overlays} |")
    lines += ["", "[Original frame, clock and hash index](frame-index.json)", ""]
    (output / "README.md").write_text("\n".join(lines), encoding="utf-8")
    print(f"[shot-attachment-evidence] PACKAGED cases={len(records)} output={output}")


if __name__ == "__main__":
    main()
