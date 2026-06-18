# ADR-0007 — Dedicated-server topology & LAN discovery protocol

- **Status:** Accepted
- **Date:** 2026-06-17
- **Superseded-by:** —

---

## Context

ADR-0005 commits the community model to self-hosted dedicated servers + a server
browser, naming two artifacts to build in Milestone 6: a headless dedicated-server
export, and LAN broadcast discovery. ADR-0005 settles the *why*; it does not settle
the *topology mechanics* or the *discovery wire format*. This ADR does.

Milestones 1b–5 were all built and proven on a **listen-server** topology: one
machine is simultaneously the authoritative server *and* player 1 (the host), the
other is a client + player 2. That assumption is wired into the code in two places
that a headless server — which has **no local player** — silently violates:

1. `NetworkManager.HostGame` unconditionally spawns a player node for peer 1 and
   emits the UI-only `GameReady` signal. A headless server has no peer-1 player and
   no lobby UI.
2. `BallController._Ready` hardcodes the tipoff holder to peer 1
   (`new BallStateMachine(initialHolderPeerId: 1)`). On a dedicated server peer 1 is
   the server with no player node, so the ball's holder resolves to a non-existent
   node and the ball sits at the world origin forever.

A third, *non*-issue worth recording so it is not "fixed" by mistake:
`PlayerController.TickServerOwnPlayer` (the `IsServer && IsLocalPlayer` role) is
**dead, not broken** on a headless host. The server's unique id is always 1, no
client node is ever named `"1"`, so `_PhysicsProcess` routes every player node to
`TickServerRemotePlayer` — which is already the correct authoritative path (driven by
`SubmitInput` / `RequestBeginMove` / `RequestFeint` RPCs from the owning client). The
fix is to *not create* a peer-1 player, not to touch the tick role.

Two topology options were considered for how dedicated relates to the existing path:

- **Supersede** — delete the listen-server / host-is-player-1 path entirely. Cleaner
  long-term, but invalidates every EDITOR_TASKS verification flow (which runs two
  editor instances, one hosting) and has a large blast radius across proven code.
- **Coexist** — keep listen-server as the default in-editor path; add dedicated as an
  *additive* entry path selected at launch by a command-line flag. Lowest risk;
  nothing already proven is disturbed.

## Decision

**1. Topology — coexist.** The headless dedicated server is an additive entry path.
`NetworkManager` gains `StartDedicatedServer(int port)` alongside the unchanged
`HostGame` / `JoinGame`. The dedicated path creates the ENet server, assigns the
multiplayer peer, subscribes peer lifecycle, and **does not** spawn a peer-1 player or
emit `GameReady` (safe: `GameReady` is purely the lobby's hide-self cue; gameplay
ticking depends on the multiplayer peer + spawned player nodes, not on that signal).
Entry is selected by a launch flag, not a build difference — the same export can run
as a client (default) or a dedicated server (`--dedicated`).

**2. No-local-player is the authoritative-host shape.** On the dedicated server,
`Multiplayer.GetUniqueId() == 1` and no player node is named `"1"`; all player nodes
are remote clients driven through the existing server-authoritative RPC path. The
server owns and broadcasts all transform and ball truth exactly as before (ADR-0002);
it simply has no locally-controlled player to read hardware for.

**3. Tipoff holder is self-assigned by the server, topology-agnostic.** The ball's
initial holder is no longer hardcoded to peer 1. `BallController` starts with no
holder (`HolderPeerId == 0`, an already-representable "loose" value — `Shoot()`
already clears to 0). On the **server only**, while `HolderPeerId == 0` and at least
one player node exists under `Players`, the ball self-assigns the holder to the first
present player node and broadcasts it via the existing `ReceiveState` RPC. This keeps
possession logic inside `BallController` (it already reads `Players`) rather than
coupling `NetworkManager` to the ball, and it is correct for both topologies:
listen-server's first present node is the host (peer 1), dedicated's is the first
client. Determinism across peers is moot — only the server decides, and it broadcasts
the result; clients never compute the holder independently.

