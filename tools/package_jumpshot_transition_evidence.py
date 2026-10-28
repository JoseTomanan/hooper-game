"""Package unchanged #440 rendered captures for review, without judging feel.

Example:
    python tools/package_jumpshot_transition_evidence.py \
        --before .godot/440/baseline-gameplay --after .godot/440/after-gameplay \
        --output docs/analysis/evidence/0440/gameplay --label local-gameplay

Whole source frames are resized into two panels. Labels occupy separate pixels;
the ball, bodies and court are never cropped, retouched or interpolated.
"""
from __future__ import annotations

import argparse
import gzip
import hashlib
import json
import re
from pathlib import Path

from PIL import Image, ImageDraw

from analyze_jumpshot_transitions import OBSERVER_ROLES, observations, pose_signature

ROOT = Path(__file__).resolve().parents[1]
EVIDENCE = ROOT / "docs/analysis/evidence/0440"
PANEL = (480, 270)
WIDTH, HEADER, FOOTER = 1000, 76, 30
HEIGHT = HEADER + PANEL[1] + FOOTER
BEFORE_ASSET_SHA256 = "E36A16BE4C3313A906518D922B0C48B7EBED614522F8379F5E0AD2390D5CDDA9"
BEFORE_SCENE_SHA256 = "061CA5B670D13BFD2D01EEDC9EB5088DD53CEB5C69526514230CFE1DA68E7995"
KNOWN_BEFORE_DEFECT = "changed final pose on duplicate source tick"


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8"))


def source_tick(row, remote):
    return row["selectedPresentationTick"] if remote else row["physicsFrame"]


def source_path(path):
    return str(path.resolve().relative_to(ROOT)).replace("\\", "/")


def read_capture(directory, *, before=False):
    """Select one observed image per adjacent source tick, including both seams."""
    manifest = read_json(directory / "manifest.json")
    if manifest.get("result") != "captured" or not manifest.get("images"):
        raise ValueError(f"capture incomplete or has no rendered images: {directory}")
    if manifest.get("role") == "server":
        raise ValueError(f"authority-only process has no rendered review surface: {directory}")
    remote = manifest["role"] in OBSERVER_ROLES
    raw = [json.loads(line) for line in (directory / "trace.jsonl").read_text(encoding="utf-8").splitlines()]
    shot = [row for row in raw if row.get("pose", {}).get("bones")
            and row["pose"].get("move") == "jumpshot" and row["pose"].get("phase") != "Inactive"
            and source_tick(row, remote) >= 0]
    if not shot:
        raise ValueError(f"no observed shot: {directory}")
    first, last = source_tick(shot[0], remote), source_tick(shot[-1], remote)
    # The selection includes the first evaluated new state and all observed
    # post-handoff poses; the eight neutral samples are not endpoint guesses.
    window = [row for row in raw if first - 8 <= source_tick(row, remote) <= last + 8]
    changed = []
    rows, failures, duplicates = observations(window, remote, changed)
    permitted_before = (before and
        manifest.get("animationResourceSha256") == BEFORE_ASSET_SHA256 and
        manifest.get("playerSceneSha256") == BEFORE_SCENE_SHA256)
    if failures and (not permitted_before or any(failure != KNOWN_BEFORE_DEFECT for failure in failures)):
        raise ValueError(f"invalid source-tick evidence in {directory}: {sorted(set(failures))}")
    # Keep the actual adjacent raw observations, not only the earliest pose used
    # by the source-tick projection. These are the reproduced baseline defect.
    raw_edges, previous = [], None
    for row in window:
        if not row.get("pose", {}).get("bones") or row["pose"].get("physicsFrame") != row.get("physicsFrame"):
            continue
        if remote and source_tick(row, True) < 0:
            continue
        if previous is not None and source_tick(row, remote) == source_tick(previous, remote) and pose_signature(row) != pose_signature(previous):
            raw_edges.append((previous, row))
        previous = row
    if len(raw_edges) != len(changed):
        raise ValueError(f"raw changed-edge inventory differs from continuity diagnostics: {directory}")
    expected = list(range(first - 8, last + 9))
    if [source_tick(row, remote) for row in rows] != expected:
        raise ValueError(f"missing/duplicate/reset-spanning rendered source ticks: {directory}")
    if len({row.get("presentationResyncCount", 0) for row in rows}) != 1:
        raise ValueError(f"presentation reset spans the review film: {directory}")
    if any(row["pose"].get("phase") != "Inactive" for row in rows[:8] + rows[-8:]):
        raise ValueError(f"eight adjacent neutral poses missing on either side: {directory}")
    keys = {(row["pose"]["phase"], row["pose"]["frame"]) for row in rows[8:-8]}
    feint = manifest["variant"] in ("early", "late")
    startup_end = (3 if manifest["variant"] == "early" else 11) if feint else 17
    required = {("Startup", frame) for frame in range(1, startup_end + 1)}
    required |= {("Recovery", frame) for frame in (range(13, 20) if feint else range(20))}
    if not feint:
        required |= {("Active", frame) for frame in range(4)}
    missing = required - keys
    if missing:
        raise ValueError(f"missing required rendered phases {sorted(missing)}: {directory}")
    for row in rows + [row for edge in raw_edges for row in edge]:
        if not row.get("file") or not (directory / row["file"]).is_file():
            raise ValueError(f"missing original PNG: {directory} sample {row.get('sample')}")
        if not all(key in row for key in ("camera", "cameraProvenance", "viewportSize")):
            raise ValueError(f"observed camera metadata missing: {directory}")
    return manifest, rows, duplicates, raw_edges


