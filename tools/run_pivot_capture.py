"""Capture one #416 case, three repetitions. Headless output is diagnostic state only."""
import argparse
import os
from pathlib import Path
import subprocess
import time

ROOT = Path(__file__).resolve().parents[1]
SCENE = "res://tests/integration/PivotContinuityCapture.tscn"


def launch(args, role):
    directory = args.output / role
    command = [args.godot, "--path", str(ROOT), "--max-fps", "60", "--disable-vsync",
               "--resolution", args.resolution, "--log-file", str(args.output / (role + ".godot.log"))]
    # Native local capture evaluates exactly one simulation tick per rendered frame.
    # Network fixed-fps is opt-in because it removes real-time synchronization.
    # https://docs.godotengine.org/en/4.7/tutorials/editor/command_line_tutorial.html
    fixed_fps = args.paced or (not args.remote and not args.headless)
    if fixed_fps:
        command += ["--fixed-fps", "60"]
    if args.headless or role == "server":
        command.append("--headless")
    elif args.compatibility:
        command += ["--rendering-method", "gl_compatibility", "--rendering-driver", "opengl3"]
    command += [SCENE, "--", "--capture-output=" + str(directory), "--capture-role=" + role,
                "--capture-case=" + args.case, "--capture-gesture=" + args.gesture,
                "--capture-commit=" + args.commit, "--capture-port=" + str(args.port),
                "--capture-images=" + str(args.images and role != "server").lower(),
                "--capture-paced=" + str(args.paced).lower(), "--capture-fixed-fps=" + str(fixed_fps).lower()]
    if args.reset_owner_presentation:
        command.append("--capture-reset-owner-presentation=true")
    if args.protection:
        command.append("--capture-protection=true")
    if args.disable_pivot_pose:
        command.append("--capture-disable-pivot-pose=true")
    if args.pivot_xfade is not None:
        command.append("--capture-pivot-xfade=" + str(args.pivot_xfade))
    options = {}
    if os.name == "nt":
        startup = subprocess.STARTUPINFO()
        startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        startup.wShowWindow = subprocess.SW_HIDE
        options["startupinfo"] = startup
    log = open(args.output / (role + ".stdout.log"), "w", encoding="utf-8")
    # Argument lists preserve paths with spaces without shell interpolation.
    # https://docs.python.org/3/library/subprocess.html#popen-constructor
    try:
        process = subprocess.Popen(command, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT, **options)
    except BaseException:
        log.close()
        raise
    return process, log


def stop(process, log):
    if process.poll() is None:
        process.kill()
        process.wait(timeout=10)
    log.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--godot", default=os.environ.get("GODOT", "godot"))
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--case", choices=("idle", "run", "left", "right"), required=True)
    parser.add_argument("--gesture", choices=("flick", "held", "forward"), required=True)
    parser.add_argument("--remote", action="store_true")
    parser.add_argument("--headless", action="store_true")
    parser.add_argument("--images", action="store_true")
    parser.add_argument("--paced", action="store_true", help="diagnostic: fixed 60Hz delta; remote server wall clock slowed to ~20Hz; no real-time claim")
    parser.add_argument("--compatibility", action="store_true", help="native local/client Mesa-compatible OpenGL renderer")
    parser.add_argument("--resolution", default="960x540")
    parser.add_argument("--reset-owner-presentation", action="store_true", help="local run-held diagnostic: reset unrelated remote history during pivot")
    parser.add_argument("--protection", action="store_true", help="right-held regression: hold production protection through pivot and neutral tail")
    parser.add_argument("--disable-pivot-pose", action="store_true", help="negative control: disable production PivotPose")
    parser.add_argument("--pivot-xfade", type=float, help="diagnostic only: override direct Pivot transition blend duration")
    parser.add_argument("--port", type=int, default=23466)
    args = parser.parse_args()
    if args.reset_owner_presentation and (args.remote or args.protection or args.case != "run" or args.gesture != "held"):
        parser.error("--reset-owner-presentation requires local run-held without protection")
    if args.paced and not args.remote:
        parser.error("--paced requires --remote; native local already uses fixed-fps60")
    if args.protection and (args.case != "right" or args.gesture != "held"):
        parser.error("--protection requires --case right --gesture held")
    if args.headless and args.images:
        parser.error("--images requires a native renderer; headless traces make no image claim")
    args.output = (ROOT / args.output).resolve()
    if not args.output.is_relative_to(ROOT / ".godot"):
        parser.error("--output must be a fresh directory below workspace .godot")
    args.output.mkdir(parents=True, exist_ok=False)
    running = []
    try:
        if args.remote:
            server, log = launch(args, "server")
            running.append((server, log))
            deadline = time.monotonic() + 20
            while "Spawned player for peer 1" not in Path(log.name).read_text(encoding="utf-8", errors="replace"):
                if server.poll() is not None or time.monotonic() >= deadline:
                    raise RuntimeError(f"server did not start; see {log.name}")
                time.sleep(.1)
            running.append(launch(args, "client"))
        else:
            running.append(launch(args, "local"))
        deadline = time.monotonic() + 90
        while any(process.poll() is None for process, _ in running):
            for process, log in running:
                if process.poll() not in (None, 0):
                    raise RuntimeError(f"Godot exited {process.returncode}; see {log.name}")
            if time.monotonic() >= deadline:
                raise TimeoutError("capture exceeded 90 seconds; see role logs")
            time.sleep(.1)
        for process, log in running:
            if process.returncode:
                raise RuntimeError(f"Godot exited {process.returncode}; see {log.name}")
        print(f"[pivot-capture] CAPTURED case={args.case} gesture={args.gesture} remote={args.remote}")
    finally:
        for process, log in running:
            stop(process, log)


if __name__ == "__main__":
    main()
