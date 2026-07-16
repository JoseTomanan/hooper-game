# ADR-0021 — Feel passes and taste checks deferred until "sufficiently built" (amends ADR-0015/0017)

- **Status:** Accepted
- **Date:** 2026-07-16
- **Superseded-by:** —

---

## Context

ADR-0015 established the per-milestone human feel pass as the one irreducible
human checkpoint in the autonomous-merge pipeline: "Anything the harness cannot
assert — feel, telegraph readability, lean degrees, 'does it look right' — is
collected into one human feel-acceptance pass **per milestone**, not per issue."
ADR-0017 then built on that cadence as one of its two closure conditions for
autopilot activation: a predecessor milestone is only "genuinely closed" once
its epic has closed under the ADR-0015/0016 bar, which ADR-0017 explicitly
spells out as "CI + harness + code-review + its one per-milestone human feel
pass." Both ADRs assumed the human would be available on roughly a
per-milestone cadence to run that pass — the cadence was the open question,
never the *existence* of the pass as a gate.

On 2026-07-16 the human, in-session, disclosed two related but distinct
external constraints, verbatim: "Taste can be postponed semi-indefinitely; I
want the app to be sufficiently built before I make these checks, so no time is
wasted. Feel passes, same way, can be postponed indefinitely for now; I just
want you to do as much work as possible while I'm busy doing other work." This
is a scheduling decision about *when* the human is available to gate feel and
taste, not a retraction of the *bar itself* — the human is not saying feel no
longer matters or should be auto-accepted; they are saying the pass should wait
until there is enough built to make one consolidated pass worthwhile, rather
than fragmenting the human's scarce attention across every milestone boundary.

Because this changes ADR-0015's per-milestone cadence and ADR-0017's activation
closure condition — both locked decisions — Decision Discipline requires
recording it rather than letting the autopilot silently reinterpret "one feel
pass per milestone" as "no feel pass for now."

### Forces at play

1. **The human explicitly named the bottleneck this removes.** ADR-0015 already
   diagnosed "human owns every merge" as a throughput bottleneck and traded it
   away; this is the same move one layer up — the *per-milestone feel-pass
   cadence itself* is now the bottleneck on autopilot throughput while the human
   is occupied elsewhere, and they've asked to remove it the same way.
2. **Deferral is not the same claim as auto-acceptance.** ADR-0015 gate 4 is
   unconditional: "Feel is never auto-accepted as feel." Postponing *when* the
   human looks is compatible with that gate only if postponed feel/taste work
   stays visibly *open* (not silently closed) until the human actually looks.
   Anything that would auto-close a feel-gated `hitl` issue in the meantime
   would cross from deferral into acceptance, which is out of scope here.
