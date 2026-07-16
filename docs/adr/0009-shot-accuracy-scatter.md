# ADR-0009 — Shot accuracy — distance-based scatter, server-authoritative

- **Status:** Accepted (amended 2026-06-26 — movement penalty #64, contest penalty #65; design questions resolved 2026-06-27; amended 2026-06-27 — facing penalty #81; amended 2026-07-16 — committed on-ball contest #99)
- **Date:** 2026-06-26
- **Superseded-by:** —

---

## Context

Every shot currently makes by construction: `ShotArc` is built with `ShotTarget`
(the rim centre) as its target, so a shot released within the committed-move
timing window always produces a clean arc to the basket.  M8's design pass
(epic #61) calls for shots to be able to miss, with the probability of missing
growing with shot distance — a long two-pointer should be harder to make than a
layup.  Issue #62 tracks this feature.

This is the first time the project draws random numbers at runtime.  That creates
a determinism risk: ADR-0004 requires the ball's trajectory to be bit-identical
on server and every client, and ADR-0002 requires the server to be the single
authority for all game-state outcomes.  Getting randomness wrong here would break
both constraints at once — clients would disagree on whether a shot is in-flight
toward the basket or toward an offset target, producing diverging simulations that
reconciliation cannot fix within one RTT.

The forces:

- **Determinism (ADR-0004).** Any RNG draw that affects the ball trajectory must
  produce the same result in the authoritative simulation.  The authoritative
  simulation runs on the server, so the draw must be server-owned and seeded.  A
  draw on the client would produce a different result, and a shared unseed draw
  would diverge across peers at the first shot.
- **Predict-then-reconcile (ADR-0002).** Client prediction already aims
  dead-centre; the reconcile path (`ReconcileFromServer`) snaps the client's
  in-flight arc to the server's position+velocity snapshot every tick.  That
  mechanism already exists and already handles any divergence between the client's
  predicted arc and the server's authoritative arc — scatter is just a new source
  of such divergence, and the existing path absorbs it within ~1 RTT.
- **Testability (ADR-0004 headless-seam discipline).** The scatter geometry is a
  pure function of its inputs; mixing RNG state into it would break headless unit
  testing.  The RNG samples must be injected by the caller, not generated inside
  the helper.

## Decision

Add server-only, seeded, distance-based scatter to shot trajectories via two
components:

**1. `ShotScatter` — pure static helper.**
A pure static class (`scripts/Ball/ShotScatter.cs`) with a single method:

```csharp
public static Vector3 Scatter(
    Vector3 target, float distance,
    float angle01, float radius01,
    float scatterPerMeter, float maxScatter)
```

The two `[0,1)` random samples (`angle01`, `radius01`) are **injected by the
caller** — there is no `Random` instance or engine singleton inside this class.
The helper converts them into an XZ-plane offset via uniform-disc sampling
(`r = min(scatterPerMeter × distance, maxScatter) × sqrt(radius01)`,
`theta = 2π × angle01`), leaving Y unchanged.  This keeps the helper a pure
deterministic function: headless unit-testable, zero dependency on execution
context.

**2. `BallController` — server-gated draw.**
`BallController` owns a seeded `System.Random _shotRng` (seeded from the exported
`ShotScatterSeed` in `_Ready`).  In `ApplyShootLocally`, only when
`IsServer && ShotScatterEnabled`, the XZ distance from `GlobalPosition` to
`ShotTarget` is computed, two samples are drawn from `_shotRng`, and
`ShotScatter.Scatter` produces the `aimTarget` the arc is built from.  Clients
always use `ShotTarget` dead-centre; `ReconcileFromServer` (which already runs
every in-flight tick) snaps them to the server's arc within ~1 RTT.

**3. New exports (all on `BallController`):**
- `ShotScatterEnabled` — master switch, **defaults `false`** so all existing
  behaviour is preserved until scatter is play-tested.
- `ShotScatterPerMeter` — scatter radius per metre of shot distance (default
  `0.03f` m/m; a 5 m shot produces a ~0.15 m raw radius).
- `MaxShotScatter` — hard cap on offset radius (default `0.4f` m, ~1.7× the rim
  radius), preventing very long shots from producing absurd multi-metre misses.
- `ShotScatterSeed` — integer seed for `_shotRng` (default `12345`), editable in
  the Inspector to test different miss distributions without recompiling.

## Consequences

**Easier / what this buys:**

- Shots can now miss.  The miss probability grows naturally with distance through
  the `scatterPerMeter` tunable, creating a distance-cost the offense must factor
  into shot selection — the design intent of issue #62.
- No new netcode.  The server-draw + client-predict + reconcile-snaps pattern
  reuses the existing `ReconcileFromServer` path that already runs every in-flight
  tick (ADR-0002).  A scattered arc looks to the reconcile logic exactly like any
  other position divergence.
- Testable without a running engine.  `ShotScatter` follows the headless-seam
  discipline (ADR-0004): the unit tests in `ShotScatterTests.cs` cover zero-
  distance no-op, cap enforcement, determinism, radius-scales-with-distance, and
  Y-preservation — all without instantiating a Node.

**Harder / accepted tradeoffs:**

- **The defaults are tuned, not just guessed** (see *Tuning* below).  They were
  fitted to a real make-percentage curve via simulation and are now enabled by
  default (`ShotScatterEnabled = true`).  They remain exported balance surface —
  play-testing may still adjust feel — but they are no longer placeholders.
- **`ShotScatterSeed` makes reproducibility explicit but also visible.**  A
  sufficiently motivated player who can inspect exported values could predict miss
  patterns for a given seed.  For a 1v1 competitive game, this is acceptable: both
  players see the same physics; the seed is not a secret.  If it becomes an issue
  it can be randomised at game-start and broadcast once — a later decision.
- **Scatter is applied at shoot time only**, not adjusted for fatigue or
  defender proximity.  Proximity-based accuracy degradation (issue #65) is
  explicitly deferred — this ADR establishes the scatter mechanism it will extend.

**Rejected alternatives:**

- **Shared seed broadcast to clients, clients predict scatter.**  This would
  remove the ~1-RTT latency before a client sees the scattered arc (the client
  could draw the same offset immediately).  Rejected because it requires a new
  RPC to broadcast the seed or sample before each shot, adding netcode complexity
  and a new authority-synchronisation point for the sole benefit of eliminating
  one reconcile snap — a snap that already exists and already works.
- **Flat random miss chance** (coin flip: either a perfect make or a complete
  miss).  Rejected: it has no notion of distance, so a long shot and a layup are
  equally risky.  That removes shot-selection depth, which the spacing/commitment
  duel (CLAUDE.md §1) depends on.
- **Proximity / defender pressure scatter** (issue #65).  Not rejected — deferred
  at #62 time; implemented in the amendment below.
- **Engine randomness (`GD.Randf`, `RandomNumberGenerator`).**  Rejected on
  determinism grounds (ADR-0004): Godot's random API is not guaranteed to be
  seeded per-node, and mixing it with the ball mini-physics would introduce a
  non-injected RNG source that cannot be tested headlessly.

---

## Amendment — movement penalty (#64) and contest penalty (#65)

**Date:** 2026-06-26

Two additional accuracy penalties compose multiplicatively with the base distance
scatter established above.  Both are computed ONLY inside the existing
`if (IsServer && ShotScatterEnabled)` block — client prediction is unchanged, and
`ReconcileFromServer` absorbs the divergence as before.  No new netcode.

### Composition model

`ShotScatter.Scatter` gains an `accuracyMultiplier` parameter (default `1.0f`).
The cap (`maxScatter`) is applied to the base radius FIRST; the multiplier is
applied AFTER the cap.  This means penalty stacking can intentionally push the
final offset beyond `maxScatter` — a moving, closely-contested shot is genuinely
harder, not just harder up to the same ceiling as an open stationary shot.

```
accuracyMultiplier = movementFactor × contestFactor

r = min(scatterPerMeter × distance, maxScatter) × accuracyMultiplier × sqrt(radius01)
```

The multiplier is finite because both K-scaled ratios are clamped to [0, 1].

### #64 — movement-stillness penalty

```
speedRatio     = clamp(holder.Velocity.Length() / holder.MoveSpeed, 0, 1)
movementFactor = 1 + MovementScatterK × speedRatio
```

New exports on `BallController`: `MovementScatterK` (default `1.0f`).

**Resolved (2026-06-27): continuous speed-ratio penalty.**  The discrete
planted/not-planted threshold alternative was rejected.  Rationale: shot stillness
is a property of the *analog-movement* half of ADR-0003's hybrid input model, not
its committed-move half.  Movement speed is already a continuous, server-authoritative
quantity (`holder.Velocity.Length()`), so a continuous penalty reads the system as it
actually is — every increment of speed costs a little accuracy.  A discrete "planted"
state would only be honest if it were tied to genuine committed-move state in the
`CommittedMoveMachine`, which would (a) conflate the spacing/footwork spine with the
commitment layer and (b) expand scope into ADR-0003 — explicitly out of bounds for
this minimal accuracy slice.  The `MovementScatterK` constant remains the sole balance
surface.

### #65 — defender-contest penalty

```
defDist   = XZ distance from shooter to other player
proximity = clamp(1 - defDist / ContestRange, 0, 1)
contestFactor = 1 + ContestScatterK × proximity
```

If no other player node exists (solo test), `contestFactor = 1`.

New exports on `BallController`: `ContestScatterK` (default `1.0f`),
`ContestRange` (default `2.2f` m).

**Resolved (2026-06-27): proximity-alone.**  Requiring the defender to be facing or
actively closing out was rejected for this slice.  Rationale: a facing-based contest
would need the defender's *orientation*, and the only orientation the project has today
is `scripts/Player/FacingResolver.cs`, which ADR-0004 makes explicitly cosmetic,
client-side, and never authoritative.  Reading it to decide a make/miss would make an
authoritative outcome depend on cosmetic state — a direct ADR-0004 violation.  Honest
facing-based contest would require deriving a *server-authoritative* orientation (e.g.
from the defender's authoritative movement/aim vector), which is new scope and belongs
with the full contest/timing mechanic ADR-0003 earmarks for the timing-window layer.
Proximity is the deliberately-minimal first slice and must NOT grow into block/steal
logic — that belongs to a later milestone.

---

## Tuning — how the default magnitudes were chosen

**Date:** 2026-06-26

The scatter and floor-bounce defaults are not guesses; they were fitted to real
basketball numbers and verified by simulating the **actual** deterministic
physics (`ShotScatter` → `ShotArc` → `RimBackboard`) over a deterministic
stratified sweep of the scatter sample space.  The sweep is preserved as a
regression test (`tests/Hooper.Ball.Tests/ShotMakeCurveTests.cs`), so retuning a
constant without updating the expected bands fails the build.

**Key insight.** A shot makes iff the scattered aim point lands inside the
inner-rim radius `RimRadius − BallRadius = 0.11 m` (a rim graze sends the ball
`Loose`, which is not re-checked for a make).  For uniform-disc sampling this
gives a closed form, `make% ≈ (0.11 / min(perMeter·distance, maxScatter))²`,
which the simulation confirmed — and refined: flat-arc shots near the boundary
rim out slightly more than the closed form predicts, which the test captures.

**Resulting open (uncontested, stationary) make curve** with
`ShotScatterPerMeter = 0.026`, `MaxShotScatter = 0.45`:

| Distance | Make% | Real-world anchor |
|----------|-------|-------------------|
| ≤ 3 m    | ~100% | open layup — automatic |
| 5 m      | ~67%  | open mid-range |
| 5.8 m    | ~53%  | at the clear line |
| 6.75 m   | ~41%  | NBA wide-open three ≈ 38–40% |
| 10 m     | ~21%  | steep falloff rewards spacing |

Penalties (`MovementScatterK = 0.8`, `ContestScatterK = 1.0`,
`ContestRange = 2.2 m`) compose onto that curve: an open 5 m shot (~67%) drops to
~43% contested, ~35% on the move, ~22% both.  Close shots stay forgiving unless
*both* moving and contested — a sprinting, tightly-contested 2 m shot is the only
way to miss point-blank, which reads as a genuinely bad decision rather than
random punishment.

**Floor bounce** (`FloorRestitution = 0.82`, `FloorHorizontalDecay = 0.9`,
`FloorSettleSpeed = 0.6`): an NBA ball on hardwood rebounds to ~1.22 m from a
1.8 m drop ⇒ COR ≈ √(1.22/1.8) ≈ 0.82.  Simulated, 0.82 gives a realistic
first rebound (~125 cm) decaying over ~15 ever-smaller bounces, versus the dead
single thud a low value produced.

---

## Amendment — facing penalty (#81)

**Date:** 2026-06-27

### What changed

A third accuracy factor — `facingFactor` — is added to the multiplicative
model.  The composition becomes:

```
accuracyMultiplier = movementFactor × contestFactor × facingFactor

facingFactor = 1 + FacingScatterK × (angle / π)

where angle = shortest angular distance in [0, π] between
              holder.Heading and the direction to ShotTarget
```

Implementation lives in `scripts/Ball/ShotFacing.cs` — a new pure static
class following the same headless-seam discipline (ADR-0004) as `ShotScatter`.
The factor is computed at the call site in `BallController.ApplyShootLocally`
and passed into `ShotScatter.Scatter`; `ShotScatter` itself is unchanged.

### Why holder.Heading, not FacingResolver (the ADR-0004 unblocking)

Issue #65 noted that a facing-based contest penalty would require the
*defender's* orientation, and the only orientation available at the time was
`FacingResolver` — explicitly cosmetic-only and client-local (ADR-0004).
Reading it to decide a make/miss would make an authoritative outcome depend on
cosmetic, non-replicated state.

Issue #80 (ADR-0010) elevated the *shooter's* heading to server-authoritative
state: `PlayerController.Heading` is updated inside `Move()`, replayed during
reconciliation, and broadcast alongside pos/vel.  This gives the server an
honest, authoritative orientation that can feed an outcome.  Issue #81 uses it.

The rule from ADR-0004 holds: `FacingResolver` is never read on the authority
path.  The penalty reads `holder.Heading` (ADR-0010) exclusively.

### Balance surface

New export on `BallController`: `FacingScatterK` (default `0.8f`).

At `FacingScatterK = 0.8`:

| Facing angle | facingFactor |
|---|---|
| 0° (squared up) | 1.00 |
| 45° | 1.20 |
| 90° (side-on) | 1.40 |
| 135° | 1.60 |
| 180° (back-to-basket) | 1.80 |

The back-to-basket penalty (1.80×) is deliberately below the full on-ball
closeout penalty (`ContestScatterK = 1.0` → 2.0×).  A turnaround fadeaway
with no defender is harder than an open squared-up shot but not as punishing
as a fully-contested shot — matching basketball intuition.  The two penalties
compose: a moving, contested, back-to-basket shot stacks all three factors.

### Cross-references

- ADR-0010: elevates `Heading` to server-authoritative state — the prerequisite
  for this penalty.
- Issue #80: implements ADR-0010 (the `Heading` property on `PlayerController`).
- Issue #81: this amendment.
- `ShotScatter` is unchanged; the factor is computed and composed at the call
  site, keeping `ShotScatter` agnostic to the source of the multiplier.

---

## Amendment — committed on-ball contest (#99)

**Date:** 2026-07-16

### What changed

A fourth accuracy factor — `contestMoveFactor` — is added to the
multiplicative model. The composition becomes:

```
accuracyMultiplier = movementFactor × contestFactor × contestMoveFactor × facingFactor

contestMoveFactor = 1 + ContestMoveScatterK   if the defender's committed
                                                ContestMove is Active on the
                                                exact tick the shot releases
                     1                          otherwise
```

This is the concrete factor shape ADR-0018 §2 named but deferred: "a
committed contest whose Active overlaps the shot's release window applies an
additional, discrete accuracy penalty on top of the passive distance/contest
scatter... getting that composition right (no double-counting) is the
contest issue's explicit job." #99 is that job; this amendment records the
composition it actually implements, per CLAUDE.md Decision Discipline (a
change to this ADR's locked accuracy model must be recorded here in the same
PR as the code, not just referenced from ADR-0018).

Implementation lives in `DefensiveResolution.ContestAppliesAt` (the timing
gate) and `DefensiveResolution.ContestMoveFactor` (the factor itself) —
`scripts/Ball/DefensiveResolution.cs` — computed at the call site in
`BallController.ApplyShootLocally` and multiplied into the existing chain;
`ShotScatter` itself is unchanged, exactly as the facing-penalty amendment
above established for this composition pattern.

### Why an ADDITIONAL factor, not a replacement of `contestFactor`

ADR-0018 §2 is explicit that contest composes with — never replaces — the
existing passive proximity term (#65's `contestFactor`, itself unchanged by
this amendment). The two factors model genuinely different things: `contestFactor`
is a continuous, passive consequence of simply standing near the shooter
(no committed cost); `contestMoveFactor` is the consequence of a defender
**spending a committed move** (Startup telegraph, Recovery punish window) to
actively pressure the release. Multiplying them together means a shot that is
both passively crowded AND actively pressured is harder than either alone —
matching the real-ball intuition that active pressure is strictly stronger
than passive proximity (ADR-0014 tier 2), without discarding the proximity
term #65 already earned.

### Why the composition collapses to a single-tick timing gate

Unlike block (whose vulnerable window spans `BlockGraceTicks` after release,
because the ball keeps rising near the defender for several ticks after it
leaves the hand), contest's effect on the shot can only ever be applied at
ONE moment: shot scatter (this ADR) is computed exactly once, inside
`ApplyShootLocally`, at the instant of release. There is no later
recomputation to apply a later-arriving contest to. So the "release window"
ADR-0018 §2 describes for contest is, in this implementation, the single
release tick itself:
`DefensiveResolution.Succeeds(contestActiveStart, contestActiveEnd, releaseTick,
releaseTick + 1)` — algebraically "is the defender's ContestMove Active right
now." `ContestAppliesAt` routes through the same shared `Succeeds` overlap
predicate steal and block use (ADR-0018 §1) rather than inlining that
equivalence directly, so contest's resolution stays auditable against the one
shared timing primitive, and stays open to a future multi-tick release
window without a call-site rewrite.

### Why no spatial reach gate (contrast block's #214 amendment)

Block was separately amended (2026-07-16, ADR-0018) to require
`BlockReachRadius` proximity in addition to its timing overlap, because block
grants a binary succeed/fail and a spatially unconditioned block let a
defender anywhere on the court connect. Contest never grants a binary
success — it only scales an ALREADY-proximity-gated term (`contestFactor`
already requires the defender be within `ContestRange` to have any effect).
Composing a second, independent spatial gate specifically on `contestMoveFactor`
would double-count proximity with no basis in ADR-0018 §2's text, which
describes contest's composition as a function of Active-overlaps-release-window
only. If a future review wants an active contest to ALSO require close-range
presence independent of the passive term, that is a new decision, not this
one.

### Balance surface

New export on `BallController`: `ContestMoveScatterK` (default `0.5f`,
provisional). ADR-0014 citation (real half-court ball, tier 2): a defender
who actively closes out and times their pressure to the release is strictly
harder to shoot over than one who merely stands nearby, so the additional
factor should be strictly greater than 1 whenever it applies — 0.5 (an extra
1.5× on top of whatever `contestFactor` already contributed) is a citable
starting point, not a locked balance value. Exact magnitude tuning is
deferred to #104 + the per-milestone feel pass (ADR-0015), same as every
other penalty constant in this ADR.

New committed move: `ContestMove` (`scripts/Input/ContestMove.cs`), frame
data 6/8/20 (Startup/Active/Recovery) — provisional, same tuning deferral.

### Cross-references

- ADR-0018 §2: names contest's composition rule ("additional, on top of
  passive") — this amendment records the concrete factor shape.
- ADR-0018's 2026-07-16 amendment (#214): the sibling block-reach gate this
  amendment's "why no spatial reach gate" section contrasts against.
- Issue #99: this amendment.
- Folded-forward cleanup from PR #220's review (issue #99 comment): the
  shared `DefensiveResolution.DistanceXZSquared` XZ-distance helper, and
  `WithinBlockReach`'s switch to a squared-distance comparison, both landed
  in the same PR as this amendment — see `DefensiveResolution.cs`.
