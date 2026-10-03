#!/usr/bin/env python3
"""Non-authoritative accounting helpers for issue #388's reuse spike.

This module deliberately does not alter ``harness_catalog.py``.  It validates
the probe's JSONL event protocol so an experiment cannot turn a missing,
duplicated, or misattributed result into a green run.
"""

from __future__ import annotations

import argparse
import json
import platform
import re
import subprocess
import sys
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable, Mapping, Sequence


_STABLE_ID = re.compile(r"^[a-z0-9]+(?:-[a-z0-9]+)*$")


@dataclass(frozen=True)
class AccountingReport:
    passed_ids: tuple[str, ...]
    errors: tuple[str, ...]
    active_case_id: str | None
    log_path: Path | None

    @property
    def ok(self) -> bool:
        return not self.errors

    def summary(self) -> str:
        lines = ["PASS" if self.ok else "FAIL"]
        lines.extend(self.errors)
        if self.log_path is not None:
            lines.append(f"log: {self.log_path.as_posix()}")
        return "\n".join(lines)


def _validate_selected_ids(selected_ids: Sequence[str]) -> tuple[str, ...]:
    selected = tuple(selected_ids)
    if not selected:
        raise ValueError("at least one stable case ID is required")
    if len(set(selected)) != len(selected):
        raise ValueError("selected stable case IDs must be unique")
    invalid = [case_id for case_id in selected if not _STABLE_ID.fullmatch(case_id)]
    if invalid:
        raise ValueError(f"invalid stable case ID(s): {', '.join(invalid)}")
    return selected


def account_events(
    selected_ids: Sequence[str],
    events: Iterable[Mapping[str, object]],
    *,
    returncode: int | None,
    timed_out: bool = False,
    log_path: Path | None = None,
) -> AccountingReport:
    """Account for one process without trusting its exit code alone.

    A selected ID passes only when it emits exactly one explicit ``pass``
    result.  Unknown, duplicate, malformed, missing, timeout, and abrupt-exit
    outcomes all fail closed.  Result events clear the active ID so a later
    process exit is not falsely blamed on a completed case.
    """

    selected = _validate_selected_ids(selected_ids)
    selected_set = set(selected)
    results: dict[str, str] = {}
    errors: list[str] = []
    active_case_id: str | None = None

    for event in events:
        event_kind = event.get("event")
        case_id = event.get("case_id")
        if not isinstance(case_id, str):
            errors.append("event missing string case_id")
            continue

        if event_kind == "start":
            if case_id not in selected_set:
                errors.append(f"unknown start for {case_id}")
                continue
            active_case_id = case_id
            continue

        if event_kind != "result":
            errors.append(f"unknown event {event_kind!r} for {case_id}")
            continue
        if case_id not in selected_set:
            errors.append(f"unknown result for {case_id}")
            continue
        if case_id in results:
            errors.append(f"duplicate result for {case_id}")
            continue

        status = event.get("status")
        if status not in ("pass", "fail"):
            errors.append(f"invalid result status for {case_id}: {status!r}")
            continue
        results[case_id] = status
        if status == "fail":
            message = event.get("message")
            detail = message if isinstance(message, str) and message else "assertion failed"
            errors.append(f"{case_id}: {detail}")
        if active_case_id == case_id:
            active_case_id = None

    for case_id in selected:
        if case_id not in results:
            errors.append(f"missing result for {case_id}")

    if timed_out:
        target = active_case_id or "no reported case"
        errors.append(f"timeout while {target} was active")
    elif returncode not in (0, None) and active_case_id is not None:
        errors.append(f"process exited {returncode} while {active_case_id} was active")
    elif returncode not in (0, None) and not any(status == "fail" for status in results.values()):
        errors.append(f"process exited {returncode} without an attributed assertion failure")

    passed = tuple(case_id for case_id in selected if results.get(case_id) == "pass")
    return AccountingReport(passed, tuple(errors), active_case_id, log_path)


def parse_event_lines(lines: Iterable[str]) -> tuple[Mapping[str, object], ...]:
    events: list[Mapping[str, object]] = []
    for line_number, line in enumerate(lines, start=1):
        if not line.strip():
            continue
        try:
            event = json.loads(line)
        except json.JSONDecodeError as error:
            raise ValueError(f"malformed JSONL event at line {line_number}: {error.msg}") from error
        if not isinstance(event, dict):
            raise ValueError(f"JSONL event at line {line_number} is not an object")
        events.append(event)
    return tuple(events)


def read_events(path: Path) -> tuple[Mapping[str, object], ...]:
    if not path.exists():
        return ()
    return parse_event_lines(path.read_text(encoding="utf-8").splitlines())


def build_godot_command(
    godot: Path,
    repo_root: Path,
    log_path: Path,
    event_path: Path,
    selected_ids: Sequence[str],
    *,
    leak: str,
    failure: str,
) -> list[str]:
    selected = _validate_selected_ids(selected_ids)
    return [
        str(godot),
        "--headless",
        "--path",
        str(repo_root),
        "--log-file",
        log_path.as_posix(),
        "res://tests/integration/SameSceneReuseProbe.tscn",
        "--",
        "--probe-events",
        event_path.as_posix(),
        "--probe-sequence",
        ",".join(selected),
        "--probe-leak",
        leak,
        "--probe-failure",
        failure,
    ]


