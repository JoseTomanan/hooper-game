---
name: hooper-diagnostics-and-tooling
description: How to run and interpret hooper-game's EXISTING shipped measurement instruments (for designing NEW analyses from first principles, see hooper-proof-and-analysis-toolkit). Load this to run the shot-scatter characterization instrument (the 5 intentionally-skipped xUnit theories), run the full headless-harness scenario matrix locally (scripts included), add a *ForHarness observability property, interpret a harness scenario failure / dotnet test output / the CI integration-job log, or diagnose client/server divergence with tick-stamped instrumentation. Keywords - make percentage, characterization, grid sweep, harness scenario, exit code, [harness] output, PASS/FAIL, divergence, GD.Print instrumentation, run-harness-local.
---

# hooper-diagnostics-and-tooling — measure, don't eyeball

This repo's culture is "numbers or it didn't happen": tuning claims come from
deterministic sweeps recorded in `docs/analysis/`, behaviour claims come from
headless harness scenarios with exit-code verdicts, and divergence claims come
from server-vs-client log comparison. This skill is the operator's manual for
those measurement surfaces.

Jargon: the **harness** is the headless Godot integration-test surface
(ADR-0016) — real scenes run with `--headless` whose process exit code is the
verdict. A **characterization test** is a test whose job is to *measure and
document* current behaviour, not to gate CI.

All commands run from the repo root. The repo path and the game csproj name
both contain spaces — **always quote paths**.

---

## Surface 1 — The shot-scatter characterization instrument panel

File: `tests/Hooper.Ball.Tests/ShotScatterCurveCharacterizationTests.cs`.
This is a deliberate "instrument panel", not broken tests. It measures the
live make-percentage curve of the real physics chain
(`ShotScatter → ShotArc → RimBackboard`) and records the numbers in its own
doc comments and in `docs/analysis/0079-shot-scatter-curve.md`.

Structure (as of 2026-07-15):

| Part | Theory | What it measures |
|---|---|---|
| 1 | `OpenShotMakeCurveCharacterization` | Open-shot make% at 1–11 m |
| 2 | `MovementPenaltyAt5m` | Movement multiplier sweep (mult 1.0–1.8) |
| 3 | `ContestPenaltyAt5m` | Contest multiplier sweep (mult 1.0–2.0) |
| 4 | `FacingPenaltyAt5m` | Facing-angle sweep (0°–180°) |
| 5 | `CombinedPenaltyScenariosCharacterization` | Stacked penalties at 2 m / 5 m / 6.75 m |
| 6 | `DefaultsMatchShotMakeCurveBands` | **Unskipped cross-check** — runs in CI |

Parts 1–5 carry `[Theory(Skip = "characterization: run manually; not a CI
gate")]`. Part 6 stays live in CI: it asserts the constants duplicated at the
top of the file (`Spm = 0.026`, `MaxScatter = 0.45`, …) still mirror
`BallController`'s live `[Export]` defaults — if it goes red, the instrument
panel's recorded numbers have silently drifted from the shipped game.

### How the sweep works (the honest-measurement pattern)

- **Stratified deterministic grid, no RNG**: `MakePct()` sweeps a 100×100
  centroid grid of the unit square — sample (i,j) = ((i+0.5)/100, (j+0.5)/100)
  — feeding `ShotScatter.Scatter(...)`'s `angle01`/`radius01` inputs directly.
  10,000 samples per data point, bit-identical across runs.
- **Full physics per sample**: each sample flies a real `ShotArc` and resolves
  contacts through `RimBackboard.Resolve` for up to 600 ticks at `dt = 1/60`.
  `Make` = make; `Bounce` or falling below ball radius = miss.
- **Closed-form cross-check**: make% ≈ `(0.11 / rMax)²` for `rMax > 0.11 m`
  (inner-radius disc-area ratio). Measured deviates from closed form in
  documented, explained ways (rim-outs below it at 4–5 m; the 3-D capture
  cylinder above it at ≥ 6 m — NOT "glass assists"; board contact is a miss).

### Running the skipped theories — what actually works

