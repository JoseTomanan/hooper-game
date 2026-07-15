---
name: hooper-architecture-contract
description: Load-bearing design decisions, system map, and the numbered invariants for the hooper-game codebase (Godot 4 .NET / C#, server-authoritative 1v1 basketball). Trigger when adding/changing ball, player, netcode, committed-move, possession, or heading logic; before touching BallController.cs or PlayerController.cs; when a change might break determinism or reconciliation; or when you need to know "where does X kind of logic live" or "what will silently break if I do Y." Also load when reviewing a PR that touches scripts/Ball, scripts/Player, scripts/Networking, or scripts/Input for invariant violations.
---

# Hooper architecture contract

This skill is the map of **how the codebase is actually built** and the
**rules that must never be silently violated**. It is not a tutorial and not
a tunable-values reference — see "When NOT to use this" below for those.

Read this before writing engine-facing code in `scripts/Ball/`,
`scripts/Player/`, `scripts/Networking/`, `scripts/Input/`, or
`scripts/Systems/`, and before reviewing a PR that touches them.

Game-design jargon used here without re-definition ("cradle", "hand-side",
"pivot latch", "dead dribble", move-phase names) is defined in
`hooper-duel-design-reference` §7's glossary.

## The dominant pattern (read this first)

Every system in this codebase follows the same shape: **a thin Godot node
that owns engine API, RPCs, and per-tick sequencing, wrapping pure C#
classes that hold all the actual math and rules.** The pure classes have no
`Node`, no `Random`, no `DateTime`, no `Input.*` — nothing engine-shaped —
which is what makes them unit-testable headless and bit-identical across
server and every client.

There are exactly **two god-nodes**, and they are the ONLY places engine
API / RPC / tick-sequencing code is allowed to live:

| Node | File | Size (as of 2026-07-12) | Owns |
|---|---|---|---|
| `BallController` | `scripts/Ball/BallController.cs` | 2785 lines | Ball state machine sequencing, all ball RPCs, shot/steal/block resolution order |
| `PlayerController` | `scripts/Player/PlayerController.cs` | 2217 lines | Movement, heading, committed-move sequencing, all player RPCs, reconciliation |

**Rule of thumb for new work:** if you're adding new ball behavior, put the
logic in a new or existing *pure* class under `scripts/Ball/` (e.g.
`ShotArc`, `RimBackboard`, `FloorBounce`, `DefensiveResolution`) and have
`BallController` call it. Never grow the god-node's own math — grow the pure
class it delegates to. Same discipline applies to `PlayerController` and
`scripts/Player/`, `scripts/Input/`.

Re-verify the line counts with:
```
wc -l "scripts/Ball/BallController.cs" "scripts/Player/PlayerController.cs"
```

---

## System map

### 1. Ball — deterministic mini-physics (`scripts/Ball/`, ADR-0004)

- **`BallController`** (Node3D) is a single shared node instanced once per
  Main.tscn, present identically on every peer. There is no "am I the ball
  node" role split — the role split is "is this machine the server" (via
  `IsServer`) crossed with "is the local peer the holder." See the class doc
  at the top of `BallController.cs`.
