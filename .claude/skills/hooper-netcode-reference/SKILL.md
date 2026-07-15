---
name: hooper-netcode-reference
description: Load this skill before touching anything in scripts/Player/PlayerController.cs, scripts/Ball/BallController.cs, scripts/Networking/, or any RPC/reconciliation/prediction code in the hooper-game repo. Teaches server-authoritative prediction, reconciliation, and determinism AS APPLIED HERE — not textbook netcode theory. Trigger on: "reconciliation", "prediction buffer", "RPC channel", "ReceiveState", "force-snap", "desync", "rubber-banding", "client-side prediction", "why does the ball/player jump/snap", "adding a new networked field", "discrete vs continuous state", or any task touching ADR-0002/0004/0012/0018.
---

# Hooper netcode reference

You are a competent C# engineer with **no prior netcode intuition** and **no
Godot experience**. This skill teaches you the domain theory this repo
actually runs on, through its real implementations — not a generic rollback-
netcode tutorial. Read it before writing or reviewing any code that touches
prediction, reconciliation, RPCs, or the tick loop.

Every class/line reference below was verified against the repo on
2026-07-12 at commit `3085ee1`. Re-verify with the commands in
**Provenance and maintenance** before trusting a specific line number.

---

## 1. Why server-authoritative + client prediction (ADR-0002)

**Term: authoritative.** One process's copy of the game state is legally
"true"; every other copy is a *guess* that gets corrected when it disagrees
with the authoritative copy.

Two competing models exist for real-time competitive games:

| Model | Topology | Who is "the server" |
|---|---|---|
| **Server-authoritative + client prediction** (chosen) | client↔server | One dedicated process, always |
| **Peer rollback / GGPO** (rejected) | peer↔peer | Each peer, for its own inputs |

**Term: rollback / GGPO.** A peer-to-peer netcode model (GGPO is the
best-known implementation, used in fighting games) where both peers simulate
ahead optimistically, and when a remote input arrives late, each peer
rewinds ("rolls back") its simulation to the tick that input belongs to and
re-simulates forward. It requires every peer to be able to rewind and replay
the whole game state.

Hooper is built for **self-hosted dedicated servers** (ADR-0005) — a
client↔server topology where one process must own truth. Rollback is a
2-peer model; bolting it onto a dedicated-server topology means the
dedicated server would *also* need to participate in rollback, for no
benefit — a dedicated-server game already gets rollback's real goal (no
single peer favoured) for free, by construction (ADR-0002).

**What "authoritative" means operationally** — the only-server-mutates list
(verified in `scripts/Ball/BallController.cs` and `scripts/Systems/`):

- Score (`GameManager.RegisterBasket`, server-guarded, `GameManager.cs:134`)
- The `IsCleared` (take-it-back) flip, OOB awards, steal/block outcomes
- The shot-scatter RNG draw itself (`_shotRng.NextDouble()`, only reached
  `if (IsServer)` — `BallController.cs:2583-2584`, seeded from the
  `ShotScatterSeed` export in `_Ready`, `BallController.cs:920`)

Everything else — movement, heading, committed-move phase progression, the
ball's flight arc — is **predicted identically** by every peer from the same
deterministic pure math (ADR-0004), and corrected only when a peer's guess
disagrees with what the server broadcasts.

Godot's `MultiplayerApi`/`MultiplayerSpawner`/RPCs are **transport only**
(ADR-0002, "Godot-specific note"). They move bytes and replicate node
*existence*. Prediction, reconciliation, and the tick loop are 100% hand-
written C# in this repo — Godot gives you none of it for free.

---

## 2. The tick model

**Term: tick.** One fixed-size step of the simulation — here, one call to
`_PhysicsProcess`, at a fixed rate set by Godot's
`Engine.PhysicsTicksPerSecond` (60 in this project). **Term: wall-clock
delta.** The variable, real-elapsed-time value Godot hands
`_PhysicsProcess(double delta)` — NOT used for ball/heading/committed-move
math here.

**The rule: `dt` is always `1.0f / Engine.PhysicsTicksPerSecond`, never the
wall-clock `delta` parameter**, anywhere ball or heading math runs:

```csharp
// BallController.cs:1049
float dt = 1.0f / Engine.PhysicsTicksPerSecond;

// PlayerController.cs:1606, inside ReconcileFromServer's replay loop
double fixedDelta = 1.0 / Engine.PhysicsTicksPerSecond;
foreach (Vector2 input in _buffer.Replay())
    Move(input, fixedDelta);
```

