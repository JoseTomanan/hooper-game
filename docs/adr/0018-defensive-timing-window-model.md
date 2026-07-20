# ADR-0018 — Defensive timing-window & reaction-tilt model

- **Status:** Accepted
- **Date:** 2026-06-29
- **Superseded-by:** —

---

## Context

ADR-0003 names defense as *"a symmetric core (mirror footwork + committed reads)
with a deliberate asymmetric tilt toward reaction"* but never specifies what a
defensive *read* mechanically **is**. M10 (epic #89) builds three defensive
committed moves — steal (#96), block (#98), on-ball contest (#99) — plus the
whiff-punish blow-by (#100). Every one of them needs a single shared answer to
"when does a defensive move succeed?" before it can be built, or the three will
drift into three inconsistent rules. This ADR is that answer (issue #95); the
sibling issues implement it.

It changes no architecture. It sits on the locked committed-move framework
(ADR-0003: `MovePhase` Startup→Active→Recovery, `MoveFrameData` integer-tick
windows), the server-authoritative model (ADR-0002), the deterministic ball
(ADR-0004), authoritative heading (ADR-0010), and the authoritative ball-hand
(#83 / ADR-0012). The numbers it implies are **not** set here — they are the
tuning sub-issue's job (#104) and the per-milestone feel pass (ADR-0015).

The forces:

- **A read, not a roll.** The defensive outcome must be decided by *timing a
  commitment against a visible offensive vulnerability*, never by a hidden
  percentage. A flat steal% kills the mind game (CLAUDE.md §1) and is the
  arcade anti-goal (ADR-0003).
- **Symmetry with a reaction tilt.** Offense and defense share the same
  commitment grammar (Startup/Active/Recovery), but defense must be *more*
  punishable on a wrong guess — that asymmetry is what makes defense reactive
  rather than a coin-flip race.
- **Determinism & authority (ADR-0002/0004).** The success test must be a pure
  function of authoritative integer-tick state so the server decides it and a
  client predicts the identical result.

## Decision

### 1. The overlap rule (the single shared success predicate)

A defensive committed move **succeeds iff its `Active` window overlaps the
target's currently-open vulnerable window** — both expressed as half-open
integer-tick intervals `[start, end)` on the same deterministic physics-tick
clock. This is the one predicate all three defensive moves call:

```
DefensiveResolution.Succeeds(activeStartTick, activeEndTick,
                             vulnStartTick,   vulnEndTick) : bool
   ⇔  activeStartTick < vulnEndTick && vulnStartTick < activeEndTick
```

It is a **pure, headless-testable** helper (ADR-0004 seam discipline) — no Node,
no engine singletons — so it unit-tests exactly like `OobResolution` /
`FlightTermination`. The defender's `Active` interval comes straight from
`MoveFrameData` (StartupFrames → ActiveFrames). The *vulnerable* interval is
defined per move in §2. A move whose `Active` opens too early (defender guessed)
or too late (defender reacted slow) simply does not overlap → it fails, and the
defender pays Recovery (§3).

### 2. The three vulnerable windows

All three are read from **authoritative** state only (§4).

- **Steal → the dribble-exposed phase, on the correct hand.** The ball is
  stealable only while the dribble has it away from the hand — a sub-window of
  `DribbleCycle.Phase` around the floor-contact point (Phase ≈ 0.5; Phase 0/1 is
  the ball back at hand height and *not* stealable). The vulnerable interval is
  the band of ticks where `Phase` sits inside an exported `[loExposed, hiExposed]`
  band straddling 0.5. **Additionally the steal must target the authoritative
  ball-hand** (`HandSide`, #83 / ADR-0012): a steal committed to the wrong side
  fails even on a perfect time overlap. Stealing is thus a *two-axis* read —
  *when* (phase) and *which hand* (side).

- **Block → the shot's release / in-flight window.** The shot is blockable from
  the moment the offensive `JumpShot` reaches `Active` (the ball is leaving the
  hand) through the early `BallState.InFlight` ticks, bounded by an exported
  ceiling so a block cannot connect with a ball already past the defender. The
  vulnerable interval is `[JumpShot.Active start, InFlight start + blockGraceTicks)`.

- **On-ball contest → a committed amplifier of the passive scatter.** Contest
  does **not** use a binary overlap to "succeed/fail"; it composes with the
  existing *passive* proximity scatter (ADR-0009 / #65). A committed contest
  whose `Active` overlaps the shot's release window applies an **additional,
  discrete** accuracy penalty on top of the passive distance/contest scatter the
  shooter already eats — i.e. actively pressuring the shot is strictly stronger
  than merely standing near it, but it spends a committed move (Recovery) to do
  so. Recorded here so #99 composes with #65 instead of replacing it.

### 3. Reaction-tilt asymmetry (expressed in `MoveFrameData`)

The "tilt toward reaction" is made concrete as a **frame-data rule**, not a
separate system: a defensive move's `MoveFrameData` carries a **tighter `Active`
window and/or harsher `Recovery`** than a comparable offensive move. Concretely:

- A defensive `Active` is *no wider* than the offensive vulnerable window it must
  hit — the defender cannot paper over mistimings with a long active phase; the
  read has to be right.
- A defensive `Recovery` is *at least as long* as the offensive move's, so a
  missed defensive commitment is **more** exploitable than a missed offensive one
  — this is the blow-by the whiff-punish issue (#100) cashes in.

This states the *rule*; the exact tick counts are deferred to #104 + the feel
pass. The rule is what guarantees defense reads rather than guesses.

### 4. Authority constraint

Every input to the overlap rule and the vulnerable windows is
**server-authoritative** state, predicted-and-reconciled like all gameplay
(ADR-0002):

- heading / facing → `PlayerController.Heading` (ADR-0010), **never** the
  cosmetic `FacingResolver` (ADR-0004 — it is never authoritative);
- ball-hand → authoritative `HandSide` (#83 / ADR-0012);
- dribble phase & shot state → `DribbleCycle.Phase` and `BallStateMachine`.

The success predicate runs on the server; a client predicts it from the same
authoritative values and the next `ReceiveState` broadcast reconciles any
divergence. No defensive outcome is ever computed from cosmetic state.

## Consequences

**What this buys:**

- One shared, pure success predicate (`DefensiveResolution.Succeeds`) the three
  M10 moves reuse — no per-move divergence, unit-tested headless.
- Defense is a genuine *read*: a timed commitment against a *visible*
  vulnerability (a low dribble, a rising shot), legible to both players (ADR-0003).
- The reaction tilt is honest and data-driven — it lives in `MoveFrameData`, so
  tuning it is editing frame counts, not rewriting logic.

**Accepted tradeoffs:**

- **More tunables.** Exposed-phase band, block grace ticks, contest penalty
  magnitude, and the defensive Active/Recovery counts are all new balance
  surface — deliberately left to #104 + the feel pass, not fixed here.
- **Two-axis steal is harder to land than a percentage.** Intended: the steal
  is meant to be a skillful read (when + which hand), not a spammed dice roll.
- **Contest composes rather than replaces.** #99 must layer onto #65's passive
  scatter; getting that composition right (no double-counting) is the contest
  issue's explicit job, flagged here.

**Rejected alternatives:**

- **Flat steal/block percentage roll.** Rejected: a hidden RNG outcome is not a
  read, removes the mind game, and is the arcade anti-goal (ADR-0003). The whole
  point of M10 is that defense is *timed*, not *rolled*.
- **Pure reflex with no commitment (instant steal/block button).** Rejected:
  move-and-strike with no Startup/Recovery is exactly the arcade decoupling
  ADR-0003 forbids — there would be nothing to telegraph and nothing to punish.
- **A separate "reaction stat" system.** Rejected: the tilt belongs in the
  existing frame-data grammar (one fewer system; symmetry with offense), not a
  parallel mechanic that would risk becoming a co-equal pillar.

## Open engine-API items

None. The model rides existing structures (`MoveFrameData`, `MovePhase`,
`DribbleCycle`, `BallStateMachine`, `HandSide`, `Heading`). The new
`DefensiveResolution.Succeeds` helper is pure C# with no engine-facing calls;
the per-move vulnerable-window bands are ordinary `[Export]` fields on the
defensive moves.

## Amendment 2026-07-01 — Steal implements the overlap by per-tick repetition, not a direct `Succeeds()` call

The #96 remediation (a merged bug where `ResolveStealAttempts` sampled the
dribble phase only on `JustEnteredActive`, collapsing the §1 interval-overlap
rule to a single point) exposed that the steal's actual implementation does
**not** call `DefensiveResolution.Succeeds(activeStart, activeEnd, vulnStart,
vulnEnd)` at all — it never did, before or after the fix. `Succeeds` remains
unused in `scripts/` today.

Why: §1's `Succeeds` needs concrete tick bounds for the vulnerable window
up front. Block's vulnerable window (§2) has one — `InFlight`'s start tick is
a fact the moment the shot releases. The steal's vulnerable window is a
**repeating** band of `DribbleCycle.Phase` (it opens and closes every dribble
cycle for as long as the ball is Dribbling) with no fixed start/end tick to
hand `Succeeds` in advance — computing one would mean projecting the dribble
forward from the Active window's start, duplicating `DribbleCycle`'s own
phase math in a second place purely to feed an interval into a predicate that
does the same overlap check `DefensiveResolution.StealSucceeds` already does
per-tick, just less directly.

The fix instead re-evaluates `StealSucceeds` — a point-in-band test — against
the **live** `DribbleCycle.Phase` on every tick `ActiveStealTargetHand` reports
non-null (i.e. every tick the machine is in Active). The union of those
in-band point tests, taken over the whole Active window, **is** the §1
interval overlap — just derived by repetition against ground truth instead of
by a single call against a precomputed interval. `Block` (#98), whose window
has a real start tick, is expected to call `Succeeds` directly as originally
specified; the steal's per-tick form is not a template for #98 to copy.

This is recorded as an accepted per-move implementation detail, not a
retraction of §1: the *rule* (Active must overlap the vulnerable window) is
unchanged and still what both moves obey. Only the *mechanism* by which the
steal proves that overlap differs from `Succeeds`'s direct interval-vs-interval
form, because its vulnerable window cannot be expressed as one in advance.

## Amendment 2026-07-16 — Block composes the overlap rule with a spatial reach gate (#214)

§1 states the overlap rule as *timing-only* — a block succeeds iff its `Active`
window overlaps the shot's vulnerable window. Issue #214 adds a **second,
spatial** term to block success: `ResolveBlockAttempts` now computes
`success = timingSucceeds && withinReach`, where `withinReach` is the pure
predicate `DefensiveResolution.WithinBlockReach(defenderPosition, ballPosition,
BlockReachRadius)` — an XZ-only distance gate. Timing overlap is therefore no
longer *sufficient* for block; both the timing read **and** arm's-reach
proximity must hold.

Why: §1's timing-only rule let a defender **anywhere on the court** block a shot
on a perfect time read alone, deleting the spacing axis from the shot/block duel
— directly against the design identity (CLAUDE.md §1, "the duel is the space
between two players"). §2's block window already gestured at this intent ("an
exported ceiling so a block cannot connect with a ball already past the
defender"), but expressed it purely *temporally* via `blockGraceTicks`; #214
makes the spatial constraint explicit as a distance, which `blockGraceTicks`
alone cannot enforce (a distant defender with perfect timing still passed).

Reference basis (ADR-0014, tier 2 — real half-court ball): a block only connects
within an arm's reach of the release point. The `BlockReachRadius` default
(2.2 m) **reuses** `BallController.ContestRange`'s own already-cited
"arm's-length closeout" anchor (issue #65) rather than inventing a new number
for the same physical concept; the exact value remains tuning surface deferred
to #104 + the feel pass, consistent with §2's "more tunables" tradeoff.

Scope — this is **not** a retraction of §1's shared overlap primitive:
`DefensiveResolution.Succeeds` is still called for block's timing check exactly
as §1/§2 specify; the reach gate is an *additional per-move term* composed on
top, structurally identical to how steal already carries its own second axis
(the ball-hand read, §2). §1's "no per-move divergence" refers to the shared
timing *primitive*, which block still obeys — it does not forbid a move from
carrying a move-specific vulnerable-window term (steal's hand, contest's
compose-with-scatter, block's reach are all such per-move terms under §2).
Steal is deliberately left timing+hand only for now; a symmetric steal-reach
term, if wanted, is its own future issue.

## Amendment 2026-07-16 — Contest implements its §2 composition as a single-tick timing gate, no reach term (#99)

§2 named contest's composition rule ("an additional, discrete accuracy
penalty on top of the passive distance/contest scatter... getting that
composition right... is the contest issue's explicit job") but deferred the
concrete shape to #99. The concrete factor shape — `ContestAppliesAt` (the
timing gate, built on §1's shared `Succeeds` primitive with the shot's
release window collapsed to the single release tick, since shot scatter is
computed exactly once) and `ContestMoveFactor` (the `1 + ContestMoveScatterK`
factor itself) — is now recorded in full in **ADR-0009's own 2026-07-16
amendment**, since it changes that ADR's `accuracyMultiplier` composition
formula directly (CLAUDE.md Decision Discipline: a change to a locked ADR's
model is recorded on that ADR, in the same PR as the code). This entry is a
pointer, not a duplicate — see ADR-0009 for the full reasoning, including why
contest carries no spatial reach gate (contrast this file's #214 amendment
above): contest never grants a binary success, so it never needed one.

`ContestMove` (`scripts/Input/ContestMove.cs`) is the concrete committed move:
frame data 6/8/20 (Startup/Active/Recovery), Active bounded by
`BlockGraceTicks` per this ADR's §3, Recovery matching JumpShot's per the same
reaction-tilt rule block and steal already follow.

## Amendment 2026-07-16 — The whiff-punish blow-by lane (#100): a beaten window that suppresses contest

§3 named the reaction-tilt's payoff ("a missed defensive commitment is more
punishable than a missed offensive one — this is the blow-by the whiff-punish
issue (#100) cashes in") but left the mechanism itself unbuilt. #100 makes it
concrete: on a whiffed `StealMove` — its `Active` phase expiring naturally
into `Recovery` (`WasRecoveryEnteredEarly == false`, §"Amendment 2026-07-01"'s
own natural-vs-early distinction, extended here to detect a NATURAL Recovery
entry rather than an early one), not a resolved success — the defender enters
a server-authoritative, time-boxed **beaten window**
(`scripts/Player/BeatenWindow.cs`, a pure struct: `UntilTick`, `IsActive`,
`Trigger`). While a defender is beaten, `BallController.ApplyShootLocally`
forces BOTH of that defender's accuracy contributions to their neutral
values against the handler's next shot — the committed `ContestMove` factor
(this ADR's own §2/2026-07-16 amendment above) AND the passive proximity
scatter factor. **The suppressed-factor list is recorded here as a pointer;
the authoritative composition-model text lives in ADR-0009's own 2026-07-16
amendment**, per CLAUDE.md Decision Discipline (a change to that ADR's locked
`accuracyMultiplier` formula is recorded on that ADR, in the same PR as the
code) — this entry is this ADR's record of the mechanism and the reaction-tilt
rationale; ADR-0009 is the record of exactly which multiplier terms it zeroes.

### Why this is the offense's reward, not the defender's cost

Recovery frames already gate the defender (they cannot act again until
Recovery elapses) — that is the defender's OWN cost, unrelated to this
mechanism and explicitly out of scope (confirmed in the issue's own planning
grill: "cooldown/spam is NOT in scope — recovery frames already gate that").
The beaten window is a SEPARATE, asymmetric advantage handed to the
**offense**: the ball-handler gets a burst window in which the SAME defender
who just whiffed cannot contest them, even once Recovery ends and they are
technically free to act again (see `BallController.BlowByWindowTicks`'s own
doc for why the window default deliberately outlasts `StealMove`'s Recovery
by a margin, not merely matches it — an exactly-matching window would make
the committed-`ContestMove` half of the suppression structurally unreachable
in real play, since the defender cannot begin ANY new move, including a
fresh contest, before their own Recovery elapses).

### Reusable by construction, not hardwired to steal (#196)

`PlayerController.TriggerBeatenWindow(currentTick, windowTicks)` is the ONE
choke point that grants the window — it has no idea a steal exists; it only
knows "beaten until tick N." `BallController.ResolveBeatenWindowTriggers`
(today's sole caller) merely detects a natural `StealMove` whiff via the
generic `PlayerController.JustWhiffedDefensiveMove<TMove>()` and calls the
trigger. Per the human's own planning note on #100 (recorded 2026-07-04),
issue #196 (a defender caught in Recovery mid-crossover-transit steal
attempt) is expected to reuse this SAME lane for its own whiff path — calling
`TriggerBeatenWindow` from its own resolution site — rather than inventing a
parallel mechanism. This mirrors how `DefensiveResolution.Succeeds` is this
ADR's one shared success predicate (§1): one shared punish mechanism, reused
by every future whiff path, not one per move.

### Server-authoritative; no new prediction/reconciliation channel

The beaten state is consumed ENTIRELY inside `ApplyShootLocally`'s existing
`IsServer && ShotScatterEnabled` guard (ADR-0002/0004 §1's only-server-mutates
list — the shot-scatter RNG draw already lives here) — no client ever
predicts or reconciles it directly, because no client-side shot-accuracy
prediction exists to reconcile against in the first place (clients always
predict dead-centre and are corrected by the next `ReceiveState`, exactly as
every other accuracy factor in this file already works). A harness
observability hook (`PlayerController.BeatenUntilTickForHarness`,
`BallController.LastContestFactorForHarness` alongside the existing
`LastContestMoveFactorForHarness`) exists for proof and for issue #102's
future telegraph remote sync to read from — #102 owns making the beaten
state visibly legible to both players (ADR-0003); this issue's scope is the
mechanic only, no animation/display.

## Amendment 2026-07-19 — A Held ball is steal-vulnerable during a JumpShot's Startup and feint-Recovery (#206)

### The gap this closes

`BallController.ResolveStealAttempts` (§1/§2's home) resolved steals only
while `StateMachine.Current == BallState.Dribbling` — a `Held` ball was
**total sanctuary**, with no vulnerable window at all. Combined with
`CradleForShotStartup` flipping Dribbling→Held **synchronously** the instant
a `JumpShot` begins (`PlayerController.BeginCommittedMove`), this created a
strictly-dominant defensive dodge: a holder who saw a steal's Startup
telegraph could tap the shoot button, escape the Dribbling-only check before
the defender's Active window ever opened, then pump-fake the shot away
(`CommittedMoveMachine.Feint()`) with zero downside. Mashing the pump-fake
was a free counter to any steal read on reaction — inverting the mind-game
this ADR's whole timing-window model exists to create (CLAUDE.md §1).

### The decision: Option A, pump-fake-window variant

Decided by the human 2026-07-19 via the held-ball-steal campaign (see issue
#206's "Design decision — human call 2026-07-19" comment for the full ranked
menu and evidence). **A `Held` ball becomes steal-vulnerable exactly during
a `JumpShot`'s Startup (the gather/raise) and feint-Recovery (the pump-fake
abort) — fixed integer-tick intervals, resolved with this ADR's §1 shared
`Succeeds` interval-overlap predicate directly (the block form, not steal's
per-tick point-in-band form — see §1's note on why steal and block use
different shapes of the same primitive).**

**Vulnerable interval, in `[start, end)` terms:**

- While `CommittedMoveMachine.Phase == Startup` on a `JumpShot`:
  `[start, start + StartupFrames)` where `start` is the tick Startup began
  (`PhysicsTick - FrameInPhase`).
- While `Phase == Recovery` on a `JumpShot`: `[end - FeintRecoveryFrames,
  end)` where `end = PhysicsTick - FrameInPhase + RecoveryFrames`. This
  `Recovery` case can ONLY be reached via a genuine pump-fake while the ball
  is still `Held` — a normally-completed Active phase releases the ball the
  SAME tick Active is entered (`BallController.CheckJumpShotRelease` reads
  `JustReleasedJumpShot`), flipping `BallState` off `Held` before Recovery
  begins, so this branch and a completed shot's Recovery are mutually
  exclusive by construction (see `PlayerController.HeldStealVulnerableWindow`'s
  own doc for the doubt-cycle-verified derivation of why `end` is computed
  correctly from `FrameInPhase` even though Feint() enters Recovery at a
  non-zero `FrameInPhase`, while the naive symmetric `start` formula would
  not be).
- Every OTHER phase/move (Inactive, or a Held ball with no `JumpShot` in
  progress at all) has NO vulnerable window — this is deliberate, not an
  oversight: see "What this does NOT fix" below.

**Success predicate**: `DefensiveResolution.HeldStealSucceeds(activeStart,
activeEnd, vulnStart, vulnEnd)` — a thin, separately-named delegate to §1's
shared `Succeeds`, composing the defender's `StealMove` Active window against
the holder's vulnerable window above.

**No hand-side axis (ADR-0014 tier-2 self-resolution).** Unlike the live
Dribbling steal (§2, two axes: timing AND hand), the Held check is
TIMING-ONLY. Real half-court 1v1 (ADR-0014 tier 2): a gathered/triple-threat
ball is protected with the whole body, not dribbled to one side, so "which
hand did you aim at" has no real-ball referent for a stationary cradle.
Requiring a hand match would hand the holder ANOTHER axis to dodge (aim the
body so the "wrong" hand faces the defender), diluting the exact design
property that won this option: a pump-fake exposes the gather, full stop,
no second read to bait.

### Required state-machine change: `Held` is now a legal `GoLoose()` source

`BallStateMachine.GoLoose()` previously only accepted `Dribbling`/`InFlight`
as legal sources — `Held → Loose` returned `false` unconditionally. This was
the ACTUAL mechanical root cause of total Held-steal immunity (discovered via
this issue's own doubt-driven review): even with the window/predicate wiring
above fully correct, `ResolveHeldStealAttempts`'s success branch silently
no-op'd on the state transition itself. `GoLoose()` now also accepts `Held`;
every other edge and every other caller (the Dribbling steal, block,
`TickLoose`'s own re-triggers) is unchanged.

### What this does NOT fix (deferred to #255)

This amendment closes **exploit #1 only** (pump-fake-mash beating the steal
on reaction). It deliberately does **not** address **exploit #2**: a holder
who simply never attempts a shot at all — a plain, idle `Held` possession
with no `JumpShot` in progress — has no vulnerable window here and remains
fully untouchable (no travel/5-second pressure exists in this codebase). The
human explicitly accepted leaving that "dead-Held staller" case open at this
stage rather than take on a second ADR amendment (ADR-0008 territory, a
possession-turnover rule, not a defensive-timing one) plus a HUD legibility
obligation in the same PR. It is tracked as a separate follow-up issue
(#255) — no PR closing #206 may claim Held-immunity is fully solved.

### Rejected options (recorded per the campaign's ranked menu)

- **(B) Proximity/facing-based exposure** (a continuous predicate reading
  authoritative `Heading` + defender bearing): rejected for #206's scope —
  it targets the SAME exploit as (A) but does nothing about the
  pump-fake-specific incentive inversion, and adds a facing-authority
  dependency this issue didn't need to take on.
- **(C) Hybrid (B) + 5-second rule**: rejected — bundles two ADR amendments
  (0018 + 0008) and a HUD element into one PR; the human explicitly chose
  to scope this PR to exploit #1 only.
- **(D) 5-second-rule only**: rejected for THIS issue — it addresses
  exploit #2 (the staller), not exploit #1 (the pump-fake dodge), so it
  cannot close #206 alone; it remains a candidate for #255.

### Harness proof (ADR-0016)

`tests/integration/HeldStealTest.tscn`: `held-vulnerable` (a correctly-timed
steal during Startup forces the turnover), `held-immune-outside-window`
(CONTROL — identical setup, steal fully resolved before any `JumpShot`
begins, ball stays `Held` throughout), `pumpfake-now-exposed` (the headline
scenario — reproduces the historical live-dribble degenerate exchange: a
steal timed against a live dribble, the holder cradles into `Held` before
the defender's Active window opens, then pump-fakes away; the turnover
connects anyway via the window above). Proven RED against the pre-#206
`ResolveStealAttempts`/`GoLoose()` and GREEN after, per ADR-0016's
evidence bar.

## Amendment 2026-07-20 — Transit (crossover-sweep) steal window (#196)

### The gap this closes

§2's steal window keys on the dribble-phase band only — nowhere does it read
where the ball physically IS. Worse, `HandSide` flips to the new hand in a
single tick at crossover Active-entry (issue #195), while the ball itself is
still physically sweeping across the body — mid-transit, the authoritative
hand already reports the NEW side while the ball is visibly still on the OLD
side. A defender poking exactly where the ball visibly is gets told "wrong
hand," the one moment the ball is most exposed being the one moment the
existing model refuses. Real 1v1 (ADR-0014 tier 2): a cross in the defender's
grill risks a poke — the ball briefly exposed as it travels hand-to-hand, and
a defender reading that exposure can reach in and knock it loose.

### The decision: a THIRD steal shape, unioned with the normal window

Locked by grill+triage 2026-07-04, implemented once #195 (the authoritative
swept ball position) landed. The transit window is unioned with — not a
replacement for — the normal live-dribble window §2 already defines. Both
are evaluated every tick `ResolveDribblingStealAttempts` sees the defender's
`StealMove` in Active; either succeeding resolves the SAME steal-success path
(`ResolveStealSuccess`) exactly once (§1's "one steal resolves per tick" still
holds — the union is checked, then a single resolution fires):

| Window | Timing axis | Side axis | Spatial axis |
|---|---|---|---|
| Normal dribble (§2, unchanged) | `Phase ∈ [lo, hi]` | must match `HandSide` | — |
| **Transit (#196, this amendment)** | ticks the #195 sweep is active (`_sweepActive`) | **dropped** | defender within `StealReachRadius` of the **swept** ball position |

- **Timing axis = the #195 sweep-active interval.** Gated by the CALLER
  (`ResolveDribblingStealAttempts`) reading `_sweepActive` directly (the same
  private field `SweepActiveForHarness` mirrors for harness observability) —
  not threaded through the pure predicate as tick bounds, because the caller
  already owns exactly this kind of gate for the normal window's
  `ActiveMove<StealMove>()` check. "Visibly crossing == stealable," one
  source of truth, maximally legible (ADR-0003).
- **Spatial axis = `DefensiveResolution.WithinStealTransitReach(defenderPos,
  sweptBallPos, StealReachRadius)`** — a thin delegate to the SAME XZ-only
  distance primitive block's reach gate uses (`WithinBlockReach`, #214),
  named separately for call-site legibility (mirroring how
  `HeldStealSucceeds`/`ContestAppliesAt` are themselves thin delegates to §1's
  shared `Succeeds`). New export `StealReachRadius` (default 2.2 m) reuses the
  SAME ADR-0014 "arm's-length closeout" anchor `ContestRange`/
  `BlockReachRadius` already cite (issue #65) — a new export because it is a
  distinct feel axis (a different move's reach), not because the physical
  concept differs. Feel tuning deferred to the consolidated tuning pass #238
  (#104 closed into #238 because the magnitudes interact), consistent with
  how `BlockReachRadius` documents its own deferral.
- **Side axis is DROPPED during transit.** The ball is between hands —
  `targetHand` has no discriminable referent mid-sweep — so the spatial axis
  IS the de-facto side discriminator: a defender only ends up close to the
  swept position on the side the ball is actually crossing INTO. This is the
  headline behavior: a steal connects during the sweep even though
  `holder.HandSide` already reports the NEW hand, closing exactly the gap
  described above.
- **A facing-cone gate is deliberately omitted for 1v1.** The on-ball
  defender always faces the handler, so a cone gate never bites in the
  current 1v1 game — it would add dead code with no discriminating power.
  The natural extension point for a help-defender side-poke, once 2v2 exists,
  is composing a heading-cone check alongside the reach test at the call
  site, not folding one into the pure predicate.

### Determinism (ADR-0002/0004) — no new netcode

The swept ball position is not a new authoritative channel: `TickDribbling`
already writes the #195 sweep's lateral/forward/vertical offsets directly
into the ball's authoritative `GlobalPosition` (never a cosmetic mesh
offset — re-confirmed by direct read of `TickDribbling`/`AdvanceHandSweep`
for this amendment, the doubt-cycle check this issue's own spec called for
given the M7b #69 remote-display bug class), and `ReceiveState` broadcasts
that same `GlobalPosition` every tick. The transit check therefore reads
already-reconciled state exactly like the normal window's `_dribble.Phase`
read — no new RPC, no new prediction/reconciliation channel, identical
result on the server and every predicting client.

### Whiff → blow-by composes for free (reuses #100's lane)

A transit-steal whiff — the defender's `StealMove` Active phase expiring
naturally into Recovery without either window ever opening — needs no
dedicated handling: it is caught generically by `ResolveBeatenWindowTriggers`'
`JustWhiffedDefensiveMove<StealMove>()`, which detects a natural whiff
regardless of which steal branch almost fired. This is exactly what the §
"Reusable by construction, not hardwired to steal (#196)" note under the
2026-07-16 whiff-punish amendment predicted — the blow-by lane needed no
changes for this issue.

### Harness proof (ADR-0016)

`tests/integration/TransitStealTest.tscn`: `transit-steal` (the headline
scenario — a defender's `StealMove` Active window overlaps a live #195
crossover sweep, positioned within `StealReachRadius` of the swept ball;
the turnover connects even though `holder.HandSide` already reports the new
hand, and the defender's `TargetHand` is deliberately set to the OLD hand so
the normal window's side axis would refuse — isolating that the transit
window, not the normal one, resolved the steal), `out-of-reach-recovery`
(CONTROL — identical setup, defender positioned well outside
`StealReachRadius`; the steal must NOT connect, the defender's `StealMove`
must resolve naturally into Recovery, and the resulting beaten window must
fire — proving both the risk half of the gamble AND that the setup could
have detected a steal at all). Proven RED against the pre-#196
`ResolveDribblingStealAttempts` and GREEN after, per ADR-0016's evidence bar.

Out of scope, per the issue: #255 (the deferred dead-Held staller — a plain,
idle `Held` ball with no sweep or shot attempt in progress remains untouched
by this amendment, same as it was left by #206) and any 2v2 facing-cone term
(noted above as a future extension point, not built here).

## Amendment 2026-07-20 — Static proximity/facing exposure closes the dead-Held staller (#255, Route A)

### The gap this closes

The 2026-07-19 amendment above closed exploit #1 (pump-fake-mash beating a
steal read) but explicitly left exploit #2 open: a holder who simply never
attempts a shot — a plain, idle `Held` possession with no `JumpShot` in
progress — had `HeldStealVulnerableWindow == null` for the ENTIRE hold and
remained fully steal-immune. No travel/5-second pressure exists in this
codebase, so a patient triple-threat stall was a free, zero-risk stalling
tactic with no counter anywhere in the system.

### The decision: Route A, static proximity/facing exposure

Decided by the human 2026-07-20 (issue #255, "HITL: Human response — Go
with Route A: Static proximity/facing exposure"), over the campaign's
originally-recommended candidate menu recorded in the 2026-07-19 amendment's
"Rejected options" list. **A `Held` ball — with or without a `JumpShot` in
progress — is now ALSO vulnerable whenever a defender is within
`HeldStealReachRadius` of the ball AND positioned inside the holder's
exposed ball-hand cone**, unioned with (not replacing) the pump-fake window
above. Success requires ALL THREE, checked at the call site
(`BallController.ResolveHeldStealAttempts`):

1. **Timing** — the SAME shared gate every steal shape in this ADR uses:
   the defender's `StealMove` must be in its Active phase THIS tick
   (`defender.ActiveMove<StealMove>()`). A steal is a committed read, never
   a passive proximity drain — this is not a new vulnerable-tick interval,
   it is the existing timing axis composed with two new spatial/orientation
   ones.
2. **Proximity** — `DefensiveResolution.WithinHeldStealReach(defenderPos,
   ballPos, HeldStealReachRadius)`, a thin delegate to the SAME
   `WithinBlockReach` XZ-distance primitive block's reach gate (#214) and
   the transit steal's `WithinStealTransitReach` (2026-07-20 amendment
   above) already use. New export `HeldStealReachRadius` (default 2.2 m)
   reuses the SAME ADR-0014 "arm's-length closeout" anchor those two already
   cite (issue #65) — a distinct feel axis (a different move/window), not a
   different physical concept.
3. **Facing exposure** — `DefensiveResolution.HeldStaticHandExposed(holderPos,
   holder.Heading, holder.HandSide, defenderPos, halfConeRadians)`, the
   genuinely NEW predicate this amendment adds. Reads the holder's
   authoritative `Heading` (ADR-0010) and `HandSide` (ADR-0012) — never the
   cosmetic `FacingResolver` — to derive a world-space "hand-side direction"
   and tests whether the unit vector from holder to defender falls within a
   tunable half-angle (`HeldStealExposureConeDegrees`, provisional default
   60°) of it. Turning the body (changing `Heading`) rotates the whole cone
   with it — this IS the "shield the ball" counter; no separate "behind the
   holder" check is needed.

   **The hand-side direction is LOCKED to `BallController.HandRight`/
   `HandSign`** — the exact formula that already places the ball mesh
   in-hand (`right = (-forward.Z, forward.X)`, `handSign = +1` for Right/`-1`
   for Left) — not re-derived independently. Code review of this PR's first
   version caught a real bug here: it had independently derived "Right ⇒
   world +X" as a fresh assumption, which is backwards in this codebase's
   coordinate convention — Godot's +Z-forward, right-handed system puts a
   facing-+Z player's actual right hand toward world **−X** (a Right-hand
   ball is carried toward −X, a Left-hand ball toward +X). The independently-
   derived version pointed the exposed cone at the *protected* side and
   nothing caught it locally: the unit tests and the one harness scenario
   that exercised it were written against the SAME wrong assumption, so they
   all passed green while being semantically inverted. The fix re-derives
   the hand direction from the same two primitives the render reads, so it
   cannot drift out of sync with the render again, and adds a harness
   control (`held-static-immune-wrong-side`, below) specifically shaped to
   catch a re-introduced mirror — the pre-existing `held-static-immune-
   shielded` control is left/right symmetric and provably cannot.

### Reconciling with the pump-fake window's "no hand-side axis" note

The 2026-07-19 amendment's `HeldStealSucceeds` is deliberately TIMING-ONLY,
arguing a cradled ball mid-pump-fake has no discriminable hand-side to aim
at (the whole body is committed to the shot-startup animation, no read to
bait). That argument does **not** transfer here, and this predicate
deliberately reads `HandSide` anyway: a STATIC triple-threat hold has no
committed-move startup consuming the body, so the holder's actual carry
side is a real, currently-true fact about where the ball sits relative to
their torso, not a manufactured axis. Exposing it is the entire point of
Route A — it is an orientation/positioning read (rewarding a defender who
gets to the right spot), not a second timing window layered onto the first.
The two predicates therefore differ in kind on purpose: `HeldStealSucceeds`
composes ADR-0018 §1's shared `Succeeds` tick-interval form (a discrete
timing question); `HeldStaticHandExposed` is a continuous spatial-angle
predicate with its own closed-boundary convention (mirroring §2's
`StealSucceeds` phase-band inclusivity, not §1's half-open tick convention)
— see that method's own doc comment for the full boundary-semantics
rationale.

### ADR-0014 citation (tier 2, real half-court 1v1)

A gathered/triple-threat ball is not sanctuary in real ball — it is
protected by body position (shoulder/hip between the ball and the defender),
not invulnerability. A defender who gets goal-side of that positioning, on
the exposed hand, can still reach in and poke a stationary hold; a holder
who pivots to shield denies that angle. This is the same reference tier the
2026-07-19 amendment's own hand-side reasoning cites, applied to the
opposite conclusion because the underlying game state differs (static hold
vs. mid-pump-fake gather) — not a contradiction of that amendment, a
narrower-scoped sibling rule for a case it explicitly deferred.

### Rejected alternative: Route B (closely-guarded / 5-second rule)

Recorded per the campaign's original menu (2026-07-19 amendment, option D)
and the issue's decision brief. Route B — a server tick counter awarding a
turnover after N ticks of sustained close-guarding, independent of any
defensive committed move — was NOT chosen. It amends **ADR-0008**
(possession rules), not this ADR, since it is a possession-turnover rule
rather than a defensive-timing-window one; it also carries a legibility
obligation (a hidden tick count violates CLAUDE.md §1's "legibility is a
competitive requirement," so it would need a pressure-count HUD element —
a UI sub-task and feel surface this issue's scope did not take on). Route B
remains a candidate for a FUTURE issue if Route A alone proves insufficient
in the human's deferred feel pass (#173) — the two are not mutually
exclusive; Route A does not preclude adding Route B later, it simply
resolves #255 on its own without also requiring one.

### Harness proof (ADR-0016)

`tests/integration/HeldStealTest.tscn`, extending the existing scene:
`held-static-vulnerable` (a plain idle `Held` ball — no `JumpShot` ever
begins — with the defender in reach AND on the exposed hand-side cone,
timing a `StealMove` Active window; the turnover must connect — the
headline metric, the previously-untouchable staller losing the ball),
`held-static-immune-out-of-reach` (CONTROL — identical geometry/timing,
defender moved beyond `HeldStealReachRadius`; the steal must NOT connect,
ball stays `Held`), `held-static-immune-shielded` (CONTROL — identical
in-reach geometry, but the holder's `Heading` is rotated 180° so the
defender who was on-axis is now off the exposed cone; the steal must NOT
connect — NOTE: this control is left/right symmetric and therefore cannot
by itself catch a mirrored hand-side convention), and
`held-static-immune-wrong-side` (CONTROL, added after code review caught the
mirror bug described above — identical heading/timing, defender in reach but
on the PROTECTED off-hand side per the locked `HandRight`/`HandSign`
convention; the steal must NOT connect. This IS the control that
discriminates handedness: it and `held-static-vulnerable` were both proven
RED against a deliberately re-mirrored convention and GREEN against the
corrected one). The pre-existing `held-vulnerable`/`held-immune-outside-
window`/`pumpfake-now-exposed` scenarios (2026-07-19 amendment) and
`TripleThreatTest`'s `dead-dribble`/`production-drive` scenarios remain
green unchanged, proving the union does not disturb the pump-fake timing
path or fire during an ordinary triple-threat hold with no defender present
— `held-immune-outside-window` specifically needed its shared default
defender geometry moved off the newly-added exposure cone (directly in
front of the holder, 90° off either hand axis) once the static term made
that geometry genuinely exposed under the corrected convention; it was
never about proximity/facing, only the pump-fake window's own null-ness, so
narrowing its setup to isolate that axis again is not a weakening of the
proof. `held-static-vulnerable` was ALSO proven RED against the pre-#255
`ResolveHeldStealAttempts` (which early-returned whenever
`HeldStealVulnerableWindow` was null) and GREEN after, per ADR-0016's
evidence bar.

Tuning (`HeldStealReachRadius`, `HeldStealExposureConeDegrees`) is
provisional, deferred to the consolidated tuning pass #238 — not this
issue's or this ADR's to sign off.
