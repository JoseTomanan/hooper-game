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
    if not args.remote or args.paced:
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
    if args.paced:
        cmd += ["--capture-paced=true", "--capture-pacing-ms=" + str(args.pacing_ms)]
    if args.entry:
        cmd += ["--capture-entry=" + args.entry, "--capture-entry-offset=" + str(args.entry_offset)]
    if args.subject_role != "host":
        cmd += ["--capture-subject-role=" + args.subject_role]
    if args.before440:
        cmd += ["--capture-before440=true"]
    if args.legacy_shot:
        cmd += ["--capture-legacy-shot=true"]
    if args.fadeaway_substitute:
        cmd += ["--capture-fadeaway-substitute=" + args.fadeaway_substitute]
    if getattr(args, "heading_angle", None) is not None:
        cmd += ["--capture-heading-angle=" + str(args.heading_angle),
                "--capture-heading-rate=" + str(args.heading_rate),
                "--capture-heading-entry=" + args.heading_entry]
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


def run_case(args, output, hand, variant):
    if not args.remote:
        finish(*start(args, output, "local", hand, variant))
    else:
        server, server_log = start(args, Path(str(output) + "-server"), "server", hand, variant)
        owner = owner_log = None
        try:
            deadline = time.monotonic() + 20
            ready = ("Dedicated server running headless" if args.subject_role == "owner" else "Spawned player for peer 1")
            while ready not in Path(server_log.name).read_text(encoding="utf-8", errors="replace"):
                if server.poll() is not None or time.monotonic() > deadline:
                    raise RuntimeError("server not ready: " + server_log.name)
                time.sleep(.1)
            if args.subject_role == "owner":
                owner, owner_log = start(args, Path(str(output) + "-owner"), "owner", hand, variant)
                deadline = time.monotonic() + 20
                while "Connected to server as peer" not in Path(owner_log.name).read_text(encoding="utf-8", errors="replace"):
                    if owner.poll() is not None or time.monotonic() > deadline:
                        raise RuntimeError("owner not connected: " + owner_log.name)
                    time.sleep(.1)
                observer, observer_log = start(args, output, "observer", hand, variant)
                try:
                    finish(owner, owner_log)
                    finish(observer, observer_log)
                finally:
                    if observer.poll() is None:
                        observer.kill(); observer.wait(timeout=10)
                    observer_log.close()
            else:
                finish(*start(args, output, "client", hand, variant))
            finish(server, server_log)
        finally:
            if owner is not None and owner.poll() is None:
                owner.kill(); owner.wait(timeout=10)
            if owner_log is not None: owner_log.close()
            if server.poll() is None:
                server.kill()
                server.wait(timeout=10)
            server_log.close()
    print(f"[jumpshot-capture] CAPTURED {output}", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--godot", default=os.environ.get("GODOT", "godot"))
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--headless", action="store_true")
    parser.add_argument("--remote", action="store_true")
    parser.add_argument("--paced", action="store_true", help="diagnostic server pacing; no real-time timing claim")
    parser.add_argument("--pacing-ms", type=int, choices=(80, 160), default=80,
                        help="producer delay for diagnostic readback coverage; requires --paced")
    parser.add_argument("--legacy-shot", action="store_true", help="supplemental before reproduction from the preserved three-clip fixture")
    parser.add_argument("--fadeaway-substitute", choices=("before429", "before439", "normal"),
                        help="isolated production fadeaway asset substitution for diagnostic proof")
    parser.add_argument("--heading-angle", type=float, help="initial rim-relative heading angle in degrees")
    parser.add_argument("--heading-rate", type=float, default=900,
                        help="heading-fixture turn rate in degrees per second; used with --heading-angle")
    parser.add_argument("--heading-entry", choices=("stationary", "moving"), default="stationary",
                        help="heading-fixture entry; used with --heading-angle")
    parser.add_argument("--compatibility", action="store_true")
    parser.add_argument("--resolution", default="960x540")
    parser.add_argument("--port", type=int, default=23468)
    parser.add_argument("--origin", choices=("Right", "Left"))
    parser.add_argument("--variant", choices=("normal", "early", "late", "fadeaway"))
    parser.add_argument("--view", choices=("gameplay", "front", "side"), default="gameplay")
    parser.add_argument("--repeat", type=int, default=1)
    parser.add_argument("--entry", choices=("held", "dribble", "moving"))
    parser.add_argument("--entry-offset", type=int, default=0)
    parser.add_argument("--subject-role", choices=("host", "owner"), default="host")
    parser.add_argument("--before440", action="store_true", help="isolated pre-440 normal asset substitution; playback unchanged")
    args = parser.parse_args()
    if args.subject_role == "owner" and not args.remote:
        parser.error("owner subject requires --remote (dedicated server + owner + observer)")
    if args.entry_offset < 0:
        parser.error("entry offset must be nonnegative")
    if args.paced and not args.remote:
        parser.error("--paced requires --remote")
    if args.pacing_ms != 80 and not args.paced:
        parser.error("--pacing-ms requires --paced")
    args.output = (ROOT / args.output).resolve()
    if not any(args.output.is_relative_to(ROOT / ".godot" / number) for number in ("418", "440")) or args.repeat < 1:
        parser.error("output must be below .godot/418 or .godot/440; repeat must be positive")
    args.output.mkdir(parents=True, exist_ok=False)
    for hand in [args.origin] if args.origin else ("Right", "Left"):
        for variant in [args.variant] if args.variant else ("normal", "early", "late", "fadeaway"):
            for repeat in range(args.repeat):
                stem = (f"{hand.lower()}-{args.entry}-{variant}-o{args.entry_offset}" if args.entry
                        else f"{hand.lower()}-{variant}")
                output = args.output / f"{stem}-{repeat + 1}"
                run_case(args, output, hand, variant)


if __name__ == "__main__":
    main()
