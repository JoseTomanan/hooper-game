# ADR-0017 — Autopilot may activate deferred milestones in dependency order

- **Status:** Accepted
- **Date:** 2026-06-29
- **Superseded-by:** —

---

## Context

CLAUDE.md §2 ends with a hard rule:

> "Do not build ahead of the current milestone unless asked. M6b, M7b, and M9 are
> open for work; M8 and M10 onward are not."

and §2's table marks M10–M15 as **DEFERRED** planning epics, each staying
deferred "until explicitly activated," at which point a human flips its status to
Active. That rule exists for a good reason: it stops an eager agent from
half-building M13 audio before the M10 defensive core that gives audio something
to react to even exists — i.e. it enforces **dependency order** and prevents
speculative work on an unstable foundation.

The human has now asked for the autopilot to **drive the full roadmap to release
(M15)**, auto-activating deferred epics as active ones close. That directly
contradicts the "do not build ahead" rule — so per Decision Discipline this ADR
records the change rather than letting the autopilot silently violate it.

The key realisation: the "do not build ahead" rule was protecting two distinct
things, and only one of them needs a human.

1. **Dependency order** — don't build a milestone whose foundations aren't done.
   This is a *graph* property (a DAG over milestones), and a DAG can be walked by
   a machine as correctly as by a person. The roadmap in §2 already *states* the
   dependency order explicitly ("M9–M10 complete the core duel … M11 adds the
   stamina pillar on top, M12–M13 turn the loop into a game … M15 ships it").
2. **Human intent gate** — don't wander off building things the human didn't
   want. Under the old manual model this was the human typing "now do M10." Under
   maximum autonomy the human has *pre-authorised the whole DAG* — the intent is
   "build all of it, in order, to M15." So the per-milestone human "go" is no
   longer carrying intent; it's just being a slow scheduler for a DAG the machine
   can already read.

So the rule can be relaxed along axis 2 (the human pre-authorises the full walk)
**without** abandoning axis 1 (the DAG order is still strictly enforced — the
autopilot does not get to build M13 before M10).

### Forces at play

1. **The dependency order is explicit and stable.** §2 already encodes it; this
   ADR doesn't invent an order, it commits to *walking the one already written*.
2. **"Don't build ahead" conflated order with intent.** Separating them lets us
   keep the valuable half (order) and automate the slow half (intent), per the
   analysis above.
3. **Activation is not the same as good work.** Letting the autopilot *start* a
   milestone says nothing about whether the work is correct — that is still
   gated by ADR-0015 (merge only on green) and ADR-0016 (the harness). This ADR
   only governs *what may be picked up*, not *what may be merged*.
4. **A runaway autopilot is the risk.** Auto-activation means the system never
   idles waiting for a human "next" — good for throughput, but it must not
   activate a milestone whose predecessors aren't *actually* done (not merely
   "merged some PRs"). "Done" for a predecessor milestone must mean its epic
   closed under the ADR-0015/0016 bar, including its per-milestone feel pass.
5. **The roadmap may need to change as we learn.** Building M10 might reveal that
   M11's design is wrong. Auto-activation must not ossify the DAG — the human (or
   a discovered constraint) can still re-order, insert, or cut milestones; the
   autopilot walks the *current* DAG, not a frozen one.

### Alternatives considered

1. **Status quo — human types "now do MN" before each milestone.**
   Rejected. Under pre-authorised full-roadmap autonomy this gate carries no
   intent (force 2); it is pure scheduling latency the machine can absorb.
2. **Auto-activate everything immediately; let workers grab any open issue
   regardless of milestone.**
   Rejected, dangerous. It throws away axis 1 (dependency order) — workers would
   build M13 against a non-existent M10, exactly the speculative-on-unstable-
   foundation failure the original rule prevented (force 1). Order is the half we
   keep.
