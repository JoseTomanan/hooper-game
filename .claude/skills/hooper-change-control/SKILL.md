---
name: hooper-change-control
description: How changes are classified, gated, and reviewed in the hooper-game repo — single-commit vs branch+PR, closing-keyword law, afk/hitl separation, merge gates (ADR-0015), Decision Discipline, ADR-0014 reference authority, and the discipline-skill choice before coding an afk issue. Load this before opening a PR, closing an issue, writing/amending an ADR, picking /tdd vs /doubt-driven-development, or any time you're unsure whether a change needs a human before it lands.
---

# Hooper change control

This is the rulebook for how a change gets from "someone decided to do it" to
"merged on `main` and the issue is closed" in `hooper-game`. Every rule below
is binding (CLAUDE.md: "IMPORTANT: These instructions OVERRIDE any default
behavior"). Where a rule exists because something already went wrong, the
incident is named — that is not decoration, it is the reason the rule cannot
be quietly relaxed.

Audience: a zero-context Sonnet-class agent picking up an `afk` issue, or a
junior engineer opening their first PR here. No prior knowledge of this
repo's history is assumed.

## 1. Classify the change: single-commit-to-main vs branch+PR

Default to the smallest container that fits:

| Shape | Container | Who/what closes the issue |
|---|---|---|
| One focused commit fixes it | Commit straight to `main` | That commit's **body** carries `Closes #X` |
| Naturally spans several commits (most M1b+ work) | Branch `<type>/<issue#>-<slug>` + PR | The **PR body** carries `Closes #X` |

- Branch name: `<type>/<issue#>-<slug>` — conventional type prefix (`feat`,
  `fix`, `docs`, `chore`), the issue number, a short slug. Example from this
  repo's history: `feat/98-block-committed-move`.
- One branch per issue, or per epic if its sub-issues are tightly coupled.
- **Keep commits single-concern on the branch.** Several focused commits is
  the goal, not one mega-commit. Subjects stay conventional
  (`feat(net): ...`). A commit body may add `Refs #X` — never a closing
  keyword (see §2).

### Merge, don't squash — and why

Every PR merges as a **merge commit**, never squashed. The reason is
documentary, not aesthetic: the per-commit rationale on a branch **is** the
design record for how a change was built. Squashing collapses a multi-step
investigation (e.g. steal's four-generation audit cascade, issues #96 →
#174–#182, where each commit's message carries a distinct root-cause
explanation) into one paragraph and throws away the trail a future agent
needs to understand *why* the code looks the way it does, not just what it
does today.

### Scene edits ship in their own single-concern commit with a headless load check

Per [ADR-0011](../../../docs/adr/0011-claude-authors-scenes.md), Claude
authors `.tscn`/`.tres`/`.res`/`project.godot` by direct text-edit as
ordinary AFK work. Scene files are fragile — `ext_resource`/`sub_resource`
IDs, `uid`, load-step counts all have to stay internally consistent, and a
mis-count silently corrupts the scene without a compile error to catch it.
So:

- A scene-touching change lands in its **own commit**, isolated from
  unrelated code changes, so a bad scene edit is a one-commit revert.
- Where a local or CI Godot binary is available, run a headless load check
  (boot the scene `--headless`, or at minimum let CI's
  `godot --headless --build-solutions --quit` step run) before treating the
  edit as done. A scene that fails to parse is a load-time failure, not a
  compile-time one — nothing else in the pipeline catches it.
- AnimationTree graph authoring (state-machine nodes/transitions,
  BlendSpace1D points) is now also AFK text-editing territory (ADR-0011,
  lifted 2026-06-28 per spike #87) — but it inherits the same fragility.
  `docs/spikes/0011-animationtree-text-authoring.md` documents the sharpest
  gotcha: `transitions` is a flat interleaved array
  (`[from, to, SubResource, from, to, SubResource, …]`) where an off-by-one
  in the sub_resource block count silently misparses the whole array.
- One structural exclusion remains genuinely HITL: editor **import-dialog**
  settings that are not scriptable headlessly. Everything else in
  `.tscn`/`.tres`/`.res`/`project.godot` is fair game for text-edit.

## 2. Closing-keyword law

**Exactly one artifact closes an issue, and it carries `Closes #X` in its
BODY — never a commit subject line.**

| Shape | Where `Closes #X` lives |
|---|---|
| Single-commit fix straight to `main` | That commit's **body** |
| Branch + PR | The **PR body** (commits on the branch use `Refs #X` only) |

Two failure modes this law exists to prevent:

1. **A closing keyword in a commit subject line** auto-closes on the first
   push, before review or CI has run — the issue reads "done" while the
   branch is still in flight.
2. **A closing keyword on a branch commit whose PR never merges** silently
   closes an issue with no code on `main` to back it up.

### The incident: PR #169 was auto-closed unmerged when its base branch was deleted

PR #169 carried three commits implementing block-as-committed-move (issue
#98), stacked on branch `feat/96-steal-committed-move`. When that base
branch merged (PR #168) and was deleted, GitHub auto-closed PR #169 as a
mechanical side effect of the stacked-PR base-branch deletion — not a
review rejection, zero review comments, just deleted-base collateral damage.
The work was real; the closure was noise. It was re-landed cleanly as
**PR #215**, which stated in its body: *"Supersedes closed PR #169"*, went
through two `/doubt-driven-development` review cycles, and shipped with the
ADR-0016 harness proof PR #169 lacked.

**The operational lesson:** when a PR is superseded (closed unmerged for any
mechanical reason — deleted base, stale branch, force-push mishap), the
**replacement PR's body** must both carry `Closes #X` and explicitly name
what it supersedes (`Refs #<old-PR>`), so the trail from "the original
attempt" to "what actually landed" survives. Do not assume a closed PR's
`Closes #X` still has any effect — GitHub only honors closing keywords on
the PR that actually merges.

### Partial-PR discipline

If a PR only resolves *part* of an issue (or fixes a symptom but leaves a
related gap open — e.g. a deliberately-not-fixed accepted trade-off), **keep
every close/fix/resolve keyword out of both the commit bodies and the PR
body.** Use `Refs #X` only. A partial fix that accidentally auto-closes its
issue is worse than one that stays open with an honest comment — reopening
an incorrectly-closed issue is more friction than never closing it
prematurely.

## 3. afk/hitl: single-purpose issues

Per [ADR-0013](../../../docs/adr/0013-afk-hitl-separate-issues.md), an issue
is *either*:

- an **`afk`** build issue — closes when the code merges, or
- a **`hitl`** verify issue — closes only when proven (now: proven by the
  headless harness for state-checkable criteria, or a human for irreducible
  feel — see §4).

**Never file or leave an issue carrying both labels.** If a unit of work has
a build half and a verification half, split it: the `afk` half builds and
merges; a separate `hitl` issue holds the dual-instance verify / feel
judgment.

### The incident: #83–#86 dual-label deadlock → #114

M9's crossover/hesitation issues (#83, #84, #85, #86) were each tagged with
**both** `afk` and `hitl`. Their AFK halves merged in PR #88 — the code was
done, releasable, on `main`. But because each issue also carried a `hitl`
verify gate, "Done means proven" (CLAUDE.md) held all four open, waiting on
a single editor session the owner could not run yet. That is the deadlock a
dual-labelled issue creates by construction:

- The build is finished and *should* read as closed.
- The verify gate says *not done yet*.
- One issue cannot honestly be both.

The owner resolved it by hand: closed #83–#86 on their merged code, and
consolidated all four dual-instance verifies into one new `hitl` issue,
**#114**. ADR-0013 records that resolution as the standing convention, not a
one-off:

- When closing a build issue whose verify moves elsewhere, **name the
  destination verify issue in the closing comment** (traceability from the
  build side).
- The consolidated verify issue **names its sources** (traceability from the
  verify side) — #114's body opens with *"This consolidates the
  dual-instance verifies formerly tracked on #83, #84, #85, #86."*
- **One `hitl` issue may consolidate several AFK features** proven in the
  same editor/harness session — this is preferred over one-verify-per-feature
  when the features are naturally checked together (#114 later also became
  the combined M9+M10 feel-pass gate, batching two milestones' feel
  judgments into one session per ADR-0015's "feel batched per milestone").

If you find a legacy issue still carrying both labels, that is a smell to
split immediately, not a pattern to imitate.

## 4. Merge gates (ADR-0015): what has to be green before anything lands on `main`

Per [ADR-0015](../../../docs/adr/0015-autonomous-merge-proven-by-harness.md),
an `afk` issue may go from dispatch to merged with no human in the critical
path — but **only** when ALL of these are green:

1. CI build of `HOOPER GAME.csproj` passes.
2. The full `dotnet test` suite passes (0 failed).
3. The headless integration harness ([ADR-0016](../../../docs/adr/0016-headless-verification-harness.md))
   passes for any issue whose acceptance criteria are harness-checkable.
4. `/code-review` returns no unresolved correctness findings.

**No merge on red, ever.** The orchestrator does not merge "with known
failures," and no agent reports an issue done while any gate above is red.

### "Done means proven" → "proven by the harness"

The bar (proof before close) is unchanged; the *prover* moved from a human
to the headless harness for anything the harness can assert. A `hitl` issue
whose acceptance criteria are expressible as harness assertions (state
checks: "the ball went loose," "the score incremented," "the remote client's
phase matches") closes when those assertions pass in CI, riding the PR's
`Closes #X`.

**Feel is never auto-accepted as feel.** The harness may assert the *state*
a feel value produces (e.g. "lean is non-zero only during the Active phase")
but must never claim a value *feels* right. That judgment is reserved for
one **human feel-acceptance pass per milestone** — issue #114 (the combined
M9+M10 offense/defense feel pass) is the live example: its M9 section is
concrete because that engineering already shipped, its M10 section is
explicitly a placeholder that "gets populated as M10's sub-issues land — do
not treat its checkboxes as verifiable until the corresponding code is
merged." Feel-tuned magnitudes (frame counts, turn-rate caps, scatter
radius) still ship as researched, cited starting defaults (see §6) with a
single HITL verify issue filed — never an up-front ask to the human before
coding.

### The Stop-hook green gate is a WEAKER local mirror — do not trust it alone

`.claude/hooks/verify-green.sh` runs on the `Stop` and `SubagentStop` hook
events. It is the *local* safety net, and it is deliberately weaker than the
real gate:

- It builds only `HOOPER GAME.csproj` and runs only
  `tests/Hooper.Ball.Tests` — not the full CI matrix (it does not run the
  headless integration harness at all).
- It **skips entirely (exits 0)** if `dotnet` cannot be resolved from `PATH`
  or the known Program Files location.
- After **3 consecutive red Stop attempts** (tracked via
  `.claude/.greengate-attempts`), it gives up and **allows the stop anyway**
  (exit 0, with a loud stderr warning) to avoid trapping an agent in an
  infinite loop — meaning an agent *can* technically finish and report "done"
  red after three tries.

Because of this, **independent confirmation via `gh pr checks` is
mandatory** before treating a PR as merge-ready — never rely on the local
hook's silence as proof the full gate is green. This is explicit in the
orchestrator's own operating doc, which calls the hook "a weaker mirror" and
requires it to "independently confirm `gh pr checks` green (do NOT trust
worker's self-report)."

## 5. Decision Discipline: ADRs travel with the code that implements them

If a session makes or changes an architectural decision — engine,
networking model, input model, ball physics, community model, or anything
already recorded in `docs/adr/` — **do not just act on it.** Add a new ADR,
or update the `Status`/`Superseded-by` fields and append a dated
`## Amendment` section to an existing one, **in the same commit as the
code.**

- **Locked ADRs are locked.** If a request contradicts one, **stop and flag
  the contradiction before writing code.** Do not silently comply. (This is
  the ADR-0014 §3 "ADR contradiction" escalation trigger too — see §6.)
- **New decisions get the next numbered ADR**, following the skeleton in
  `docs/adr/0000-template.md`: metadata header (`Status`/`Date`/
  `Superseded-by`), then `## Context`, `## Decision`, `## Consequences`. In
  practice every ADR in this repo also adds a numbered `## Alternatives
  considered` section, and this repo's actual convention for *evolving* a
  decision is a dated `## Amendment` appended in place — **not** a new ADR
  number and not flipping `Status` to Superseded. All 20 ADRs in this repo
  (0000 template + 0001–0019) are `Status: Accepted`; several carry multiple
  dated amendments (ADR-0003 has 6, ADR-0008 has 7, ADR-0010 has 3).

### The incident: #134 — a heading-rate retune shipped in code before the ADR caught up

[ADR-0010](../../../docs/adr/0010-authoritative-heading.md) originally
specified `MaxTurnRateDeg = 400°/s` in its Decision table. A later commit
retuned the shipped export to `530°/s` **directly in code**, without
touching the ADR or `HeadingMathTests` (which still hardcoded the stale
`400` figure and its explanatory math). For a period, the ADR, the test, and
the running game all disagreed with each other — silent drift, not caught by
any gate because nothing in CI checks an ADR's prose against a shipped
constant.

It surfaced as issue #134 and went to a **human design call**, not a
unilateral fix in either direction: the human's explicit ruling was
*"snappier is better"* — **ratify 530°/s**, and bring the ADR text and test
constant up to match the shipped value, rather than reverting the game back
to 400 to match the stale documentation. ADR-0010's dated 2026-06-30
amendment records exactly this: the decided value, the human's stated
reasoning, and the rejected alternative (reverting code to match the doc).

**The process gap this closes:** a code-only retune of an ADR-governed
numeric default is a Decision Discipline violation the moment it ships
without the matching ADR edit — even if the new value turns out to be the
one everyone wants. The fix is not "always keep the original value"; it is
"the ADR edit and the code change land together," full stop. If you find a
shipped constant that disagrees with its governing ADR, that is a live
instance of this exact incident — flag it, don't quietly pick a side.

## 6. ADR-0014: reference-game decision authority (cite-or-ask)

Most day-to-day design questions in this repo are not identity changes —
they are reference-grounded ("what does real half-court 1v1 ball do here?
how does Undisputed 3 commit this? what does 2K call it?").
[ADR-0014](../../../docs/adr/0014-reference-game-decision-authority.md)
lets an agent **self-resolve** those on the record, instead of routing every
one back to the human.

### The reference tiers (strict lexical precedence — higher always wins, no balancing)

1. **Locked design identity + ADRs.** Supreme. Nothing below overrides this
   tier. A request that contradicts a locked ADR is still Decision
   Discipline's stop-and-flag (§5) — ADR-0014 does not loosen that.
2. **Real half-court 1v1 basketball.** Physical truth and pickup-game rules
   (feet plant, a dribble cannot be recalled off the floor, make-it-take-it,
   winner's-outs, check-ball, clear-it). Full-court NBA/FIBA rules apply only
   by analogy where the half-court game is silent (e.g. what counts as a
   carry). [ADR-0008](../../../docs/adr/0008-possession-rules.md) remains the
   authority for the possession ruleset itself.
3. **UFC Undisputed 3.** Governs commitment and feel — how a move commits,
   its startup→active→recovery arc, and the legibility of the read. This is
   the target-feel axis [ADR-0003](../../../docs/adr/0003-input-model-hybrid.md)
   already names.
4. **NBA 2K.** Authoritative **only** for basketball taxonomy (what a
   crossover, hesi, size-up, pull-up, and-one, take-foul *is*) and
   control-surface familiarity (dribble moves live on the right stick).
   2K's feel and arcade tendencies (shot meters, magnetic-blended
   animations, ankle-breaker cinematics, free-cancels) carry **zero weight**
   — wherever 2K's feel conflicts with tiers 1–3, **2K loses by rule.** The
   "2K test": *2K can tell you what a mechanic is called and where the
   button lives. It can never tell you how the mechanic should feel, or
   whether it should exist.* Mechanic existence is always a tier-1 identity
   call.

The reference set is **closed** to exactly these three
({real half-court ball, Undisputed 3, 2K}) — invoking a fourth reference
(a fighting game's frame-data convention, streetball showmanship, etc.) is
an ADR amendment with reasoning, never an ad-hoc mid-task appeal.

### What must appear in the commit/PR body

For any **non-trivial** self-resolved call (one that would otherwise have
prompted a question to the human):

- **Cite or ask.** State which reference, which tier, and the specific real
  behaviour being modelled. If you cannot name the reference and tier, that
  is the signal to ask, not to guess.
- **No bare-preference appeals.** *"It's more fun," "it's smoother,"* or
  *"2K has it"* with no tier grounding is **not** a valid self-resolution —
  route it to the HITL tuning issue path below, or to the human.
- **Feel-tuned magnitudes** (frame counts, turn-rate caps, scatter
  magnitude) get a defensible, cited **starting value** shipped immediately,
  plus a single-purpose `hitl` verify issue filed (§3) — this is the
  ADR-0009 (shot scatter)/ADR-0010 (turn-rate) pattern. No up-front ask.
- Trivial calls need no citation.

### When to escalate to the human instead of self-resolving

Stop and ask (`AskUserQuestion`/`/grill-me`), or run
`/doubt-driven-development`, only when:

1. **Design-identity / spine / anti-goal change** — anything that would
   relitigate *what the game is* (mechanic *existence within* the identity
   is self-resolved; changing the identity itself is not).
2. **ADR contradiction** — the same hard stop as Decision Discipline (§5).
3. **Genuine reference deadlock** — the precedence order does not cleanly
   resolve it: the references are silent, or all point at an arcade answer
   tier 1 forbids with no honest alternative in view.
4. **High-stakes irreversible / authoritative decisions** — netcode,
   authority, or state where a confident-but-wrong answer is costly to
   unwind. Default to `/doubt-driven-development`; ask if doubt survives.

When genuinely unsure which side of that line a call sits on, **treat it as
escalation, not self-resolution.**

## 7. Pick a discipline skill before writing any code on an afk issue

This is a standing instruction, not a per-issue judgment call — invoke one
of these *before* the first line of implementation code, every time:

- **`/tdd`** — the task has a clear, testable spec and the risk is *getting
  the behaviour right* (new logic, bug fixes, the deterministic ball,
  scoring/possession rules). Red-green-refactor pins the behaviour.
- **`/doubt-driven-development`** — the task touches unfamiliar code, the
  stakes are high (netcode, irreversible/authoritative state), or a
  wrong-but-confident answer would be expensive to debug later. It subjects
  each non-trivial decision to a fresh-context adversarial review.

They are not mutually exclusive: a task that is both well-specced *and*
high-stakes can run `/tdd` for the behaviour while leaning on doubt-driven
review for the risky decisions inside it (PR #215's block-as-committed-move
rework went through two `/doubt-driven-development` review cycles on top of
its test coverage). **When genuinely unsure, default to
`/doubt-driven-development`.**

State which one you chose, and why, in your **first response on the issue**
— before any code — so the choice itself is part of the reviewable record.

## 8. Milestone activation (ADR-0017): DEFERRED → Active has one legal order

Per [ADR-0017](../../../docs/adr/0017-autopilot-activates-deferred-milestones.md),
the autopilot may flip a milestone from `DEFERRED` to `Active` in CLAUDE.md
§2 **without** a per-milestone human "go" — but only under two hard
constraints:

1. **Strictly walk the dependency order already documented in CLAUDE.md §2's
   milestone table.** No skipping ahead, no inventing a different graph.
2. **Only after the predecessor milestone's epic is genuinely closed** — CI
   green + harness green + `/code-review` clean + its one per-milestone
   human feel pass (§4) — under ADR-0015.

Activation flips `DEFERRED` → `Active` in the CLAUDE.md §2 table and gates
**pickup**, not **merge**: it decides which milestone's issues the
autopilot is now allowed to start grabbing, not whether any individual PR
is safe to land (that is still §4's merge gates, unconditionally, for every
PR regardless of milestone). Outside this autopilot walk, the standing rule
holds: do not build ahead of the current milestone unless asked.

## 9. Do-not-do list

- **Never close an issue on code/compile alone.** "Done means proven" is
  unconditional; the only thing ADR-0015 changed is *who* proves it (§4).
- **Never reopen #119.** It was manually closed by the human on 2026-06-29
  (recorded in `docs/handoffs/dual-instance-harness.md`); it is a deliberate
  human call, not an oversight — do not second-guess it by reopening.
- **Never self-resolve a feel call.** Feel-tuned magnitudes get a cited
  starting value plus a `hitl` issue (§6) — the value is not "approved"
  until the per-milestone human feel pass says so, regardless of how
  confident the citation is.
- **Never self-resolve an ADR-changing call.** Any change to a locked ADR's
  Decision (not an in-place dated Amendment that extends it) is, by
  definition, the "ADR contradiction" escalation trigger (§6.3) — stop and
  flag, don't quietly amend around it.
- **Never bypass the closed reference set** (§6) by inventing a fourth
  reference mid-task ("what does game X do") — that requires an ADR
  amendment first.
- **Never carry both `afk` and `hitl` on one issue** (§3) — split it the
  moment you notice, don't defer the split.
- **Never trust the local Stop-hook green gate as sufficient proof** (§4) —
  it is a weaker mirror by design; confirm `gh pr checks` independently.
- **Never put a closing keyword in a commit subject line, or on a
  branch-commit that isn't the merging PR body** (§2) — that is exactly how
  PR #169 pattern-adjacent mistakes happen (that one was a mechanical
  deleted-base accident, not a misplaced keyword, but a misplaced keyword
  produces the same class of harm: an issue reads closed with nothing
  provably on `main`).

## When NOT to use this

- **Templates for ADRs, handoffs, or other repo documents** — use
  `hooper-docs-and-writing` for the house style and skeletons; this skill
  only tells you *when* an ADR/PR/commit is required and what must be in it,
  not how to format the document itself.
- **What counts as evidence, or how to run the harness / add a test** — use
  `hooper-verification-and-qa`; this skill only states the gate (§4), not
  the mechanics of clearing it.
- **Picking the next issue to work, decomposing an epic, or running the
  orchestrator/issue-worker loop itself** — that operational detail (not the
  rules governing it) lives with the `orchestrator` and `issue-worker`
  agent definitions in `.claude/agents/`; this skill is the rulebook they
  must obey, not the runbook for driving them.
- **Frame data, possession-rule specifics, or other game-design content**
  — use `hooper-duel-design-reference`; this skill covers the *process* of
  landing a change, not the domain content of what the change should be.

## Provenance and maintenance

Verified against the live repo and `gh` on **2026-07-12**: all ADR numbers,
statuses, and amendment dates (ADR-0000–0019, 20 files, all `Status:
Accepted`, no gaps); issue numbers and states for #83, #86, #98, #114, #119,
#134, #168, #169, #215; PR #215's body text ("Supersedes closed PR #169");
`.claude/hooks/verify-green.sh` behavior (build scope, skip-on-missing-dotnet,
3-attempt bail); `.claude/agents/orchestrator.md`'s "weaker mirror" /
independent `gh pr checks` language.

Re-verify if any of these drift:

- `gh issue view 114 --json state,body` — confirm #114 is still the live
  M9+M10 feel-pass gate and whether its M10 section has moved past
  placeholder.
- `git log --oneline docs/adr/ | wc -l` and `ls docs/adr/*.md | wc -l` —
  confirm the ADR count/gap-free numbering hasn't changed.
- `gh pr view 169 --json state,closedAt,mergedAt` and
  `gh pr view 215 --json state,mergedAt` — confirm the supersession record
  still reads as stated here.
- `cat .claude/hooks/verify-green.sh` — confirm the local green-gate's scope
  and 3-attempt bail behavior haven't been changed or hardened.
- `grep -n "MaxTurnRateDeg" docs/adr/0010-authoritative-heading.md scripts/Player/*.cs` —
  confirm ADR-0010's documented default still matches the shipped export
  (the #134 incident is exactly this check failing silently once).
