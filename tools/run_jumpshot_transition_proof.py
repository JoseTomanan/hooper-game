"""Run #440's local/owning-client/observer matrix and discriminating asset control.

Headless output proves sampled state and final poses, never rendered appearance.
Frozen before reports must match role, input, view and clock metadata.
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import uuid

from analyze_jumpshot_transitions import analyze, compare, load_json

ROOT = Path(__file__).resolve().parents[1]
EVIDENCE = ROOT / "docs/analysis/evidence/0440"


def run_capture(args, output, topology, controls=False):
    command = [sys.executable, str(ROOT / "tools/run_jumpshot_transition_capture.py"),
               "--godot", args.godot, "--output", str(output), "--topology", topology,
               "--headless", "--jobs", "2", "--port", str(args.port)]
    if topology == "owner":
        # Match the immutable source-clock baseline. Producer pacing makes every
        # source tick observable; this is state/pose evidence, never a latency
        # or real-time performance measurement.
        command += ["--paced", "--pacing-ms", "80"]
    if controls: command += ["--before440", "--variants", "normal", "--offsets", "0"]
    subprocess.run(command, cwd=ROOT, check=True)


def reports(directory):
    cases = []
    for path in sorted(directory.glob("*/manifest.json")):
        if path.parent.name.endswith("-server"): continue
        cases.append(analyze(path.parent))
    return cases


def baseline_cases(document, topology):
    """An incomplete before recording cannot authorize an after verdict."""
    expected = 36 if topology == "local" else 72
    cases = document.get("cases", [])
    known_defect = "changed final pose on duplicate source tick"
    complete_before = document.get("measurementComplete") is True
    if (document.get("baselineMeasurement") is not True or len(cases) != expected or
            (not complete_before and (document.get("result") != "passed" or
                                     any(case.get("result") != "passed" for case in cases)))):
        raise ValueError(f"{topology}: baseline must contain {expected} complete passing before measurements")
    if complete_before:
        # The before recording intentionally contains the reproduced defect.
        # Permit ONLY that named presentation failure; gaps, resets, missing
        # premises or any other failed evidence still make it incomplete.
        for case in cases:
            failures = case.get("failures", [])
            edges = case.get("changedDuplicateEdges", [])
            if (any(failure != known_defect for failure in failures) or len(failures) != len(edges)
                    or case.get("result") != ("failed" if failures else "passed")):
                raise ValueError(f"{topology}: incomplete baseline evidence beyond the named playback defect")
            if failures and (case.get("result") != "failed" or
                    any(edge.get("maxWorldPositionStepM", 0) <= 0 for edge in edges)):
                raise ValueError(f"{topology}: expected playback defect lacks its raw changed-pose evidence")
        if document.get("expectedPresentationDefect") != known_defect or not any(
                edge.get("maxWorldPositionStepM", 0) >= .6 for case in cases for edge in case.get("changedDuplicateEdges", [])):
            raise ValueError(f"{topology}: named pre-fix playback defect was not reproduced")
    indexed = {case["case"]: case for case in cases}
    if len(indexed) != expected:
        raise ValueError(f"{topology}: duplicate baseline case names")
    return indexed


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--godot", default=os.environ.get("GODOT", "godot"))
    parser.add_argument("--output", type=Path)
    parser.add_argument("--topology", choices=("all", "local", "owner"), default="all")
    parser.add_argument("--port", type=int, default=24550)
    parser.add_argument("--baseline-local", type=Path, default=EVIDENCE / "baseline-local.json.gz")
    parser.add_argument("--baseline-owner", type=Path, default=EVIDENCE / "baseline-owner.json.gz")
    args = parser.parse_args()
    output = args.output or ROOT / ".godot/440" / ("catalog-" + uuid.uuid4().hex)
    output = output.resolve()
    if not output.is_relative_to(ROOT / ".godot/440"):
        parser.error("output must be below .godot/440")
    topologies = ("local", "owner") if args.topology == "all" else (args.topology,)
    baselines = {}
    for topology in topologies:
        path = getattr(args, "baseline_" + topology)
        if not path.is_file(): parser.error("immutable matched baseline report missing: " + str(path))
        try:
            baselines[topology] = baseline_cases(load_json(path), topology)
        except (KeyError, ValueError) as error:
            parser.error(str(error))
    output.mkdir(parents=True, exist_ok=False)
    results, controls = [], []
    for topology in topologies:
        capture = output / topology
        run_capture(args, capture, topology)
        cases = reports(capture)
        expected = 36 if topology == "local" else 72  # separate owner and observer final poses
        if len(cases) != expected:
            raise RuntimeError(f"{topology}: expected {expected} case/role reports, observed {len(cases)}")
        for case in cases:
            baseline = baselines[topology].get(case["case"])
            if baseline is None:
                case["failures"].append("immutable baseline case missing")
                case["result"] = "failed"
            else:
                compare(case, baseline)
        report = {"result": "passed" if all(c["result"] == "passed" for c in cases) else "failed", "cases": cases}
        (output / (topology + "-proof.json")).write_text(json.dumps(report, indent=2), encoding="utf-8")
        results.extend(cases)
    control_capture = output / "asset-only-control"
    run_capture(args, control_capture, "local", controls=True)
    controls = reports(control_capture)
    intended = "normal shot carries horizontal hip travel"
    control_passed = len(controls) == 6 and all(case["failures"] == [intended] for case in controls)
    control_report = {"meaning": "pre-440 normal assets with current playback/transitions; not historical behavior restoration",
                      "detectedIntendedFailureOnly": control_passed, "cases": controls}
    (output / "asset-only-control.json").write_text(json.dumps(control_report, indent=2), encoding="utf-8")
    passed = bool(results) and all(case["result"] == "passed" for case in results) and control_passed
    summary = {"result": "passed" if passed else "failed", "cases": len(results),
               "failures": [{"case": c["case"], "role": c["role"], "failures": c["failures"]}
                            for c in results if c["failures"]], "assetControl": control_passed,
               "verdict": "objective final-pose proof; human appearance/feel remains #173"}
    (output / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(f"[harness] {'PASS' if passed else 'FAIL'} jumpshot-transitions report={output / 'summary.json'}")
    raise SystemExit(0 if passed else 1)


if __name__ == "__main__":
    main()