- **State machine**: `BallStateMachine.cs` — pure. States `Held / Dribbling /
  InFlight / Loose`; legal transition edges are `StartDribble / StopDribble /
  Shoot / Catch / GoLoose / Turnover`. Transition methods return `bool`
  instead of throwing — a rejected transition must never crash a tick.
  `ForceState` bypasses the edge graph and exists ONLY for reconciliation
  (invariant #6 below).
- **Integration**: fixed `dt = 1.0f / Engine.PhysicsTicksPerSecond` (see
  `BallController._PhysicsProcess`), never wall-clock delta. `ShotArc.Step`
  uses **trapezoidal (average-velocity) integration** —
  `Position += 0.5f * (oldVel + newVel) * dt` — deliberately NOT
  semi-implicit Euler, because Euler undershoots by `0.5*g*t*dt` per tick and
  the accumulated undershoot clanged shots off the front rim that should
  have gone in. See the derivation comment at the top of `ShotArc.cs` (grep
  `trapezoidal`). Launch velocity is solved closed-form from a desired apex
  height (`ShotArc.SolveInitialVelocity`).
- **Shot chain** (server-only): release check → `ApplyShootLocally` — refuses
  a release while the shooter is OOB, computes
  `accuracyMultiplier = movementFactor × contestFactor × facingFactor`
  (facing reads the *authoritative* `Heading`, never the cosmetic
  `FacingResolver` — see invariant #10), draws the scatter target via
  `ShotScatter.Scatter(...)` (uniform-disc sample in XZ, multiplier applied
  AFTER the distance cap — see `ShotScatter.cs`), then constructs
  `new ShotArc(pos, aimTarget, ShotApexHeight, Gravity)`.
- **Contact resolution**: `TickInFlight` steps the arc, then
  `RimBackboard.Resolve` checks, in priority order: (1) Make — inside the
  inner rim, descending, near the rim plane; (2) rim-ring bounce —
  `v' = v - (1+e)(v·n)n` plus depenetration; (3) backboard rectangle bounce.
  A Make transitions to `Loose` everywhere, and additionally calls
  `ResolveServerMake()` if `IsServer`. A non-contact result defers to
  `FlightTermination.ShouldGoLoose` (floor or OOB) to end airballs.
- **Floor bounce**: `FloorBounce.cs` — contact triggers at `Y <= ballRadius`,
  depenetrates to exactly `ballRadius`. If `|vY| * restitution < settleSpeed`
  the ball *fully* settles (whole velocity zeroed, not just vY). Otherwise
  `vY' = -vY * 0.76` (coefficient of restitution — derived from the NBA ball
  inflation spec, proven by a regulation-drop rebound-height test) and
  `vXZ' = vXZ * 0.9`.
  Re-verify: `grep -n "0.76" scripts/Ball/FloorBounce.cs`.
- **Dribble**: `DribbleCycle.cs` — cosine height curve, dt injected (never
  reads engine time itself). Crossover/behind-the-back ball transit uses
  `CrossoverBallSweep`, a single-arch curve whose duration is a **tick
  count** (`CrossoverSweepDuration × PhysicsTicksPerSecond`), never a
  wall-clock timer — see invariant #1.
- **Steal/block** (ADR-0018; server-only resolution, never client-predicted
  — the acting client learns the result ~1 RTT later via broadcast):
  - **Steal** (`ResolveStealAttempts`, called AFTER the state switch — grep
    it in `BallController.cs`) only fires while `Dribbling`. Success test is
    `DefensiveResolution.StealSucceeds(dribblePhase, lo=0.35, hi=0.65,
    targetHand, holder.HandSide)` in `DefensiveResolution.cs`: hand side must
    match AND the dribble phase must fall in the exposed band straddling
    ball-floor-contact (phase 0.5). This is a **per-tick point-in-band test
    against a live continuous value**, NOT the same interval-overlap
    predicate block uses — ADR-0018 explicitly flags this asymmetry as "not
    a template to copy" for future defensive moves. Success transitions
    `GoLoose()`, applies a provisional knock velocity, sets
    `_lastToucherPeerId = defender`, and ends the move
    (`EndResolvedDefensiveMove()` — one turnover per committed move).
  - **Block** (`ResolveBlockAttempts`, called BEFORE the state switch, plus a
    release-tick top-up — see invariant #4) uses a genuine **half-open
    interval-overlap** predicate: `DefensiveResolution.Succeeds(defActiveStart,
    defActiveEnd, _inFlightStartTick, _inFlightStartTick + BlockGraceTicks)`
    where the overlap test is `aStart < vEnd && vStart < aEnd`. This is
    **timing-only — there is no proximity/reach term** (a defender anywhere
    on the court currently "blocks" if the timing lines up). This is a known,
    accepted gap tracked as **issue #214**; do not silently "fix" it by
    adding a distance check without reading that issue first.
- **Ball netcode**: server calls `Rpc(MethodName.ReceiveState, ...)` every
  tick (`UnreliableOrdered`, `CallLocal = false` — see invariant #9) carrying
  state enum, position, velocity, holder peer id, `IsCleared`, `HasDribbled`.
  `ReconcileFromServer` force-snaps the state machine's state + holder,
  `IsCleared`, `HasDribbled`, position, and arc velocity; any resulting
  visual pop is absorbed by a lerped mesh offset (`ApplySmoothCorrection`),
  never by moving the physics body gradually. The ball is **deliberately
  never frozen at game-over** — see the long comment at the top of
  `BallController._PhysicsProcess`, which explains that `RegisterBasket` can
  flip `IsGameOver = true` synchronously mid-tick while the ball still needs
  several more ticks of `TickLoose` to finish falling to the floor; freezing
  the ball here would hang every client's view mid-air on the game-winning
  shot. Freezing the *players* (done in `PlayerController`) is what actually
  stops the match.

### 2. Networking — server-authoritative + client prediction (`scripts/Networking/`, `PlayerController`, ADR-0002)

- **Four tick roles** inside `PlayerController`: server-own, server-remote,
  client-own (predicted), client-remote (display-only — `TickClientRemotePlayer`
  never calls `Move()` or ticks `_machine`; it only Lerps toward the last
  broadcast state).
- **Predicted and reconciled**: position, `Velocity`, `Heading`, the pivot
  latch, `HandSide`, and the client's own committed-move phase (its local
  `_machine` predicts `Begin`/`Feint`).
- **Never predicted, ever**: score, steal/block outcomes, the shot-scatter
  RNG draw, the true `IsCleared` flip. These are exclusively server-decided
  and clients only ever display the broadcast result.
- **Reconciliation** (`PlayerController.ReconcileFromServer`) runs in this
  order: Step 0 `ShouldForceInactive` reverts a speculative move-begin the
  server rejected and restores `HandSide` — this is the method that embodies
  the "NETCODE LAW" documented in code: it deliberately never compares the
  ~1-RTT-stale broadcast `FrameInPhase` against the local counter (see
  invariant #6). Step 0.5 `ShouldForceRecovery` handles the server having
  ended an Active phase early. Then `_buffer.Acknowledge(ackSeq)`
  (`PredictionBuffer.cs`), a hard-snap of the physics body to the
  authoritative position/velocity/heading/pivot, a **replay** of buffered
  inputs through `Move()` at fixed dt — replay deliberately skips
  `TickCommittedMoveBehavior`, a documented residual divergence (an
  Active-phase burst is not reconstructed during replay) — and finally
  `_smoothOffset` is **SET** (never accumulated) to the delta between the
  pre-snap rendered position and the post-snap position, then lerped to zero
  on the mesh child only (never the physics body).
- **RPC table**:

  | RPC | Direction/Mode | Channel | CallLocal | Purpose |
  |---|---|---|---|---|
  | `SubmitInput` | AnyPeer | UnreliableOrdered | — | per-tick input from client to server |
  | `RequestBeginMove` | AnyPeer | Reliable | — | one-shot discrete committed-move begin |
  | `RequestFeint` | AnyPeer | Reliable | — | one-shot feint event |
  | `ReceiveState` (Player) | Authority | UnreliableOrdered | false | server broadcast of authoritative player state |
  | `ReceiveState` (Ball) | Authority | UnreliableOrdered | false | server broadcast of authoritative ball state |
  | `ReceiveScoreState` | Authority | Reliable | false | server broadcast of score/game-over |

  `ReceiveState` is deliberately UnreliableOrdered, not Reliable — Reliable
  would head-of-line-block and cause visible rubber-banding on packet loss;
  the continuous resend already self-heals a dropped packet next tick. Every
  RPC handler anti-spoofs by checking
  `GetRemoteSenderId().ToString() != Name` (see invariant #2 for why `Name`
  is trustworthy here).
- **`MultiplayerSpawner`** only replicates node *existence* (`Player.tscn`
  instances under the `Players` node, `spawn_limit = 2` — this literally IS
  the 1v1 cap). ALL gameplay state rides the manual RPCs above; there is no
  `MultiplayerSynchronizer` anywhere in this project.
- **Dedicated server** (`DedicatedServerBootstrap`, ADR-0007) starts the
  server via `CallDeferred`, one frame late, so sibling node exports are
  already `_Ready`. Tipoff self-assigns to the first player node that exists
  — this works identically for listen-server and headless-dedicated
  topologies.

### 3. Committed-move state machine (`scripts/Input/`, ADR-0003)

- **Phases**: `MovePhase` = `Inactive / Startup / Active / Recovery`.
  `MoveFrameData` is an immutable record that validates itself (Startup /
  Active / Recovery each ≥ 1 tick, feint window strictly inside Startup,
  etc.) at construction.
- **Move registry** — abstract `CommittedMove` carries a stable string `Id`
  plus a `FrameData`. Concrete subclasses (frame counts as of 2026-07-12,
  re-verify with `grep -n "FrameData" scripts/Input/*.cs`; the authoritative
  frame-data table with design rationale lives in
  `hooper-duel-design-reference` §2):
  - `Crossover` — 6/3/12 (Startup/Active/Recovery), feint window at frame 4,
    carries `BurstDirection` (±1).
  - `BehindTheBack` — 6/3/10, its own subclass (shares the ball-transit
    physics by composition with Crossover, not inheritance).
  - `Hesitation` — 4/8/6, unfeintable.
  - `JumpShot` — 18/4/20, pump-fake feint window frames [3,12), feint
    recovery 8.
  - `StealMove` — 8/8/20, carries `TargetHand`.
  - `BlockMove` — 10/8/20, no target — timing-only (this is the frame data
    behind the block-has-no-reach-term gap, issue #214).
  Active-phase *side effects* (what the move actually does to the ball/hand
  side) live in `PlayerController`'s per-move switch statement, NOT inside
  the `CommittedMove` subclass itself — the subclass is pure frame data.
- **`BeginCommittedMove`** — the single choke point on `PlayerController`
  (grep `private bool BeginCommittedMove(CommittedMove move)`) that every
  Begin call site must route through. It enforces, in order: the dead-dribble
  rule (Crossover/Hesitation/BehindTheBack are refused while the holder's
  ball is `Held`, not `Dribbling`), delegates to `_machine.Begin` (legal only
  from `Inactive`), clears the pivot latch on every successful begin, and
  cradles the ball for `JumpShot` startup. See invariant #11 — bypassing this
  method is a real, previously-committed bug class.
- **Commitment blocking**: while `_machine.IsActive`, `Move()` and
  auto-dribble are both skipped, client stick input is zeroed, heading is
  frozen, and a new `Begin()` structurally no-ops (the state machine won't
  leave `Inactive` mid-move). There is deliberately **no `Cancel()`** — only
  a windowed `Feint()` and a server-driven `EndActiveEarly()` (used e.g. by a
  successful block). This is the literal mechanism behind ADR-0003's
  "no flow-cancel" rule.
- **Gestures**: `RightStickGestureRecognizer` is a pure, sample-fed FSM — a
  hold longer than 4 ticks reads as Crossover intent, a quick return reads
  as Feint. `FeintGateResolver.ShouldFeint` scopes `JumpShot`'s pump-fake to
  the explicit feint button only (this was a real fix — an earlier version
  let an incidental stick flick during Startup silently feint-cancel a shot;
  see the failure-archaeology skill for the full story).
- **Remote display sync** (issue #69):
  `DisplayPhaseResolver.LocalMachineDrivesDisplay = isServer ||
  isLocalPlayer`. A client's rendered copy of the *opponent* displays the
  broadcast `serverPhase`, not its own never-ticked local `_machine` — this
  is strictly cosmetic and is never written back into any authoritative
  field. `MoveAnimState` is a deliberately separate enum from `MovePhase`
  (display taxonomy vs. gameplay state — don't conflate them).
  `LeanResolver` only tilts during Active — there is intentionally no
  pre-lean telegraph during Startup (that would leak the read early).

### 4. Possession rules & scoring (`scripts/Systems/`, ADR-0008)

- **Single mutation entry point**: `BallController.AwardPossession`
  dispatches `Turnover` vs `Catch`, then unconditionally resets
  `_lastToucherPeerId`, `HasDribbled = false`, the dribble cycle, `IsCleared`,
  and the "has been inside the clear line" flag. New possession-transfer code
  should call this, not hand-roll the reset sequence.
  **The one documented exception**: the steal-turnover path (inside
  `ResolveStealAttempts`) calls `GoLoose()` directly and lets the existing
  `TickLoose` rebound-scramble logic award possession next tick, bypassing
  `AwardPossession` — this is intentional, not an oversight, but it means a
  steal's possession transfer is one tick slower than a direct call would be.
- **Make-it-take-it**: `ResolveServerMake` (server-only) — a cleared make
  calls `RegisterBasket(shooter)` then `AwardPossession(shooter, cleared:
  true)` (the scorer starts their next possession *already* cleared, per an
  ADR-0008 amendment). An **uncleared make scores no points and turns the
  ball over to the defender** — this is a real, deliberate rule, not a bug.
- **Clear / take-it-back**: `IsCleared` flips true only on a genuine
  inside→behind crossing via `ClearLine.Advance` inside
  `UpdateClearStatus` (server-only). Merely standing behind the arc after a
  rebound does NOT clear the ball (issue #135) — the check is edge-crossing,
  not position-at-a-point-in-time.
- **Live rebound**: `TickLoose` runs on every peer and predicts recovery via
  `ReboundContest.Resolve(ballPos, candidates, PickupRadius)` — nearer player
  wins within a 1.0 m XZ radius. Divergent client guesses are corrected by
  the holder force-snap in `ReconcileFromServer`.
- **Out of bounds**: a loose ball going OOB awards via
  `OobResolution.ResolveRecipient(_lastToucherPeerId, other)` — "last
  toucher is out, ball goes to the other player" — keyed on **last toucher**,
  not last shooter (issue #118; these differ after a block or steal). A
  *handler* carrying the ball across the line is a separate, distinct
  server-only rule (`ResolvePlayerOutOfBounds`, called before
  `UpdateClearStatus` each tick — see the ordering block quoted in invariant
  #4). Both award paths are internally `IsServer`-gated.
- **Scoring flow**: `RegisterBasket` → `GameManager.RegisterBasket`
  (server-guarded) → pure `Scoreboard.RegisterBasket` (sets `IsGameOver` /
  `WinnerPeerId` once `TargetScore` is reached) → `BroadcastAndEmit`
  (`ReceiveScoreState`, Reliable RPC) → `ScoreChanged` / `GameOver` signals
  drive `ScoreHud` and a ball green-flash. **Score has no prediction channel
  and no reconciliation at all** — it is server-only writes and a passive
  client mirror. Do not add client-side score prediction; there is nothing
  to reconcile it against that isn't already simpler to just wait for.

### 5. Heading / pivot (`scripts/Player/HeadingMath.cs`, ADR-0010)

- `HeadingMath.RotateTowardYaw` computes the shortest-path angular diff,
  `t = |diff| / π`, and an effective turn rate =
  `Lerp(MaxTurnRateDeg, MaxTurnRateDeg * BackTurnSlowFactor, t)` — bounded
  and non-linear: fast for small corrections, slower for a full reversal.
  Current tuned defaults (re-verify, these have moved before —
  `grep -rn "MaxTurnRateDeg\|BackTurnSlowFactor" scripts/Player/`):
  900°/s max, 0.95 back-turn factor, giving a ≈0.20s full 180° reversal.
- `HeadingMath.Step` also implements the **pivot-plant gate** (issue #172):
  a heading change greater than 90° latches a `PivotState`; while latched,
  `Move()` sets `Velocity = Vector3.Zero` and skips
  `MovementMath.ComputeVelocity` entirely — feet stay planted until the
  latched target yaw is reached, and re-aiming mid-latch re-latches to the
  new target. This is what makes a hard direction reversal read as a real
  plant-and-pivot instead of an instant moonwalk.
- All of this runs identically — same pure `MathF`-only code path — in the
  server tick, client prediction, and reconciliation replay. There is no
  server-only or client-only heading branch; determinism comes from that
  uniformity.

### 6. Harness seams (how tests reach engine-facing code)

Two distinct mechanisms — know which one you need before adding a new one:

1. **`internal *ForHarness` properties** on `BallController` only:
   `LastToucherPeerIdForHarness`, `DribblePhaseForHarness`,
   `SweepActiveForHarness`, `SweepIsBehindBodyForHarness`,
   `VelocityForHarness`. Each exists because the underlying private state is
   otherwise only *indirectly* observable (e.g. you need
   `SweepActiveForHarness` to distinguish "no sweep happened" from "sweep
   already finished").
2. **`tests/integration/*HarnessSeam.cs`** — `partial class PlayerController`
   files living under `tests/integration/`, compiled into the **game**
   assembly (not the test assembly) via the game csproj's Compile Include for
   `tests/integration/**/*.cs`. They exist because production RPC entry
   points like `RequestBeginMove` are sender-id-gated and a headless
   single-Godot-instance test can't spoof a remote sender. Existing seam
   files: `StealHarnessSeam`, `BlockHarnessSeam`, `CrossoverSweepHarnessSeam`,
   `MovingCrossoverHarnessSeam`, `BehindTheBackHarnessSeam`,
   `TripleThreatHarnessSeam`.
   **Hard rule when adding a new seam**: a seam method MUST call the real
   production choke point (e.g. `BeginCommittedMove(new BlockMove())`), never
   `_machine.Begin()` or other internals directly. A prior seam skipped
   `BeginCommittedMove` and silently stopped testing the real path (it missed
   the pivot-latch clear) — see `BlockHarnessSeam.cs`'s own comment on this
   for the cautionary tale.
3. **CI** (`.github/workflows/ci.yml`) builds two separate compile surfaces —
   the game csproj (tests excluded) and the test csproj (`ImplicitUsings` on)
   — so a straight `dotnet build "HOOPER GAME.csproj"` is a real, distinct
   gate that `dotnet test` alone cannot provide. A separate `integration-test`
   CI job boots a real headless Godot 4.6.3 .NET process; pass/fail is the
   process exit code.

---

## THE INVARIANTS

These are the rules that, if silently broken, corrupt determinism or
netcode in ways that are hard to notice locally and only show up as
cross-peer desync, rubber-banding, or a state a human can't reproduce.
Each one states what breaks if you violate it.

1. **Fixed dt everywhere in ball/heading/committed-move math.** Always use
   `dt = 1.0f / Engine.PhysicsTicksPerSecond`, never `_PhysicsProcess`'s own
   `delta` argument. Any duration (sweep length, dribble cycle, cooldown)
   must be expressed as a **tick count**, never a wall-clock timer.
   *Break it and*: server and client integrate different amounts of motion
   per tick, and prediction/replay silently diverges from the authoritative
   sim — invisible until reconciliation starts snapping visibly.

2. **`Node.Name` == the peer-id string, set BEFORE `AddChild`.** This is the
   identity contract that holder lookup, RPC sender-anti-spoof checks
   (`GetRemoteSenderId().ToString() != Name`), and role checks all key off
   of (`NetworkManager.cs`, `player.Name = name;` before the child is added).
   *Break it and*: RPC anti-spoof either falsely rejects the real owner or
   silently accepts an impostor, and holder-lookup-by-name returns nothing.

3. **`Players` node comes before `Ball` node in `Main.tscn`.** The Ball's
   per-tick logic reads `holder.HandSide` and a defender's block Active
   interval on the assumption that players already ticked this frame.
   `Main.tscn` declares `Players` then `Ball` in that order (grep
   `name="Players"` / `name="Ball"` in `scenes/Main.tscn`).
   *Break it and*: block windows and hand-side-dependent steal checks shift
   by one tick, silently changing timing with no error anywhere. Test scenes
   replicate this exact node order deliberately — don't "clean it up."

4. **Per-tick ordering inside `BallController._PhysicsProcess` is
   load-bearing**, specifically: reconcile-from-server → increment local
   tick counter → `ResolveBlockAttempts()` (BEFORE the state switch) → state
   switch (`TickHeld`/`TickDribbling`/`TickInFlight`/`TickLoose`) →
   release-tick block top-up (only when `_inFlightStartTick == _physicsTick`)
   → tipoff auto-assign → `ResolvePlayerOutOfBounds()` →
   `ResolveStealAttempts()` → `UpdateClearStatus()` → `Rpc(ReceiveState,...)`.
   Each step has an in-code comment explaining why it sits where it does.
   Block-before-switch specifically guarantees a block always wins a
   same-tick race against a make: without it, `TickInFlight` could reach
   `RegisterBasket` before the block flips the ball `Loose`. The release-tick
   top-up exists because the pre-switch call ran while the ball was still
   Held/Dribbling — the shot only becomes `InFlight` inside the switch, so
   the release tick itself would otherwise never be evaluated and a defender
   whose Active window ends exactly on that tick would unfairly whiff.
   Player-OOB runs before `UpdateClearStatus` so the clear check evaluates
   the NEW holder, not the one being dispossessed.
   *Break it and*: a legitimately-blocked shot can still score, edge-timed
   blocks whiff, or clear/OOB decisions use stale possession data.

5. **Only the server mutates authoritative outcomes.** Score
   (`GameManager.RegisterBasket`), the `IsCleared` flip, OOB awards,
   steal/block success, and the shot-scatter RNG draw (`_shotRng`, drawn
   only when `IsServer`) are exclusively server-side. Clients predict
   everything else deterministically and accept corrections.
   *Break it and*: two clients can permanently disagree about who scored or
   who has the ball, with no mechanism to converge — the single most
   expensive class of bug in a server-authoritative game.

6. **Discrete state force-snaps; never partial-correct an enum; never
   force-match a stale frame counter (the "NETCODE LAW").**
   `BallStateMachine.ForceState` exists solely for reconciliation.
   `PlayerController`'s `ShouldForceInactive` / `ShouldForceRecovery`
   likewise hard-set the move phase rather than nudging it. Critically, the
   code documents (grep `NETCODE LAW` in `PlayerController.cs`) that
   reconciliation must NEVER compare the broadcast `FrameInPhase` counter
   against the local one to decide "are we in sync" — the broadcast value is
   already ~1 RTT stale by the time it arrives, so treating it as current
   truth causes perpetual false-corrections. Only *identity* (which state,
   which phase) force-snaps; the *frame count within* that phase stays with
   local prediction.
   *Break it and*: a shimmering enum (client and server disagree about a
   discrete state every other tick) or a jittering committed-move phase
   timer that never settles.

7. **`_smoothOffset` is SET, never accumulated**, on both `BallController`
   and `PlayerController`. The physics body snaps instantly to the
   authoritative position on reconciliation; a separate mesh-child offset
   absorbs the visual pop, and that offset is always assigned fresh
   (`_smoothOffset = renderedPos - GlobalPosition`), never `+=`'d.
   *Break it and*: repeated small corrections compound into runaway visual
   drift between the rendered mesh and the actual collider.

8. **`HandSide` contract (ADR-0012).** Mutated ONLY inside
   `TickCommittedMoveBehavior`'s Active-phase tick, and reset via
   `ResetHandSide()` on a possession change. Reconciled ONLY inside the
   force-Inactive branch of `ReconcileFromServer` — never every tick, which
   would flicker a correct client prediction back and forth. The ball's own
   mesh only ever *reads* `HandSide`; it never writes it.
   *Break it and*: crossover/hesitation hand visuals flicker, or (worse)
   the `HandSide`-gated steal check (ADR-0018) uses a value not actually in
   sync with the real committed-move state.

9. **`ReceiveState` has `CallLocal = false`** on both `BallController` and
   `PlayerController` (and `ReceiveScoreState` on `GameManager`). The
   server/host must never reconcile against its own broadcast — its local
   state already IS the ground truth it just sent.
   *Break it and*: the host reconciles against a 0-RTT echo of its own last
   tick — at best redundant work, at worst a spurious self-correction loop.

10. **Cosmetic state never feeds authoritative decisions** (ADR-0004 /
    ADR-0010). `FacingResolver`, `LeanResolver`, `DisplayPhaseResolver`, and
    `MoveAnimState` are display-only. Any authoritative outcome (the shot
    facing-accuracy term, defensive timing, etc.) reads the real
    server-authoritative `Heading`/`MovePhase`, never these display helpers
    — see `ShotFacing.cs`, which explicitly reads `Heading`.
    *Break it and*: a purely visual lerp state silently changes gameplay
    outcomes — a determinism bug AND unfair, because the two players see
    different "true" facings.

11. **All committed-move begins route through
    `PlayerController.BeginCommittedMove`.** It is the single choke point
    that enforces the dead-dribble rule, clears the pivot latch, and
    cradles the ball for `JumpShot` startup. Calling `_machine.Begin()`
    directly (as one early harness seam did) silently skips all three.
    *Break it and*: a move begins in a technically-legal but behaviorally
    wrong state — e.g. the pivot latch stays set from a previous turn, or a
    jump shot never cradles the ball.

12. **Pure classes stay engine-free.** No `Node`, no `Random` (inject an
    RNG or samples instead), no `DateTime`, no `Input.*` inside anything
    that is not `BallController`, `PlayerController`, or a node script that
    explicitly needs engine access (e.g. HUD). This is what makes
    `ShotArc`, `RimBackboard`, `DefensiveResolution`, `CommittedMoveMachine`,
    `Scoreboard`, `HeadingMath`, etc. runnable in a plain `dotnet test`
    process with no Godot runtime, and what keeps them deterministic.
    New ball behavior goes in a pure class, not the node.
    *Break it and*: the class needs Godot to test, and determinism is at
    risk from any non-deterministic engine API it pulls in.

13. **Never apply a non-uniform scale to a round Jolt collision shape**
    (`CylinderShape3D`, `CapsuleShape3D`, `SphereShape3D`). Their
    cross-section is a single radius; Jolt silently clamps a mismatched X/Z
    scale instead of erroring, and the collider stops matching its mesh.
    Author size on the shape resource (`radius`/`height`), keep node scale
    at `1`. `BoxShape3D` is exempt (independent X/Y/Z extents). Project-wide
    CLAUDE.md convention, not just a ball rule.

14. **The ball is never frozen at game-over.** See the long rationale
    comment at the top of `BallController._PhysicsProcess`: `RegisterBasket`
    flips `IsGameOver = true` synchronously mid-tick, but the ball still
    needs several more `TickLoose` ticks (and their `ReceiveState`
    broadcasts) to finish falling. An early game-over guard here freezes
    every client's ball mid-air on the winning shot. Freezing *players* at
    game-over (in `PlayerController`) is correct and is the actual
    match-stop mechanism.

15. **The court rectangle has one source of truth: `CourtBounds.DefaultMin`
    / `CourtBounds.DefaultMax`** in `scripts/Ball/CourtBounds.cs` (currently
    `(-7.62, -1.0)` to `(7.62, 11.88)`). `BallController`'s `CourtMin` /
    `CourtMax` exports default to these constants. Change court dimensions
    THERE, not by hand-editing an individual export default — that desyncs
    OOB logic from the visual court outline (a real bug this project already
    hit and fixed once).

---

## Known-weak points / accepted gaps

Grep for `TODO|FIXME|HACK` across `scripts/` returns **zero matches** (as of
2026-07-12) — this project tracks deferred work as dated "doubt cycle N,
finding #M" comments and numbered GitHub issues instead. The following are
real, currently-live gaps. They are documented decisions, not oversights —
do not "clean them up" without reading the owning issue/comment first:

- **Cradle race (~1 tick)**: a known-accepted reliable/unreliable
  channel-ordering race in the jump-shot cradle path
  (`CradleForShotStartup` in `BallController.cs`), explicitly deferred
  in-code.
- **Reconciliation replay skips `TickCommittedMoveBehavior`**: an
  Active-phase committed-move burst is not reconstructed during replay.
  Documented residual divergence; whether it should be replayed is an open
  design question.
- **Block has no reach/proximity term** — timing-only success; owned by
  **issue #214**. Do not add a distance check ad hoc.
- **`_lastToucherPeerId`'s steal-path write is server-only** with no client
  counterpart — safe today only because its sole consumer is itself
  server-gated. Adding any client-side consumer of this field requires
  building its own reconciliation path first.
- **`_inFlightStartTick` staleness trap**: any future code path that enters
  `InFlight` without going through `ApplyShootLocally` inherits a stale
  block-timing window (`ResolveBlockAttempts` would evaluate against the
  previous shot's release tick). If you add a new way to launch the ball,
  set this field explicitly.
- **Settled design forks with STALE code comments (trap)**: the movement
  shot penalty (**issue #64** — resolved: continuous speed-ratio penalty) and
  the contest model (**issue #65** — resolved: proximity-alone) were both
  decided 2026-06-27 and recorded as ADR-0009 amendments; both issues are
  CLOSED. The doc-comments in `scripts/Ball/BallController.cs` still say
  "Open design question (#64)" / "pending human review" — they are stale.
  Trust ADR-0009, not the code comments; do not relitigate or escalate these.
- **Feel-tuning is deferred wholesale to issue #104** plus the per-milestone
  human feel pass: steal window bounds, knock/swat speeds, `BlockGraceTicks`
  (which must stay ≥ `BlockMove`'s ActiveFrames of 8 — shrinking it below
  that silently makes some legal blocks impossible). Current values live in
  `hooper-config-and-flags`.
- **Steal-turnover bypasses `AwardPossession`** — the ONE documented
  exception to "AwardPossession is the single possession-mutation entry
  point" (§4 above). Intentional; any audit of "does every possession change
  go through AwardPossession" must account for it.
- **`ShotScatterSeed` is a fixed export (12345)** — the miss pattern is
  deterministic per server run. Fine for tests; whether shipping needs a
  per-match reseed is an open question, not a decision.

---

## Scene conventions (the parts that bite when hand-editing `.tscn`)

Per ADR-0011, Claude authors `.tscn`/`.tres`/`.res`/`project.godot` directly
by text-edit as ordinary AFK work. The fragile bits:

- Every scene file uses `format=3` plus a scene-level `uid=` (e.g.
  `[gd_scene format=3 uid="uid://c047mrir711mo"]` at the top of
  `Main.tscn`). **None of the hand-edited scenes declare `load_steps`** —
  Godot repopulates it on an editor save; its absence is consistent with
  pure text-authoring and is not something to "fix" by guessing a count.
  Expect diff noise (ids reshuffled, `load_steps` added) the first time a
  scene round-trips through an editor save.
- `ext_resource` ids follow `"<index>_<5charhash>"` and carry their own
  independent `uid=`. `sub_resource` ids follow `"<TypeName>_<suffix>"` with
  no uid. The two id namespaces can coincidentally share a suffix —
  harmless, but confusing when grepping.
- `node_paths=PackedStringArray(...)` on a `[node]` line is how scripts get
  exported `NodePath` fields wired to scene nodes. In `Main.tscn`, the
  `Ball`, `NetworkManager`, and `DiscoveryBroadcaster` nodes all wire a
  `"Players"` path — **all three must point at the SAME spawn root**, or the
  peer-id identity contract (invariant #2) breaks for whichever one doesn't.
- `MultiplayerSpawner` (`Main.tscn`) references `Player.tscn` in
  `_spawnable_scenes` **by `uid://`**, not by path, with
  `spawn_path = NodePath("../Players")` and `spawn_limit = 2` (the literal
  1v1 cap). Renaming or re-uid'ing `Player.tscn` silently breaks spawning
  with no load-time error — after touching `Player.tscn`'s identity, always
  re-check the spawner's `_spawnable_scenes` value.
- Node order matters (invariant #3): `Players` before `Ball`.
- Ship scene edits in their own single-concern commit with a headless load
  check wherever a local Godot binary is available (ADR-0011's guardrail);
  never bundle a `.tscn` edit into an unrelated logic-change commit. CI uses
  `chickensoft-games/setup-godot@v2` with Godot 4.6.3 if you need a binary
  reference point.

---

## Governing ADR map

| System | ADR(s) |
|---|---|
| Engine choice (Godot 4 .NET/C#) | ADR-0001 |
| Server-authoritative netcode + client prediction | ADR-0002 |
| Hybrid input / committed moves / no flow-cancel | ADR-0003 |
| Deterministic ball physics; cosmetic-never-authoritative | ADR-0004 |
| Dedicated-server topology & LAN discovery | ADR-0007 |
| Possession rules (make-it-take-it, live rebound, clear) | ADR-0008 |
| Shot accuracy / distance-based scatter + penalty factors | ADR-0009 |
| Player heading, non-linear turn rate, pivot latch | ADR-0010 |
| Claude authors scenes/config by text-edit | ADR-0011 |
| Ball hand-side is server-authoritative | ADR-0012 |
| Headless harness as official verification surface | ADR-0016 |
| Defensive timing-window model (steal/block/contest) | ADR-0018 |

Read all ADRs at session start before writing engine-facing code (CLAUDE.md
standing rule); this table only maps which one governs which system here.

---

## When NOT to use this

- **The theory/why of netcode patterns in general** — prediction,
  reconciliation, determinism as concepts and how they were adapted here →
  `hooper-netcode-reference`. This skill states the contract; that one
  explains the reasoning a newcomer needs to not fight it.
- **Current tunable values** (turn-rate degrees, frame data, court
  dimensions, seeds, export defaults) beyond the handful cited for context
  → `hooper-config-and-flags`. This skill states invariants and structure;
  that skill owns the numbers and how to add a new tunable.
- **How an invariant or gap was learned** (the original bug, the failed
  first attempt, the doubt-cycle finding) → `hooper-failure-archaeology`.
- **Symptom-first debugging** ("the ball is desyncing, where do I look?") →
  `hooper-debugging-playbook`.
- **Game-design rules and reference-tier grounding** (why possession rules
  are what they are; real ball vs. Undisputed 3 vs. 2K authority) →
  `hooper-duel-design-reference`.
- **Process discipline** (merge gates, afk/hitl, ADR change control) →
  `hooper-change-control`.

---

## Provenance and maintenance

Authored 2026-07-12; reviewed and corrected 2026-07-15 (the #64/#65 design
forks were already settled by ADR-0009 amendments on 2026-06-27 — the earlier
"open forks" entry trusted stale BallController doc-comments). Verified
against the repo at commit `3085ee1` by direct
`Read`/`Grep` of the cited files (BallController._PhysicsProcess ordering
block read in full; NETCODE LAW, CallLocal, CourtBounds, FloorBounce 0.76,
ShotArc trapezoidal, Main.tscn node order, `player.Name = name`, and
`BeginCommittedMove` call sites all grep-confirmed) — not solely from
discovery digests. Line numbers are deliberately avoided in favor of
class/method names plus grep patterns, because line numbers drift.

Re-verification commands (run from the repo root; quote paths — the repo
path and csproj name contain spaces):

```
# God-node sizes (2785 / 2217 as of 2026-07-12)
wc -l "scripts/Ball/BallController.cs" "scripts/Player/PlayerController.cs"

# NETCODE LAW comment still present
grep -rn "NETCODE LAW" scripts/

# CallLocal=false sites (must include Ball ReceiveState, Player ReceiveState, GameManager ReceiveScoreState)
grep -rn "CallLocal *= *false" scripts/

# Court bounds single source of truth
grep -n "DefaultMin\|DefaultMax" scripts/Ball/CourtBounds.cs

# Floor-bounce restitution constant
grep -n "0.76" scripts/Ball/FloorBounce.cs

# Trapezoidal integration comment + implementation
grep -n "trapezoidal\|0.5f \* (oldVel" scripts/Ball/ShotArc.cs

# Main.tscn node order (Players must appear before Ball)
grep -n 'name="Players"\|name="Ball"\|name="NetworkManager"\|name="DiscoveryBroadcaster"' scenes/Main.tscn

# Peer-id identity contract (Name set before AddChild)
grep -n "player.Name = name" scripts/Networking/NetworkManager.cs

# BeginCommittedMove choke point + all call sites route through it
grep -n "BeginCommittedMove" scripts/Player/PlayerController.cs tests/integration/*.cs

# Per-tick ordering inside BallController._PhysicsProcess
grep -n "_PhysicsProcess\|ResolveBlockAttempts\|ResolveStealAttempts\|ResolvePlayerOutOfBounds\|UpdateClearStatus" scripts/Ball/BallController.cs

# TODO/FIXME/HACK should still be zero in scripts/
grep -rn "TODO\|FIXME\|HACK" scripts/

# Harness seam files still present
ls tests/integration/*HarnessSeam.cs

# Issue states for the owned gaps (#214 block reach, #64/#65 settled forks — expect CLOSED, #104 feel tuning)
gh issue view 214 --json state,title
gh issue view 64 --json state,title
gh issue view 65 --json state,title
gh issue view 104 --json state,title
```

If a re-verification drifts (an issue closes, a constant changes, a file
moves), update this file in the repo's ADR-amendment spirit: append a dated
correction rather than silently rewriting, unless the original fact was
simply wrong.
