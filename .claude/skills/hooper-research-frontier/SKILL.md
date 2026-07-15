---
name: hooper-research-frontier
description: Catalog of open research problems where the hooper-game repo is positioned to produce a genuine, publishable-quality result (a measured writeup, a tool, a validated technique). Load this when the user asks "what could we research/publish/measure here", "what's novel about this project that we could prove", wants to start a case study on AI-driven development, a netcode replication-pattern writeup, a Roslyn analyzer, or a legibility/reaction-window metric — or when picking a frontier problem to work on. Provides, per problem: why existing literature falls short, what asset this repo uniquely has, the first three concrete steps in this repo, and a falsifiable "you have a result when…" bar.
---

# Hooper research frontier — open problems this repo can actually advance

**Honesty label, read first:** everything in this file is an OPEN PROBLEM or
CANDIDATE, not an achievement. Nothing below has been done yet. The repo's
*assets* (harness, git/issue trail, frame data) are real and verified; the
*results* do not exist until you produce them. Do not describe any of these as
completed work anywhere — see `hooper-external-positioning` for the proof bar
required before making any external claim.

Jargon used throughout, defined once:

- **Committed move** — a discrete action (crossover, steal, block, jump shot)
  that, once begun, runs a fixed Startup → Active → Recovery frame arc with no
  cancel (ADR-0003). Frame counts live in `scripts/Input/MoveFrameData.cs` and
  each move's `DefaultFrameData`.
- **Harness** — the headless Godot integration-test surface (ADR-0016):
  scenes under `tests/integration/` run with `--headless`, exit code 0 = pass.
  See `hooper-verification-and-qa` for the full runbook.
- **Autonomous merge** — a PR merged by the agent orchestrator on green CI +
  harness + code review, without a human reading the diff (ADR-0015/0019).

## The three frontier directions (locked by the human)

| # | Direction | Why this repo, specifically |
|---|-----------|------------------------------|
| 1 | **AI-driven development methodology** | A rare fully-instrumented case study: a solo dev with no game-dev experience driving a nontrivial networked game entirely through AI agents, with harness-proven autonomous merges (ADR-0015/0016/0017/0019), a complete ADR ledger (`docs/adr/`), and a full git (446+ commits as of 2026-07-12) and issue (160 issues, 55 merged PRs as of 2026-07-12) trail. |
| 2 | **Netcode at indie scale** | Server-authoritative prediction + deterministic mini-physics (ADR-0002/0004) on Godot's thin multiplayer layer, with *discrete committed moves* layered on analog movement (ADR-0003) — a hybrid input model that mainstream rollback/prediction literature (GGPO-style fighting games, Source-style FPS prediction) doesn't cover. |
| 3 | **Legible competitive design** | "Legibility is a competitive requirement, not an aesthetic" (CLAUDE.md §1, ADR-0003): telegraphed commitment is a design invariant here. Nobody has turned it into a *measured* property; this repo has the frame data and the netcode to compute it. |

Every candidate problem below serves one of these directions. Pick ONE
problem, work it to its result bar, and write it up — a half-finished tool or
an anecdote-grade writeup is not a result.

---

## Problem 1 — Harness-closed autonomy case study (direction 1)

**Question:** do harness-proven autonomous merges leak more defects than
human-reviewed merges — and if so, what *kind* of defects escape?

**(a) Why the state of the art falls short.** Published claims about AI-agent
code quality are almost entirely benchmark-based (SWE-bench-style resolved-%
on curated issues) or vendor anecdotes. There is very little longitudinal data
from a single real project where (i) the merge gate is explicitly defined and
machine-checkable, (ii) every escaped defect is filed as an issue traceable to
its source PR, and (iii) the whole history is public and replayable. "Does
proven-by-harness actually substitute for human review, and where does it
fail?" is an open empirical question, not a solved one.

**(b) What this repo uniquely has.** As of 2026-07-12: 55 merged PRs
(verified: `gh pr list --state merged --limit 200 --json number -q 'length'`),
each classifiable as pre- or post-ADR-0015 (autonomous-merge era). Escaped
defects are *labeled and traceable*: e.g. issues #209 (crossover velocity
cliff) and #210 (crossover exit-vector client/server divergence) were both
filed from review of PR #205 — the follow-up bug names its source PR. The
repo's dominant pattern is the "self-audit cascade" (feature merges, a fresh
review pass files 2–4 follow-up bugs: steal #96 → #174–#182; block #98 →
#214/#216/#217), which means the defect-discovery process itself is on the
record. Merge gates are written down in ADR-0015/0016, so "what the gate was
supposed to catch" is not reconstructed from memory.