**Why wall-clock delta breaks determinism and reconciliation replay:** the
server simulated each historical tick at exactly `1/60 s`. If you replay a
buffered input with today's *actual* frame delta (which varies with frame
rate, hitches, OS scheduling) instead of the fixed `1/60 s` the server used,
your replayed position no longer matches what the server computed for that
tick — reconciliation silently reintroduces the very divergence it exists to
close. The same reasoning applies to plain prediction: server and client
must integrate with the same timestep or they disagree even with identical
inputs.

**Durations are tick counts, never wall-clock timers.** `MoveFrameData`
records Startup/Active/Recovery as **integer physics ticks**
(`scripts/Input/MoveFrameData.cs:8`), not milliseconds. The crossover
ball-transit sweep converts a design-facing seconds value to a tick count
exactly once, at setup: `Mathf.RoundToInt(CrossoverSweepDuration *
Engine.PhysicsTicksPerSecond)` (`BallController.cs:1895`) — everything
downstream counts ticks. If you add a new timed behavior, follow this
pattern: author the tunable in seconds if that's more human-legible,
convert to a tick count once, then count ticks. Never store or compare a
`DateTime` / `Time.GetTicksMsec()` value as authoritative timing.

---

## 3. Determinism requirements for the ball (ADR-0004)

**Term: determinism.** Given the same inputs, the same code always produces
the *exact same* output — same result on the server, on your client, on your
opponent's client, on any OS, forever. This is what lets the server and
every client predict the ball's flight identically without constant
correction.

Why not just use Godot's physics engine (the project runs Jolt for player
collisions)? Physics engines are built for visual plausibility, not
bit-identical replay: their constraint solvers' iteration order and
floating-point summation can legally differ by platform, thread count, or
minor version — bit-identical results are simply not a promise they make
(ADR-0004). Any divergence forces a correction; frequent corrections make
the ball feel jittery. So the ball's authoritative state machine
(`Held/Dribbling/InFlight/Loose`, `scripts/Ball/BallStateMachine.cs`) and
all its math are **hand-authored, pure, and engine-free**:

- **Pure `MathF` float math only** — no `Random`, no `DateTime`, no
  `Input.*`, no `Node` inheritance in the math classes (`ShotArc`,
  `RimBackboard`, `FloorBounce`, `DribbleCycle`, `HeadingMath`,
  `CommittedMoveMachine`, `Scoreboard`, `DefensiveResolution`, …). This is
  also what makes them unit-testable headless, with zero Godot instance
  required.
- **Injected `dt`** — every pure class takes `dt` as a parameter
  (e.g. `ShotArc.Step(dt)`; `ShotArc.cs:165` documents the expected
  `(float)(1.0 / Engine.PhysicsTicksPerSecond)`); nothing in the pure layer
  reads engine singletons itself. The *caller* (the node) computes `dt`
  once and passes it down.
- **Server-seeded, injected RNG** — `_shotRng = new Random(ShotScatterSeed)`
  (`BallController.cs:920`), an editor-tunable export (default `12345`,
  `BallController.cs:314`). The RNG instance exists on every peer, but is
  only ever **drawn from when `IsServer`** — so only the server's draw
  matters and clients cannot diverge, because they never draw.
  `ShotScatter.Scatter(...)` itself is a pure function taking the drawn
  samples as parameters (ADR-0009) — it never calls `GD.Randf()` (Godot's
  engine RNG, explicitly rejected: not reproducible/injectable).
- **Ordered pipeline** — the sequence of the ball's per-tick steps inside
  `BallController._PhysicsProcess` (reconcile → block-resolve before the
  state switch → state switch → release-tick block top-up → player-OOB →
  steals → clear status → broadcast, `BallController.cs:1027-1132`) is
  itself part of the correctness contract, not an implementation detail you
  may reorder for readability. Example: blocks resolve BEFORE the state
  switch precisely so a block can never lose a same-tick race with scoring.

**The rule generalizes past the ball** (ADR-0004, 2026-06-17 amendment):
the player's visual mesh, cosmetic facing, and burst lean are
presentation-only, computed locally, **never** fed back into authoritative
state. Authoritative outcomes read the server-authoritative `Heading`
(ADR-0010), never the cosmetic `FacingResolver`.

---

## 4. Prediction & reconciliation walkthrough

