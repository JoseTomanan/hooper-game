"""Capture #427 rebound presentation; headless output is diagnostic state only."""
import argparse
import os
from pathlib import Path
import subprocess
import time

ROOT = Path(__file__).resolve().parents[1]
READY = "[rebound-capture] READY"


def start(args, output, role):
    cmd = [args.godot, "--path", str(ROOT), "--max-fps", "60", "--disable-vsync",
           "--resolution", args.resolution, "--log-file", str(output) + ".godot.log"]
    if not args.remote or args.paced:
        cmd += ["--fixed-fps", "60"]
    if args.headless or role == "server":
        cmd += ["--headless"]
    elif args.compatibility:
        cmd += ["--rendering-method", "gl_compatibility", "--rendering-driver", "opengl3"]
    cmd += ["res://tests/integration/ReboundPresentationCapture.tscn", "--",
            "--capture-output=" + str(output), "--capture-role=" + role,
            "--capture-actor=" + args.actor, "--capture-hand=" + args.hand,
            "--capture-height=" + str(args.height), "--capture-view=" + args.view,
            "--capture-action=" + args.action,
            "--capture-port=" + str(args.port)]
    if args.commit:
        cmd += ["--capture-commit=" + args.commit]
    images = not args.headless and role != "server"
    cmd += ["--capture-images=" + str(images).lower()]
    if args.moving:
        cmd += ["--capture-moving=true"]
    if args.contested:
        cmd += ["--capture-contested=true"]
    if args.paced:
        cmd += ["--capture-paced=true"]
    if args.legacy_pose:
        cmd += ["--capture-legacy-pose=true"]
    if args.long_loose:
        cmd += ["--capture-long-loose=true"]
    if args.reset_neutral:
        cmd += ["--capture-reset-neutral=true"]
    options = {}
    if os.name == "nt":
        # Hidden helpers still render their viewport when capture images are enabled.
        # https://docs.python.org/3.14/library/subprocess.html#subprocess.STARTUPINFO
        info = subprocess.STARTUPINFO()
        info.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        info.wShowWindow = subprocess.SW_HIDE
        options["startupinfo"] = info
    log = open(str(output) + ".stdout.log", "w", encoding="utf-8")
    # Argument sequences preserve paths with spaces without shell interpretation.
    # https://docs.python.org/3.14/library/subprocess.html#popen-constructor
    try:
        process = subprocess.Popen(cmd, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT, **options)
    except BaseException:
        log.close()
        raise
    return process, log


def stop(process, log):
    try:
        if process.poll() is None:
            process.kill()
            process.wait(timeout=10)
    finally:
        log.close()


def finish(process, log):
    # Popen.wait's timeout does not kill its child; cleanup belongs to our finally.
    # https://docs.python.org/3.14/library/subprocess.html#subprocess.Popen.wait
    code = process.wait(timeout=90)
    if code:
        raise RuntimeError(f"Godot capture exited {code}: {log.name}")


def wait_ready(process, log):
    deadline = time.monotonic() + 20
    while True:
        if process.poll() is not None:
            raise RuntimeError("server exited before client startup: " + log.name)
        if READY in Path(log.name).read_text(encoding="utf-8", errors="replace"):
            return
        if time.monotonic() >= deadline:
            raise RuntimeError("server readiness timed out: " + log.name)
        time.sleep(.1)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--godot", default=os.environ.get("GODOT", "godot"))
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--commit")
    parser.add_argument("--headless", action="store_true")
    parser.add_argument("--remote", action="store_true")
    parser.add_argument("--actor", choices=("host", "client"), default="host")
    parser.add_argument("--hand", choices=("Right", "Left"), default="Right")
    parser.add_argument("--height", type=float, choices=(.12, 2.), default=.12)
    parser.add_argument("--moving", action="store_true")
    parser.add_argument("--legacy-pose", action="store_true", help="pre-fix goalkeeper pose control")
    parser.add_argument("--long-loose", action="store_true", help="observe a loose interval before recovery")
    parser.add_argument("--action", choices=("none", "immediate", "feint"), default="none", help="owning production-input interruption control")
    parser.add_argument("--reset-neutral", action="store_true", help="reset the remote neutral presentation epoch before recovery")
    parser.add_argument("--contested", action="store_true")
    parser.add_argument("--paced", action="store_true", help="diagnostic server pacing; no real-time timing claim")
    parser.add_argument("--view", choices=("gameplay", "side"), default="gameplay")
    parser.add_argument("--compatibility", action="store_true", help="explicit supplemental OpenGL capture")
    parser.add_argument("--resolution", default="960x540")
    parser.add_argument("--port", type=int, default=23469)
    args = parser.parse_args()
    if args.paced and not args.remote:
        parser.error("--paced requires --remote")
    if args.actor == "client" and not args.remote:
        parser.error("--actor client requires --remote")
    if args.reset_neutral and not args.remote:
        parser.error("--reset-neutral requires --remote")
    if not 1 <= args.port <= 65535:
        parser.error("--port must be between 1 and 65535")
    args.output = (ROOT / args.output).resolve()
    capture_root = (ROOT / ".godot" / "427").resolve()
    if args.output == capture_root or not args.output.is_relative_to(capture_root):
        parser.error("output must be a fresh directory below .godot/427")
    args.output.mkdir(parents=True, exist_ok=False)
    processes = []
    try:
        if not args.remote:
            local = start(args, args.output / "local", "local")
            processes.append(local)
            finish(*local)
        else:
            server = start(args, args.output / "server", "server")
            processes.append(server)
            wait_ready(*server)
            client = start(args, args.output / "client", "client")
            processes.append(client)
            finish(*client)
            finish(*server)
    finally:
        # Also covers failed client creation and client timeout/nonzero exit.
        for process, log in reversed(processes):
            stop(process, log)
    print(f"[rebound-capture] CAPTURED {args.output}", flush=True)


if __name__ == "__main__":
    main()