**(c) First three steps in this repo:**

1. Build the PR ledger. From the repo root:
   ```
   # run from the repo root
   gh pr list --state merged --limit 200 --json number,title,mergedAt,body --jq '.[] | [.number, .mergedAt, .title] | @tsv'
   ```
   For each PR, record: merge date, whether it predates ADR-0015 (read
   `docs/adr/0015-autonomous-merge-proven-by-harness.md` for the adoption
   date), and whether it carried harness coverage (its body/commits mention a
   `tests/integration/` scenario).
2. Build the defect-escape ledger. For every issue labeled as a bug follow-up,
   trace it to the PR that introduced the defect:
   ```
   gh issue list --state all --limit 300 --json number,title,body --jq '.[] | select(.body | test("PR #[0-9]+|found (in|by) review|follow-up"; "i")) | [.number, .title] | @tsv'
   ```
   Then hand-verify each candidate link by reading the issue body
   (`gh issue view <n>`) — the grep is a lead-generator, not the ledger.
   Known anchors to start from: #209/#210 ← PR #205; #174–#182 ← the #96
   steal PR chain; #214/#216/#217 ← PR #215.
3. Define the metric before counting: defect-escape rate = (issues tracing to
   a PR as introduced-defect) / (merged PRs) per cohort
   (human-reviewed vs autonomous), with a defect-severity split
   (crash / correctness / cosmetic / accepted-trade-off — the repo has
   explicit accepted non-fixes, e.g. issue #189 and the BehindTheBack revert
   gap recorded only in commit `16bb309`; decide their treatment up front and
   state it).

