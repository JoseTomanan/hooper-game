# TASKS.md — Living work tracker

This is the issues/progress tracker for the project. Unlike CLAUDE.md (which holds
locked design decisions) and EDITOR_TASKS.md (the human's Godot-editor checklist),
this file tracks *state*: what's done, what's in flight, what's next, what's blocked.

Rules of the road:
- Claude Code updates this file when it finishes a unit of work, in the same commit
  as the code. (See CLAUDE.md §3 — Decision Discipline.)
- "Done" means proven, not written. A networking task isn't done until the human has
  confirmed it in two editor instances (EDITOR_TASKS step 8).
- Keep it terse. One line per item. Move finished items to DONE with the date.
- This file is for *tasks/state*. Architectural decisions go in CLAUDE.md, not here.

---

## Current milestone

**Milestone 1 — Networked movement proof.** Two player-controlled capsules on a flat
plane; one peer hosts, one joins over localhost; each sees the other move smoothly,
with client-side prediction so the controlling player feels zero input lag.

Split into two sessions (decided 2026-05-28):
- **1a — Local movement only** (current). One capsule, analog input, immediate feel.
  No networking, no second player, no prediction.
- **1b — Networking on top.** Server-authoritative + client prediction + reconciliation
  over Godot MultiplayerApi/ENet. Built on 1a's movement step.

---

## In progress

- [ ] **1a** — `PlayerController` (CharacterBody3D): analog movement read + move step.
  Structured so a network layer can drive the same movement step later (1b), without
  building any networking now.

## Next

- [ ] **1a editor** (human) — build Main.tscn + Player.tscn, attach script, add Input
  Map actions, run single instance, confirm immediate movement (EDITOR_TASKS 1–7).
- [ ] **1b** — host/join (ENet), MultiplayerSpawner, tick loop, client-side prediction,
  server reconciliation, lag compensation. Verify each API against live Godot C# docs.
- [ ] **1b editor** (human) — Debug → Run Multiple Instances → 2; confirm second capsule
  appears and moves smoothly in both windows (EDITOR_TASKS 8). ← Milestone 1 PROVEN here.

## Blocked / open

- (none yet)

---

## Done

- [x] 2026-05-28 — Project context files authored: CLAUDE.md, EDITOR_TASKS.md, README.md.
- [x] 2026-05-28 — One-time setup decided: .NET SDK → Godot 4 .NET/C# → VS Code/VS → Git
  → Node.js → Claude Code. (See EDITOR_TASKS "One-time setup".)

---

## Parking lot (future milestones — do NOT build ahead)

- M2 — Local ball mini-physics (dribble attach + shot arc), single player, no net.
- M3 — Hybrid input: analog movement + one discrete committed move with frames.
- M4 — Networking the ball + committed moves.
- M5 — Win condition / scoring for minimal 1v1.
- M6 — Dedicated-server export + basic server browser.

> Reconcile open thread: the handoff doc from the original design chat still describes
> the S&box/Source 2 stack. ADR-0001 (docs/adr/0001-engine-godot-csharp.md) supersedes
> it (Godot/C#). If that handoff is ever fed to a session, it must not reintroduce S&box.
