"""Publish rejected owner rendering separately from accepted continuity proof."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

from package_jumpshot_transition_evidence import (
    read_json, BEFORE_ASSET_SHA256, BEFORE_SCENE_SHA256, EVIDENCE,
    WIDTH, HEIGHT, authority_manifest, draw_pair, event_clock, frame_reference,
    gzip_copy, metadata_matches,
)
from analyze_jumpshot_transitions import pose_signature


def read_owner(directory, *, before=False):
    manifest = read_json(directory / "manifest.json")
    if manifest.get("role") != "owner" or manifest.get("subjectRole") != "owner":
        raise ValueError("diagnostics require owning predicted rendering")
    if before and (manifest.get("animationResourceSha256") != BEFORE_ASSET_SHA256 or
                   manifest.get("playerSceneSha256") != BEFORE_SCENE_SHA256):
        raise ValueError("frozen before asset/scene hashes differ")
    if manifest.get("result") != "captured" or not manifest.get("images") or manifest.get("variant") != "normal":
        raise ValueError("complete rendered normal owner capture required")
    raw = [json.loads(line) for line in (directory / "trace.jsonl").read_text(encoding="utf-8").splitlines()]
    shots = [row for row in raw if row.get("pose", {}).get("move") == "jumpshot"
             and row["pose"].get("phase") != "Inactive"]
    if not shots:
        raise ValueError("no owner shot observations")
    first, last = shots[0]["physicsFrame"] - 8, shots[-1]["physicsFrame"] + 8
    rows = [row for row in raw if first <= row["physicsFrame"] <= last]
    for a, b in zip(rows, rows[1:]):
        if a["physicsFrame"] == b["physicsFrame"] and pose_signature(a) != pose_signature(b):
            raise ValueError("changed duplicate owner pose")
    if [row["physicsFrame"] for row in rows] != list(range(first, last + 1)):
        raise ValueError("missing/nonconsecutive owner physics ticks")
    for row in rows:
        if not row.get("file") or not (directory / row["file"]).is_file():
            raise ValueError("missing original PNG: " + str(directory / row.get("file", "missing")))
        if (not row.get("locallyPredicted") or not row.get("pose", {}).get("bones") or
                row["pose"].get("physicsFrame") != row["physicsFrame"]):
            raise ValueError("missing same-tick owning predicted final pose")
        if not all(key in row for key in ("camera", "cameraProvenance", "viewportSize", "presentationResyncCount")):
            raise ValueError("missing owner camera/reset metadata")
    required = {(phase, frame) for phase, frames in
                (("Startup", range(1, 18)), ("Active", range(4)), ("Recovery", range(20))) for frame in frames}
    if required - {(row["pose"]["phase"], row["pose"]["frame"]) for row in rows}:
        raise ValueError("missing normal owner phase observations")
    if any(row["pose"].get("phase") != "Inactive" for row in rows[:8] + rows[-8:]):
        raise ValueError("missing eight actual neutral owner neighbors")
    frames = []
    previous_rows = [row for row in raw if row["physicsFrame"] == first - 1]
    previous = previous_rows[-1].get("presentationResyncCount") if previous_rows else None
    for index, row in enumerate(rows):
        count = row["presentationResyncCount"]
        frames.append({"outputFrame": index, "physicsFrame": row["physicsFrame"], "resetCount": count,
                       "previousResetCount": previous, "resetEvent": previous is not None and previous != count})
        previous = count
    return manifest, rows, {"status": "rejected",
        "ownerRenderedContinuityVerdict": "not established by this rendered diagnostic", "frames": frames}


def reset_label(marker):
    label = "ball reset count=" + str(marker["resetCount"])
    if marker["resetEvent"]:
        label += f" | RESET {marker['previousResetCount']}->{marker['resetCount']}"
    return label


def package_case(before_dir, after_dir, output):
    # Standard-library CI can import and validate this tool without Pillow.
    from PIL import Image, ImageDraw

    old_manifest, old, old_record = read_owner(before_dir, before=True)
    new_manifest, new, new_record = read_owner(after_dir)
    if not all(any(frame["resetEvent"] for frame in record["frames"]) for record in (old_record, new_record)):
        raise ValueError("rejected reset diagnostic requires actual reset events in both captures")
    metadata_matches(old_manifest, new_manifest)
    if len(old) != len(new):
        raise ValueError("unequal owner timelines; cannot preserve every observation in paired film")
    old_clock, new_clock = event_clock(before_dir, old_manifest), event_clock(after_dir, new_manifest)
    old_first, new_first = old[8]["physicsFrame"], new[8]["physicsFrame"]
    if old_first - old_clock["beginTick"] != new_first - new_clock["beginTick"]:
        raise ValueError("independent predicted Begin alignment differs")
    stem = "rejected-owner-" + before_dir.name.removesuffix("-owner")
    title = "REJECTED OWNER DIAGNOSTIC | " + old_manifest["hand"]
    images, records = [], []
    for index, (a, b) in enumerate(zip(old, new)):
        relative = a["physicsFrame"] - old_first
        if relative != b["physicsFrame"] - new_first:
            raise ValueError("unequal owner physics offsets")
        canvas = draw_pair(before_dir, after_dir, a, b, title, relative,
            "REJECTED: ball presentation resets; NO rendered-owner continuity verdict; 60Hz owner physics playback")
        labels = ImageDraw.Draw(canvas)
        for x, marker in ((12, old_record["frames"][index]), (512, new_record["frames"][index])):
            labels.text((x, 59), reset_label(marker), fill="#ffaaaa" if marker["resetEvent"] else "#ffddaa")
        images.append(canvas)
        records.append({"outputFrame": index, "predictedPhysicsOffset": relative,
            "before": {**frame_reference(before_dir, a, False, old_first, old_clock),
                       **old_record["frames"][index], "externalResetLabel": reset_label(old_record["frames"][index])},
            "after": {**frame_reference(after_dir, b, False, new_first, new_clock),
                      **new_record["frames"][index], "externalResetLabel": reset_label(new_record["frames"][index])}})
    durations = [(17, 17, 16)[i % 3] for i in range(len(images))]
    normal = stem + "-normal.webp"
    images[0].save(output / normal, save_all=True, append_images=images[1:],
                   duration=durations, loop=0, lossless=True, method=6)
    selected = sorted({0, 7, 8, len(images)-9, len(images)-8, len(images)-1} |
                      {i for i in range(len(images)) if old_record["frames"][i]["resetEvent"] or
                       new_record["frames"][i]["resetEvent"]})
    sheet = Image.new("RGB", (WIDTH, HEIGHT * len(selected)), "#101820")
    for index, frame in enumerate(selected):
        sheet.paste(images[frame], (0, HEIGHT * index))
    contact = stem + "-contact.png"
    sheet.save(output / contact)
    archives = {}
    for prefix, directory, manifest in (("before", before_dir, old_manifest), ("after", after_dir, new_manifest)):
        archives[prefix] = {}
        for label, source in (("manifest.json", directory / "manifest.json"),
                              ("trace.jsonl", directory / "trace.jsonl"),
                              ("authority-manifest.json", authority_manifest(directory, manifest))):
            name = stem + "-" + prefix + "-" + label + ".gz"
            gzip_copy(source, output / name)
            archives[prefix][label] = {"file": name, "sourceSha256": hashlib.sha256(source.read_bytes()).hexdigest()}
    return {"case": stem, "status": "rejected", "reason": "ball presentation resets within owner shot window",
        "ownerRenderedContinuityVerdict": "not established by this rendered diagnostic",
        "normalSpeedDiagnostic": normal, "contactSheet": contact, "contactSheetFrameIndexes": selected,
        "beforeManifest": old_manifest, "afterManifest": new_manifest,
        "beforeEventClock": old_clock, "afterEventClock": new_clock,
        "beforeResetEvents": [m for m in old_record["frames"] if m["resetEvent"]],
        "afterResetEvents": [m for m in new_record["frames"] if m["resetEvent"]],
        "frames": records, "frameDurationsMs": durations, "archives": archives}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--before", type=Path, required=True)
    parser.add_argument("--after", type=Path, required=True)
    parser.add_argument("--output", type=Path, default=EVIDENCE / "owner-diagnostics")
    args = parser.parse_args()
    output = args.output.resolve()
    allowed = (EVIDENCE / "owner-diagnostics").resolve()
    if output != allowed and not output.is_relative_to(allowed):
        parser.error("output must be owner-diagnostics or a fresh subdirectory")
    if output.exists():
        parser.error("output exists; refusing to overwrite diagnostic artifacts")
    names = [hand + "-dribble-normal-o0-1-owner" for hand in ("left", "right")]
    # Refuse malformed sources before producing an apparently complete directory.
    for name in names:
        a, _, _ = read_owner(args.before / name, before=True)
        b, _, _ = read_owner(args.after / name)
        metadata_matches(a, b)
        event_clock(args.before / name, a)
        event_clock(args.after / name, b)
    output.mkdir(parents=True)
    cases = [package_case(args.before / name, args.after / name, output) for name in names]
    index = {"schema": "hooper-rejected-owner-rendered-diagnostics/v1", "status": "rejected",
        "ownerRenderedContinuityVerdict": "not established by this rendered diagnostic",
        "reason": "ball presentation reset count changes within these rendered shot windows",
        "imageTreatment": "whole original 960x540 PNGs resized to 480x270; external labels; no crop or ball/pose editing",
        "observations": "every consecutive owner physics tick across shot plus eight neutral neighbors; no omitted observations or temporal interpolation",
        "clockMeaning": "paired relative owning predicted physics clocks; server authority release events remain independent and are never joined to owner physics",
        "playback": "60Hz owner physics playback, 17/17/16ms; paced capture establishes no latency/performance result",
        "acceptedProof": "separate headless 108-role matrix; these films supply no continuity acceptance or numeric before/after gate",
        "cases": cases}
    (output / "frame-index.json").write_text(json.dumps(index, indent=2) + "\n", encoding="utf-8")
    lines = ["# REJECTED OWNER DIAGNOSTIC", "",
        "These owning predicted gameplay films are **rejected for continuity** because the ball presentation reset count changes within each shot window. They show rendered owner behavior and provide **no rendered-owner continuity verdict**. They are separate from the accepted headless 108-role matrix and its owner continuity proof.", "",
        "Every consecutive actual owner physics tick across the shot and eight neutral neighbors is retained. Reset counts appear on every frame; each reset event is marked RESET in the external labels and index. Panels preserve the whole source image, including the ball; there is no crop, ball editing, frame cleansing or interpolation.", "",
        "Playback uses 17/17/16ms per owner physics tick (60Hz). These paced captures are not latency/performance evidence. Relative owner predicted physics clocks align the panels; independent authority release events remain separate in the index. Appearance and smoothness remain unaccepted in #173.", "",
        "| Rejected case | Normal-speed diagnostic | Contact sheet |", "|---|---|---|"]
    lines += [f"| {case['case']} | [WebP]({case['normalSpeedDiagnostic']}) | [PNG]({case['contactSheet']}) |" for case in cases]
    lines += ["", "[Raw traces, manifests, PNG hashes, independent clocks and reset markers](frame-index.json)", ""]
    (output / "README.md").write_text("\n".join(lines), encoding="utf-8")
    print(f"[owner-diagnostics] REJECTED cases={len(cases)} frames={sum(len(c['frames']) for c in cases)} output={output}")


if __name__ == "__main__":
    main()
