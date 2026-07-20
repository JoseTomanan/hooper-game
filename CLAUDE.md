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

> **M8b / M10 — the active set (as of 2026-07-20; M9 epic #75 closed
> 2026-07-20 — build complete, feel deferred to #173).**
>
> **M8b — Realism & polish, continued** (epic **#171**): M8's leftover
> verify/feel work — #153 net/fence visuals (human feel pass FAILED 2026-06-30;
> awaiting an AFK material fix before re-verify), #170 realistic player rig
> (blocked on a human asset-license pick, sourcing bounded by ADR-0020) and its
> verify #178.
>
> **M9 — Basketball-related controls, offense** (epic **#75** — CLOSED
> 2026-07-20): the full dribble-move family landed — crossover/hesi (PR #88),
> moving crossover (#198), behind-the-back (#194), ball-hand sweep (#195),
> step-back/retreat dribble (#197), between-the-legs (#199), jab step (#200),
> spin (#201), in-and-out (#202) — plus crossover netcode hardening (#209/#210),
> the StepBack cradle-race fix (#253), and the rim-finishing vertical (**#203**
> umbrella, ADR-0022): layup/rim-finish (#229), drive-gather (#230), euro-step
> (#231), taxonomy leaf (#232), layup-range fallback (#236). The anim-clip build
> halves also landed — planted-pivot clip (#242), fadeaway clip (#243). #241
> (external-event abort) was closed as designed-behavior: the human #189 ruling
> (ADR-0003 amendment, 2026-07-20) rejected the carve-out — a committed move runs
> to completion even when a steal/OOB voids its payload; the lost time IS the
> punishment. Still open but NOT gating the close (feel/visual, ADR-0021): the
> `hitl` verify halves #184 (pivot clip) and #185 (fadeaway indicator),
> deferred to the consolidated feel pass #173.
>
> **M10 — Defense & the reactive read** (epic **#89**): the core shipped —
> foundation ADR-0018 (#95), steal (#96), block (#98, + reach gate #214), input
> map (#101), on-ball contest (#99, PR #221), blow-by punish (#100), telegraph
> remote sync (#102), held-ball steal window (#206, PR #259 — Option A pump-fake
> window, ADR-0018 Amendment 2026-07-19; the dead-Held staller half was carved
> off to #255), spatial/transit steal window (#196, PR #260 — a third steal
> shape unioned into the live-dribble check: while a #195 ball-hand sweep is
> active a defender within `StealReachRadius` of the swept ball position
> connects with the hand-side axis dropped, ADR-0018 Amendment 2026-07-20).
> Remaining: #261 (transit-steal harness coverage for the non-crossover sweep
> paths, a #196 follow-up — **in flight 2026-07-20**, PR open) and #255
> (deferred dead-Held staller — no travel/5-second pressure yet; its 5-second/
> travel-violation shape arguably belongs to M12 flow, to be dispositioned when
> M10 is next assessed for close). Tuning is no longer
> per-milestone: #104 closed into the consolidated tuning pass **#238**, because
> the magnitudes interact (see that issue). Feel for M9+M10 is deferred to the
> consolidated human feel pass **#173** (#114 is the M9+M10 checklist folded
> into it), per ADR-0021 — it no longer gates milestone activation.

### Milestone status

| Milestone | Status | Epic |
|-----------|--------|------|
| M1a — Local movement | Done | #1 |
| M1b — Networking on top | Done | #4 |
| M2 — Local ball mini-physics | Done | #8 |
| M3 — Hybrid input: committed moves | Done | #13 |
| M4 — Networked ball + committed moves | Done | #19 |
| M5 — Win condition + scoring | Done | #23 |
| M6a — Dedicated server + server browser | Code done; editor verify (#32) trails M6b | #28 |
| M6b — Possession loop | Done (epic closed 2026-06-24; feel deferred to #173 per ADR-0021) | #46 |
| M7a — Static readability pass | Done | #53 |
| M7b — Rigged humanoid animation | Done (epic closed 2026-06-26) | #54 |
| M8 — Realism & polish pass | Done (epic closed; leftover verify/feel/realism work continues under M8b) | #61 |
| **M8b — Realism & polish pass, continued** | **Active** (umbrella; M8 leftovers — #153 net/fence verify, #170 realistic player rig (sourcing bounded by ADR-0020) + its verify #178) | #171 |
| M9 — Basketball-related controls (offense) | Done (epic closed 2026-07-20; full dribble-move family #88/#194/#195/#197/#198/#199/#200/#201/#202, netcode #209/#210, cradle-race #253, rim-finishing #203 (#229/#230/#231/#232/#236), anim-clip builds #242/#243; #241 closed as designed-behavior per #189 ruling; feel-verifies #184/#185 deferred to #173 per ADR-0021, don't gate) | #75 |
| **M10 — Defense & the reactive read** | **Active** (umbrella; core shipped — ADR-0018 #95, steal #96, block #98/#214, contest #99, blow-by #100, telegraph #102, held-ball steal #206 (PR #259, Option A), transit/spatial steal #196 (PR #260, ADR-0018 Amendment 2026-07-20); open: non-crossover transit-steal coverage #261 (in flight), deferred staller #255; tuning #104 closed into #238; feel deferred to #173 per ADR-0021, #114 folded in) | #89 |
| M11 — Stamina & resource economy | DEFERRED (planning epic) | #90 |
| M12 — Match flow, HUD & session lifecycle | DEFERRED (planning epic) | #91 |
| M13 — Audio & game feel | DEFERRED (planning epic) | #92 |
| M14 — Training, onboarding & practice opponent | Closed — `wontfix` (2026-07-04) | #93 |
| M15 — Mobile, performance & release readiness | Closed — `wontfix` (2026-07-04) | #94 |

GitHub Issues is the source of truth for the live state of each milestone and its
sub-issues; this table is the at-a-glance map.

**M11–M13 are a forward roadmap, not a work queue.** They are deferred planning
epics that record *what comes next and why*, in dependency order: M9–M10 complete
the core duel (offense then defense), M11 adds the stamina pillar on top, M12–M13
turn the loop into a game (flow + feel). Their rows are listed here for the
at-a-glance map; each stays DEFERRED until explicitly activated, at which point its
"DEFERRED" status flips to "Active" and (for the umbrella epics) it stops merely
accruing sub-issues. **M14 and M15 were closed `wontfix` on 2026-07-04** — they
are no longer on the roadmap; their rows remain only so the numbering stays
legible. M10 was activated by human design call (2026-06-30) ahead of the
combined M9+M10 feel pass (#114, folded into the consolidated #173 pass), which
per [ADR-0021](docs/adr/0021-feel-taste-deferred-indefinitely.md) is deferred
indefinitely rather than gating M10. M9's epic (#75) closed 2026-07-20 (build
complete; feel-verifies #184/#185 deferred to #173, don't gate). M10 remains an
active umbrella that still accrues sub-issues.

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
- **`scripts/`** — all C# code, the part Claude Code owns. Subfolders by
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
  esp. the fragile flat `transitions` array). One structural exclusion remains HITL:
  editor **import-dialog** settings not already scriptable headlessly. Scene edits
  are fragile (`ext_resource`/
  `sub_resource` IDs, `uid`, load-step counts) — so they ship in their own
  single-concern commit with a headless load check where a Godot binary is available.
- **`assets/`** — models, textures, sounds. Placeholder/gray is fine for now.
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
- One script = one node responsibility. `partial` class extending the node type.
- Comment the "why," not the "what," especially around netcode and the
  deterministic ball, because the human is learning the engine.
- When you finish a unit of work, tell the human exactly which EDITOR steps (if
  any) they must do to see it run — you cannot do them.
- Prefer one clear path; explain the tradeoff in a sentence and proceed.

### Issue tracker

GitHub Issues is the sole task tracker. TASKS.md no longer exists.

- Issues labeled `afk` are Claude Code's to implement.
- Issues labeled `hitl` require an editor-level verification step (see EDITOR_TASKS.md) before they can close. Under autopilot ([ADR-0016](docs/adr/0016-headless-verification-harness.md)) that verification is performed by the **headless harness** wherever the acceptance criterion is state-checkable; only irreducibly *feel* criteria still need a human (deferred to the consolidated human-scheduled pass, [ADR-0021](docs/adr/0021-feel-taste-deferred-indefinitely.md)).
- **`hitl` issues decompose at pickup ([ADR-0024](docs/adr/0024-hitl-async-evidence-restructure.md), once accepted).** State-checkable criteria split out into `afk` harness-scenario issues (close on merge); visual/audio judgments convert to **async artifact review** (CI-captured evidence, human judges on their own schedule — no live editor session); bounded decision gates that block AFK work proceed on a **default-with-veto** decision brief. Only the irreducible-feel residue folds into #173.
- **AFK build and HITL verify are separate issues** ([ADR-0013](docs/adr/0013-afk-hitl-separate-issues.md)). An issue is single-purpose: either an `afk` build issue (closes on merge) or a `hitl` verify issue (closes only when proven in-engine) — **do not file or leave an issue carrying both labels.** If work has a build half and a verification half, split it; the `afk` issue merges and a separate `hitl` issue holds the dual-instance verify. When a legacy dual-labelled `afk` issue's code merges, close it and move the verify into a `hitl` issue (name the destination in the closing comment, name the sources in the verify issue — the #83–#86 → #114 pattern). One `hitl` issue may consolidate several AFK features proven in the same editor session.
- **Done means proven, not written** — *proven* now means **proven by the harness** ([ADR-0015](docs/adr/0015-autonomous-merge-proven-by-harness.md)/[ADR-0016](docs/adr/0016-headless-verification-harness.md)). A `hitl` issue whose acceptance criteria are state-checkable is closed when the headless harness asserts them green in CI; a criterion that is irreducibly *feel* stays open until the deferred, human-scheduled consolidated pass ([ADR-0021](docs/adr/0021-feel-taste-deferred-indefinitely.md)). The bar (proof before close) is unchanged — the prover moved from a human to the harness. Never close on code/compile alone.
- When finishing a unit of work, tell the human which issue(s) to close and which EDITOR_TASKS steps (if any) they must complete first.
- **Closing keyword placement.** Exactly one artifact closes an issue, and it carries `Closes #X` in its *description/body* (never the subject line), so GitHub auto-closes the issue and the close is traceable:
  - **Single-commit fix → straight to `main`:** that commit's body carries `Closes #X` (the M1a pattern).
  - **Multi-commit work → branch + PR:** the *PR body* carries `Closes #X`; the commits do not (see Branching below).

### Starting AFK work (do this first, every time)

Before writing any code for an `afk` issue, decide which discipline fits and
**invoke it** — do not start coding unguided. This is a standing instruction for
every agent on this repo; the human should not have to ask for it each time.

- Investigate the task, then pick one:
  - **`/tdd`** — when the task has a clear, testable spec and the risk is *getting
    the behaviour right* (new logic, bug fixes, the deterministic ball, scoring/
    possession rules). Red-green-refactor pins the behaviour.
  - **`/doubt-driven-development`** — when the task is in unfamiliar code, the
    stakes are high (netcode, irreversible/authoritative state), or a wrong-but-
    confident answer would be costly to debug later. It subjects each non-trivial
    decision to a fresh-context adversarial review.
- The two are not mutually exclusive — if a task is both well-specced *and* high-
  stakes, run `/tdd` for the behaviour and lean on doubt-driven review for the
  risky decisions within it. When genuinely unsure, default to
  `/doubt-driven-development`.
- State which one you chose and why in your first response on the issue, then
  invoke it.

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
- **`hitl` still means proven.** A PR may merge the *code*, but the issue closes
  only on proof. Under autopilot the **harness** supplies that proof for
  state-checkable criteria ([ADR-0016](docs/adr/0016-headless-verification-harness.md)):
  a `hitl` issue's `Closes #X` may ride the PR when its acceptance is asserted by
  a green integration test in CI. Where proof is irreducibly *feel*, omit `Closes`
  and leave it for the deferred, human-scheduled consolidated pass
  ([ADR-0021](docs/adr/0021-feel-taste-deferred-indefinitely.md)) to close.
  `Done means proven` (now proven-by-harness) still overrides bare auto-close.
- **Merges are autonomous on green ([ADR-0015](docs/adr/0015-autonomous-merge-proven-by-harness.md)).**
  The orchestrator opens the PR with `gh` and merges it (merge-commit, not squash)
  once ALL gates are green: game build, full `dotnet test`, the headless harness
  (ADR-0016) for harness-checkable issues, and `/code-review` with no unresolved
  correctness findings. **No merge on red, ever.** Feel is never auto-accepted as
  feel — it is the deferred, human-scheduled consolidated pass
  ([ADR-0021](docs/adr/0021-feel-taste-deferred-indefinitely.md)). (Pre-autopilot
  default, still valid when not running the autopilot: the human owns the merge.)

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
