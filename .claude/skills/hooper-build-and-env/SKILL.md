---
name: hooper-build-and-env
description: Recreate the hooper-game build environment from scratch and know its traps — use when setting up a fresh clone/machine, when `dotnet build`/`dotnet test` fails or behaves unexpectedly, when you need the exact quoted build/test/harness commands, when reconciling a "dotnet test is green but CI is red" (or vice versa) confusion, when you need the Godot version pin or how to obtain a local Godot binary, when touching `HOOPER GAME.csproj`/`tests/Hooper.Ball.Tests.csproj`/`.github/workflows/ci.yml`/`.claude/hooks/verify-green.sh`, or when you hit gitignore/worktree/path-with-spaces surprises.
---

# hooper-build-and-env

Ground truth for recreating and understanding the hooper-game build environment:
what to install, the exact commands that must stay green, the two asymmetric
compile surfaces, CI structure, the local green-gate hook, and the environment
traps that bite newcomers (paths with spaces, stale worktrees, gitignored
export artifacts, uid sidecars).

This skill is about **building and environment setup**. It does not cover
*running* the game/server (see `hooper-run-and-operate`) or *what counts as
proof / adding new tests* (see `hooper-verification-and-qa`) — read the "When
NOT to use this" section below before assuming this is the right skill.

## Prerequisites

| Tool | Required version | Why it must match |
|---|---|---|
| .NET SDK | 8.0.x (verified locally: 8.0.421) | Both csprojs target `net8.0`. Verify with `dotnet --list-sdks`. |
| Godot editor/runtime | **4.6.3 STABLE, MONO build** | `HOOPER GAME.csproj` pins `Sdk="Godot.NET.Sdk/4.6.3"` and the test csproj pins `PackageReference Include="GodotSharp" Version="4.6.3"`. A mismatched Godot binary will not load the project correctly or will disagree with the C# bindings. Get the **mono** variant, not the standard (GDScript-only) build — Godot ships separate binaries and only the mono one carries .NET support. |

Godot is **not** an SDK/NuGet dependency you `dotnet restore` — it's a
separate engine binary you download yourself. It is normal for it to be
**absent** on a fresh clone/CI runner until you provision it (see below).

**Do not hardcode a specific machine's download path as load-bearing.** On the
machine this skill was verified on, a working 4.6.3 mono binary happened to
live under the user's `Downloads` folder — that is a fragile, deletion-prone
location and MUST NOT be treated as a stable contract. Instead:

- Recommend an env-var convention: set `$GODOT` (or `$env:GODOT` on
  PowerShell) to the full path of your local `..._console.exe`, and have any
  script/command reference `$GODOT` instead of a literal path. This repo's own
  dual-instance harness scripts already take the binary as an argument
  (`bash tests/integration/run-net-state-sync.sh "$GODOT"`), which is the same
  idea — don't invent a second convention.
- On Windows, download the **`_console.exe`** variant (e.g.
  `Godot_v4.6.3-stable_mono_win64_console.exe`), not the plain
  `Godot_v4.6.3-stable_mono_win64.exe`. The console variant prints to the
  terminal (stdout/stderr visible); the windowed variant swallows it — you
  will not see `[harness] PASS/FAIL` output from the windowed binary when run
  from a shell.
- The Godot binary is typically **NOT on PATH** on a dev machine — every local
  invocation in this skill spells out the binary explicitly (or via `$GODOT`).
  CI does not have this problem: it provisions Godot fresh every run via
  `chickensoft-games/setup-godot@v2` with `version: 4.6.3, use-dotnet: true`,
  which puts a `godot` binary on PATH for the job.
- Confirm your local binary matches the pin before trusting it:
  `& $GODOT --version` must print `4.6.3.stable.mono.official.<hash>`. Any
  other version string is a real risk — Godot's C# API has churned across
  minor versions.

## The exact commands (verified 2026-07-12)

**Quote every path.** Both the repo path (`C:\Users\The King\Documents\GitHub\hooper-game`,
because of `The King`) and the game csproj filename (`HOOPER GAME.csproj`,
because of the space before `GAME`) contain spaces. An unquoted command copied
from a different project will silently fail or target the wrong file.

Run these from the repo root:

