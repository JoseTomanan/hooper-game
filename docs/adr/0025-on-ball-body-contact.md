# ADR-0025 — On-ball body contact is a deterministic pre-solver term, not the physics solver

- **Status:** Accepted
- **Date:** 2026-08-15
- **Superseded-by:** —

---

## Context

The design identity is *"the duel is the space between two players"*, and
[CLAUDE.md §1](../../CLAUDE.md) names the spine as **separation creation vs.
denial**. Nobody had ever designed what happens when the two bodies meet.

### What is actually there today

Not "nothing" — an *unchosen* model, which is worse, because it looks like an
absence and behaves like a decision:

- `scenes/Player.tscn:870` declares the `CharacterBody3D` with **no**
  `collision_layer` and **no** `collision_mask`, so both default to layer 1 /
  mask 1. The two players do collide.
- `scenes/Player.tscn:12` declares `CapsuleShape3D_p0vlq` with **zero
  properties** — pure Godot defaults, radius 0.5 m, height 2.0 m. Two players
  therefore cannot approach closer than **1.0 m centre-to-centre**, which is
  wider than real on-ball defense. Nobody chose that number.
- Contact is resolved by Jolt inside `MoveAndSlide()`
  (`scripts/Player/PlayerController.cs:4186`), symmetrically. There is no
  leverage, no absorption, and no notion of who is set and who is moving. The
  driver and the set defender are resolved identically to two capsules bumping
  in a corridor.

