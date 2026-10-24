"""Retain #428's complete measured traces and selected original rendered PNGs.

All source files remain in place. Local JSON/log gzip copies are lossless.
Capture traces retain every observed JumpShot row (including holds and all
52 bones) and one neutral row on each side. retention.json records the exact
selection and both source/selection hashes. Contact sheets resize whole
viewports and place labels outside them; they do not accept visual quality.
"""
import argparse
import gzip
import hashlib
import json
import math
from pathlib import Path

from PIL import Image, ImageDraw

from analyze_shot_heading_capture import read_rows, rendered_metrics

ROOT = Path(__file__).resolve().parents[1]
TARGETS = (("Startup", 1), ("Startup", 6), ("Startup", 12),
           ("Startup", 17), ("Active", 0), ("Recovery", 19))


def sha(data):
    return hashlib.sha256(data).hexdigest()


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8", newline="\n")


def verify(output):
    """Verify both retained bytes and exact selection against untouched sources."""
    selections = {entry["retained"]: entry for entry in json.loads((output / "retention.json").read_text())}
    originals = json.loads((output / "originals.json").read_text())
    for entry in originals:
        source = (ROOT / entry["source"]).read_bytes()
        retained = (output / entry["retained"]).read_bytes()
        if sha(source) != entry["originalSha256"] or sha(retained) != entry["retainedSha256"]:
            raise ValueError(f"source or retained hash changed: {entry['retained']}")
        payload = gzip.decompress(retained) if entry["compression"].startswith("gzip") else retained
        if entry["retained"] in selections:
            selection = selections[entry["retained"]]
            lines = source.splitlines(keepends=True)
            expected = b"".join(lines[index] for index in selection["selectedRowIndices"])
            if len(lines) != selection["fullRowCount"] or sha(payload) != selection["selectedSha256"]:
                raise ValueError(f"selection metadata changed: {entry['retained']}")
            full_shots = sum(json.loads(line)["move"] == "jumpshot" for line in lines)
            selected_shots = sum(json.loads(line)["move"] == "jumpshot" for line in payload.splitlines())
            if full_shots != selected_shots:
                raise ValueError(f"shot observations were dropped: {entry['retained']}")
        else:
            expected = source
        if payload != expected:
            raise ValueError(f"retained bytes differ from declared source/selection: {entry['retained']}")
    for line in (output / "SHA256SUMS").read_text().splitlines():
        expected, name = line.split("  ", 1)
        if sha((output / name).read_bytes()) != expected:
            raise ValueError(f"package checksum changed: {name}")
    print(f"VERIFIED {len(originals)} originals/selections; {len(selections)} complete shot-row selections")