def metadata_matches(before, after):
    keys = ("role", "entry", "hand", "variant", "entryOffset", "view", "subjectRole",
            "paced", "pacingMs", "engine", "observerPacingMs")
    differing = [key for key in keys if before.get(key) != after.get(key)]
    if differing:
        raise ValueError("incomparable before/after input, view or pacing metadata: " + ", ".join(differing))


def event_clock(directory, manifest):
    authority = read_json(authority_manifest(directory, manifest))
    # The owner predicts on its own physics clock. Its observed Begin can align
    # its own film; a server physics-frame number cannot align an owner image.
    selected = manifest if manifest["role"] == "owner" else authority
    begins = [event for event in selected.get("events", []) if event.get("kind") == "accepted-begin-observed"]
    if len(begins) != 1 or "physicsFrame" not in begins[0]:
        raise ValueError(f"independent Begin event clock missing: {directory}")
    releases = [event for event in authority.get("events", []) if event.get("kind") == "release"]
    return {"kind": "predicted owner physics" if manifest["role"] == "owner" else "authority physics/source tick",
            "beginTick": begins[0]["physicsFrame"],
            "authorityReleaseEvents": releases,
            "releaseTick": releases[0]["physicsFrame"] if releases and manifest["role"] != "owner" else None}


def frame_reference(directory, row, remote, first, event_alignment):
    path = directory / row["file"]
    pose = {key: value for key, value in row["pose"].items() if key != "bones"}
    return {"source": source_path(path), "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
            "sample": row["sample"], "sourceTick": source_tick(row, remote),
            "relativeToFirstObservedStartupTick": source_tick(row, remote) - first,
            "eventClock": event_alignment["kind"],
            "relativeToObservedBeginTick": source_tick(row, remote) - event_alignment["beginTick"],
            "relativeToAuthorityReleaseTick": source_tick(row, remote) - event_alignment["releaseTick"]
                if event_alignment["releaseTick"] is not None else None,
            "physicsFrame": row["physicsFrame"], "pose": pose,
            "selectedPresentationTick": row.get("selectedPresentationTick"),
            "ballPresentationTick": row.get("ballPresentationTick"),
            "releaseCount": row.get("releaseCount"), "visibleBall": row.get("ball"),
            "camera": {key: row[key] for key in ("camera", "cameraProvenance", "viewportSize",
                "cameraFov", "cameraProjection", "cameraSize", "cameraNear", "cameraFar")}}


def pose_label(row):
    pose = row["pose"]
    state = pose.get("animation", row.get("animation", "?"))
    time = pose.get("playbackTime", row.get("playbackTime", 0))
    return f"{pose['phase']}/{pose['frame']}  {state}  clip t={time:.4f}s"