**4. Discovery protocol — UDP LAN broadcast.**
- **Port:** 7778 for discovery (game default is 7777), kept separate so discovery
  traffic and gameplay traffic never contend.
- **Beacon packet** (server → broadcast, ~1 Hz):
  `magic "HOOP" (4 bytes) | version (1) | gamePort (uint16) | curPlayers (1) |
  maxPlayers (1) | nameLen (1) | name (UTF-8, nameLen bytes)`.
  Fixed header, bounded variable name; everything a browser row needs without a reply
  round-trip.
- **Client** binds a UDP listener on `0.0.0.0:7778`, keys discovered servers by
  `(sourceIP, gamePort)`, refreshes `lastSeen` on each beacon (dedupe), and expires an
  entry after ~3 s of silence (≈3 missed beacons). A chosen entry's `IP:gamePort` is
  handed to the existing `NetworkManager.JoinGame` — discovery adds no new connection
  path.
- The pure byte format (`ServerBeacon` encode/decode) and the list bookkeeping
  (`ServerList` dedupe/expiry, with an injected clock) are engine-free and unit-tested;
  only the `PacketPeerUdp` socket wrappers touch Godot and are excluded from tests by
  design, mirroring the existing `BallController` test seam.

## Consequences

**Easier:**
- The authoritative simulation runs unchanged headlessly — server-authoritative
  netcode (ADR-0002) was already "the server owns truth"; removing its local player
  removes code, not capability.
- Listen-server stays fully intact, so every existing EDITOR_TASKS verification flow
  keeps working; M6 adds paths rather than rewriting proven ones.
- Discovery is decoupled from gameplay transport and from any central service
  (ADR-0005) — pure LAN, no infrastructure.

**Harder / accepted tradeoffs:**
- Two server-entry paths (`HostGame`, `StartDedicatedServer`) must be kept coherent as
  the netcode evolves; the shared `SpawnPlayer` / peer-lifecycle core limits the
  divergence. This coexistence is **transitional, not a committed shipping feature**:
  the listen-server path is retained because the EDITOR_TASKS verification flow
  currently depends on it (two editor instances, one hosting). Once the dedicated
  server + browser are proven — ideally cross-machine — a future milestone is free to
  delete `HostGame` and migrate verification onto the dedicated path, collapsing back
  to a single topology without re-litigating this decision. Nothing new should be built
  to *depend* on listen-server being permanent.
- The tipoff-holder change touches `BallController` initialization — necessary
  possession plumbing, explicitly *not* a scoring change (M6 makes no scoring changes).
- **Single-host discovery limits** (acceptance-relevant): a UDP listener binding 7778
  means two *client* instances on one machine contend for the receive port, and
  limited broadcast (`255.255.255.255`) does not reliably loop back on a single host.
  Therefore true cross-host LAN discovery is **not provable on one machine** and is
  deferred until a second LAN host is available; the single-machine acceptance proves
  pure-logic tests, headless boot with zero players, join-by-IP, and at most a
  single-browser-client display. (See the M6 acceptance procedure.)
- `MaxClients = 2` already means "2 remote clients," which is the correct cap for a
  dedicated 1v1; the real hard 1v1 cap on listen-server is the `MultiplayerSpawner`
  `spawn_limit = 2` (host + 1 client). No value change is required for M6.

## Open engine-API items (to confirm against live Godot docs per ADR-0001, Stage 2)
- Exact command-line argument retrieval for the `--dedicated` flag
  (`OS.GetCmdlineArgs()` vs `OS.GetCmdlineUserArgs()` and the `--` separator
  convention).
- `PacketPeerUdp` broadcast setup (`SetBroadcastEnabled`, `SetDestAddress`, `Bind`)
  and any address-reuse options for the listener.
These are flagged inline in the implementing code and are not yet verified at the time
this ADR is drafted.
