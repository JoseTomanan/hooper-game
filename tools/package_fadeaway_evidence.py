"""Package exact matched #429 rendered observations; this makes no taste verdict."""
import argparse
import json
from pathlib import Path

from PIL import Image, ImageDraw

from package_jumpshot_evidence import frame_reference, gzip_copy, read_capture

ROOT = Path(__file__).resolve().parents[1]


def package(pair, output):
    before_dir, after_dir = (ROOT / pair[k] for k in ("before", "after"))
    before_manifest, before = read_capture(before_dir, pair["remote"])
    after_manifest, after = read_capture(after_dir, pair["remote"])
    if [key for key, _ in before] != [key for key, _ in after]:
        raise ValueError("phase alignment differs: " + pair["name"])
    frames, records = [], []
    for index, ((label, old), (_, new)) in enumerate(zip(before, after)):
        canvas = Image.new("RGB", (1024, 334), "#111821")
        draw = ImageDraw.Draw(canvas)
        draw.text((16, 7), pair["name"] + " | " + label + " | 2.4x slow motion", fill="white")
        draw.text((16, 25), "BEFORE: original shot presentation", fill="#b7d8ff")
        draw.text((528, 25), "AFTER: #429 fadeaway / preserved controls", fill="#b7d8ff")
        for x, directory, row in ((16, before_dir, old), (528, after_dir, new)):
            with Image.open(directory / row["file"]) as image:
                if image.size != (960, 540):
                    raise ValueError("source dimensions changed")
                # Full frames, no cropping or pose edits. Labels sit outside
                # the source pixels. Official Pillow resize and GIF interfaces:
                # https://github.com/python-pillow/Pillow/blob/12.0.0/src/PIL/Image.py
                # https://github.com/python-pillow/Pillow/blob/12.0.0/docs/handbook/image-file-formats.rst#gif
                canvas.paste(image.convert("RGB").resize((480, 270), Image.Resampling.LANCZOS), (x, 48))
        frames.append(canvas)
        records.append(dict(outputFrame=index, alignment=label,
            before=frame_reference(before_dir, old), after=frame_reference(after_dir, new)))
    name = pair["name"]
    frames[0].save(output / (name + ".gif"), save_all=True, append_images=frames[1:],
                   duration=40, loop=0, disposal=2, optimize=False)
    variant = before_manifest["variant"]
    milestones = (["entry", "Startup:17", "Active:0", "Active:2", "Active:3", "Recovery:0", "neutral:8"]
                  if variant in ("normal", "fadeaway") else
                  ["entry", "Startup:" + ("3" if variant == "early" else "11"),
                   "Recovery:13", "Recovery:16", "Recovery:19", "neutral:1", "neutral:8"])
    indices = {record["alignment"]: i for i, record in enumerate(records)}
    sheet = Image.new("RGB", (1024, 334 * len(milestones)), "#111821")
    for i, key in enumerate(milestones):
        sheet.paste(frames[indices[key]], (0, i * 334))
    sheet.save(output / (name + ".png"))
    # Preserve exact PNG bytes for measured core poses and representative
    # control poses. The full sequence is in the labelled film; the index also
    # records original per-frame hashes and scratch paths for reproduction.
    retained = ({"Active:2", "Active:3"} if variant == "fadeaway" else
                {"Active:2"} if variant == "normal" else
                {"Recovery:13", "Recovery:19"})
    for prefix, directory, aligned in (("before", before_dir, before), ("after", after_dir, after)):
        originals = output / "originals" / name / prefix
        originals.mkdir(parents=True, exist_ok=True)
        for label, row in aligned:
            if label not in retained:
                continue
            data = (directory / row["file"]).read_bytes()
            (originals / row["file"]).write_bytes(data)
            record = next(r for r in records if r["alignment"] == label)
            record[prefix]["retainedOriginal"] = str((originals / row["file"]).relative_to(output)).replace("\\", "/")
        for filename in ("manifest.json", "trace.jsonl"):
            gzip_copy(directory / filename, output / f"{name}-{prefix}-{filename}.gz")
        server = Path(str(directory) + "-server")
        if pair["remote"]:
            for filename in ("manifest.json", "trace.jsonl"):
                gzip_copy(server / filename, output / f"{name}-{prefix}-server-{filename}.gz")
    return dict(name=name, remote=pair["remote"], gif=name + ".gif", contactSheet=name + ".png",
                beforeManifest=before_manifest, afterManifest=after_manifest, frames=records)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plan", type=Path, required=True)
    parser.add_argument("--output", type=Path, default=ROOT / "docs/analysis/evidence/0429")
    args = parser.parse_args()
    pairs = json.loads(args.plan.read_text())
    output = args.output.resolve()
    if not output.is_relative_to(ROOT / "docs/analysis/evidence/0429"):
        parser.error("output must be below docs/analysis/evidence/0429")
    for pair in pairs:
        manifests = [read_capture(ROOT / pair[key], pair["remote"])[0]
                     for key in ("before", "after")]
        for field in ("hand", "variant", "view", "role", "paced"):
            if manifests[0][field] != manifests[1][field]:
                raise ValueError("incompatible before/after " + field + ": " + pair["name"])
        if manifests[0]["role"] != ("client" if pair["remote"] else "local"):
            raise ValueError("pair topology does not match observed role: " + pair["name"])
        pacing = [m.get("pacingMs", 80 if m["paced"] else 0) for m in manifests]
        if pacing[0] != pacing[1]:
            raise ValueError("before/after diagnostic pacing differs: " + pair["name"])
    output.mkdir(parents=True, exist_ok=True)
    cases = [package(pair, output) for pair in pairs]
    (output / "frame-index.json").write_text(json.dumps(dict(
        schema="hooper-fadeaway-evidence/v1", cases=cases, verdict="review only; #185/#173 remain open",
        sourcePixels="selected uncropped 960x540 core/control originals retained; complete review pairs resized to 480x270 with labels outside",
        playback="40ms per 60Hz source pose = 2.4x slow; paced remote is diagnostic, not real-time"), indent=2))
    print(f"PACKAGED {len(cases)} matched cases: {output}")


if __name__ == "__main__":
    main()