class Package:
    def __init__(self, source, output):
        self.source, self.output = source, output
        self.originals = []
        self.retention = []

    def relative_source(self, path):
        return path.resolve().relative_to(ROOT).as_posix()

    def copy(self, source, target, compress=False, selected=None):
        target = self.output / target
        target.parent.mkdir(parents=True, exist_ok=True)
        data = source.read_bytes()
        retained_input = data if selected is None else selected
        retained = gzip.compress(retained_input, compresslevel=9, mtime=0) if compress else retained_input
        target.write_bytes(retained)
        self.originals.append({"source": self.relative_source(source),
                               "retained": target.relative_to(self.output).as_posix(),
                               "originalSha256": sha(data), "retainedSha256": sha(retained),
                               "originalBytes": len(data), "retainedBytes": len(retained),
                               "compression": "gzip-mtime0-selected-rows" if selected is not None else "gzip-mtime0" if compress else "unchanged"})

    def trace(self, source, target):
        data = source.read_bytes()
        lines = data.splitlines(keepends=True)
        rows = [json.loads(line) for line in lines]
        shot_indices = [index for index, row in enumerate(rows) if row["move"] == "jumpshot"]
        if not shot_indices:
            raise ValueError(f"no JumpShot observations in trace: {source}")
        selected_indices = set(shot_indices)
        if shot_indices[0] > 0:
            selected_indices.add(shot_indices[0] - 1)
        if shot_indices[-1] + 1 < len(rows):
            selected_indices.add(shot_indices[-1] + 1)
        selected_indices = sorted(selected_indices)
        selected = b"".join(lines[index] for index in selected_indices)
        self.copy(source, target, True, selected)
        self.retention.append({
            "source": self.relative_source(source), "retained": Path(target).as_posix(),
            "originalSha256": sha(data), "selectedSha256": sha(selected),
            "fullRowCount": len(rows), "selectedRowCount": len(selected_indices),
            "selectedRowIndices": selected_indices,
            "selectionPolicy": "Every observed move==jumpshot row, including repeated held rows and all final-pose bones; one row immediately before the first and after the last shot observation when present. Full raw source remains untouched in .godot.",
        })

    def local(self, folder, target):
        files = sorted(folder.glob("*.json"))
        if not files:
            raise ValueError(f"missing local evidence: {folder}")
        for path in files:
            self.copy(path, Path(target) / (path.name + ".gz"), True)

    def capture(self, source, target):
        for role in ("server", "client"):
            manifest = source / role / "manifest.json"
            record = json.loads(manifest.read_text())
            if record["result"] != "captured":
                raise ValueError(f"failed capture: {source / role}")
            self.copy(manifest, Path(target) / role / "manifest.json")
            self.trace(source / role / "trace.jsonl", Path(target) / role / "trace.jsonl.gz")
        for path in sorted(source.glob("*.log")):
            self.copy(path, Path(target) / (path.name + ".gz"), True)

    def matrix(self):
        chosen = []
        for angle in (0, 45, 89, 90, 91, 135, 180):
            for entry in ("stationary", "moving"):
                for hand in ("Left", "Right"):
                    name = f"{angle}-{entry}-{hand}"
                    baseline = self.source / "net-before-final" / name
                    supplement = self.source / "net-before-supplement-v2" / name
                    if name == "91-moving-Left":
                        if not supplement.is_dir():
                            raise ValueError("required 91-moving-Left before supplement is not ready")
                        baseline = supplement
                        # Keep the original reset/missing-phase diagnostic too;
                        # it is not silently substituted into the passing proof.
                        self.capture(self.source / "net-before-final" / name,
                                     Path("diagnostics/original-before-reset") / name)
                    self.capture(baseline, Path("matrix/before") / name)
                    self.capture(self.source / "net-after-final" / name, Path("matrix/after") / name)
                    chosen.append({"case": name, "beforeSource": self.relative_source(baseline),
                                   "afterSource": self.relative_source(self.source / "net-after-final" / name)})
        write_json(self.output / "matrix-sources.json", chosen)

    def rendered(self):
        pairs = {
            "stationary-right": (
                self.source / "render-before-fixed-180-right/180-stationary-Right",
                self.source / "render-after-fixed-180-right/180-stationary-Right"),
            "moving-left": (
                self.source / "render-before-moving-v4/180-moving-Left",
                self.source / "render-after-moving-v3/180-moving-Left"),
        }
        metrics, selections = {}, []
        for name, sources in pairs.items():
            # Whole viewport at half resolution, with caption regions outside
            # image content. Pillow12 API:
            # https://pillow.readthedocs.io/en/12.0.0/reference/Image.html#PIL.Image.Image.thumbnail
            sheet = Image.new("RGB", (6 * 480, 2 * (270 + 44) + 32), "#171a20")
            draw = ImageDraw.Draw(sheet)
            draw.text((8, 8), f"#428 {name}: actual separate ENet observer; gameplay camera; no feel acceptance", fill="white")
            metrics[name] = {}
            cameras = []
            for row_index, (version, folder) in enumerate(zip(("before", "after"), sources)):
                target = Path("rendered") / name / version
                self.capture(folder, target)
                measurement = rendered_metrics(folder)
                metrics[name][version] = measurement
                cameras.append((measurement["camera"], measurement["viewport"]))
                rows = read_rows(folder / "client")
                for column, (phase, frame) in enumerate(TARGETS):
                    candidates = [sample for sample in rows
                                  if sample["move"] == "jumpshot" and sample["phase"] == phase
                                  and sample["frame"] == frame and sample["file"]
                                  and sample["poseMatchesPhysics"]]
                    if not candidates:
                        raise ValueError(f"missing rendered {phase}/{frame}: {folder}")
                    sample = candidates[0]
                    png = folder / "client" / sample["file"]
                    self.copy(png, target / "client" / png.name)
                    with Image.open(png) as original:
                        if original.size != (960, 540):
                            raise ValueError(f"unexpected original viewport: {png}")
                        thumbnail = original.convert("RGB")
                        thumbnail.thumbnail((480, 270), Image.Resampling.LANCZOS)
                        x, y = column * 480, 32 + row_index * (270 + 44)
                        heading = math.degrees(sample["heading"])
                        draw.text((x + 6, y + 4), f"{version} {phase}/{frame} heading={heading:.2f}deg", fill="white")
                        draw.text((x + 6, y + 21), f"source tick {sample['selectedPresentationTick']}; playback {sample['playbackTime']:.6f}s", fill="#c0c8d4")
                        sheet.paste(thumbnail, (x, y + 44))
                    selections.append({"comparison": name, "version": version, "phase": phase, "frame": frame,
                                       "source": self.relative_source(png),
                                       "retained": (target / "client" / png.name).as_posix(),
                                       "sourceFileName": png.name, "originalSha256": sha(png.read_bytes()),
                                       "sourcePresentationTick": sample["selectedPresentationTick"],
                                       "physicsFrame": sample["physicsFrame"], "playbackTime": sample["playbackTime"],
                                       "posePhysicsFrame": sample["pose"]["physicsFrame"],
                                       "headingRadians": sample["heading"], "phaseFrame": sample["frame"],
                                       "camera": sample["camera"], "viewport": sample["viewportSize"]})
            if cameras[0] != cameras[1]:
                raise ValueError(f"comparison camera or viewport differs: {name}")
            (self.output / "rendered").mkdir(parents=True, exist_ok=True)
            sheet.save(self.output / "rendered" / f"{name}-comparison.png")
        write_json(self.output / "rendered-selection.json", selections)
        write_json(self.output / "rendered-metrics.json", metrics)

    def logs(self, network):
        for name in ("final-v3", "drop-target-v3", "wrong-instance-v3"):
            folder = network / name
            if not (folder / "summary.json").is_file():
                raise ValueError(f"missing network evidence: {folder}")
            for source in sorted(folder.rglob("*")):
                if source.is_file():
                    rel = Path("network") / name / source.relative_to(folder)
                    self.copy(source, rel.with_name(rel.name + ".gz") if source.suffix == ".log" else rel,
                              source.suffix == ".log")
        for name in ("mutations", "legacy-anim-proof"):
            folder = self.source / name
            for source in sorted(folder.glob("*")):
                if source.is_file() and source.suffix in (".json", ".log"):
                    rel = Path(name) / source.name
                    self.copy(source, rel.with_name(rel.name + ".gz") if source.suffix == ".log" else rel,
                              source.suffix == ".log")

    def finish(self):
        self.originals.sort(key=lambda entry: entry["retained"])
        write_json(self.output / "originals.json", self.originals)
        write_json(self.output / "retention.json", sorted(self.retention, key=lambda entry: entry["retained"]))
        files = sorted(path for path in self.output.rglob("*")
                       if path.is_file() and path.name not in ("SHA256SUMS", "package.json") and path.suffix != ".import")
        total = sum(path.stat().st_size for path in files)
        write_json(self.output / "package.json", {
            "schema": "hooper-shot-heading-evidence/v1", "source": self.relative_source(self.source),
            "originals": len(self.originals), "matrixCases": 28, "selectedOriginalPngs": 24,
            "bytesBeforePackageManifest": total, "sizeWarning": total > 20 * 1024 * 1024,
            "limitation": "State, final-pose and rendered evidence only; no visual/feel acceptance or ball-coupling fix.",
            "compression": "Gzip level9 mtime0. Local JSON/logs are byte-preserving; capture traces are explicit row selections in retention.json, retaining every observed shot row and all final-pose bones."})
        files = sorted(path for path in self.output.rglob("*")
                       if path.is_file() and path.name != "SHA256SUMS" and path.suffix != ".import")
        (self.output / "SHA256SUMS").write_text("".join(
            f"{sha(path.read_bytes())}  {path.relative_to(self.output).as_posix()}\n" for path in files),
            encoding="utf-8", newline="\n")
        print(f"PACKAGED {len(files)} files; {sum(path.stat().st_size for path in files) / 1024 / 1024:.2f} MiB; {self.output}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=ROOT / ".godot/428")
    parser.add_argument("--network", type=Path, default=ROOT / ".godot/shot-net")
    parser.add_argument("--output", type=Path, default=ROOT / "docs/analysis/evidence/0428")
    parser.add_argument("--verify-only", action="store_true")
    args = parser.parse_args()
    source, output, network = args.source.resolve(), args.output.resolve(), args.network.resolve()
    if not source.is_relative_to(ROOT) or not network.is_relative_to(ROOT) or not output.is_relative_to(ROOT):
        parser.error("all paths must stay inside the repository")
    if args.verify_only:
        verify(output)
        return
    output.mkdir(parents=True, exist_ok=True)
    package = Package(source, output)
    package.local(source / "before-v2", "local/before")
    package.local(source / "final-v3", "local/after")
    package.matrix()
    package.rendered()
    package.logs(network)
    package.finish()
    verify(output)


if __name__ == "__main__":
    main()
