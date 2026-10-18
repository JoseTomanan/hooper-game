"""Render #415's real-input Held fixture; images are review evidence, never human acceptance."""

import argparse
import json
import os
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--godot", default=os.environ.get("GODOT", "godot"))
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--resolution", default="960x720")
    parser.add_argument("--compatibility", action="store_true")
    parser.add_argument("--headless", action="store_true", help="reject rendered evidence without a native window")
    args = parser.parse_args()
    if args.headless:
        parser.error("rendered capture requires native rendering; omit --headless")
    output = (ROOT / args.output).resolve()
    if not output.is_relative_to(ROOT / ".godot") or output.exists():
        parser.error("--output must be a fresh directory below the workspace .godot directory")
    output.parent.mkdir(parents=True, exist_ok=True)
    native_log = output.with_name(output.name + ".godot.log")
    stdout_log = output.with_name(output.name + ".stdout.log")
    if native_log.exists() or stdout_log.exists():
        parser.error("capture log paths must also be fresh")
    command = [args.godot, "--path", str(ROOT), "--fixed-fps", "60", "--max-fps", "60", "--disable-vsync",
               "--resolution", args.resolution, "--log-file", str(native_log)]
    if args.compatibility:
        command += ["--rendering-method", "gl_compatibility", "--rendering-driver", "opengl3"]
    command += ["res://tests/integration/HeldPossessionTest.tscn", "--", "--harness-scenario=held",
                "--capture-output=" + str(output), "--capture-commit=" + args.commit]
    options = {}
    # Hidden Windows startup avoids taking focus; argument sequences preserve paths with spaces.
    # https://docs.python.org/3/library/subprocess.html#subprocess.STARTUPINFO
    if os.name == "nt":
        startup = subprocess.STARTUPINFO()
        startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        startup.wShowWindow = subprocess.SW_HIDE
        options["startupinfo"] = startup
    with stdout_log.open("w", encoding="utf-8") as log:
        process = subprocess.Popen(command, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT, **options)
        try:
            result = process.wait(timeout=120)
        finally:
            if process.poll() is None:
                process.kill()
                process.wait(timeout=10)
    if result:
        raise SystemExit(f"Godot exited {result}; see {stdout_log}")
    if not (output / "manifest.json").is_file():
        raise SystemExit(f"capture manifest missing; see {stdout_log}")
    (output / "launch.json").write_text(json.dumps({"command": command, "commit": args.commit,
        "stdout": str(stdout_log), "native_log": str(native_log)}, indent=2), encoding="utf-8")
    print(f"[held-capture] CAPTURED {output}; human visual acceptance remains open")


if __name__ == "__main__":
    main()
