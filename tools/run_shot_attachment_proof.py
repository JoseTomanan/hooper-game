"""Capture the complete #442 local/owner matrix and independently analyze each role."""
from __future__ import annotations

import argparse
import itertools
import json
import os
from pathlib import Path
import subprocess
import sys
import uuid

from analyze_shot_attachment import load_calibration, safe_analyze

ROOT = Path(__file__).resolve().parents[1]


def expected_cases(topology, offsets=(0, 7)):
    stems = {f"{hand}-{entry}-{variant}-o{offset}-1"
             for hand, entry, variant, offset in itertools.product(
                 ("right", "left"), ("held", "dribble", "moving"), ("normal", "early", "late"), offsets)}
    return {(stem + suffix, role) for stem in stems for suffix, role in
            ((("", "local"),) if topology == "local" else (("", "observer"), ("-owner", "owner")))}


def capture(args, output, topology):
    command = [sys.executable, str(ROOT / "tools/run_jumpshot_transition_capture.py"),
               "--godot", args.godot, "--output", str(output), "--topology", topology,
               "--headless", "--jobs", "1" if topology == "owner" else "2", "--port", str(args.port)]
    command += ["--offsets", *map(str, args.offsets)]
    if topology == "owner":
        command += ["--paced", "--pacing-ms", "80"]
    # Official Python subprocess.run accepts argument sequences and check=True;
    # paths containing spaces remain single arguments, and capture failures abort.
    # https://docs.python.org/3/library/subprocess.html#subprocess.run
    subprocess.run(command, cwd=ROOT, check=True)


def reports(directory, topology, calibration, offsets=(0, 7)):
    cases = []
    for manifest in sorted(directory.glob("*/manifest.json")):
        if manifest.parent.name.endswith("-server"):
            continue
        cases.append(safe_analyze(manifest.parent, calibration))
    observed = [(case["case"], case.get("role")) for case in cases]
    expected = expected_cases(topology, offsets)
    if len(observed) != len(expected) or set(observed) != expected:
        raise ValueError(f"{topology}: incomplete/duplicate matrix role evidence; "
                         f"expected {len(expected)}, observed {len(observed)}")
    return cases


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--godot", default=os.environ.get("GODOT", "godot"))
    parser.add_argument("--output", type=Path)
    parser.add_argument("--topology", choices=("all", "local", "owner"), default="all")
    parser.add_argument("--port", type=int, default=24750)
    parser.add_argument("--offsets", nargs="+", type=int, choices=(0, 7), default=[0, 7])
    args = parser.parse_args()
    if len(set(args.offsets)) != len(args.offsets):
        parser.error("offsets must be distinct")
    output = (args.output or ROOT / ".godot/440" / ("442-catalog-" + uuid.uuid4().hex)).resolve()
    if not output.is_relative_to(ROOT / ".godot/440"):
        parser.error("output must be below .godot/440")
    calibration = load_calibration()
    output.mkdir(parents=True, exist_ok=False)
    results = []
    for topology in (("local", "owner") if args.topology == "all" else (args.topology,)):
        directory = output / topology
        capture(args, directory, topology)
        cases = reports(directory, topology, calibration, args.offsets)
        passed = all(case["result"] == "passed" for case in cases)
        (output / (topology + "-proof.json")).write_text(json.dumps({
            "schema": "hooper-shot-attachment/v1", "result": "passed" if passed else "failed",
            "topology": topology, "cases": cases}, indent=2) + "\n", encoding="utf-8")
        results.extend(cases)
    passed = bool(results) and all(case["result"] == "passed" for case in results)
    summary = {"result": "passed" if passed else "failed", "cases": len(results),
               "failures": [{"case": c["case"], "role": c.get("role"), "failures": c["failures"]}
                            for c in results if c["failures"]],
               "verdict": "anatomical contact, release mechanics and existing transition timing; feel remains #173"}
    (output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    print(f"[harness] {'PASS' if passed else 'FAIL'} shot-attachment report={output / 'summary.json'}")
    raise SystemExit(0 if passed else 1)


if __name__ == "__main__":
    main()
