"""Run #416's real-scene matrix and actual transition-break negative controls."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import uuid

from verify_pivot_continuity import verify
from verify_pivot_context import verify_context

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--godot", default=os.environ.get("GODOT", "godot"))
    parser.add_argument("--output", type=Path)
    parser.add_argument("--commit")
    parser.add_argument("--rendered", action="store_true")
    parser.add_argument("--compatibility", action="store_true")
    parser.add_argument("--paced", action="store_true", help="diagnostic remote source pacing; excludes real-time timing claims")
    parser.add_argument("--resolution", default="960x540")
    parser.add_argument("--port", type=int, default=23468)
    parser.add_argument("--local-render-delay-ms", type=int, default=0)
    args = parser.parse_args()
    revision = args.commit or subprocess.run(["git", "rev-parse", "HEAD"], cwd=ROOT, check=True, capture_output=True, text=True).stdout.strip()
    output = (ROOT / (args.output or Path(f".godot/pivot-continuity/catalog-{uuid.uuid4().hex}"))).resolve()
    if not output.is_relative_to(ROOT / ".godot"):
        parser.error("output must stay below workspace .godot")
    output.mkdir(parents=True, exist_ok=False)
    summary = {"result": "FAIL", "commit": revision, "rendered": args.rendered, "captures": [], "negativeControls": []}

    def context_capture(name, case, *flags):
        destination = output / "context" / name
        subprocess.run([sys.executable, str(ROOT / "tools/run_pivot_capture.py"), "--godot", args.godot,
                        "--output", str(destination), "--commit", revision, "--case", case,
                        "--gesture", "held", "--port", str(args.port), "--headless", *flags], cwd=ROOT, check=True)
        return destination / "local"

    def capture(case, gesture, remote, mutation=False):
        destination = output / ("negative" if mutation else "remote" if remote else "local") / f"{case}-{gesture}"
        command = [sys.executable, str(ROOT / "tools/run_pivot_capture.py"), "--godot", args.godot,
                   "--output", str(destination), "--commit", revision, "--case", case, "--gesture", gesture,
                   "--port", str(args.port), "--resolution", args.resolution]
        command += ["--images"] if args.rendered and not mutation else ["--headless"]
        if args.rendered and not remote and not mutation and args.local_render_delay_ms:
            command += ["--local-render-delay-ms", str(args.local_render_delay_ms)]
        if remote:
            command.append("--remote")
            if args.paced:
                command.append("--paced")
        if args.compatibility and args.rendered and not mutation:
            command.append("--compatibility")
        if mutation:
            command.append("--disable-pivot-pose")
        subprocess.run(command, cwd=ROOT, check=True)
        results = []
        for role in (("server", "client") if remote else ("local",)):
            directory = destination / role
            report = verify(directory)
            (directory / "verification.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
            manifest = json.loads((directory / "manifest.json").read_text(encoding="utf-8-sig"))
            if mutation:
                if manifest["result"] != "captured" or manifest["completedCycles"] != 3 or not manifest["mutationApplied"]:
                    raise RuntimeError(f"negative control never exercised the mutation: {directory}")
                if not any(re.search(r"step [0-9.]+ m >", failure) for failure in report["failures"]):
                    raise RuntimeError(f"negative control did not detect an actual pose jump: {directory}")
                report["expectedFailure"] = True
            elif report["failures"]:
                raise RuntimeError(f"pose regression failed: {directory}; see verification.json")
            results.append(report)
        return results

    try:
        for remote in (False, True):
            for case in ("idle", "run", "left", "right"):
                for gesture in ("flick", "held"):
                    summary["captures"] += capture(case, gesture, remote)
            summary["captures"] += capture("run", "forward", remote)
        for case, gesture in (("run", "held"), ("left", "flick")):
            summary["negativeControls"] += capture(case, gesture, False, True)
        # These comparisons must use identical physics input cadence. Native local
        # rendering uses fixed-fps; capture a fresh normal headless pair as well.
        normal = context_capture("normal-owner", "run")
        reset = context_capture("reset-owner", "run", "--reset-owner-presentation")
        protection = context_capture("protection-fixed", "right", "--protection")
        control = context_capture("protection-control", "right", "--protection", "--disable-pivot-pose")
        summary["context"] = verify_context(normal, reset, protection, control)
        if summary["context"]["failures"]:
            raise RuntimeError("pivot context regressions failed; see summary.json")
        summary["result"] = "PASS"
    finally:
        (output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    print(f"[pivot-regression] PASS captures={len(summary['captures'])} negativeControls=2 output={output}")


if __name__ == "__main__":
    main()
