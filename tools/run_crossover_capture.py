"""Run #414's production-input capture matrix (native or explicit headless diagnostics).

Every run creates fresh outputs below the workspace .godot directory. On Linux,
run inside xvfb-run for rendered evidence. This tool does not accept feel.
"""

import argparse
import os
from pathlib import Path
import subprocess
import time

ROOT = Path(__file__).resolve().parents[1]
SCENE = "res://tests/integration/CrossoverContinuityCapture.tscn"


def command(args, directory, role, origin, motion, count):
    result = [args.godot, "--path", str(ROOT), "--max-fps", "60",
              "--disable-vsync", "--resolution", args.resolution, "--log-file", str(directory) + ".godot.log"]
    # Fixed-fps disables real-time synchronization. Network peers must advance
    # with wall time; otherwise one can finish before the other connects.
    # https://docs.godotengine.org/en/4.7/tutorials/editor/command_line_tutorial.html
    if not args.remote or args.paced:
        result += ["--fixed-fps", "60"]
    if args.headless or role == "server":
        result.append("--headless")
    elif args.compatibility:
        result += ["--rendering-method", "gl_compatibility", "--rendering-driver", "opengl3"]
    result += [SCENE, "--", "--capture-output=" + str(directory), "--capture-role=" + role,
               "--capture-move=" + args.move, "--capture-origin=" + origin, "--capture-motion=" + motion, "--capture-count=" + str(count),
               "--capture-commit=" + args.commit, "--capture-port=" + str(args.port)]
    if args.headless or role == "server":
        result.append("--capture-images=false")
    if args.disable_pose:
        result.append("--capture-disable-crossover-pose=true")
    if args.hold_movement:
        result.append("--capture-hold-movement=true")
    if args.lose_ball != "none":
        result.append("--capture-lose-ball=" + args.lose_ball)
    if role == "client" and args.presentation_delay_ticks:
        result.append("--capture-presentation-delay-ticks=" + str(args.presentation_delay_ticks))
    if role == "server" and args.paced:
        # Pace wall time only; fixed-fps still supplies one 1/60 simulation
        # step per frame. This gives the native observer time to persist PNGs.
        result.append("--capture-delay-ms=100")
    if args.remote and args.paced:
        result += ["--capture-max-physics-gap=1", "--capture-remote-paced=true"]
    elif args.remote:
        result.append("--capture-max-physics-gap=2")
    return result


def start(args, directory, role, origin, motion, count):
    # Argument sequences avoid shell quoting and paths-with-spaces hazards.
    # https://docs.python.org/3.14/library/subprocess.html#popen-constructor
    options = {}
    if os.name == "nt":
        startup = subprocess.STARTUPINFO()
        startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        startup.wShowWindow = subprocess.SW_HIDE
        options["startupinfo"] = startup
    log = open(str(directory) + ".stdout.log", "w", encoding="utf-8")
    try:
        process = subprocess.Popen(command(args, directory, role, origin, motion, count), cwd=ROOT,
                                   stdout=log, stderr=subprocess.STDOUT, **options)
    except BaseException:
        log.close()
        raise
    return process, log


def finish(process, log):
    try:
        code = process.wait(timeout=90)
        if code:
            raise RuntimeError(f"Godot exited {code}; see {log.name}")
    finally:
        if process.poll() is None:
            process.kill()
            process.wait(timeout=10)
        log.close()


def run(args, origin, motion, count):
    name = f"{origin.lower()}-{motion}-{count}"
    directory = args.output / name
    if not args.remote:
        finish(*start(args, directory, "local", origin, motion, count))
    else:
        server_directory = args.output / (name + "-server")
        server, server_log = start(args, server_directory, "server", origin, motion, count)
        try:
            deadline = time.monotonic() + 20
            while "Spawned player for peer 1" not in Path(server_log.name).read_text(encoding="utf-8", errors="replace"):
                if server.poll() is not None or time.monotonic() >= deadline:
                    raise RuntimeError(f"server did not become ready; see {server_log.name}")
                time.sleep(.1)
            finish(*start(args, directory, "client", origin, motion, count))
            finish(server, server_log)
        finally:
            if server.poll() is None:
                server.kill()
                server.wait(timeout=10)
            server_log.close()
    print(f"[crossover-matrix] CAPTURED {name} remote={args.remote}", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--godot", default=os.environ.get("GODOT", "godot"))
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--move", choices=("crossover", "behindtheback", "hesitation", "stepback", "idle"), default="crossover")
    parser.add_argument("--remote", action="store_true")
    parser.add_argument("--headless", action="store_true")
    parser.add_argument("--disable-pose", action="store_true", help="negative control: disable the production pose adapter")
    parser.add_argument("--hold-movement", action="store_true", help="moving single-cycle return with movement held")
    parser.add_argument("--lose-ball", choices=("none", "early", "late"), default="none", help="remote single-cycle production possession award")
    parser.add_argument("--presentation-delay-ticks", type=int, choices=(0, 6), default=0,
                        help="diagnostic: hold the first Startup presentation envelope and its successors for six client physics ticks")
    parser.add_argument("--compatibility", action="store_true")
    parser.add_argument("--paced", action="store_true", help="diagnostic server pacing; excludes real-time timing claims")
    parser.add_argument("--resolution", default="960x540")
    parser.add_argument("--port", type=int, default=23464)
    parser.add_argument("--origin", choices=("Left", "Right"))
    parser.add_argument("--motion", choices=("stationary", "moving"))
    parser.add_argument("--count", type=int, choices=(1, 3))
    args = parser.parse_args()
    if args.move != "crossover" and (args.disable_pose or args.lose_ball != "none" or args.presentation_delay_ticks or args.hold_movement):
        parser.error("crossover-specific variants require --move crossover")
    if args.move in ("hesitation", "stepback", "idle") and args.count != 1:
        parser.error("identity controls require --count 1")
    if args.headless and args.paced:
        parser.error("--paced requires a rendered observer; headless fixed-fps can outrun the diagnostic server")
    if args.hold_movement and (args.motion != "moving" or args.count != 1):
        parser.error("--hold-movement requires --motion moving --count 1")
    if args.lose_ball != "none" and (not args.remote or args.count != 1):
        parser.error("--lose-ball requires --remote --count 1")
    if args.presentation_delay_ticks and (not args.remote or args.count != 1):
        parser.error("--presentation-delay-ticks requires --remote --count 1")
    args.output = (ROOT / args.output).resolve()
    if not args.output.is_relative_to(ROOT / ".godot"):
        parser.error("capture output must be below the workspace .godot directory")
    args.output.mkdir(parents=True, exist_ok=False)
    for origin in [args.origin] if args.origin else ("Right", "Left"):
        for motion in [args.motion] if args.motion else ("stationary", "moving"):
            for count in [args.count] if args.count else (1, 3):
                run(args, origin, motion, count)


if __name__ == "__main__":
    main()