**Term: prediction.** The client applies its own input to its local copy of
the game state *immediately*, without waiting for the server, so the
controlling player feels zero input lag. **Term: reconciliation.** When the
server's authoritative broadcast arrives (always describing a moment ~1 RTT
in the past), the client corrects its local state to match it, then
re-simulates ("replays") everything it did since, so its *current* position
is still an accurate prediction of "now". **Term: RTT (round-trip time).**
The time for a packet to travel client→server→client.

### 4a. Four tick roles (`PlayerController.cs` class doc, lines 16-33)

| Role | IsServer | IsLocalPlayer | Behavior |
|---|---|---|---|
| Host's own player | T | T | Read hardware, apply immediately — authoritative, zero lag |
| Server's copy of a remote player | T | F | Consume latest `SubmitInput`, apply, broadcast with the client's ack |
| Client's own player | F | T | **Predict**: apply locally now, send input+seq, reconcile on broadcast |
| Client's copy of a remote player | F | F | **Display-only**: `TickClientRemotePlayer` (`:1023-1047`) Lerps `GlobalPosition` toward the last broadcast; never calls `Move()`, never ticks `_machine` |

The client-remote row matters beyond movement: because that copy's
`CommittedMoveMachine` never ticks, its local `_machine.Phase` is
permanently `Inactive` — which is exactly the bug `DisplayPhaseResolver`
(§4f) exists to work around for animation display.

### 4b. `PredictionBuffer` — what gets buffered, acked, replayed

**Term: sequence number (seq).** A monotonically increasing integer the
client stamps on each tick's input, so the server can say exactly which
inputs it has applied. **Term: ack (acknowledgement).** The server's
"last-applied seq for you" value, echoed back in every `ReceiveState`.

