# ADR-0019 — Session-driven orchestration loop (the autopilot brain)

- **Status:** Accepted
- **Date:** 2026-06-30
- **Superseded-by:** —

---

## Context

ADR-0015 authorised autonomous merge for the AFK lane, ADR-0016 supplied the
headless harness that makes the green signal trustworthy, and ADR-0017 let the
autopilot walk the milestone DAG without a per-milestone human "go". Together they
describe an autopilot — but they describe it from the *governance* side. The
*mechanism* was only half-built: `.claude/agents/issue-worker.md` (the Sonnet
"hands" that takes one `afk` issue to a green PR) existed, and its own description
repeatedly refers to "the Opus orchestrator" that dispatches it, reviews its PR,
and merges on green. **That orchestrator was never built.** `.claude/agents/`
contained only the worker.

The consequence: the autopilot advanced only when a human hand-played
orchestrator — manually picking the next issue, spawning the worker, running
`/code-review`, and merging. The throughput win ADR-0015/0017 promised was gated
on a human doing the orchestration by hand, which is most of the work.

This ADR records the decision to build the missing brain — `.claude/agents/
orchestrator.md`, an Opus-pinned agent that runs the dispatch→review→merge loop —
**and**, more consequentially, *how* that loop is driven: by a live human-started
Claude Code session, not by an unattended trigger. That second choice changes the
project's autonomy posture, so it is recorded here rather than left implicit.

### Forces at play

1. **The worker presupposes an orchestrator that didn't exist.** The two-tier
   design (cheap executor + capable planner) was already chosen and written into
   the worker; this ADR completes it rather than inventing it. The planner is the
   half that must be Opus, because its decisions — *what to build next*, *is this
   PR safe to merge* — are the irreversible ones.
2. **Autonomy ≠ unattended.** "Maximum autonomy" (ADR-0015) meant *no human in the
   per-change critical path*, not *no human present*. Those are different axes. A
   loop can be fully autonomous **within** a session — picking, dispatching,
   reviewing, merging with no human clicks — while still requiring a human to
   *start* the session.
3. **An unattended loop needs a stored credential; a session loop doesn't.** To
   fire without a human present, something must hold an API/OAuth secret and a
   schedule (a cron workflow, a GitHub Action that launches Claude). That is a
   standing, always-armed merge capability on `main`. A session-driven loop reuses
   the *already-authenticated* context of the human's live session — no new secret
   exists to leak, rotate, or misfire.
4. **For a solo dev, "session = on/off switch" is a feature, not a limitation.**
   The human opening a session *is* the intent signal and the kill switch in one.
   Close the laptop and the autopilot stops; no orphaned automation keeps merging
   to `main` overnight. Oversight-by-presence is exactly the lightweight control a
   solo project wants.
5. **Self-pacing must not become busy-polling.** Within a session the loop has to
   wait — on a background worker, on CI — without burning the session. The harness
   re-invokes the orchestrator when a background subagent finishes, so the loop
   waits on events where it can (ScheduleWakeup long fallback) and polls only the
   genuinely external signal (CI) at a cache-warm cadence.

### Alternatives considered

1. **Unattended cron + stored credential** — a scheduled GitHub Action (or local
   cron) holds an API/OAuth secret and launches Claude to run the loop with no
   human present. **Rejected as overengineering for a solo dev.** It buys
   "advances while the laptop is closed" at the cost of a standing auto-merge
   credential on `main` that is always armed whether or not anyone is watching —
   a security-and-blast-radius liability with no proportional benefit for one
   developer. It also removes the natural on/off switch (force 4): a
   mis-configured schedule could merge for hours unobserved. The session-driven
   loop delivers the same *in-session* throughput with the human's presence as the
   loop's ignition and kill switch, and **no new secret to manage**. If the
   project ever grows beyond a solo dev and genuinely needs overnight progress,
   this is the decision to revisit — explicitly, with the credential's custody
   designed, not as a default.
2. **No orchestrator agent — keep hand-playing it in the main thread.** Rejected.
   It works, but it is exactly the manual orchestration the autonomy ADRs set out
   to remove; the judgment is reusable and belongs in a named, Opus-pinned agent
   so any session can dispatch it identically.
