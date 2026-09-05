# AGENTS.md — Project Context for Coding Agents

This file is read at the start of every coding-agent session. It encodes the
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
| [ADR-0008](docs/adr/0008-possession-rules.md) | Half-court 1v1 possession rules: make-it-take-it, live rebound, take-it-back/clear |
| [ADR-0009](docs/adr/0009-shot-accuracy-scatter.md) | Shot accuracy: deterministic, server-authoritative distance-based scatter (amended for movement/contest/facing/on-ball-contest penalties) |
| [ADR-0010](docs/adr/0010-authoritative-heading.md) | Player heading: server-authoritative, bounded non-linear turn rate, integrated into Move() |
| [ADR-0011](docs/adr/0011-claude-authors-scenes.md) | Claude authors `.tscn`/`.res`/`project.godot` by text-edit; human owns feel + verification only |
| [ADR-0012](docs/adr/0012-authoritative-ball-hand.md) | Ball-hand-side is server-authoritative state, not cosmetic |
| [ADR-0013](docs/adr/0013-afk-hitl-separate-issues.md) | AFK build work and HITL editor verification live in separate issues (no dual-labelled issue) |
| [ADR-0014](docs/adr/0014-reference-game-decision-authority.md) | Reference-game decision authority: ranked references (real half-court ball > Undisputed 3 feel > 2K taxonomy) — self-resolve reference-grounded calls on the record, escalate only genuine design calls |
| [ADR-0015](docs/adr/0015-autonomous-merge-proven-by-harness.md) | Autonomous merge for the AFK lane + harness-closed `hitl`; "Done means proven" redefined as proven-by-harness (supersedes "human owns merges"); feel batched to one human pass per milestone |
| [ADR-0016](docs/adr/0016-headless-verification-harness.md) | Headless Godot harness (`tests/integration/`, `--headless`, exit-code pass/fail) is the official verification surface — the automated stand-in for human in-editor verification of state-checkable acceptance criteria |
| [ADR-0017](docs/adr/0017-autopilot-activates-deferred-milestones.md) | Autopilot may activate DEFERRED milestones in the §2 dependency order without a per-milestone human "go" (supersedes "do not build ahead of the current milestone"); activation gates pickup, not merge |
| [ADR-0018](docs/adr/0018-defensive-timing-window-model.md) | Defensive timing-window & reaction-tilt model (tick-interval overlap, `DefensiveResolution.Succeeds`) |
| [ADR-0019](docs/adr/0019-session-driven-orchestration-loop.md) | Session-driven orchestration loop: an Opus `orchestrator` agent runs dispatch→review→merge within a live human-started session (no unattended cron / stored credential — rejected as overengineering for a solo dev) |
| [ADR-0020](docs/adr/0020-performance-target-low-spec.md) | Performance & asset target: low-to-mid-spec devices, calibrated to NBA 2K14 old-gen (Xbox 360/PS3) as the fidelity ceiling — human external commitment, does not reopen M15 |
| [ADR-0021](docs/adr/0021-feel-taste-deferred-indefinitely.md) | Feel passes and taste checks deferred until the human judges the game "sufficiently built"; amends ADR-0015 (per-milestone pass → human-scheduled consolidated pass in #173) and ADR-0017 (activation gate drops the feel-pass requirement) |
| [ADR-0022](docs/adr/0022-rim-finishing-offensive-vertical.md) | Rim-finishing offensive vertical (un-defer of #203): a new ADR, not an ADR-0009 amendment — the layup reuses the existing shot-accuracy model verbatim, the drive-gather reuses the hybrid-gather momentum model, the euro-step reuses the exit-cone precedent |
| [ADR-0023](docs/adr/0023-authoritative-gate-prediction-tolerance.md) | Server-authoritative move gates widen their threshold by a bounded network tolerance and reject out-of-tolerance requests — they never begin a move the client did not request (rejects #236's JumpShot-fallback: it breaks the moveId invariant both reconciliation gates depend on) |
| [ADR-0024](docs/adr/0024-hitl-async-evidence-restructure.md) | HITL restructured to async evidence (Proposed): mandatory harness-first decomposition of `hitl` verifies, rendered-evidence artifact review replaces live editor sessions (spike-gated), default-with-veto unblocks bounded decision gates; feel still never auto-accepted (ADR-0015 gate 4 / ADR-0021 intact) |
| [ADR-0025](docs/adr/0025-on-ball-body-contact.md) | Accepted contact model: deterministic pre-solver response and depenetration, raw opponent snapshots, binary speed-and-facing set predicate, both movement paths; implementation tracked under #347/#355 |

---

## 1. What we are building

A **competitive 1v1 basketball game**. The design identity in one sentence:
**the duel is the space between two players and who breaks first.** It is
deliberately NOT arcade like NBA 2K — it is technical and skill-based, closer in
spirit to a fighting game (Tekken) crossed with the competitive legibility of
Undisputed 3.

The developer building this has **no prior game-dev experience** and is driving
the project primarily through AI-written code. Therefore: explain
non-obvious choices, prefer clarity over cleverness, and never silently assume
game-dev knowledge on the human's part.

### Design identity (do not relitigate without being asked)

The identity itself is locked. But most day-to-day design questions are not
identity changes — they are *reference-grounded* ("what does real half-court 1v1
ball do here? how does *Undisputed 3* commit this? what does 2K call it?").
**Self-resolve those on the record per [ADR-0014](docs/adr/0014-reference-game-decision-authority.md)**
(ranked references, cite-or-ask) instead of routing them to the human. Only
genuine design calls — an identity/anti-goal change, an ADR contradiction, a true
reference deadlock, or a high-stakes irreversible decision — still come back to
the human.

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

> **Status checked against GitHub on 2026-09-05:** M8b (#171) and M9 (#75)
> are closed (both closed 2026-09-03); M10 (#89) is also closed. Their open
> verification issues remain separate from those build-epic closures.
>
> **The autopilot HOLDS at the M11 boundary** (human ruling 2026-07-20): M11
> (stamina, #90) is NOT activated and its foundation ADR #105 is NOT to be
> auto-drafted — both await an explicit human "go". M9's closure removes the
> former open-epic blocker; it does not lift this explicit hold or independently
> establish every ADR-0015 activation gate. M12/M13 remain downstream.
>
> **Animation build campaign:** batches #276/#302 are closed. #296's generic
> Startup/Active/Recovery clip repair landed in PR #363; #297's locomotion
> rest-frame repair landed in PR #364. The generic resolver fallback remains
> intentional; do not mistake its presence for an unfinished clip repair.
>
> Batch **#302** (2) is complete: its exact eleven clips are JabStep #304,
> RetreatDribble #305, StepBack #306, Hesitation #307, InAndOut #308,
> BetweenTheLegs #309, Spin #310, DriveGather #311, EuroStep #312, Layup #313,
> and Contest #314. The crossover #317 and fadeaway #318 Blender re-authors are
> also closed. #335 regenerated the six clips stale against #321's leg-IK fix.
>
> `tools/blender_anim_lib.py` (#315) is shared by every clip script — a change
> there is a change to all of them.
>
> **Verification debt:** #153's net/fence pass succeeded 2026-07-19, and #170's
> rig build is closed. #178 remains open for rig verification, with objective
> proof gaps split into #367. #301 remains a distinct moving-dribble visual
> verify with its own human-directed reference and explicit #173 exception;
> its build prerequisite #300 is closed. Capture spike #365 will test evidence
> capture, not automatic feel acceptance. #366 tracks automation of #32's
> dedicated-server journey; neither new task is implemented yet.
>
> **Next AFK work:** evidence tasks #365–#367, then #356's contact fixture and
> the accepted ADR-0025 implementation (#368–#370) under #347 / umbrella #355.
> Follow GitHub's parent and blocked-by links for the contact dependency order.
> #325 (sprint) precedes #345 (protection); #346 (stance) and #348 (post-up)
> still need scoped triage. #355 is an existing work umbrella, not a newly
> activated numbered milestone. This queue does not reopen M9/M10 or lift M11.
>
> **Closed-milestone detail is deliberately not repeated here.** What landed
> under M9's original scope and M10's defensive core lives in GitHub; the rules
> those issues settled live in the ADRs — notably ADR-0003 §"External events do
> not interrupt a committed move" (#189/#241: a committed move runs to
> completion even when a steal/OOB voids its payload, and the lost time IS the
> punishment) and ADR-0018's eight amendments. Tuning is no longer per-milestone
> — it consolidated into **#238**. Feel consolidated into **#173** (ADR-0021)
> and never gates activation; the two still-open feel/visual verify halves,
> **#184** (pivot clip) and **#185** (fadeaway indicator), are folded into that
> pass and do not gate anything.

### Milestone status

| Milestone | Status | Epic |
|-----------|--------|------|
| M1a — Local movement | Done | #1 |
| M1b — Networking on top | Done | #4 |
| M2 — Local ball mini-physics | Done | #8 |
| M3 — Hybrid input: committed moves | Done | #13 |
| M4 — Networked ball + committed moves | Done | #19 |
| M5 — Win condition + scoring | Done | #23 |
| M6a — Dedicated server + server browser | Build epic closed; #32 proof debt has AFK child #366 | #28 |
| M6b — Possession loop | Done (epic closed 2026-06-24; feel deferred to #173 per ADR-0021) | #46 |
| M7a — Static readability pass | Done | #53 |
| M7b — Rigged humanoid animation | Done (epic closed 2026-06-26) | #54 |
| M8 — Realism & polish pass | Build epic closed; remaining verification tracked separately | #61 |
| M8b — Realism & polish pass, continued | Closed 2026-09-03; #178 verification remains open | #171 |
| M9 — Basketball-related controls (offense) | Closed 2026-09-03 after July reopening; visual/feel debt remains separate | #75 |
| M10 — Defense & the reactive read | Done (epic closed 2026-07-20; feel deferred to #173 per ADR-0021) | #89 |
| M11 — Stamina & resource economy | DEFERRED (planning epic) | #90 |
| M12 — Match flow, HUD & session lifecycle | DEFERRED (planning epic) | #91 |
| M13 — Audio & game feel | DEFERRED (planning epic) | #92 |
| M14 — Training, onboarding & practice opponent | Closed — `wontfix` (2026-07-04) | #93 |
| M15 — Mobile, performance & release readiness | Closed — `wontfix` (2026-07-04) | #94 |

GitHub Issues is the source of truth for the live state of each milestone and its
sub-issues; this table is the at-a-glance map.

**M11–M13 are a forward roadmap, not a work queue.** They are deferred planning
epics recording *what comes next and why*, in dependency order: M9–M10 complete
the core duel (offense then defense), M11 adds the stamina pillar on top,
M12–M13 turn the loop into a game (flow + feel). Each stays DEFERRED until
explicitly activated, at which point its status flips to "Active" and (for the
umbrella epics) it stops merely accruing sub-issues. **M14 and M15 were closed
`wontfix` on 2026-07-04** — off the roadmap; their rows remain only so the
numbering stays legible. Feel never gates activation
([ADR-0021](docs/adr/0021-feel-taste-deferred-indefinitely.md) deferred it to
the consolidated human pass #173). The current evidence/contact queue and
the M11 hold are in §2; don't duplicate them here.

**Autopilot exception ([ADR-0017](docs/adr/0017-autopilot-activates-deferred-milestones.md)):**
the human has pre-authorised driving the full roadmap (now ending at M13 — M14/M15
closed `wontfix` 2026-07-04), so the autopilot
**may** activate a DEFERRED milestone without a per-milestone human "go" —
**but only by walking the dependency order documented in this table**, and only
after each predecessor milestone's epic is genuinely closed (CI + harness +
code-review + epic closed — the feel pass is **not** part of this gate; see
[ADR-0021](docs/adr/0021-feel-taste-deferred-indefinitely.md), which deferred it
to a human-scheduled consolidated pass) under [ADR-0015](docs/adr/0015-autonomous-merge-proven-by-harness.md).
Activation flips DEFERRED → Active in this table and gates *pickup*, not *merge*.
Outside that autopilot walk, the old rule still holds: do not build ahead of the
current milestone unless asked.

---

## 3. Repo conventions (Godot has no enforced layout — this is ours)

- `project.godot`, the `.sln`, and the `.csproj` live at the **project root**
  (Godot generates the .sln/.csproj there; don't move them — Godot has known
  bugs relocating them).
- **`scripts/`** — all C# code, authored by the coding agent. Subfolders by
  responsibility: `Player/`, `Networking/`, `Input/`, `Ball/`, `Systems/`.
- **`scenes/`** — `.tscn` scene files, plus `.tres`/`.res` resources and
  `project.godot`. Per [ADR-0011](docs/adr/0011-claude-authors-scenes.md), Claude
  Code authors these by **direct text-edit** as ordinary AFK work: adding/renaming
  nodes, setting properties, assigning exports/`NodePath`s, instancing sub-scenes,
  and Input Map entries. The human's role narrows to **feel/tuning judgments** and
  **in-engine verification** (see EDITOR_TASKS.md). AnimationTree **graph authoring**
  (BlendSpace points, state-machine nodes/transitions) is now **AFK** — spike #87
  proved a hand-authored tree loads and runs identically to an editor-authored one
  (see `docs/spikes/0011-animationtree-text-authoring.md` for the authoring gotchas,
  esp. the fragile flat `transitions` array). The FBX **retarget** subset (a
  `BoneMap` + `SkeletonProfile` assigned in a `.fbx.import` `_subresources` block)
  is also **AFK** — proven headless in #267 (see
  `docs/spikes/0012-headless-import-retarget.md`); only import operations with no
  headless path at all remain HITL. Scene edits
  are fragile (`ext_resource`/
  `sub_resource` IDs, `uid`, load-step counts) — so they ship in their own
  single-concern commit with a headless load check where a Godot binary is available.
- **`assets/`** — models, textures, sounds. Placeholder/gray is fine for now.
- **`addons/godot_dotnet_mcp/`** — vendored in-repo (MIT,
  [LuoxuanLove/godot-dotnet-mcp](https://github.com/LuoxuanLove/godot-dotnet-mcp)
  v1.3.0). An editor plugin, not game code: it runs an HTTP MCP server *inside
  the running Godot editor process* so a coding-agent session can read live
  editor/scene/runtime state (selected node, output, diagnostics) that a
  filesystem snapshot can't see. Setup steps (enabling the plugin, starting the
  `MCPDock` service, the `claude mcp add` invocation) are in
  `docs/godot-mcp-setup.md`.
  Its `dotnet_bridge/` subproject is excluded from the game assembly the same
  way `tests/` is (see `HOOPER GAME.csproj`) — never remove that exclusion, the
  bridge references Roslyn packages the game assembly doesn't have. Enabling
  the plugin writes an `[autoload]` singleton and `[editor_plugins]/enabled`
  entry into `project.godot`; that's expected, not a stray edit.
- **Physics colliders (the project runs Jolt — `3d/physics_engine="Jolt Physics"`):**
  never apply a **non-uniform scale** to a `CylinderShape3D`, `CapsuleShape3D`, or
  `SphereShape3D`. Their cross-section is a single *radius*, so a mismatched X/Z
  scale is impossible to honour — Jolt silently clamps it (you'll see
  `Failed to correctly scale shape … not supported by Jolt Physics` at load) and
  the collider stops matching its mesh. Author the size on the **shape resource**
  (`radius` / `height`) and keep the node's scale at `1`. `BoxShape3D` is exempt —
  a box has independent X/Y/Z extents. The visual `MeshInstance3D` may still be
  scaled freely; only the collision shape is constrained. If you find a scaled
  round collider in a `.tscn`, flag it.
- **Local Godot logging:** when launching a headless Godot command from a
  filesystem-sandboxed Windows agent, always add `--log-file .godot/<unique>.log`.
  Godot 4.7.1 Mono can native-crash when its default `user://logs` path is not
  writable. The shipped harness launchers already do this; use a distinct log
  file for concurrent server/client processes. Do not launch the Godot editor
  inside that sandbox: its editor-settings writes still require an unsandboxed
  process.
- One script = one node responsibility. `partial` class extending the node type.
- Comment the "why," not the "what," especially around netcode and the
  deterministic ball, because the human is learning the engine.
- When you finish a unit of work, tell the human exactly which EDITOR steps (if
  any) they must do to see it run — you cannot do them.
- Prefer one clear path; explain the tradeoff in a sentence and proceed.

### Issue tracker & change control

GitHub Issues is the sole task tracker. TASKS.md no longer exists.

**Full detail — the incident behind each rule, the merge-gate mechanics, the
ADR-0014 reference tiers, the partial-PR discipline — lives in the
`hooper-change-control` skill. Invoke it before opening a PR, closing an issue,
or writing an ADR.** The invariants below stay resident because they gate
irreversible actions:

- Issues labeled `afk` are the coding agent's to implement; `hitl` issues need proof,
  not code. **Never file or leave an issue carrying both labels** (ADR-0013) —
  split it into a build half and a verify half.
- **Done means proven, not written** — and *proven* means **proven by the headless
  harness** (ADR-0015/ADR-0016). Never close on code/compile alone. Irreducibly
  *feel* criteria stay open for the deferred human pass (ADR-0021, #173).
- `hitl` issues **decompose at pickup** (ADR-0024): state-checkable criteria split
  out into `afk` harness-scenario issues, visual/audio judgments become async
  artifact review, and only the irreducible-feel residue folds into #173.
- **Closing keyword placement.** Exactly one artifact closes an issue, and it
  carries `Closes #X` in its *body*, **never the subject line**. Single-commit fix
  → that commit's body. Multi-commit work → the **PR** body; the commits use
  `Refs #X` only.
- **Branch per issue**, named `<type>/<issue#>-<slug>` (e.g. `feat/5-host-join`).
  Default to small: a single focused commit goes straight to `main`. Keep commits
  single-concern with conventional subjects (`feat(net): ...`).
- **Merge, don't squash** — the per-step history is the documentation trail for a
  big change; squashing discards it.
- **No merge on red, ever.** ALL gates green before anything lands on `main`: game
  build, full `dotnet test`, the headless harness for harness-checkable issues,
  and `/code-review` with no unresolved correctness findings (ADR-0015). **Feel is
  never auto-accepted.**
- When finishing a unit of work, tell the human which issue(s) to close and which
  EDITOR_TASKS steps (if any) they must complete first.

### Starting AFK work (do this first, every time)

Before writing any code for an `afk` issue, decide which discipline fits and
**invoke it** — do not start coding unguided, and state which you chose and why in
your first response on the issue. This is a standing instruction for every agent on
this repo; the human should not have to ask for it each time.

- **`/tdd`** — the spec is clear and testable and the risk is *getting the
  behaviour right* (new logic, bug fixes, the deterministic ball, scoring/
  possession rules). Red-green-refactor pins the behaviour.
- **`/doubt-driven-development`** — the code is unfamiliar, the stakes are high
  (netcode, irreversible/authoritative state), or a wrong-but-confident answer
  would be costly to debug later.
- Not mutually exclusive: a well-specced *and* high-stakes task runs `/tdd` for the
  behaviour with doubt-driven review on the risky decisions inside it. **When
  genuinely unsure, default to `/doubt-driven-development`.**

### Delegate the dirty work to Sonnet subagents

**Standing instruction for every feature and every GitHub issue on this repo.**
The expensive model's job is *judgment*: choosing the approach, resolving the
non-obvious call, and deciding whether the evidence actually proves what it
claims. Everything downstream of a settled decision is **dirty work**, and dirty
work goes to a **Sonnet** subagent (`model: "sonnet"`) with high effort. The
human steers and verifies; the orchestrating model must not burn its own context
on volume it has already fully specified.

**Delegate** mechanical, high-volume, or long-running work whose shape is already
decided — new harness files from an existing template, the full local harness
sweep, mutation runs, bulk mechanical edits, evidence-gathering. *(The full
taxonomy and the briefing rules-of-engagement — brief them with the decisions not
the problem, name the traps explicitly, worktree isolation — are in the
`hooper-change-control` skill, §"Delegating to Sonnet subagents".)*

**Do NOT delegate** — anything where being wrong is cheap to write and expensive
to discover:

- the discipline choice above, or any ADR-0014 design call;
- deciding *what* an assertion should assert, or whether a green result is
  honest (a subagent will happily report a vacuous pass as a win);
- judging whether a threshold that went red is a real defect or a bad assertion
  — that distinction is the whole value of the doubt-driven pass;
- merges, issue closes, and anything touching a locked ADR.

**Verify the deliverable yourself.** Re-read the assertions it wrote and confirm
the mutation evidence is real. A returned "all green" is a claim, not proof —
treat it the way you would treat your own untested code.

**Commit before dispatching**, and pass `isolation: "worktree"` when fanning out
several agents at once or they collide in the main checkout.

### Decision Discipline

If during a session we make or change an architectural decision (engine,
networking model, input model, ball physics, community model — anything currently
recorded in `docs/adr/`), do not just act on it — **add a new ADR or update the
Status/Superseded-by fields of an existing one in `docs/adr/`**, with the
reasoning and the rejected alternative, in the same commit as the code. A brand-new
decision gets the next numbered file per `docs/adr/0000-template.md`.

If I ask you to do something that contradicts a locked ADR, stop and flag the
contradiction before writing code; don't silently comply.

### Handoffs (cross-session work)

When work spans more than one session — typically a big change like networking or
the ball physics — leave the next session a handoff at
`docs/handoffs/<topic>.md` (e.g. `docs/handoffs/M1b-networking.md`). That folder
is **gitignored** (handoffs are scratch in-flight state, not durable docs); only
its `README.md` is tracked. Put *only* what isn't already in AGENTS.md, the ADRs,
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
- `addons/godot_dotnet_mcp/` (see §3) is a third-party editor plugin whose
  author states its code is 100% AI-generated; it gets write access to scenes
  and scripts through the live editor. Its CI only caches Godot 4.6 mono while
  this project runs 4.7 — treat editor-mutating MCP tool calls the same as any
  other risky automated edit: don't run them with uncommitted work you'd hate
  to lose.

---

## Agent skills

Per-repo configuration for the engineering skills (`triage`, `to-issues`,
`to-prd`, `qa`, `diagnose`, `tdd`, `improve-codebase-architecture`, …).

### Issue tracker

GitHub Issues on `JoseTomanan/hooper-game`, via the `gh` CLI (the sole task
tracker). See `docs/agents/issue-tracker.md`.

### Triage labels

Five canonical roles; `ready-for-agent`/`ready-for-human` reuse this repo's
existing `afk`/`hitl` labels (ADR-0013). See `docs/agents/triage-labels.md`.

### Domain docs

Single-context (`CONTEXT.md` + `docs/adr/` at the repo root). See
`docs/agents/domain.md`.
