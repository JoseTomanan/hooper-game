---
name: hooper-proof-and-analysis-toolkit
description: First-principles analysis recipes for hooper-game — load this to BUILD a new measurement or proof from scratch (for running the instruments that already ship in the repo, see hooper-diagnostics-and-tooling). Triggers - designing a new characterization sweep, explaining WHY a measured number diverges from theory, asserting that something did NOT happen, reasoning about tick windows/off-by-ones at phase boundaries, deriving a physics constant (restitution, integration scheme), or reviewing a merged diff for bugs the author could not see. Provides six recipes, each with a worked example from this repo's real history, plus the project's evidence bar for research-grade claims.
---

# hooper-proof-and-analysis-toolkit

The core discipline of this repo is **"prove it, don't just claim it."** Code
that compiles is not done; a fix without a discriminating test is not proven; a
causal explanation that was never checked against the code is a liability. This
skill packages the six analysis methods this project actually uses, each as a
recipe with a worked example drawn from the repo's own commit history — so you
can see what the method looks like when it worked (and, twice, when it caught
its own author being wrong).

Jargon used throughout, defined once:

- **Harness** — the headless Godot integration-test surface under
  `tests/integration/` (ADR-0016). Scenes boot with `--headless`, script the
  game, and `Quit(exitCode)`: 0 = pass.
- **Committed move** — a discrete action (crossover, jump shot, steal, block)
  with Startup/Active/Recovery frame phases that cannot be cancelled (ADR-0003).
- **Tick** — one fixed physics step, `1/Engine.PhysicsTicksPerSecond` (60 Hz).
  All gameplay timing is integer tick counts, never wall-clock time.
- **Doubt cycle** — a fresh-context adversarial review pass over a decision or a
  merged diff (see the `/doubt-driven-development` skill).

---

## Recipe 1 — Deterministic characterization sweep

**Use when:** you need to know what a tunable system *actually does* across its
input space — a make-% curve, a penalty response, a timing envelope — as a
numeric baseline for tuning or for detecting drift.

**Steps:**

1. **No RNG.** Replace random sampling with a stratified centroid grid: for an
   N×N sweep of the unit square, sample (i, j) = ((i+0.5)/N, (j+0.5)/N). The
   result is bit-identical across runs — reruns discriminate real change from
   noise.
2. **Drive the full production chain, not a model of it.** Call the same
   pure classes production calls, in the same order, with the same constants.
   A simplified re-derivation measures your model, not the game.
3. **Stability check.** State the sampling standard error and confirm the
   numbers are stable to about ±1 percentage point before publishing them.
4. **Pin the constants.** Record every `[Export]` default and geometry value
   the numbers depend on, and add an unskipped cross-check test asserting the
   duplicated constants still match the live defaults — so silent drift fails CI.
5. **Record it in `docs/analysis/NNNN-<slug>.md`** (number = the driving issue).
   Report measurements; deliberately stop short of feel recommendations — feel
   is the human's call (ADR-0015).