def resolve_output_dir(repo_root: Path, output_dir: Path) -> Path:
    root = repo_root.resolve()
    resolved = (root / output_dir).resolve() if not output_dir.is_absolute() else output_dir.resolve()
    if not resolved.is_relative_to(root):
        raise ValueError("--output-dir must stay inside the workspace")
    return resolved


def _run_process(
    command: Sequence[str],
    repo_root: Path,
    selected_ids: Sequence[str],
    event_path: Path,
    log_path: Path,
    timeout_seconds: float,
) -> tuple[AccountingReport, float]:
    started = time.perf_counter()
    returncode: int | None = None
    timed_out = False
    try:
        completed = subprocess.run(
            command,
            cwd=repo_root,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            timeout=timeout_seconds,
            check=False,
        )
        returncode = completed.returncode
    except subprocess.TimeoutExpired:
        timed_out = True
    elapsed = time.perf_counter() - started

    try:
        events = read_events(repo_root / event_path)
    except ValueError as error:
        events = ()
        malformed = account_events(
            selected_ids,
            events,
            returncode=returncode,
            timed_out=timed_out,
            log_path=log_path,
        )
        return AccountingReport(
            malformed.passed_ids,
            (str(error), *malformed.errors),
            malformed.active_case_id,
            malformed.log_path,
        ), elapsed

    return account_events(
        selected_ids,
        events,
        returncode=returncode,
        timed_out=timed_out,
        log_path=log_path,
    ), elapsed


def _run_measurement(args: argparse.Namespace, repo_root: Path) -> int:
    output_dir = Path(args.output_dir)
    resolved_output_dir = resolve_output_dir(repo_root, output_dir)
    resolved_output_dir.mkdir(parents=True, exist_ok=True)

    sequence = tuple(part.strip() for part in args.sequence.split(",") if part.strip())
    _validate_selected_ids(sequence)
    samples: list[dict[str, object]] = []
    all_ok = True
    boot_count = 0

    for repetition in range(1, args.repeat + 1):
        process_groups = (sequence,) if args.mode == "batch" else tuple((case_id,) for case_id in sequence)
        sample_started = time.perf_counter()
        process_reports = []
        for process_number, selected_ids in enumerate(process_groups, start=1):
            stem = f"{args.mode}-r{repetition}-p{process_number}"
            log_path = output_dir / f"{stem}.log"
            event_path = output_dir / f"{stem}.jsonl"
            command = build_godot_command(
                Path(args.godot),
                repo_root,
                log_path,
                event_path,
                selected_ids,
                leak=args.leak,
                failure=args.failure,
            )
            report, elapsed = _run_process(
                command,
                repo_root,
                selected_ids,
                event_path,
                log_path,
                args.timeout,
            )
            boot_count += 1
            all_ok &= report.ok
            process_reports.append(
                {
                    "selected_ids": list(selected_ids),
                    "wall_seconds": elapsed,
                    "ok": report.ok,
                    "errors": list(report.errors),
                    "log": log_path.as_posix(),
                    "events": event_path.as_posix(),
                }
            )
        samples.append(
            {
                "repetition": repetition,
                "wall_seconds": time.perf_counter() - sample_started,
                "processes": process_reports,
            }
        )

    version = subprocess.run(
        [args.godot, "--version"],
        cwd=repo_root,
        capture_output=True,
        text=True,
        check=False,
    ).stdout.strip()
    measurement = {
        "authoritative": False,
        "mode": args.mode,
        "sequence": list(sequence),
        "repeat": args.repeat,
        "boot_count": boot_count,
        "leak": args.leak,
        "failure": args.failure,
        "timeout_seconds": args.timeout,
        "godot_version": version,
        "os": platform.platform(),
        "processor": platform.processor(),
        "python": sys.version.split()[0],
        "samples": samples,
        "ok": all_ok,
    }
    measurement_path = resolved_output_dir / f"measurement-{args.mode}.json"
    measurement_path.write_text(json.dumps(measurement, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(measurement, indent=2))
    return 0 if all_ok else 1


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    run = subparsers.add_parser("run", help="run the non-authoritative Godot reuse probe")
    run.add_argument("--godot", required=True)
    run.add_argument("--mode", choices=("isolated", "batch"), required=True)
    run.add_argument("--sequence", default="probe-a-first,probe-b,probe-a-second")
    run.add_argument("--repeat", type=int, default=5)
    run.add_argument("--timeout", type=float, default=15.0)
    run.add_argument("--leak", choices=("none", "static", "input", "resource", "autoload", "timer", "deferred", "cached-node"), default="none")
    run.add_argument("--failure", choices=("none", "assert", "timeout", "crash", "missing", "duplicate"), default="none")
    run.add_argument("--output-dir", default=".godot/issue388-probe")
    return parser


def main(argv: Sequence[str] | None = None, repo_root: Path | None = None) -> int:
    args = _parser().parse_args(argv)
    root = repo_root or Path(__file__).resolve().parents[1]
    if args.repeat < 1 or args.timeout <= 0:
        raise ValueError("--repeat and --timeout must be positive")
    return _run_measurement(args, root)


if __name__ == "__main__":
    raise SystemExit(main())
