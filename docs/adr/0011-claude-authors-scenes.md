# ADR-0011 — Claude authors scenes/config by text-edit; human owns feel + verification

- **Status:** Accepted
- **Date:** 2026-06-28
- **Superseded-by:** —

---

## Context

From M1 onward the repo has split work along a fixed line: Claude Code writes the
C# under `scripts/`, and the **human** does everything inside the Godot editor.
CLAUDE.md §3 states it plainly —

> "`scenes/` — `.tscn` scene files. Authored in the Godot editor by the human
> (see EDITOR_TASKS.md). Claude Code writes the C# a scene's nodes reference, but
> the human wires nodes in the editor."

— and the whole of EDITOR_TASKS.md is written as "the part AI can't do."

That line was drawn early, when the human was learning the engine and the safest
division was "AI touches code, human touches editor." It has since hardened into
a bottleneck: every milestone ends with a long manual editor checklist (add this
node, attach this script, drag this export into this Inspector field, save the
scene) that gates `hitl` issues from closing. The human has asked to move the
line: **maximise AFK work — including editor tasks currently done by hand — and
narrow HITL to purely checking the *feel* of things and *verifying* behaviour.**

### Forces at play

1. **Most "editor wiring" is text.** `.tscn`, `.tres`/`.res`, and `project.godot`
   are plain text resource files. Adding a node, setting a property, assigning an
   export/`NodePath`, instancing a sub-scene, and adding an Input Map action are
   all line edits to those files — not operations that require a human clicking.

2. **Precedent already exists in this repo.** M3's Input Map actions were *"added
   by text-edit to `project.godot` in the PR"* (EDITOR_TASKS.md, M3 Step 2), and
   the M7b locomotion clips (`assets/locomotion.res` + `idle/run.res`) were
   *extracted headlessly* with Godot 4.6 `ResourceSaver`, not through the import
   dialog. The boundary has already been crossed ad hoc; this ADR makes it a
   policy instead of an exception.

3. **`.tscn` text is fragile.** Scene files encode `ext_resource`/`sub_resource`
   IDs, a resource `uid`, and node/property ordering. A hand-authored edit that
   gets an ID, a `uid`, or load-step count wrong yields a scene that fails to
   load in the editor — a failure mode C# edits don't have. This is the main risk
   the decision must contain, not wish away.

4. **Some editor work is genuinely not text-authorable (or not safely so).**
   Three categories resist hand-editing:
   - **Feel/tuning judgments** — lean degrees, shadow bias, burst speed,
     blend-space ranges, "does the telegraph read." Claude can change the
     *numbers*; only a human watching the game can decide they *feel* right.
     This is the spacing/commitment design identity (ADR-0003), which is
     subjective by definition.
   - **AnimationTree graph authoring** — BlendSpace1D points and state-machine
     nodes/transitions are built through the editor's visual graph tools. They do
     serialize into `.tscn`, but authoring them blind is unproven here.
   - **Import-dialog settings** — FBX→animation extraction, "Set Animation Save
     Paths." Partly scriptable (see force 2), partly dialog-driven.

5. **Verification is the human's value, and `Done means proven` is unchanged.**
   The project rule that a `hitl` issue closes only after in-editor confirmation
   (CLAUDE.md §3) is a *correctness* guarantee, not an authoring step. Narrowing
   HITL to verification keeps that guarantee fully intact while removing the
   authoring toil.

### Alternatives considered

1. **Status quo — human authors all scenes.**
   Rejected. It is the bottleneck this ADR exists to remove, and forces 1–2 show
   most of it never needed a human in the first place.

2. **Full automation, including AnimationTree graphs and import dialogs.**
   Rejected *for now*. AnimationTree text-authoring (force 4) is unproven and the
   densest editor-only surface (M7b Steps 3–4); committing to it blind risks
   silent animation breakage that the human's verify pass might not catch as
   "wrong" vs "unwired." Deferred to a spike (see Consequences) rather than
   adopted.

3. **Claude generates a full scene from scratch each time, human reviews the
   whole file.**
   Rejected as the default. Whole-file generation maximises the ID/`uid`/ordering
   failure surface (force 3). Small, targeted, single-concern edits to existing
   scenes are safer and reviewable, and match the repo's existing commit-clean
   discipline.

4. **Keep the line where it is but have Claude write richer EDITOR_TASKS steps.**
   Rejected. Better instructions still leave the human doing mechanical wiring;
   it optimises the bottleneck instead of removing it.

## Decision

**Claude Code may author and modify `.tscn`, `.tres`/`.res`, and `project.godot`
by direct text-edit as ordinary AFK work.** The human's required role narrows to
two things only:

- **(a) Feel / tuning judgments** — deciding whether tuned values *feel* right
  (ADR-0003 territory). Claude proposes the numbers; the human accepts or
  redirects.
