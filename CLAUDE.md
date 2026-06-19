# CLAUDE.md — Project Context for Claude Code

This file is read at the start of every Claude Code session. It encodes the
design identity, repo conventions, and the rules of engagement so that any
coding session starts already knowing the project.

## Architecture decisions live in `docs/adr/`

Read **all ADRs** at session start before writing engine-facing code. They are
locked unless explicitly revisited (see Decision Discipline in §4 below).

| ADR | Decision |
|-----|----------|
| [ADR-0001](docs/adr/0001-engine-godot-csharp.md) | Engine: Godot 4 .NET / C# (not Unity, not S&box) |
| [ADR-0002](docs/adr/0002-networking-server-authoritative.md) | Networking: server-authoritative + client prediction (not rollback/GGPO) |
| [ADR-0003](docs/adr/0003-input-model-hybrid.md) | Input: hybrid analog movement + discrete committed moves, no flow-cancel |
| [ADR-0004](docs/adr/0004-deterministic-ball-physics.md) | Ball physics: custom deterministic mini-physics (not Godot Physics/Jolt) |
| [ADR-0005](docs/adr/0005-community-model-dedicated-servers.md) | Community: self-hosted dedicated servers + server browser (CS 1.6 style) |
| [ADR-0006](docs/adr/0006-renderer-mobile.md) | Renderer: Godot Mobile (D3D12), not Compatibility/Forward+ |
| [ADR-0007](docs/adr/0007-dedicated-server-topology-discovery.md) | Dedicated-server topology (listen→headless) + LAN discovery wire format |

---

## 1. What we are building

A **competitive 1v1 basketball game**. The design identity in one sentence:
**the duel is the space between two players and who breaks first.** It is
deliberately NOT arcade like NBA 2K — it is technical and skill-based, closer in
spirit to a fighting game (Tekken) crossed with the competitive legibility of
Undisputed 3.

The developer building this has **no prior game-dev experience** and is driving
the project primarily through AI-written code (Claude Code). Therefore: explain
non-obvious choices, prefer clarity over cleverness, and never silently assume
game-dev knowledge on the human's part.

### Design identity (do not relitigate without being asked)

- **Spine:** footwork / spacing — separation creation vs. denial is the core 1v1
  interaction.
- **On top of the spine:** a commitment / mind-game layer — both players read and
  commit; wrong reads are punished.
- **Subordinate systems** (live INSIDE the spacing spine, not co-equal pillars):
  timing windows (shot release, steal, block) and stamina / resource.
- **Legibility is a competitive requirement, not an aesthetic:** committed moves
  must engage the whole body (planted feet, weight, recovery) so startup frames
  are visibly telegraphed and both players can make fair reads. Bounded — primary
  anti-goal: *arcade decoupling* of action from physical commitment (unplanted
  shots, move-and-strike, free cancels — *EA UFC 5*), which kills realism and the
  mind game at once; secondary anti-goal: manufactured comedic jank (*Goat
  Simulator*). Polish itself is fine. Target feel is *Undisputed 3*. See
  [ADR-0003](docs/adr/0003-input-model-hybrid.md).
- **Defense:** symmetric core (mirror footwork + committed reads) with a
  deliberate asymmetric tilt toward reaction.

---

## 2. Current milestone

