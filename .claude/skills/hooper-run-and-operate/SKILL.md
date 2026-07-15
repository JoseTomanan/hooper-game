---
name: hooper-run-and-operate
description: How to actually RUN hooper-game — windowed/headless single instance, the dedicated server (--dedicated/--port flags), LAN discovery (UDP 7778, "HOOP" beacon), local dual-instance dev runs (the run-net-*.sh scripts, ports 23456-59), and where every runtime artifact lands (.godot/, build/server/, harness PASS/FAIL lines). Load this when asked to launch/run/host/join the game, start a dedicated server, debug a dual-instance script, explain DedicatedServerBootstrap/NetworkManager/DiscoveryBroadcaster topology choices, or figure out what a launch command's flags mean.
---

# hooper-run-and-operate

Operating manual for **running** hooper-game — not building it, not the test
matrix (see "When NOT to use this"). Everything here was verified against the
repo at **2026-07-12**; re-verify anything volatile with the commands at the
bottom.

Two facts govern every command in this skill:
- The repo path contains a space: `C:\Users\The King\Documents\GitHub\hooper-game`
  (or wherever the clone lives — assume a space is possible).
- The project filename contains a space: `HOOPER GAME.csproj` (and the local
  exported binary's product name is `PROJECT.exe`, not `HOOPER GAME.exe` — §5).

**Always double-quote both** in any command you write.

---

## 1. Running the game locally (windowed)

The main scene is `scenes/Main.tscn`, wired into `project.godot` **by uid**,
not by path:

```
run/main_scene="uid://c047mrir711mo"
```

(If you ever need to confirm this still resolves to `Main.tscn`, check the
scene's own `uid` on its first line — don't assume from the string.)

`Main.tscn` is shared by **all three roles** — client, listen-server host, and
headless dedicated server. Nothing in the scene picks a role; the launch
arguments do (§2).

### From the editor
Open the project in the Godot editor and press **F5**. For a two-player local
test, use **Debug → Run Multiple Instances → 2** — the standard dev loop for
anything needing two peers (all of EDITOR_TASKS.md's per-milestone dual-window
verifications use this flow: window 1 clicks **Host**, window 2 clicks
**Join** with the pre-filled `127.0.0.1:7777`).

### From a terminal (windowed, single instance)
```
& "<path-to>\Godot_v4.6.3-stable_mono_win64_console.exe" --path "C:\Users\The King\Documents\GitHub\hooper-game"
```
Use the `_console.exe` variant for terminal stdout — the sibling
`Godot_v4.6.3-stable_mono_win64.exe` is windowed-only and silent on the
console. Neither is on PATH by default; see §6 "Getting a Godot binary".

### Headless (no window, still runs Main.tscn as a live sim)
```
& <godot_console.exe> --headless --path "C:\Users\The King\Documents\GitHub\hooper-game"
```
`--headless` is a Godot engine flag that expands to
`--display-driver headless --audio-driver Dummy`. This boots the full scene
tree with no display or audio device. Note: this bare invocation just runs
`Main.tscn` headless — it does not self-exit and asserts nothing. For
automated pass/fail you run a `tests/integration/*.tscn` scene instead (that
runbook lives in `hooper-verification-and-qa`).

---

## 2. Dedicated server (ADR-0007)

### 2.1 The flags — verified against `scripts/Networking/DedicatedServerArgs.cs`

`DedicatedServerArgs` is a pure static class (no Godot Node — unit-testable)
parsing the launch args. It defines exactly **two** flags:

| Flag | Constant | Behavior |
|---|---|---|
| `--dedicated` | `DedicatedServerArgs.DedicatedFlag` | Presence anywhere in the arg list selects the headless dedicated-server path. `IsDedicated()` scans for an exact-match token. |
| `--port 7777` or `--port=7777` | `DedicatedServerArgs.PortFlag` | Overrides the listen port. `ParsePort()` accepts the joined `--port=N` form or the two-token `--port N` form; first occurrence wins. Falls back to the caller-supplied default (`NetworkManager.DefaultPort` = **7777**) if the flag is absent, has no value, or the value is not a valid port in 1..65535. |

There is no `--server` flag in this repo (Godot's tutorial uses that name; we
use `--dedicated`).

### 2.2 The launch line, and why it's shaped this way

```
"HOOPER GAME.exe" --headless -- --dedicated --port 7777
```

(Name caveat: a *local* export lands as `build/server/PROJECT.exe`, not
`HOOPER GAME.exe` — see §5. The flag anatomy below is identical either way.)

- `--headless` is a Godot **engine** flag: it must come **before** the `--`
  separator. No window, no audio device — the authoritative sim runs blind.
- `--dedicated` and `--port` are **our user args**, placed **after** a
  standalone `--`. Godot reserves everything after `--` for the game and never
  interprets it, so our flags can never collide with a current or future
  engine flag.
- `DedicatedServerBootstrap._Ready()` reads BOTH `OS.GetCmdlineUserArgs()`
  (the documented home for post-`--` args) AND `OS.GetCmdlineArgs()`, merged
  user-args-first — because Godot's own dedicated-server tutorial shows flags
  read *without* the `--` separator, the bootstrap deliberately accepts either
  invocation style. So the looser `--headless --dedicated --port 7777` (no
  `--`) also works, but prefer the canonical form above.

Expected log lines on a successful headless boot (from
`DedicatedServerBootstrap` and `NetworkManager`):
```
[DedicatedServerBootstrap] --dedicated detected; starting headless server on port 7777
[NetworkManager] Server up on port 7777
[NetworkManager] Dedicated server running headless; awaiting clients.
```

To run the *repo checkout* (not an export) as a dedicated server:
```
& <godot_console.exe> --headless --path "C:\Users\The King\Documents\GitHub\hooper-game" -- --dedicated --port 7777
```

### 2.3 `DedicatedServerBootstrap` and the CallDeferred one-frame-late start

`DedicatedServerBootstrap` (a Node in Main.tscn with `NetworkManager` and
`Lobby` exports) is what distinguishes the dedicated role: if `--dedicated` is
present it `QueueFree`s the Lobby overlay (not just hides it — so it stops
processing entirely; a headless server has no one to show it to) and starts
the server. Crucially, `_Ready()` does **not** call
`NetworkManager.StartDedicatedServer(port)` directly. It does:

```csharp
CallDeferred(MethodName.StartServerDeferred, port);
```

**Why one frame late:** `_Ready()` order across sibling nodes is not a
contract worth depending on for transport bringup. Deferring guarantees the
entire Main scene tree — NetworkManager's exports, the `MultiplayerSpawner`
sibling — is fully ready before the ENet server binds and starts accepting
peers. If `NetworkManager` is unassigned, `_Ready()` logs
`[DedicatedServerBootstrap] --dedicated given but NetworkManager is not
assigned` and returns — no server, no crash.

On a normal launch (no `--dedicated`) the bootstrap does nothing and the
Lobby's Host/Join flow runs unchanged.

### 2.4 Three topologies — which to use when

`NetworkManager` has two server entry points sharing one private bringup core
(`StartServer`), plus the client entry:

| Topology | Entry point | Local player? | When it's the right choice |
|---|---|---|---|
| **Listen-server (host)** | `HostGame(port)` | **Yes** — calls `SpawnPlayer(1)` immediately after the server binds; the host process IS player "1" and emits `GameReady` so its own Lobby hides | Normal Host/Join play. **Required for remote-display proofs**: any test where the server side must *own and drive a player* (e.g. `run-net-behindtheback-sweep.sh` uses `HostGame` so the server owns node `"1"`, the ball holder, and the client proves the broadcast renders remotely). |
| **Headless dedicated server** | `StartDedicatedServer(port)` | **No.** It skips `SpawnPlayer(1)` (peer 1 is the server, which has no local player) and skips `GameReady` (no Lobby exists to hide). Player nodes are created only for connecting clients via `OnPeerConnected`. Since no node is named `"1"`, PlayerController's "server-own-player" tick role is simply never reached. | Production/LAN hosting where the server box is not a player. Also `run-net-node-replication.sh`, which specifically proves this shipped path. |
| **Client** | `JoinGame(ip, port)` | The client's node is spawned server-side and replicated to it via `MultiplayerSpawner` | Joining either of the above. |

Both server paths converge on the same `ENetMultiplayerPeer.CreateServer`
call and both emit the `ServerStarted(port)` signal — which is what activates
LAN discovery (§3), so listen-server host games are discoverable too, same
code path, for free.

Other facts worth knowing when operating a server:
- `MaxClients` = 2 at the ENet layer; the real 1v1 ceiling is the
  `MultiplayerSpawner`'s `spawn_limit = 2` in Main.tscn.
  `NetworkManager.MaxPlayersPerMatch` (= 2) is what the discovery beacon
  reports as max players.
- A double `HostGame`/`JoinGame`/`StartServer` call is guarded (`_started`)
  and logs an error rather than stacking handlers — one game per process
  launch.
- Spawn positions: peer 1 gets `HostSpawn` (0, 0, 6 — offense/ballhandler),
  every joining client gets `ClientSpawn` (0, 0, 3 — defender), per ADR-0008's
  half-court stack.

### 2.5 Tipoff self-assignment

The ball's tipoff self-assigns to the **first player node** found under the
spawn root (`BallController`'s tipoff logic). This works unmodified for both
topologies: on a listen-server, player "1" already exists at bringup; on a
headless dedicated server, the first *connecting client's* node becomes the
tipoff holder. Operationally: on a dedicated server the ball waits for a
client — it must NOT sit frozen at the origin once a real client joins (that
symptom means tipoff never assigned; see EDITOR_TASKS.md's M6a step 5).

---

## 3. LAN discovery (ADR-0007)

Same pure-class / engine-glue split as everywhere in this repo:

| Piece | Pure class (unit-tested) | Engine node (untested by design — touches sockets/clock) |
|---|---|---|
| Sender | `Hooper.Networking.ServerBeacon` | `DiscoveryBroadcaster` |
| Receiver | `Hooper.Networking.ServerList` / `ServerListEntry` | `DiscoveryListener` (owned/driven by `ServerBrowser`) |

### Wire format (`ServerBeacon`, protocol version 1)
UDP payload, fixed 10-byte header + bounded name:

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | magic `"HOOP"` (ASCII) |
| 4 | 1 | protocol version (`ServerBeacon.ProtocolVersion` = 1; decoders reject others) |
| 5 | 2 | game port, **big-endian** uint16 (hand-written bytes, not BitConverter — the spec, not the CPU, defines endianness) |
| 7 | 1 | current players |
| 8 | 1 | max players |
| 9 | 1 | name length in bytes (0-255; encoder clamps over-long names) |
| 10 | N | server name, UTF-8 |

`TryDecode` returns false (never throws) for wrong magic, unknown version,
too-short data, or a declared name length that doesn't **exactly** match the
buffer — strict by design, since the listener receives arbitrary UDP traffic
on that port. The sender's IP is deliberately NOT in the packet: the receiver
reads it from `PacketPeerUDP.GetPacketIP()` (a server can't be trusted to
self-report its address; the transport already knows it). The **game port IS
in the packet** because the discovery port differs from the port a client
must dial to join.

### Ports
- **Discovery port: `7778`** — `DiscoveryBroadcaster.DiscoveryPort` and
  `DiscoveryListener.DiscoveryPort` exports, both default 7778. Must match.
- **Game port** (default 7777) rides inside the beacon payload.

### Broadcaster (server side)
`DiscoveryBroadcaster` activates on `NetworkManager.ServerStarted` — both
server topologies fire it. Every `BroadcastInterval` seconds (default
**1.0 s**) it sends a fresh beacon to the limited broadcast address
`255.255.255.255:7778`; current players = `Players.GetChildCount()` at send
time. **Known limitation (documented in the source):** the limited broadcast
does not reliably loop back on a single host, so single-machine discovery
cannot be fully proven — a server may or may not appear in a browser on the
same box, OS-dependent.

### Listener + server browser (client side)
`DiscoveryListener.StartListening()` binds port 7778 **on demand** (not in
`_Ready`) — only while the Server Browser UI is open — because the bind is
exclusive: **two browser clients on one machine contend for 7778.** Use one
browser client per machine when testing locally. Received beacons feed the
pure `ServerList`, keyed by `(ip, gamePort)` (so one machine can host two
servers on different ports and both list), and entries expire after
`TimeoutSeconds` (default **3.0 s** ≈ 3 missed beacons at the 1 Hz broadcast
rate) without a refresh. The browser flow: run a client → discovered servers
appear as rows in the `ServerBrowser` ItemList → double-click (or select +
Enter) a row → `JoinGame(ip, gamePort)` → browser hides on success, same as
the Lobby.

### Status as of 2026-07-12
Wire format and bookkeeping are pure and unit-tested; the whole flow is
code-done. **Cross-machine operation is NOT human-proven**: issue **#32**
(M6a's editor-verify issue) is still open — its remaining bar is a server on
machine A appearing in the browser on machine B over a real LAN, which cannot
be proven on one box. Do not claim cross-machine discovery works; label it
code-done, verification open.

---

## 4. Local dual-instance runs (`tests/integration/run-net-*.sh`)

Bash orchestrators — run via **Git Bash** on Windows (no PowerShell
equivalent exists) — that launch a real two-process server+client pair of one
Godot scene and use **only the client's exit code** as the verdict. This is
the capability a single headless process structurally cannot provide: a real
ENet handshake, a real broadcast, the real `NetworkManager` bringup.

| Script | Scene | Default port | What it proves | Server topology |
|---|---|---|---|---|
| `run-net-handshake.sh` | `NetHandshakeTest.tscn` | 23456 | Two real processes complete an ENet handshake | scene-internal harness roles |
| `run-net-state-sync.sh` | `NetStateSyncTest.tscn` | 23457 | Server broadcasts a strictly-increasing authoritative tick per physics frame; client receives a sustained in-order run | scene-internal harness roles |
| `run-net-node-replication.sh` | `NetNodeReplicationTest.tscn` | 23458 | The **shipped** `NetworkManager.StartDedicatedServer` boots; `JoinGame` connects; `SpawnPlayer` + `MultiplayerSpawner` replicate the client's node — client passes when it observes `Players/<its-peer-id>` | `StartDedicatedServer` (headless) |
| `run-net-behindtheback-sweep.sh` | `NetBehindTheBackSweepTest.tscn` | 23459 | A real BehindTheBack driven on the server's own player renders as a shielded behind-body sweep + `DisplayMoveId() == "behindtheback"` on a true remote client — the broadcast branch unreachable in any single process | `HostGame` (listen-server — the server must OWN player "1", the holder) |

**Ports 23456-23459 are claimed.** A new dual-instance test takes the next
port (23460) and follows the same env-override pattern.

### Common anatomy (identical in all four scripts)
```bash
GODOT="${1:-godot}"                        # $1 = godot binary; defaults to "godot" (CI has it on PATH)
PORT="${HARNESS_PORT:-<per-script default>}"
SERVER_BIND_WAIT="${SERVER_BIND_WAIT:-6}"  # seconds for Godot's .NET cold boot to bind the port
```
1. Server launched in the background:
   `"$GODOT" --headless --path . "$SCENE" -- --harness-role=server --harness-port="$PORT" &`
2. `trap cleanup EXIT` registered — kills the background server no matter how
   the script exits (pass, fail, Ctrl-C of the script itself).
3. `sleep "$SERVER_BIND_WAIT"` — a fixed sleep, not a poll. `CreateClient`
   fails fast if nothing is listening, so the server must genuinely be bound
   first; ~6 s covers the .NET cold start.
4. Liveness check (`kill -0`) — if the server died during the wait, fail fast.
5. Client runs in the **foreground**, same scene with
   `--harness-role=client`. **`CLIENT_RC` is the entire verdict**: 0 = PASS,
   anything else = FAIL. The server's own exit code is deliberately ignored
   (its self-exit races the trap's kill).

### Local invocation (Windows, Git Bash, from the repo root)
```bash
GODOT="/c/path/to/Godot_v4.6.3-stable_mono_win64_console.exe"
SERVER_BIND_WAIT=6 bash tests/integration/run-net-state-sync.sh "$GODOT"
```
Override `HARNESS_PORT` if a default port is stuck in use (e.g. an orphaned
server from an interrupted run):
```bash
HARNESS_PORT=23460 bash tests/integration/run-net-node-replication.sh "$GODOT"
```
If a run hangs or you hard-kill the terminal, check for an orphaned Godot
process still holding the port before rerunning — the trap only fires when
the *script itself* exits.

CI runs all four (each preceded by `chmod +x`) as the tail of the
`integration-test` job; see `hooper-build-and-env` for the CI pipeline and
`hooper-verification-and-qa` for the full scenario matrix.

---

## 5. Artifacts and outputs — what lands where

| Path | What it is | In git? |
|---|---|---|
| `.godot/` | Godot's generated cache/import dir; running headless touches only this | No (gitignored) |
| `.godot/mono/temp/bin/Debug/HOOPER GAME.dll` | The built game assembly (`dotnet build "HOOPER GAME.csproj" --configuration Debug` output) | No |
| `build/server/` | Local-only exported Windows dedicated-server build (~190 files) | **No** (gitignored) |
| `build/server/PROJECT.exe` | The exported executable. **The product name is `PROJECT`, not `HOOPER GAME`** — the preset's `export_path` is literally `build/server/PROJECT.exe`. §2.2's `"HOOPER GAME.exe"` line is the generic form from the code doc; the actual local artifact is `PROJECT.exe`. | No |
| `build/server/PROJECT.console.exe` | Console wrapper (preset has `debug/export_console_wrapper=1`) — run this variant to see stdout in a terminal | No |
| `build/server/PROJECT.pck` | Packed game data (preset has `embed_pck=false`, so the pck ships beside the exe) | No |
| `build/server/data_HOOPER GAME_windows_x86_64/` | Embedded .NET runtime + native deps the export needs alongside | No |
| `export_presets.cfg` | The export preset that produced all of the above | **No** (gitignored; §5.1) |

Both `export_presets.cfg` and `build/` are gitignored (verify:
`git check-ignore export_presets.cfg build`). **A fresh clone or CI runner
has neither.** CI never exports — it builds and runs the harness in place.

### Where harness PASS/FAIL lines appear
Single-instance headless scenes print a `[harness]`-prefixed line to stdout,
e.g.:
```
[harness] PASS — 30 fixed ticks, deterministic
```
then exit with the matching code. The dual-instance scripts log with their
own name as the prefix (`[run-net-state-sync] PASS: ...` /
`[run-net-...] FAIL: ... (rc=N)`); the client scene's own output also streams
through since the client runs in the foreground. The scripts never parse the
text — only exit codes decide.

### Exit-code contract (ADR-0016)
Every harness scene ends via `GetTree().Quit(exitCode)`:
- **0** = PASS
- **1** = FAIL (a scenario assertion did not hold)
- **anything else** = the harness itself crashed — CI treats it as failure
  regardless of what was printed

For the dual-instance scripts this contract applies to the **client** process.

### 5.1 Export process — recreating the preset (unreproducible-from-repo)

`export_presets.cfg` follows the common Godot convention of being gitignored,
so the preset exists **only on the machine that last exported** — label it
unreproducible-from-repo. As of 2026-07-12 the local machine's preset was
(verified by reading the file directly):

- Preset name/platform: **"Windows Desktop"**
- `export_path="build/server/PROJECT.exe"`
- `binary_format/architecture="x86_64"`
- `binary_format/embed_pck=false` (separate `.pck` beside the exe)
- `debug/export_console_wrapper=1` (produces `PROJECT.console.exe`)
- `dedicated_server=false` — the preset does NOT use Godot's
  dedicated-server resource-stripping option; a normal desktop export is
  sufficient because the `--headless` **runtime** flag is what removes the
  display/audio surface (per the Godot dedicated-servers doc cited in
  `DedicatedServerBootstrap.cs`)

To recreate on a new machine: **Project → Export… → Add… → Windows Desktop**,
set the values above, then **Export Project** to `build/server/`. Godot will
prompt to install export templates the first time. This is an editor
(human) step — export presets are created in the export dialog, which is not
text-authored today.

---

## 6. Getting a Godot binary

`godot` is **not on PATH** on a typical dev machine here. You need the exact
**Godot 4.6.3 .NET/Mono** build (must match the `Godot.NET.Sdk/4.6.3` +
GodotSharp 4.6.3 pin in the csproj):

- **Local**: download the ".NET" (mono) build of 4.6.3 from godotengine.org.
  Use the `_console.exe` variant for anything terminal-driven. Point an env
  var at it (`$GODOT` in Git Bash) so the `run-net-*.sh` `$1` argument stays
  a one-liner.
- **CI**: gets `godot` on PATH via `chickensoft-games/setup-godot@v2`
  (`version: 4.6.3`, `use-dotnet: true`) — which is why CI scripts call bare
  `godot` while local runs need an explicit path.

Sanity-check before trusting any run:
```
& <godot_console.exe> --version    # must print 4.6.3.stable.mono.official.<hash>
```
A version mismatch against the csproj pin is a real source of subtle
divergence. Full toolchain story: `hooper-build-and-env`.

---

## 7. The human-editor interface (EDITOR_TASKS.md)

`EDITOR_TASKS.md` (repo root) is the catalogue of steps only a human in the
Godot editor can do. Its scope has narrowed twice:
- **ADR-0011**: Claude authors `.tscn`/`.res`/`project.godot` by text-edit;
  the human keeps feel/tuning judgments and in-engine verification. One hard
  structural exclusion remains: **editor import-dialog settings** not already
  scriptable headlessly. (AnimationTree graph authoring, once excluded, went
  AFK after spike #87 proved text-authored trees load identically.)
- **ADR-0016**: state-checkable verification moved to the headless harness;
  what's left for the human is irreducible *feel* (batched into one pass per
  milestone, ADR-0015) plus not-yet-automated residue.

**Operating rule when you finish work needing in-editor verification:** an
agent cannot press F5 or judge feel. Write the editor steps into
`EDITOR_TASKS.md` as a new milestone-numbered section following the house
format (a "Build first" step — hammer icon — then numbered verify steps
mapped to acceptance criteria, then a symptom/cause troubleshooting table),
and tell the human which issue(s) the steps close. If the criterion is
actually state-checkable, it does not belong there — it belongs in a
`tests/integration/` harness scene (see `hooper-verification-and-qa`).

---

## When NOT to use this

- **Building the project** — dotnet/csproj/CI toolchain, compile surfaces,
  the two-project asymmetry, green-gate hook → **`hooper-build-and-env`**.
- **The test matrix** — which harness scenarios exist, adding a new
  integration test or seam, xUnit vs headless-scene choice, what counts as
  evidence → **`hooper-verification-and-qa`**.
- Netcode *theory* (why reconciliation/smoothing behaves as it does once
  these processes are running) → `hooper-netcode-reference`. Gameplay
  tunables and their defaults → `hooper-config-and-flags`. Merge/issue/ADR
  process → `hooper-change-control`.

---

## Provenance and maintenance

Authored 2026-07-12; reviewed 2026-07-15 (§2.2 now cross-references the
`PROJECT.exe` local-export name up front). Verified against, read in full:
- `scripts/Networking/DedicatedServerArgs.cs`, `DedicatedServerBootstrap.cs`,
  `NetworkManager.cs`, `ServerBeacon.cs`, `ServerList.cs`,
  `DiscoveryBroadcaster.cs`, `DiscoveryListener.cs`
- `tests/integration/run-net-handshake.sh`, `run-net-state-sync.sh`,
  `run-net-node-replication.sh`, `run-net-behindtheback-sweep.sh`
- `project.godot` (`run/main_scene` uid), the local (gitignored)
  `export_presets.cfg` and `build/server/` directory listing,
  EDITOR_TASKS.md's ADR-0011/0016 preamble and M6/M6b/M8 sections
- Discovery digests build-env.md / agent-infra.md (2026-07-12) were used as
  leads and spot-checked against the sources above.

Re-verify on drift:
- Flags: `grep -n "DedicatedFlag\|PortFlag" scripts/Networking/DedicatedServerArgs.cs`
- Ports/caps: `grep -n "DefaultPort\|MaxPlayersPerMatch\|MaxClients" scripts/Networking/NetworkManager.cs`
- Discovery defaults: `grep -n "DiscoveryPort\|BroadcastInterval\|TimeoutSeconds" scripts/Networking/Discovery*.cs`
- Dual-instance ports: `grep -n "HARNESS_PORT:-" tests/integration/run-net-*.sh`
- Gitignore status of export artifacts: `git check-ignore export_presets.cfg build`
- Main-scene uid: `grep main_scene project.godot`
- Issue #32 (cross-machine discovery verify) still open?: `gh issue view 32 --json state,title`