def draw_pair(before_dir, after_dir, old, new, title, relative_tick, pace, *, raw_clocks=False):
    canvas = Image.new("RGB", (WIDTH, HEIGHT), "#101820")
    draw = ImageDraw.Draw(canvas)
    draw.text((12, 7), title + f" | source tick {relative_tick:+d} from first observed Startup", fill="white")
    draw.text((12, 24), "BEFORE: current-main baseline", fill="#bddcff")
    draw.text((512, 24), "AFTER: #440 repair", fill="#bddcff")
    draw.text((12, 43), pose_label(old), fill="white")
    draw.text((512, 43), pose_label(new), fill="white")
    if raw_clocks:
        for x, row in ((12, old), (512, new)):
            tick = row.get("selectedPresentationTick", -1)
            if tick < 0: tick = row["physicsFrame"]
            draw.text((x, 59), f"source {tick} | physics {row['physicsFrame']} | {row['pose'].get('clip', row.get('clip', '?'))}", fill="white")
    for x, directory, row in ((10, before_dir, old), (510, after_dir, new)):
        with Image.open(directory / row["file"]) as source:
            if source.size != tuple(row["viewportSize"]) or source.size != (960, 540):
                raise ValueError(f"unexpected original PNG dimensions: {directory / row['file']}: {source.size}")
            # Whole-frame resampling; no crop or animation interpolation.
            # https://raw.githubusercontent.com/python-pillow/Pillow/12.0.0/docs/reference/Image.rst
            canvas.paste(source.convert("RGB").resize(PANEL, Image.Resampling.LANCZOS), (x, HEADER))
    draw.text((12, HEADER + PANEL[1] + 7), pace + " | whole source frames; feel remains human review", fill="#dde6ed")
    return canvas


def gzip_copy(source, destination):
    # filename="" and mtime=0 make byte-identical inputs reproducible.
    with destination.open("wb") as output:
        with gzip.GzipFile(filename="", fileobj=output, mode="wb", mtime=0) as compressed:
            compressed.write(source.read_bytes())


def authority_manifest(directory, manifest):
    if manifest["role"] == "local":
        return directory / "manifest.json"
    primary = directory.name.removesuffix("-owner")
    path = directory.parent / (primary + "-server") / "manifest.json"
    if not path.is_file():
        raise ValueError(f"independent authority manifest missing: {path}")
    return path


def milestones(manifest, rows):
    by_pose = {(row["pose"]["phase"], row["pose"]["frame"]): i for i, row in enumerate(rows[8:-8], 8)}
    if manifest["variant"] == "normal":
        keys = [("Startup", 8), ("Active", 0), ("Recovery", 2), ("Recovery", 19)]
    else:
        keys = [("Startup", 3 if manifest["variant"] == "early" else 11),
                ("Recovery", 13), ("Recovery", 16), ("Recovery", 19)]
    return [7] + [by_pose[key] for key in keys] + [len(rows) - 6]


