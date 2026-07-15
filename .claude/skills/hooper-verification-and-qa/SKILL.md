---
name: hooper-verification-and-qa
description: What counts as evidence in hooper-game, and the complete runbook for adding proof. Load this before closing any issue, claiming a behavior change is "done," writing a new xUnit test or headless-harness scenario, or judging whether a PR's test coverage is adequate. Covers the proven-by-harness evidence bar (ADR-0015/0016), the unit-test-vs-integration-harness-vs-dual-instance decision guide, the golden test inventory, the copy-pasteable ADD-A-SCENARIO runbook, and the assertion disciplines that keep harness proofs honest (control scenarios, event-time latching, code-built-tree defaults, dual-instance co-occurrence).
---

# Hooper verification & QA

This is the repo's answer to "how do I prove this is done?" It does not cover
*how to develop* (that's `/tdd`), *how to run the game/server locally*
(`hooper-run-and-operate`), or *merge/issue-close mechanics*
(`hooper-change-control`). It covers **what counts as evidence** and **how to
add more of it**.

## When NOT to use this

- Writing the feature itself, red-green-refactor loop → `/tdd`.
- Measuring an existing behavior, characterizing a curve, profiling →
  `hooper-diagnostics-and-tooling`.
- Deciding whether a PR is mergeable, ADR/closing-keyword mechanics, afk/hitl
  labeling → `hooper-change-control`.
- Running the game, dedicated server, or a harness scenario locally for
  manual poking (not authoring new proof) → `hooper-run-and-operate`.
- Diagnosing *why* a scenario is red → `hooper-debugging-playbook`.

---

## 1. The evidence bar

**"Done means proven" now means proven-by-harness** (ADR-0015, ADR-0016). The
prover moved from a human to the headless harness; the bar (proof before
close) did not move.

- A `hitl` issue whose acceptance criteria are **state-checkable** closes when
  a green integration-harness assertion covers it in CI. `Closes #X` may ride
  the same PR as the code.
- A criterion that is **irreducibly feel** (does this read right, does this
  *feel* fair) closes only at the batched per-milestone human feel pass (the
  #114 pattern) — never auto-accepted, never inferred from harness green.
- **Never close on code/compile alone.** A passing build or a green
  `dotnet test` run is necessary, not sufficient — see the asymmetry below.
- **Unit-test green alone is insufficient for engine-facing work.** The game
  project (`HOOPER GAME.csproj`) and the test project
  (`tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj`) are two separate
  compile surfaces: the test project enables `ImplicitUsings`, the game
  project does not, so `dotnet test` can stay green while
  `dotnet build "HOOPER GAME.csproj"` fails outright (e.g. `MathF` resolving
  via the implicit `using System;` in tests but not in the game build). CI
  builds the game project explicitly for exactly this reason (the comment at
  the top of `.github/workflows/ci.yml` says so) — so must you, locally,
  before claiming green.
- **Node-derived code is unreachable by unit tests, by construction.** Any
  class extending `Node`/`Node3D`/`CharacterBody3D` (`BallController.cs`,
  `PlayerController.cs`, `NetworkManager.cs`, ...) cannot be instantiated in
  the plain-`Microsoft.NET.Sdk` xUnit project — Godot engine types need a live
  engine. A unit test can pin the pure math/state-machine layer under such a
  class, but the glue that wires it to the live engine (input, physics tick,
  RPC, scene tree) can *only* be proven by the headless integration harness.
  If your change touches a `Node`-derived file's *behavior* (not just calls
  into an already-pinned pure helper), a unit-test-only PR is not proof.

## 2. Decision guide — which proof surface

| Change touches... | Proof surface | Where |
|---|---|---|
| Pure logic/math/state machine, no `Node` inheritance, no engine singleton | xUnit unit test | `tests/Hooper.Ball.Tests/` |
| A `Node`-derived class's live behavior: scene tree, physics tick, `Input` singleton, RPC dispatch, single-process | Integration harness scenario | `tests/integration/*Test.cs` + `.tscn` |
| Two-process behavior: handshake, replication, broadcast-driven remote display | Dual-instance script | `tests/integration/run-net-*.sh` |
| `.tscn`/`.tres`/`project.godot` text-edit only (ADR-0011) | Scene-load-only check (headless launch succeeds, no crash) — not a full scenario | ad hoc; a bare headless `--path . <scene>.tscn` launch, or an existing harness that already loads the touched scene |

**Adding a pure-logic source file to the unit-test surface**: it must be added
to `tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj`'s explicit
`<Compile Include="..\..\scripts\...\Foo.cs" />` allowlist — this project does
**not** glob. As of 2026-07-12 there are **47** such `<Compile Include>` lines
(re-verify: `grep -c "Compile Include" tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj`),
spanning `scripts/Ball/`, `scripts/Input/`, `scripts/Systems/`,
`scripts/Networking/`, and `scripts/Player/`. Anything extending `Node`
(e.g. `BallController.cs`, `PlayerController.cs`, `NetworkManager.cs`,
`PlayerInputGlue.cs`, `DedicatedServerBootstrap.cs`) is **deliberately
banned** from that list — do not add it, and do not "fix" the exclusion.
The test project references `GodotSharp` as a bare NuGet package instead of
being a `Godot.NET.Sdk` project on purpose: the SDK approach was tried and
rejected because Godot's SDK resolved xUnit attributes against the wrong
project, producing `CS0246` on `[Fact]` (documented in the csproj comment).

## 3. The golden inventory

Verified live 2026-07-12. Re-run the commands in "Provenance and maintenance"
before trusting these numbers — CLAUDE.md and ADR-0016 have both gone stale on
test counts before ("~250", "459" — actual is 669).

### Unit tests

`dotnet test "tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj" --configuration Debug`
→ **669 total: 664 passed, 5 skipped, 0 failed.**

The 5 skips are all `[Theory(Skip = "characterization: run manually; not a CI
gate")]` in `ShotScatterCurveCharacterizationTests.cs` (distance curve,
movement/contest/facing penalty sweeps, combined-penalty scenarios). This is
**not** "broken tests that should be enabled" — it's an intentional
"instrument panel": a deterministic 100×100 stratified-grid sweep (10,000
samples, no RNG) that measures the live make-percentage curve and records the
numbers in doc comments for tuning reference. Only its 6th group stays
unskipped and runs in CI: `DefaultsMatchShotMakeCurveBands`, a live
cross-check asserting the constants duplicated at the top of the
characterization file (`Spm`, `MaxScatter`) still mirror `BallController`'s
real `[Export]` defaults — it exists to catch silent drift between the
doc-file copy and the production defaults, not to validate the curve itself.
A green run reads "664 passed / 5 skipped"; do not mistake the skips for a
regression, and do not un-skip them in CI.

### Integration harness — single-instance scenarios (`tests/integration/`)

Every scene: `godot --headless --path . res://tests/integration/<Scene>.tscn -- --harness-scenario=<name>`.
Exit 0 = PASS, 1 = FAIL, anything else = harness crash (CI fails on it too).
This table is transcribed from `.github/workflows/ci.yml`'s actual run steps
(read in full, 2026-07-12), not from memory.

| Scene | Scenarios | Proves |
|---|---|---|
| `SmokeTest.tscn` | (none — fixed run) | Phase-1 go/no-go: 30 fixed physics ticks, `delta` exactly `1/PhysicsTicksPerSecond` every tick, no drift |
| `InputMapDefensiveActionsTest.tscn` | (none) | `InputMap.HasAction()` true for `def_steal`, `def_block`, `def_contest` in the live engine (a bare load only proves the INI parsed) |
| `StealTurnoverTest.tscn` | `success`, `whiff`, `recovery-reset` | Per-Active-tick (not entry-tick-only) steal resolution (#96); last-toucher attribution; dribble-phase reset to 0 on scramble recovery (#176, closes the instant-re-steal exploit); plus a shadow-client reconciliation proof of `ShouldForceRecovery`/`ForceState` (#175) |
| `BlockTurnoverTest.tscn` | `success`, `success-lastactive`, `whiff`, `control-make` | Blocked shot's turnover, swat trajectory (away from rim, downward), defender last-toucher + Recovery; `success-lastactive` pins the release-tick top-up call (Active window's LAST tick on release); `whiff` pins the half-open interval boundary exactly; `control-make` is the counterfactual proving the identical unblocked shot scores exactly once (#98, ADR-0018 §2) |
| `OobTurnoverTest.tscn` | `held-turnover`, `defender-exempt`, `both-oob`, `wall-placement` | Holder-crosses-line turnover; non-holder crossing is a no-op; no 60Hz strobe when both players are OOB; the four `Walls/WallCollision*` shapes in the live `scenes/Main.tscn` sit outside `CourtMin`/`CourtMax` (#179, adjudicating hitl #119) |
| `TripleThreatTest.tscn` | `dead-dribble`, `production-drive` | Held-start tipoff (not auto-chained into Dribbling); dead-dribble flag on jump-shot cradle; dribble-move refusal while Held; OOB reset restores a live possession; `production-drive` presses REAL `Input.ActionPress` and proves Held→Dribbling via the unmodified `CheckAutoStartDribble` → `TryStartDribble` production path (#193, #204) |
| `CrossoverSweepTest.tscn` | `crossover-sweep`, `possession-change-no-sweep`, `remote-handside-sweep` | Genuine mid-body ball transit (lateral offset dips well below `HandOffset` magnitude — not a one-tick snap); a possession-change hand reset NEVER activates a sweep; the broadcast-receive (remote-client display) path also drives the sweep (#195, PR #208) |
| `PivotPlantTest.tscn` | `exports`, `flick-180`, `held-135`, `no-plant-boundary`, `committed-cancel` | The #172-retuned exports are live; a released 180° flick pivots to completion with zero displacement in a ~0.35s band; a sustained >90° hold stays planted then resumes; an exactly-90° turn never plants; a committed move (`def_steal`) cancels an in-progress pivot (#172, ADR-0010 amendment) |
| `MovingCrossoverTest.tscn` | `retains-speed`, `stationary-forward-exit`, `hesitation-still-hard-zeroes`, `remote-pending-stick` | Startup's `GatherDecel` bleeds (never zeroes) momentum; forward burst component from a dead stop; Hesitation still hard-zeroes (the ADR-0003 amendment's own scoping guard); the server's REMOTE-player copy reaches `CrossoverBurstMath` via `_pendingRawStick`, not just the own-player `ReadInput()` path (#198) |
| `BehindTheBackTest.tscn` | `shielded-sweep`, `narrower-exit-cone`, `dead-dribble-gate` | Ball's forward offset goes genuinely NEGATIVE (behind the holder's centerline — the discriminator vs. Crossover's in-front sweep); pure-lateral stick input yields a real forward burst through the narrower exit cone (`move_size_up` modifier wired end-to-end); same Held dead-dribble gate as Crossover/Hesitation (#194) |

That's 30 single-instance `godot` invocations in CI: 2 fixed steps
(SmokeTest, InputMapDefensiveActionsTest) + 28 scenario-flagged invocations
(3+4+4+2+3+5+4+3) across the 8 scenario scenes.

(Two further `.tscn` files exist under `tests/integration/` —
`HarnessPlayer.tscn` and `HarnessNetPlayer.tscn` — support scenes instanced
by harnesses, never CI-invoked directly.)

### Integration harness — dual-instance scripts (two headless processes)

Each launches the SAME `.tscn` twice via `--harness-role=server|client
--harness-port=N`, backgrounds the server, sleeps `SERVER_BIND_WAIT` (default
6s, env-overridable) for Godot's .NET cold boot to bind the port, then runs
the client in the foreground — **the client's exit code is the verdict**; the
server is best-effort, killed via `trap cleanup EXIT`. Scripts are bash (Git
Bash on Windows); each takes the godot binary as `$1` (default `godot`).

| Script | Port | Scene | Proves |
|---|---|---|---|
| `run-net-handshake.sh` | 23456 | `NetHandshakeTest.tscn` | A real ENet handshake between two headless processes — the two-process capability the single-instance harnesses cannot cover |
| `run-net-state-sync.sh` | 23457 | `NetStateSyncTest.tscn` | Server RPC-broadcasts a strictly-increasing authoritative value every physics tick (the real `ReceiveState` cadence, ADR-0002); client passes only on a sustained in-order run |
| `run-net-node-replication.sh` | 23458 | `NetNodeReplicationTest.tscn` | The SHIPPED `NetworkManager.StartDedicatedServer`/`JoinGame` path (ADR-0007); `MultiplayerSpawner` replicates a server-authored, peer-id-named node to the client |
| `run-net-behindtheback-sweep.sh` | 23459 | `NetBehindTheBackSweepTest.tscn` | Listen-server (`HostGame`) topology; the client's `DisplayMoveId()` remote-broadcast branch drives the shielded sweep — verified via co-occurrence on one live-sweep tick (#212) |

Topology note: `NetworkManager.StartDedicatedServer` (headless
authoritative-host) has **no own player**, so any proof that needs the client
to see the opponent's committed-move *display* (not just a replicated node's
existence) must use `NetworkManager.HostGame` (listen-server), where the
server process owns player node "1" and the client gets a true remote copy.
That is why the behind-the-back sweep script uses `HostGame` while the
node-replication script deliberately proves the dedicated-server path.
Also: in a single offline instance `Multiplayer.IsServer()` is unconditionally
true (`OfflineMultiplayerPeer`), so a single-instance harness structurally
CANNOT reach remote-broadcast display branches — that class of proof always
needs dual-instance.

---

## 4. ADD-A-SCENARIO runbook

Copy-pasteable steps for adding a new integration-harness scenario. Follow in
order. (How the harness compiles: `HOOPER GAME.csproj` does
`<Compile Remove="tests/**/*.cs" />` then
`<Compile Include="tests/integration/**/*.cs" />` — harness code is compiled
INTO the game assembly, so a harness compile error breaks the game build, and
editing harness code means rebuilding the game project.)

### Step 0 — decide if you need a seam

If the harness needs to drive private production machinery that a headless
single instance structurally cannot reach through the normal input/RPC layer
— `RequestBeginMove` is `[Rpc(AnyPeer, CallLocal=false)]` and sender-gated,
with no remote peer in one process to deliver it; `SampleMoveInput` reads
hardware input headless does not provide — you need a **harness seam**. If
the behavior is already reachable via the real `Input` singleton (see
`TripleThreatTest`'s `production-drive`, which presses real
`Input.ActionPress`) or an existing internal accessor, skip to Step 2.

### Step 1 — write the seam file (if needed)

Create `tests/integration/<Name>HarnessSeam.cs` — a `partial class` of the
**production type** (e.g. `PlayerController`, declared in the production
namespace `Hooper.Player`). Because of the csproj re-include trick above,
this file IS the same class as the production file — not a mock, not a
subclass. Expose `internal` (never `public`) test-only accessors so nothing
leaks outside the game assembly. Existing examples: `StealHarnessSeam.cs`,
`BlockHarnessSeam.cs`, `CrossoverSweepHarnessSeam.cs`,
`MovingCrossoverHarnessSeam.cs`, `BehindTheBackHarnessSeam.cs`,
`TripleThreatHarnessSeam.cs`.

**Hard rule, learned the expensive way**: the seam MUST call the production
choke point — `BeginCommittedMove(...)` — never `_machine.Begin(...)`
directly. A prior version of `BlockHarnessSeam.cs` called `_machine.Begin()`
straight, reasoning that `BlockMove` has no `BeginCommittedMove`-gated side
effect. That reasoning MISSED that `BeginCommittedMove` unconditionally
clears the in-place-pivot latch (`_pivot = HeadingMath.PivotState.None`) on
every successful `Begin()` regardless of move type (#172) — so the harness
was silently NOT exercising the real begin path, contrary to its own doc
comment. Reach production behavior through the real choke point, by
construction, instead of re-deriving its effects by argument. Template
(verbatim shape of `tests/integration/BlockHarnessSeam.cs`):

```csharp
using Hooper.Moves;

namespace Hooper.Player;

public partial class PlayerController
{
    /// <summary>
    /// Test-only: begins a BlockMove via the same BeginCommittedMove path
    /// production input reaches, bypassing only the input/RPC layer.
    /// </summary>
    internal bool BeginBlockForHarness() => BeginCommittedMove(new BlockMove());
}
```

### Step 2 — write `tests/integration/<Name>Test.cs`

A `partial class <Name>Test : Node` in namespace
`HOOPERGAME.Tests.Integration`. Skeleton (shape of `StealTurnoverTest.cs`):

```csharp
using System.Linq;
using Godot;
// ... production namespaces you need (Hooper.Ball, Hooper.Player, ...)

namespace HOOPERGAME.Tests.Integration;

public partial class <Name>Test : Node
{
    private const double TimeoutSeconds = 15.0;
    private string _scenario = "<default-scenario>";
    private int _frame;
    private bool _finished;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "<default-scenario>");
        GD.Print($"[<name>] scenario={_scenario} booting headless…");

        // Build the tree here — see the tree-construction tradeoff below.
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _frame++;
        // Drive scripted input/state changes at specific frames.
        // LATCH observations at the event tick (see §5).
        // On verdict frame or timeout: Finish(pass).
    }

    private void Finish(bool pass)
    {
        _finished = true;
        GD.Print(pass ? "[<name>] PASS" : "[<name>] FAIL");
        GetTree().Quit(pass ? 0 : 1);
    }
}
```

Use the shared `HarnessArgs.ReadArg` (`tests/integration/HarnessArgs.cs`) —
it tolerates both `--flag value` and `--flag=value`; it was extracted after
the third byte-for-byte duplicate was flagged in review. Do not paste a
fourth copy.

**Tree construction — code-built vs `.tscn`:** prefer building the node tree
in code inside `_Ready()` (the pattern of `StealTurnoverTest`,
`BlockTurnoverTest`, `TripleThreatTest`) — it dodges the fragile
`ext_resource`/`uid` scene-authoring failure modes for a throwaway harness.
Reserve real scene-graph authoring for when the scene file itself is what's
under test. Two non-negotiables when building in code:

1. **Match production sibling order: `Players` BEFORE `Ball`**, mirroring
   `scenes/Main.tscn`'s declaration order — sibling tick order is
   load-bearing for same-frame read/write races (an earlier harness had Ball
   before Players and its "mirrors production timing" claim was false).
2. **Force-set state; distrust defaults.** Code-built nodes get raw C#
   defaults, not `.tscn` property overrides (see §5).

Even with a code-built tree you still need a minimal companion `.tscn`,
because a `--script`-only / SceneTree-only run does not work for this harness
style at all — spike #87 found `_init()` returns before any frame processes,
so `_PhysicsProcess` never fires; every harness must be a full scene load.
The minimal scene is one script pointer (verbatim `StealTurnoverTest.tscn`):

```
[gd_scene load_steps=2 format=3]

[ext_resource type="Script" path="res://tests/integration/<Name>Test.cs" id="1_<slug>"]

[node name="<Name>Test" type="Node"]
script = ExtResource("1_<slug>")
```

**Every "X didn't happen" assertion needs a control scenario.** If your
scenario proves "the score stays 0-0 because the shot was blocked," add a
sibling scenario (see `BlockTurnoverTest`'s `control-make`) running the
identical setup WITHOUT the intervention and asserting the event DOES happen
— otherwise "stayed 0-0" is equally consistent with "the shot setup never
worked in the first place."

### Step 3 — wire it into CI

Add a step to `.github/workflows/ci.yml`'s `integration-test` job, after the
existing single-instance steps:

```yaml
      # <Dense doc-comment block: what production code path this exercises,
      # what each scenario's setup/placement is chosen to DISCRIMINATE (the
      # RED-on-old-code / GREEN-on-fixed-code story per scenario), and which
      # existing unit test — if any — already pins the pure math this
      # harness proves the LIVE GLUE for. Exit-code contract note (ADR-0016).>
      - name: Run <name> harness
        run: |
          godot --headless --path . res://tests/integration/<Name>Test.tscn -- --harness-scenario=<scenario-1>
          godot --headless --path . res://tests/integration/<Name>Test.tscn -- --harness-scenario=<scenario-2>
```

Every existing step follows this justify-each-scenario comment convention —
match that density. The comment is the only way a reviewer can tell a real
discriminating proof from a vacuous one without re-deriving the frame math.

For a **dual-instance** scenario, additionally write
`tests/integration/run-net-<name>.sh` by copying `run-net-state-sync.sh` (the
template: server backgrounded, `SERVER_BIND_WAIT` sleep, client foreground,
client exit code = verdict, `trap cleanup EXIT`) and bump the port past
23459 (ports 23456–23459 are taken). Reference the script from ci.yml with a
preceding `chmod +x`, matching the existing four steps.

### Step 4 — run it locally before pushing

See §6 for the exact commands. Two gates before you push:

1. `dotnet build "HOOPER GAME.csproj" --configuration Debug` — the harness
   compiles into the game assembly; this is where a harness compile error
   surfaces.
2. **Prove the scenario RED against the pre-fix code and GREEN after.** A
   scenario that was already green on the buggy code proves nothing; state
   the RED evidence (stash the fix, run, watch it fail) in the PR body.

---

## 5. The assertion disciplines

Each of these is a real trap this repo has already hit. Violating any one
turns a green scenario into a vacuous one.

- **Every "X didn't happen" needs a control scenario.**
  `BlockTurnoverTest`'s `control-make` is the counterfactual for `success`:
  it never begins a `BlockMove` and asserts the identical unblocked shot
  scores exactly once — so `success`'s "score still 0-0" means "genuinely
  blocked," not "the shot setup was broken all along."
- **Latch observations at event time, never at verdict time.**
  `StealTurnoverTest._everLoose` / `_toucherAtSteal` are captured on the
  FIRST tick the condition holds and never re-read — the scramble's later
  `AwardPossession` legitimately overwrites `LastToucherPeerId` once someone
  recovers the ball, which would mask the exact bug the assertion targets.
- **Code-built trees get raw C# defaults, not `.tscn` overrides — force-set
  state, distrust geometry defaults.** A code-built `BallController` carries
  none of the property overrides a `.tscn`-instanced one would; that is why
  `StealTurnoverTest` explicitly calls `_ball.StateMachine.StartDribble()`
  after construction, and why issue #217 (`BoardCenter`/`RimCenter`
  inconsistency between the two default sources) happened at all. If a
  scenario depends on geometry (rim, backboard, court bounds), set it
  explicitly in the harness rather than trusting whatever the C# default is.
- **`Input.ActionPress` just-pressed edge lands NEXT frame; a parent
  observes a child's write one frame later still.** Root ticks first, then
  the players' committed-move machines advance, THEN `BallController` reads
  the now-current-frame phase (Players before Ball, per `Main.tscn` order) —
  `StealTurnoverTest`'s `ComputeBeginFrame` carries a "+1" adjustment for
  exactly this, and its `VerdictMarginFrames` exists because Root observes
  the ball state the tick AFTER `BallController` set it.
- **Frame-band tolerance, not exact-frame, for input-driven timing.**
  `PivotPlantTest`'s `flick-180`/`held-135` assert timing *bands* (e.g.
  ~0.35s completion), not exact ticks — real-`Input`-singleton edges plus
  the tick-order skews above make exact-frame assertions flaky by
  construction.
- **Dual-instance pass evidence must be co-occurrence on one tick, never
  independent latches.** `NetBehindTheBackSweepTest` passes only if, on the
  SAME live-sweep tick, the client's ball flags behind-body AND the remote
  holder's `DisplayMoveId()` returns `"behindtheback"` AND the forward
  offset is genuinely negative. Independent latches accumulated across the
  run would pass on two unrelated events that each happened *sometime*.
- **Scene-load-only checks for `.tscn` edits (ADR-0011).** A pure
  scene/resource text-edit (add a node, set an export, wire a `NodePath`)
  needs a headless load check — the scene loads without errors — in its own
  single-concern commit; it does not need a bespoke scenario unless the edit
  changes behavior. Conversely, a load check alone never proves behavior.

---

## 6. Running it locally (one-liners; full detail in `hooper-run-and-operate`)

Quote everything — the repo path AND the csproj name contain spaces
(`C:\Users\...\hooper-game`, `HOOPER GAME.csproj`). Run from the repo root.

```powershell
# Unit tests (expect 664 passed / 5 skipped / 669 total)
dotnet test "tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj" --configuration Debug

# Game-project build (the guard dotnet test alone cannot provide)
dotnet build "HOOPER GAME.csproj" --configuration Debug

# One single-instance scenario. Needs a local Godot 4.6.3 .NET/Mono binary —
# it is NOT on PATH by default; point $GODOT (or an env var) at the
# *_console.exe variant for terminal output. The version MUST match the
# Godot.NET.Sdk/GodotSharp 4.6.3 pin. CI provisions its own via
# chickensoft-games/setup-godot@v2 (version: 4.6.3, use-dotnet: true).
& $GODOT --headless --path . res://tests/integration/SmokeTest.tscn
& $GODOT --headless --path . res://tests/integration/StealTurnoverTest.tscn -- --harness-scenario=success
```

```bash
# Dual-instance (bash / Git Bash, not PowerShell; client exit code = verdict)
SERVER_BIND_WAIT=6 bash tests/integration/run-net-state-sync.sh "$GODOT"
```

## 7. What reviewers check

The `/code-review` gate (ADR-0015: no merge with unresolved correctness
findings) and pr-test-analyzer expectations for this repo reduce to one rule:
**a behavior change without a discriminating RED/GREEN test is not proven.**
Concretely, reject (or expect rejection of) a PR that:

- Changes `Node`-derived behavior with only a unit test on the pure helper
  underneath — the live glue is unproven (§1).
- Adds a harness scenario never verified RED against the pre-fix code — a
  scenario green on both old and new code discriminates nothing.
- Claims "X no longer happens" with no control scenario proving the setup
  could detect X at all (§5).
- Passes `dotnet test` but was never built against `HOOPER GAME.csproj` —
  the compile-surface asymmetry (§1).
- Closes a `hitl` issue whose criteria mix state-checkable and
  irreducibly-feel parts without splitting them — the feel half still goes
  to the batched per-milestone human pass (ADR-0013/0015).

---

## Provenance and maintenance

Authored from a verification pass dated 2026-07-12 (written to disk
2026-07-14), verified against the repo's live `main`; reviewed and corrected
2026-07-15 (CI invocation count 22 → 30 — the scenario sum was 28 all along):

- Unit-test run: `dotnet test "tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj" --configuration Debug` → 664 passed, 5 skipped, 669 total.
- `.github/workflows/ci.yml` read in full (357 lines); the §3 scenario matrix
  is transcribed from its actual `run:` steps and doc comments.
- `ls tests/integration/*.tscn` → 16 scenes (the 10 CI-invoked single-instance
  scenes, 4 `Net*Test.tscn` dual-instance scenes, plus 2 support scenes
  `HarnessPlayer.tscn`/`HarnessNetPlayer.tscn` never invoked directly).
- Read directly to ground the runbook: `tests/integration/StealTurnoverTest.cs`,
  `HarnessArgs.cs`, `BlockHarnessSeam.cs`, `StealTurnoverTest.tscn`,
  `run-net-state-sync.sh`, `HOOPER GAME.csproj`,
  `tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj` (47 `Compile Include`
  lines), and `ls tests/integration/*Seam.cs` (6 seam files).

**Re-verification commands** (run these before trusting any count or list
above — they drift):

```bash
# Unit-test totals
dotnet test "tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj" --configuration Debug
# Unit-test compile allowlist size
grep -c "Compile Include" tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj
# Live CI scenario list
grep -n "harness-scenario=\|run-net-.*\.sh" .github/workflows/ci.yml
# Scene + seam inventory
ls tests/integration/*.tscn tests/integration/*Seam.cs
```

This skill does not restate ADR text or milestone status — those live in
`docs/adr/` and CLAUDE.md; read them directly rather than trusting a copy
here.
