"""Capture #428 against a chosen checkout with a real separate ENet observer.

The selected checkout supplies gameplay, imports and assembly. The identical
JumpshotFormCapture diagnostic is copied there before building the baseline.
Images are actual post-draw viewport PNGs, never headless reconstructions.
"""
import argparse
import json
import os
from pathlib import Path
import subprocess
import time

ROOT = Path(__file__).resolve().parents[1]


def run_case(args, angle, entry, hand, index):
    name = f"{angle}-{entry}-{hand}"
    output = args.output / name
    output.mkdir()
    port = args.port
    processes = []
    logs = []
    try:
        for role in ("server", "client"):
            folder = output / role
            log_path = output / f"{role}.stdout.log"
            cmd = [args.godot, "--path", str(args.project), "--max-fps", "60", "--disable-vsync",
                   "--fixed-fps", "60", "--resolution", "960x540", "--log-file", str(output / f"{role}.godot.log")]
            if role == "server" or not args.render:
                cmd.append("--headless")
            cmd += ["res://tests/integration/JumpshotFormCapture.tscn", "--",
                    f"--capture-role={role}", f"--capture-output={folder}",
                    f"--capture-origin={hand}", "--capture-variant=normal", "--capture-view=gameplay",
                    f"--capture-commit={args.commit}", f"--capture-port={port}",
                    f"--capture-heading-angle={angle}", f"--capture-heading-entry={entry}",
                    f"--capture-images={'true' if args.render and role == 'client' else 'false'}"]
            if args.paced:
                cmd += ["--capture-paced=true"]
            options = {}
            if os.name == "nt":
                info = subprocess.STARTUPINFO()
                info.dwFlags |= subprocess.STARTF_USESHOWWINDOW
                info.wShowWindow = subprocess.SW_HIDE
                options["startupinfo"] = info
            log = log_path.open("w", encoding="utf-8")
            logs.append(log)
            process = subprocess.Popen(cmd, cwd=args.project, stdout=log, stderr=subprocess.STDOUT, **options)
            processes.append(process)
            if role == "server":
                deadline = time.monotonic() + 25
                while "Spawned player for peer 1" not in log_path.read_text(encoding="utf-8", errors="replace"):
                    if process.poll() is not None or time.monotonic() > deadline:
                        raise RuntimeError(f"server not ready: {log_path}")
                    time.sleep(.05)
        for process in reversed(processes):
            if process.wait(timeout=90):
                raise RuntimeError(f"capture failed: {output}")
        for role in ("server", "client"):
            manifest = json.loads((output / role / "manifest.json").read_text())
            if manifest["result"] != "captured":
                raise RuntimeError(f"invalid manifest: {output / role}")
        print(f"[shot-heading-capture] CAPTURED {name}", flush=True)
    finally:
        for process in processes:
            if process.poll() is None:
                process.kill()
                process.wait(timeout=10)
        for log in logs:
            log.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--godot", default=os.environ.get("GODOT", "godot"))
    parser.add_argument("--project", type=Path, default=ROOT)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--port", type=int, default=23528)
    parser.add_argument("--render", action="store_true")
    parser.add_argument("--paced", action="store_true")
    parser.add_argument("--angle", type=int)
    parser.add_argument("--entry", choices=("stationary", "moving"))
    parser.add_argument("--hand", choices=("Left", "Right"))
    args = parser.parse_args()
    args.project = args.project.resolve()
    args.output = args.output.resolve()
    if not args.output.is_relative_to(ROOT / ".godot" / "428"):
        parser.error("output must be beneath .godot/428")
    args.output.mkdir(parents=True, exist_ok=False)
    cases = [(angle, entry, hand) for angle in ([args.angle] if args.angle is not None else (0, 45, 89, 90, 91, 135, 180))
             for entry in ([args.entry] if args.entry else ("stationary", "moving"))
             for hand in ([args.hand] if args.hand else ("Left", "Right"))]
    for index, case in enumerate(cases):
        run_case(args, *case, index)


if __name__ == "__main__":
    main()
