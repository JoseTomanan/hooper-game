"""Bounded two-process production-reconciliation instrument (#368).

The relay is present even at delay zero. All sockets and children are owned by
one trial; fresh processes avoid mistaking a partial netstate reset for isolation.
"""
import argparse
import heapq
import json
import os
from pathlib import Path
import select
import secrets
import socket
import subprocess
import time


ROOT = Path(__file__).resolve().parents[1]
SCENE = "res://tests/integration/CommittedReplayTest.tscn"


def bound_socket():
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind(("127.0.0.1", 0))
    sock.setblocking(False)
    return sock


def trial(args, move, contact, delay, index):
    directory = args.output / f"{move}-{contact}-{delay}ms-{index}"
    directory.mkdir(parents=True, exist_ok=True)
    front, upstream, reservation = bound_socket(), bound_socket(), bound_socket()
    server_address = reservation.getsockname()
    reservation.close()  # Godot must bind its port; the relay ports remain reserved.
    children, files = [], []
    queue, client_address = [], None
    statistics = {direction: [] for direction in ("client_to_server", "server_to_client")}
    enqueued = {direction: 0 for direction in statistics}
    serial = 0
    error = None
    started = time.monotonic()
    run_id = secrets.token_hex(6)
    # A monotonic clock makes scheduled dwell independent of wall-clock changes.
    # Source: https://docs.python.org/3.14/library/time.html#time.monotonic
    def launch(role, port):
        output = directory / f"{role}.stdout.log"
        stream = output.open("w", encoding="utf-8")
        files.append(stream)
        engine_log = ROOT / ".godot" / "committed-replay" / "native" / run_id / f"{role}.log"
        engine_log.parent.mkdir(parents=True, exist_ok=True)
        command = [args.godot, "--headless", "--path", str(ROOT), "--log-file", str(engine_log), SCENE,
                   "--", f"--harness-role={role}", f"--harness-port={port}", f"--move={move}", f"--contact={contact}"]
        flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
        children.append(subprocess.Popen(command, cwd=ROOT, stdout=stream, stderr=subprocess.STDOUT, creationflags=flags))
        return output
    try:
        server_log = launch("server", server_address[1])
        deadline = time.monotonic() + 10
        while "[committed-replay] READY server" not in server_log.read_text(encoding="utf-8", errors="replace"):
            if children[0].poll() is not None or time.monotonic() > deadline:
                raise RuntimeError("server failed to become ready")
            time.sleep(0.02)
        client_log = launch("client", front.getsockname()[1])
        deadline = time.monotonic() + 30
        while any(child.poll() is None for child in children):
            now = time.monotonic()
            if now > deadline:
                raise RuntimeError("trial exceeded 30-second deadline")
            readable, _, _ = select.select([front, upstream], [], [], 0.001)
            for sock in readable:
                try:
                    payload, address = sock.recvfrom(65535)
                except ConnectionResetError:
                    # Windows surfaces UDP port-unreachable after a peer shuts down.
                    # Keep peer exit/PASS and bounded-deadline checks as the verdict;
                    # an early failed peer still fails those checks.
                    # Source: https://docs.python.org/3.14/library/exceptions.html#ConnectionResetError
                    # UDP meaning: https://learn.microsoft.com/en-us/windows/win32/api/winsock2/nf-winsock2-recvfrom
                    continue
                if sock is front:
                    client_address = address
                    target_socket, target_address, direction = upstream, server_address, "client_to_server"
                else:
                    if client_address is None:
                        raise RuntimeError("upstream traffic before client address")
                    target_socket, target_address, direction = front, client_address, "server_to_client"
                enqueued[direction] += 1
                serial += 1
                queued_at = time.monotonic()
                heapq.heappush(queue, (queued_at + delay / 1000, serial, queued_at, target_socket, target_address, direction, payload))
            now = time.monotonic()
            while queue and queue[0][0] <= now:
                _, _, queued_at, target_socket, target_address, direction, payload = heapq.heappop(queue)
                # sendto preserves each ENet datagram; no packet content is rewritten.
                # Source: https://docs.python.org/3.14/library/socket.html#socket.socket.sendto
                target_socket.sendto(payload, target_address)
                statistics[direction].append((time.monotonic() - queued_at) * 1000)
            if any(child.poll() not in (None, 0) for child in children):
                raise RuntimeError("Godot trial failed; inspect retained logs")
        for child, log in zip(children, (server_log, client_log)):
            if child.returncode != 0 or "[harness] PASS committed-replay" not in log.read_text(encoding="utf-8", errors="replace"):
                raise RuntimeError("missing exit-zero/PASS evidence")
        if not all(statistics.values()):
            raise RuntimeError("relay did not forward traffic in both directions")
    except Exception as exception:
        error = str(exception)
    finally:
        for child in children:
            if child.poll() is None:
                child.kill()
            child.wait(timeout=5)
        for stream in files:
            stream.close()
        front.close()
        upstream.close()
    rows = []
    server_rows = []
    client_path = directory / "client.stdout.log"
    if client_path.exists():
        for line in client_path.read_text(encoding="utf-8", errors="replace").splitlines():
            if line.startswith("REPLAY_ROW "):
                rows.append(json.loads(line[len("REPLAY_ROW "):]))
    server_path = directory / "server.stdout.log"
    if server_path.exists():
        for line in server_path.read_text(encoding="utf-8", errors="replace").splitlines():
            if line.startswith("SERVER_ROW "):
                server_rows.append(json.loads(line[len("SERVER_ROW "):]))
    (directory / "rows.jsonl").write_text("".join(json.dumps(row) + "\n" for row in rows), encoding="utf-8")
    (directory / "server-rows.jsonl").write_text("".join(json.dumps(row) + "\n" for row in server_rows), encoding="utf-8")
    relay = {}
    for direction, values in statistics.items():
        relay[direction] = {"enqueued": enqueued[direction], "forwarded": len(values),
                            "discarded_at_teardown": enqueued[direction] - len(values),
                            "min_ms": min(values) if values else None,
                            "mean_ms": sum(values) / len(values) if values else None,
                            "max_ms": max(values) if values else None}
    phases = {}
    for phase in ("Inactive", "Startup", "Active", "Recovery"):
        selected = [row for row in rows if row["server_phase"] == phase]
        phases[phase] = {"rows": len(selected), "nonempty_replays": sum(row["replay_count"] > 0 for row in selected),
                         "mean_replay_count": sum(row["replay_count"] for row in selected) / len(selected) if selected else 0,
                         "max_correction": max((row["correction"] for row in selected), default=0),
                         "max_oracle_position_error": max((row["oracle_position_error"] for row in selected), default=0)}
    summary = {"move": move, "contact": contact, "delay_ms": delay, "repeat": index, "passed": error is None,
               "error": error, "elapsed_seconds": time.monotonic() - started, "rows": len(rows), "relay": relay, "phases": phases,
               "server_opponent_hits": sum(row["opponent_hits"] for row in server_rows),
               "server_committed_opponent_hits": sum(row["opponent_hits"] for row in server_rows if row["first_phase"] != "Inactive"),
               "server_minimum_separation": min((row["separation"] for row in server_rows), default=None),
               "native_logs": str(ROOT / ".godot" / "committed-replay" / "native" / run_id)}
    (directory / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(json.dumps(summary), flush=True)
    return summary


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--godot", required=True)
    parser.add_argument("--move", choices=("neutral", "crossover", "gather"), default="neutral")
    parser.add_argument("--contact", choices=("contact", "no-contact"), default="contact")
    parser.add_argument("--delay-ms", type=int, choices=(0, 30), default=0)
    parser.add_argument("--repeat", type=int, default=1)
    parser.add_argument("--all", action="store_true")
    parser.add_argument("--output", type=Path, default=ROOT / ".godot" / "committed-replay" / "results")
    args = parser.parse_args()
    if args.repeat < 1:
        parser.error("--repeat must be positive")
    conditions = [(m, c, d) for m in ("neutral", "crossover", "gather") for c in ("contact", "no-contact") for d in (0, 30)] if args.all else [(args.move, args.contact, args.delay_ms)]
    summaries = [trial(args, m, c, d, i) for m, c, d in conditions for i in range(args.repeat)]
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "summary.json").write_text(json.dumps(summaries, indent=2), encoding="utf-8")
    return 0 if all(summary["passed"] for summary in summaries) else 1


if __name__ == "__main__":
    raise SystemExit(main())
