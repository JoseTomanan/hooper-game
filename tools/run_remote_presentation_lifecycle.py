"""Real two-peer game-over drain and transport reconnect proof (#414)."""

import argparse
import json
import os
from pathlib import Path
import signal
import subprocess
import time
import uuid

from harness_catalog import process_group_options, terminate_process_tree

ROOT = Path(__file__).resolve().parents[1]
PREFIX = '[remote-lifecycle]'


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--godot', required=True)
    parser.add_argument('--port', type=int, default=23467)
    parser.add_argument('--output', type=Path, default=ROOT / '.godot/remote-presentation-lifecycle')
    args = parser.parse_args(argv)
    if not 0 < args.port < 65536:
        parser.error('port must be between 1 and 65535')
    output = args.output.resolve()
    if not output.is_relative_to((ROOT / '.godot').resolve()):
        parser.error('output must be within the workspace .godot directory')
    output.mkdir(parents=True, exist_ok=True)
    token = uuid.uuid4().hex
    peers, streams, jobs, logs = {}, {}, {}, {}
    result = {'passed': False, 'reason': '', 'port': args.port, 'peers': {}}

    def launch(role):
        stdout = output / f'{token}-{role}-stdout.log'
        native = output / f'{token}-{role}-native.log'
        logs[role] = stdout
        stream = stdout.open('xb')
        streams[role] = stream
        command = [args.godot, '--headless', '--path', str(ROOT), '--log-file', str(native),
                   '--max-fps', '60', 'res://tests/integration/RemotePresentationLifecycleTest.tscn',
                   '--', f'--harness-role={role}', f'--harness-port={args.port}']
        options = process_group_options()
        launcher = subprocess.Popen
        if os.name == 'nt':
            # Hide console helpers without changing their real-time physics cadence.
            # https://docs.python.org/3.14/library/subprocess.html#subprocess.STARTUPINFO
            startup = subprocess.STARTUPINFO()
            startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
            startup.wShowWindow = subprocess.SW_HIDE
            options['startupinfo'] = startup
            from windows_process_job import WindowsProcessJob
            jobs[role] = WindowsProcessJob()
            launcher = jobs[role].launch
        # Argument lists preserve Windows paths with spaces without a shell.
        # https://docs.python.org/3.14/library/subprocess.html#frequently-used-arguments
        peers[role] = launcher(command, cwd=ROOT, stdin=subprocess.DEVNULL,
                               stdout=stream, stderr=subprocess.STDOUT, **options)
        result['peers'][role] = {'stdout': str(stdout), 'native': str(native), 'command': command}

    def text(role):
        return logs[role].read_text(encoding='utf-8', errors='replace')

    try:
        launch('server')
        deadline = time.monotonic() + 15
        while f'{PREFIX} READY server' not in text('server').splitlines():
            if peers['server'].poll() is not None or f'{PREFIX} FAIL' in text('server'):
                raise RuntimeError('server failed before semantic READY')
            if time.monotonic() >= deadline:
                raise RuntimeError('semantic READY timeout')
            time.sleep(.025)
        launch('client')
        deadline = time.monotonic() + 90
        while True:
            complete = True
            for role, process in peers.items():
                code = process.poll()
                if f'{PREFIX} FAIL' in text(role):
                    raise RuntimeError(f'{role} reported FAIL')
                if code is None:
                    complete = False
                elif code != 0:
                    raise RuntimeError(f'{role} exited {code}')
                elif f'{PREFIX} {role} RESULT: PASS (exit 0)' not in text(role).splitlines():
                    raise RuntimeError(f'{role} exited zero without required PASS evidence')
            if complete:
                result['passed'] = True
                result['reason'] = 'both peers exited zero with lifecycle PASS evidence'
                break
            if time.monotonic() >= deadline:
                raise RuntimeError('90 second proof timeout')
            time.sleep(.025)
    except (OSError, RuntimeError) as error:
        result['reason'] = str(error)
    finally:
        for role, process in reversed(tuple(peers.items())):
            result['peers'][role]['exitBeforeCleanup'] = process.poll()
            if role in jobs:
                jobs[role].close()
                process.wait(timeout=3)
            else:
                terminate_process_tree(process)
        for stream in streams.values():
            stream.close()
        (output / f'{token}-summary.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
        print(json.dumps(result, indent=2), flush=True)
    return 0 if result['passed'] else 1


def interrupted(signum, frame):
    raise KeyboardInterrupt


if __name__ == '__main__':
    signal.signal(signal.SIGTERM, interrupted)
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        print(f'{PREFIX} interrupted; owned peers cleaned up.', flush=True)
        raise SystemExit(130)
