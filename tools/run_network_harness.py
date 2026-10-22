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
    'state-sync': Scenario('NetStateSyncTest', 'net-statesync', 23457),
    'node-replication': Scenario('NetNodeReplicationTest', 'net-noderepl', 23458),
    'behindtheback-sweep': Scenario('NetBehindTheBackSweepTest', 'net-btb-sweep', 23459),
    'defensive-telegraph': Scenario('NetDefensiveTelegraphTest', 'net-def-telegraph', 23460,
                                  default='telegraph', variants=('telegraph', 'control')),
    'exitvector-rpc': Scenario('NetExitVectorRpcTest', 'net-exitvec', 23461,
                              required=('server',), default='poisoned',
                              variants=('poisoned', 'steady'), proof_timeout=45),
    'rig-presentation': Scenario('NetRigPresentationTest', 'net-rig', 23462,
                                required=('server', 'client'), default='left',
                                variants=('left', 'right')),
    'held-possession': Scenario('NetHeldPossessionTest', 'net-held', 23466,
                               required=('server', 'client'), default='host',
                               variants=('host', 'client')),
    'moving-crossover': Scenario('MovingCrossoverLateralTest', 'moving-crossover-lateral', 23467,
                                required=('server', 'client'), default='host',
                                variants=('host', 'client', 'client-reset-race')),
}


EXIT_VECTOR_DIVERGENCE = ('[net-exitvec] FAIL (server): composed velocity diverged '
                          'from the TRUE-exit-vector oracle:')


def retryable_exit_vector_failure(result) -> bool:
    """Retry only #264's documented composition race, with natural exit evidence.

    Deterministic composition regressions still consume every attempt and fail;
    launch/readiness/timeout/auxiliary failures never become retry successes.
    """
    if result.passed or result.role != 'server' or result.phase != 'proof':
        return False
    if result.reason not in ('exited 1', 'reported scenario FAIL'):
        return False
    server = result.peers.get('server')
    client = result.peers.get('client')
    if server is None or client is None or server.exit_before_cleanup != 1:
        return False
    server_lines = server.text().splitlines()
    if '[net-exitvec] server RESULT: FAIL (exit 1)' not in server_lines:
        return False
    failures = [line for line in server_lines if 'FAIL' in line]
    if not any(line.startswith(EXIT_VECTOR_DIVERGENCE) for line in failures):
        return False
    if any(not line.startswith(EXIT_VECTOR_DIVERGENCE)
           and line != '[net-exitvec] server RESULT: FAIL (exit 1)' for line in failures):
        return False
    client_text = client.text()
    if 'FAIL' in client_text or client.exit_before_cleanup not in (None, 0):
        return False
    if client.exit_before_cleanup == 0 and '[net-exitvec] client RESULT: PASS (exit 0)' not in client_text:
        return False
    return True


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
    max_attempts = 1
    if args.family == 'exitvector-rpc':
        try:
            max_attempts = int(os.environ.get('MAX_ATTEMPTS', '3'))
            if max_attempts <= 0:
                raise ValueError('MAX_ATTEMPTS must be a positive integer')
        except ValueError:
            parser.error('MAX_ATTEMPTS must be a positive integer')
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
    for attempt in range(1, max_attempts + 1):
        if args.family == 'exitvector-rpc':
            print(f'[multiprocess] exitvector-rpc scenario={scenario} attempt={attempt}/{max_attempts}', flush=True)
        attempt_prefix = Path(f'{prefix}-attempt{attempt}') if args.family == 'exitvector-rpc' else prefix
        result = run_pair(peer('server'), peer('client'),
            ready_marker=f'[{config.prefix}] READY server', artifact_prefix=attempt_prefix,
            ready_timeout=args.ready_timeout, proof_timeout=config.proof_timeout, cwd=ROOT)
        report(result)
        if result.passed:
            return 0
        if args.family != 'exitvector-rpc' or not retryable_exit_vector_failure(result):
            return 1
        if attempt < max_attempts:
            # run_pair has already reaped both peers before returning.
            print('[multiprocess] retrying documented sub-tick composition divergence (#264)', flush=True)
    return 1


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
