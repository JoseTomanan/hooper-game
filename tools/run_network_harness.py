"""Scenario policy for the seven legacy two-peer network harness entry points."""

import argparse
from dataclasses import dataclass
import math
import os
from pathlib import Path
import signal

from multiprocess_harness import Peer, report, run_pair

ROOT = Path(__file__).resolve().parents[1]


@dataclass(frozen=True)
class Scenario:
    scene: str
    prefix: str
    port: int
    required: tuple[str, ...] = ('client',)
    default: str | None = None
    variants: tuple[str, ...] = ()
    proof_timeout: float = 65


SCENARIOS = {
    'handshake': Scenario('NetHandshakeTest', 'net-harness', 23456, proof_timeout=25),
}


def positive_seconds(value: str) -> float:
    number = float(value)
    if not math.isfinite(number) or number <= 0:
        raise argparse.ArgumentTypeError('timeout must be finite and positive')
    return number


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('family', choices=SCENARIOS)
    parser.add_argument('godot', nargs='?', default='godot')
    parser.add_argument('scenario', nargs='?')
    # SERVER_BIND_WAIT is retired, rather than silently repurposed as a timeout.
    parser.add_argument('--ready-timeout', type=positive_seconds,
                        default=os.environ.get('HARNESS_READY_TIMEOUT', '15'))
    args = parser.parse_args(argv)
    config = SCENARIOS[args.family]
    scenario = args.scenario or config.default
    if scenario is not None and scenario not in config.variants:
        parser.error(f'invalid scenario for {args.family}: {scenario}')
    try:
        port = int(os.environ.get('HARNESS_PORT', config.port))
        if not 0 < port < 65536:
            raise ValueError('port out of range')
    except ValueError as error:
        parser.error(str(error))
    if 'SERVER_BIND_WAIT' in os.environ:
        print('[multiprocess] SERVER_BIND_WAIT retired; use HARNESS_READY_TIMEOUT for a readiness deadline.', flush=True)

    def peer(role):
        def command(native):
            return [args.godot, '--log-file', str(native), '--headless', '--path', str(ROOT),
                    f'res://tests/integration/{config.scene}.tscn', '--',
                    f'--harness-role={role}', f'--harness-port={port}',
                    *([f'--harness-scenario={scenario}'] if scenario is not None else [])]
        return Peer(role, command, f'[{config.prefix}] {role} RESULT: PASS (exit 0)',
                    role in config.required)

    suffix = f'-{scenario}' if scenario is not None else ''
    prefix = ROOT / '.godot/harness-logs' / f'net-{args.family}{suffix}'
    result = run_pair(peer('server'), peer('client'),
        ready_marker=f'[{config.prefix}] READY server', artifact_prefix=prefix,
        ready_timeout=args.ready_timeout, proof_timeout=config.proof_timeout, cwd=ROOT)
    report(result)
    return 0 if result.passed else 1


def interrupted(signum, frame):
    # exec in shell adapters delivers catalog TERM here; finally owns teardown.
    # Source: https://docs.python.org/3/library/signal.html#signal.signal
    raise KeyboardInterrupt


if __name__ == '__main__':
    signal.signal(signal.SIGTERM, interrupted)
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        print('[multiprocess] interrupted; peer cleanup completed.', flush=True)
        raise SystemExit(130)