`dotnet test --filter` **selects but does not un-skip**. Verified 2026-07-15:

```
dotnet test "tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj" --configuration Debug --filter "FullyQualifiedName~ShotScatterCurveCharacterizationTests.OpenShotMakeCurveCharacterization"
```

matches exactly 1 test and reports it `[SKIP]` — xUnit honours the `Skip`
argument regardless of the filter. Two further catches:

1. To execute a skipped theory you must **temporarily delete the
   `Skip = "..."` argument locally** (leave `[Theory]`), run the filtered
   command above, then revert with `git checkout -- tests/`. Never commit the
   un-skip.
2. Even un-skipped, a *passing* `Assert.True(cond, message)` **does not print
   its message** — xUnit only surfaces messages on failure. The recorded
   numbers were originally captured with a throwaway console app that reuses
   the same source files and simulation loop (per
   `docs/analysis/0079-shot-scatter-curve.md`, "not committed"). So to READ
   fresh measured values, either:
   - build a scratch console app in your scratchpad that `<Compile Include>`s
     `scripts/Ball/ShotScatter.cs`, `ShotArc.cs`, `RimBackboard.cs` and copies
     the `IsMake`/`MakePct` loop (see hooper-proof-and-analysis-toolkit for
     the recipe), or
   - locally invert the assertion (`Assert.True(false, $"...")`) so every data
     point "fails" and dumps its measured/closed-form line, then revert.

### Where results get recorded

`docs/analysis/NNNN-<slug>.md` (existing example:
`docs/analysis/0079-shot-scatter-curve.md`). Required shape: header block
(**Issue**, **ADR**, **Date**, **Purpose**), a **Simulation method** section
(grid, make condition, harness file, standard error), a **constants table**
snapshotting every `[Export]` default the numbers depend on (numbers are
declared invalid if those change), then the results tables with a closed-form
column. See hooper-docs-and-writing for the house format.

---

## Surface 2 — Harness scenarios as instruments

(Ownership note: the golden scenario inventory, the exit-code contract, and
the ADD-A-SCENARIO runbook are OWNED by `hooper-verification-and-qa` — if
this section and that skill ever disagree, that skill wins. This section
covers only the running/interpreting side.)

A harness scenario is not just pass/fail — it is an instrument that observes
internal state you cannot see from outside. Two observation mechanisms:

### The `internal *ForHarness` properties

All five live on `scripts/Ball/BallController.cs` (verified 2026-07-15):

| Property | Line | Exposes |
|---|---|---|
| `LastToucherPeerIdForHarness` | 791 | `_lastToucherPeerId` (OOB/steal attribution) |
| `DribblePhaseForHarness` | 802 | `_dribble.Phase` (steal vulnerable-band checks) |
| `SweepActiveForHarness` | 812 | `_sweepActive` (crossover ball transit) |
| `SweepIsBehindBodyForHarness` | 819 | `_sweepIsBehindBody` (BTB vs crossover discriminator) |
| `VelocityForHarness` | 830 | `CurrentVelocity()` (swat/knock trajectory) |

They are `internal`, so they are visible to `tests/integration/**` (compiled
into the SAME game assembly via the csproj's `<Compile Remove="tests/**">` +
`<Compile Include="tests/integration/**">` trick) but invisible to external
consumers. Each exists to prove something otherwise only indirectly
observable — e.g. "no sweep occurred" is distinguishable from "sweep already
finished" only by sampling `SweepActiveForHarness` every tick.

**To add one**: add an expression-bodied `internal` getter next to the
existing five in `BallController.cs`, named `<Thing>ForHarness`, with a
comment saying which scenario needs it and why the observation is otherwise
unreachable. Read-only getters only — a harness must observe, never mutate,
production state (mutation goes through a `*HarnessSeam.cs` file calling the
production choke point, e.g. `BeginCommittedMove`; see
hooper-verification-and-qa for the seam rules).

### `[harness]` console output and exit codes

Conventions (grep-verified in `tests/integration/*.cs`):

