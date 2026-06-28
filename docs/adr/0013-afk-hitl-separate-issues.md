# ADR-0013 — AFK build work and HITL verification live in separate issues

- **Status:** Accepted
- **Date:** 2026-06-28
- **Superseded-by:** —

---

## Context

[ADR-0011](0011-claude-authors-scenes.md) moved scene/config *authoring* into AFK
and narrowed the human's role to **feel/tuning judgments** and **in-engine
verification**. Its Consequences section already anticipated this follow-on:

> "the `afk`/`hitl` labels regain precision: `hitl` now means 'needs a human to
> *judge feel or verify*,' not 'needs a human to *author*.' When writing or
> re-triaging issues, scope the `hitl` portion down to verification-only and move
> the wiring into the AFK PR (this directly re-scopes the M9 issues #83–#86…)."

What ADR-0011 did **not** settle is *where* the verification obligation lives once
the AFK portion is done. The M9 crossover/hesi issues (#83–#86) were each tagged
with **both** `afk` and `hitl`. Their AFK halves merged in PR #88, but because
each issue also carried the `hitl` verify gate, `Done means proven` (CLAUDE.md)
held them all open — the code shipped, yet four issues sat merged-but-open waiting
on a single editor session the owner could not run yet.

This is the deadlock a dual-labelled issue creates:

- The AFK author has finished and wants the issue **closed** (it's merged, it's
  releasable, it shouldn't show up as outstanding work).
- The verification gate wants the issue **open** (nobody has proven it in-engine).
- One issue cannot be both. So either the rule is violated (close on code alone,
  losing the proof obligation) or merged work lingers as open noise.

The owner hit exactly this and resolved it by hand: closing #83–#86 on their
merged code and consolidating the four verifies into a **separate** HITL issue
(#114). This ADR records that resolution as the standing convention so the next
agent doesn't re-derive it (or re-deadlock).

### Forces at play

1. **The two halves have different "done" conditions.** AFK build is done when the
   code merges; HITL verify is done when a human watches it run. Tying them to one
   issue forces the stricter gate onto the looser half.
2. **`Done means proven` is non-negotiable and unchanged** (CLAUDE.md, ADR-0011).
   The verification obligation must survive; it cannot be quietly dropped when the
   build issue closes. A separate issue is what carries it forward.
3. **Merged-but-open issues are noise.** A board where finished, released work
   stays open "because a human hasn't clicked yet" loses signal — you can no
   longer tell *in-progress* from *awaiting-proof* at a glance.
4. **Verification often batches.** Several AFK features (the whole crossover/hesi
   set) are naturally proven in one dual-instance session. One consolidated verify
   issue (#114) matches how the work is actually done better than four parallel
   verify gates.

### Alternatives considered

1. **One issue carries both `afk` and `hitl` (status quo).**
   Rejected. This is the deadlock above — it either violates `Done means proven`
   or leaves merged work open as noise.
2. **Close the dual-labelled issue on merged code; track verify only in a comment
   or a checklist.** Rejected. A comment is not a tracked work item; the proof
   obligation would fall off the board and `Done means proven` would erode in
   practice even if honoured in principle.
3. **Keep the dual-labelled issue open until verified, accept the merged-but-open
   noise.** Rejected. It's the bottleneck ADR-0011 set out to remove, just
   relocated from authoring to closure.

## Decision

**AFK build work and HITL editor verification are tracked as separate issues.** An
issue is single-purpose: it is either an `afk` build issue (closes when the code
merges) **or** a `hitl` verify issue (closes only when a human confirms it
in-engine — `Done means proven`, unchanged).

Operationally:

- **Do not file or leave an issue carrying both `afk` and `hitl`.** If a unit of
  work has both a build half and a verification half, split it: the `afk` issue
  builds and merges; a separate `hitl` issue holds the dual-instance verify and
  any feel/tuning.
- **When an `afk`-labelled issue also carries a `hitl` verify** (legacy or
  mis-scoped), the merged AFK work closes it; open or fold into an existing
  `hitl` verify issue so the proof obligation persists. Name the source in the
  closing comment so the trail is traceable (the #83–#86 → #114 pattern).
- **Verify issues may consolidate** several AFK features that are naturally proven
  in one editor session (e.g. #114 holds the whole M9 crossover/hesi + ball-orbit
  verify). One `hitl` issue per coherent verification pass is fine and preferred
  over one-verify-per-feature.

This is the issue-board corollary of ADR-0011: 0011 split *authoring* (AFK) from
*verification* (HITL) at the **work** level; 0013 splits them at the **issue**
level so the board stays honest.

## Consequences

**Easier:**
- The board reads true: `afk` issues close on merge, so open issues mean
  *genuinely outstanding* work, not awaiting-a-click. `hitl` issues are exactly
  the list of "things a human still has to prove in-engine."
- `Done means proven` is *strengthened*, not bent: the proof obligation gets its
  own durable, tracked home instead of blocking an otherwise-finished build issue.
- Verification batches cleanly into one session-sized issue.

**Harder / accepted tradeoffs:**
- **Discipline at filing/triage time.** New issues must be scoped single-purpose
  up front; dual-labelled issues are a smell to split, not a default. The
  `/grill-me`-style design sessions that produce build issues should file the
  matching verify issue (or fold into an existing one) rather than tacking `hitl`
  onto the build issue.
- **Traceability burden.** When closing a build issue whose verify moves
  elsewhere, the closing comment must name the destination verify issue, and the
  verify issue must name its sources — otherwise the proof obligation looks
  orphaned. (Done for #83–#86 ↔ #114.)
- **Does not lower the verification bar.** Splitting issues changes *bookkeeping*,
  not *proof*. A consolidated verify issue still closes only after the human runs
  the dual-instance session.

**Reversible.** This is a board convention, not code; if single-purpose issues
prove more overhead than they're worth, the project can revert to dual-labelled
issues without touching anything that ships.
