# ADR-0016 — Headless Godot harness is the official verification surface

- **Status:** Accepted
- **Date:** 2026-06-29
- **Superseded-by:** —

---

## Context

ADR-0011 narrowed the human's role to **feel + in-engine verification**, and
CLAUDE.md's "Done means proven" makes that in-engine verification the gate that
closes every `hitl` issue. ADR-0015 now wants to remove the human from the
per-change path entirely — but it can only do that if *something else* can run
the game and check that it behaves. That something is this ADR.

Today there are two automated surfaces and one manual one:

1. **Game compile** (`dotnet build "HOOPER GAME.csproj"`) — proves the C# is
   valid against the real Godot SDK compile surface.
2. **Pure unit tests** (`dotnet test`, ~250) — prove the *pure* logic
   (deterministic ball, scoring, possession, committed-move machine, resolvers).
   These deliberately exclude every `Node`-derived type: the test csproj
   references `GodotSharp` as a bare NuGet and hand-picks only engine-free `.cs`
   files, because a `Node` cannot be instantiated without a running engine.
3. **Human-in-editor** — the only surface that ever exercises an actual scene:
   `.tscn` wiring, `_Ready`/`_PhysicsProcess` lifecycles, the multiplayer
   layer, AnimationTree, signals. EDITOR_TASKS.md is the whole catalogue of
   "things only a running engine reveals."

The gap is stark: **everything that needs the engine runtime is verified only by
a human.** That is exactly the surface ADR-0015 needs automated. Unit tests can
never close it — by construction they run *without* the engine, so they cannot
catch a broken `NodePath` export, a spawner that doesn't replicate, a scene that
fails to load, or a server-authoritative reconcile that desyncs.

The unlock is that **Godot 4 .NET ships a headless mode** (`--headless`) and a
CI-provisionable binary (chickensoft-games/setup-godot supports the .NET build on
hosted runners — no self-hosted machine needed). A headless Godot can load a real
scene, run real `_PhysicsProcess` ticks, script input frames, and read back real
engine state — then `GetTree().Quit(exitCode)` to report pass/fail to CI. That is
a third verification surface that reaches what unit tests structurally cannot.

This epic is the project's **go/no-go bet** (per the autonomy mandate): if Godot
cannot verify headless on CI, ADR-0015's strong form collapses to its fallback
(afk-only auto-merge, human keeps `hitl`).

### Forces at play

1. **Unit tests and the harness verify disjoint things.** Unit tests own *pure
   logic in isolation*; the harness owns *the engine wiring and lifecycle that
   logic runs inside*. Neither subsumes the other — a green unit suite over a
   scene that fails to load is a false "done." We need both, kept separate.
