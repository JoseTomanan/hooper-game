"""Capture #418 through production input. Headless output is diagnostic state only."""
import argparse
import os
from pathlib import Path
import subprocess
import time

ROOT = Path(__file__).resolve().parents[1]


def start(args, output, role, hand, variant):
    cmd = [args.godot, "--path", str(ROOT), "--max-fps", "60", "--disable-vsync",
           "--resolution", args.resolution, "--log-file", str(output) + ".godot.log"]
    if not args.remote:
        cmd += ["--fixed-fps", "60"]
    if args.headless or role == "server":
        cmd += ["--headless"]
    elif args.compatibility:
        cmd += ["--rendering-method", "gl_compatibility", "--rendering-driver", "opengl3"]
    cmd += ["res://tests/integration/JumpshotFormCapture.tscn", "--",
            "--capture-output=" + str(output), "--capture-role=" + role,
            "--capture-origin=" + hand, "--capture-variant=" + variant,
            "--capture-view=" + args.view, "--capture-commit=" + args.commit,
            "--capture-port=" + str(args.port)]
    if args.headless or role == "server":
        cmd += ["--capture-images=false"]
    options = {}
    if os.name == "nt":
        info = subprocess.STARTUPINFO()
        info.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        info.wShowWindow = subprocess.SW_HIDE
        options["startupinfo"] = info
    log = open(str(output) + ".stdout.log", "w", encoding="utf-8")
    # Sequence arguments preserve literal paths with spaces, without a shell.
    # https://docs.python.org/3/library/subprocess.html#popen-constructor
    try:
        return subprocess.Popen(cmd, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT, **options), log
    except BaseException:
        log.close()
        raise


def finish(process, log):
    try:
        if process.wait(timeout=90):
            raise RuntimeError("Godot capture failed: " + log.name)
    finally:
        if process.poll() is None:
            process.kill()
            process.wait(timeout=10)
        log.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--godot", default=os.environ.get("GODOT", "godot"))
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--headless", action="store_true")
    parser.add_argument("--remote", action="store_true")
    parser.add_argument("--compatibility", action="store_true")
    parser.add_argument("--resolution", default="960x540")
    parser.add_argument("--port", type=int, default=23468)
    parser.add_argument("--origin", choices=("Right", "Left"))
    parser.add_argument("--variant", choices=("normal", "early", "late", "fadeaway"))
    parser.add_argument("--view", choices=("gameplay", "front", "side"), default="gameplay")
    parser.add_argument("--repeat", type=int, default=1)
    args = parser.parse_args()
    args.output = (ROOT / args.output).resolve()
    if not args.output.is_relative_to(ROOT / ".godot" / "418") or args.repeat < 1:
        parser.error("output must be below .godot/418; repeat must be positive")
    args.output.mkdir(parents=True, exist_ok=False)
    for hand in [args.origin] if args.origin else ("Right", "Left"):
        for variant in [args.variant] if args.variant else ("normal", "early", "late", "fadeaway"):
            for repeat in range(args.repeat):
                output = args.output / f"{hand.lower()}-{variant}-{repeat + 1}"
                if not args.remote:
                    finish(*start(args, output, "local", hand, variant))
                else:
                    server, server_log = start(args, Path(str(output) + "-server"), "server", hand, variant)
                    try:
                        deadline = time.monotonic() + 20
                        while "Spawned player for peer 1" not in Path(server_log.name).read_text(encoding="utf-8", errors="replace"):
                            if server.poll() is not None or time.monotonic() > deadline:
                                raise RuntimeError("server not ready: " + server_log.name)
                            time.sleep(.1)
                        finish(*start(args, output, "client", hand, variant))
                        finish(server, server_log)
                    finally:
                        if server.poll() is None:
                            server.kill()
                            server.wait(timeout=10)
                        server_log.close()
                print(f"[jumpshot-capture] CAPTURED {output}", flush=True)


if __name__ == "__main__":
    main()
