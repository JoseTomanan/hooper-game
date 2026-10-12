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
| `MaxTurnRateDeg` | 900 °/s (see Amendment 2026-07-03 — #172 retune) | At `BackTurnSlowFactor` 0.95, integrated 180° back-turn time is `(180/900)·ln(1/0.95)/(1−0.95) ≈ 0.205 s` — near-linear, the back-turn tax now almost negligible. **Retuned 400 → 530 (#134, 2026-06-30), then 530 → 900 (#172 follow-up feel pass, 2026-07-03) — see amendments below.** |
| `BackTurnSlowFactor` | 0.95 (see Amendment 2026-07-03 — #172 retune) | The back-turn is now only mildly slower than a micro-correction — a near-linear finishing touch. Chosen so the pivot is legibly slow without being so slow it feels broken. Designer-tuneable via Inspector export. |

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

### 2026-07-03 — Flick-to-latch in-place pivot + `BackTurnSlowFactor` re-split (#172)

Issue #172 revisited the 180° back-turn feel again, this time from the human's
NBA-2K reference rather than a raw-rate complaint: a snap-turn that resolves in
place, plants the feet, and commits the player for a beat reads more like the
2K pivot than a uniformly slow turn does. Two changes landed together, both
extending — not reversing — the model this ADR decided:

1. **A new predicted+reconciled `PivotState`** (`HeadingMath.PivotState` /
   `HeadingMath.Step`, `scripts/Player/HeadingMath.cs`). A facing change past
   `PivotThresholdDeg` (new export, default 90°) now *latches*: the player is
   planted (`IsPivotingInPlace`) every tick from the moment the latch is
   created until the latched facing is actually reached, re-latching onto a
   held stick every tick so a moving target keeps dragging the pivot with it.
   Releasing the stick mid-pivot (a "flick") does not cancel the pivot — it
   keeps resolving to completion, which is what makes a quick flick-and-release
   read as a real committed pivot rather than a canceled gesture. `_pivot` is
   carried on `PlayerController` with **exactly the same treatment `Heading`
   already has**: advanced inside `Move()`, broadcast on `ReceiveState`
   alongside `pos/vel/Heading`, and snapped to the authoritative value before
   the reconciliation replay loop (see `ReconcileFromServer`'s pivot-snap
   comment, immediately following its `Heading` snap). This is a structural
   *extension* of the pattern this ADR established, not a new one — no new
   ADR is warranted for "one more piece of state gets the Heading treatment."
   Below the threshold, movement is never gated — a turn continues to resolve
   exactly as `RotateToward` always has.

2. **`BackTurnSlowFactor` re-split 0.35 → 0.90.** Before this issue, a single
   knob (`BackTurnSlowFactor`) had to carry two jobs at once: "how much slower
   is a back-turn than a micro-correction" *and* "how legible/committed does
   a back-turn feel." The new plant-then-pivot gate above now carries the
   *commitment* read on its own — a planted body that cannot move is
   unmistakably legible regardless of how fast the yaw itself turns — so the
   raw rate no longer needs to be as punishing. Raising `BackTurnSlowFactor`
   to 0.90 (near-linear: a 180° back-turn is now only mildly slower than a
   micro-correction) combined with the pivot gate brings a full 180° reversal
   down to **≈0.35 s**, from the pre-#172 figure of ≈0.55 s (itself the 530°/s
   `MaxTurnRateDeg` amendment above). The net feel is *faster resolution, but
   now with an honest plant* — arcadeness allowed (per the human's explicit
   #172 framing, "arcadeness allowed, competitiveness deferred" — see the
   ADR-0003 amendment below for the full design-authority note), not a return
   to the pre-ADR-0010 instant-pivot the Alternatives section above rejected:
   the plant still costs real position (`Velocity` forced to
   `Vector3.Zero` while `IsPivotingInPlace`), so the back-turn remains a real,
   observable commitment — the cost just moved from "slow yaw" to "frozen
   feet," which is the more legible signal of the two.

**Re-proposed and re-rejected alternative: visual-only heading (this ADR's
Alternative #2).** In #172 triage the human re-raised making heading
cosmetic-only again — the same alternative rejected above in 2026-06-27 — on
the theory that a purely client-local pivot animation might be enough now that
the plant is the primary legibility signal. **Rejected again, for the same
reasons that stood the first time and one new one:** issue #81's shot-accuracy
read and #96/#175's steal-timing resolution both still need the server's
opinion of where the player is actually pointing (Forces #3 above), and #172's
own pivot-cancel rule (`BeginCommittedMove` clears `_pivot` the instant a
committed move begins, so a defender cannot exploit a stale pivot latch to
freeze through a punish window) is itself only enforceable because the pivot
latch is server state, not a client-local animation flag a tampered client
could ignore. Heading (and now `_pivot` with it) stays authoritative.

This amendment adds a new predicted+reconciled field and re-tunes a default;
it does not reverse the Decision or any Alternative rejection above. Status
remains **Accepted**.

### 2026-07-03 — `MaxTurnRateDeg` 530 → 900, `BackTurnSlowFactor` 0.90 → 0.95 (#172 follow-up feel pass)

The #172 retune above brought the 180° reversal to ≈0.35 s; on feeling it in
motion the human judged that still too slow against the NBA-2K reference and
asked for ≈0.20 s (same ADR-0014 design authority, same "arcadeness allowed"
framing as #172). Two knobs moved together to hit it, purely a re-tune of
existing exports — no new state, no behavioural change:

- **`MaxTurnRateDeg` 530 → 900 °/s.** This is the dominant lever: it scales
  *every* turn proportionally, so a 180° reversal drops from ≈0.35 s toward
  ≈0.20 s while a micro-correction stays effectively instant (≈0.02 s). It is
  the knob to reach for when "turning feels too slow" *overall*, as distinct
  from the back-turn's *relative* slowdown.
- **`BackTurnSlowFactor` 0.90 → 0.95.** A near-linear finishing touch — with
  the plant-then-pivot gate already carrying the commitment read, the residual
  back-turn tax is now almost negligible. Integrated 180° time at 900/0.95 is
  `(180/900)·ln(1/0.95)/(1−0.95) ≈ 0.205 s`.

The plant-then-pivot commitment gate (frozen feet, `Velocity = 0`,
committed-move cancel) is untouched, so the legibility argument the #172
amendment made still holds in full — only the yaw *speed* changed. The shipped
values and the resulting ≈0.20 s reversal are pinned in CI by
`tests/integration/PivotPlantTest.cs` (the `exports` and `flick-180`
scenarios). Human in-motion feel sign-off on the 900/0.95 pair is still
pending (a #172 follow-up feel pass, to be batched with the other pending
feel judgments per ADR-0015). Status remains **Accepted**.

### 2026-07-18 — Sanctioned Active-phase exception: the spin's scripted heading arc (#201)

The Consequences section above already anticipated this exact case: *"If a
future move requires heading updates during Active/Recovery, `RotateToward`
can be called explicitly in `TickCommittedMoveBehavior` for that phase."* The
spin move (`scripts/Input/Spin.cs`, M9/#75) is that future move — this
amendment is the "call it out on the record" the issue itself demanded rather
than a silent bypass.

**The exception, precisely scoped:** during a `Spin`'s **Active phase only**,
`TickCommittedMoveBehavior`'s Spin branch directly overwrites `Heading` with
`SpinHeadingMath.ArcHeading(entryHeading, direction, frameInPhase,
activeFrames)` — a scripted ~180° arc — instead of advancing it through
`HeadingMath.RotateToward`'s bounded, non-linear turn-rate cap (the model
this ADR's Decision section establishes for `Move()`). `ArcHeading` is a
**pure function** of exactly four inputs, all already-deterministic
same-tick state:

1. `entryHeading` — the authoritative `Heading` captured ONCE, at the instant
   this role's own machine entered Active (`JustEnteredActive`), never
   re-read mid-arc;
2. `direction` — `Spin.SpinDirection`'s sign, part of the `CommittedMove`
   instance itself, reconstructed identically on every role from the same
   wire payload;
3. `frameInPhase` — `CommittedMoveMachine.FrameInPhase`, which the machine
   keeps deterministic and identical across roles by construction;
4. `activeFrames` — `MoveFrameData.ActiveFrames`, a compile-time constant on
   `Spin.DefaultFrameData`.

It reads **no live per-tick input** — no `Vector2`, no `Input` singleton, no
Godot node state — so it replays bit-identically on the server's own tick,
the server's copy of a remote player, and a predicting client's own tick.
Outside the Active phase (Startup, Recovery, or any other move), `Move()`'s
`RotateToward` path is completely unchanged; the exception is bounded to
exactly this one move's exactly one phase.

**ADR-0014 reference-tier grounding (tier-1, real half-court ball):** a real
spin is a committed, explosive, full-body rotation around a shielded ball —
it is deliberately, categorically **faster** than a player steering an
ordinary turn with their eyes on the defender, precisely because the whole
point of the move is to beat the defender's reaction before they can adjust.
Forcing the arc through `RotateToward`'s bounded rate (tuned in the #172/#172-
follow-up amendments above for *steerable* turning) would either take the
spin many multiples of its intended Active window to complete, or require
retuning the general turn-rate cap around one move's needs — neither serves
the real-ball fact this move exists to model.

**Rejected alternative: make the spin obey `RotateToward`'s cap.** Rejected
because it defeats the committed-rotation identity a spin needs: `RotateToward`
is built for player-*steerable* turning (Forces #2 in this ADR's own Context —
"micro-aim must stay fluid"), and a spin is the opposite of steerable — it is
a locked-in commitment the player cannot redirect once begun (ADR-0003's
committed-move contract), resolving over a fixed, short Active window
regardless of the entry/exit heading delta. Routing it through the bounded
non-linear rate would either silently violate the "committed, not steered"
identity (if the rate happened to complete the rotation anyway) or leave the
rotation visibly incomplete when Active ends (if it didn't) — both worse than
an explicit, narrow, on-the-record exception.

This amendment does not reverse the Decision or weaken the general
`RotateToward` model for ordinary movement-stick turning — it adds one
narrowly-scoped, pure-function exception for one move's one phase, exactly as
the original Consequences section already contemplated. Status remains
**Accepted**.


## Amendment 2026-10-11 - Offensive sprint composes a turn-rate factor (#325)

Eligible analog sprint scales the effective `MaxTurnRateDeg` by the exported
`SprintTurnRateScale` (initially 0.65) in the same shared movement step used by
authority, prediction, and replay. The existing non-linear turn profile and
plant/pivot latch remain in force. The base export stays 900 degrees/s; the
sprint factor does not retune ordinary jogging or committed-move heading.

Reference basis: #325's accepted brief applies real half-court basketball's
line commitment: a body driving at speed cannot redirect as freely as a jog?to
the existing authoritative heading model. The initial factor is a provisional
cost, with magnitude tuning in #238 and feel judgment in #173.

Sprint requests travel with continuous input. The server derives eligibility
from its holder, ball, and move state. Prediction records the resolved modifier
alongside each tick's movement intent, so correction replay uses that tick's
modifier rather than today's button or ball state. Authority still owns truth;
cosmetic facing never determines speed, turning, or exposure.

Rejected: a cosmetic-only slower turn, because the movement and steal rules
need the authoritative heading. Rejected: replaying movement vectors without
the sprint modifier, because correction would reconstruct a jog instead of
the sprint the player predicted.

## Amendment 2026-10-11 - Held defensive stance faces the live attacker (#346)

The maintainer explicitly approved automatic attacker-facing slides in
[#346's approved brief](https://github.com/JoseTomanan/hooper-game/issues/346#issuecomment-6104239046)
on 2026-10-11. This narrowly revisits Alternative 3's movement-intent-facing
rule: while `move_protect` is held by the defender of a live Held/Dribbling
opponent, the movement stick keeps its world-space translation meaning and
authoritative heading approaches the floor-plane bearing to that opponent.
Idle stance also turns. Coincident positions retain heading. There is no aim
channel, pursuit, attraction or automated translation.

`HeadingMath.StepDefensiveStance` uses the existing bounded non-linear turn
schedule with `StanceTurnRateDeg = 360` degrees/s. An ordinary pivot already
owed is completed first at the ordinary rate, without retargeting its latch
to the attacker or applying two heading steps on its completion tick. Released
posture retains ordinary stick-facing and latch-retarget rules. Committed
moves own their existing heading/displacement/timeline throughout; holding
posture can resume stance after completion but cannot alter a move.

The same Ctrl/left-trigger action selects protection for the live dribbling
holder and stance for the defender. Raw posture wins over sprint. Stance is
ineligible for Loose/InFlight, an absent opponent or a committed defender;
eligibility is re-evaluated without a release-to-rearm requirement. Starting
movement tunables are `StanceSpeed = 3` m/s, `StanceAccel = 60` m/s² and
`StanceDecel = 90` m/s² against unchanged neutral 6/45/70. These model the
approved real-half-court (ADR-0014 tier 2) trade: a low slide changes lateral
direction sooner but recovers less ground than upright running. Half neutral
speed and a bounded one-third acceleration increase are modeling defaults,
not measurements of real-player speed. Tuning remains #238 and feel #173.
Finite velocity integration preserves entry/exit momentum. Contact consumes
the resulting heading and velocity without changing its speed/facing set
predicate, leverage or any defensive success/shot/rebound rule.

Authority/prediction/replay share explicit motion inputs. Server callers use
true opponent state and derive eligibility independently from their ball and
move state; clients use latest raw broadcast opponent state. The prediction
buffer retains both held posture input and locally sampled eligibility for
each tick, including committed-move exclusion. Replay uses this historical
context rather than today's possession or hardware. It re-evaluates pivot
precedence from the restored authoritative pivot state. Effective stance is
broadcast for remote presentation and restored before replay. No stale move
frame counter is force-matched. As accepted under ADR-0025/#370, all replayed
ticks use the latest available raw opponent sample: exact moving-opponent
history and committed-motion replay remain unclaimed approximations.

Rejected: instant lock-on, cosmetic-only facing, a general independent aim
channel, or clearing an owed pivot on posture entry. Rejected: a stance
CommittedMove, defensive sprint, automatic set status or new success bonuses.
The low/wide skeleton modifier reads effective posture and gives committed
clips/pivots priority; it never feeds authoritative state.

## Amendment 2026-10-12 — Approved post-facing exception (#348)

The maintainer explicitly approved bounded post facing away from the rim in
#348's 2026-10-11 Agent Brief. Within effective live-dribble post only, heading
approaches the floor-plane away-from-rim target at an initial 360 degrees/s
through the existing non-linear profile, including at zero stick input.
Translation retains world-space input and waits until within 2 degrees of
that target. An existing ordinary pivot completes first, without retargeting
or a second heading step on its completion tick. Entry brakes existing
momentum finitely; exit resumes ordinary heading/pivot rules from actual
heading and velocity. Held post cannot erase an owed pivot or a committed
move. A zero rim direction retains finite current heading.

The distinct committed turnaround snapshots its rim-facing target at accepted
Begin and turns during Startup at a bounded 360 degrees/s. Its shortest-arc
exact half-turn tie chooses positive yaw; later input cannot retarget it.
This is a narrow authoritative exception, not an independent aim channel.
Ordinary movement, sprint and defensive stance retain their existing rules.
Actual heading still feeds contact and the unchanged shot-facing penalty.
See ADR-0026 for entry/exit, fixed phase data, accuracy composition and scope.

Rejected: cosmetic-only rotation, instant snap, clearing a pivot on post
entry, or a general strafing/aim channel. Server derives eligibility; the
held request and historical eligibility/context are buffered, authoritative
post state is restored before replay, and effective state is broadcast for
remote display. No stale frame-counter equality gate is added. The existing
latest-raw-opponent approximation remains; no moving-opponent history is
claimed. Magnitudes remain provisional for #238 and feel remains #173.
