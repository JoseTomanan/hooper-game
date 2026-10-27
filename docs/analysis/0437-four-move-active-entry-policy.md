# Four-move Active-entry policy pilot

- Issue: [#437](https://github.com/JoseTomanan/hooper-game/issues/437), parent #411
- Baseline: `69015098f08beae140555edbd4cef4eb7b66bae5`
- Implementation: `5f279204875c7816d44f162ba050ced04d10bf5f`
- Measurement: 2026-10-26–27, Godot `4.7.1.stable.mono.official.a13da4feb`, .NET 8
- Discipline: doubt-driven development; no locked ADR changes

## Boundary and maintenance assessment

`FourMoveBurstResolver` owns one concrete-type dispatch for recognition, normalized
payload sign, live tuning profile, existing calculation and requested hand swap.
Crossover alone selects `ComposeCrossoverActiveVelocity`; BehindTheBack and
BetweenTheLegs retain their own speed/forward/cone values; InAndOut selects the
shared calculation with a negated sign and no swap. Unsupported moves return
false with a default result. The immutable tuning value has no independent
defaults. The warmed all-family dispatch allocation test measures zero managed
bytes across 40,000 calls.

The controller passes eleven current exported values at entry, applies velocity
and authoritative hand mutation at the original point, then follows the existing
contact integration. Its Active-entry branch has no family list, sign rule,
profile switch or calculation selection. Startup gather, Recovery, other move
branches, move identity/machine/begin, ball effects, input, RPCs, reconciliation
and remote presentation were not changed.

The maintenance gain is a single policy boundary with independent pure and live
proofs; the controller still makes tuning ownership and event timing visible.
This pilot does **not** justify another extraction by itself. No further
extraction is recommended without a separately triaged, concrete policy boundary
and its own expected maintenance benefit. No follow-on extraction was started.

## Characterization and review

`FourMoveActiveEntryTest` runs one scenario per fresh engine process. A minimal
partial seam calls `BeginCommittedMove`, then the ordinary server-remote physics
path advances the move. An observer with later physics priority checks the actual
phase/frame and entry flag after each player tick. It latches entry hand and
velocity then, rather than reconstructing them at verdict time.

Eighteen representative cases cover all four moves, both initial hands and
direction signs (including fractional payloads), rotated headings, zero/nonzero
momentum, neutral/forward/lateral/backward exits, inside/outside cone edges and
two deadzone-adjacent inputs. Every profile has deliberately distinct nondefault
exports. Expected velocities call the pre-existing math with explicit independent
case parameters, never the new resolver. Existing formula tests remain the math
oracle; this work does not duplicate their entire matrix.

Assertions prove successful production Begin and stable move identity, Startup
gather, entry at Active frame zero, exactly one entry effect, the event-time
swap/no-swap distinction with paired sibling controls, all later Active ticks
after a discriminating stick change, and Recovery braking through completion.
Pure tests additionally cover zero payloads, unsupported/null moves, all-family
deadzone boundaries and repeated calls with changed tuning.

- Baseline: all 18 cases passed before production extraction. The forward
  Crossover/InAndOut pair was then strengthened with matching moving starts and
  both passed again on the unmodified production path before commit `7b0194d`.
- After extraction: all 18 cases and the existing local family cases passed.
  Moving-crossover host/client/reset-race network proofs passed with Git Bash.
- Ordinary (`IncludeIntegrationHarness=false`) and proof (`true`) builds passed
  with zero warnings/errors. Full unit suite: 1,313 passed, five intentional
  shot-scatter characterization skips. Repository suite: 106 run, one platform
  skip, no failures.
- Fresh-context design and extraction review identified deadzone discrimination
  gaps. Boundary cases for Crossover and then every sibling were added. Final
  `/code-review` of committed `69015098..5f27920` found no high-confidence
  introduced defects after behavior, repository-contract and history passes.

## Mutation discrimination

All eleven independent mutants compiled/executed and failed the intended
assertion. Every run began from the committed implementation, restored both
source files in a `finally` block and verified their original SHA-256 values.
The source diff was empty afterward. A clean proof rebuild, all 18 live cases
and the complete 1,313-pass unit suite then passed again.

| Deliberate error | Discriminating observation |
|---|---|
| Crossover selects shared composition | `crossover-moving`: wrong velocity at Active frame 0 |
| BehindTheBack selects BetweenTheLegs speed | `behind-neutral`: wrong entry velocity |
| BetweenTheLegs selects BehindTheBack cone | `between-lateral`: wrong entry velocity |
| InAndOut selects Crossover speed | `inout-neutral`: wrong entry velocity |
| InAndOut omits sign inversion | `inout-neutral`: opposite entry vector |
| InAndOut requests a hand swap | `inout-neutral`: expected Left, observed Right at entry |
| Controller omits `JustEnteredActive` gate | `crossover-moving`: velocity and hand change on Active frame 1 |
| Each of the four branches substitutes `.15f` for live deadzone `.12f` (four mutants) | The corresponding pure theory's `.13f` exit row fails `Assert.Equal`; four other rows still pass |

For example, the repeated-entry mutant's first entry was correct. At Active
frame 1 it produced `(1.9484224, 0, 14.158742)` and Left hand instead of the
latched `(-0.9159292, 0, 5.1310835)` and Right hand. The erroneous InAndOut
swap preserved the expected velocity exactly while independently failing the
event-time hand comparison. The BehindTheBack stale-deadzone mutant produced
`(0.74489427, 0, 1.3252708)` instead of `(4.0042515, 0, 6.097843)` on the
`.13f` row. These are assertion failures, not build/load/crash verdicts.

Retained evidence under [evidence/0437](evidence/0437/):

- `characterization-baseline.log.gz`: all 18 baseline native logs, using the
  strengthened moving forward-pair rerun for those two cases.
- `characterization-after.log.gz`: all 18 clean post-mutation native logs.
- `mutation-results.json`: commands, test exits, failure excerpts and original/
  restored source hashes for every mutant, followed by clean gate results.
- `mutation-logs.log.gz`: full mutation build/test/native logs and the exact
  executed campaign script. The script was run from `.godot/437-mutations/`;
  it is retained as execution evidence rather than a general mutation tool.

The exhaustive local catalog and hosted CI verdicts, including any failed
attempts and unchanged diagnostic reruns, are recorded in
[PR #438](https://github.com/JoseTomanan/hooper-game/pull/438). The executable
catalog remains the authoritative inventory; no selected subset substitutes
for its exhaustive gate.

## Scope of the evidence

This is behavior-preserving characterization: old and new production paths are
expected to be green. Deliberately broken variants establish discrimination.
The small fixture isolates policy/timing from opponents and ball state; existing
network, same-tick remote BehindTheBack sweep, committed-replay and production
contact proofs supply regression coverage. They do not independently prove every
sibling mapping.

The reliable exit-vector preference/fallback race is unchanged. Shot and
turnaround replay keep their special paths; other committed replay retains its
accepted neutral-motion approximation. No claim establishes exact historical
committed/contact replay, latency-neutral feel or animation quality. Feel/tuning
remain #173/#238. No editor or visual approval step is required.

## Reproduction

From the repo root, with the pinned Mono Godot binary in `GODOT`:

```powershell
dotnet build "HOOPER GAME.csproj" --configuration Debug -p:IncludeIntegrationHarness=false
dotnet build "HOOPER GAME.csproj" --configuration Debug -p:IncludeIntegrationHarness=true
dotnet test "tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj" --configuration Debug
python tools/harness_catalog.py run --scene FourMoveActiveEntryTest.tscn --run-id 437-characterization
python tools/harness_catalog.py run --all --bash "C:/Program Files/Git/bin/bash.exe" --run-id 437-exhaustive
```

On Linux, omit `--bash` to use the normal Bash executable. Catalog selection
preserves paired controls and injects unique workspace-local Godot logs. On this
Windows checkout, the default `bash` resolved to WSL; explicitly selecting Git
Bash fixed adapter launch failures. Restricted AppData temp access also broke
repository subprocess fixtures; the supported shell and unrestricted temp access
produced the passing repository result above. These were environment failures,
not production behavior changes.
