# Hooper

> **The duel is the space between two players — and who breaks first.**

A competitive 1v1 basketball game built around **footwork, spacing, and commitment**. Not an arcade game. Not a simulation. Closer in spirit to a fighting game (Tekken) crossed with the competitive legibility of *Undisputed 3* — every move has a telegraphed wind-up, wrong reads are punished, and the mind game lives in the gap between two players.

---

## Design Identity

| Pillar | What it means |
|--------|---------------|
| **Spine — Spacing** | Separation creation vs. denial is the core 1v1 interaction. Footwork is the neutral game. |
| **Commitment layer** | Discrete moves have real startup and recovery frames. Once you commit, you cannot cancel. Wrong reads are punished. |
| **Legibility** | A competitive requirement, not an aesthetic. Startup frames are clunkier than a "polished" game so both players can make fair reads. Bounded: never smoothed away (destroys the mind game); never comedic (Undisputed 3, not Goat Simulator). |
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

## Current Status & Roadmap

The current milestone and the full milestone status map live in
[`AGENTS.md` §2](AGENTS.md) (the single source of truth), with GitHub Issues
tracking the live state of each milestone and its sub-issues.

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
├── scenes/           # Agent-authored .tscn files and resources (ADR-0011)
├── assets/           # Models, textures, sounds (placeholder/gray for now)
├── addons/           # Godot editor plugins — see AGENTS.md §3 (godot_dotnet_mcp)
└── docs/
    ├── adr/          # Architecture Decision Records — read before changing engine-facing code
    └── handoffs/     # Cross-session scratch notes (gitignored)
```

Agents author scene wiring, resources and Input Map entries directly as text
(ADR-0011). Scene properties and node paths connect the scenes to C# scripts;
the human owns visual and feel judgments.

---

## Running the Project

This project uses **Godot 4.7.1 (.NET edition)** and the **.NET 8 SDK** — the
standard Godot build does *not* include C# support.

1. Install Godot **4.7.1 .NET** and the [.NET 8 SDK](https://dotnet.microsoft.com/download).
2. Open `project.godot` in Godot.
3. Build the C# solution (`Build > Build Solution` or MSBuild via CLI).
4. Run the `Main` scene.

Input actions are already configured in `project.godot`; no manual Input Map
setup is required. Offensive sprint uses Shift or the right trigger while
dribbling ([#325](https://github.com/JoseTomanan/hooper-game/issues/325), closed
2026-10-11).

While dribbling, **tap K** for a **BehindTheBack** to the opposite hand, or
**hold K** for a **StepBack**. The downward tap allowance is twelve physics
samples (200 ms at 60 Hz); the thirteenth held sample selects StepBack.
StepBack gathers and spends the dribble. After the move completes, movement
input can turn/pivot the holder but cannot make them walk; shooting remains
available. A new possession restores the dribble.

For **RetreatDribble**, hold **Q** through a K tap and release. Q plus **J/L**
toward the empty hand also still selects BehindTheBack. Both dribble moves
keep bouncing. The same downward tap/hold grammar and size-up modifier apply
on the right stick; horizontal gesture timing is unchanged.

### Running tests

The pure, engine-free modules (ball physics, committed-move framework, score
state, server discovery, player movement math + prediction bookkeeping) have a
headless xUnit suite that runs without opening Godot:

```
dotnet test tests/Hooper.Ball.Tests
```

The unit suite is one merge gate. State-checkable integration proof runs through
the executable headless harness catalog, alongside build and code review gates
(ADR-0015/0016). [#381](https://github.com/JoseTomanan/hooper-game/issues/381)
made the catalog authoritative; [#382](https://github.com/JoseTomanan/hooper-game/issues/382)
sharded exhaustive CI without reducing coverage.

Build a proof-capable assembly before running integration scenes:

```powershell
dotnet build "HOOPER GAME.csproj" -p:IncludeIntegrationHarness=true
python tools/harness_catalog.py run --all --godot <path-to-godot-console> --bash <path-to-bash>
```

For an ordinary game compile, `-p:IncludeIntegrationHarness=false` omits
integration sources; the compatibility default is `true`. See
[build-isolation spike #389](docs/spikes/0015-harness-build-isolation.md) for the
configuration boundary. Same-scene process batching was rejected after
[#388's NO-GO](docs/spikes/0014-same-scene-process-reuse.md);
[#394](https://github.com/JoseTomanan/hooper-game/issues/394) closed as not planned.

---

## Architecture Decisions

All significant decisions — engine choice, networking model, input model, ball physics, community model, renderer — are recorded as ADRs in [`docs/adr/`](docs/adr/). Read them before making engine-facing changes. Locked decisions are not relitigated without explicit discussion.

---

## Project Notes

- Built by a solo developer with no prior game-dev experience, driving implementation primarily through AI-written code (Claude Code).
- Issues are tracked in [GitHub Issues](https://github.com/JoseTomanan/hooper-game/issues). `afk` means ready for agent implementation; `hitl` tracks remaining human judgments or verification.
- "Done means proven, not written." State-checkable criteria use headless proof; visual and feel judgments remain human-owned. [#365](https://github.com/JoseTomanan/hooper-game/issues/365) proved hosted rendered capture, which supplies evidence without accepting feel. The consolidated feel pass remains deferred in [#173](https://github.com/JoseTomanan/hooper-game/issues/173) (ADR-0021); ADR-0024 remains Proposed.