3. **A consolidated pass already exists as a precedent and a landing spot.**
   Issue #173 ("HITL FEEL PASS FOR M6b + M8 + M9 + M10") is already the running
   consolidation point for the M9+M10 feel debt referenced in CLAUDE.md §2 (via
   #114 as the per-milestone-pair checklist folded into it). Naming #173 as
   *the* deferred pass — rather than inventing a new tracking mechanism — costs
   nothing and keeps one place where feel debt visibly accrues.
4. **ADR-0017's activation gate cannot keep requiring a pass that won't run on
   schedule.** If "genuinely closed" still required the per-milestone feel pass
   literally, the autopilot would stall at the very first predecessor milestone
   whose feel pass hasn't happened yet — directly contradicting the human's "do
   as much work as possible" instruction. The gate must drop to CI + harness +
   code-review + epic-closed, with feel debt tracked separately rather than
   blocking.
5. **This is explicitly reversible scheduling, not architecture.** Nothing about
   *how* feel is measured, gated, or eventually accepted changes — only *when*
   the human is asked to look. The human can resume per-milestone cadence, or
   schedule the consolidated pass, at any time with zero code or process
   rearchitecting.

### Alternatives considered

1. **Status quo — one human feel pass per milestone, litigated as scheduled.**
   Rejected: this is precisely the per-milestone attention cost the human asked
   to remove while occupied elsewhere; keeping it would mean either the
   autopilot stalls waiting for passes that won't happen, or an agent quietly
   skips the requirement without recording it — both worse than an honest ADR.
2. **Auto-accept feel from harness-green as a stopgap** (treat "state is
   correct" as "feels right" until the human can check). Rejected outright:
   this violates ADR-0015 gate 4 verbatim ("Feel is never auto-accepted as
   feel") and the human's own framing — deferring a check is not the same as
   waiving it. A harness asserting "lean is non-zero during Active phase" was
   never evidence that the lean *feels* right, and that gap doesn't close just
   because the human is busy.
3. **Invent a new tracking issue/label for deferred feel debt instead of reusing
   #173.** Rejected as unnecessary fragmentation — #173 already plays this role
   for M6b/M8/M9/M10 feel debt (with #114 as its M9+M10-specific checklist);
   growing #173's title/scope to keep absorbing later milestones' feel debt is
   simpler than standing up a parallel mechanism.
4. **Drop ADR-0017's activation gate to CI+harness+code-review only, without
   any ADR recording the change.** Rejected: silently reinterpreting a locked
   ADR's closure bar is exactly what Decision Discipline exists to prevent —
   the next agent reading ADR-0017 verbatim would wrongly conclude a feel pass
   still gates activation.

## Decision

**The per-milestone human feel pass (ADR-0015) and all taste/art-direction
checks are postponed indefinitely. They run only when the human judges the game
"sufficiently built" and schedules a consolidated pass. The autopilot never
waits on them to proceed.** This amends both ADR-0015 and ADR-0017 in place, as
follows:

**(a) Amends ADR-0015's feel-pass cadence.** "One human feel-acceptance pass
per milestone" becomes **a single human-scheduled deferred pass**, consolidated
in issue **#173** (titled "HITL FEEL PASS FOR M6b + M8 + M9 + M10", and expected
to keep growing in scope as later milestones accrue feel debt — #114 remains
the M9+M10-specific checklist folded into #173's scope). Feel is **still never
auto-accepted** — ADR-0015 gate 4 is untouched. Feel/taste `hitl` issues stay
**open**, not closed, until that consolidated pass actually runs; deferral moves
the bar in **time**, not in **kind**.

**(b) Amends ADR-0017's activation-closure gate.** A predecessor milestone's
epic is "genuinely closed" for DEFERRED→Active activation purposes once **CI +
harness + code-review + the epic issue itself is closed** — the per-milestone
human feel-pass requirement is **dropped from the activation gate**. Feel debt
for that milestone accrues in #173 (per (a)) and does **not** block the next
milestone's DEFERRED→Active walk.

Both changes are scheduling-policy amendments, not retractions of either ADR's
core decision: ADR-0015's harness-proof bar for `afk`/state-checkable `hitl`
work is unchanged, and ADR-0017's DAG-walk-in-documented-order rule is
unchanged. Only the feel-pass's position — per-milestone gate versus deferred
consolidated checkpoint — moves.

## Consequences

**Easier:**
- The autopilot can walk the full milestone DAG (ADR-0017) without stalling at
  any milestone boundary waiting for human feel-pass availability — directly
  answering "do as much work as possible while I'm busy."
- Milestone activation and issue throughput are now gated purely by
  machine-checkable signals (CI, harness, code review, epic closure), which is
  the same throughput logic ADR-0015 already applied one layer down to the
  per-issue merge gate.
- One visible place (#173) accumulates all deferred feel/taste debt across
  milestones, so nothing silently vanishes — a future consolidated pass has a
  single, growing checklist to work through rather than needing to reconstruct
  what was skipped.

**Harder / accepted tradeoffs:**
- **Feel regressions can accumulate across multiple milestones before a human
  ever sees them**, not just within one milestone as ADR-0015 originally
  accepted. Accepted, with the same mitigations ADR-0015 already relied on:
  the harness guarantees state-correctness (a regression can be structurally
  wrong-feeling but not structurally broken), tuning values ship as
  research-backed defaults with cited rationale (source-driven-development,
  ADR-0014), and every change remains a per-PR revert away from undone.
- **ADR-0017's own stated mitigation is knowingly given up.** ADR-0017 named
  "each milestone's feel pass is a natural 'is this still the right next
  thing?' checkpoint" as a partial mitigation for a wrong DAG propagating
  unnoticed (its own Consequences section, "A wrong DAG now propagates
  faster"). This ADR removes that checkpoint from the activation path in
  exchange for throughput while the human is unavailable — recorded here
  explicitly so it is a chosen trade, not a silent loss.
- **Feel/taste `hitl` issues can sit open for a long time.** Accepted: that is
  the honest state of the work (unverified-for-feel, not broken), and it is
  exactly what #173's growing scope is for — better a visibly large backlog
  than a quietly waived one.
- **Reversible.** The human can schedule the consolidated #173 pass, or restore
  per-milestone cadence for ADR-0015/0017, at any time with no code or
  architecture change — this is scheduling policy, not architecture, exactly
  as ADR-0015 and ADR-0017 both already characterized their own reversibility.