> **M6b — Possession loop (active gameplay spine).** Turn the single shot into a
> continuous half-court 1v1 playable to any `TargetScore > 1`: make-it-take-it,
> live loose-ball rebounds, and the take-it-back ("clear") rule. Server-
> authoritative with client prediction throughout (ADR-0002); the ball stays
> deterministic mini-physics (ADR-0004). The possession ruleset is being recorded
> in **ADR-0008** (issue #47) as part of this milestone. Epic: **#46**.
>
> **M7a — Static readability pass (parallel presentation track).** Independent of
> M6b and may proceed alongside it: swap the capsule for a humanoid mesh, add
> cosmetic facing + burst lean, and directional shadows — all visual-only, with
> collision and netcode unchanged. Foundation sub-issue #37 (extract `MovementMath`
> + green-before-merge gate) blocks the rest. Epic: **#53**.
>
> **M7b is DEFERRED — do NOT pull it forward.** The rigged-animation pass (#54/#41)
> starts only after M7a is proven in-editor.

### Milestone status

| Milestone | Status | Epic |
|-----------|--------|------|
| M1a — Local movement | Done | #1 |
| M1b — Networking on top | Done | #4 |
| M2 — Local ball mini-physics | Done | #8 |
| M3 — Hybrid input: committed moves | Done | #13 |
| M4 — Networked ball + committed moves | Done | #19 |
| M5 — Win condition + scoring | Done | #23 |
| M6 — Dedicated server + server browser | Code done; editor verify (#32) trails M6b | #28 |
| **M6b — Possession loop** | **Active (current)** | #46 |
| **M7a — Static readability pass** | **Active (parallel presentation track)** | #53 |
| M7b — Rigged humanoid animation | DEFERRED (after M7a proven) | #54 |

GitHub Issues is the source of truth for the live state of each milestone and its
sub-issues; this table is the at-a-glance map.

Do not build ahead of the current milestone unless asked. M6b and M7a are both
open for work; M7b is not.

---

## 3. Repo conventions (Godot has no enforced layout — this is ours)

- `project.godot`, the `.sln`, and the `.csproj` live at the **project root**
  (Godot generates the .sln/.csproj there; don't move them — Godot has known
  bugs relocating them).
- **`scripts/`** — all C# code, the part Claude Code owns. Subfolders by
  responsibility: `Player/`, `Networking/`, `Input/`, `Ball/`, `Systems/`.
- **`scenes/`** — `.tscn` scene files. Authored in the Godot editor by the human
  (see EDITOR_TASKS.md). Claude Code writes the C# a scene's nodes reference, but
  the human wires nodes in the editor.
- **`assets/`** — models, textures, sounds. Placeholder/gray is fine for now.
- One script = one node responsibility. `partial` class extending the node type.
- Comment the "why," not the "what," especially around netcode and the
  deterministic ball, because the human is learning the engine.
- When you finish a unit of work, tell the human exactly which EDITOR steps (if
  any) they must do to see it run — you cannot do them.
- Prefer one clear path; explain the tradeoff in a sentence and proceed.

### Issue tracker

GitHub Issues is the sole task tracker. TASKS.md no longer exists.

- Issues labeled `afk` are Claude Code's to implement.
- Issues labeled `hitl` require a human editor step (see EDITOR_TASKS.md) before they can close.
- **Done means proven, not written.** A `hitl` issue is only closed after the human confirms it in the editor (the relevant EDITOR_TASKS steps). Do not close it on code alone.
- When finishing a unit of work, tell the human which issue(s) to close and which EDITOR_TASKS steps (if any) they must complete first.
- **Closing keyword placement.** Exactly one artifact closes an issue, and it carries `Closes #X` in its *description/body* (never the subject line), so GitHub auto-closes the issue and the close is traceable:
  - **Single-commit fix → straight to `main`:** that commit's body carries `Closes #X` (the M1a pattern).
  - **Multi-commit work → branch + PR:** the *PR body* carries `Closes #X`; the commits do not (see Branching below).

### Branching & multi-commit issues

Default to small: if an issue is one focused commit, commit straight to `main`.
When an issue's solution naturally spans several commits — most M1b+ work will —
isolate it on a branch and land it via a PR. This keeps `main` releasable and
gives the change a single review surface.

- **Branch per issue.** Name it `<type>/<issue#>-<slug>`, e.g. `feat/5-host-join`.
  One branch per issue, or per epic if its sub-issues are tightly coupled.
- **Keep commits single-concern on the branch.** The commit-clean discipline
  still applies to each commit; several focused commits is the goal, NOT one
  mega-commit. Subjects stay conventional (`feat(net): ...`). A commit body may
  link the issue with `Refs #X`, but must NOT use a closing keyword — only the
  PR closes.
- **The PR closes the issue.** Put `Closes #X` (plus any sibling sub-issues the
  PR fully resolves) in the PR *body*. Merging to `main` is what closes them.
- **Merge, don't squash.** Preserve the focused commit history — the per-step
  rationale is the documentation trail for a big change; squashing discards it.
- **`hitl` still means proven.** A PR may merge the *code*, but must not
  auto-close a `hitl` issue before the human verifies in-editor. For a `hitl`
  issue, omit its `Closes` from the PR and let the human close it after the
  EDITOR_TASKS step (or have them verify before merge). `Done means proven`
  overrides auto-close.
- **The human owns merges**, as they own commits: open the PR with `gh`, but let
  the human review and merge.

### Decision Discipline

If during a session we make or change an architectural decision (engine,
networking model, input model, ball physics, community model — anything currently
recorded in `docs/adr/`), do not just act on it — **add a new ADR or update the
Status/Superseded-by fields of an existing one in `docs/adr/`**, with the
reasoning and the rejected alternative, in the same commit as the code.

If a decision is entirely new (no existing ADR covers it), create the next
numbered ADR file following the template in `docs/adr/0000-template.md`.

If I ask you to do something that contradicts a locked ADR, stop and flag the
contradiction before writing code; don't silently comply.

### Handoffs (cross-session work)

When work spans more than one session — typically a big change like networking or
the ball physics — leave the next session a handoff at
`docs/handoffs/<topic>.md` (e.g. `docs/handoffs/M1b-networking.md`). That folder
is **gitignored** (handoffs are scratch in-flight state, not durable docs); only
its `README.md` is tracked. Put *only* what isn't already in CLAUDE.md, the ADRs,
the issues, or the code: where you were interrupted, build/run state, anything
verified the hard way, gotchas, and remaining `hitl` editor steps. See
`docs/handoffs/README.md` for the full convention. At session start, check
`docs/handoffs/` for an existing handoff before assuming a cold start.

---

## 4. Open technical risks

- Custom prediction + lag compensation on Godot's thin multiplayer layer is the
  hardest part of the project. Prove it in isolation (Milestone 1) first.
- The deterministic mini-physics ball is second-hardest. Self-contained, tested.
- Godot C# API churn + GDScript-centric examples: use the **Context7 MCP
  server** to fetch live Godot docs before writing unfamiliar engine-facing
  calls. Don't copy GDScript patterns without translating.
