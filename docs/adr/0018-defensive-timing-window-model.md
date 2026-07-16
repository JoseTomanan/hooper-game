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
