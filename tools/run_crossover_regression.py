"""Run #414's bounded capture regression, including non-vacuous negative controls."""

import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import uuid

from verify_crossover_continuity import verify

ROOT = Path(__file__).resolve().parents[1]


def capture(args, output, *, remote=False, mutation=False, origin=None, motion=None, count=None, presentation_delay=0):
    command = [sys.executable, str(ROOT / "tools/run_crossover_capture.py"),
               "--godot", args.godot, "--output", str(output), "--commit", args.commit,
               "--port", str(args.port), "--resolution", args.resolution]
    if args.rendered and not args.native:
        command.append("--compatibility")
    elif not args.rendered:
        command.append("--headless")
    if remote:
        command.append("--remote")
        if args.paced:
            command.append("--paced")
    if mutation:
        command += ["--disable-pose", "--origin", origin, "--motion", "stationary", "--count", "1"]
    else:
        if origin:
            command += ["--origin", origin]
        if motion:
            command += ["--motion", motion]
        if count:
            command += ["--count", str(count)]
    if presentation_delay:
        command += ["--presentation-delay-ticks", str(presentation_delay)]
    # Delegate process ownership/timeout/cleanup to the existing runner. Argument
    # sequences preserve paths with spaces without invoking a shell.
    # https://docs.python.org/3/library/subprocess.html#subprocess.run
    subprocess.run(command, cwd=ROOT, check=True)


def check_suite(output, expected):
    directories = sorted(path.parent for path in output.glob("*/manifest.json") if not path.parent.name.endswith("-server"))
    if len(directories) != expected:
        raise RuntimeError(f"expected {expected} captures below {output}, found {len(directories)}")
    results = [verify(directory) for directory in directories]
    if any(result["failures"] for result in results):
        raise RuntimeError(json.dumps(results, indent=2))
    return results


def check_mutation(output):
    directories = [path.parent for path in output.glob("*/manifest.json")]
    if len(directories) != 1:
        raise RuntimeError(f"expected one mutation capture below {output}")
    directory = directories[0]
    manifest = json.loads((directory / "manifest.json").read_text(encoding="utf-8-sig"))
    if (manifest["result"] != "captured" or manifest["completedCycles"] != 1 or
            manifest.get("mutation") != "disable-crossover-pose" or not manifest.get("mutationApplied")):
        raise RuntimeError(f"mutation did not complete with its modifier disabled: {directory}")
    result = verify(directory)
    # A failed process or an absent phase is not a successful negative control.
    # Require the external verifier's measured limb/root continuity assertion.
    continuity = re.compile(r"\b(?:Hips|LeftHand|RightHand|LeftFoot|RightFoot) step [0-9.]+ m >")
    fixture_errors = [failure for failure in result["failures"] if not continuity.search(failure)]
    if fixture_errors or not any(continuity.search(failure) for failure in result["failures"]):
        raise RuntimeError(f"mutation did not expose actual continuity failure: {json.dumps(result)}")
    result["expectedFailure"] = True
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--godot", default=os.environ.get("GODOT", "godot"))
    parser.add_argument("--output", type=Path)
    parser.add_argument("--commit")
    parser.add_argument("--rendered", action="store_true")
    parser.add_argument("--paced", action="store_true", help="pace only remote rendered captures with fixed simulation delta; no real-time timing claims")
    parser.add_argument("--native", action="store_true", help="use the project renderer for local rendered runs; CI defaults to Mesa compatibility")
    parser.add_argument("--resolution", default="480x270")
    parser.add_argument("--port", type=int, default=23464)
    args = parser.parse_args()
    if args.paced and not args.rendered:
        parser.error("--paced requires --rendered")
    if args.native and not args.rendered:
        parser.error("--native requires --rendered")
    if args.commit is None:
        args.commit = subprocess.run(["git", "rev-parse", "HEAD"], cwd=ROOT, check=True,
                                     capture_output=True, text=True).stdout.strip()
    args.output = (ROOT / (args.output or Path(f".godot/crossover-continuity/catalog-{uuid.uuid4().hex}"))).resolve()
    if not args.output.is_relative_to(ROOT / ".godot"):
        parser.error("output must be below the workspace .godot directory")
    args.output.mkdir(parents=True, exist_ok=False)
    summary = {"mode": "rendered" if args.rendered else "headless", "commit": args.commit,
               "remoteTiming": ("paced fixed-fps diagnostic; not real-time" if args.paced else "real-time") if args.rendered else "not run",
               "result": "FAIL", "captures": [], "negativeControls": []}
    try:
        capture(args, args.output / "local")
        summary["captures"] += check_suite(args.output / "local", 8)
        if args.rendered:
            capture(args, args.output / "remote", remote=True)
            summary["captures"] += check_suite(args.output / "remote", 8)
        else:
            for origin in ("Right", "Left"):
                output = args.output / ("mutation-" + origin.lower())
                capture(args, output, mutation=True, origin=origin)
                summary["negativeControls"].append(check_mutation(output))
        for origin in ("Right", "Left"):
            output = args.output / ("late-presentation-" + origin.lower())
            capture(args, output, remote=True, origin=origin, motion="stationary", count=1, presentation_delay=6)
            summary["captures"] += check_suite(output, 1)
        summary["result"] = "PASS"
    except Exception as error:
        summary["error"] = str(error)
        raise
    finally:
        (args.output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    print(f"[crossover-continuity] PASS mode={summary['mode']} output={args.output}")


if __name__ == "__main__":
    main()
