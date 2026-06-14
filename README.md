# Hooper

> **The duel is the space between two players — and who breaks first.**

A competitive 1v1 basketball game built around **footwork, spacing, and commitment**. Not an arcade game. Not a simulation. Closer in spirit to a fighting game (Tekken) crossed with the clunky-but-readable texture of *Undisputed 3* — every move has a telegraphed wind-up, wrong reads are punished, and the mind game lives in the gap between two players.

---

## Design Identity

| Pillar | What it means |
|--------|---------------|
| **Spine — Spacing** | Separation creation vs. denial is the core 1v1 interaction. Footwork is the neutral game. |
| **Commitment layer** | Discrete moves have real startup and recovery frames. Once you commit, you cannot cancel. Wrong reads are punished. |
| **Legibility** | Moves are deliberately readable — wind-ups are clunkier than a "polished" game on purpose. Those frames are the product. |
| **Defense** | Symmetric core (mirror footwork + committed reads) with a deliberate asymmetric tilt toward reaction. |

Systems like timing windows (shot release, steal/block) and stamina live **inside** the spacing spine — they are not co-equal pillars.

---

## Tech Stack

| Layer | Choice | Why |
|-------|--------|-----|
| Engine | **Godot 4 (.NET / C#)** | MIT-licensed, no licensor risk on a multi-year solo timeline; lightweight runtime |
| Networking | **Server-authoritative + client prediction** | Clean authority model; correct fit for self-hosted dedicated servers |
| Input | **Hybrid analog + discrete committed moves** | Fluid neutral game underneath locked-in commitment layer |
| Ball physics | **Custom deterministic mini-physics** | Bit-identical results on server and all clients; no reconciliation jitter |
| Community | **Self-hosted dedicated servers + server browser** | CS 1.6 model — no central matchmaking needed at launch |

Architecture decisions are documented in full in [`docs/adr/`](docs/adr/).

---

## Current Status — Milestone 1

> **Networked movement proof.**  
> Two player-controlled capsules on a flat plane. One peer hosts, the other connects (Godot ENet / MultiplayerApi). Each player sees the other move smoothly in real time, with local prediction so the controlling player feels zero input lag.

**No ball, no basketball rules, no committed moves yet.** Milestone 1 exists to prove the single riskiest assumption first: that server-authoritative prediction works in Godot before anything else is built on top of it.

### Milestone roadmap

| # | Goal |
|---|------|
| **M1** | Networked movement proof (two capsules, prediction, no lag) |
| **M2** | Local-only ball mini-physics (dribble attach + shot arc) |
| **M3** | Hybrid input: analog movement + one discrete committed move with frames |
| **M4** | Networking the ball + committed moves (prediction over the hard cases) |
| **M5** | Win condition / scoring for a minimal 1v1 |
| **M6** | Dedicated-server export + basic server browser |

---

## Repo Layout

```
hooper-game/
├── scripts/          # All C# code (Claude Code's domain)
│   ├── Player/       # PlayerController — analog movement, prediction seam
│   ├── Networking/   # Tick loop, client prediction, server reconciliation
│   ├── Input/        # Input reading, committed-move state machine
│   ├── Ball/         # Deterministic mini-physics
│   └── Systems/      # Cross-cutting systems
├── scenes/           # .tscn files (human-authored in the Godot editor)
├── assets/           # Models, textures, sounds (placeholder/gray for now)
└── docs/
    ├── adr/          # Architecture Decision Records — read before changing engine-facing code
    └── handoffs/     # Cross-session scratch notes (gitignored)
```

`scenes/` files are wired in the Godot editor by the human. `scripts/` is AI-written C#. The two halves meet at `[Export]` properties and node paths.

---

## Running the Project

This project requires **Godot 4 (.NET edition)** — the standard Godot 4 build does *not* include C# support.

1. Install [Godot 4 .NET](https://godotengine.org/download/) and the [.NET 8 SDK](https://dotnet.microsoft.com/download).
2. Open `project.godot` in Godot.
3. Build the C# solution (`Build > Build Solution` or MSBuild via CLI).
4. Run the `Main` scene.

Input actions (`move_left`, `move_right`, `move_forward`, `move_backward`) must be configured in **Project → Project Settings → Input Map** before movement works.

---

## Architecture Decisions

All significant decisions — engine choice, networking model, input model, ball physics, community model, renderer — are recorded as ADRs in [`docs/adr/`](docs/adr/). Read them before making engine-facing changes. Locked decisions are not relitigated without explicit discussion.

---

## Project Notes

- Built by a solo developer with no prior game-dev experience, driving implementation primarily through AI-written code (Claude Code).
- Issues are tracked in [GitHub Issues](../../issues). `afk` label = ready for AI implementation; `hitl` label = requires a human editor step in Godot before closing.
- "Done means proven, not written." `hitl` issues close only after in-editor verification.
