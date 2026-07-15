---
name: hooper-failure-archaeology
description: Chronicle of every settled battle in the hooper-game repo — bugs found, fixed, and (in a few cases) deliberately left unfixed. Load this BEFORE fixing or investigating anything in a previously-touched subsystem (steal, block, court bounds, shot scatter, heading/turn-rate, ball-hand-sweep, the headless harness itself), before deleting any git branch, or whenever a "known bug" smells familiar — it may already be fixed, already be a documented accepted trade-off, or already have a tracking issue you're about to duplicate. Provides symptom → root cause → evidence (verified commit SHAs / PRs / issues) → status for each story, plus branch-deletion safety verdicts and the meta-patterns this repo's bugs keep repeating.
---

# Hooper failure archaeology

This is a **chronicle**, not a how-to. It exists so that no future session
re-fights a battle someone already won (or already decided not to fight).
Every entry below was verified against the actual repo history on
2026-07-12 — every SHA was checked with `git show --stat -s <sha>` before
being written down here. Trust these SHAs; re-verify only if you suspect the
history has been rewritten (it hasn't, as of this writing — see Provenance).

## How to use this file

1. About to touch steal, block, court bounds/OOB, shot scatter, heading/turn
   rate, ball-hand-sweep, or the headless harness itself? Find the matching
   story below FIRST. You may be about to re-fix something already fixed, or
   "fix" something that's deliberately NOT fixed for a documented reason.
2. About to delete a branch? Check "Branch dispositions" below — one branch
   is flagged do-not-blindly-delete.
3. Writing a new harness scenario that touches ball/rim/board geometry, or a
   new committed move? Read "Recurring meta-patterns" — your new code is
   statistically likely to hit one of these two known bug shapes.

---

## Settled battles (symptom → root cause → evidence → status)

### 1. Steal — four-generation audit cascade (issue #96, #174–#182)

- **SYMPTOM:** Initial steal implementation only sampled the vulnerable
  dribble-phase window on the single tick the defender entered Active
  (`JustEnteredStealActive`), missing legitimate steals that connected on any
  later tick of the same Active window.
- **ROOT CAUSE:** Point-sample instead of a per-tick interval-overlap check
  (ADR-0018 requires overlap between the defender's Active window and the
  ball-holder's vulnerable window). Fixing that exposed two more latent bugs:
  `GoLoose()` never seeded `BallController._arc`, so **every steal attempted
  before the ball's first shot crashed the server with a
  NullReferenceException**; and `DribbleCycle.Phase` stayed frozen while the
  ball was Loose, letting one steal window fire `GoLoose()` multiple times.
- **EVIDENCE:**
  - `6980036` — `fix(defense): resolve steal across the whole Active window, safely and once (issue #96)` — the per-tick interval-overlap fix + NRE fix.
  - `7a35160` — `fix(defense): charge the defender, not the offense, as last toucher on a steal`
  - `5d1f43c` — `fix(ball): guard the steal last-toucher write against the peer-0 sentinel`
  - `f68eea0` — `fix(input): reconcile early-ended committed moves via a level-triggered signal` (issue #175 — see story 10)
  - `946a7f0` — `fix(player): correct the own client's Active prediction after a steal resolves`
  - `a2acd47` — `fix(defense): reset the dribble cycle on every possession award` (issue #176), backed by `b3372154` — `feat(ball): add DribbleCycle.Reset() to restart a possession's dribble phase`
  - `b14b182` — `test(integration): OOB headless harness — held-ball turnover, defender-exempt, no-strobe, both-oob edge (adjudicates #119)` (issue #179 — a dedicated harness written to adjudicate a suspected bug that turned out NOT to exist)
  - `e3cecd2` — `docs(adr): amendment -- steal implements the overlap by per-tick repetition, not Succeeds()` (ADR-0018 amendment recording that steal's window has no fixed start/end tick, so it can't call the shared `DefensiveResolution.Succeeds()` predicate directly — **this asymmetry is intentional, not a bug**, and ADR-0018 itself flags it as "not a template for #98 [block] to copy")
- **STATUS:** Fixed. Chain closed on `main` across PRs #174, #180, #181, #182.

### 2. Block committed-move — release-tick off-by-one + harness self-falsification (issue #98, PR #215, current HEAD)

- **SYMPTOM:** A defender whose Active window's last legal frame was exactly
  the shot-release tick T should connect the block. It was silently missed.
- **ROOT CAUSE:** `ResolveBlockAttempts()` ran only before the per-state
  switch in the tick loop, but ball release happens INSIDE that switch
  (`TickHeld`/`TickDribbling` → `CheckJumpShotRelease` → `ApplyShootLocally`).
  The first real evaluation of the block landed on tick T+1, by which point
  the defender had already transitioned to Recovery.
- **EVIDENCE:**
  - `c8a0cd8` — `fix(defense): evaluate the block on the release tick itself` — added a second, server-only, release-tick-only call to the resolver, guarded against double-resolve.
  - `7616a63` — `fix(defense): route block through BeginCommittedMove and gate the server-side begin` — block had bypassed the shared choke point (`BeginCommittedMove`) that clears a stale pivot latch. Same bug SHAPE as steal hit independently, weeks earlier — see meta-pattern #2 below.
  - `c1bf180` — `fix(defense): fail-safe name parse and degenerate-swat fallback in block resolution` — `defender.Name` parse failure fell through as a LIVE blocker (fail-open), not a non-blocker (fail-closed).
  - **Harness falsified its own control scenario:** `8051e28` — `test(defense): place the harness backboard behind the rim so the control shot can score`. The "guaranteed clean make" control scenario failed on first CI run — code-built scene trees inherit `BallController`'s raw C# export defaults, where `BoardCenter` (0, 3.5, 0.3) sits IN FRONT OF `RimCenter` (0, 3.05, 0). Every control arc bounced off the board; the control-make scenario timed out 0-0, silently making the "score unchanged" success assertion vacuous (it would have passed even if the block logic were entirely broken). See story 9 and issue #217 for the general pattern.
  - More harness self-corrections found while proving #98: `50e79f2` — `test(defense): latch the defender-Recovery observation at block time` (the verdict was reading the defender's LIVE phase too late — a 20-tick Recovery window had legitimately already expired by verdict-check time); `fb5ac54` — `test(defense): derive the counterfactual wait from the predicted release frame` (the counterfactual-wait math derived the verdict frame from the observed `_releaseFrame` field, which is provably never set on the success path — resolution collapses Held→InFlight→Loose within a single frame, before the parent node — which observes children one frame late per `project_godot_harness_input_gotchas` — ever sees InFlight).
- **STATUS:** Fixed, merged PR #215 (`3085ee1`, HEAD as of this writing).
- **KNOWN GAP LEFT BEHIND:** block has no reach/proximity term yet — see issue #214, tracked separately, deliberately not folded into #215.

### 3. M6 dedicated server — same-day accidental double-revert (PR #42/#43/#44)

- **SYMPTOM:** None visible today — net no-op. Recorded here only so nobody
  mistakes the revert-then-restore pair for a live incident when reading
  `git log`.
- **EVIDENCE:** `6ffaf96` (merge PR #42, `feat/milestone-6`) → `edc9b96`
  (merge PR #43, reverts #42, same day) → `c42c585` (merge PR #44,
  un-reverts #43, same day).
- **CAVEAT:** none of the three merge commit bodies documents WHY the revert
  happened. This reads as an accidental revert caught and corrected within
  hours — but there is no recorded reasoning trail, which is unusual for
  this repo (see meta-pattern below: nearly every other fix here carries a
  multi-paragraph root-cause explanation; this trio does not).
- **STATUS:** Net no-op. M6 content intact on `main`. Nothing to do.

### 4. Court boundary line vs. actual OOB rule — two-round diagnosis of "shooting not working" (PR #188)

- **SYMPTOM:** Jump-shot windup animation always played, but the ball
  sometimes never released. Reported as failing "further away (but not
  beyond the white line)."
- **ROOT CAUSE, round 1:** `4345f22` — `fix(input): stop ambiguous
  right-stick gesture from eating a jump shot`. `RightStickGestureRecognizer`
  reported `GestureKind.Feint` for ANY quick aim-stick flick-and-return,
  regardless of which committed move was running. For a running `JumpShot`
  this silently converted the shot into a pump-fake — `Feint()` routes
  Startup → Recovery without ever setting `JustEnteredActive`, so the windup
  animation played but the ball never released. Fixed narrowly for JumpShot
  via a new `FeintGateResolver`.
- **ROOT CAUSE, round 2:** `843e90d` — `fix(systems): compensate CourtVisuals
  boundary line for parent Scale.X`. `CourtVisuals.BuildCourtBoundOutline`
  never compensated for its parent node's `Scale.X = 1.8` set in
  `Main.tscn`. The visible white boundary line rendered at ~1.8× the TRUE
  rule boundary that `CourtBounds.IsOutOfBounds` actually enforces. A player
  who looked visually safe was already out of bounds by the real rule, and
  `ResolvePlayerOutOfBounds` runs every server tick independent of
  committed-move phase — silently handing the ball to the opponent
  mid-shot-windup, which read to the human as "the shot just didn't happen."
- **DELIBERATELY NOT FIXED as part of this PR:** `7826c85` —
  `test(input): characterize the phantom-shot gap after mid-move possession
  loss`. `CommittedMoveMachine` has no `Cancel()`/`Interrupt()` by explicit
  ADR-0003 design (no flow-cancel), so any mid-move possession loss leaves
  the holder's machine ticking to completion — i.e. the phantom-shot
  animation. Fixing this would touch a locked ADR, so it was filed as
  **issue #189** for a human/doubt-driven design call instead of silently
  patched. **#189 is still OPEN as of 2026-07-12 — see the dedicated section
  below.**
- **STATUS:** Both real bugs (rounds 1 and 2) fixed, PR #188 merged. #189
  remains open and deliberately unresolved.

### 5. Regulation court width follow-up (PR #190)

- Once story 4's scale bug was found, the sideline value itself turned out
  to be wrong too — the earlier "fix" (±4.88) was itself a scale bug per
  memory `project_court_oob_zone_design`, not the regulation width.
- **EVIDENCE:** `92353a1` — `refactor(ball): centralize court rectangle as
  CourtBounds.Default{Min,Max}`; `30a0692` — `feat(ball): widen sidelines to
  regulation ±7.62`; `24fb429` — `chore(scene): drop redundant CourtVisuals
  Scale.X` (now redundant once the outline draws at the true rect);
  `5ac897d` — `docs: correct stale ±4.88 court-width references` (repo-wide
  doc sweep).
- **STATUS:** Fixed, merged.

### 6. Shot-scatter make-% curve — measured correctly, explained wrongly, then corrected (issue #79)

- **SYMPTOM:** None — this is a case of a CORRECT measurement with an
  INCORRECT causal write-up, not a code bug.
- **WHAT HAPPENED:** The first write-up (`e4de463` →`73bf82e`, PR #117)
  measured make% exceeding the closed-form disc-area formula at long range
  and attributed it to "backboard/glass assists." That explanation is
  impossible given the actual code: `RimBackboard.Resolve` returns `Bounce`
  on board contact, and `IsMake` counts any `Bounce` as a miss — the board
  can only ever REDUCE makes, never assist them. The real mechanism,
  corrected in `ebc10eb` (PR #132, `docs(analysis): correct shot-scatter
  divergence mechanism (#79)`): the 3D make-test is a vertical CAPTURE
  CYLINDER around the rim, not a flat disc, so the closed-form disc-area
  formula understates true make% at the margins. `ebc10eb` also corrects a
  sign error and two misstated percentages from the first draft.
- **STATUS:** The measured numbers in `docs/analysis/0079-shot-scatter-curve.md`
  were never wrong. Only the causal prose was, and it's now corrected.
- **LESSON:** don't trust your own causal story for a measured result
  without checking it against the actual contact-resolution code path.

### 7. Turn-rate / heading feel — three retunes, one drift caught and ratified (ADR-0010, issues #134/#172)

- `d7ab3e5` — `feat(player): server-authoritative heading with bounded turn
  rate` shipped the mechanism. `94f0636` — `tune(player): heading turn rate
  540→400°/s for a ~0.75s reverse-pivot` set the first documented default.
- **DRIFT:** `fdd409c` — `fix(player): speed up turn rate — 180° reversal
  0.75s → 0.55s` — this commit changed the shipped rate to 530°/s DIRECTLY
  IN CODE without updating ADR-0010 or `HeadingMathTests` to match. ADR and
  tests silently drifted from what actually shipped.
- **CAUGHT AND RATIFIED (not reverted):** `c526394` — `docs(heading): ratify
  MaxTurnRateDeg=530°/s, align ADR-0010 + tests (#134)` — issue #134 was a
  human design call ("snappier is better") that accepted the drifted value
  as the new intended value, rather than reverting to the documented one.
- **LATER RETUNE:** `3577609` — `feat(player): retune 180 pivot to ~0.20s
  (turn rate 900, back-turn 0.95)` (issue #172), alongside a new
  flick-to-latch pivot gate: `424c788` — `feat(player): flick-to-latch
  in-place pivot step in HeadingMath`, `0ff80bc` — `fix(player): keep pivot
  planted until latched facing is reached`. Follow-up PR #187 added
  `a1de332` — `feat(player): soften crossover burst speed 12 -> 9 m/s` and
  `f86315b` — `docs(adr): record the #172 follow-up pivot retune to ~0.20s`.
- **CURRENT SHIPPED VALUES (verify against `HeadingMath.cs` / ADR-0010
  before relying on this):** turn rate 900°/s, back-turn factor 0.95.
- **LESSON:** a code-then-ADR drift happened exactly once in this repo's
  history and was caught by a human design-call issue, not by a review
  gate. There is no automated check preventing it from happening again.

### 8. Ball-hand-sweep — doubt-driven review found two real defects, documented one accepted gap (issues #194, #195)

- **REAL DEFECTS FOUND AND FIXED:** `2a05d30` — `fix(ball): harden
  AdvanceHandSweep against a null-holder cache clobber and a terminal-tick
  pop`. Two independent bugs in one function: (1) the sweep advanced its
  tick counter BEFORE sampling `t`, so the last active sample landed short
  of `t=1` — every sweep had a visible one-tick "pop" at its end; fixed by
  sampling `t` inclusive of 1.0 before deactivating. (2) a transient null
  ball-holder unconditionally clobbered the cached `_lastObservedHandSide`
  with null, dropping the very next tick's flip detection.
- **DELIBERATELY NOT FIXED:** `16bb309` — `docs(ball): record the #194 doubt
  cycle's wrong-sweep-path revert gap`. A mispredicted `BehindTheBack`'s
  self-correcting reverse sweep can render via the WRONG rendering path,
  because `_machine.CurrentMove` is typically already null at the exact
  revert tick. This is a real, found, understood gap — accepted as a
  cosmetic self-correcting trade-off (the sweep still ends up in the right
  place, just via the wrong intermediate path for one tick). **There is NO
  tracking issue for this** — only the commit message and inline code
  comments record it. If you're hunting for "the BehindTheBack issue," it
  does not exist as a GitHub issue; grep the commit message instead.

### 9. Headless CI harness — introduced, then proved its own premises wrong twice (ADR-0016)

- **TIMELINE:** `58d84f93` — `test(integration): headless smoke harness —
  first ADR-0016 surface` → `7375a469` — `ci: add headless Godot
  integration-test job (ADR-0016)` → `d54fe72` — `docs(adr): record
  ADR-0016 Go — harness proven headless on CI`.
- **THE PATTERN (see also story 2 and issue #217):** the harness's own
  test-scene assumptions were falsified TWICE, independently, by two
  unrelated features (story 2's backboard placement; issue #217's
  `BoardCenter`/`RimCenter` code-defaults inconsistency). Both times the
  root cause was the SAME: a scene tree built directly in C# for a harness
  test inherits `BallController`'s raw C# export defaults, NOT `Main.tscn`'s
  corrected override values. `Main.tscn` places `BoardCenter` behind
  `RimCenter`; the raw C# defaults place it in front.
- **STANDING RULE THIS ESTABLISHES:** any new harness scenario that touches
  ball/rim/board geometry must assume the code defaults are WRONG until
  proven otherwise — either mirror `Main.tscn`'s override values explicitly
  in the harness scene construction, or add an explicit assertion that the
  control/success-path scenario actually exercises the intended condition
  (not just that "nothing bad happened," which is vacuously true if the
  control scenario silently can't succeed at all — see also memory
  `project_harness_code_defaults_gotcha`).

### 10. `EndActiveEarly()` needed a client-side counterpart (issue #175)

- **SYMPTOM:** After a steal resolved server-side, the DEFENDING client's own
  local prediction kept predicting its move was still Active, and couldn't
  `Begin()` a new committed move until the next state broadcast caught up.
- **ROOT CAUSE:** `EndActiveEarly()` (added to serve story 1's steal fix) ran
  server-only; no client-side reconciliation path existed for "my Active
  window was ended early by an external event, not by my own move
  completing."
- **EVIDENCE:** `f68eea0` — added `WasRecoveryEnteredEarly`, deliberately
  made LEVEL-TRIGGERED (true for the WHOLE Recovery duration, not a
  one-tick edge) so the signal survives an UnreliableOrdered packet drop.
  `ShouldForceRecovery` was kept as a narrow, separate predicate rather than
  folded into the existing `ShouldForceInactive` check, because a bare
  `serverPhase == Recovery` check would misfire on every ORDINARY
  Active→Recovery transition under normal jitter, not just early-ended
  ones. `946a7f0` wires the new signal through `ReceiveState`. This PR also
  fixed a related finding (#177 finding R3): `JustEnteredActive` could stay
  true for one extra tick after already entering Recovery.
- **STATUS:** Fixed, PR #182.

---

## Deliberately NOT fixed — do not "helpfully" fix these

These were found, are real, are understood, and were explicitly left alone.
Fixing one without the linked authorization is very likely to be reverted or
rejected in review.

| Item | What it is | Why it's not fixed | Authorization needed |
|---|---|---|---|
| **Issue #189** (OPEN, verified 2026-07-12) | Phantom-shot animation: `CommittedMoveMachine` has no `Cancel()`/`Interrupt()`, so losing the ball mid-move (e.g. to a steal) still plays the move to completion. See story 4. | Touches ADR-0003's locked "no flow-cancel" rule. An interrupt-on-external-event carve-out may or may not be compatible with that ADR — genuinely undecided. | A human or `/doubt-driven-development` design call that either amends ADR-0003 or shows the carve-out is compatible with it. |
| `16bb309` (commit, no issue) | `BehindTheBack`'s self-correcting reverse sweep can render via the wrong code path for one tick during a misprediction revert. See story 8. | Accepted as a cosmetic, self-correcting trade-off — the sweep still lands correctly, only the intermediate rendering path is wrong for a tick. | None filed. If you want to fix this, file the tracking issue first — it doesn't exist yet. |
| ~1-tick cradle race | A steal attempt and a "ball settles into cradle" (Held) transition can land in the same tick window; documented in code/tests as a **KNOWN ACCEPTED RACE**, not adjudicated by a tick-order rule. | Related to and partly overlaps issue #206 (Held-ball steal-immunity) — see that issue for the live design question about Held-ball vulnerability. | #206's resolution will likely also settle this. Don't fix in isolation. |
| Block's missing reach/proximity term | Block resolution (#98/PR #215, story 2) checks timing-window overlap only — no distance/reach gate. A defender arbitrarily far away can still "block" if the timing lines up. | Explicitly scoped OUT of #98/PR #215 to keep that PR shippable; deferred on purpose. | **Issue #214** (OPEN, verified 2026-07-12) owns this. |
| Replay skips `TickCommittedMoveBehavior` | An accepted divergence in how replay/reconciliation handles committed-move side effects. | Documented as an accepted trade-off, not a bug being tracked toward a fix. | None currently filed — treat as intentional unless you find new evidence it's wrong. |

---

## Branch dispositions (verified 2026-07-12)

**Safe to delete (fully merged, empty diff vs `main`):** `fix/shooting-not-working`,
`fix/make-court-bounds-wider` (PR #188/#190), `feat/2k-movement-feel` (PR #186),
`claude-audit/175-steal-desync` (#182), `claude-audit/176-dribble-reset` (#181),
`claude-audit/179-oob-harness` (#180), `chore/79-scatter-curve-analysis` (#117),
`docs/79-scatter-mechanism-correction` (#132), `fix/96-steal-active-window-interval`
(#174), `pr182` (stale alias), `worktree-milestone-7b-parallel`, `claude/afk-work-m8`,
`claude/afk-work-m9`, `claude/m9-afk-work`, `fix/audit-afk-fixes`,
`feat/harness-headless-ci-spike`, `feat/172-pivot-speed-followup`, all 10
`worktree-agent-*` branches, `feat/194-behind-the-back` (#211),
`feat/195-ball-sweep` (#208), `feat/198-moving-crossover` (#205),
`feat/96-steal-committed-move` (#168), `feat/98-block-committed-move` (#215),
`feat/101-input-map-defensive-actions` (#167), `docs/97-steal-block-turnover-paths`
(#166).

**`docs/95-defense-model-adr`** — RE-VERIFIED directly (not just trusted from a
digest): `git log main..docs/95-defense-model-adr --oneline` shows exactly one
commit, `c56074c` ("ADR-0018 defensive timing-window & reaction-tilt model
(#95)"). At first glance that looks like "1 commit ahead, unmerged." It is
NOT — `git diff c56074c e524e08 -- docs/adr/0018*` (where `e524e08` is the
commit that actually landed ADR-0018 on `main` via a sibling branch/PR #165)
is EMPTY: the two commits are patch-identical, just different commit objects
with different parents, so git counts `c56074c` as "not an ancestor of main"
even though its content already exists on `main`. This branch is a stale
duplicate pointer with zero unmerged value. **Safe to delete.**
(If you re-run this check yourself and get a different `main..branch` commit
list, the situation may have changed — trust your own re-run over this
paragraph.)

**`feat/80-81-authoritative-heading`** — the ONE branch that is genuinely NOT
safe to delete without a look. `git diff main...feat/80-81-authoritative-heading
--stat` (three-dot, from merge-base) shows **1055 lines across 11 files**:
`docs/adr/0009-shot-accuracy-scatter.md`, `docs/adr/0010-authoritative-heading.md`,
`scripts/Ball/BallController.cs`, `scripts/Ball/ShotFacing.cs` (new file),
`scripts/Player/HeadingMath.cs` (new file), `scripts/Player/PlayerController.cs`,
`tests/Hooper.Ball.Tests/HeadingMathTests.cs`, `Hooper.Ball.Tests.csproj`,
`tests/Hooper.Ball.Tests/ShotFacingTests.cs`, `tests/Hooper.Ball.Tests/ShotMakeCurveTests.cs`.
This is NOT merely orphaned test coverage — it includes source files
(`ShotFacing.cs`, `HeadingMath.cs`). However, spot-checking `main` shows
`scripts/Ball/ShotFacing.cs` already exists there today, added independently
via `0bedc22` (`feat(ball): off-facing shot accuracy penalty`) and corrected
via `b73b9c2` and `216c7b9` — i.e. `main` already has a (later-corrected)
equivalent implementation via a completely different commit lineage. This
strongly suggests the branch's version is SUPERSEDED, not missing work — but
this was not exhaustively diffed line-by-line against the current
`ShotFacing.cs`/`HeadingMath.cs` implementations, so **do not delete this
branch on this skill's say-so alone**. Before deleting: diff the branch's
`ShotFacing.cs`/`HeadingMath.cs` against `main`'s current versions of the
same logic and confirm no unique correctness fix is stranded there.

---

## Recurring meta-patterns

1. **Self-audit cascade.** A feature merges, then an immediate follow-up
   review/audit pass on that SAME diff finds 2–4 more bugs, which land as
   small, fast fix PRs days apart. Seen in steal (#96→#174→#175/176/177/179→#182,
   story 1), block (#98, story 2), and ball-hand-sweep (#194/#195, story 8).
   **Implication:** after landing anything in the defense/committed-move
   family, expect and budget for a same-week second pass — it is not a sign
   the first pass was low quality, it's this repo's normal cadence.

2. **The harness falsifies its own premises, not the product code.** Twice,
   independently (story 2 and story 9 / issue #217), a harness "success"
   scenario was itself broken because code-built scene trees carry raw C#
   export defaults that `Main.tscn` silently overrides in production. Every
   new harness scenario needs its OWN proof that its control/success path can
   actually succeed — "the assertion passed" is not enough if the assertion
   was vacuously satisfiable.

3. **Choke-point bypass is a repeating bug shape, not a one-off.** Both the
   steal saga and the block saga independently discovered the same bug:
   a new committed move called `_machine.Begin()` directly instead of going
   through the shared `BeginCommittedMove()` choke point, silently skipping
   the stale-pivot-latch clear that choke point performs. This happened
   twice, in unrelated PRs, weeks apart, and was not obvious from reading
   either diff in isolation — it only became visible once someone asked "did
   this go through the choke point?" **If you add a new committed move type,
   explicitly verify it enters via `BeginCommittedMove()`, don't just verify
   it "works."**

---

## When NOT to use this

- Diagnosing a bug that's happening RIGHT NOW and you don't yet know if it's
  new or a repeat → use `hooper-debugging-playbook` for live triage first;
  come back here once you have a symptom to match against.
- Deciding whether you're allowed to make a change (ADR discipline, afk vs
  hitl, merge gates) → use `hooper-change-control`.
- Need the current system map / invariants rather than history → use
  `hooper-architecture-contract`.

---

## Provenance and maintenance

- **Date-stamped:** 2026-07-12. Verified against `main` at HEAD `3085ee1`
  ("Merge pull request #215 from JoseTomanan/feat/98-block-committed-move"),
  444 commits total at verification time (corrected 2026-07-15; originally
  overstated as 446).
- **Every commit SHA above was checked with `git show --stat -s <sha>`**
  before being written down; one digest typo (`e4de264` → corrected to
  `e4de463`) was caught and fixed during authoring. If a SHA in this file
  ever fails to resolve, that's a signal history was rewritten or this file
  has bit-rotted — re-verify before trusting anything else in the same
  entry.
- **Re-verification commands:**
  - Confirm current HEAD and commit count: `git log --oneline -1` and
    `git rev-list --count HEAD`.
  - Re-check any SHA cited here: `git show --stat -s <sha>`.
  - Re-check the `docs/95-defense-model-adr` branch verdict:
    `git log main..docs/95-defense-model-adr --oneline` (expect exactly one
    commit, `c56074c`) then `git diff c56074c e524e08 -- docs/adr/0018*`
    (expect empty output — patch-identical).
  - Re-check the `feat/80-81-authoritative-heading` branch verdict:
    `git diff main...feat/80-81-authoritative-heading --stat` (expect ~1055
    insertions across 11 files if nothing has changed).
  - Re-check open/closed status of any issue cited here (#189, #206, #214,
    etc.): `gh issue view <number> --json number,title,state`.
  - Re-check a branch is still fully merged before deleting it:
    `git diff main <branch> --stat` (expect empty output for anything listed
    under "safe to delete" above; if it's no longer empty, something changed
    and this file needs updating, not the branch deleting).
