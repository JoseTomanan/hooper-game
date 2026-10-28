"""Capture #440 entry/return matrix through production input, with honest role metadata.

Capture exit zero means the fixture finished; objective continuity/form verdicts
belong to the analyzer. Headless output contains no rendered evidence.
"""
import argparse
from concurrent.futures import ThreadPoolExecutor
from copy import copy
import itertools
import os
from pathlib import Path
import subprocess

from run_jumpshot_capture import ROOT, run_case


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--godot", default=os.environ.get("GODOT", "godot"))
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--commit")
    parser.add_argument("--topology", choices=("local", "host", "owner"), default="local")
    parser.add_argument("--entries", nargs="+", choices=("held", "dribble", "moving"), default=["held", "dribble", "moving"])
    parser.add_argument("--origins", nargs="+", choices=("Right", "Left"), default=["Right", "Left"])
    parser.add_argument("--variants", nargs="+", choices=("normal", "early", "late"), default=["normal", "early", "late"])
    parser.add_argument("--offsets", nargs="+", type=int, default=[0, 7])
    parser.add_argument("--headless", action="store_true")
    parser.add_argument("--paced", action="store_true")
    parser.add_argument("--pacing-ms", type=int, choices=(80, 160), default=80)
    parser.add_argument("--before440", action="store_true", help="pre-fix assets with current playback; not historical full-behavior restoration")
    parser.add_argument("--view", choices=("gameplay", "front", "side"), default="gameplay")
    parser.add_argument("--resolution", default="960x540")
    parser.add_argument("--compatibility", action="store_true")
    parser.add_argument("--port", type=int, default=24450)
    parser.add_argument("--jobs", type=int, choices=(1, 2), default=1)
    args = parser.parse_args()
    if any(offset < 0 for offset in args.offsets): parser.error("offsets must be nonnegative")
    if args.paced and args.topology == "local": parser.error("pacing requires a network topology")
    if args.jobs > 1 and not args.headless: parser.error("parallel capture is diagnostic/headless only")
    args.output = (ROOT / args.output).resolve()
    if not args.output.is_relative_to(ROOT / ".godot" / "440"): parser.error("output must be below .godot/440")
    args.output.mkdir(parents=True, exist_ok=False)
    args.commit = args.commit or subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    args.remote = args.topology != "local"
    args.subject_role = "owner" if args.topology == "owner" else "host"
    args.legacy_shot = False
    args.fadeaway_substitute = None
    cases = list(itertools.product(args.entries, args.origins, args.variants, args.offsets))

    def capture(index_case):
        index, (entry, hand, variant, offset) = index_case
        case_args = copy(args)
        case_args.entry = entry
        case_args.entry_offset = offset
        # Distinct bind ports allow concurrent isolated runs without sharing
        # any process or simulation. Each log is unique to its case and role.
        case_args.port = args.port + index
        output = args.output / f"{hand.lower()}-{entry}-{variant}-o{offset}-1"
        run_case(case_args, output, hand, variant)

    with ThreadPoolExecutor(max_workers=args.jobs) as executor:
        list(executor.map(capture, enumerate(cases)))
    print(f"[jumpshot-matrix] CAPTURED cases={len(cases)} topology={args.topology} images={not args.headless}")


if __name__ == "__main__":
    main()