**(d) You have a result when:** a writeup exists containing measured
per-cohort escape rates with the tracing methodology stated, at least one
falsifiable claim (e.g. "autonomous merges escaped X defects per PR vs Y for
human-reviewed, and the escapes cluster in <category>"), and a threats-to-
validity section (small n, cohorts differ in time period and task difficulty,
the same agents wrote both cohorts' code). If the honest conclusion is "n is
too small to distinguish the cohorts," that IS a result — report it. An
anecdote-grade "the autopilot seems fine" is NOT a result.

---

## Problem 2 — Choke-point-bypass static analyzer (directions 1 + 2)

**Question:** can a Roslyn analyzer catch the "new code bypasses the mandated
choke point" bug class at compile time, validated against this repo's real
history?

**(a) Why the state of the art falls short.** Generic analyzers (banned-API
lists, `BannedApiAnalyzers`) can forbid a method outright, but this bug shape
is subtler: `_machine.Begin()` is *legal* inside the choke point
(`PlayerController.BeginCommittedMove`) and in harness seams, and *illegal*
everywhere else. That "legal only from these call sites" rule isn't expressible
in off-the-shelf banned-API tooling, and there is no published account of
validating such an analyzer against a real project's actual historical
bypass bugs (as opposed to synthetic examples).

**(b) What this repo uniquely has.** The bug happened here **twice,
independently, weeks apart** — first in the steal work, then in block —
which is the empirical evidence that review vigilance alone doesn't hold.
The block instance is fixed in commit `7616a63` (verified 2026-07-12:
"fix(defense): route block through BeginCommittedMove and gate the
server-side begin" — two sites called `_machine.Begin(new BlockMove())`
directly, bypassing the choke point that clears the stale pivot latch;
`git show 7616a63` shows the exact before/after). That gives you a real
RED case: check out the parent commit and the analyzer must fire; check out
`7616a63` and it must go quiet. A complication that makes the problem honest:
the harness seams (`tests/integration/*HarnessSeam.cs`) compile into the game
assembly and legitimately call internal machinery — though note the newer
seam convention says seams must ALSO go through `BeginCommittedMove`
(a prior seam that didn't wasn't testing the real path), so decide whether
seams are an allowlist or additional targets.

**(c) First three steps in this repo:**

1. Map the legal call sites of `_machine.Begin`:
   ```
   # run from the repo root
   grep -rn "_machine.Begin\|BeginCommittedMove" scripts/ tests/integration/ --include=*.cs
   ```
   Record which files/methods are the choke point, which are seams, and
   confirm nothing else calls `_machine.Begin` directly today.
2. Reconstruct the RED case:
   ```
   git show 7616a63 -- scripts/Player/PlayerController.cs
   git show 7616a63^:scripts/Player/PlayerController.cs > "C:/Users/THEKIN~1/AppData/Local/Temp/claude/before-7616a63.cs"
   ```
   (Write the extracted file to your scratchpad, NOT into the repo.) The
   analyzer's acceptance test is: fires on the `7616a63^` version, silent on
   HEAD.
3. Scaffold the analyzer OUTSIDE the game csproj (a new
   `Microsoft.CodeAnalysis.CSharp` class library + a test project using
   `Microsoft.CodeAnalysis.Analyzer.Testing`). Rule sketch: report any
   invocation of `CommittedMoveMachine.Begin` whose containing method is not
   `BeginCommittedMove` (plus an explicit, justification-required allowlist).
   Note the machine is `scripts/Input/CommittedMoveMachine.cs`. Only wire it
   into `"HOOPER GAME.csproj"` after it passes its own tests — and that wiring
   is a repo change that goes through normal change control
   (`hooper-change-control`), not something this skill authorizes.

**(d) You have a result when:** the analyzer (i) fires on the pre-`7616a63`
code, (ii) is silent on current `main`, (iii) fires on a freshly-written
synthetic bypass (e.g. a fake new move calling `_machine.Begin` directly), and
(iv) its false-positive story for seams is decided and documented. Stretch:
generalize the rule spec so other projects can express "method X is only
callable from method Y" — that generalization plus the real-history validation
is the publishable unit.

---

## Problem 3 — Discrete-intent replication pattern (direction 2)

**Question:** can the "one-shot reliable RPC for discrete committed-move
intent, layered over unreliable continuous state" pattern be documented and
harness-proven as a reusable pattern for the whole burst-move family?

**(a) Why the state of the art falls short.** Netcode literature is dominated
by two poles: full rollback (GGPO — resimulate on late input) and continuous
state replication with client prediction (FPS-style). A hybrid input model —
analog movement replicated unreliably + discrete, uncancelable committed moves
whose *parameters* (e.g. burst direction) must arrive exactly once and
consistently on both sides — sits between the poles, and there is no
standard-named pattern for it. Issue #210 (open as of 2026-07-12: "Crossover
exit vector can diverge between client and server under jitter/packet loss")
is precisely the failure mode of getting this wrong: the discrete intent rode
an unreliable-ordered input stream, so client and server could compute
different exit vectors.

**(b) What this repo uniquely has.** A working instance of the pattern
already in production: `Crossover.BurstDirection` and
`BehindTheBack.BurstDirection` (`scripts/Input/Crossover.cs:33`,
`scripts/Input/BehindTheBack.cs:56`) are carried by the one-shot
`RequestBeginMove` / display-broadcast RPCs in
`scripts/Player/PlayerController.cs` (see the doc comments around lines
~486, ~1197, ~1745 — "that RPC fires once at Begin()"). Issue #210's body
prescribes extending exactly this approach as the fix. The pattern has a
whole family of upcoming consumers: #197 (step-back), #199
(between-the-legs), #201 (spin move) — all open burst moves that will
each hit the same divergence bug if built ad hoc. And uniquely: the repo has
a *dual-instance packet-level harness* (`tests/integration/run-net-*.sh`,
`NetBehindTheBackSweepTest.tscn`) that can prove the pattern under real
two-process ENet conditions, not simulation. Related hard-won lore is in the
memory of #175: discrete/level-triggered flags (`WasRecoveryEnteredEarly`)
must be designed to survive UnreliableOrdered drops — the same principle from
the reconciliation side.

**(c) First three steps in this repo:**

1. Read the prescription and the existing instance:
   ```
   # run from the repo root
   gh issue view 210
   grep -n "BurstDirection" scripts/Player/PlayerController.cs
   ```
   Write down, precisely: which values are "discrete intent" (sent once,
   reliable, at Begin) vs "continuous state" (streamed, unreliable) for each
   existing move.
2. Read the dual-instance harness template you'll extend:
   `tests/integration/run-net-behindtheback-sweep.sh` +
   `NetBehindTheBackSweepTest.tscn`, and
   `docs/handoffs/dual-instance-harness.md` if present (handoffs are
   gitignored scratch — may be absent on a fresh clone; the tests-harness
   knowledge is also in `hooper-verification-and-qa`). Note the topology rule:
   remote-display proofs need `HostGame` (listen server), not
   `StartDedicatedServer`; ports 23456–23459 are taken, pick a new one.
3. Draft the pattern doc as a checklist ("a burst move's begin ships its
   full deterministic parameter set in the one-shot begin RPC; the receiving
   side never derives those parameters from streamed input"), then enumerate
   every current violation:
   ```
   grep -n "_pendingRawStick\|RequestBeginMove\|DisplayMoveId" scripts/Player/PlayerController.cs
   ```
   (#210 root-causes `_pendingRawStick` timing as the divergence source —
   confirm against the issue body before asserting it.)

**(d) You have a result when:** the pattern is written down as a checklist a
zero-context agent can apply to a new burst move, #210's fix is implemented
per the pattern (through normal change control — issue, branch, PR, green
gates), AND a dual-instance harness scenario demonstrates divergence-free
exit vectors under induced jitter/loss where the pre-fix code diverges. The
RED half matters: if you cannot reproduce the divergence pre-fix in the
harness, you have not proven the pattern fixes anything — say so and report
what you could and couldn't reproduce.

---

## Problem 4 — Measurable legibility metric (direction 3)

**Question:** can "this move is legible to the defender" be computed as a
number — the actual reaction window a *remote* defender gets — with a
pass/fail threshold per move?

**(a) Why the state of the art falls short.** Fighting-game frame data is a
mature practice (startup/active/recovery published per move), but it assumes
offline/rollback play where both players see the same frame. In a
server-authoritative prediction model, the defender sees the attacker's
commitment only after it has traveled attacker→server→defender, so the *real*
reaction window is `telegraph duration − network delay − human reaction
floor`, and it differs per move and per connection. Design writing treats
"telegraphed" as a binary aesthetic judgment; nobody publishes the computed
remote reaction window as a shippable design gate. This repo's identity
explicitly requires legibility ("a competitive requirement, not an
aesthetic" — ADR-0003), which makes a metric a design *need*, not a nicety.

**(b) What this repo uniquely has.** Exact, code-authoritative frame data at
60 ticks/s in each move's `DefaultFrameData` (verified 2026-07-12,
`startup/active/recovery` in ticks):

| Move | File | Startup | Active | Recovery |
|------|------|---------|--------|----------|
| Crossover | `scripts/Input/Crossover.cs` | 6 | 3 | 12 |
| Steal | `scripts/Input/StealMove.cs` | 8 | 8 | 20 |
| Block | `scripts/Input/BlockMove.cs` | 10 | 8 | 20 |
| JumpShot | `scripts/Input/JumpShot.cs` | 18 | 4 | 20 |

(Re-verify before computing — these are tunables and have been retuned
before; see the turn-rate saga where code and ADR drifted. The authoritative
full frame-data table, including feint windows, is
`hooper-duel-design-reference` §2.) Plus: the exact
replication path that determines *when* the defender's client learns of the
move (`RequestBeginMove`/display broadcast in
`scripts/Player/PlayerController.cs`), a deterministic 60Hz tick
(`physics_ticks_per_second` is NOT set in `project.godot` — the engine
default of 60 applies), and a dual-instance
harness that can *measure* the attacker-begin-tick → defender-display-tick
gap empirically on localhost rather than assuming it.

**(c) First three steps in this repo:**

1. Derive the model on paper first. For each committed move M and one-way
   delay `d` (seconds): defender's reaction window
   `W(M, d) = StartupFrames(M)/60 − d_display − r`, where `d_display` is the
   time from the attacker's Begin tick to the tick the defender's client
   *renders* the telegraph (this is NOT just RTT/2 — it includes the server
   hop and any broadcast cadence), and `r` is a human reaction floor
   (~0.20–0.25 s for a prepared visual reaction; cite a source, don't invent
   one). Immediate red flag to investigate: Crossover startup is 6 ticks =
   100 ms, which is negative-window at ANY realistic delay + reaction floor —
   is the mind game meant to be read from earlier cues (spacing, stance), or
   is the move illegible by construction? Either answer is a finding.
2. Ground `d_display` in the code, not assumption: trace when the remote copy
   first shows the move —
   ```
   # run from the repo root
   grep -n "DisplayMoveId\|RequestBeginMove\|BroadcastState" scripts/Player/PlayerController.cs scripts/Networking/*.cs
   ```
   and read `hooper-netcode-reference` for how state broadcast cadence works
   here. Determine: does the telegraph ride the reliable one-shot begin RPC
   or the unreliable state stream? (The answer changes the loss-sensitivity
   of the whole metric.)
3. Measure instead of trusting the derivation: extend the dual-instance
   harness (template: `NetBehindTheBackSweepTest.tscn` + its shell script) to
   timestamp, in ticks, attacker-begin on the server instance and
   first-visible-telegraph on the client instance, and report the gap.
   Localhost gives you the floor; add artificial delay only if you can do it
   honestly (document how).

**(d) You have a result when:** a table exists giving, per committed move,
the computed and *harness-measured* remote reaction window at stated delay
values, plus a proposed pass/fail threshold (e.g. "every committed move with
a counter must give ≥ X ms at 50 ms one-way delay") — and at least one move
either passes or measurably fails the threshold. If a move fails, file the
finding as an issue with the numbers (that is a design input for the human
feel pass, per ADR-0015 — the metric informs, it does not auto-retune).
"Legibility feels fine" is NOT a result; a number with a method is.

---

## Working rules for any frontier problem

1. **Results live outside the gameplay lane.** Analyzer projects, writeups,
   measurement scripts: keep them out of `"HOOPER GAME.csproj"`'s compile
   glob (remember it hoovers `**/*.cs` from the repo root except `tests/`,
   re-including `tests/integration/` — a stray `.cs` file at the wrong path
   breaks the game build). Prototype in your scratchpad; land repo artifacts
   only through normal change control.
2. **Repo changes go through `hooper-change-control`.** A frontier problem
   that touches production code (Problem 3's #210 fix, Problem 2's csproj
   wiring) is ordinary issue → branch → PR → green-gates work. This skill
   picks the problem; it does not waive the process.
3. **Verify every number the day you use it.** Frame data, PR counts, issue
   states above are date-stamped 2026-07-12 and WILL drift. Re-verification
   commands are in Provenance below.
4. **Negative results are results.** "n too small," "divergence not
   reproducible," "window is negative for all moves" — report them with the
   method. The failure mode to avoid is oversell, per
   `hooper-external-positioning`.

## When NOT to use this

- **You want to claim something is novel/impressive TODAY** (a README blurb,
  a post, a pitch) → `hooper-external-positioning` owns the proof bar for
  external claims about the project as it stands.
- **You're working the #206 held-ball steal-immunity campaign** → that has
  its own executable plan in `hooper-held-ball-steal-campaign`; it is design
  work, not open research.
- **You need an analysis recipe** (how to sweep a curve, characterize a
  distribution, build a measurement harness step-by-step) →
  `hooper-proof-and-analysis-toolkit` owns the worked how-to recipes; this
  skill only says *what* is worth measuring and *why*.
- **You're fixing a bug or building a milestone feature** → that's ordinary
  work: `hooper-change-control` for process, `hooper-debugging-playbook` /
  `hooper-failure-archaeology` for the bug, `/tdd` or
  `/doubt-driven-development` for discipline.
- **You need harness mechanics** (how scenes/seams/dual-instance scripts
  work) → `hooper-verification-and-qa`; netcode theory-as-applied →
  `hooper-netcode-reference`.

## Provenance and maintenance

Authored 2026-07-12; reviewed and corrected 2026-07-15
(`physics_ticks_per_second` is NOT set in `project.godot` — engine default 60
applies; hardcoded machine paths replaced with repo-root-relative commands).
Verified against the live repo and GitHub state on the authoring
date: commit `7616a63` content (`git show 7616a63`), `MoveFrameData` defaults
read directly from `scripts/Input/{Crossover,StealMove,BlockMove,JumpShot}.cs`,
issues #209/#210 both OPEN, 55 merged PRs, `BurstDirection` call sites in
`scripts/Player/PlayerController.cs`. Frontier directions and the four
candidate problems were designated by the human (2026-07-12); their status
(open, unstarted) was accurate at authoring time.

Re-verify before relying on volatile facts:

(run from the repo root)

- Frame data: `grep -n "new(startupFrames" scripts/Input/*.cs`
- Merged-PR count: `gh pr list --state merged --limit 200 --json number -q 'length'`
- Issue states: `gh issue view 209 --json state -q .state` (same for 210, 197, 199, 201, 189, 206)
- Choke-point call sites: `grep -rn "_machine.Begin" scripts tests/integration --include=*.cs`
- The 7616a63 anchor: `git show 7616a63 --stat`
- Tick rate: `grep physics_ticks project.godot` — expect NO match; the
  setting is absent, so the engine default of 60 ticks/s applies. A match
  appearing means someone changed the tick rate — re-derive every
  ticks↔seconds figure in this file.

If a problem here gets *worked to its result bar*, move its write-up to a
durable home (docs/ + an issue/PR trail) and update this file to point at the
result instead of restating the candidate — a solved problem listed as open
is as misleading as an open one listed as solved.
