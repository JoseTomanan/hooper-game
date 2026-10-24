"""Three real ENet peers, with independently measured UDP relays for normal-shot Startup heading (#428)."""
import argparse
import heapq
import json
import os
from pathlib import Path
import secrets
import select
import socket
import subprocess
import time

ROOT = Path(__file__).resolve().parents[1]
SCENE = "res://tests/integration/NetShotHeadingTest.tscn"


def bound_socket():
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind(("127.0.0.1", 0))
    sock.setblocking(False)
    return sock


def trial(args, delay, index):
    directory = args.output / f"{delay}ms-{index}"
    directory.mkdir(parents=True, exist_ok=True)
    reservation = bound_socket()
    server_address = reservation.getsockname()
    reservation.close()
    links = {role: {"front": bound_socket(), "upstream": bound_socket(), "client": None}
             for role in ("driver", "observer")}
    sockets = {sock: (role, direction) for role, link in links.items()
               for direction, sock in (("client_to_server", link["front"]), ("server_to_client", link["upstream"]))}
    dwell = {(role, direction): [] for role, direction in sockets.values()}
    enqueued = dict.fromkeys(dwell, 0)
    children, streams, queue, logs = [], [], [], {}
    serial = 0
    observer_pause_until = 0.0
    observer_pause_applied = False
    error = None
    run_id = secrets.token_hex(6)
    # Source: https://docs.python.org/3.14/library/time.html#time.monotonic
    started = time.monotonic()

    def launch(role, port):
        log = directory / f"{role}.stdout.log"
        stream = log.open("w", encoding="utf-8")
        streams.append(stream)
        native = ROOT / ".godot" / "shot-net" / "native" / run_id / f"{role}.log"
        native.parent.mkdir(parents=True, exist_ok=True)
        command = [args.godot, "--headless", "--path", str(ROOT), "--log-file", str(native), SCENE,
                   "--", f"--harness-role={role}", f"--harness-port={port}", f"--mutation={args.mutation}"]
        flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
        # Source: https://docs.python.org/3.14/library/subprocess.html#subprocess.Popen
        children.append(subprocess.Popen(command, cwd=ROOT, stdout=stream, stderr=subprocess.STDOUT, creationflags=flags))
        logs[role] = log

    try:
        launch("server", server_address[1])
        deadline = time.monotonic() + 10
        while "[shot-net] READY server" not in logs["server"].read_text(encoding="utf-8", errors="replace"):
            if children[0].poll() is not None or time.monotonic() > deadline:
                raise RuntimeError("server readiness exceeded 10 seconds")
            time.sleep(.02)
        for role, link in links.items():
            launch(role, link["front"].getsockname()[1])
        deadline = time.monotonic() + 45
        while any(child.poll() is None for child in children):
            if time.monotonic() > deadline:
                raise RuntimeError("trial exceeded 45 seconds")
            if not observer_pause_applied and "[shot-net] observer-delivery-pause 100ms" in logs["server"].read_text(encoding="utf-8", errors="replace"):
                observer_pause_applied = True
                observer_pause_until = time.monotonic() + .1
            readable, _, _ = select.select(list(sockets), [], [], .001)
            for sock in readable:
                try:
                    payload, address = sock.recvfrom(65535)
                except ConnectionResetError:
                    # Windows reports ICMP port-unreachable after a peer closes its UDP
                    # endpoint. Keep ownership/exit/PASS checks as the verdict; an early
                    # failed peer still fails those checks or the bounded deadline.
                    # Source: https://learn.microsoft.com/en-us/windows/win32/api/winsock2/nf-winsock2-recvfrom
                    continue
                role, direction = sockets[sock]
                link = links[role]
                if direction == "client_to_server":
                    link["client"] = address
                    target, destination = link["upstream"], server_address
                else:
                    if link["client"] is None:
                        raise RuntimeError("upstream packet before client address")
                    target, destination = link["front"], link["client"]
                serial += 1
                queued = time.monotonic()
                enqueued[role, direction] += 1
                heapq.heappush(queue, (queued + delay / 1000, serial, queued, target, destination, role, direction, payload))
            while queue and queue[0][0] <= time.monotonic():
                _, _, queued, target, destination, role, direction, payload = heapq.heappop(queue)
                if role == "observer" and direction == "server_to_client" and time.monotonic() < observer_pause_until:
                    serial += 1
                    heapq.heappush(queue, (observer_pause_until, serial, queued, target, destination, role, direction, payload))
                    continue
                # Source: https://docs.python.org/3.14/library/socket.html#socket.socket.sendto
                target.sendto(payload, destination)
                dwell[role, direction].append((time.monotonic() - queued) * 1000)
            if any(child.poll() not in (None, 0) for child in children):
                raise RuntimeError("Godot peer failed; inspect retained logs")
        for child, role in zip(children, ("server", "driver", "observer")):
            if child.returncode != 0 or f"[harness] PASS shot-net {role}:" not in logs[role].read_text(encoding="utf-8", errors="replace"):
                raise RuntimeError(f"{role} missing exit-zero/PASS")
        if not all(dwell.values()):
            raise RuntimeError("both relay directions must carry traffic on both links")
        if any(min(values) < delay for values in dwell.values()):
            raise RuntimeError("measured relay dwell fell below the requested injection")
    except Exception as exception:
        error = str(exception)
    finally:
        for child in children:
            if child.poll() is None:
                child.kill()
            child.wait(timeout=5)
        for stream in streams:
            stream.close()
        for sock in sockets:
            sock.close()
    observations = {role: [line for line in log.read_text(encoding="utf-8", errors="replace").splitlines()
                           if "[shot-net]" in line or "[harness]" in line]
                    for role, log in logs.items()}
    relay = {}
    for (role, direction), values in dwell.items():
        relay.setdefault(role, {})[direction] = {
            "enqueued": enqueued[role, direction], "forwarded": len(values),
            "discarded_at_teardown": enqueued[role, direction] - len(values),
            "min_ms": min(values) if values else None,
            "mean_ms": sum(values) / len(values) if values else None,
            "max_ms": max(values) if values else None}
    summary = {"mutation": args.mutation, "requested_delay_ms": delay, "repeat": index,
               "observer_delivery_pause_ms": 100 if observer_pause_applied else 0,
               "passed": error is None, "error": error, "relay": relay, "observations": observations,
               "elapsed_seconds": time.monotonic() - started,
               "native_logs": str(ROOT / ".godot" / "shot-net" / "native" / run_id)}
    (directory / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(json.dumps(summary), flush=True)
    return summary


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--godot", required=True)
    parser.add_argument("--mutation", choices=("none", "drop-target", "wrong-instance"), default="none")
    parser.add_argument("--delay-ms", type=int, choices=(0, 30), default=0)
    parser.add_argument("--repeat", type=int, default=1)
    parser.add_argument("--all", action="store_true")
    parser.add_argument("--output", type=Path, default=ROOT / ".godot" / "shot-net" / "results")
    args = parser.parse_args()
    if args.repeat < 1:
        parser.error("--repeat must be positive")
    conditions = [0, 30] if args.all else [args.delay_ms]
    summaries = [trial(args, delay, i) for delay in conditions for i in range(args.repeat)]
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "summary.json").write_text(json.dumps(summaries, indent=2), encoding="utf-8")
    return 0 if all(summary["passed"] for summary in summaries) else 1


if __name__ == "__main__":
    raise SystemExit(main())

