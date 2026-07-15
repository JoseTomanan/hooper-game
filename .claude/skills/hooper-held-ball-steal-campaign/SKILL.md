---
name: hooper-held-ball-steal-campaign
description: "Executable, decision-gated campaign for issue #206 — a dead-Held ball is steal-immune (pump-fake-mash beats any steal read; a stalling holder is untouchable; no travel/5-second pressure exists). Load when picking up #206 or any task about Held-ball steal vulnerability, closely-guarded/5-second rules, cradle-vs-steal race, or 'steals don't work against a held ball'. Provides the reproduce-first baseline, ranked solution menu with ADR-0014 citations, the human decision gate, per-option implementation plans, required harness scenarios with controls, and fenced-off wrong paths."
---

# Campaign: kill the steal-immune Held ball (issue #206)

This is a **campaign skill**: an ordered, gated sequence of phases for one
specific live problem, chosen by the human (2026-07-12) as the hardest live
problem worth a dedicated runbook. It is NOT a general how-to. If issue #206
is closed, this skill is historical — check first:

```
gh issue view 206 --json state -q .state
```

**Jargon at first use.** *Held* is a `BallState` where the holder carries the
ball in-hand, not bouncing it. *Dead Held* means the holder has already used
their dribble for this possession (`HasDribbled == true`) and cannot resume
bouncing (`DeadDribbleRule`, issue #193). *Cradle* is the automatic
Dribbling→Held transition fired when a jump shot begins (the "gather").
*Steal read* is the two-axis timed steal of ADR-0018 §2 (dribble-phase band +
correct hand). *Pump-fake* is a feinted `JumpShot` (aborted during its
Startup feint window). A *harness scenario* is a headless Godot integration
test per ADR-0016 (`tests/integration/`, exit code 0 = pass).

## The problem, precisely (verified against code 2026-07-15)

`BallController.ResolveStealAttempts` (`scripts/Ball/BallController.cs`,
method at ~line 1407) opens with:

```csharp
if (StateMachine.Current != BallState.Dribbling) return;
```

That single branch is the whole gap. Steals resolve **only while Dribbling**.
Meanwhile:

1. **Pump-fake-mash beats any steal read.**
   `PlayerController.BeginCommittedMove` (~line 1131) calls
   `BallController.CradleForShotStartup` (~line 2394) the instant a
   `JumpShot` begins, which **synchronously** flips Dribbling→Held
   (`StopDribble()`) and sets `HasDribbled = true`. So a holder who sees a
   steal wind-up (StealMove Startup = 8 ticks of visible telegraph) just
   taps shoot: the ball is Held before the defender's Active window opens,
   the steal whiffs by construction, and the holder feints the shot away
   (JumpShot feint window: startup frames [3, 12)). The dribble-rhythm read
   M10 is built on is no-op-able on reaction — the degenerate incentive
   inverts the whole mind game.
2. **A dead-Held staller is untouchable.** After a feinted pump-fake (or by
   never driving from the Held tipoff, #193), no mechanic in the codebase
   can force a turnover from Held. Travel/5-second rules were deliberately
   left out of #193's scope ("bare-minimum realism"), so stalling is free.

`TickHeld()` (~line 1701) confirms: it only places the ball in-hand and
checks shot release — no exposure, no dribble phase, nothing a steal could
target. `DefensiveResolution` (`scripts/Ball/DefensiveResolution.cs`) has two
predicates — the interval `Succeeds` (~line 59) and the per-tick
`StealSucceeds` (~line 84) — and neither is ever evaluated against a Held
ball because the caller returns before reaching them.

---

## Phase 0 — Reproduce & baseline

Do not design anything until you have demonstrated the gap *today*, on the
current main. All commands run from the repo root
(`C:/Users/The King/Documents/GitHub/hooper-game` — path contains spaces,
always quote). `godot` means a Godot **4.6.3 .NET** binary on PATH or via an
env var you set; CI provisions it with `chickensoft-games/setup-godot@v2`,
`version: 4.6.3`, `use-dotnet: true`.

**Step 0.1 — Confirm the gating branch still exists.**

```
grep -n "BallState.Dribbling) return" "scripts/Ball/BallController.cs"
```

Expected: one hit inside `ResolveStealAttempts` (~line 1409, as of
2026-07-15). If the hit is gone, or `ResolveStealAttempts` now handles
`BallState.Held`, **STOP** — someone has already landed a fix; check
`git log --oneline --grep="206"` and reconcile before doing anything.

**Step 0.2 — Baseline build + unit tests.**

```
dotnet build "HOOPER GAME.csproj" --configuration Debug
dotnet test tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj --configuration Debug
```

Expected: build success; **664 passed, 5 skipped, 669 total** (count as of
2026-07-12 — a higher count is fine, a *lower* count or any failure means
your baseline is broken; fix that first, it is not part of this campaign).

**Step 0.3 — Control: prove the steal DOES work against a Dribbling ball.**

```
godot --headless --path . res://tests/integration/StealTurnoverTest.tscn -- --harness-scenario=success
```

Expected: exit code 0. This is the control for everything that follows —
it proves the steal machinery, harness seams, and scene wiring are healthy,
so a later "steal never fires vs Held" observation means *Held-gating*, not
broken plumbing. Note: `StealTurnoverTest._Ready()` calls
`_ball.StateMachine.StartDribble()` (~line 154) explicitly to escape the
Held tipoff — that line is the workaround issue #206 names.

**Step 0.4 — Demonstrate the gap with a red harness scenario.**

There is (as of 2026-07-15) **no existing scenario that exhibits the gap** —
you must write the demonstration. Add a scenario (suggested name
`held-steal-immune`) to `StealTurnoverTest.cs` or a new
`HeldStealTest.cs` + minimal `.tscn`, following the harness conventions in
**hooper-verification-and-qa** (HarnessArgs flag parsing, code-built tree,
`Players` node added before `Ball`, latch observations at event time). The
scenario script:

1. Build holder + defender + ball; leave the ball **Held** (post-#193 tipoff
   is already Held — do NOT call `StartDribble()`).
2. Drive the defender through a real `StealMove` via the harness seam
   (`BeginStealForHarness` in `tests/integration/StealHarnessSeam.cs` — and
   note the seam law: seams must route through `BeginCommittedMove`, never
   `_machine.Begin()` directly).
3. Sweep the steal's 8-tick Active window across every alignment you like.
4. Assert, with observations latched at each tick: ball state never leaves
   `Held`, `LastToucherPeerIdForHarness` never becomes the defender, no
   `GoLoose()` ever fires.

Expected observation today: the assertion "steal never connects" **passes on
every alignment** — that green is the demonstration of the bug (immunity),
and it doubles as the future *control* scenario ("held-still-immune-
outside-window") once a vulnerability window exists. If you instead see a
steal connect against Held → the gap is already fixed; branch to Step 0.1's
STOP path.

**Step 0.5 — Demonstrate the pump-fake race (optional but recommended).**

Extend the scenario: start Dribbling, begin the defender's steal timed so
its Active window would overlap the exposed dribble band
(`StealLoExposed=0.35`..`StealHiExposed=0.65`, code defaults, ~lines
425/434), then have the holder `Begin(JumpShot)` during the steal's Startup.
Expected today: `CradleForShotStartup` flips to Held before the Active
window opens → steal whiffs; holder feints; ball state ends Held,
possession unchanged. That is the degenerate exchange the campaign must
kill. (Beware the documented ~1-tick Reliable-vs-UnreliableOrdered channel
race in `CradleForShotStartup`'s doc comment — single-instance harness code
calls it synchronously, so the race does not apply headless.)

Record all Phase-0 outputs (exit codes, latched tick numbers) in the issue
thread before proceeding.

---

## Phase 1 — Design decision gate (THIS IS A DESIGN CALL)

**GATE: the choice below is a genuine design call under ADR-0014's
escalation rules** — it defines new possession-pressure rules (ADR-0008
territory) and a new vulnerable window (ADR-0018 territory). The campaign
**prepares the decision brief**; a human (or a session the human has
explicitly authorized to decide) makes the call. A Sonnet worker must NOT
silently pick an option and start coding. Post the brief (menu below +
Phase-0 evidence) on #206 and wait for / obtain the decision.

### The menu, ranked

**(a) Held-ball steal-vulnerability window — RECOMMENDED CANDIDATE.**
Real-ball tier (ADR-0014 tier 2): in real half-court 1v1, a held ball is
*not* sanctuary — defenders reach in and poke at a cradled ball; it is
riskier than stealing a live dribble (better protected, body shielding),
but not immune. Theory obligation: ADR-0018 defines steal success as Active
overlapping a **vulnerable interval** — so this option MUST define what the
vulnerable interval of a static Held ball *is*. Candidates to grill:
  - *Proximity + facing-based exposure*: the Held ball is vulnerable while
    the defender is within reach AND the holder's authoritative `Heading`
    (ADR-0010, never cosmetic `FacingResolver`) leaves the ball-side hand
    exposed toward the defender. Continuous predicate, so it implements
    like the steal's per-tick amendment form (ADR-0018 Amendment
    2026-07-01), not a precomputed interval.
  - *Pump-fake Startup/Recovery frames as the exposed windows*: the ball is
    vulnerable exactly while a `JumpShot`'s Startup (the gather/raise) or
    feint-Recovery (8 ticks, `feintRecoveryFrames`) is in progress. These
    ARE fixed integer-tick intervals, so this form can call
    `DefensiveResolution.Succeeds` directly, like block does. Design
    property worth stating in the brief: **this inverts the degenerate
    incentive** — feint-mashing would *create* vulnerability instead of
    immunity, while a patient triple-threat holder stays hard (not
    impossible) to steal from. That preserves the mind game rather than
    patching it.
  - A hybrid (static Held mildly exposed by proximity/facing; pump-fake
    frames heavily exposed) is also on the menu.
ADR to amend: **ADR-0018** (new §2 vulnerable window; keep the amendment
convention — dated `## Amendment`, in the **same commit** as the code, per
CLAUDE.md's Decision Discipline). Invariants
touched: server-only mutation (resolution stays inside the `IsServer` block
of `_PhysicsProcess`), no hidden RNG on defensive outcomes (window must be
a pure function of authoritative state: `Heading`, `HandSide`, positions,
`MovePhase` ticks), `BeginCommittedMove` choke point untouched (the steal
still begins there). Harness-provable: fully — window open/closed is exact
tick state.

**(b) Closely-guarded / 5-second rule.**
Real-ball tier: FIBA closely-guarded (5 seconds while guarded within ~1 m)
and the NBA/FIBA 5-second variants are genuine rules of the sport. Needs: a
server tick counter (counting ticks, converted for display — never
hardcoding "300 ticks = 5 s"; derive from `Engine.PhysicsTicksPerSecond`),
a proximity predicate (defender within an exported radius of the holder
while ball is Held), and a turnover consequence (route through
`AwardPossession(defenderPeerId, ...)` — the single possession-mutation
entry point — NOT a bespoke `GoLoose` path). Simplest to build and prove;
but it adds a **legibility obligation**: a hidden count violates the
"legibility is a competitive requirement" identity (CLAUDE.md §1), so it
carries a timer/pressure HUD element — a UI sub-task and a small feel
surface. Also note it punishes *stalling* but does nothing about the
*pump-fake race* (problem 1). ADR to amend: **ADR-0008** (possession
rules — this is a possession-turnover rule, not a defensive-timing rule);
ADR-0018 untouched. Invariants: server-only counter and turnover; counter
is discrete state — if it is ever broadcast, never force-match it on
clients (frame-counter staleness law). Harness-provable: fully — tick
counting is the harness's home turf (with a "4.9 s no-turnover" control).

**(c) Tick-order cradle-vs-steal resolution — NARROW.**
Resolves only the pump-fake race: a steal already in its Active window when
`Begin(JumpShot)` fires wins the tick (e.g. cradle refused or steal
evaluated against the pre-cradle Dribbling state on that tick). Reference:
real ball, weakly — "the poke beats a late gather" is defensible, but this
option leaves problem 2 (the untouchable staller) fully intact, so it
cannot close #206 alone. Cheapest diff: ordering/priority logic around
`CradleForShotStartup` and `ResolveStealAttempts` (note they currently run
in *different places*: cradle synchronously inside `BeginCommittedMove`,
steal in the server tick after the state switch — "tick order" here means
deferring the cradle's state flip to tick resolution, a real change, not a
line swap). ADR: amendment to ADR-0018 (steal window semantics) and a note
on ADR-0008's #193 amendment. Invariants: BeginCommittedMove stays the
choke point (do not move the cradle call site out of it; change what the
ball does with it). Harness-provable: fully — it is precisely a same-tick
race, the kind of thing the block release-tick top-up already proves.

**(d) Combinations.**
The strongest full answer to #206 as written is likely **(a) + (b)** — (a)
kills the pump-fake-immunity inversion, (b) kills the stall — or
**(a)-pump-fake-variant + (b)**, with (c) subsumed by (a)'s pump-fake
window. Cost: two ADR amendments (0018 + 0008), two scenario families, one
HUD element. The brief should present (d) as the "complete" option and let
the human trade completeness against scope.

For every option the brief must state: which ADR is amended, the exact
vulnerable-interval definition in ADR-0018's `[start, end)` terms (or why
it is the per-tick amendment form), the invariants list above, and the
named harness scenarios from Phase 2. Cite tiers per ADR-0014's
cite-or-ask convention in the eventual PR body.

---

## Phase 2 — Implementation plan (per chosen option)

Common rules regardless of option:

- **Pure class first.** New decision logic goes in a pure, engine-free
  class or into `DefensiveResolution` (`scripts/Ball/DefensiveResolution.cs`)
  with xUnit coverage in `tests/Hooper.Ball.Tests/` BEFORE any
  BallController wiring. No `Node`, no `Random`, no `DateTime`, no
  `Input.*` in pure code. New pure files must be added to the test csproj's
  explicit `<Compile Include>` allowlist or the tests silently won't see
  them.
- **Frame-data changes go through `MoveFrameData`** (`scripts/Input/
  MoveFrameData.cs`) — e.g. if (a)'s pump-fake variant needs the JumpShot
  feint-recovery window widened, that is a `MoveFrameData` constructor-arg
  change on `JumpShot.DefaultFrameData`, never an ad-hoc constant.
- **Discipline skill:** #206 is both well-specced (after the gate) and
  high-stakes (authoritative possession state) — run `/tdd` for the pure
  predicates and lean on `/doubt-driven-development` for the
  BallController tick-ordering decisions. State the choice on the issue.
- **Server-only wiring:** all resolution stays inside the existing
  `if (IsServer)` block of `BallController._PhysicsProcess`; respect the
  documented per-tick order (block → state switch → block top-up →
  player-OOB → steal → clear → broadcast). If your option needs a new step,
  document WHY at its insertion point like every existing step does.
- **Watch #216:** the defensive-plumbing consolidation issue may land first
  and move `ResolveStealAttempts`/`ResolveBlockAttempts` structure around.
  Before editing, `gh issue view 216 --json state -q .state`; if closed,
  re-locate your touch points in whatever shared plumbing it introduced.

### Option (a) touch points

- `scripts/Ball/DefensiveResolution.cs`: new pure predicate, e.g.
  `HeldStealSucceeds(...)` taking whatever authoritative inputs the chosen
  window definition needs (defender distance, holder `Heading`, defender
  bearing, `HandSide`, or JumpShot phase ticks). Unit tests: full boundary
  matrix (inside/outside band, wrong hand, exact-boundary half-open
  semantics).
- `scripts/Ball/BallController.cs` `ResolveStealAttempts`: replace the
  Dribbling-only early return with a state dispatch (Dribbling → existing
  path unchanged; Held → new predicate). On Held-steal success reuse the
  existing success block (GoLoose, `_arc` seed — the NRE lesson from #96:
  `GoLoose()` never seeds `_arc`, and TickLoose dereferences it on the next
  tick — knock velocity, `_lastToucherPeerId = defender`,
  `EndResolvedDefensiveMove()`). Extract that block into a shared private
  method rather than duplicating it (or ride #216's consolidation).
- Pump-fake-window variant additionally reads the holder's
  `CommittedMoveMachine` phase — the same authoritative source
  `ActiveStealTargetHand` already demonstrates; the ball reads players
  AFTER they tick (Players-before-Ball node order is load-bearing).
- New exports (window bounds/reach radius) on `BallController` with doc
  comments marking values provisional, tuning deferred to #104.
- **Required harness scenarios** (single-instance):
  - `held-vulnerable` — a correctly-aimed steal inside the defined window
    forces the turnover from Held (state leaves Held via GoLoose; last
    toucher = defender, latched at the event tick).
  - `held-still-immune-outside-window` — the CONTROL: identical setup, the
    one window predicate falsified (out of reach / wrong facing / outside
    the pump-fake frames / wrong hand), steal never fires, ball stays Held.
    Without this control, `held-vulnerable` proves nothing about the window
    shape (the "every X-didn't-happen assertion needs a control" law).
  - `pumpfake-now-exposed` (pump-fake variant only) — the Phase-0.5
    degenerate exchange rerun: the same pump-fake-on-reaction that dodged
    the steal in Phase 0 now LOSES the ball. This scenario is the
    campaign's headline success metric.
- Client display: a Held-steal turnover rides the existing
  `ReceiveState`/`ReconcileFromServer` force-snap path (state + holder), so
  no new dual-instance proof is strictly required — but if you add ANY new
  broadcast field or client-visible telegraph, a dual-instance scenario is
  mandatory (HostGame topology for remote-display proofs; ports
  23456–23459 are taken, use the next free one; co-occurrence on one tick,
  never independent latches).

### Option (b) touch points

- New pure class, e.g. `scripts/Systems/CloselyGuardedRule.cs`: tick
  accumulator + proximity predicate + reset conditions (ball leaves Held,
  defender leaves radius, possession change). Unit tests: accumulation,
  reset, exact threshold (half-open: turnover fires on the tick the count
  reaches the threshold, not before).
- `BallController._PhysicsProcess` (server block): advance the rule while
  Held, and on expiry award via `AwardPossession(defender, cleared:
  false)` — a discrete dead-ball turnover, matching ADR-0008's turnover
  grammar (contrast the steal's live-scramble GoLoose).
- Threshold export in ticks, derived-from-seconds in the doc comment only.
- HUD: pressure-count element (a `hitl`-verifiable legibility obligation —
  file the feel/visual half as a separate `hitl` issue per ADR-0013).
- **Required harness scenarios**:
  - `five-second-turnover` — guarded Held for ≥ threshold ticks →
    possession flips to the defender.
  - `four-nine-no-turnover` — the CONTROL: identical, one tick short of
    threshold → no turnover.
  - `guard-break-resets` — defender steps outside the radius mid-count,
    count resets, no turnover at the original expiry tick.

### Option (c) touch points

- `BallController`: the cradle no longer flips state synchronously against
  an in-flight steal — e.g. `CradleForShotStartup` records a pending cradle
  that the server tick applies AFTER `ResolveStealAttempts` has evaluated
  the tick (mind the documented ~1-tick channel race in its doc comment;
  your change must not widen it). Doubt-driven review mandatory here — this
  is exactly the same-tick-race territory where the block work burned two
  review cycles.
- **Required harness scenarios**: `steal-beats-late-cradle` (steal Active
  already open when JumpShot begins → turnover) + CONTROL
  `cradle-beats-early-steal` (JumpShot begun before the steal's Active
  opens → cradle wins, steal whiffs, ball Held).

---

## Phase 3 — Validation & promotion

All gates, in order, before merge (ADR-0015 — no merge on red, ever):

1. `dotnet build "HOOPER GAME.csproj" --configuration Debug` green.
2. `dotnet test tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj
   --configuration Debug` — all prior tests (669 total as of 2026-07-12)
   plus your new pure-class tests green.
3. Every new harness scenario green locally AND wired into
   `.github/workflows/ci.yml`'s `integration-test` job (one `godot
   --headless ... -- --harness-scenario=<name>` line per scenario, each
   with the conventional comment block explaining what it discriminates),
   and green in CI.
4. Existing scenario matrix still green — especially
   `StealTurnoverTest` (all scenarios), `TripleThreatTest`
   (`dead-dribble`, `production-drive` — your change must NOT resurrect
   the dead dribble or break the drive exit), `BlockTurnoverTest`.
5. `/code-review` with no unresolved correctness findings.
6. **ADR amendment in the same PR** as the code (Decision Discipline):
   dated `## Amendment` on ADR-0018 and/or ADR-0008 per the chosen option,
   including the rejected menu options and the ADR-0014 tier citation.
7. Feel residue — window sizes, knock speeds, guard radius, count
   threshold, HUD look — is NOT yours to sign off. File/point it at the
   tuning issue **#104** and the batched milestone feel pass **#114**
   (the #104/#114 pattern); values ship as provisional exports.
8. Branch `feat/206-held-ball-steal`; single-concern commits, conventional
   subjects, `Refs #206` in commit bodies; **`Closes #206` in the PR body
   only** — never in a commit subject/body, and only if the PR fully
   resolves the issue (a partial landing keeps ALL closing keywords out
   and uses `Refs` only).

**Success is measurable, never judged by eye:** the named scenarios green
in CI *plus* the Phase-0 degenerate exchange demonstrably dead — the
`pumpfake-now-exposed` (or option-(b) `five-second-turnover`) scripted
exchange shows the previously-untouchable holder losing the ball, with the
turnover latched at an exact tick.

---

## WRONG PATHS — fenced off

| Do not | Why |
|---|---|
| Add hidden RNG to any steal/Held outcome | Violates ADR-0018's core rationale ("a read, not a roll") and the ADR-0003 arcade anti-goal. A percentage roll is the explicitly rejected alternative. |
| Client-predict the steal/turnover outcome | Defensive outcomes are server-only by invariant (steal/block resolve inside `IsServer`; acting client sees results ~1 RTT later). Clients reconcile, never decide. |
| Bypass `BeginCommittedMove` (call `_machine.Begin()` directly, in code OR harness seams) | Burned twice (steal and block sagas independently): skips the pivot-latch clear, dead-dribble gate, and JumpShot cradle. All Begin call sites route through the choke point. |
| Force-match a frame counter in any new reconcile field | Broadcast counters are ~1 RTT stale; the NETCODE LAW in `PlayerController.ReconcileFromServer` forbids comparing them. Force discrete identity only. |
| Hardcode tick counts as seconds (or vice versa) | Durations are tick counts derived from `Engine.PhysicsTicksPerSecond`; wall-clock timers break determinism (fixed-dt invariant). |
| Tune feel numbers solo (window widths, radii, thresholds, HUD) | Feel is never auto-accepted (ADR-0015). Provisional exports + #104/#114. |
| Skip control scenarios | Both prior defensive harnesses self-falsified without controls (vacuous "score unchanged"; #217 code-defaults trap). Every "X didn't happen" needs its counterfactual. |
| Rely on code-built-tree defaults for geometry | Code-built harness trees get raw C# export defaults, not `Main.tscn` overrides — `BoardCenter`/`RimCenter` defaults are internally inconsistent (#217). Force-set what matters. |

## Adjacent work — do not duplicate or break

- **#196** (position-based steal window during crossover transit, open as of
  2026-07-15): a *different* spatial steal term for a *Dribbling* ball
  mid-sweep. If your option (a) predicate wants proximity/position inputs,
  design the pure-function signature so #196 can reuse it — but do not
  implement #196's window here.
- **#216** (defensive plumbing consolidation, open): if it lands first, your
  BallController touch points move into its shared plumbing. Check its
  state before coding (command in Phase 2).
- **#189** (phantom shot / committed-move machine can't react to losing the
  ball mid-move, needs-triage): a Held-steal turnover mid-JumpShot-Startup
  WILL leave the shooter's machine ticking to completion — that is #189's
  known, deliberately-unresolved gap touching locked ADR-0003. Note the
  interaction in your PR body; do NOT expand scope into fixing it.

## When NOT to use this

- Harness mechanics (how to write/wire/debug scenarios, seams, dual-instance
  scripts) → **hooper-verification-and-qa**.
- Gates, branching, closing keywords, ADR discipline, afk/hitl split →
  **hooper-change-control**.
- Steal/block symptom triage unrelated to Held immunity →
  **hooper-debugging-playbook**.
- The invariants and system map in general → **hooper-architecture-contract**.
- ADR/amendment house style → **hooper-docs-and-writing**.

## Provenance and maintenance

Authored 2026-07-15; reviewed and corrected same day (Decision Discipline
wording tightened from "same PR" to "same commit" to match CLAUDE.md).
Verified
against: issue #206 body (`gh issue view 206`); ADR-0018 full text incl.
the 2026-07-01 amendment; live code on main at `3085ee1` —
`ResolveStealAttempts` (~BallController.cs:1407, Dribbling gate :1409),
`TickHeld` (:1701), `CradleForShotStartup` (:2394),
`BeginCommittedMove`/dead-dribble gate (PlayerController.cs:1131/1155),
`DefensiveResolution.Succeeds`/`StealSucceeds`, `StealMove` (8/8/20, feint
4), `JumpShot` (18/4/20, feint window [3,12), feint recovery 8), exports
`StealLoExposed=0.35`/`StealHiExposed=0.65`; unit-test count 669 (664+5
skipped) as of 2026-07-12; issues #196/#216/#189/#104 all OPEN as of
2026-07-15.

Staleness checks before executing:

```
gh issue view 206 --json state -q .state        # CLOSED → this skill is historical
grep -n "BallState.Dribbling) return" "scripts/Ball/BallController.cs"   # gate gone → fix landed
gh issue view 216 --json state -q .state        # CLOSED → touch points moved
dotnet test tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj --configuration Debug   # refresh baseline count
```

Line numbers drift with every BallController/PlayerController edit — treat
all `:NNNN` references as "grep for the symbol, expect it near here".
