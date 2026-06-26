# ADR-0009 — Shot accuracy — distance-based scatter, server-authoritative

- **Status:** Accepted (amended 2026-06-26 — movement penalty #64, contest penalty #65)
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

**Open design question (deferred to human):** The continuous speed-ratio above vs.
a discrete planted/not-planted threshold.  A threshold may fit ADR-0003's
hybrid committed-move model better, where "committed" is a binary state.
Implemented as continuous pending review; the K constant is the balance surface
regardless of which variant wins.

### #65 — defender-contest penalty

```
defDist   = XZ distance from shooter to other player
proximity = clamp(1 - defDist / ContestRange, 0, 1)
contestFactor = 1 + ContestScatterK × proximity
```

If no other player node exists (solo test), `contestFactor = 1`.

New exports on `BallController`: `ContestScatterK` (default `1.0f`),
`ContestRange` (default `2.2f` m).

**Open design question (deferred to human):** Proximity-alone (current) vs.
requiring the defender to be facing or actively closing out.  ADR-0003 earmarks
the full contest/timing mechanic for the timing-window layer; this is the
deliberately-minimal first slice.  The proximity gate must NOT grow into
block/steal logic — that belongs to a later milestone.

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
