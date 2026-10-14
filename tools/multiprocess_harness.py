"""Bounded two-peer lifecycle; scenario adapters own topology and proof policy."""

from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable
import subprocess
import os
import math
import signal
import threading
import time
import uuid

from harness_catalog import process_group_options, terminate_process_tree


@dataclass(frozen=True)
class Peer:
    role: str
    command: Callable[[Path], list[str]]
    pass_marker: str
    required: bool = True


@dataclass
class PeerEvidence:
    stdout: Path
    native: Path
    exit_before_cleanup: int | None = None
    exit_code: int | None = None

    def text(self) -> str:
        if not self.stdout.exists():
            return ''
        return self.stdout.read_text(encoding='utf-8', errors='replace')


@dataclass
class PairResult:
    passed: bool = False
    role: str = 'server'
    phase: str = 'launch'
    reason: str = ''
    ready_seconds: float | None = None
    elapsed_seconds: float = 0
    peers: dict[str, PeerEvidence] = field(default_factory=dict)


def run_pair(server: Peer, client: Peer, *, ready_marker: str,
             artifact_prefix: Path, ready_timeout: float = 15,
             proof_timeout: float = 65, cleanup_grace: float = .25,
             cwd: Path | None = None) -> PairResult:
    """Require fresh readiness, then exit-zero + role-specific proof evidence.

    Each call owns a fresh attempt, including when an adapter retries. Files,
    rather than unread PIPEs, prevent verbose peers deadlocking on pipe capacity.
    Source: https://docs.python.org/3/library/subprocess.html#subprocess.Popen.wait
    """
    if server.role == client.role or not any(p.required for p in (server, client)):
        raise ValueError('distinct roles and at least one proof peer are required')
    if any(not math.isfinite(v) or v <= 0 for v in (ready_timeout, proof_timeout, cleanup_grace)):
        raise ValueError('timeouts and cleanup grace must be finite and positive')
    result = PairResult(role=server.role)
    processes = {}
    jobs = {}
    streams = []
    started = time.monotonic()
    prefix = artifact_prefix.resolve()
    prefix.parent.mkdir(parents=True, exist_ok=True)
    token = uuid.uuid4().hex

    def launch(peer):
        result.role, result.phase = peer.role, 'launch'
        evidence = PeerEvidence(Path(f'{prefix}-{token}-{peer.role}-stdout.log'),
                                Path(f'{prefix}-{token}-{peer.role}-native.log'))
        result.peers[peer.role] = evidence
        stream = evidence.stdout.open('xb')
        streams.append(stream)
        # Sequence arguments preserve paths containing spaces without a shell.
        # Source: https://docs.python.org/3/library/subprocess.html#frequently-used-arguments
        launcher = subprocess.Popen
        if os.name == 'nt':
            from windows_process_job import WindowsProcessJob
            jobs[peer.role] = WindowsProcessJob()
            launcher = jobs[peer.role].launch
        processes[peer.role] = launcher(peer.command(evidence.native), cwd=cwd,
            stdin=subprocess.DEVNULL, stdout=stream, stderr=subprocess.STDOUT,
            **process_group_options())

    def failure(peer, phase, reason):
        result.role, result.phase, result.reason = peer.role, phase, reason
        return result

    def observed_failure(peer, phase):
        evidence = result.peers[peer.role]
        code = processes[peer.role].poll()
        evidence.exit_before_cleanup = code
        text = evidence.text()
        if code is not None and code != 0:
            return failure(peer, phase, f'exited {code}')
        if 'FAIL' in text:
            # Godot prints the verdict before Quit finishes flushing. Collect a
            # bounded natural exit so adapters can classify documented retries;
            # a marker from a still-running or stuck peer remains failure.
            if code is None:
                try:
                    code = processes[peer.role].wait(timeout=.5)
                except subprocess.TimeoutExpired:
                    pass
                evidence.exit_before_cleanup = code
            return failure(peer, phase, 'reported scenario FAIL')
        if code == 0 and peer.pass_marker not in text:
            return failure(peer, phase, 'exit zero without required PASS evidence')
        return None

    try:
        launch(server)
        # monotonic cannot go backwards when wall-clock time is adjusted.
        # Source: https://docs.python.org/3/library/time.html#time.monotonic
        deadline = started + ready_timeout
        while True:
            failed = observed_failure(server, 'readiness')
            if failed is not None:
                return failed
            if processes[server.role].poll() is not None:
                return failure(server, 'readiness', 'exited before client launch')
            if time.monotonic() >= deadline:
                return failure(server, 'readiness', 'semantic READY timeout')
            if ready_marker in result.peers[server.role].text().splitlines():
                result.ready_seconds = time.monotonic() - started
                break
            time.sleep(.025)
        launch(client)
        deadline = time.monotonic() + proof_timeout
        while True:
            complete = True
            for peer in (server, client):
                failed = observed_failure(peer, 'proof')
                if failed is not None:
                    return failed
                if not peer.required:
                    continue
                evidence = result.peers[peer.role]
                if evidence.exit_before_cleanup is None:
                    complete = False
                elif peer.pass_marker not in evidence.text():
                    return failure(peer, 'proof', 'exit zero without required PASS evidence')
            if complete:
                result.passed = True
                result.phase, result.reason = 'proof', 'all required peers exited zero with PASS'
                return result
            if time.monotonic() >= deadline:
                pending = next(p for p in (server, client)
                    if p.required and result.peers[p.role].exit_before_cleanup is None)
                return failure(pending, 'proof', 'proof timeout')
            time.sleep(.025)
    except OSError as error:
        result.reason = f'launch/io error: {error}'
        return result
    finally:
        # Defer new termination signals across finalization, including evidence
        # reads. Raising inside finally would strand independent POSIX sessions.
        # Source: https://docs.python.org/3/library/signal.html#signal.signal
        cleanup_interrupt = []
        handlers = {}
        def defer_interrupt(signum, frame):
            cleanup_interrupt.append(KeyboardInterrupt())
        if threading.current_thread() is threading.main_thread():
            for signum in (signal.SIGINT, signal.SIGTERM):
                handlers[signum] = signal.signal(signum, defer_interrupt)
        for role, process in reversed(tuple(processes.items())):
            # Another peer's teardown can take time. Recheck at this peer's
            # actual cleanup boundary before attributing any exit to our stop.
            try:
                result.peers[role].exit_before_cleanup = process.poll()
                if result.passed:
                    peer = server if role == server.role else client
                    if observed_failure(peer, 'proof') is not None:
                        result.passed = False
            except KeyboardInterrupt as error:
                cleanup_interrupt.append(error)
            except OSError as error:
                result.passed = False
                result.role, result.phase, result.reason = role, 'cleanup', str(error)
            try:
                if role in jobs:
                    jobs[role].close()
                    process.wait(timeout=cleanup_grace)
                else:
                    terminate_process_tree(process, grace_seconds=cleanup_grace)
            except (OSError, subprocess.TimeoutExpired) as error:
                result.passed = False
                result.role, result.phase, result.reason = role, 'cleanup', str(error)
            except KeyboardInterrupt as error:
                cleanup_interrupt.append(error)
                # Finish this peer too, then continue cleanup of every other peer.
                if role in jobs:
                    jobs[role].close()
                else:
                    terminate_process_tree(process, grace_seconds=cleanup_grace)
            result.peers[role].exit_code = process.poll()
        for job in jobs.values():
            try:
                job.close()
            except OSError as error:
                result.passed = False
                result.phase, result.reason = 'cleanup', str(error)
        for stream in streams:
            stream.close()
        result.elapsed_seconds = time.monotonic() - started
        for signum, handler in handlers.items():
            signal.signal(signum, handler)
        if cleanup_interrupt:
            raise cleanup_interrupt[0]


def report(result: PairResult) -> None:
    verdict = 'PASS' if result.passed else 'FAIL'
    print(f'[multiprocess] {verdict} role={result.role} phase={result.phase} '
          f'reason={result.reason} ready_seconds={result.ready_seconds} '
          f'elapsed_seconds={result.elapsed_seconds:.3f}', flush=True)
    for role, evidence in result.peers.items():
        print(f'[multiprocess] role={role} exit_before_cleanup={evidence.exit_before_cleanup} '
              f'exit={evidence.exit_code} stdout={evidence.stdout} native={evidence.native}', flush=True)
        if not result.passed:
            print(evidence.text(), flush=True)