3. **Fold orchestration into the worker (one self-merging agent).** Rejected. A
   worker that picks its own issues *and* merges its own PRs collapses the
   proposer/reviewer separation ADR-0015 leaned on (its alternative 4) — an agent
   reviewing and merging its own work is the weakest possible gate. The brain and
   the hands stay distinct: the worker opens the PR, the orchestrator (independent
   context) reviews and merges it.
4. **Run many workers in parallel for throughput.** Rejected as the default.
   Concurrent worktrees on this small repo collide, producing merge conflicts the
   orchestrator then has to resolve — a stop condition that costs more than the
   parallelism saves. Default is one worker in flight, merges serialized;
   independent-track concurrency is a deliberate exception, not the norm.

## Decision

**The AFK autopilot is driven by an Opus-pinned `orchestrator` agent that runs the
dispatch → review → merge loop within a live, human-started Claude Code session.
There is no unattended trigger and no stored credential; the human starting and
ending the session is the loop's on/off switch.**

Concretely:

- **`.claude/agents/orchestrator.md`** (`model: opus`) is the brain. It refreshes
  state from CLAUDE.md §2 (the work-authorisation table, ADR-0017) and open GitHub
  issues; picks the next ready `afk` leaf in DAG order; decomposes an epic into
  harness-checkable sub-issues (ADR-0016) when it has no ready leaf; dispatches
  exactly one `issue-worker` per issue in an isolated background worktree; runs
  `/code-review` on the resulting PR; and **merges (merge-commit) only on green CI
  + clean review** (ADR-0015). It never writes feature code and never merges on
  red.
- **The loop is session-scoped and self-paced.** Between iterations the
  orchestrator uses ScheduleWakeup / the `/loop` skill to re-arm and to wait —
  relying on automatic re-invocation when a background worker finishes (long
  fallback timer for hangs) and polling only CI (external state) at a cache-warm
  cadence. The loop pauses naturally when the human ends the session.
- **The human checkpoints are unchanged and explicit.** The orchestrator stops and
  surfaces — it does not push through — on a milestone's per-milestone feel pass
  (ADR-0015), repeated worker failure, an unresolvable merge conflict, or any
  decision that would change an ADR (Decision Discipline) / a genuine design call
  (ADR-0014). It activates DEFERRED milestones only in §2's documented order and
  only after genuine closure (ADR-0017).
- **Explicitly out of scope (rejected, not deferred):** any unattended trigger — a
  cron workflow, a scheduled GitHub Action that launches Claude, or any stored
  API/OAuth secret. Adding one is a future decision that revisits this ADR, not a
  silent extension.

## Consequences

**Easier:**
- The autopilot actually runs autonomously *within a session*: a human dispatches
  the orchestrator once and it walks the DAG — picking, building (via workers),
  reviewing, and merging — with no per-change human clicks, which is the
  throughput ADR-0015/0017 promised but ADR-0016's mechanism alone couldn't
  deliver without a brain.
- The two-tier agent design is complete and symmetric: the worker's references to
  "the Opus orchestrator" now resolve to a real agent with the matching contract.
- No new secret exists. The autonomy posture adds no standing credential and no
  always-armed automation; security surface is unchanged from a normal session.

**Harder / accepted tradeoffs:**
- **No overnight progress.** The autopilot only advances while a session is open;
  it cannot pick up work while the human is away (force 4 / alternative 1). This
  is the deliberate price of having no unattended credential — accepted for a solo
  dev, and the explicit thing to revisit if the project scales.
- **The orchestrator's judgment is now load-bearing.** It decides what to build
  and what to merge; a confident-but-wrong orchestrator could merge bad work or
  walk the DAG wrong. Mitigated by: the non-bypassable merge gates (ADR-0015) it
  cannot weaken, independent `/code-review`, the harness (ADR-0016), and the
  surfaced stop conditions for everything ambiguous or feel-related.
- **Documentation must track the new agent.** This ADR and the at-a-glance ADR
  table in CLAUDE.md gain a row; the orchestrator agent and worker now form a
  documented pair.

**Reversible.** The orchestrator is an agent definition (configuration, not
architecture) and the loop is a session behaviour. Removing the agent returns the
project to hand-played orchestration with zero code impact; adding an unattended
trigger later is a separate, explicit decision against alternative 1.
