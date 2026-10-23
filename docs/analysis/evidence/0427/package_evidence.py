"""Reproduce #427's compact package from the unchanged workspace captures.

Run with Python from any directory. No Godot launch or source trace edits occur.
The nine normal reports are regenerated with the current analyzer; negative
control reports are retained verbatim. Trace gzip headers use mtime=0 and no name.
"""
import gzip
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys

DEST = Path(__file__).resolve().parent
ROOT = DEST.parents[3]
SOURCE = ROOT / ".godot/427"
NORMAL = ("local", "observer", "owner", "long-loose", "falling-contested", "moving",
          "moving-observer", "moving-owner", "reset-neutral")
ACTIONS = ("immediate-local", "feint-local", "immediate-owner", "feint-owner")
ANALYZER = ROOT / "tools/analyze_rebound_capture.py"


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def relative(path, base=ROOT):
    return path.relative_to(base).as_posix()


def main():
    entries, captures, issues = [], [], []

    def copy(source, transform="copy", metadata=None):
        target = DEST / source.relative_to(SOURCE)
        if transform == "gzip-mtime-zero":
            target = target.with_suffix(target.suffix + ".gz")
        if not source.is_file():
            issues.append("missing input: " + relative(source))
            return
        target.parent.mkdir(parents=True, exist_ok=True)
        if transform == "gzip-mtime-zero":
            # Empty filename prevents a machine/path-dependent original-name header.
            # https://docs.python.org/3/library/gzip.html#gzip.GzipFile
            with source.open("rb") as source_file, target.open("wb") as output:
                with gzip.GzipFile(filename="", fileobj=output, mode="wb", mtime=0) as compressed:
                    shutil.copyfileobj(source_file, compressed)
        else:
            shutil.copyfile(source, target)
        entry = dict(source=relative(source), packaged=relative(target, DEST),
                     transformation=transform, sourceSha256=sha(source), packagedSha256=sha(target),
                     packagedBytes=target.stat().st_size)
        if metadata:
            entry.update(metadata)
        entries.append(entry)

    source_dirs = [SOURCE / "proof-final" / case for case in (*NORMAL, *ACTIONS, "legacy-pose")]
    source_dirs += [SOURCE / "probe-owner-completion-fix", SOURCE / "baseline-falling/local",
                    SOURCE / "native-before/local", SOURCE / "native-after/local", SOURCE / "native-observer/client"]
    for source_dir in source_dirs:
        manifests = [source_dir / "manifest.json"] if (source_dir / "manifest.json").is_file() else sorted(source_dir.rglob("manifest.json"))
        if not manifests:
            issues.append("no manifest below: " + relative(source_dir))
        for manifest_path in manifests:
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            capture = dict(source=relative(manifest_path.parent), originalCommit=manifest.get("commit"),
                           result=manifest.get("result"), failure=manifest.get("failure", ""),
                           role=manifest.get("role"), actor=manifest.get("actor"), images=manifest.get("images"),
                           animationResourceSha256=manifest.get("animationResourceSha256"))
            captures.append(capture)
            if manifest.get("result") != "captured" or manifest.get("failure"):
                issues.append("invalid capture: " + relative(manifest_path))
            copy(manifest_path)
            copy(manifest_path.parent / "trace.jsonl", "gzip-mtime-zero")

    reports = []
    for case in NORMAL:
        source_report = SOURCE / "proof-final" / (case + "-proof.json")
        target = DEST / source_report.relative_to(SOURCE)
        target.parent.mkdir(parents=True, exist_ok=True)
        command = [sys.executable, relative(ANALYZER), relative(SOURCE / "proof-final" / case),
                   "--output", relative(target)]
        result = subprocess.run(command, cwd=ROOT, check=False)
        report = json.loads(target.read_text(encoding="utf-8"))
        reports.append(dict(case=case, result=report["result"], exitCode=result.returncode))
        if result.returncode:
            issues.append("normal report failed: " + case)
        entries.append(dict(source=relative(source_report), packaged=relative(target, DEST),
                            transformation="reanalyzed unchanged capture with current analyzer; full exit window through end+7",
                            sourceSha256=sha(source_report) if source_report.is_file() else None,
                            packagedSha256=sha(target), packagedBytes=target.stat().st_size,
                            command=command[1:]))
    for name in ("legacy-pose-control.json", "disconnected-control.json", "delayed-control.json"):
        path = SOURCE / "proof-final" / name
        copy(path, metadata=dict(expectedResult="failed; intentional negative control"))
        if path.is_file() and json.loads(path.read_text(encoding="utf-8")).get("result") != "failed":
            issues.append("negative control did not fail: " + name)

    for case in ("native-before", "native-after"):
        for frame in (63, 70, 90):
            copy(SOURCE / case / "local" / f"frame-{frame:05d}.png",
                 metadata=dict(imageMeaning="original rendered PNG; no image manipulation"))

    observer_dir = SOURCE / "native-observer/client"
    observer_rows = [json.loads(line) for line in (observer_dir / "trace.jsonl").read_text(encoding="utf-8").splitlines() if line.strip()]
    catches = [row for row in observer_rows if row.get("pose", {}).get("animation") == "ReboundGrab"]
    if catches:
        first_tick = catches[0]["posePresentationTick"]
        for age in (0, 7, 27):
            choices = [row for row in catches if row.get("posePresentationTick") == first_tick + age and row.get("file")]
            if not choices:
                issues.append(f"native observer original frame unavailable at first catch age {age}")
                continue
            row = choices[0]
            copy(observer_dir / row["file"], metadata=dict(imageMeaning="original rendered PNG; no image manipulation",
                 firstCatchAge=age, presentationTick=row["posePresentationTick"], traceSample=row["sample"]))
    else:
        issues.append("native observer has no first catch")

    inventory = dict(schema="hooper-rebound-evidence-package/v1",
                     packagingHead=subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
                     analyzer=dict(path=relative(ANALYZER), sha256=sha(ANALYZER), exitWindow="first catch tick -1 through last catch tick +7 inclusive"),
                     traceEncoding="complete original UTF-8 JSONL bytes in gzip; mtime=0; empty gzip filename",
                     images="nine selected original PNGs only; no image processing",
                     captures=captures, normalReportResults=reports, issues=issues,
                     files=sorted(entries, key=lambda entry: entry["packaged"]))
    (DEST / "inventory.json").write_text(json.dumps(inventory, indent=2) + "\n", encoding="utf-8")
    packaged_files = sorted(path for path in DEST.rglob("*") if path.is_file() and path.name != "SHA256SUMS")
    (DEST / "SHA256SUMS").write_text("".join(f"{sha(path)}  {relative(path, DEST)}\n" for path in packaged_files), encoding="utf-8")
    all_files = [*packaged_files, DEST / "SHA256SUMS"]
    print(json.dumps(dict(files=len(all_files), bytes=sum(path.stat().st_size for path in all_files),
                          captures=len(captures), issues=issues), indent=2))
    return 1 if issues else 0


if __name__ == "__main__":
    raise SystemExit(main())