`scripts/Player/PredictionBuffer.cs` is a **pure** class (no Godot Node,
no engine singletons) — extracted from inline PlayerController fields
specifically so the seq/ack contract is unit-testable (issue #55).

- `Record(input)` — the client's own player calls this every tick. Assigns
  the next sequence number (starting at 1), evicts the oldest entry if the
  buffer is at `Capacity` (default 120 ≈ 2 s at 60 Hz — only hit under ~2 s
  of sustained server silence), enqueues `(seq, input)`, returns the seq to
  send alongside the input in `SubmitInput`.
- `Acknowledge(ackSeq)` — call **before** `Replay()`. Dequeues every
  buffered entry with `seq <= ackSeq`: the server confirmed it applied
  these, so they no longer need replaying.
- `Replay()` — yields the remaining (unacknowledged) inputs, oldest first,
  for `ReconcileFromServer` to re-apply through `Move()`. The replay
  *execution* deliberately stays in PlayerController — replaying means
  calling `MoveAndSlide()`, Godot's collision solver, which is irreducibly
  engine-bound.

### 4c. `ReconcileFromServer` (`PlayerController.cs:1452-1619`) — the full algorithm

1. **Step 0 / 0.5 — discrete correction** (§5 — THE netcode law).
2. **Step 1**: `_buffer.Acknowledge(ackSeq)` — prune confirmed inputs.
3. **Step 2**: remember `renderedPos = GlobalPosition` (what is on screen
   right now) for the divergence measurement at the end.
4. **Hard-snap**: `GlobalPosition = authPos; Velocity = authVel; Heading =
   authHeading;` — the physics body jumps straight to the server's
   ~1-RTT-old truth. The pivot latch is snapped too (`:1599`) so the replay
   starts from the correct plant state.
5. **Step 3 — replay**: `foreach (Vector2 input in _buffer.Replay())
   Move(input, fixedDelta);` at the **fixed** timestep (§2) — re-simulates
   every unacknowledged input to advance the authoritative-but-stale
   position back up to "now". One `Move()` (with its internal
   `MoveAndSlide()`) per buffered input is intentional — multiple physics-
   solver calls in one rendered frame is correct here; each mirrors one
   past server tick.
6. **Step 4 — smooth correction**: `divergence = renderedPos -
   GlobalPosition`; if it exceeds `ReconcileSnapThreshold`, **SET** (never
   `+=`) `_smoothOffset = divergence` (`:1617`).

**Why replay at all, instead of just snapping?** The broadcast is already
~1 RTT stale when it arrives. Snapping to it with no replay would visibly
yank the player backward by 1 RTT of movement on every correction. Replay
brings the corrected state forward to "now" using the same inputs the
player already sent.

### 4d. The mesh-offset smoothing trick

**SET, not accumulate:** `_smoothOffset` is *assigned* the fresh
divergence, never added to a running total. A doubt-cycle review found that
accumulating (`+=`) stacks a new correction on top of an unfinished old one,
causing overshoot and drift (doc comments at `PlayerController.cs:52-57`
and `:1614-1616` — an earlier version that manipulated `GlobalPosition`
directly overshot by 2.33×). A fresh divergence *supersedes* the prior one.

The physics body (`CharacterBody3D`) **snaps instantly** to the
authoritative position — collisions, raycasts, and every physics query must
see the true position immediately. Only the **child `MeshInstance3D`** is
visually offset by `_smoothOffset` and lerped back toward
`_meshRestPosition` each frame (`ApplySmoothCorrection`,
`PlayerController.cs:1637-1654`). The correction therefore *looks* like a
smooth drift instead of a hard pop, without ever lying to gameplay code
about where the player actually is. Note it lerps to `_meshRestPosition`,
not `Vector3.Zero` — Player.tscn authors a seat offset on the visual node
and clobbering it caused a "player floats above the floor" bug.
`BallController.cs` implements the identical pattern for the ball
(`ApplySmoothCorrection`, `:2767-2784`; SET-not-accumulate at `:693-698`).

### 4e. What is deliberately NOT replayed: `TickCommittedMoveBehavior`

The replay loop calls **`Move()` only** — never
`TickCommittedMoveBehavior`, the method that applies an Active-phase
committed move's burst (crossover lateral shove, etc.). This is a
**documented, accepted trade-off** (`PlayerController.cs:90-97`,
`:1590-1598`): a buffered tick whose real-time moment had an Active move
running is replayed as if only `Move()` had run, so the burst itself is not
reconstructed. The residual position-only divergence is absorbed by the
same `_smoothOffset` mesh-lerp (§4d) — a visual smoothing matter, not a
correctness bug, and explicitly called out as such. **Do not "fix" this by
adding `TickCommittedMoveBehavior` to the replay loop** without reading the
doc comment at `PlayerController.cs:90-97` first — it is a known,
load-bearing simplification, not an oversight (and it is the divergence
channel behind open issue #210, §12).

### 4f. Remote-phase display: `DisplayPhaseResolver` (issue #69)

Because the client's copy of the opponent never ticks its local machine
(§4a), reading that machine for the opponent's animation means the
opponent's startup→active→recovery arc **never renders on your screen** —
silently breaking ADR-0003's promise that both players can see commitment.
The fix (`scripts/Player/DisplayPhaseResolver.cs`, pure class):
`LocalMachineDrivesDisplay(isServer, isLocalPlayer) => isServer ||
isLocalPlayer` — three of the four roles simulate the node and display
their local machine; the lone exception (client's copy of a remote player)
displays the **broadcast** `serverPhase` instead. Strictly a cosmetic read
— it never writes `_machine`, prediction, or replicated state, and the
reconcile path keeps consulting `_serverMovePhase` exactly as before.

---

## 5. Discrete vs continuous state correction — THE NETCODE LAW

This is the single most important rule in this codebase's netcode, and the
one most likely to bite a well-meaning fix.

**Term: discrete state.** State with a small set of named values and legal
transitions — an enum, effectively — where "wrong" means being in the wrong
*named value* (`BallState.Held` vs `InFlight`; `MovePhase.Active` vs
`Recovery`). **Term: continuous state.** State that varies smoothly —
position, velocity, a frame counter — where "wrong" means being off by
*some amount*.

**The law:**

> **Force discrete identity. NEVER force-match a ~1-RTT-stale frame
> counter.**

Concretely:

- Ball state: `BallStateMachine.ForceState` (`BallStateMachine.cs:213-217`)
  bypasses the legal-transition graph and slams the enum to the server's
  value — reconciliation-only.
- Move phase: `CommittedMoveMachine.ShouldForceInactive` /
  `ShouldForceRecovery` (`CommittedMoveMachine.cs:351-356`, `:400-410`)
  decide *whether* to force a phase snap; when they fire,
  `ForceState(phase, frameInPhase: 0, ...)` **always resets `frameInPhase`
  to 0** — never to the server's broadcast `FrameInPhase`.

**Why never force-match the frame counter:** `ReceiveState` arrives ~1 RTT
stale (§4c). By the time it lands, the client's own `FrameInPhase` for an
ongoing move is *always* further along than the packet reports — not a bug,
just how far real time has moved since the server sampled it. If you
compared `FrameInPhase` for equality and forced a match on mismatch, you
would **continuously rewind every active move for the entire duration of
any nonzero latency**, visibly mangling every burst's timing. The
broadcast's numeric values are old news; the discrete *identity* (which
phase, which move, which ball state) is the only part still trustworthy as
truth right now — identity mismatches are rare and meaningful, while
numeric staleness is the *normal*, expected condition of every packet. Once
phase/move identity agree, `Tick()` is a pure function of elapsed ticks
with no further network dependency — exactly like the ball's InFlight arc,
which is never force-rewound to a stale frame count either.

Read `PlayerController.cs:1444-1515` in full before touching
`ReconcileFromServer` — it is the canonical statement of this law.

**Two-gate structure, deliberately not merged:**

| Gate | Fires when | Correction |
|---|---|---|
| `ShouldForceInactive` (`:351-356`) | server says `Inactive` AND the client is active past Startup (a move the server rejected) | Force `Inactive`; restore authoritative `HandSide` in this same branch only (ADR-0012 — a per-tick HandSide snap would flicker every correctly-predicted crossover swap) |
| `ShouldForceRecovery` (`:400-410`) | client `Active`, server `Recovery`, **AND** the level-triggered `WasRecoveryEnteredEarly` bit is true, **AND** local/server move IDs match | Force `Recovery`, `frameInPhase: 0`, `recoveryWasEarly: true` |

Why not fold the second into the first with a bare `serverPhase ==
Recovery` case? Every move legitimately crosses Active→Recovery on
schedule, and under ordinary jitter the client's own Tick() reaches
Recovery around the same wall-clock time the broadcast reports it — a bare
phase check would force a spurious correction on **every ordinary
boundary**. Only an *early* Active end (server-driven `EndActiveEarly()` —
today only a resolved steal) should correct anything, and detecting "early"
needs a dedicated signal: §6. The move-ID guard additionally prevents a
stale echo of move A's early end from truncating a brand-new move B.

There is also a **Startup grace** built into `ShouldForceInactive`: it
never fires while the client is still in Startup, because a ~1-RTT delay
before the server's confirmation arrives is expected on *every legitimate*
move attempt — reverting during Startup would falsely flicker every single
committed move (`PlayerController.cs:1470-1482`).

---

## 6. Edge-triggered vs level-triggered signals over lossy channels

**Term: edge-triggered.** A signal true for exactly one tick, at the moment
an event happens — a pulse. **Term: level-triggered.** A signal that stays
true for the entire duration of a state — a light switch, not a doorbell.

**The story (issue #175):** After a steal resolved, `EndActiveEarly()`
ended the defender's Active phase early — but this ran **server-only**,
with no client-side counterpart. The defending client's own predicted copy
kept believing it was Active for the rest of the window, unable to
`Begin()` a new move until the broadcast caught up.

The naive fix — a one-tick "Active just ended early" flag riding
`ReceiveState` — would be **edge-triggered**, and `ReceiveState` is
UnreliableOrdered (§7): if the one packet carrying the pulse is dropped,
the client never learns the event happened, because every subsequent
packet's flag has already reverted to false.

The actual fix: `WasRecoveryEnteredEarly` (`CommittedMoveMachine.cs:69`) is
**level-triggered** — `Phase == MovePhase.Recovery && _recoveryWasEarly`,
where the backing field is set true ONLY inside `EndActiveEarly()`
(`:241`) and stays true for the whole Recovery duration; every normal
phase transition and `Begin()` resets it. The client overwrites its copy on
**every** broadcast, even a redundant "still true"
(`PlayerController.cs:1418-1421`) — that redundancy is exactly the
packet-loss robustness: the *next* delivered packet still carries the
correct value.

**General lesson:** any one-shot discrete event a peer must learn about
over an unreliable channel must be represented as a **level** (a flag that
stays set for as long as it is relevant) — or sent on a Reliable channel as
a one-shot RPC (the `RequestBeginMove` pattern, §7). A single-tick pulse on
an unreliable channel dies silently the instant one packet is lost.

---

## 7. RPC channel semantics

**Term: RPC (remote procedure call).** A method whose body executes on a
*different* machine than the caller — Godot's `[Rpc]` attribute plus
`Rpc()`/`RpcId()` invocations. **Term: transfer mode / channel.** The
delivery guarantee ENet applies: **Reliable** (guaranteed delivery, in
order — a lost packet is retransmitted, and later packets wait for it) vs
**UnreliableOrdered** (best-effort — drops are silent, but a stale packet
arriving after a newer one is discarded rather than applied out of order).
**Term: head-of-line (HOL) blocking.** When a reliable channel is waiting
to retransmit one lost packet, everything queued behind it on that channel
stalls too — even if the receiver would rather have the newer data now.

| RPC | Mode | Channel | Why |
|---|---|---|---|
| `SubmitInput` (`PlayerController.cs:1074-1077`) | AnyPeer, CallLocal=false | UnreliableOrdered | Streamed every tick; a dropped old input is worthless once a newer one exists — retransmitting it would be actively harmful |
| `RequestBeginMove` (`:1232-1235`), `RequestFeint` (`:1322-1325`) | AnyPeer, CallLocal=false | **Reliable** | One-shot discrete events — a dropped "I began a move" must NOT silently vanish, so it retransmits |
| `ReceiveState` (Player `:1392-1397`; Ball `BallController.cs:2682-2685`) | **Authority**, **CallLocal=false** | UnreliableOrdered | Broadcast every tick. Reliable would HOL-block behind any lost packet and visibly rubber-band while waiting for a retransmit of an already-obsolete snapshot — a newer snapshot supersedes the old, so drop-and-move-on is correct. `CallLocal=false` so the host/server **never reconciles against its own broadcast** — it IS the authority; correcting itself to itself is at best a wasted replay and at worst a feedback loop |

("AnyPeer" = any peer may invoke this RPC on the receiver; "Authority" =
only the multiplayer authority — the server, peer 1 — may invoke it.)

**Anti-spoof pattern**, identical in every AnyPeer handler:

```csharp
int senderId = Multiplayer.GetRemoteSenderId();
if (senderId.ToString() != Name)
{
    GD.PrintErr($"[PlayerController] Unauthorized SubmitInput from peer {senderId} for node '{Name}'");
    return;
}
```

`Node.Name` is the peer-id string (§9). `GetRemoteSenderId()` is **valid
only synchronously inside the RPC handler** — read it at the top, never
cache it for later (`PlayerController.cs:1061-1064`, `:1079-1081`). This is
what stops client A from forging inputs or move-begins for client B's node
— trusting a peer-id *parameter* instead of the transport-verified sender
would let any client puppet any player.

---

## 8. Channel-ordering races between Reliable and UnreliableOrdered

ENet's ordering guarantee is **per-channel only**. `RequestBeginMove`
(Reliable) and `SubmitInput`/`ReceiveState` (UnreliableOrdered) are
different channels — there is **no guarantee** which arrives first when
both are in flight around the same tick.

**The cradle KNOWN ACCEPTED RACE** (`BallController.cs:2396-2410`,
`CradleForShotStartup`): a JumpShot's begin (Reliable) and the surrounding
state broadcasts (UnreliableOrdered) can be observed ~1 tick out of order
relative to each other. This is explicitly documented in-code as accepted
and deferred — **do not "fix" it by moving more traffic onto the Reliable
channel**; that reintroduces HOL-blocking risk on the hot broadcast path
for a ~1-tick edge case. If you find a suspected cross-channel race, check
whether it is this documented one before treating it as a new bug (and
check the sibling `hooper-failure-archaeology` skill).

---

## 9. Identity: `MultiplayerSpawner`, `Node.Name`, and what auto-replicates (nothing)

`MultiplayerSpawner` (`scenes/Main.tscn:187-190`) replicates **existence
only** — it makes a `Player.tscn` instance appear under the `Players` node
on every peer (`spawn_path = NodePath("../Players")`, `spawn_limit = 2` —
the real 1v1 cap). It replicates **no gameplay state**. There is **no
`MultiplayerSynchronizer`** anywhere in this project
(`NetworkManager.cs:16-18`): every field that must cross the network rides
one of the manual RPCs in §7. Why: a Synchronizer replicates properties on
the engine's schedule with no seq/ack contract — useless for prediction/
reconciliation, and a second writer fighting the hand-built netcode. If you
add a new piece of authoritative state, you must add it to a `ReceiveState`
payload (or a new one-shot Reliable RPC, per §12's prescribed pattern)
explicitly. Nothing auto-replicates.

**The identity contract**: `player.Name = <peer-id string>`, set **before**
`AddChild` (`NetworkManager.cs:402` then `:414`):

```csharp
player.Name = name;       // set BEFORE AddChild — _Ready/_PhysicsProcess haven't run yet
Players.AddChild(player); // replication to clients + lifecycle starts here
```

`Name == Multiplayer.GetUniqueId().ToString()` is how a peer recognizes
"this node is my player" (`IsLocalPlayer`), how the ball looks up its
holder, and the anti-spoof check in every RPC handler (§7). Setting `Name`
after `AddChild` would open a window where the node runs with the wrong
identity. Don't reorder this, and don't rename player nodes.

---

## 10. Topologies: listen-server vs headless dedicated server

**Term: listen-server.** One machine is simultaneously the server process
AND player 1 (`NetworkManager.HostGame`) — the topology used through
Milestones 1b-5 and by most harness scenes. **Term: headless dedicated
server.** A server process with **no** locally-controlled player
(`StartDedicatedServer`, bootstrapped by
`scripts/Networking/DedicatedServerBootstrap.cs`) — added by ADR-0007 as an
*additional* topology, coexisting with listen-server, not replacing it.
ADR-0002's authority model is identical in both; only whether the server
process also renders and controls a player differs.

- `DedicatedServerBootstrap` starts the server via `CallDeferred` one frame
  late so sibling nodes' exports are `_Ready` first
  (`DedicatedServerBootstrap.cs:84-88`).
- Tipoff self-assigns the ball to the first player node — the same code
  path works whether that player is the listen-server host's own or a
  purely remote client (`BallController.cs:896-910`).

**Why remote-display proofs need `HostGame`:** a test proving "the
OPPONENT's committed move renders on MY screen" is exercising the
client-remote display role (§4a/§4f) — it needs two live instances where
one is a *client observing a remote copy*. A headless dedicated server has
no client-remote copy of anything to inspect. Use the dual-instance
`HostGame` topology (harness mechanics live in the sibling
`hooper-verification-and-qa` skill).

---

## 11. Latency reasoning: server-only outcomes arrive ~1 RTT late

Steal, block, and shot-scatter outcomes are **never client-predicted** —
only the server resolves them (§1). Consequence: when a defender begins a
steal attempt, their client immediately predicts the *move* (phase
progression, animation) but only learns the *outcome* — did the ball come
loose? — when the server's next `ReceiveState` broadcast arrives, **~1 RTT
after the fact**. This is a design consequence of ADR-0002, not a bug:
predicting the outcome client-side would mean guessing state the client
cannot legitimately know (the opponent's live dribble phase and hand side
at the server's resolution tick), and a wrong guess (a flickering
false-positive steal) is worse than a clean short delay before showing the
truth.

If a task asks to "make steal/block feel more responsive," the legitimate
fix space is feedback polish on the attempting player's own predicted
Active-phase visuals — never predicting the outcome itself, which would
violate the only-server-mutates boundary (§1).

---

## 12. Open netcode issues to know (state as of 2026-07-12 — re-check)

Both found in code review of PR #205 (#198 moving crossover); both OPEN and
`afk`-labeled as of 2026-07-12.

- **#210 — crossover exit-vector divergence under jitter/packet loss.**
  The Active-phase burst direction is sampled by each role at its OWN
  Active-entry tick: the predicting client reads live stick input; the
  server reads its cached `_pendingRawStick` from the last `SubmitInput`
  received. `SubmitInput` is UnreliableOrdered (§7), so jitter or a drop
  can leave the server composing the burst from an older stick sample —
  roughly 5-30° of angular divergence, occasionally more. There is no
  self-heal: `ReconcileFromServer` snaps `Velocity` unconditionally and the
  replay never re-runs `TickCommittedMoveBehavior` (§4e), so the divergence
  shows as a hard, unsmoothed direction change mid-Active. **Prescribed fix
  (in the issue)**: replicate the exit vector as a discrete one-shot
  **Reliable** RPC (e.g. `RequestCrossoverExitVector(x, y)`) fired at the
  client's local `JustEnteredActive`; the server uses that value directly
  instead of independently sampling its cache. This is the same
  "discrete intent rides a reliable one-shot; streams stay unreliable"
  pattern as `RequestBeginMove` (§7) and the discrete-identity law (§5).
  The fix should land in the shared burst-composition path so
  behind-the-back and later burst-family moves (#194/#197/#199/#201)
  inherit it.
- **#209 — crossover push-cross velocity cliff at 4.0 m/s.** A bit-exact
  branch (`survivingXZ.LengthSquared() < 0.0001f`) in
  `CrossoverBurstMath.ComposeActiveVelocity` makes entry speed 3.9 m/s
  yield a full 9 m/s lateral burst while 4.1 m/s yields ~nothing. This is
  *deterministic and identical on all peers* — a feel/legibility
  discontinuity (ADR-0003), not a desync. Listed here because it shares the
  burst code path with #210; know it exists before touching crossover math.

---

## Quick reference: the invariants this skill teaches the reasoning for

(The complete invariant list with the full system map is the
`hooper-architecture-contract` skill's territory; this is the netcode
subset.)

1. Fixed `dt = 1/Engine.PhysicsTicksPerSecond` in all ball/heading/replay
   math; durations are tick counts (§2).
2. Only the server mutates score / `IsCleared` / OOB awards / steal-block
   outcomes / the scatter RNG draw (§1).
3. Force discrete identity; never force-match a stale frame counter (§5).
4. Level-triggered signals for one-shot events on unreliable channels (§6).
5. `_smoothOffset` is SET, never accumulated; the physics body snaps, only
   the mesh child drifts (§4d).
6. `ReceiveState` is Authority + UnreliableOrdered + `CallLocal=false` on
   both Player and Ball (§7).
7. Every AnyPeer RPC validates `GetRemoteSenderId().ToString() == Name`,
   read synchronously at handler top (§7).
8. `Node.Name` = peer-id string, set before `AddChild`; no
   MultiplayerSynchronizer, nothing auto-replicates (§9).
9. Cosmetic (`FacingResolver`/`LeanResolver`/`DisplayPhaseResolver`/
   `MoveAnimState`) never feeds authoritative state (§3, §4f).

---

## When NOT to use this skill

- **You need the full invariant list, system map, or weak-point inventory**
  → `hooper-architecture-contract`. This skill is the *theory*; that one is
  the *map and contract*.
- **You are triaging a live symptom** (snap, rubber-band, phantom move,
  crash) → `hooper-debugging-playbook` first (symptom→triage), and
  `hooper-failure-archaeology` for battles already fought — several
  apparent netcode bugs here are documented accepted races or settled
  fixes.
- **Gameplay rules** (possession, scoring, frame data, shot scatter design)
  that aren't about *networking* them → `hooper-duel-design-reference`.
- **Writing or running harness proofs** (including the dual-instance
  HostGame remote-display pattern) → `hooper-verification-and-qa`.
- **Adding or changing a tunable/export** → `hooper-config-and-flags`.

---

## Provenance and maintenance

Written 2026-07-12; reviewed and corrected 2026-07-15 (`RegisterBasket` line
anchor 136 → 134); verified against commit `3085ee1` (HEAD of `main`, PR
#215 merged). All quoted code, RPC attributes, class names, and the
#210/#209 issue states were read directly from the repo/`gh` at that
commit; line numbers cite that snapshot.

Re-verification one-liners (run from the repo root,
`"C:/Users/The King/Documents/GitHub/hooper-game"` — note the space, always
quote):

- Line numbers drift with every refactor — re-grep before trusting any
  `:NNN` above, e.g.:
  `grep -n "ShouldForceRecovery" "scripts/Input/CommittedMoveMachine.cs"`
- RPC channel table (§7):
  `grep -n -A3 "\[Rpc(" "scripts/Player/PlayerController.cs" "scripts/Ball/BallController.cs"`
- Tick rate is Godot's default 60 unless overridden — check:
  `grep -n "physics_ticks_per_second" project.godot` (no match = default 60).
- Issue states (§12): `gh issue view 210 --json state,title` and
  `gh issue view 209 --json state,title`.
- No-MultiplayerSynchronizer claim (§9):
  `grep -rn "MultiplayerSynchronizer" scripts/ scenes/` (expect comment-only
  hits in `NetworkManager.cs`, no node usage).
- `ShotScatterSeed` default (12345) and seeding site:
  `grep -n "ShotScatterSeed" "scripts/Ball/BallController.cs"`.
- ADR statuses (0002/0004/0007/0009/0010/0012/0018 all Accepted as of
  2026-07-12): `grep -n "Status" docs/adr/0002-*.md docs/adr/0004-*.md`.
