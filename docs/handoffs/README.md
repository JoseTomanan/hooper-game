# Handoffs — cross-session scratch state

This folder holds **handoff documents**: the in-flight state one Claude Code
session leaves for the next when a piece of work spans multiple sessions
(typically a big change — networking, the ball physics — that won't finish in one
sitting).

**Everything in this folder except this README is gitignored.** Handoffs are
scratch, not durable documentation. They capture "where I am right now," which
goes stale the moment the work lands. Durable knowledge belongs in the other
three layers, not here:

- **Why an architecture choice was made** → an ADR in `docs/adr/`.
- **What a unit of work must achieve** → the GitHub issue (scope + acceptance
  criteria).
- **Why a specific change was made** → the commit body (with `Closes #X`).

## When to write one

When you stop mid-change and the next agent would otherwise have to re-derive
state that lives in none of the three durable layers above.

## What a good handoff contains

Only what is *not* already in CLAUDE.md, the ADRs, the issues, or the code:

- The exact next task, and where you were interrupted.
- Build/run state ("compiles clean as of `<sha>`", "runs in-editor").
- Anything verified the hard way (e.g. an engine API checked against live docs
  because Context7 was unavailable — see ADR-0001).
- Gotchas and traps the next agent will otherwise hit.
- Remaining human (`hitl`) editor steps that only the user can do.

## Naming

`docs/handoffs/<topic>.md`, e.g. `M1b-networking.md`. One file per ongoing
strand of work; overwrite/update it as the strand progresses, delete it once the
work has landed.
