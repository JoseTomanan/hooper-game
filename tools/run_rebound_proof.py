"""Run isolated production-scene local, observer and owner #427 proof captures."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import uuid

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--godot", default=os.environ.get("GODOT", "godot"))
    parser.add_argument("--port", type=int, default=23469)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    output = (args.output or ROOT / ".godot" / "427" / ("catalog-" + uuid.uuid4().hex)).resolve()
    capture_root = (ROOT / ".godot" / "427").resolve()
    if output == capture_root or not output.is_relative_to(capture_root):
        parser.error("output must be a fresh directory below .godot/427")
    if not 1 <= args.port <= 65515:
        parser.error("--port must be between 1 and 65515")
    # Sequence arguments and check=True propagate failed subprocesses without a
    # shell; timeout bounds the wrapper even if its child launcher malfunctions.
    # https://docs.python.org/3.14/library/subprocess.html#subprocess.run
    commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    output.mkdir(parents=True, exist_ok=False)
    launcher = [sys.executable, str(ROOT / "tools/run_rebound_capture.py"), "--godot", args.godot,
                "--headless", "--commit", commit]
    analyzer = [sys.executable, str(ROOT / "tools/analyze_rebound_capture.py")]
    normal_cases = (("local", []), ("observer", ["--remote"]),
                    ("owner", ["--remote", "--actor", "client"]),
                    ("long-loose", ["--long-loose"]),
                    ("falling-contested", ["--height", "2", "--contested", "--hand", "Left"]),
                    ("moving", ["--moving"]),
                    ("moving-observer", ["--moving", "--remote"]),
                    ("moving-owner", ["--moving", "--remote", "--actor", "client"]),
                    ("reset-neutral", ["--remote", "--reset-neutral", "--long-loose"]))
    for offset, (name, extra) in enumerate(normal_cases):
        capture = output / name
        subprocess.run([*launcher, "--port", str(args.port + offset), "--output", str(capture), *extra],
                       cwd=ROOT, check=True, timeout=220)
        subprocess.run([*analyzer, str(capture), "--verify", "--output", str(output / (name + "-proof.json"))],
                       cwd=ROOT, check=True, timeout=30)
        if name == "owner":
            owner = json.loads((capture / "client/manifest.json").read_text(encoding="utf-8"))
            if owner.get("observedRecoveries") != 0 or owner.get("authoritativeRecoveriesReceived") != 2:
                raise RuntimeError("owner fixture did not reproduce two recoveries without a live Loose edge")
    # These capture-side controls require actual accepted production actions;
    # they intentionally exclude the normal uninterrupted 30-tick catch proof.
    for offset, (name, extra) in enumerate((("immediate-local", ["--action", "immediate"]),
                                          ("feint-local", ["--action", "feint"]),
                                          ("immediate-owner", ["--remote", "--actor", "client", "--action", "immediate"]),
                                          ("feint-owner", ["--remote", "--actor", "client", "--action", "feint"])), 9):
        capture = output / name
        subprocess.run([*launcher, "--port", str(args.port + offset), "--output", str(capture), *extra],
                       cwd=ROOT, check=True, timeout=220)
        for path in capture.rglob("manifest.json"):
            manifest = json.loads(path.read_text(encoding="utf-8"))
            if manifest.get("result") != "captured" or manifest.get("controlFailure"):
                raise RuntimeError("interruption control failed: " + str(path))
            owning = manifest["role"] == "local" or manifest["role"] == "client" and manifest["actor"] == "client"
            if owning and (manifest.get("acceptedActions") != 2 or
                           manifest["action"] == "feint" and manifest.get("acceptedFeints") != 2):
                raise RuntimeError("interruption control lacks accepted actions: " + str(path))
    legacy = output / "legacy-pose"
    subprocess.run([*launcher, "--port", str(args.port + 13), "--output", str(legacy), "--legacy-pose"],
                   cwd=ROOT, check=True, timeout=220)
    legacy_report = output / "legacy-pose-control.json"
    legacy_result = subprocess.run([*analyzer, str(legacy), "--verify", "--output", str(legacy_report)],
                                   cwd=ROOT, timeout=30)
    legacy_data = json.loads(legacy_report.read_text(encoding="utf-8"))
    if legacy_result.returncode != 1 or not any("palm surface contact exceeds" in failure
            for case in legacy_data["cases"] for failure in case["failures"]):
        raise RuntimeError("legacy goalkeeper pose escaped the contact detector")
    for mutation, detector in (("disconnected", "palm surface contact exceeds"),
                               ("delayed", "onset exceeds two source ticks")):
        report_path = output / (mutation + "-control.json")
        result = subprocess.run([*analyzer, str(output / "local"), "--verify", "--mutate-" + mutation,
                                 "--output", str(report_path)], cwd=ROOT, timeout=30)
        report = json.loads(report_path.read_text(encoding="utf-8"))
        failures = [f for case in report["cases"] for f in case["failures"]]
        if result.returncode != 1 or not any(detector in failure for failure in failures):
            raise RuntimeError(mutation + " control escaped its intended detector")
    print(f"[harness] PASS rebound-presentation-proof reports={output}")


if __name__ == "__main__":
    main()