**Worked example — the shot-scatter make-% curve
(`docs/analysis/0079-shot-scatter-curve.md`, issue #79):**
a 100×100 grid (10,000 deterministic samples per data point) pushed through the
real `ShotScatter → ShotArc → RimBackboard` chain, up to 600 simulated ticks per
shot, standard error ≈ ±0.5 pp at 40% make rate. It produced the project's
tuning anchors (100% ≤ 3 m, 67% at 5 m, 41% at 6.75 m vs. the NBA open-three
baseline of 38–40%, 21% at 10 m). The living instrument is
`tests/Hooper.Ball.Tests/ShotScatterCurveCharacterizationTests.cs`: Parts 1–5
are `[Theory(Skip = ...)]` — a manual instrument panel, not broken tests —
while the Part 6 cross-check (`DefaultsMatchShotMakeCurveBands`) stays unskipped
in CI to catch constant drift between the doc file and `BallController`'s live
exports.

---

## Recipe 2 — Closed-form cross-check + causal-story verification

**Use when:** you have measured numbers and are about to write down *why* they
look the way they do.

**Steps:**

1. **Derive the analytic prediction** (e.g. the flat-disc area formula for
   make-% under uniform scatter) before or alongside the measurement.
2. **Compare.** Where measurement diverges from the closed form, that gap is
   the interesting finding — name it and quantify it.
3. **Check your explanation against the code before writing it.** Open the
   actual resolution code and confirm the mechanism you're about to name is
   *possible*. A plausible story that the code forbids is worse than "unknown".
4. If a published explanation turns out wrong, correct the *prose* in a
   follow-up PR and say so explicitly — measured numbers and their explanation
   have separate lifecycles.

**Worked example — the "backboard assist" that was impossible (PR #117 →
corrected in PR #132):** the first write-up of the #79 curve attributed
measured make-% *exceeding* the closed form at long range to "backboard/glass
assists." That mechanism is impossible in this codebase: `RimBackboard.Resolve`
returns `Bounce` on any board contact and the make test counts any Bounce as a
miss — the board can only *reduce* makes. The real mechanism, corrected in PR
#132: the 3D make test is a vertical **capture cylinder**, not a flat disc, so
steep heavily-scattered arcs can still drop in where the disc model says miss.
The measurements were never wrong; only the causal prose was. The lesson is
step 3 verbatim: check the story against the contact-resolution code before
publishing it.

---

## Recipe 3 — Control-scenario counterfactual design

**Use when:** any test asserts a **negative** — "the shot did NOT score," "no
sweep occurred," "the state did NOT change."

**Steps:**

1. For every "X didn't happen" assertion, add a sibling **control scenario**
   proving X *was possible* in this exact setup with the one causal ingredient
   removed. Without it, a broken setup passes vacuously.
2. Run the control FIRST when debugging: if the control fails, your premise is
   wrong, not the product code.
3. In this repo specifically, assume **code-built scene trees carry raw C#
   export defaults, not `.tscn` overrides** — geometry that works in
   `Main.tscn` can be nonsense in a harness-built tree. (See the
   hooper-debugging-playbook skill for the full trap table.)
4. **Latch observations at event time.** Capture the fact on the tick it occurs;
   never re-read live state at verdict time, because later legitimate
   transitions (scramble recovery, Recovery expiry) overwrite it.

**Worked example — `BlockTurnoverTest`'s `control-make` scenario (block saga,
issue #98 / PR #215):** the `success` scenario asserts a blocked shot leaves
the score 0-0; `control-make` runs the identical shot with no block and asserts
it scores exactly once. On first CI run **the control itself failed**
(`8051e28`): the code-built tree used `BallController`'s raw export defaults,
where `BoardCenter` sits *in front of* `RimCenter`, so every control arc
bounced off the board and timed out 0-0 — which would have made `success`'s
"score unchanged" pass vacuously forever. The harness falsified its own premise
before it could certify anything. Two sibling fixes in the same PR show step 4:
`50e79f2` (the defender-Recovery observation was read at verdict time, after
the 20-tick Recovery had legitimately expired — latch it at block time) and
`fb5ac54` (the counterfactual wait was derived from an observed field that is
provably never set on the success path — derive it from the *predicted* release
frame instead).

---

## Recipe 4 — Integer-tick interval reasoning

**Use when:** anything involves timing windows, phase boundaries, or "did A
overlap B" — defensive windows vs. vulnerable windows, release ticks,
harness frame scheduling.

**Steps:**

1. **Model windows as half-open integer intervals `[start, end)`.** The overlap
   predicate is `activeStart < vulnEnd && vulnStart < activeEnd` — this is
   ADR-0018's `DefensiveResolution.Succeeds`
   (`scripts/Ball/DefensiveResolution.cs`), shared by steal (#96), block (#98),
   and contest (#99) so all three carry one auditable success definition.
   Two adjacent intervals — one ending exactly where the other begins — do
   NOT overlap. Half-open kills the classic
   fencepost ambiguity ("is the last frame in or out?") by construction.
2. **Hunt off-by-ones at phase boundaries, not mid-window.** Ask: on the exact
   tick a phase transitions, which code runs first — the evaluation or the
   transition? Per-tick pipeline order (documented in `BallController`'s
   `_PhysicsProcess`) decides who wins same-tick races.
3. **Choose edge vs. level triggers deliberately under packet loss.** A
   one-tick edge flag on an UnreliableOrdered channel can be dropped and gone
   forever; a level signal (true for the whole duration of a state) survives.
4. **Account for observer offsets in harnesses:** the just-pressed edge of
   `Input.ActionPress` lands next frame, and a parent node observes its
   children one frame late — harness frame math carries an explicit `+1` for
   this (see `ComputeBeginFrame` in the steal harness).

**Worked examples:**

- **Block release-tick off-by-one (`c8a0cd8`, PR #215):**
  `ResolveBlockAttempts()` ran only *before* the per-state switch, but the shot
  release happens *inside* the switch — so a defender whose Active window's
  last legal frame was exactly the release tick T was first evaluated at T+1,
  already in Recovery, and the block silently missed. Fix: a second,
  release-tick-only, server-only evaluation guarded against double-resolve.
- **`WasRecoveryEnteredEarly` as a level signal (issue #175, `f68eea0`):** when
  the server ends a defender's Active early, the client must stop predicting
  Active. A one-tick edge would be lost to UnreliableOrdered packet drop, so
  the flag is deliberately level-triggered — true for the entire Recovery
  duration — and paired with a narrow `ShouldForceRecovery` predicate (a bare
  `serverPhase == Recovery` check would misfire on every ordinary
  Active→Recovery boundary under jitter).

---

## Recipe 5 — Physical-constant derivation

**Use when:** you need a physics constant (restitution, gravity shaping, an
integration scheme) and are tempted to eyeball-tune it.

**Steps:**

1. **Find a real-world spec** the constant must satisfy (a regulation, a
   measured range, a rulebook number).
2. **Derive the constant** from that spec analytically.
3. **Pin it with a test** that re-asserts the real-world behaviour, not the
   constant itself — so a future retune must consciously break a named,
   real-world claim.
4. For numerical schemes: derive the *error term* of the candidate integrators
   and pick based on where the error lands in gameplay, not on generic
   accuracy folklore.

**Worked examples:**

- **Floor restitution 0.76:** derived from the NBA ball-inflation specification
  (drop from regulation height must rebound into a legal band), pinned by
  `FloorBounceTests.RegulationDrop_ReboundTop_LandsInNbaLegalBand` in
  `tests/Hooper.Ball.Tests/`. Changing the COR now fails a test named after
  the rulebook claim it violates.
- **Trapezoidal integration for the shot arc (`ShotArc.Step`):** semi-implicit
  Euler undershoots position by `0.5*g*t*dt` per unit time — at 60 Hz over a
  full arc that is several centimetres, enough to clang shots off the front
  rim. The fix chosen from the error analysis, not trial and error:
  average-velocity (trapezoidal) stepping, `Position += 0.5*(oldVel+newVel)*dt`,
  which is exact for constant acceleration. The derivation lives as a comment
  in `scripts/Ball/ShotArc.cs`.

---

## Recipe 6 — Adversarial doubt-cycle review of merged work

**Use when:** a non-trivial diff has merged and you want the bugs the author's
own context blinded them to. (For *in-flight* review posture, use the
`/doubt-driven-development` skill instead — this recipe is the post-merge
variant.)

**Steps:**

1. **Fresh context.** The reviewer must not be the session that wrote the code.
   Read the merged diff cold, with the ADRs, and ask "what would make this
   wrong?" per hunk.
2. **Hunt author-blindness classes:** order-of-operations at tick boundaries,
   caches clobbered by transient states, fail-open error paths, code that
   bypasses a shared choke point, negatives asserted without controls.
3. **Every finding becomes one of exactly two things:** an immediate small fix
   PR, or an **explicitly-accepted gap** recorded on the record (an issue, or
   at minimum a commit message + code comment). "Noticed and forgotten" is not
   an outcome.
4. Findings in this repo are labelled in-code as dated "doubt cycle N,
   finding #M" comments (prefix with the issue number if the file already has
   labels from an earlier milestone).

**Worked examples:**

- **`AdvanceHandSweep` review (`2a05d30`, issues #194/#195):** two real defects
  found post-merge in the ball hand-sweep: (1) the tick counter advanced
  *before* sampling `t`, so the final active sample landed short of `t = 1` — a
  one-tick visual "pop" at the end of every sweep; (2) a transient null holder
  unconditionally clobbered the cached `_lastObservedHandSide`, dropping the
  next tick's flip detection. Both fixed in one commit. A third finding was
  **accepted, not fixed** (`16bb309`): a mispredicted BehindTheBack's
  self-correcting reverse sweep can render via the wrong path — documented as a
  cosmetic self-correcting trade-off in the commit message and code comments
  (deliberately no tracking issue).
- **The steal audit cascade (issue #96 → #174–#182):** the merged steal
  mechanic was re-reviewed repeatedly; each pass found real bugs the previous
  pass introduced or exposed — entry-tick-only sampling instead of per-tick
  window checks, a NullReferenceException on any steal before the first shot
  (`GoLoose()` never seeded the arc), wrong last-toucher attribution, a frozen
  dribble phase letting one window fire multiple turnovers, and the client-side
  prediction desync of Recipe 4's `WasRecoveryEnteredEarly`. Four generations
  of small fix PRs, each traceable to a named finding. This cascade — merge,
  self-audit, immediate fix PRs — is the repo's dominant quality pattern.

---

## THE EVIDENCE BAR (research methodology)

These are the standards a claim must meet in this repo before it is treated as
knowledge rather than opinion:

1. **One mechanism must explain ALL observations, including the negatives.** If
   your explanation covers the makes but not the rim-outs, it is not the
   mechanism. The #79 correction happened because "backboard assist" could not
   explain how a miss-only interaction increased makes.
2. **Hypotheses must predict numbers BEFORE running.** Write down the expected
   value (closed form, derived constant, expected tick) first; then measure.
   Post-hoc agreement with an unstated prediction is not evidence.
3. **A fix without a discriminating RED/GREEN test is not proven.** The test
   must fail on the pre-fix code and pass on the post-fix code, and its CI
   step should document what it discriminates (every harness step in
   `.github/workflows/ci.yml` carries such a comment).
4. **Accepted-not-fixed is a legitimate outcome — but it must be recorded.**
   Either an issue (the #189 pattern: contradicts a locked ADR, so it goes to
   the human) or an explicit commit message + code comment (the `16bb309`
   pattern). The record is the deliverable; silence is the failure mode.
5. **The idea lifecycle here:** hunch → doubt cycle or spike → measured
   analysis doc → ADR amendment *or documented retirement*. The two canonical
   paths: spike #87 (`docs/spikes/0011-animationtree-text-authoring.md`,
   31/31 checks, PASS → lifted an ADR-0011 exclusion) and the #79 curve
   (`docs/analysis/0079-shot-scatter-curve.md` → ADR-0009 tuning section).
   An idea that dies should die on the record, with the measurement that
   killed it.
6. **Where good findings have historically come from** — prioritize these
   activities when hunting for defects: (a) **post-merge self-audit** of your
   own just-merged diff (the steal cascade, the hand-sweep review); (b) **the
   harness falsifying its own premises** (the control-make backboard, the
   BoardCenter/RimCenter defaults — issue #217); (c) **human feel reports**
   ("shooting not working" led to two real bugs, PR #188). Notably NOT a
   historical source: speculative refactoring or generic lint sweeps.

---

## When NOT to use this

- You want the **instruments themselves** — how to run the harness, the
  characterization tests, diagnostic scripts, or how to measure instead of
  eyeball — that is **hooper-diagnostics-and-tooling**.
- You want the **in-flight review posture** while writing code, not a
  post-merge audit method — invoke **/doubt-driven-development**.
- You want the symptom→cause triage table for known failure modes — that is
  **hooper-debugging-playbook**; settled historical battles live in
  **hooper-failure-archaeology**.
- You want what counts as merge evidence / the harness runbook — that is
  **hooper-verification-and-qa**.
- You want the open research problems these methods could be applied to —
  that is **hooper-research-frontier**.

---

## Provenance and maintenance

Written 2026-07-12 against repo HEAD `3085ee1` (merge of PR #215); description
sharpened 2026-07-15 to draw the build-new-analysis vs. run-existing-
instruments boundary against `hooper-diagnostics-and-tooling`. Verified
directly: commits `8051e28`, `50e79f2`, `fb5ac54`, `c8a0cd8`, `2a05d30`,
`16bb309`, `f68eea0` (subjects match the claims above);
`docs/analysis/0079-shot-scatter-curve.md` (grid method, ±0.5 pp SE, constants
table); `ShotScatterCurveCharacterizationTests.cs` skip/cross-check structure;
`DefensiveResolution` half-open overlap predicate; `ShotArc` trapezoidal-step
rationale (`scripts/Ball/ShotArc.cs:43-56,182-183`); the
`RegulationDrop_ReboundTop_LandsInNbaLegalBand` test
(`tests/Hooper.Ball.Tests/FloorBounceTests.cs:435`);
`DefensiveResolution.Succeeds` (`scripts/Ball/DefensiveResolution.cs:59-60`).
PR merges verified: #117 = `b09c6a4` (first curve write-up, `73bf82e`),
#132 = `3a666cc` (mechanism correction, `ebc10eb`), #215 = `3085ee1`.

Re-verify on drift:

- Commit hashes still resolve:
  `git log --oneline -1 8051e28 c8a0cd8 2a05d30 16bb309 f68eea0` (run per hash).
- Analysis doc unchanged constants:
  `git log --oneline -- docs/analysis/0079-shot-scatter-curve.md`
- Characterization cross-check still unskipped and green:
  `dotnet test tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj --configuration Debug --filter DefaultsMatchShotMakeCurveBands`
- Floor-COR pin still present:
  `dotnet test tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj --configuration Debug --filter RegulationDrop_ReboundTop_LandsInNbaLegalBand`
