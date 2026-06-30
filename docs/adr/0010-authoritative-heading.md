# ADR-0010 — Player heading: server-authoritative, bounded non-linear turn rate

- **Status:** Accepted
- **Date:** 2026-06-27
- **Superseded-by:** —

---

## Context

Before this decision the player's visual facing was computed entirely from
Velocity via `FacingResolver.ResolveYaw(velocity, currentYaw)` — a
cosmetic-only, client-local calculation that was never networked or replayed
(ADR-0004's cosmetic-only rule). The player could therefore reverse heading
180° in a single physics tick with no cost.

An instant 180° pivot is the clearest example of *arcade decoupling* — the
primary anti-goal of ADR-0003:

> "Action that floats free of physical constraint … any move that can be started
> and freely cancelled. This is the arcadey feel we are avoiding. It fails twice
> over — it looks unreal *and* it erases the commitment frames the duel is built
> on."

A reverse-pivot that costs nothing cannot be read or punished; it silently
destroys the spacing/commitment mind-game that is the game's design identity.

### Forces at play

1. **ADR-0003 legibility requirement.** A committed body direction must have
   visible startup. A 180° back-turn is the largest possible commitment —
   it must also be the slowest and most readable.

2. **Micro-aim must stay fluid.** ADR-0003 allows continuous flow on the
   left analog stick. A *uniform* turn-rate cap would make the stick feel
   sluggish for small corrections even though they cost no commitment; that
   trades one problem for another. The rate must be non-linear.

3. **Server authority (ADR-0002).** If facing is cosmetic-only, a client can
   draw its own player facing a different direction than the server believes it
   faces, and the server cannot enforce punish windows based on orientation.
   Issue #81 (facing-based shot accuracy) specifically needs the server's
   opinion of the player's heading at shot release — making heading authoritative
   is a prerequisite.

4. **Determinism (ADR-0004).** The heading update runs inside `Move()`, the
   shared server-authority / client-prediction / reconciliation-replay step.
   `HeadingMath.RotateToward` is pure, uses only `MathF`, and produces the
   same result on every platform for identical inputs — identical reasoning to
   `MovementMath.ComputeVelocity`.

### Alternatives considered

1. **Instant pivot (status quo).**
   Rejected. Instant 180° reversal is arcade decoupling; it erases the
   commitment cost that ADR-0003 requires.

2. **Cosmetic-only via FacingResolver, but with a turn-rate cap.**
   Rejected. A cosmetic cap would smooth the *display* but leave the server
   blind to facing. The server could not use heading for shot accuracy (#81),
   and two clients could legitimately disagree on the player's facing without
   either being wrong — the server has no truth to adjudicate.

3. **Aim-stick / explicit right-stick facing (Strafing model).**
   Rejected for the movement stick. ADR-0003 is explicit: the left analog
   stick drives movement, not an independent aim direction. Decoupling
   movement direction from facing would break the spacing spine by allowing
   a player to run in one direction while presenting a different threat angle.
   The heading follows the movement intent; there is no separate aim channel
   at the movement layer (committed-move gestures live on the right stick).

4. **Uniform linear turn rate.**
   Rejected. A fixed cap would slow small corrections equally, making the
   neutral movement stick feel sticky — an ADR-0003 violation in the other
   direction (manufactured clunk, the secondary anti-goal). The non-linear
   schedule (continuous lerp between full rate at diff=0° and minimum rate
   at diff=180°) gives micro-corrections near-free cost while maximising
   the commitment penalty exactly where it matters: the back-turn.

## Decision

Track `Heading` (Y-rotation in radians) as **server-authoritative state**,
updated every tick inside `Move()` via `HeadingMath.RotateToward`. Broadcast
it in `ReceiveState` alongside `pos/vel`. Snap it to the authoritative value
before the reconciliation replay loop (same treatment as `GlobalPosition` and
`Velocity`). The client's display yaw is read directly off `Heading`, replacing
`FacingResolver.ResolveYaw(Velocity, …)`.

The turn rate is **non-linear**: `MathF.Lerp(maxTurnRateDeg, maxTurnRateDeg ×
backTurnSlowFactor, |diff|/π)`. Default values:

| Export | Default | Reasoning |
|--------|---------|-----------|
| `MaxTurnRateDeg` | 530 °/s | At `BackTurnSlowFactor` 0.35, a 180° back-turn takes ≈ 0.55 s (integrated time of the non-linear schedule — the constant-rate 180/(rate×f) estimate overestimates because the rate accelerates as the diff closes). A 20° correction takes ≈ 0.05 s — effectively instant to a human player. **Retuned 400 → 530 (#134, 2026-06-30): "snappier is better" — see amendment below.** |
| `BackTurnSlowFactor` | 0.35 | The back-turn is ~3× slower than a micro-correction. Chosen so the pivot is legibly slow without being so slow it feels broken. Designer-tuneable via Inspector export. |

## Consequences

**Easier:**
- The back-pivot is now a visible commitment that can be read, anticipated,
  and punished — restoring the spacing/commitment mind-game (ADR-0003).
- Heading is on the wire, so the server can read it for orientation-dependent
  systems (shot accuracy, #81; future: pass angle, block arc, etc.).
- `HeadingMath` is pure and unit-testable without Godot; netcode correctness
  is verifiable in CI exactly like `MovementMath`.

**Harder / accepted tradeoffs:**
- `ReceiveState` gains one additional `float` argument. This is a low-cost
  change: `ReceiveState` already carries pos, vel, phase, frameInPhase,
  moveId, and moveParam; one more float fits within the same UnreliableOrdered
  datagram and adds no new RPC channel.
- The reconciliation replay now snaps `Heading` before the replay loop,
  matching the pos/vel snap. This is the correct treatment but must not be
  forgotten when adding new authoritative state fields in the future.
- `FacingResolver` is left in the codebase but is no longer called on the
  main display path. It is retained for historical reference; removing it is
  a clean-up that can happen in a later commit.
- Because `Heading` is updated in `Move()` but not in `TickCommittedMoveBehavior`,
  the heading does not advance during the Active or Recovery phases of a
  committed move (those skip `Move()`). This is intentional: feet are planted
  during a committed move, so the heading should not drift mid-burst. If a
  future move requires heading updates during Active/Recovery, `RotateToward`
  can be called explicitly in `TickCommittedMoveBehavior` for that phase.
- Determinism caveat (ADR-0004): `HeadingMath.RotateToward` uses `MathF.Atan2`,
  a transcendental not guaranteed by IEEE-754 to be bit-identical across
  heterogeneous architectures (unlike `MovementMath`, which uses only basic
  arithmetic). In practice this is bounded and safe: the shot-accuracy outcome
  (#81) reads the SERVER's authoritative `Heading` only, and clients snap
  `Heading` to the broadcast value before the reconciliation replay — so any
  cross-arch drift in client prediction is corrected within the
  unacknowledged-input window and never affects an authoritative outcome.

## Amendments

### 2026-06-30 — `MaxTurnRateDeg` default ratified at 530 °/s (#134)

The original Decision table specified `MaxTurnRateDeg = 400 °/s` (≈ 0.75 s
back-turn). The shipped export had drifted to **530 °/s** (≈ 0.55 s back-turn),
and `HeadingMathTests` hardcoded the stale 400 figure — so the ADR's documented
pivot timing, the test's explanatory math, and the running game disagreed (#134).

**Decision:** ratify **530 °/s** as the default. The human design call was
explicit — *"snappier is better"*. 530 keeps the back-turn a readable commitment
(it is still ~3× slower than a micro-correction via the unchanged
`BackTurnSlowFactor = 0.35`), so the ADR-0003 legibility requirement is preserved;
the faster nominal rate only makes neutral micro-aim feel more responsive.

**Rejected alternative:** revert the export to 400 °/s to match the original
table. Rejected — the 530 value is the one that has been playtested in-feel and is
preferred; the table and test were the stale artifacts, not the code. The fix is
to bring the documentation and test constant up to the shipped value, not to slow
the game back down.

This amendment is a tuning-default change, not a structural reversal: the
server-authoritative, non-linear, integrated-in-`Move()` heading model decided
above is unchanged. Status remains **Accepted**.
