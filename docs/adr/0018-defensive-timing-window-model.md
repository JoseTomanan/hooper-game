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
