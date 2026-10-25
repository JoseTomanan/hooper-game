"""Comparable-clock #429 form/continuity proof, including production substitutions."""
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
    parser.add_argument("--remote", action="store_true")
    args = parser.parse_args()
    output = ROOT / ".godot/418" / ("429-proof-" + uuid.uuid4().hex)
    output.mkdir(parents=True)
    commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    capture = [sys.executable, str(ROOT / "tools/run_jumpshot_capture.py"), "--godot", args.godot,
               "--headless", "--commit", commit, "--port", "23478"]
    if args.remote:
        capture.append("--remote")
    analyzer = [sys.executable, str(ROOT / "tools/analyze_fadeaway_capture.py")]
    for name, substitute in (("before", ["--fadeaway-substitute", "before429"]), ("after", [])):
        subprocess.run([*capture, "--output", str(output / name), *substitute], cwd=ROOT, check=True)
    baseline = output / "baseline.json"
    subprocess.run([*analyzer, str(output / "before"), "--measure-only", "--output", str(baseline)], cwd=ROOT, check=True)
    subprocess.run([*analyzer, str(output / "after"), "--baseline", str(baseline),
                    "--output", str(output / "proof.json")], cwd=ROOT, check=True)
    # The ordinary asset is substituted at FadeawayActive's actual production
    # selection seam. A missing phase or invalid release premise is not a pass.
    subprocess.run([*capture, "--output", str(output / "upright"), "--variant", "fadeaway",
                    "--origin", "Right", "--fadeaway-substitute", "normal"], cwd=ROOT, check=True)
    negative = output / "upright.json"
    status = subprocess.run([*analyzer, str(output / "upright"), "--output", str(negative)], cwd=ROOT).returncode
    cases = json.loads(negative.read_text())["cases"]
    failures = [f for c in cases for f in c["failures"]]
    form_failures = ("both legs must lead upper torso", "both legs must advance from hips")
    if status != 1 or not failures or any(not f.startswith(form_failures) for f in failures):
        raise RuntimeError("ordinary substitute did not fail specifically on two-leg form: " + repr(failures))
    # Reuse the established boundary counterfactual against the observed
    # positive motion envelope. The old clip's much larger discontinuity can
    # exceed this mutation (2 * NEW motion + 1m), so that old envelope would
    # make this particular negative control inconclusive.
    boundary = output / "boundary-negative.json"
    status = subprocess.run([sys.executable, str(ROOT / "tools/analyze_jumpshot_capture.py"),
        str(output / "after/right-fadeaway-1"), "--baseline", str(output / "proof.json"), "--mutate-boundary",
        "--output", str(boundary)], cwd=ROOT).returncode
    failures = [f for c in json.loads(boundary.read_text())["cases"] for f in c["failures"]]
    if status != 1 or not any(f.startswith("boundary displacement exceeds baseline:") for f in failures):
        raise RuntimeError("mismatched boundary escaped its detector")
    print(f"[harness] PASS {'net-' if args.remote else ''}fadeaway-form report={output / 'proof.json'}")


if __name__ == "__main__":
    main()