def package_case(before_dir, after_dir, output, label):
    before_manifest, before, before_holds, before_edges = read_capture(before_dir, before=True)
    after_manifest, after, after_holds, _ = read_capture(after_dir)
    metadata_matches(before_manifest, after_manifest)
    if len(before) != len(after):
        raise ValueError(f"unequal observed shot timelines require separate diagnosis: {before_dir.name}")
    stem = label + "-" + before_dir.name
    remote = before_manifest["role"] in OBSERVER_ROLES
    before_first, after_first = source_tick(before[8], remote), source_tick(after[8], remote)
    before_events = event_clock(before_dir, before_manifest)
    after_events = event_clock(after_dir, after_manifest)
    if [source_tick(row, remote) - before_first for row in before] != [source_tick(row, remote) - after_first for row in after]:
        raise ValueError(f"nonmatching source-tick offsets: {stem}")
    if before_first - before_events["beginTick"] != after_first - after_events["beginTick"]:
        raise ValueError(f"nonmatching source tick versus independently observed Begin clock: {stem}")
    title = f"{label} | {before_manifest['hand']} {before_manifest['entry']} {before_manifest['variant']} | {before_manifest['role']}"
    pacing = f"capture producer {before_manifest.get('pacingMs', 0)}ms/tick" if before_manifest.get("paced") else "capture unpaced"
    normal, slow, records = [], [], []
    for i, (old, new) in enumerate(zip(before, after)):
        relative = source_tick(old, remote) - before_first
        normal.append(draw_pair(before_dir, after_dir, old, new, title, relative,
                                "60Hz source-tick projection; raw baseline held edges in linked diagnostic; " + pacing))
        slow.append(draw_pair(before_dir, after_dir, old, new, title, relative,
                              "2.4x slow motion (40ms per source tick); " + pacing))
        records.append({"outputFrame": i, "alignment": relative,
                        "before": frame_reference(before_dir, old, remote, before_first, before_events),
                        "after": frame_reference(after_dir, new, remote, after_first, after_events)})
    durations = [(17, 17, 16)[i % 3] for i in range(len(normal))]
    # Pillow12 documents per-frame durations and lossless animated WebP. Its
    # tagged encoder confirms that duration lists accumulate integer timestamps.
    # https://raw.githubusercontent.com/python-pillow/Pillow/12.0.0/docs/handbook/image-file-formats.rst
    # https://raw.githubusercontent.com/python-pillow/Pillow/12.0.0/src/PIL/WebPImagePlugin.py
    normal[0].save(output / (stem + "-normal.webp"), save_all=True, append_images=normal[1:],
                   duration=durations, loop=0, lossless=True, method=6)
    slow[0].save(output / (stem + "-slow.gif"), save_all=True, append_images=slow[1:],
                 duration=40, loop=0, disposal=2, optimize=False)
    selected = milestones(before_manifest, before)
    sheet = Image.new("RGB", (WIDTH, HEIGHT * len(selected)), "#101820")
    for index, frame in enumerate(selected):
        sheet.paste(normal[frame], (0, HEIGHT * index))
    sheet.save(output / (stem + "-contact.png"))
    edge_records = []
    diagnostic = []
    after_by_relative = {source_tick(row, remote) - after_first: row for row in after}
    for edge_index, (old_from, old_to) in enumerate(before_edges):
        relative = source_tick(old_from, remote) - before_first
        new = after_by_relative[relative]
        edge_records.append({"edge": edge_index, "relativeSourceTick": relative,
            "beforeFrom": frame_reference(before_dir, old_from, remote, before_first, before_events),
            "beforeTo": frame_reference(before_dir, old_to, remote, before_first, before_events),
            "afterCanonicalHeldStatic": frame_reference(after_dir, new, remote, after_first, after_events)})
        for edge_end, old in (("FROM", old_from), ("TO", old_to)):
            edge_title = title + f" | raw held edge {edge_index + 1}/{len(before_edges)} {edge_end}"
            diagnostic.append(draw_pair(before_dir, after_dir, old, new, edge_title, relative,
                "diagnostic 500ms/frame; original raw BEFORE observations; canonical AFTER same source tick held static", raw_clocks=True))
    diagnostic_file = None
    if diagnostic:
        diagnostic_file = stem + "-raw-held-edges.gif"
        diagnostic[0].save(output / diagnostic_file, save_all=True, append_images=diagnostic[1:],
                           duration=500, loop=0, disposal=2, optimize=False)
    archives = {}
    for prefix, directory, manifest in (("before", before_dir, before_manifest), ("after", after_dir, after_manifest)):
        archives[prefix] = {}
        for name in ("manifest.json", "trace.jsonl"):
            destination = output / f"{stem}-{prefix}-{name}.gz"
            gzip_copy(directory / name, destination)
            archives[prefix][name] = {"file": destination.name,
                "sourceSha256": hashlib.sha256((directory / name).read_bytes()).hexdigest()}
        auth = authority_manifest(directory, manifest)
        destination = output / f"{stem}-{prefix}-authority-manifest.json.gz"
        gzip_copy(auth, destination)
        archives[prefix]["authorityManifest"] = {"file": destination.name,
            "source": source_path(auth), "sourceSha256": hashlib.sha256(auth.read_bytes()).hexdigest()}
    return {"case": stem, "normalSpeedFilm": stem + "-normal.webp", "slowMotionFilm": stem + "-slow.gif",
            "contactSheet": stem + "-contact.png", "contactSheetFrameIndexes": selected,
            "beforeManifest": before_manifest, "afterManifest": after_manifest,
            "beforeEventClock": before_events, "afterEventClock": after_events,
            "beforeDuplicateHoldSamples": before_holds, "afterDuplicateHoldSamples": after_holds,
            "sourceTickProjection": "earliest evaluated observation per source tick; baseline changed held edges are omitted ONLY from this projection and shown in the raw diagnostic",
            "beforeRawChangedHeldEdgesFilm": diagnostic_file, "beforeRawChangedHeldEdges": edge_records,
            "rawHeldEdgePlayback": "500ms per original adjacent observation; after canonical same-relative-source-tick pose held static; diagnostic only",
            "frames": records, "normalFrameDurationsMs": durations, "archives": archives}