- **(b) In-engine verification** — running the game (single- or dual-instance)
  to confirm behaviour, which is what closes a `hitl` issue (`Done means proven`,
  unchanged).

Everything structural — adding/renaming nodes, setting properties, assigning
exports and `NodePath`s, instancing sub-scenes, Input Map entries, and wiring
exports onto nodes that already exist — moves to **AFK**.

**Boundary that stays HITL (this ADR's deliberate exclusions):**
- ~~AnimationTree **graph authoring** (BlendSpace points, state-machine nodes/
  transitions) — HITL until the spike below proves text-authoring reliable.~~
  **Lifted 2026-06-28** — the spike (#87) proved a hand-authored `AnimationTree`
  (StateMachine + BlendSpace1D) loads clean and runs identically to an
  editor-authored one (in-engine confirmed). AnimationTree **graph authoring is
  now AFK**, under the standing guardrails below plus the authoring gotchas in
  `docs/spikes/0011-animationtree-text-authoring.md`. See Consequences.
- Editor **import-dialog** settings not already scriptable headlessly.
- All **feel/verification** runs.

**Guardrails (mandatory when Claude edits a scene/config file):**
1. **Isolate scene edits in their own single-concern commit** (commit-clean
   discipline), never mixed with unrelated C#, so a broken scene is trivially
   revertible and bisectable.
2. **Sanity-check the edit headlessly where a Godot binary is available** —
   e.g. `godot --headless --editor --quit` (or a script import pass) to confirm
   the project + edited scene still load with no resource errors — before handing
   it to the human's verify pass.
3. **Treat the human verify pass as the safety net, not the author.** If a
   text-authored scene fails to load or mis-wires, that is a Claude defect to
   fix, not editor work pushed back to the human.

## Consequences

**Easier:**
- AFK throughput rises sharply — milestones no longer end with a long manual
  wiring checklist gating issue closure. The human's involvement compresses to
  the irreducible "does it feel right / does it work" pass.
- The `afk`/`hitl` labels regain precision: `hitl` now means "needs a human to
  *judge feel or verify*," not "needs a human to *author*." When writing or
  re-triaging issues, scope the `hitl` portion down to verification-only and move
  the wiring into the AFK PR (this directly re-scopes the M9 issues #83–#86, and
  any remaining M6b/M7b editor steps).

**Harder / accepted tradeoffs:**
- **Scene-file fragility is now Claude's burden** (force 3). The guardrails
  (isolated commit + headless load check) exist to contain it; they are not
  optional. New failure modes — unresolved `ext_resource`, duplicate/again-used
  IDs, wrong `uid`, stale load-step counts — must be checked for, not assumed
  absent.
- ~~**AnimationTree authoring stays a manual gap** until a dedicated spike proves a
  text-authored `AnimationTree` (state machine + BlendSpace1D) loads and runs
  identically to an editor-authored one. Until then M7b Steps 3–4 remain HITL.
  This spike is the natural next follow-up to this ADR.~~ **Resolved 2026-06-28
  (spike #87, PASS).** A hand-authored `AnimationNodeStateMachine` + BlendSpace1D
  matched the editor baseline on all 31 structural/behavioral checks (clean
  headless load, identical states/node-types/blend-points/transitions) and was
  confirmed running identically in-editor by the human. AnimationTree graph
  authoring moves to AFK. The text-authoring failure modes the spike surfaced —
  the flat interleaved `transitions` array (off-by-one in sub_resource block count
  misparses silently), default `advance_mode = 1` on empty transition blocks,
  32-bit float round-off in `min_space`/`max_space`, virtual `Start`/`End` states
  — are recorded in `docs/spikes/0011-animationtree-text-authoring.md` as the
  authoring checklist. M7b Steps 3–4 (#41, #69) are no longer HITL-gated on this.
- **Documentation must be updated on acceptance, in the accepting commit**
  (Decision Discipline): rewrite CLAUDE.md §3's "scenes are authored by the
  human" paragraph to reflect the new boundary, add this ADR (0011) to CLAUDE.md's
  ADR table, and revise the EDITOR_TASKS.md preamble ("the part AI can't do") to
  describe its new, narrower scope — feel + verification, plus the explicit HITL
  exclusions above. EDITOR_TASKS.md does not disappear; it becomes the
  human's *verification* checklist rather than an *authoring* checklist.
- **`Done means proven` is unchanged.** Nothing here lets a `hitl` issue close on
  code/scene-text alone; the human verify pass remains the gate. This ADR moves
  *authoring*, not *proof*.
- **Reversible.** If text-authored scenes prove too fragile in practice, the
  boundary can move back toward human authoring without unwinding any C#; the
  isolated-commit guardrail makes any individual bad scene edit a clean revert.