- Single-instance scenes print progress as `GD.Print("[harness] ...")` and
  failures as `GD.PrintErr("[harness] FAIL ...")`, ending with
  `[harness] PASS — ...` or `[harness] RESULT: FAIL — N failure(s).`
- Dual-instance scenes use the `[net-harness]` prefix plus a role tag
  (`server`/`client`); their bash orchestrators log as `[run-net-<name>]`.
- Exit-code contract (ADR-0016): the scene calls `GetTree().Quit(0)` for PASS,
  `Quit(1)` for FAIL. **Any other exit code means the harness itself crashed**
  (uncaught C# exception, script-load failure) and CI treats it as failure.

---

## Surface 3 — Running the scenario matrix locally (shipped scripts)

Two equivalent scripts live in this skill's `scripts/` directory. Both parse
the live scenario matrix out of `.github/workflows/ci.yml` (every
`godot --headless --path . res://tests/integration/*.tscn` invocation, in CI
order), so they cannot drift from CI. As of 2026-07-15 that is **30 scenario
invocations across 10 scenes**. Both need a local Godot **4.6.3 .NET** binary
(not on PATH by default; CI gets one via `chickensoft-games/setup-godot@v2`,
version 4.6.3, `use-dotnet: true`). On Windows use the `*_console.exe`
variant so `[harness]` output reaches your terminal.

Build the game assembly first (harness code compiles into it):

```
dotnet build "HOOPER GAME.csproj" --configuration Debug
```

Git Bash:

```
GODOT="/c/path/to/Godot_v4.6.3-stable_mono_win64_console.exe" bash .claude/skills/hooper-diagnostics-and-tooling/scripts/run-harness-local.sh
bash .claude/skills/hooper-diagnostics-and-tooling/scripts/run-harness-local.sh --list           # print matrix only, no binary needed
bash .claude/skills/hooper-diagnostics-and-tooling/scripts/run-harness-local.sh "$GODOT" --stop-on-fail
```

PowerShell 5.1:

```
powershell -File .claude\skills\hooper-diagnostics-and-tooling\scripts\run-harness-local.ps1 -Godot "C:\path\to\Godot_v4.6.3-stable_mono_win64_console.exe"
powershell -File .claude\skills\hooper-diagnostics-and-tooling\scripts\run-harness-local.ps1 -List
```

Default behaviour: run everything, print a PASS/FAIL summary table, exit 1 if
anything failed. `--stop-on-fail` / `-StopOnFail` aborts at the first red.
Exit 2 = usage/environment error (no binary, ci.yml missing/unparseable).

The **dual-instance** scenarios are NOT in the matrix — run them individually
via Git Bash (they are bash orchestrators; ports 23456–23459 hardcoded,
`SERVER_BIND_WAIT` default 6 s, env-overridable):

```
bash tests/integration/run-net-handshake.sh "$GODOT"
bash tests/integration/run-net-state-sync.sh "$GODOT"
bash tests/integration/run-net-node-replication.sh "$GODOT"
bash tests/integration/run-net-behindtheback-sweep.sh "$GODOT"
```

Each launches a server in the background, waits, runs the client in the
foreground, and uses the **client's** exit code as the verdict.

---

## Interpretation guides

### Reading a scenario failure

1. Find the `[harness] FAIL ...` line (it is on stderr). It names the exact
   assertion and usually the tick, e.g. which observed value diverged from
   the expected one. If instead the run died with a stack trace and an exit
   code that is neither 0 nor 1, the harness itself crashed — treat as a
   harness bug or a compile/scene-load problem, not an assertion verdict.
2. Match the failing assertion back to the scenario's intent: every ci.yml
   step carries a dense comment block explaining what each scenario proves
   and what a RED means. Read that comment before touching code.
3. Ask "is the control scenario green?" — every "X didn't happen" assertion
   in this repo has a counterfactual scenario (e.g. `BlockTurnoverTest`
   `control-make` proves the unblocked shot scores, so `success`'s "still
   0-0" is non-vacuous). A red control with a green success means the harness
   premise broke, not the mechanic. See hooper-debugging-playbook for the
   triage table (code-built trees get raw C# defaults, off-by-one tick
   observation, etc.).

### Reading `dotnet test` output

A green run reads: **Passed: 664, Failed: 0, Skipped: 5, Total: 669**
(counts as of 2026-07-15 — re-verify, they grow). **The 5 skips are expected
permanently**: they are the characterization theories above, skipped by
design. Do not "fix" them, and do not mistake `Skipped: 5` for a regression.
Conversely, `Skipped: 6+` means someone skipped a real test — investigate.

Also remember the two-compile-surface asymmetry: the test project enables
ImplicitUsings, the game project does not, so `dotnet test` can be green while
`dotnet build "HOOPER GAME.csproj"` fails. Always run both (CI does).

### Reading the CI integration-job log

Workflow `CI`, job `integration-test` (`.github/workflows/ci.yml`). Steps run
in the same order as the local scripts' matrix, one named step per scene,
multi-scenario scenes as multi-line `run:` blocks (the FIRST failing
invocation in a block aborts the block).

```
gh run list --workflow CI --limit 5
gh run view <run-id> --log-failed
```

Reading order: (1) which step failed — the step name names the mechanic;
(2) within the step, which scenario invocation was last started; (3) the
`[harness] FAIL` line. Special cases: the `Generate .NET bindings & import
project` step deliberately tolerates non-zero (`|| exit 0` — first headless
launch can fail spuriously while bootstrapping .NET glue); a step failing
with no `[harness]` output at all usually means scene-load or assembly-load
failure — check the `Build game assembly` step and Godot's `ERROR:` lines.

---

## Capturing a new measurement honestly

The standard this repo holds measurements to (follow all five):

1. **Deterministic inputs** — stratified centroid grids over the parameter
   space, never RNG draws. If randomness is unavoidable, fixed seed, and say
   so. Bit-identical reruns are the acceptance test for the method itself.
2. **Measure the real chain** — drive the actual pure classes
   (`ShotScatter`, `ShotArc`, `RimBackboard`, `HeadingMath`, …), not a
   re-implementation of what you believe they do.
3. **Closed-form cross-check where possible** — derive an analytic
   approximation and explain every deviation mechanistically. Precedent: the
   first scatter-curve write-up attributed excess long-range makes to
   "backboard assists", which is impossible (board contact is a miss); the
   real mechanism was capture-cylinder geometry, corrected in a follow-up PR.
   Don't trust your causal story until you've checked it against the actual
   contact-resolution code.
4. **Record method + numbers in `docs/analysis/NNNN-<slug>.md`** — header
   (Issue/ADR/Date/Purpose), simulation method, a constants snapshot table
   (declare the numbers invalid if constants change), results tables with the
   closed-form column. Recipes: hooper-proof-and-analysis-toolkit. Format:
   hooper-docs-and-writing.
5. **Guard against drift** — if the analysis duplicates live constants, add
   (or extend) an unskipped cross-check test in the style of
   `DefaultsMatchShotMakeCurveBands` that fails when the duplicates and the
   `[Export]` defaults diverge.

---

## Divergence diagnosis tooling (client vs server)

When a client rubber-bands, mispredicts, or renders the wrong thing, the
instrument is a **dual-instance run with tick-stamped logs from both
processes**, diffed.

### Dual-instance scripts as a divergence instrument

The `run-net-*.sh` scripts are the template: same scene, `--harness-role=server`
and `--harness-role=client`, client exit code is the verdict. For divergence
work you usually want both processes' logs separately — run the two
invocations yourself in two terminals (or background one) redirecting each to
a file:

```
"$GODOT" --headless --path . res://tests/integration/<Scene>.tscn -- --harness-role=server --harness-port=24000 > /tmp/server.log 2>&1 &
sleep 6
"$GODOT" --headless --path . res://tests/integration/<Scene>.tscn -- --harness-role=client --harness-port=24000 > /tmp/client.log 2>&1
```

Use a port outside 23456–23459 (those belong to the committed scripts).
Topology matters: remote-DISPLAY proofs need `NetworkManager.HostGame`
(listen-server owns player "1"), not `StartDedicatedServer` — see
hooper-netcode-reference.

### Temporary tick-stamped instrumentation

`GD.Print` is pure I/O — it cannot break determinism — but the discipline is:

- **Tick-stamp every line** so the two logs align:
  `GD.Print($"[dbg t={Engine.GetPhysicsFrames()}] {(Multiplayer.IsServer() ? "S" : "C")} pos={GlobalPosition} state={State}");`
- Use a **distinct prefix** (`[dbg ...]`) — `[harness]`, `[net-harness]`, and
  `[BallController]`-style prefixes are already taken by committed code.
- Print **inputs and outputs of one suspect step**, not everything — you are
  building a comparable trace of a single computation on both peers.
- Diff: sort/align both logs on the tick number and compare fields. The first
  tick where server and client values differ is where prediction and
  authority parted; work backwards from there (stale broadcast? replay gap?
  see hooper-netcode-reference for the known accepted divergences before
  declaring a bug).
- **Never gate logic on the instrumentation** (no behaviour inside an
  `if (Debug)`), and **REMOVE every `[dbg` line before commit**. Check with:
  `grep -rn "\[dbg" scripts/ tests/` — must return nothing. Note `scripts/`
  legitimately contains ~60 permanent `GD.Print`/`GD.PrintErr` calls with
  class-name prefixes; do not confuse those with your temporaries, and do not
  bulk-delete `GD.Print`.

---

## When NOT to use this

- **Symptom triage** ("shot windup plays but never releases", "CI red but
  local green", frame off-by-ones) → **hooper-debugging-playbook** — it maps
  symptoms to causes; this skill is for building the measurements that
  discriminate between them.
- **Pass/fail proof for closing an issue** (what counts as evidence, adding a
  new harness scenario/seam, the ADR-0015/0016 gates) →
  **hooper-verification-and-qa**.
- **First-principles analysis recipes** (worked examples of deriving a
  closed form, building a scratch console sweep) →
  **hooper-proof-and-analysis-toolkit**.
- **Doc formats** for `docs/analysis/`, ADRs, handoffs →
  **hooper-docs-and-writing**.
- **Build/env problems** (Godot binary provisioning, csproj traps) →
  **hooper-build-and-env**; day-to-day running of the game/server →
  **hooper-run-and-operate**.

---

## Provenance and maintenance

Authored 2026-07-15 (per the 2026-07-12 discovery digests, load-bearing facts
re-verified live against the repo on 2026-07-15); description sharpened same
day to draw the run-existing-instruments vs. build-new-analysis boundary
against `hooper-proof-and-analysis-toolkit`. Verified against:

- `tests/Hooper.Ball.Tests/ShotScatterCurveCharacterizationTests.cs` (Skip
  attributes, constants, grid loop, cross-check theory) — read directly.
- The `--filter` behaviour (matches 1 test, stays `[SKIP]`) — executed live.
- The five `*ForHarness` properties and their line numbers — grepped live.
- `.github/workflows/ci.yml` scenario matrix (30 single-instance invocations,
  10 scenes, 4 dual-instance scripts) — grepped live.
- `[harness]`/`[net-harness]` print conventions — grepped live.
- Both shipped scripts' `--list` output and no-binary error paths — executed
  live (full matrix run additionally requires a local Godot 4.6.3 .NET
  binary; the parse/summary logic does not).
- `docs/analysis/0079-shot-scatter-curve.md` — read directly.

Re-verification commands for facts that drift:

- Unit-test counts (664/5/669): `dotnet test "tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj" --configuration Debug`
- Scenario matrix size/content: `bash .claude/skills/hooper-diagnostics-and-tooling/scripts/run-harness-local.sh --list`
- `*ForHarness` inventory: `grep -n "ForHarness" scripts/Ball/BallController.cs`
- Skipped-theory inventory: `grep -n "Skip =" tests/Hooper.Ball.Tests/ShotScatterCurveCharacterizationTests.cs`
- Characterization constants vs live defaults: CI's `DefaultsMatchShotMakeCurveBands` (unskipped) fails on drift.
