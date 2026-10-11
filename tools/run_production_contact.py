"""Three real ENet peers, with independently measured UDP relays (#370)."""
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
SCENE = "res://tests/integration/NetProductionContactTest.tscn"


def bound_socket():
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind(("127.0.0.1", 0))
    sock.setblocking(False)
    return sock


def trial(args, move, contact, delay, index):
    directory = args.output / f"{move}-{contact}-{delay}ms-{index}"
    directory.mkdir(parents=True, exist_ok=True)
    reservation = bound_socket()
    server_address = reservation.getsockname()
    reservation.close()
    links = {role: {"front": bound_socket(), "upstream": bound_socket(), "client": None}
             for role in ("driver", "defender")}
    sockets = {sock: (role, direction) for role, link in links.items()
               for direction, sock in (("client_to_server", link["front"]), ("server_to_client", link["upstream"]))}
    dwell = {(role, direction): [] for role, direction in sockets.values()}
    enqueued = dict.fromkeys(dwell, 0)
    children, streams, queue, logs = [], [], [], {}
    serial = 0
    error = None
    run_id = secrets.token_hex(6)
    # Source: https://docs.python.org/3.14/library/time.html#time.monotonic
    started = time.monotonic()

    def launch(role, port):
        log = directory / f"{role}.stdout.log"
        stream = log.open("w", encoding="utf-8")
        streams.append(stream)
        native = ROOT / ".godot" / "production-contact" / "native" / run_id / f"{role}.log"
        native.parent.mkdir(parents=True, exist_ok=True)
        command = [args.godot, "--headless", "--path", str(ROOT), "--log-file", str(native), SCENE,
                   "--", f"--harness-role={role}", f"--harness-port={port}", f"--move={move}", f"--contact={contact}"]
        flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
        # Source: https://docs.python.org/3.14/library/subprocess.html#subprocess.Popen
        children.append(subprocess.Popen(command, cwd=ROOT, stdout=stream, stderr=subprocess.STDOUT, creationflags=flags))
        logs[role] = log

    try:
        launch("server", server_address[1])
        deadline = time.monotonic() + 10
        while "[production-contact] READY server" not in logs["server"].read_text(encoding="utf-8", errors="replace"):
            if children[0].poll() is not None or time.monotonic() > deadline:
                raise RuntimeError("server readiness exceeded 10 seconds")
            time.sleep(.02)
        for role, link in links.items():
            launch(role, link["front"].getsockname()[1])
        deadline = time.monotonic() + 35
        while any(child.poll() is None for child in children):
            if time.monotonic() > deadline:
                raise RuntimeError("trial exceeded 35 seconds")
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
                # Source: https://docs.python.org/3.14/library/socket.html#socket.socket.sendto
                target.sendto(payload, destination)
                dwell[role, direction].append((time.monotonic() - queued) * 1000)
            if any(child.poll() not in (None, 0) for child in children):
                raise RuntimeError("Godot peer failed; inspect retained logs")
        for child, role in zip(children, ("server", "driver", "defender")):
            if child.returncode != 0 or f"[harness] PASS production-contact {role}:" not in logs[role].read_text(encoding="utf-8", errors="replace"):
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
    rows, observations = {}, {}
    for role, log in logs.items():
        rows[role] = []
        for line in log.read_text(encoding="utf-8", errors="replace").splitlines():
            if line.startswith("CONTACT_ROW "):
                rows[role].append(json.loads(line[len("CONTACT_ROW "):]))
            elif line.startswith("CONTACT_SUMMARY "):
                observations[role] = json.loads(line[len("CONTACT_SUMMARY "):])
        (directory / f"{role}-rows.jsonl").write_text("".join(json.dumps(row) + "\n" for row in rows[role]), encoding="utf-8")
    relay = {}
    for (role, direction), values in dwell.items():
        relay.setdefault(role, {})[direction] = {
            "enqueued": enqueued[role, direction], "forwarded": len(values),
            "discarded_at_teardown": enqueued[role, direction] - len(values),
            "min_ms": min(values) if values else None,
            "mean_ms": sum(values) / len(values) if values else None,
            "max_ms": max(values) if values else None}
    summary = {"move": move, "contact": contact, "requested_delay_ms": delay, "repeat": index,
               "passed": error is None, "error": error, "relay": relay, "observations": observations,
               "elapsed_seconds": time.monotonic() - started,
               "native_logs": str(ROOT / ".godot" / "production-contact" / "native" / run_id)}
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
    parser.add_argument("--output", type=Path, default=ROOT / ".godot" / "production-contact" / "results")
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