def cases_in(directory):
    if (directory / "manifest.json").is_file():
        return {directory.name: directory}
    return {path.parent.name: path.parent for path in directory.glob("*/manifest.json")
            if not path.parent.name.endswith("-server")}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--before", type=Path, required=True)
    parser.add_argument("--after", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True, help="fresh evidence subdirectory below docs/analysis/evidence/0440")
    parser.add_argument("--label", required=True, help="filename-safe view/topology label, e.g. local-gameplay or owner-gameplay")
    parser.add_argument("--case", action="append", help="optional exact capture directory name")
    args = parser.parse_args()
    if not re.fullmatch(r"[a-z0-9]+(?:-[a-z0-9]+)*", args.label):
        parser.error("label must contain lowercase words/numbers separated by hyphens")
    output = args.output.resolve()
    if output == EVIDENCE or not output.is_relative_to(EVIDENCE):
        parser.error("output must be a fresh subdirectory of docs/analysis/evidence/0440")
    if output.exists():
        parser.error("output already exists; refusing to overwrite evidence")
    old, new = cases_in(args.before), cases_in(args.after)
    selected = sorted(args.case or old)
    if not selected or set(selected) - set(old) or set(selected) - set(new):
        parser.error("missing matching before/after cases")
    if not args.case and set(old) != set(new):
        parser.error("before/after case inventory differs")
    # Validate everything before creating artifacts; a missing image cannot
    # produce a partial directory that looks like an accepted comparison.
    for name in selected:
        before_manifest, before, _, _ = read_capture(old[name], before=True)
        after_manifest, after, _, _ = read_capture(new[name])
        metadata_matches(before_manifest, after_manifest)
        if len(before) != len(after):
            parser.error("incomparable observed timeline length: " + name)
        for directory, manifest in ((old[name], before_manifest), (new[name], after_manifest)):
            authority_manifest(directory, manifest)
            event_clock(directory, manifest)
    output.mkdir(parents=True)
    records = [package_case(old[name], new[name], output, args.label) for name in selected]
    index = {"schema": "hooper-jumpshot-transition-rendered-evidence/v1",
             "imageTreatment": "whole 960x540 original PNGs resized to 480x270; labels outside source panels; no crop, pose editing or temporal interpolation",
             "alignment": "consecutive source ticks relative to first observed Startup; owner local physics clock remains separate from authority events; actual before/after playback offsets retained",
             "normalPlayback": "60Hz simulation presentation using 17/17/16ms durations; paced capture is not latency/performance evidence",
             "sourceTickProjection": "main films use the earliest evaluated pose per source tick; they do not reproduce changed baseline poses on held ticks; every such raw adjacent edge is retained in linked 500ms/frame diagnostics and indexed with original PNG hashes",
             "slowPlayback": "40ms per observed source tick, 2.4x slow motion",
             "feelVerdict": "unaccepted; artifacts for human review in #173", "cases": records}
    (output / "frame-index.json").write_text(json.dumps(index, indent=2) + "\n", encoding="utf-8")
    readme = ["# #440 rendered comparisons", "",
              "Each panel preserves the complete production frame, including the ball. The normal films show consecutive simulation source ticks at 60Hz (17/17/16ms frame durations); the GIFs show the same poses at 2.4x slow motion (40ms each). Paced network captures are retimed for this presentation and establish no latency or performance result.", "",
              "Before and after retain their own actual clip positions and independent release events. The index includes original PNG SHA256 hashes, observed cameras, source ticks, playback offsets and compressed raw traces/manifests. Owning-client physics ticks are never joined to server event ticks.", "",
              "The main films project the earliest evaluated observation per source tick. This projection omits the baseline's changed poses on held source ticks and must not be read as showing that the old playback was continuous. Every changed raw adjacent baseline edge appears in the linked diagnostic film at 500ms per original observation, paired with the repaired canonical pose at the same relative source tick held static. Those diagnostics label the local physics frames and actual playback state/time; their full original frame references and SHA256 hashes remain in the index. Only the named defect in the exact frozen baseline assets/scene is permitted; repaired captures reject it.", "",
              "Appearance and smoothness remain unaccepted for the human pass in #173. The full visible ball/wrist relationship remains in the source images.", "",
              "| Case | 60Hz source-tick projection | 2.4x slow projection | Contact sheet | Raw baseline held edges (500ms/frame) |",
              "|---|---|---|---|---|"]
    for record in records:
        raw_link = f"[GIF]({record['beforeRawChangedHeldEdgesFilm']})" if record['beforeRawChangedHeldEdgesFilm'] else "none observed"
        readme.append(f"| {record['case']} | [WebP]({record['normalSpeedFilm']}) | [GIF]({record['slowMotionFilm']}) | [PNG]({record['contactSheet']}) | {raw_link} |")
    readme += ["", "[Source frame and clock index](frame-index.json)", ""]
    (output / "README.md").write_text("\n".join(readme), encoding="utf-8")
    print(f"[jumpshot-evidence] PACKAGED cases={len(records)} output={output}")


if __name__ == "__main__":
    main()
