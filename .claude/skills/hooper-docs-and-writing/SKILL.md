---
name: hooper-docs-and-writing
description: Maintaining hooper-game's docs of record — ADR/spike/analysis/handoff templates, house style for commit messages and doubt-cycle comments, and the staleness registry for known-stale facts in CONTEXT.md and docs/agents/. Load this before writing or amending an ADR, writing an analysis or spike doc, writing a handoff, drafting a commit-message body, prefixing a doubt-cycle comment, or any time you're about to cite a "fact" from CONTEXT.md/docs/agents/*.md that might have drifted (ADR count, test count, milestone table). Do NOT load for gating/merge-process questions (see hooper-change-control) or outward-facing writing (see hooper-external-positioning).
---

# hooper-docs-and-writing

This repo's docs are not decoration — they are load-bearing for a project
driven mostly by AI agents across sessions with no continuous human memory.
This skill covers the doc types that record decisions and data, plus the
commit-message and code-comment conventions that carry decisions inline. It
does NOT cover gating (who may merge what, afk/hitl labels, the ADR-0013/14/
15/16 process) — that's `hooper-change-control`. It does not cover writing
aimed at people outside the repo — that's `hooper-external-positioning`.

Paths in this file are repo-relative to
`C:/Users/The King/Documents/GitHub/hooper-game` (note the space — always
quote the path in commands).

## 1. The doc taxonomy — where does a given fact belong?

| Doc type | Location | Purpose | Lifecycle |
|---|---|---|---|
| `CLAUDE.md` | repo root | PRD + ADR index + repo conventions + **§2 milestone table** | Durable; §2 table is live-edited by the autopilot orchestrator on milestone activation (ADR-0017) |
| `CONTEXT.md` | repo root | Domain glossary: term → definition → ADR pointer. **No reasoning lives here** | Durable, but see the Staleness Registry (§7) — it lags the ADR ledger |
| `docs/adr/` | repo root | Locked architectural decisions, one per number | Durable; `Status: Accepted` and effectively permanent (see §2 for amend-vs-new-number) |
| `docs/spikes/` | repo root | Time-boxed experiments with a **verdict** (PASS/FAIL/GO/NO-GO) | Durable once the verdict lands; numbered like ADRs (one exists as of 2026-07-12: `0011-animationtree-text-authoring.md`, verdict PASS) |
| `docs/analysis/` | repo root | **Measured data**, explicitly non-prescriptive | Durable; see §3 for the required shape |
| `docs/handoffs/` | repo root | Cross-session scratch state for in-flight work | **Gitignored except README.md** — see §4 |
| `EDITOR_TASKS.md` | repo root | Steps only a human can do in the Godot editor | Durable, living list; agents append when finishing work that needs in-editor verification |
| `docs/agents/` | repo root | Config docs the engineering skills themselves consume (issue-tracker conventions, triage labels, domain-doc rules) | Durable but partially stale — see §7 |

Rule of thumb for "which doc": **why** an architecture choice was made → ADR.
**What was measured** → analysis doc. **What was tried and whether it worked**
→ spike. **Where a mid-flight task was interrupted** → handoff (scratch, not
durable — nothing that belongs in the other layers goes here). **What a human
must click in the editor** → EDITOR_TASKS.md.

### CLAUDE.md §2 — the milestone table

The table is the single at-a-glance map of milestone status
(`DEFERRED`/`Active`/`Done`) and is edited in exactly two situations:

1. **Autopilot activation** (ADR-0017): the orchestrator may flip a row
   `DEFERRED → Active` itself, without a per-milestone human "go" — but only
   by walking the table's own listed dependency order, and only after the
   predecessor milestone's epic is genuinely closed (CI + harness +
   code-review + its one human feel pass, per ADR-0015). Activation gates
   *pickup*, not *merge*.
2. **Milestone closure**: a row moves to `Done` only after the human's
   per-milestone feel pass (ADR-0015 — feel is never auto-accepted).

If you are not the orchestrator and you are not recording a human-confirmed
milestone close, do not touch §2.

### CONTEXT.md — use its terms exactly

Per `docs/agents/domain.md` rule 2: when any output names a domain concept —
issue title, test name, code comment, doc — use the term as CONTEXT.md defines
it. "Committed move," "spacing spine," "feint-cancel window," "deterministic
mini-physics ball" are load-bearing names, not casual phrasing. If a concept
you need is missing from the glossary, that's a signal: either you're
inventing language the project doesn't use (reconsider), or there's a real gap
(note it — don't silently coin a term). But remember CONTEXT.md is
**incomplete past the M6b era** (§7) — a missing term for an M9/M10 concept
may just be lag, so check the relevant ADR before concluding the concept is
unnamed.

## 2. ADR house style

### The template (verbatim skeleton of `docs/adr/0000-template.md`)

```
# ADR-XXXX — Title

- **Status:** Proposed | Accepted | Deprecated | Superseded
- **Date:** YYYY-MM-DD
- **Superseded-by:** (ADR number + link, if applicable)

---

## Context
## Decision
## Consequences
```

### The two de-facto extensions (not in the template file, but the live convention)

1. **A rejected-alternatives section.** Verified 2026-07-12: ADRs 0010–0017
   and 0019 carry a dedicated `## Alternatives considered` heading (numbered
   or bolded list of rejected options, each with a one-line reason); ADRs
   0008, 0009, and 0018 fold the same content into `## Consequences` under a
   bolded **Rejected alternatives:** list. Either placement is house style;
   *omitting* rejected alternatives entirely is not. Example shape (from
   ADR-0018):
   ```
   **Rejected alternatives:**

   - **Flat steal/block percentage roll.** Rejected: a hidden RNG outcome is
     not a read, removes the mind game, and is the arcade anti-goal (ADR-0003).
   - **Pure reflex with no commitment (instant steal/block button).** Rejected: ...
   - **A separate "reaction stat" system.** Rejected: ...
   ```
2. **Dated `## Amendment YYYY-MM-DD — <one-line summary>` sections, appended
   in place** — this is how a decision evolves *without* a new ADR number.
   Real example: ADR-0018 carries `## Amendment 2026-07-01 — Steal implements
   the overlap by per-tick repetition, not a direct Succeeds() call`, which
   explains a divergence discovered during a bug fix, gives the *why* in
   full, and states explicitly what it does and does not retract ("This is
   recorded as an accepted per-move implementation detail, not a retraction
   of §1"). ADR-0008 carries nine dated amendments; ADR-0009 and ADR-0010
   several each. (ADR-0003 evolves differently — it has no `## Amendment`
   headings at all, instead carrying five inline `**Refined:**` bullets; both
   styles exist in the corpus.) Amendments are the **dominant** mechanism for
   evolving a locked decision here — always read an ADR to the end before
   assuming its original body is the whole story.

### When a NEW number is warranted vs. an amendment

- **Amend in place** when the decision still holds and you're recording a
  refinement, a retune, a discovered implementation nuance, or a resolved
  contradiction that doesn't change the core call. Example: ADR-0010's
  turn-rate retunes (400 → 530 → 900 °/s) are all amendments — "heading is
  server-authoritative with a bounded non-linear turn rate" never changed,
  only the numbers.
- **New ADR number** when the decision itself is genuinely different: a new
  architectural axis (ADR-0012 gave ball-hand-side authority its own number
  rather than amending ADR-0004, because it changes the cosmetic/authoritative
  *boundary* for one field — a new decision, not a retune), or a full
  supersession where the old approach is abandoned (set `Superseded-by` on
  the old ADR pointing to the new — note no ADR in this repo has actually
  used this yet as of 2026-07-12).
- If genuinely unsure, prefer the amendment — this repo strongly favors it,
  and a new number for what is really a refinement fragments the decision's
  history across files.

### Status field discipline

Only four legal values: `Proposed | Accepted | Deprecated | Superseded`. As of
2026-07-12 all 19 numbered ADRs (0001–0019, no gaps; 0000 is the template) are
`Status: Accepted` — none Proposed, Deprecated, or Superseded. A `Proposed`
ADR is not yet a locked constraint; conversely, a file existing on disk (or on
a branch) does not make it binding — ADR-0018 sat drafted-and-held on an
unmerged branch before landing. Check `Status:` and check the file is on
`main` before treating an ADR as locked.

### The same-commit-as-code rule

Per CLAUDE.md's Decision Discipline: any architectural decision made or
changed during work must land its ADR — new file or amendment — **in the same
commit as the code implementing it**. Never defer the ADR to a follow-up. If
no existing ADR covers the decision's category, create the next numbered file
from `docs/adr/0000-template.md`. Cautionary precedent: commit `fdd409c` once
retuned the heading rate directly in code without updating ADR-0010, and the
ADR + tests silently drifted from shipped behavior until a human design-call
issue (#134) caught and ratified it — the same-commit rule exists so that
doesn't recur.

### ADR-0014's cite-or-ask body requirement

ADR-0014 sets a **closed, ranked reference set** for self-resolving
reference-grounded design questions: (1) locked ADRs, (2) real half-court 1v1
basketball, (3) *UFC Undisputed 3* (feel/commitment modeling), (4) *NBA 2K*
(taxonomy/controls only — **zero feel weight**).

Any commit or PR body that self-resolves a reference-grounded call **must
state, explicitly**:
- which reference was consulted,
- which tier it sits at (1–4),
- the specific real-world behavior being modeled.

A bare preference appeal — "it's more fun," "2K does this" — with no tier
grounding is **not a valid self-resolution**; if you can't cite a tier,
escalate to the human instead. The reference set is closed: adding a source
requires an ADR-0014 amendment, not an ad-hoc citation. Escalate rather than
self-resolve for: identity/anti-goal changes, contradictions with a locked
ADR, genuine reference deadlocks, and high-stakes irreversible calls. The
full escalation *process* is `hooper-change-control`'s territory; this skill
owns only the writing requirement.

## 3. Analysis-doc pattern (`docs/analysis/`)

One example exists as of 2026-07-12 and it is the pattern:
`docs/analysis/0079-shot-scatter-curve.md` (issue #79, ADR-0009, 2026-06-29).
Follow its shape:

1. **Header metadata**: Issue #, related ADR #, Date, and a one-paragraph
   Purpose that names who owns the resulting decision — 0079: "The human
   (playtest) owns the final feel sign-off; this document provides the
   measured make-% curve so that any tuning proposal can reason from numbers
   rather than intuition alone."
2. **Method section first, before any numbers — deterministic and
   reproducible.** State exactly what was simulated and through which *real
   production code path*, naming the classes (0079: "measured from the real
   deterministic physics chain — `ShotScatter → ShotArc → RimBackboard` — not
   derived from the closed-form approximation"); the sampling scheme (0079: a
   100×100 centroid grid of the unit square, 10,000 samples per point, "no
   RNG; bit-identical across runs"); the per-sample pass/fail condition; the
   error bar ("stable to ±1 pp"); and where the harness lives
   (`tests/Hooper.Ball.Tests/ShotScatterCurveCharacterizationTests.cs`). If
   the actual number-producing runner isn't committed (0079's was a throwaway
   console app), say so explicitly.
3. **A constants-preconditions section.** List every constant the numbers
   depend on and state plainly that the numbers are invalid if any of them
   change (0079 has a dedicated table of `[Export]` defaults with exactly
   that warning).
4. **Measured tables, with theory alongside.** Always tables, always units.
   Where a closed-form/theoretical prediction exists, print it in a column
   next to the measured value — the divergence is often the most valuable
   finding, and it must be *explained mechanically* (0079's "capture-cylinder
   rescue" section derives the gap from the actual 3-D contact geometry).
5. **An explicit non-prescriptive ending.** Close with a section labeled to
   disclaim recommendation — 0079's is literally titled "Candidate tuning
   observations (no prescription — human's feel call)". Phrase observations
   as conditionals ("if the design intent is X, then Y would need to change"),
   never directives. Feel calls are reserved for the human's playtest pass.
   An analysis doc that prescribes tuning values has overstepped into
   `hooper-change-control` territory.

### The causal-story lesson (PR #117 → PR #132) — check mechanisms against code

0079's own history is the cautionary tale. The first published draft
(PR #117) attributed the measured make% exceeding the closed-form prediction
at long range to "backboard/glass assists." That explanation was
**mechanically impossible**: `RimBackboard.Resolve` returns
`ContactResult.Bounce` on any board contact — never `Make` — and the harness
counts any `Bounce` as a miss, so the board can only *reduce* makes. The
measured numbers were never wrong; only the causal prose was. PR #132
corrected the mechanism (a 3-D vertical *capture cylinder* make-test vs. the
closed form's flat 2-D disc: an overshooting arc still sweeps through the
cylinder on the way down) and fixed a sign error and two misstated
percentages the re-examination surfaced.

**The rule: before writing a causal explanation for a measured result, read
the actual resolution code path the harness exercises.** A plausible story
that fits the numbers is not evidence — 0079's wrong story fit the numbers
perfectly. Every "because" in an analysis doc must be checked against the
branch of code it claims to describe.

## 4. Handoffs (`docs/handoffs/`) — gitignored scratch, README tracked

Everything in `docs/handoffs/` except `README.md` is gitignored (`.gitignore`
carries `docs/handoffs/*` with a `!README.md` negation). Handoffs are scratch:
they capture "where I am right now," which goes stale the moment the work
lands. Never cite a handoff as a durable source from a tracked doc — if a
fact lives only in a handoff, promote it (to an ADR, an issue body, or
CONTEXT.md) before relying on it.

**What belongs** (only what is NOT already in CLAUDE.md, the ADRs, the
issues, or the code):
- The exact next task and where you were interrupted.
- Build/run state ("compiles clean as of `<sha>`").
- Anything verified the hard way (e.g. an engine API checked against live
  docs because the docs MCP was unavailable).
- Gotchas the next agent will otherwise hit.
- Remaining human (`hitl`) editor steps only the user can do.

**What does NOT belong**: architecture reasoning (→ ADR), acceptance criteria
(→ the issue), why-a-specific-change-was-made (→ the commit body).

**Naming**: `docs/handoffs/<topic>.md`, e.g. `M1b-networking.md`. One file per
ongoing strand of work; update it in place as the strand progresses.

**The deletion convention is honored loosely.** The README says "delete it
once the work has landed," but in practice (verified 2026-07-12) most handoff
files on disk describe long-landed work and were never deleted. Therefore:
**a handoff's presence is NOT evidence its work is in flight.** Judge
freshness by **mtime** (`ls -la docs/handoffs/`) cross-checked against the
topic's live issue/PR state (`gh issue view`, `gh pr view`) before trusting
any load-bearing claim in one.

**A sharper trap before deleting one**: some handoffs record human-grilled
design decisions ("locked design calls — do NOT re-litigate") that exist
*only* in the gitignored handoff plus GitHub issue bodies — nowhere durable.
If you're about to delete a handoff per the convention, first check for a
locked-decision block and promote it (ADR amendment or CONTEXT.md entry)
before the file goes; otherwise the decision becomes effectively unrecorded.
The M9-era handoffs are the live example of this exposure.

## 5. Commit-message house style

### Subjects: conventional, scoped

`<type>(<scope>): <imperative summary>` — types in live use: `feat`, `fix`,
`docs`, `test`, `chore`, `refactor`. Scope names the subsystem: `(defense)`,
`(net)`, `(ball)`. Real subjects from the log: `test(defense): place the
harness backboard behind the rim so the control shot can score`,
`fix(net): ...`. One commit per distinct concept — if the description needs
an "and," split the commit. Keep `Closes #X`/`Refs #X` out of the subject
line; they live in the body.

### Fix commits read like mini post-mortems — multi-paragraph root-cause bodies

This is an enforced convention, not a nicety: fix/test-fix commit bodies in
this repo state the **symptom**, the **root-cause mechanism** (naming actual
classes, fields, and values), **what was verified and how**, and often an
explicit **scope boundary** (what this commit deliberately does NOT fix). One
complete real example, commit `8051e28` (verify with
`git log -1 --format="%B" 8051e28`):

```
test(defense): place the harness backboard behind the rim so the control shot can score

The control-make scenario did exactly its job on its first CI run: it
proved the harness's "guaranteed clean make" premise false. The
code-built tree inherits BallController's raw export defaults, and the
code-default BoardCenter (0, 3.5, 0.3) sits 0.3 m in FRONT of the
code-default RimCenter (0, 3.05, 0) — mutually inconsistent defaults
that the production scene corrects in Main.tscn (board (0, 3.205, 0.03)
sits 0.27 m BEHIND rim (0, 3.05, 0.3), away from the court). Under the
raw defaults every make-arc from ShooterPosition descends through the
board face on its way down (crossing the Z=0.3 plane at Y~3.42, inside
the face's [3.2, 3.8] span), Bounces, and goes Loose: the scramble
settles into Held at 0-0 forever — the exact control-make timeout CI
reported (state=Held, score1=0) — and "score unchanged" in the success
scenario was silently vacuous, since an arc that physically cannot
score proves nothing about the block. Verified against the pure
ShotArc + RimBackboard classes: raw defaults -> backboard Bounce at the
plane; production-relative placement -> clean Make. The harness now
mirrors production's relative board placement.

Note: NOT the take-it-back rule — the tipoff pre-clears the opening
possession by design (TryAssignTipoffHolder, ADR-0008) and nothing in
these scenarios un-clears it. [...]

The raw-default inconsistency itself (BoardCenter vs RimCenter) is a
production-code observation worth its own issue; out of scope for this
test-only commit.

Refs #98

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
```

Note the shape: symptom → mechanism with concrete numbers → verification →
"what this is NOT" boundary paragraph → issue-reference line → trailer.
Follow it for any non-trivial fix; a one-line "fix the bug" body violates
house style here.

### Refs vs. Closes (writing side only)

The full closing-keyword law (which artifact is allowed to close which issue,
hitl exceptions, partial-issue rules) is `hooper-change-control`'s territory.
The writing rules: a single-commit fix landing straight on `main` carries
`Closes #X` in its **body**; branch commits carry `Refs #X` only (never a
closing keyword — `close`/`fix`/`resolve` variants all count and must stay
out); the PR body carries the `Closes #X`. Never in a subject line. If a PR
resolves an issue only partially, keep ALL closing keywords out of both
commits and PR body — `Refs` only.

### Co-Authored-By trailer — known drift; follow the running session

Agent-authored commits end with a `Co-Authored-By:` trailer. **There is live
drift on the string** (as of 2026-07-12): `.claude/agents/issue-worker.md`
hardcodes `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`, while
the current harness/system-prompt convention — confirmed in recent real
commits like `8051e28` above — is
`Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
**Rule: use the identity string your own running session's system
instructions specify, not what an older agent-definition file hardcodes.**
Don't "fix" `issue-worker.md` as a side effect of a docs task — reconciling
that drift is its own change, owned by whoever owns `.claude/agents/`.

## 6. Doubt-cycle comment labeling

When a `/doubt-driven-development` cycle adds comments to a file that
**already carries labels from an earlier milestone or issue**, prefix each
new comment with the **current issue number** (and, if applicable, the
trigger), so readers can tell which review generation reasoned about which
line. Real pattern from `tests/integration/StealTurnoverTest.cs` — a file
already carrying `#175`, `#176`, `#193` labels from prior passes:

```csharp
// (#175, doubt cycle post-CI-failure) The REAL server-side machine's own
// (#175, doubt cycle post-CI-failure) "success" ALSO needs the shadow
// (#175, doubt cycle post-CI-failure) Latch the REAL server-side
```

Format: `// (#<issue>, doubt cycle <trigger>)` — trigger optional when
there's no specific event (plain `doubt cycle` is fine). A freshly created
file with only one issue's history doesn't need the prefix; a file with
mixed-generation comments always does.

## 7. THE STALENESS REGISTRY (known-stale doc facts, as of 2026-07-12)

Docs drift. Check this table before citing any of these facts — and
re-measure anything numeric even if it isn't listed (closing rule below).

| Doc / claim | Stated as | Actual (2026-07-12) | Re-verify with |
|---|---|---|---|
| `CONTEXT.md` "Sources:" line | Scopes its sources to ADR-0001–0008 | 19 numbered ADRs exist (0001–0019, no gaps; 0000 is the template). CONTEXT.md is incomplete, not exhaustive, for anything past the M6b era | `ls docs/adr/` |
| `CONTEXT.md` "Cosmetic facing" entry | Facing is velocity-derived, client-local, cosmetic | Predates ADR-0010: `Heading` is now server-authoritative state integrated into `Move()`; `FacingResolver` remains in the codebase but is off the authoritative path (ADR-0018 §4 explicitly forbids reading it for defensive resolution) | Read ADR-0010 and ADR-0018 §4; `grep -rn "FacingResolver" scripts/` |
| `CONTEXT.md` timing-window entries | Aspirational 2K-style "green window" framing | Predate ADR-0018, which landed a **tick-interval-overlap** model (`DefensiveResolution.Succeeds`, half-open integer-tick intervals `[start, end)`) — a real semantic gap, not just missing detail | Read ADR-0018 §1 |
| `docs/agents/domain.md` | Describes `TASKS.md` as the living tracker; lists the locked set as "ADRs 0001–0005" | `TASKS.md` does not exist (CLAUDE.md §3 says so explicitly; GitHub Issues is the sole tracker); all 19 ADRs are locked, not 5 | `ls TASKS.md` (absent); `ls docs/adr/` |
| ADR-0016's body | Quotes test counts (~250 unit / ~459 total) from its writing date | Live counts are materially higher (~669 total as of early July 2026 and climbing). **Any hardcoded test count anywhere — including this row — is unreliable the moment it's written** | `dotnet test tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj --list-tests` for the unit suite; `ls tests/integration/*.cs` for harness scenarios (separate suites — no single command combines them) |
| Any summary claiming "ADR-0010 says 400°/s" | 400°/s was an early documented default | Shipped value is 900°/s with back-turn factor 0.95 after the #172 retunes — recorded in ADR-0010's later Amendment sections, so the ADR is internally consistent *if read to the end*; stale summaries elsewhere are not | Read ADR-0010 including all Amendments; `grep -rn "MaxTurnRateDeg" scripts/` for the shipped constant |
| Any handoff file's presence in `docs/handoffs/` | Implies in-flight work | Several present handoffs describe long-landed work (deletion convention honored loosely, §4) | `ls -la docs/handoffs/` mtimes + `gh issue view` / `gh pr view` on the topic |

**Closing rule: this registry is a starting list, not a closed one.** Any
number you're about to state as fact — an ADR count, a test count, a
milestone status, a tuning constant — must be re-measured with the command in
the table (or its obvious analog), not copied from a doc, a memory file, or
even this skill, if it's more than a session old. Docs decay; commands don't.

## 8. Writing FOR agents (not for humans first)

The primary audience of every doc this skill governs is a zero-context agent
picking up work cold. Write accordingly:

- **Greppable.** Prefer exact, distinctive strings over paraphrase — the next
  agent will `grep` for a class name, issue number, or ADR number, not a
  synonym. Name real identifiers (`DefensiveResolution.Succeeds`,
  `BallController`, `RimBackboard.Resolve`) instead of describing them.
- **CONTEXT.md's terms, exactly** (§1). Don't drift to a synonym because it
  reads more naturally in the sentence.
- **Absolute dates, never "recently."** Write "2026-07-12," not "recently" or
  "last week" — a session six months out has no calendar anchor otherwise.
  Date-stamp every volatile fact inline, the way this file does.
- **Numbers travel with their re-verification command.** Any figure that can
  drift (count, percentage, version, tuning constant) should be written
  alongside the exact command that reproduces it — the way §7's table does.
  A number with no re-derivation path is a trap for the next reader.

## When NOT to use this

- **Gating and process** — merge rules, afk/hitl label law, which artifact
  closes an issue, ADR-0014's escalation *mechanics* (the citation-writing
  requirement itself is here), autopilot gate order → **hooper-change-control**.
- **Outward-facing writing** — anything for an audience outside this repo
  (public README pitch, novelty claims, positioning) →
  **hooper-external-positioning**. This skill governs docs *of record*, not
  docs *of persuasion*.
- **Designing tests or harness scenarios** → **hooper-verification-and-qa**.
- **Diagnosing a live failure** → **hooper-debugging-playbook** /
  **hooper-failure-archaeology**.

## Provenance and maintenance

Authored 2026-07-12 (written to disk 2026-07-14 after a session interruption;
all facts verified against the repo as described); reviewed and corrected
2026-07-15 (amendment counts: ADR-0003 has no `## Amendment` headings — five
inline `**Refined:**` bullets; ADR-0008 has nine amendments). Verified against:
`docs/adr/0000-template.md` (full read), `docs/adr/0018-defensive-timing-
window-model.md` (full read incl. Amendment), heading survey of all 20 ADR
files (`grep -l "Alternatives considered" docs/adr/*.md` and
`grep -c "Rejected alternative" docs/adr/*.md`), `docs/analysis/0079-shot-
scatter-curve.md` (full read), `docs/handoffs/README.md` (full read),
`docs/agents/domain.md` (full read), commit `8051e28` full body
(`git log -1 --format="%B" 8051e28`), and doubt-cycle labels grepped from
`tests/integration/StealTurnoverTest.cs`. Digest-sourced claims (test counts,
handoff inventory, issue-worker trailer string) were spot-checked where
load-bearing; counts are flagged unreliable-by-nature in §7.

Re-verification commands for anything that may drift:
- ADR inventory and gaps: `ls docs/adr/ | sort`
- Any ADR's Status: `grep -n "Status:" docs/adr/<file>.md`
- Which ADRs use which alternatives-heading: `grep -l "Alternatives considered" docs/adr/*.md`
- Handoff freshness: `ls -la docs/handoffs/`
- Unit-test count: `dotnet test tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj --list-tests`
- Integration-scenario inventory: `ls tests/integration/*.cs`
- Milestone table: read CLAUDE.md §2 live — never cache it
- Trailer convention in force: your running session's system instructions,
  not `.claude/agents/issue-worker.md`