```
dotnet build "HOOPER GAME.csproj" --configuration Debug
```
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`, ~1 second incremental.
Output DLL lands at `.godot/mono/temp/bin/Debug/HOOPER GAME.dll`.

```
dotnet test "tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj" --configuration Debug
```
Expected: **`Passed! - Failed: 0, Passed: 664, Skipped: 5, Total: 669`**, ~1-4s.
(The golden test inventory that owns this baseline count lives in
`hooper-verification-and-qa` — if the numbers drift, reconcile there first.)
The 5 skips are all `Hooper.Ball.Tests.ShotScatterCurveCharacterizationTests`
theories (deliberately `Skip`'d characterization captures, tied to the #154
shot-scatter feel sign-off) — **the skips are NOT a regression.** If the pass
count changes but the skip count stays 5, something real changed; if the skip
count drops or new skips appear, that is also a real change worth
investigating, not routine noise.

Both commands were re-run live during this skill's authoring and reproduced
exactly the numbers above — re-run them yourself if you suspect drift; see
"Provenance and maintenance."

### First-time headless Godot run

The very first time you run Godot headless against this project (fresh clone,
no `.godot/` yet), it needs to generate the C# bindings and import the
project:

```
godot --headless --build-solutions --quit
```

This step **may exit non-zero on its own** even when it actually succeeded —
CI deliberately does `godot --headless --build-solutions --quit || exit 0` for
exactly this reason (documented in a `ci.yml` comment). Don't treat a nonzero
exit code from *this specific bootstrap command* as a failure signal; the
real gate is the scene run that follows it, e.g.:

```
godot --headless --path . res://tests/integration/SmokeTest.tscn
```
Expected: `[harness] PASS — 30 fixed ticks, deterministic`, exit code 0.
(The full harness scenario matrix, and what "PASS" is allowed to mean, belongs
to `hooper-verification-and-qa` — this skill only proves the plumbing works.)

## The two compile surfaces (and their asymmetry — the #1 trap)

There are **two separate C# compile surfaces** in this repo, and they disagree
in ways that produce a specific, recurring confusion: *"`dotnet test` is
green, why is CI red?"*

**1. The game project — `HOOPER GAME.csproj`** (repo root):
- `Sdk="Godot.NET.Sdk/4.6.3"`, `TargetFramework net8.0` (flips to `net9.0`
  only when `GodotTargetPlatform==android` — an Android export target uses a
  different framework than everything verified here).
- `ImplicitUsings` is **NOT** enabled.
- Contains this ItemGroup, which is load-bearing and easy to break:
  ```xml
  <Compile Remove="tests/**/*.cs" />
  <Compile Include="tests/integration/**/*.cs" />
  ```
  The Godot.NET.Sdk globs `**/*.cs` from the repo root by default, which
  would otherwise pull the xUnit test sources under `tests/` into the game
  assembly. So the first line excludes all of `tests/`. **But**
  `tests/integration/` is the headless integration harness (ADR-0016) — it's
  engine code, referenced by `.tscn` scenes, and genuinely must compile into
  the game assembly to run. The second line adds just that subtree back.
  - **Consequence:** editing anything in `tests/integration/` means
    rebuilding the **game** project, and a compile error there breaks the
    **game build**, not just some test target. Don't assume test-directory
    changes are test-project-scoped in this repo.

**2. The test project — `tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj`**:
- Plain `Microsoft.NET.Sdk`, `net8.0`, `ImplicitUsings` **enabled**,
  `Nullable` enabled. xUnit 2.5.3 + Microsoft.NET.Test.Sdk 17.8.0 +
  coverlet.collector 6.0.0.
- References `GodotSharp` version `4.6.3` as an ordinary **NuGet package**,
  **NOT** a `ProjectReference` to the game csproj. The csproj's own comment
  explains why: a `ProjectReference` to a `Godot.NET.Sdk` project causes
  xUnit attributes (`[Fact]`, etc.) to be resolved against the *game*
  project's SDK context instead of the test project's, producing `CS0246`
  errors on `[Fact]` itself. This was tried and rejected. The NuGet package
  gives you the `Godot.*` managed types (`Vector3`, etc.) without pulling in
  the Sdk's build machinery.
- Compiles ~35 hand-picked **pure C#** source files directly from `scripts/`
  via individual `<Compile Include="..\..\scripts\...\X.cs">` lines — Ball,
  Input, `Systems/Scoreboard.cs`, `Networking/DedicatedServerArgs.cs` +
  `ServerBeacon.cs` + `ServerList.cs`, and Player math/resolver files.
  **Deliberately excluded:** any file that extends a Godot `Node` type or
  touches an engine singleton (`BallController.cs`, `PlayerController.cs`,
  `NetworkManager.cs`, `PlayerInputGlue.cs`, `DedicatedServerBootstrap.cs`) —
  those can't be instantiated headlessly inside a plain xUnit run and need
  the integration harness instead.

**Why the asymmetry bites:** because `ImplicitUsings` is on in the test
project but off in the game project, code using (say) `System.MathF` with
only `using Godot;` at the top can resolve fine in the test build (implicit
global `using System;`) while failing to compile in the game build. **`dotnet
test` passing does NOT prove the game project compiles.** This is exactly why
CI builds the game project explicitly and separately (the comment at the top
of `ci.yml` says so), and why you must too: always run the
`dotnet build "HOOPER GAME.csproj"` command above before trusting a change,
never `dotnet test` alone.

## CI structure (`.github/workflows/ci.yml`)

One workflow, triggers on push to `main` + all PRs, two jobs:

1. **`build-and-test`** (ubuntu-latest): checkout → `actions/setup-dotnet@v4`
   (`dotnet-version: '8.0.x'`) →
   `dotnet build "HOOPER GAME.csproj" --configuration Debug` →
   `dotnet test tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj --configuration Debug`.
   This is the fast, engine-free gate.

2. **`integration-test`** (ubuntu-latest, `timeout-minutes: 15`): checkout →
   setup-dotnet → **`chickensoft-games/setup-godot@v2`** (`version: 4.6.3,
   use-dotnet: true, include-templates: false` — this is how CI obtains the
   Godot binary and puts `godot` on PATH) → the bootstrap
   `godot --headless --build-solutions --quit || exit 0` (the deliberately
   swallowed spurious-failure step described above) → an explicit
   `dotnet build "HOOPER GAME.csproj"` (so a genuine C# error surfaces here
   rather than as a confusing "script not found" at scene-load time) →
   **30 single-instance harness scenario invocations** of the form
   `godot --headless --path . res://tests/integration/<Scene>.tscn -- --harness-scenario=<name>`
   across SmokeTest, InputMapDefensiveActionsTest, StealTurnoverTest (3
   scenarios), BlockTurnoverTest (4), OobTurnoverTest (4), TripleThreatTest
   (2), CrossoverSweepTest (3), PivotPlantTest (5), MovingCrossoverTest (4),
   BehindTheBackTest (3) — then **4 dual-instance shell-script harnesses**
   (`run-net-handshake.sh`, `run-net-state-sync.sh`,
   `run-net-node-replication.sh`, `run-net-behindtheback-sweep.sh`, each
   `chmod +x`'d first, localhost ports 23456-23459). Exit-code contract
   (ADR-0016): scene calls `GetTree().Quit(0)` = PASS, `Quit(1)` = FAIL, any
   other code = harness crash, which also fails the job.

   This job is the project's third verification surface — it boots a real
   Godot .NET engine and exercises the live simulation, which unit tests
   structurally cannot reach. The per-scenario meaning of each harness run
   and how to add a new one is `hooper-verification-and-qa` territory; this
   skill only owns *that the plumbing runs*.

## The local green-gate hook (`.claude/hooks/verify-green.sh`)

`.claude/settings.json` (tracked) wires this one 84-line script to **both**
the `Stop` and `SubagentStop` hook events with `timeout: 300`. Every time an
agent session on this repo tries to finish, the hook runs.

What it does, in order:
1. Resolves `dotnet`: PATH first, then falls back to
   `/c/Program Files/dotnet/dotnet.exe`. If neither exists, it **skips
   silently** (`exit 0`) with only a stderr note — a machine with no dotnet
   is never blocked. This makes it deliberately weak.
2. **Gate 1:** `dotnet build "HOOPER GAME.csproj" --configuration Debug -nologo -v quiet`.
3. **Gate 2:** `dotnet test tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj --configuration Debug -nologo -v quiet`.
4. On failure of either gate: increments a counter file at
   `.claude/.greengate-attempts`, tails 25 log lines
   (`$TMPDIR/hooper-greengate-{build,test}.log`) back on stderr, and
   **exits 2** — which blocks the agent from finishing (Claude Code treats a
   Stop-hook exit 2 as "not done yet; the stderr is fed back to you").
5. **Loop bound:** after **3** consecutive red attempts (`MAX_ATTEMPTS=3`),
   it stops blocking — it surfaces a loud "STILL RED" warning and `exit 0`s
   anyway, so a genuinely unfixable tree cannot trap an agent in an infinite
   retry loop. This means a worker *can* technically finish red after 3
   tries.
6. On green: clears the counter, `exit 0`.

**Consequence for you:** every agent `Stop` on this repo costs one build+test
cycle (a few seconds incremental). And critically: this hook runs only 2 of
the gates (no headless harness, no code review) and can be skipped entirely
by a missing dotnet — it is a **weaker local mirror** of ADR-0015's "no agent
reports done on red", **never the real gate**. CI (`gh pr checks`) is the
authoritative gate; never accept "the hook let me stop" as proof of green.

## Environment traps

| Trap | What happens | What to do |
|---|---|---|
| **Paths with spaces** | Repo path (`...\The King\...`) and `HOOPER GAME.csproj` both contain spaces. Unquoted commands copied from elsewhere silently fail or mistarget. | Always double-quote both the repo path and the csproj filename in every command. |
| **Stale agent worktrees** | `.claude/worktrees/` holds ~9 leftover full repo copies from past autonomous agent runs (each branch already merged into `main`, 0 commits ahead — verified 2026-07-12 with `git merge-base --is-ancestor`). Each carries its own `.godot/` build artifacts and a full copy of `.claude/`. | Scope `Glob`/`Grep` patterns away from `.claude/` or you'll get duplicated hits from these copies. They are all merged residue, but check `hooper-failure-archaeology` before deleting anything — don't assume "merged" means "safe to remove" without checking the settled record. |
| **`export_presets.cfg` and `build/` are gitignored** | The Windows dedicated-server export (`build/server/PROJECT.exe` etc.) and the export preset that produces it exist on the original machine but are **not reproducible from a fresh clone** — `git check-ignore export_presets.cfg build` confirms both are ignored. | Don't expect `build/server/` or `export_presets.cfg` after cloning; there is currently no documented procedure to regenerate the preset (open item, see below). |
| **`*.uid` sidecars gitignored, one exception** | `.gitignore` line 26 ignores `*.uid` broadly, but line 32 explicitly un-ignores `!assets/locomotion.res.uid`. That single tracked sidecar is the **only backing** for the uid by which `Player.tscn` references the hand-saved AnimationLibrary (issue #140) — no importer regenerates it. | Never delete or regenerate `assets/locomotion.res.uid` — doing so breaks the humanoid rig on every machine except the one it was authored on. |
| **`.godot/` is gitignored** | Running Godot headless (even just the bindings bootstrap) only touches ignored paths — `git status --porcelain` is clean before and after a headless run. | Don't interpret a clean git status as "the headless run did nothing"; that's the expected shape. |
| **Dual-instance harness scripts are bash** | `tests/integration/run-net-*.sh` are POSIX shell scripts. On Windows they must run via Git Bash, not PowerShell/cmd. | Invoke as `bash tests/integration/run-net-state-sync.sh "$GODOT"` from a bash-capable shell. They take the Godot binary as `$1` (default `godot`), hardcode ports 23456-23459, launch the server in the background, sleep `SERVER_BIND_WAIT` (default 6s, env-overridable, as is `HARNESS_PORT`), and use only the **client's** exit code as the verdict. |
| **Only `net8.0` — until Android** | `HOOPER GAME.csproj` conditionally flips to `net9.0` when `GodotTargetPlatform==android`. | If a `net9.0`-related build issue ever appears, check for that conditional before assuming an SDK problem. Everything verified here is `net8.0`. |

## Fresh-clone bootstrap checklist

Run in this order; each step's expected outcome is what was reproduced live
during this skill's authoring.

1. Clone the repo. Expect: no `.godot/`, no `export_presets.cfg`, no `build/`
   — all gitignored, all normal for a fresh clone.
2. Confirm .NET SDK 8.0.x: `dotnet --list-sdks`. If missing, install it
   (matches CI's `actions/setup-dotnet@v4` with `dotnet-version: '8.0.x'`).
3. From the repo root:
   `dotnet build "HOOPER GAME.csproj" --configuration Debug`.
   Expect: `Build succeeded, 0 Warning(s), 0 Error(s)`. This does NOT require
   Godot to be installed — the Godot.NET.Sdk NuGet package alone compiles the
   C#.
4. `dotnet test "tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj" --configuration Debug`.
   Expect: `Passed: 664, Failed: 0, Skipped: 5, Total: 669` (as of
   2026-07-12; the 5 skips are the ShotScatterCurveCharacterizationTests and
   are intentional).
5. Only if you need to run the headless harness or the game locally (not
   required for pure C# work): obtain a **Godot 4.6.3 stable MONO** binary
   (the `_console.exe` variant on Windows), point `$GODOT` at it, verify
   `& $GODOT --version` prints `4.6.3.stable.mono.official.<hash>`, run
   `& $GODOT --headless --build-solutions --quit` once to bootstrap the
   bindings (tolerate a nonzero exit from this one command), then
   `& $GODOT --headless --path . res://tests/integration/SmokeTest.tscn`.
   Expect: `[harness] PASS — 30 fixed ticks, deterministic`, exit 0.
6. If working as an agent on this repo, expect the `Stop`/`SubagentStop`
   green-gate hook to re-run steps 3-4 every time you try to finish, and to
   block you (exit 2) up to 3 consecutive times while either is red.

## When NOT to use this

- **Running the game, the dedicated server, or a specific harness scenario**
  (beyond proving the environment can build and boot) →
  `hooper-run-and-operate`.
- **What counts as evidence, the full harness scenario matrix and what each
  scenario asserts, or how to add a new test** → `hooper-verification-and-qa`.
- **Diagnosing a specific broken symptom** rather than setting up or
  understanding the environment → `hooper-debugging-playbook`; measurement
  tooling → `hooper-diagnostics-and-tooling`.
- **Whether a worktree/branch/file was already settled by a past
  investigation and is safe to delete** → `hooper-failure-archaeology`.
- **ADR discipline, afk/hitl labels, merge-gating policy** (as opposed to the
  mechanics of running the gates) → `hooper-change-control`.

## Provenance and maintenance

Verified 2026-07-12 by live re-run on the authoring machine (not merely read
from digests); reviewed and corrected 2026-07-15 (CI harness invocation count
22 → 30; verify-green.sh 85 → 84 lines):
- `dotnet build "HOOPER GAME.csproj" --configuration Debug` → Build
  succeeded, 0 warnings / 0 errors, ~1.1s incremental.
- `dotnet test "tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj" --configuration Debug`
  → Passed: 664, Failed: 0, Skipped: 5 (all ShotScatterCurveCharacterizationTests),
  Total: 669.
- `dotnet --list-sdks` → 8.0.421 (the only SDK installed).
- `HOOPER GAME.csproj`, `tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj`,
  `.github/workflows/ci.yml` (357 lines), `.claude/hooks/verify-green.sh`
  (84 lines), and `.claude/settings.json` read in full.
- `.gitignore` uid lines (26: `*.uid`; 32: `!assets/locomotion.res.uid`) read
  directly; `git check-ignore export_presets.cfg build` → both ignored
  (verified 2026-07-12 per discovery).
- A local Godot 4.6.3 mono binary reported
  `4.6.3.stable.mono.official.7d41c59c4` and ran SmokeTest.tscn to
  `[harness] PASS`, exit 0 (verified 2026-07-12 per discovery).

Re-verification one-liners (run from repo root whenever this skill feels
stale, especially after any Godot / GodotSharp / .NET bump):
```
dotnet --version
dotnet build "HOOPER GAME.csproj" --configuration Debug
dotnet test "tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj" --configuration Debug
& $GODOT --version                              # expect 4.6.3.stable.mono.official.<hash>
git check-ignore export_presets.cfg build       # expect both echoed (ignored)
git worktree list                               # check the stale-worktree count
```

Known open items (unresolved in the repo as of 2026-07-12 — flag, don't
silently assume an answer):
- No documented procedure exists to regenerate `export_presets.cfg` /
  reproduce the dedicated-server export on a fresh machine.
- The "blessed" way to obtain/pin a local Godot binary is only the env-var
  convention this skill recommends; if the project later adopts a fixed
  install location or provisioning script, update this file.
- CI pins Godot to exactly `4.6.3` via `setup-godot@v2` while `setup-dotnet`
  floats on `'8.0.x'`; there is no documented policy for bumping the
  Godot / GodotSharp / local-binary trio in lockstep.
