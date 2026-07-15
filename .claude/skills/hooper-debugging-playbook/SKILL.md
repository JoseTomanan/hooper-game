---
name: hooper-debugging-playbook
description: Fast-lookup symptom-to-triage table for hooper-game's recurring, repo-specific failure modes — use when a shot windup plays but the ball never releases, CI is red while dotnet test is green (or vice versa), you see CS0246 on [Fact], a harness scenario passes locally but its control scenario fails or the pass looks vacuous, a client rubber-bands or gets stuck after a steal, a steal/block seems to miss despite correct-looking timing, you get a NullReferenceException in ball flight, Godot logs "Failed to correctly scale shape" at load, a new committed move behaves subtly wrong (pivot latch / cradle / dead-dribble), harness timing is off by one frame, Grep/Glob returns duplicate hits, or Godot exits nonzero on the first headless CI run. Provides a triage table (symptom -> likely cause -> discriminating check) plus the one-paragraph story behind each trap and pointers to discriminating experiments.
---

# Hooper Debugging Playbook

Fast-lookup complement to `/diagnose` (the generic reproduce -> minimise ->
hypothesise -> instrument -> fix -> regression-test loop). This skill supplies
the repo-specific content; `/diagnose` supplies the process discipline. Read
`/diagnose` for HOW to run a diagnosis; read this skill for WHAT this repo's
failure modes actually are, because most of them have already happened once
and the fix is a known pattern, not a fresh investigation.

## When NOT to use this

- You want the generic reproduce/minimise/hypothesise loop discipline, not
  repo-specific traps -> use `/diagnose`.
- The bug you're chasing is a settled, closed battle and you want the full
  symptom -> root cause -> evidence -> status writeup (commit hashes, PR
  numbers, the whole story) -> use `hooper-failure-archaeology`.
- You want to understand WHY the netcode is shaped the way it is (prediction,
  reconciliation, determinism theory as applied here), not just "what breaks
  and how do I tell" -> use `hooper-netcode-reference`.
- You need to know how to WRITE a new harness scenario or unit test from
  scratch (file layout, `HarnessArgs`, CI wiring) -> use
  `hooper-verification-and-qa`; this skill only tells you how to use a
  scenario to discriminate between two hypotheses once you already have one.
- You need the full system map / invariants list to understand a subsystem
  you've never touched -> use `hooper-architecture-contract`.

## How to use this table

Find your symptom in the left column. The "likely cause(s)" column lists
candidates in the order to suspect them. The "discriminating check" column is
the fastest test that tells you WHICH cause you actually have — run that
before changing any code.