2. **Headless ≠ feel.** The harness can assert *state* deterministically. It
   cannot judge *feel*. This is the same boundary ADR-0015 draws; this ADR is
   where the boundary is mechanically enforced (assert numbers, never "looks
   right").
3. **Determinism is already a project invariant.** ADR-0002 (server-authoritative
   + fixed-tick prediction) and ADR-0004 (deterministic mini-physics) mean the
   sim *already* produces identical state from identical input. That is precisely
   what makes scripted-input → step-N-ticks → assert-exact-state a reliable test
   rather than a flaky one. The harness is cashing in determinism that was built
   for netcode.
4. **Networking is the hardest thing to verify and the most valuable.** A
   dual-instance server-authoritative smoke test (host + client in one headless
   process or two) is where silent desync bugs hide (e.g. the M7b remote-phase
   gap, #69). A harness that can stand up two peers and assert convergence is the
   single highest-leverage check in the project.
5. **CI minutes and flakiness are real costs.** Booting an engine per test is
   slower than a pure unit test and has more ways to flake (timing, port
   contention, asset import). The harness must be a *separate CI job* so its cost
   and flake profile don't contaminate the fast unit gate.

### Alternatives considered

1. **Keep human-in-editor as the only engine-level verification.**
   Rejected. It is the bottleneck ADR-0015 exists to remove and the thing that
   makes per-change autonomy impossible.
2. **Push everything into pure unit tests; never boot the engine.**
   Rejected as insufficient. Force 1: by construction unit tests exclude
   `Node`-derived types, so scene loading, exports, spawners, signals, and the
   multiplayer layer are *unreachable* from them. Extracting ever-more pure
   resolvers is good practice (and already done — `MovementMath`,
   `FacingResolver`, `DisplayPhaseResolver`, …) but it can never verify that the
   resolver is *wired into a scene that loads and ticks*.
3. **Self-hosted runner with a full Godot editor + display.**
   Rejected. Heavier, slower to provision, a security/maintenance burden, and
   unnecessary — the .NET headless build runs on hosted runners via
   setup-godot. Reserve self-hosting only if a specific check provably needs a
   GPU/display (none identified yet).
4. **A GUT/GoDotTest-style in-engine test framework as the only harness.**
   Considered, partially adopted in spirit. We don't need to adopt a third-party
   in-engine framework wholesale; the deterministic sim lets a thin bespoke
   pattern — load scene, script input, step ticks, assert, `Quit(exitCode)` —
   cover the cases. We keep the door open to a framework if the bespoke pattern
   gets unwieldy, but start minimal.

## Decision

**A headless Godot integration harness becomes the third official verification
surface, alongside game-compile and pure unit tests, and is the automated stand-in
for human in-editor verification wherever an acceptance criterion is expressible
as a state assertion.**

Concretely:

- **Provision Godot 4 .NET headless in CI** via `chickensoft-games/setup-godot`
  (the .NET/Mono build, matching the `Godot.NET.Sdk` version the project pins —
  4.6.x). No self-hosted runner.
- **Integration tests live under `tests/integration/`** as real Godot scenes +
  scripts that:
  1. boot under `--headless`,
  2. load the scene(s) under test,
  3. script deterministic input frames and **step the fixed-tick sim** a known
     number of physics ticks (leaning on ADR-0002/0004 determinism),
  4. **assert exact engine state** read back from real nodes,
  5. call `GetTree().Quit(exitCode)` — `0` = pass, non-zero = fail — so CI reads
     pass/fail from the process exit code.
- **A dual-instance server-authoritative network smoke test is a first-class
  member of the harness** (force 4): stand up a host + a client, drive input on
  one, and assert the other converges — the automated form of the EDITOR_TASKS
  dual-instance verify that has gated nearly every milestone.
- **A dedicated `integration-test` CI job** runs the harness, separate from the
  fast `build-and-test` job (force 5), so engine-boot cost and flakiness are
  isolated and the job's green is what ADR-0015 gate #3 reads.
- **The harness asserts state, never feel** (force 2). Feel remains the
  per-milestone human pass (ADR-0015).
- **Scope of what it closes.** A `hitl` issue whose EDITOR_TASKS steps are
  state-checkable (scene loads clean, export resolves, score increments, ball
  goes loose, remote phase renders, peers converge) is now harness-closeable. A
  step that is irreducibly feel ("does the lean read as grounded") is not, and
  stays with the human milestone pass.

**Go/no-go (this epic is the bet):**
- **Go** — at least one integration scene boots headless on CI and reports
  pass/fail by exit code, *green*. The strong form of ADR-0015 (harness-closed
  `hitl`) is live.
- **No-go** — if Godot cannot be made to verify headless on hosted CI after a
  genuine attempt, record the failure, fall back to ADR-0015 alternative 2
  (afk-lane auto-merge only; human retains `hitl` verification), and revisit.

## Consequences

**Easier:**
- The engine-level surface that was human-only becomes automatable, which is the
  literal precondition for ADR-0015's per-change autonomy.
- Regression coverage deepens where it matters most: scene loads, export wiring,
  the multiplayer reconcile — the bug classes unit tests can't see.
- EDITOR_TASKS.md's dual-instance verifies (the milestone-gating ritual) get a
  CI equivalent, so a desync regression is caught on a PR instead of in a manual
  session weeks later.

**Harder / accepted tradeoffs:**
- **Engine-boot tests are slower and flakier than unit tests** (force 5).
  Contained by isolating them in their own CI job and leaning on determinism to
  keep assertions exact rather than timing-tolerant. Flake that does appear is a
  harness defect to fix, not a reason to retry-until-green.
- **The harness can give false confidence about feel.** Mitigated by the hard
  rule (assert state, never feel) and the retained per-milestone human pass.
  Writing a harness assertion that *pretends* to check feel is a process
  violation.
- **Authoring integration scenes is itself fragile** (the ADR-0011 `.tscn`
  text-authoring failure modes apply). Same guardrails: isolated single-concern
  commits, a headless load check, treat a broken harness scene as a Claude defect.
- **CI now depends on an external action** (setup-godot) and a specific Godot
  build being fetchable. A pinned version and a fallback note keep this from being
  a silent single point of failure.
- **Documentation updated in the accepting commit** (Decision Discipline):
  EDITOR_TASKS.md's preamble is revised to note that state-checkable verification
  has moved to the harness and the human checklist is now feel-only +
  not-yet-automated residue; CLAUDE.md's ADR table gains this row.
- **Reversible.** The harness is additive — if it proves untrustworthy, delete the
  job and fall back to human verification without touching game code.