The consequences are competitive, not cosmetic. Beating your opponent to the
spot pays off only accidentally. A blow-by is not physically real — #100
shipped the *timing* payoff of a whiffed defensive commitment, but the spatial
half (a body genuinely in the way that genuinely stops you) was undefined. And
the pace/posture batch (#325, #345, #346) is built on the premise that a
defender who reads a line can *occupy* it.

### The finding that forced the ADR's shape

Triage of #347 turned up something the issue itself did not know, and it
inverts the risk analysis.

**Contact is already being predicted client-side, incorrectly, today.**

`PlayerController.cs:1821` lerps the *remote* player's `GlobalPosition` toward
the last server broadcast at `ReconcileLerpRate` (0.3, `:192`). That moves the
`CharacterBody3D` itself, which carries the `CollisionShape3D`. The client's
own player then calls `MoveAndSlide()`, which collides against that capsule. So
the client resolves player-player contact against a body that is **~1 RTT stale
*and* exponentially lagged by the display lerp**, while the server resolves
against true positions. They disagree, every tick two bodies are in contact,
inside the movement step that reconciliation then has to paper over.

"Leave it to `MoveAndSlide`" was therefore never the safe default it appeared
to be. It is a live prediction-divergence source that this ADR removes.

### What the references say

Per [ADR-0014](0014-reference-game-decision-authority.md):

- **Tier 2 (real half-court ball)** — a defender who is set absorbs contact and
  stops the drive; a defender caught moving backward or sideways gets driven
  through. *Legal guarding position* — set feet, facing the man — is the actual
  physical content of on-ball defense. Unambiguous, and the most heavily
  weighted reference available.
- **Tier 3 (*Undisputed 3*)** — momentum carries into commitment; a body at
  speed does not teleport-stop on contact. This repo already adopted that
  pattern once, in #198's gather-bleed.
- **Tier 4 (NBA 2K — taxonomy only)** — supplies the vocabulary ("bump", "ride",
  "body-up", "box out"). Its animation-driven canned-collision *feel* carries
  zero weight and is not imitated.

---

## Decision

**Player-to-player contact is resolved by a deterministic, replayable contact
term computed before `MoveAndSlide()`, and the physics solver is removed from
player-player contact entirely.**

Six sub-decisions, each settled in the 2026-08-15 grilling session on #347.

### 1. Contact is client-predicted, not server-only

The client predicts contact rather than waiting for the server to correct it.

This deliberately departs from the house pattern. Every other
opponent-position-dependent resolution in the repo — both steal windows, block,
blow-by, player-OOB, possession — sits under a single `IsServer` guard at
`scripts/Ball/BallController.cs:1489`, with the result broadcast as discrete
state. **That pattern is correct for those and wrong for this**, and the reason
is the shape of the event, not the shape of the code: a steal is a rare
discrete event, so eating a ~1 RTT correction is invisible. Contact is
*continuous* — two bodies leaning on each other resolve every tick — so a
server-only model would put a visible interpenetrate-then-shove correction on
screen constantly, precisely during the game's core interaction.

Contact is evaluated against the opponent's **raw last-broadcast** transform,
**never** the display-lerped value. The lerp is a cosmetic smoothing filter and
has no business feeding a physics decision; excluding it removes the avoidable
half of the prediction error.

### 2. The opponent's state is passed into `Move()`, not fetched by it

`Move()` is contractually pure — *"no role branches, no network calls, no side
effects"* (`PlayerController.cs:44-48`). The opponent's transform enters as a
**parameter**, supplied by the role-aware caller, because which source is
correct depends on the role (server: true state; client: last broadcast). This
mirrors the existing precedent for `exitVectorSample` (`:3699`), passed in for
exactly this reason.

The parameter is **nullable** — `ContactOpponent?` carrying position, velocity
and heading. Nullable is load-bearing, not stylistic: most harness scenarios are
single-player, and with a non-nullable `Vector3` the absent opponent degrades to
`Vector3.Zero`, which is **centre court** — a position a real player occupies.
Every solo scenario would then silently simulate contact against a phantom body
at the origin and still print PASS. Absent-opponent must be a representable
state the contact math explicitly handles.

### 3. One contact model: the solver is masked out of player-player contact

Player↔player collision is **disabled in the solver** (the player layer is
masked out), and contact becomes *entirely* the pure pre-`MoveAndSlide` term.

Two models running simultaneously would fight — the term pushes, then Jolt
independently depenetrates whatever overlap remains. More decisively, keeping
Jolt as an authority is inconsistent with a locked ADR:
[ADR-0004](0004-deterministic-ball-physics.md) refuses to let Jolt touch
authoritative *ball* state, on the grounds that it is "not guaranteed
deterministic across platforms" (`0004:21-22`), and extends that even to
visual-only effects. Player position is equally authoritative broadcast state.
Hand-authoring the ball's physics to the metre while letting the solver decide
the outcome of the game's core spatial contest is an inconsistency a future
reader would rightly flag.

**The cost is explicit: hard non-overlap stops being free.** A velocity-only
term can be overpowered by a burst, or bypassed by a reconcile snap or a spawn.
Depenetration is therefore **part of the term's job** — it requires a positional
correction component, not merely a velocity one. This is the part of the model
most likely to be got wrong first.

### 4. "Set" = low speed **and** facing — a binary predicate

The asymmetry that makes beating your man to the spot pay off. A player is
**set** when their speed is below a threshold **and** their authoritative
heading lies within a cone toward the contact. This is the direct encoding of
legal guarding position: a defender who is set *and* facing you absorbs; one
backpedalling or turned sideways gets driven through.

Both inputs are already server-authoritative and replayed — `Heading` is
updated inside `Move()`, broadcast in `ReceiveState`, and reconciled
(`PlayerController.cs:515-529`) — so this costs **no new networked state**.

`FacingResolver` must **never** be used for this. It is cosmetic-only by
explicit contract (`FacingResolver.cs:16-19`): derived locally from velocity,
never networked, never authoritative.

**Rejected: a duration-based "set" ("held position for N ticks").** It requires
a per-tick counter in networked state, which is the exact shape this project has
already been burned by — a frame counter compared for equality against a
~1-RTT-stale broadcast force-rewinds under any nonzero latency, which is why
`FrameInPhase` is deliberately excluded from the reconciliation trigger
(`ReconcileFromServer`, `:2626-2631`). Anyone proposing a minimum set-duration
is walking into that; it is named here so the rejection survives.

**Binary, not a continuous scalar,** because **legibility is a stated
competitive requirement** ([CLAUDE.md §1](../../CLAUDE.md)), not a preference. A
continuous "setness" gradient cannot be read across the court in real time; a
hard edge can be learned. Every other resolution in this game is already
cliff-shaped — ADR-0018's timing windows are interval overlaps with hard edges.

### 5. Contact modifies displacement only — never phase, never timing

Contact **never** interrupts, shortens, or re-phases a committed move. It
changes only where the body ends up.

This is a **self-resolution against existing rules, not a new decision.**
[ADR-0003](0003-input-model-hybrid.md) §"External events do not interrupt a
committed move" (#189/#241) already establishes that a committed move runs to
completion even when an external event voids its payload, and that *the lost
time is the punishment*. A set defender absorbing a crossover's separation is
that same rule applied to a different payload: you commit, it plays out, you get
nothing, and the recovery frames are the price.

**Consequence for the wiring:** the committed-move path is mutually exclusive
with `Move()` — `if (_machine.IsActive) TickCommittedMoveBehavior(…) else
Move(…)` (`:1615-1622`). Contact wired only into `Move()` would mean **no
contact during any burst, drive or gather** — and under decision 3, with the
solver masked out, the two players would phase cleanly through each other at
exactly the moment contact matters most. Contact must therefore be applied in
**both** paths.

### 6. Box-out changes where the bodies are — it never touches loose-ball resolution

Box-out is this same primitive applied at the rim, and is settled here rather
than filed separately.

**Prohibition:** a box-out must work *only* by changing player positions. It
must **never** add a term, priority, or tiebreak to loose-ball resolution.
[ADR-0008](0008-possession-rules.md)'s live-rebound rule awards a loose ball to
the nearer player within `PickupRadius`, and its entire determinism argument is
that distance yields a **total order every peer agrees on**. A "box-out
priority" tiebreak would quietly break prediction agreement. This is stated as a
prohibition because it is exactly the kind of thing a later contributor adds in
good faith.

### Scope boundary: no fouls

Fouls, charges, blocking calls, and and-ones are **out of scope**.
[ADR-0008](0008-possession-rules.md)'s #193 amendment already cut travel and the
5-second count as "bare-minimum realism — don't build enforcement nobody asked
for yet", and a foul system is a strictly larger version of the same thing.
**Contact changes movement, not free throws.**

---

## Consequences

### What gets better

- The **live divergence documented above is removed at the root.** The client's
  solver stops resolving contact against a stale lerped capsule because it stops
  resolving player-player contact at all.
- Contact becomes a pure function of two transforms — unit-testable without a
  running Godot instance, in the same family as `MovementMath`, `HeadingMath`
  and `DefensiveResolution`.
- Beating your man to the spot finally pays, which is what makes #325/#345/#346
  mean anything.

### What gets harder, and the numbers

**Prediction error is bounded but not small.** Divergence equals opponent speed
× RTT. `MoveSpeed` is 6.0 m/s with no `.tscn` override
(`PlayerController.cs:106`), so against a *set* defender:

| RTT | Positional error | vs. capsule radius (0.5 m) |
|-----|-----------------|---------------------------|
| 50 ms | 0.30 m | 60 % |
| 100 ms | 0.60 m | 120 % |

The error is the same order as the body being collided with, and it is worst in
the drive-into-a-set-defender case that motivates the feature. This is accepted,
not solved — see the upgrade path below.

**Two known limitations, recorded so they are not rediscovered as bugs:**

1. **Replay does not reproduce committed-move contact.** The reconciliation loop
   calls `Move()` **unconditionally** (`:2801`), and `_buffer.Record()` sits
   *before* the `IsActive` branch (`:1762`) — so committed-move ticks are
   recorded and then replayed through the wrong path. This asymmetry
   **pre-exists** this ADR and may be an accepted approximation (Step 0 corrects
   only one specific divergence and smoothing absorbs the rest), but contact
   inherits it: whatever contact does inside `TickCommittedMoveBehavior` is not
   reproduced during reconciliation. Needs its own issue.
2. **Under B, all replayed ticks use one opponent transform.** The client has
   only the latest broadcast, so an N-tick replay evaluates contact against the
   same opponent position N times rather than against the position each tick
   actually had.

**Tuning risk, not an architecture problem:** the binary `set` predicate admits
a tap-stop-to-re-set degenerate strategy. The heading term mitigates it — you
cannot tap-stop *and* face correctly while retreating — but does not eliminate
it. The clean fix is a minimum set-duration, which is decision 4's rejected
option. Logged for **#238** (consolidated tuning).

### The upgrade path (recorded, not built)

**Snapshot history / lag compensation** is the correct answer to the error
above: ring-buffer the opponent's per-tick broadcast transform and, during
replay, evaluate contact against the transform for *that* tick. It is correct by
construction and cheaper than it sounds, since those broadcasts already arrive
per-tick. CONTEXT.md already names *lag compensation* as a term from
[ADR-0002](0002-networking-server-authoritative.md).

It is deliberately **not** built now. Decisions 2 and 3 make it a
strictly-additive change later — once contact is already a pure function of two
transforms, swapping where the second transform comes from touches one call
site. Building it up front would be speculative.

### Follow-on work

This ADR decomposes into sub-issues following the #203 → ADR-0022 →
#229/#230/#231 pattern. **One of them is a prerequisite for closing any of the
others:**

- **Harness contact fixture (prerequisite).** The harness currently *cannot
  observe contact at all*. The two-player precedent builds bare
  `new PlayerController()` (`tests/integration/BlowByWindowTest.cs:112`) with no
  `CollisionShape3D` — the shape lives in `Player.tscn` — so those bodies pass
  through each other today. Under
  [ADR-0016](0016-headless-verification-harness.md) ("proven by harness"), no
  contact sub-issue can be closed until a fixture exists that can see contact
  happen. Every gate on it needs a **control scenario** proving it fails when
  contact is removed.
- The contact term itself (pure math + unit tests).
- Solver masking + the depenetration component of decision 3.
- The `set` predicate and its asymmetric leverage.
- Wiring into `TickCommittedMoveBehavior` (decision 5).
- Box-out at the rim (decision 6), with a gate proving loose-ball resolution is
  *unchanged*.
- The replay-path asymmetry (limitation 1), as its own investigation.

### Rejected alternatives, in one place

| Alternative | Why rejected |
|---|---|
| **Leave it to `MoveAndSlide`** | Not a null option — it is the live divergence source documented above |
| **Server-only contact** | Correct for discrete events, wrong for a continuous one; constant on-screen correction |
| **Jolt as non-overlap backstop** | Inconsistent with ADR-0004; keeps the client resolving against a stale body |
| **Duration-based `set`** | New networked frame counter — the `FrameInPhase` trap by another name |
| **Continuous `set` scalar** | Unreadable in real time; violates the legibility requirement |
| **Canned 2K-style contact animations** | ADR-0014 gives 2K zero weight on feel; animation-driven collision is the arcade-decoupling anti-goal |
| **A foul model** | Strictly larger version of what ADR-0008 #193 already cut |