| Symptom | Likely cause(s) | Discriminating check |
|---|---|---|
| Shot windup plays but ball never releases | (A) Feint-gesture eating the shot — an incidental stick flick got read as a pump-fake. (B) Silent OOB turnover mid-windup — the shooter crossed the true rule boundary (not the visible white line) before release. | Add a one-tick log of `CommittedMoveMachine.CurrentMove` phase transitions AND `holder`/possession around the windup. (A) shows `Startup -> Recovery` with no `JustEnteredActive` ever true — feint path. (B) shows possession silently flip to the defender mid-`Startup`/`Active` with the move machine still ticking to completion (no possession loss for a committed move is not enforced — see `CommittedMoveMachine` no-`Cancel()` note below). Also check `CourtBounds.IsOutOfBounds` against the player's actual XZ, not against the rendered boundary line — the visible outline and the true rule boundary CAN differ if `CourtVisuals` scale ever regresses. |
| dotnet test green but CI red / game won't build | csproj compile-surface asymmetry: the test csproj has `ImplicitUsings` on, the game csproj does not, so a missing `using System;`-style statement compiles fine under xUnit and fails under `Godot.NET.Sdk`. Also remember `tests/integration/**/*.cs` compiles into the GAME assembly (via `<Compile Remove="tests/**/*.cs" />` then `<Compile Include="tests/integration/**/*.cs" />` in `HOOPER GAME.csproj`), so a harness-seam typo breaks the GAME build, not the test build. | Run `dotnet build "HOOPER GAME.csproj" --configuration Debug` locally BEFORE trusting a green `dotnet test`. If the game build fails but tests pass, the failing file is either missing an implicit `using`, or lives under `tests/integration/` and has a compile error that only shows up in the game assembly. |
| CS0246 on `[Fact]` (or any xUnit attribute) | Someone converted `tests/Hooper.Ball.Tests.csproj` to `Godot.NET.Sdk`, or added a `ProjectReference` to the game project instead of a bare `GodotSharp` NuGet package reference. This is a REJECTED approach, documented inline in the csproj: `Godot.NET.Sdk`/`ProjectReference` resolves xunit attributes against the wrong project. | Open `tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj` and check the `Sdk=` attribute (must be plain `Microsoft.NET.Sdk`) and the `<PackageReference>`/`<ProjectReference>` list (must reference `GodotSharp` as a bare package, no `ProjectReference` to `HOOPER GAME.csproj`). Revert to that shape rather than re-diagnosing from scratch. |
| Harness scenario passes locally, control scenario fails / the pass looks vacuous | Code-built trees (a `BallController`/`PlayerController` constructed in C# inside a harness `_Ready()`) carry raw `[Export]` C# defaults, NOT whatever `Main.tscn`'s property overrides would have baked in. The canonical trap: `RimCenter` defaults to `(0,3.05,0)` and `BoardCenter` defaults to `(0,3.5,0.3)` — the raw defaults put the board IN FRONT of the rim, so every arc bounces off the board and a "guaranteed clean make" control scenario times out 0-0, silently making the paired success scenario's "score unchanged" assertion meaningless. | Every new "X didn't happen" assertion needs a paired control scenario that proves the harness COULD produce the positive outcome (e.g. `BlockTurnoverTest`'s `control-make`: never begins the block, asserts the identical unblocked shot scores exactly once). If the control scenario itself fails, don't touch the feature under test — first check whether the harness's code-built geometry (rim/board placement, or any other `[Export]` default) matches what `Main.tscn` actually overrides. Mirror production's relative placement explicitly in the harness instead of relying on constructor defaults. |
| Client rubber-bands / move stuck after a steal (or any possession-changing defensive move) | Discrete-state staleness: something force-matched a ~1-RTT-stale broadcast field (most often `FrameInPhase`) instead of only force-snapping discrete identity (state enum, holder, `IsCleared`, `HasDribbled`). `ReconcileFromServer` has an explicit documented "NETCODE LAW" against comparing the stale `FrameInPhase`. | Check whether the fix instinct is to compare/force a broadcast frame COUNTER. It should instead be a level-triggered boolean flag that survives UnreliableOrdered packet drop (the shipped pattern: `CommittedMoveMachine.WasRecoveryEnteredEarly`, consumed by `ReconcileFromServer`'s `ShouldForceRecovery` step, distinct from the broader `ShouldForceInactive`). A bare `serverPhase == Recovery` check misfires on every ordinary Active->Recovery boundary under jitter — don't reintroduce that. |
| Steal/block seems to miss when the timing looked right | Two distinct bug shapes have hit this before: (A) point-sampling only the entry tick of a window instead of a per-tick interval-overlap check (steal, pre-#96 fix — ADR-0018 requires overlap). (B) evaluating the resolve check at the wrong point in the per-tick pipeline relative to state transitions that happen inside the same tick — the block off-by-one: `ResolveBlockAttempts` ran only before the per-state switch, but shot release happens INSIDE the switch, so the first real evaluation landed one tick late with the defender already in Recovery. | Log the exact tick numbers: defender's Active-window `[start,end)`, and the event tick (release tick for block, dribble-phase-crossing tick for steal). If the two intervals genuinely half-open-overlap (`aStart < vEnd && vStart < aEnd`, `DefensiveResolution.Succeeds`) but the result still reads a miss, check whether the resolve call happens BEFORE or AFTER the state-machine switch that consumes the event this tick, and whether there's a release-tick top-up call (the block fix added one specifically so a block can never lose a same-tick race with scoring). Also check tick ORDER: Players must tick before Ball in `Main.tscn` (`BallController.cs` reads `holder.HandSide` / defender's Active interval assuming players already advanced this frame) — reordering shifts block windows by one tick. |
| NullReferenceException in ball flight | `BallController._arc` (the `ShotArc` instance driving `TickInFlight`) was never seeded before a path that ends up in `InFlight`. The known instance: `GoLoose()` on a steal didn't seed `_arc`, so the FIRST steal before the ball's first shot crashed the server. | Grep every call site that transitions `BallStateMachine` into `InFlight` (or any path that later reads `_arc`) and confirm each one either goes through `ApplyShootLocally` (which constructs `_arc`) or explicitly seeds `_arc` itself. Any new path into `InFlight` that bypasses `ApplyShootLocally` also inherits the separate `_inFlightStartTick` staleness trap (used by the block window) — check both, not just the NRE. |
| Godot logs "Failed to correctly scale shape ... not supported by Jolt Physics" at load | A `CylinderShape3D`, `CapsuleShape3D`, or `SphereShape3D` has a non-uniform (mismatched X/Z) node `Scale`. Their cross-section is a single radius; Jolt silently clamps a non-uniform scale instead of honoring it, so the collider stops matching its mesh. `BoxShape3D` is exempt (independent X/Y/Z extents). | Find the offending node in the `.tscn` and check its `Scale` next to a round `*Shape3D` sub-resource. Fix by moving the size onto the SHAPE RESOURCE (`radius`/`height` properties) and setting the node's `Scale` back to `1` — never scale a round collider node itself. The visual `MeshInstance3D` sibling may still be freely scaled. |
| A new committed move behaves subtly wrong (pivot latch stays stale, ball cradle missing, dead-dribble gate not enforced) | The new move's `Begin` call site bypassed `PlayerController.BeginCommittedMove` — the single required choke point — and called `_machine.Begin()` (or equivalent) directly. `BeginCommittedMove` is what clears the pivot latch, enforces the dead-dribble rule, and cradles the ball for `JumpShot` startup; skipping it silently drops all three. This has happened TWICE in unrelated PRs weeks apart (a real committed-move feature, and a test harness seam file) — it is not obvious from reading the code in isolation. | Grep every call site of `_machine.Begin(` across `scripts/` AND `tests/integration/*HarnessSeam.cs`. Every one must route through `PlayerController.BeginCommittedMove`, not call `_machine.Begin()` directly. If you find a direct call, that's the bug — route it through the choke point instead of patching the specific symptom (pivot latch, cradle, dead-dribble) piecemeal. |
| Harness timing looks off by exactly one frame | Two independent, well-documented +1 sources, don't assume it's a new bug: (A) `Input.ActionPress`-driven scenarios — the just-pressed edge doesn't take effect until the NEXT physics frame after the call. (B) Parent/child tick order — a parent node (e.g. `BallController`) observes a child's (e.g. a `PlayerController`'s committed-move machine) state ONE FRAME LATER than the child itself advances, because `Main.tscn` ticks `Players` before `Ball` each frame and the child's own `_PhysicsProcess` already ran when the parent reads it. | If using `Input.ActionPress`, budget the assertion window to be frame-band tolerant (±1 tick) rather than exact-frame, or advance one extra idle tick after the press before asserting. If observing a child's state from a parent-ticking harness, add the documented "+1" adjustment (see `StealTurnoverTest`'s `ComputeBeginFrame` for the shipped pattern) instead of guessing at a new off-by-one. |
| Grep/Glob returns duplicate hits across the repo | `.claude/worktrees/` contains 10+ stale full worktree copies of the repo (leftover from past parallel-agent sessions) that are NOT excluded by default from a repo-root search. | Scope the `path`/`glob` argument away from `.claude/worktrees/` (e.g. search under `scripts/`, `tests/`, `scenes/` explicitly rather than the repo root) instead of trying to dedupe results after the fact. |
| Godot exits nonzero on the very first headless run in CI | Expected, not a real failure: the FIRST `godot --headless --build-solutions --quit` on a fresh checkout is doing .NET bindings bootstrap and can spuriously return non-zero even though it succeeded. `ci.yml` deliberately appends `\|\| exit 0` to that one bootstrap step — the REAL pass/fail gate is the scene-run steps that follow it. | Confirm the failure is specifically the bootstrap step (`--build-solutions --quit`), not one of the numbered scenario invocations that follow. If it's the bootstrap step, it is a known no-op swallow, not a regression — look at the actual scenario steps' exit codes instead. |

## Discriminating experiments

For a repo-specific bug you can't place in the table above, write the SMALLEST
possible harness scenario or unit test that forces the two competing
hypotheses to disagree, rather than adding more logging to the existing
scenario. Two starting points:

- **Which layer is wrong — pure logic or the node/tick glue?** Pure math/rules
  live in engine-free classes (`ShotArc`, `RimBackboard`, `DefensiveResolution`,
  `CommittedMoveMachine`, `Scoreboard`, `HeadingMath`, …) covered by the
  `tests/Hooper.Ball.Tests` xUnit suite (fast, no Godot process). If a bug
  could live in either the pure rule or the tick sequencing that calls it,
  write a plain xUnit test against the pure class FIRST — if it passes in
  isolation, the bug is in the node glue (tick order, reconciliation,
  RPC boundary), not the rule itself. Full "how to add a test" runbook lives in
  `hooper-verification-and-qa` — this skill is only telling you which layer to
  suspect first.
- **Is this a client/server divergence?** If the symptom only shows up for
  the non-local player (a remote copy misbehaving while your own predicted
  copy looks fine), it's a reconciliation or display-sync bug, not a rules
  bug. Use the dual-instance harness pattern (`run-net-*.sh` scripts,
  `hooper-run-and-operate` has the full runbook) to run two real headless
  Godot processes and diff what each one believes. Remember the topology
  gotcha: `NetworkManager.StartDedicatedServer` (headless authoritative host)
  has NO own player, so proving something about the REMOTE DISPLAY of a
  player (not just server-authoritative outcomes) requires `HostGame` (listen
  server) topology instead, so the server process owns a real simulated
  player and the client gets a genuine remote copy of it.
- When you do write a new scenario, follow the two disciplines already burned
  into this codebase: latch any observation AT THE EVENT TICK (not by
  re-reading state later — a later `AwardPossession`/recovery can silently
  overwrite the field you meant to check), and pair every "X didn't happen"
  scenario with a control scenario proving the harness COULD have produced X
  (see the code-defaults row above for why a missing control makes a pass
  vacuous).

## Provenance and maintenance

Authored 2026-07-12. Verified against the live repo at commit `3085ee1`
(HEAD, PR #215 merged) by direct `Read`/`Grep` of: `scripts/Ball/BallController.cs`
(`RimCenter`/`BoardCenter` export defaults, lines ~142/162), `scripts/Player/PlayerController.cs`
(`WasRecoveryEnteredEarly`/`ShouldForceRecovery`, lines ~554-1541),
`scripts/Input/FeintGateResolver.cs`, `tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj`
(CS0246/`ImplicitUsings` comment, lines ~5/18-24), and a live count of
`.claude/worktrees/` (11 entries at verification time — table says "10+", re-run
`ls .claude/worktrees | wc -l` if this drifts). Cross-referenced against sibling
discovery reports on git archaeology (steal #96/#174-182, block #98/#215,
OOB/feint #188) and the architecture/test-harness digests produced in the same
research pass.

Re-verification commands:
- `git -C "C:/Users/The King/Documents/GitHub/hooper-game" log -1 --format=%H` —
  confirm you're still at or ahead of `3085ee1`; if far ahead, spot-check that
  cited line numbers haven't drifted.
- `dotnet build "HOOPER GAME.csproj" --configuration Debug` and
  `dotnet test "tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj" --configuration Debug`
  — confirm the csproj asymmetry story is still accurate (game build and test
  build should both be green today; if `ImplicitUsings` gets added to the game
  csproj, that row of the table goes stale).
- `ls "C:/Users/The King/Documents/GitHub/hooper-game/.claude/worktrees" | wc -l`
  — re-check the stale-worktree count.
- If a NEW recurring trap gets hit twice, add it as a new row here rather than
  filing it only in `hooper-failure-archaeology` — this skill is specifically
  for the fast-lookup triage table, archaeology is the deep story.