3. **Auto-activate the *next* milestone only when the current one's epic closes,
   strictly in the §2 DAG order.**
   **Adopted.** Keeps dependency order hard-enforced (axis 1) while removing the
   per-milestone human "go" (axis 2). One milestone in flight (or its parallel
   tracks, where §2 already sanctions parallelism like M6b/M7b/M9), then the next
   unlocks on real closure.
4. **Auto-activate based on a re-derived dependency graph the autopilot computes
   itself.**
   Rejected as overreach. The autopilot should walk the *documented* order (§2),
   not invent its own — inventing one re-opens axis 2 (intent) under the guise of
   graph analysis. If the documented order is wrong, that's a human/ADR change,
   not an autopilot inference.

## Decision

**The autopilot may activate a DEFERRED milestone without a per-milestone human
"go", provided it activates strictly in the dependency order documented in
CLAUDE.md §2 and only after every predecessor milestone's epic is genuinely
closed under the ADR-0015/0016 bar.**

Concretely:

- **Walk the documented DAG, don't invent one.** The activation order is the one
  §2 already states: the core duel first (M9 offense → M10 defense), then the
  stamina pillar on top (M11), then loop-into-game (M12 flow/HUD → M13
  feel/audio), then learnability (M14), then ship (M15). Parallel tracks that §2
  already blesses (e.g. a presentation track running beside a systems track) stay
  parallel.
- **Activation = flip DEFERRED → Active.** When a milestone activates, the
  autopilot updates the §2 status table (DEFERRED → Active) in the same change
  that begins its work, and (for umbrella epics) stops it merely accruing
  sub-issues and starts decomposing + dispatching them.
- **A predecessor is "done" only at ADR-0015/0016 closure** (force 4): its epic
  issue closed, its sub-issues merged on green (CI + harness + code-review), and
  its one per-milestone human feel pass completed. "Some PRs merged" is not done;
  a milestone does not unlock its successor until it is *closed*.
- **Activation gates pickup, not merge** (force 3). Activating M10 lets workers be
  dispatched against M10 issues; it grants no relief from the merge gates. Bad
  M10 work still cannot reach `main`.
- **The DAG stays editable** (force 5). The human may re-order, insert, split, or
  cut milestones at any time; a discovered constraint during a milestone may force
  the same. The autopilot walks the *current* documented order, and a change to
  that order is a normal CLAUDE.md/ADR edit, not a fight with the autopilot.

## Consequences

**Easier:**
- The roadmap drives itself: closing a milestone's epic auto-unlocks the next, so
  the pipeline never idles on a human "next" — the throughput point of maximum
  autonomy.
- The §2 table becomes a live scheduler rather than a static map: its
  DEFERRED/Active column is now the autopilot's work-authorisation signal.

**Harder / accepted tradeoffs:**
- **A wrong DAG now propagates faster.** If the documented order is subtly wrong,
  the autopilot will march down it without a human pausing to notice (force 5).
  Mitigated by: order changes are cheap CLAUDE.md edits, and each milestone's
  feel pass is a natural "is this still the right next thing?" checkpoint.
- **"Genuinely closed" must be enforced, not assumed** (force 4). The tempting
  bug is to treat "all sub-issue PRs merged" as milestone-done and unlock the
  next before the feel pass. The decision makes closure (incl. feel pass) the
  unlock condition precisely to prevent racing ahead on an unvalidated milestone.
- **Less human situational awareness of "where are we."** The human no longer
  implicitly tracks progress by being asked to start each milestone. Mitigated by
  the live §2 table and the per-milestone feel pass keeping them in the loop at
  milestone granularity.
- **Documentation updated in the accepting commit** (Decision Discipline):
  CLAUDE.md §2's "Do not build ahead of the current milestone" rule is amended to
  reference this ADR's DAG-walk authorisation, and the ADR table gains this row.
- **Reversible.** Revoking auto-activation returns to human-typed milestone starts
  with no code impact — it is a scheduling policy, not architecture.
