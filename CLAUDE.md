# CLAUDE.md — Project Context for Claude Code

This file is read at the start of every Claude Code session. It encodes the
locked design decisions, the engine constraints, and the rules of engagement so
that any coding session starts already knowing the architecture. Keep it current:
when a design decision changes, update this file in the same commit.

---

## 1. What we are building

A **competitive 1v1 basketball game**. The design identity in one sentence:
**the duel is the space between two players and who breaks first.** It is
deliberately NOT arcade like NBA 2K — it is technical and skill-based, closer in
spirit to a fighting game (Tekken) crossed with the "clunky-but-readable"
texture of Undisputed 3.

The developer building this has **no prior game-dev experience** and is driving
the project primarily through AI-written code (Claude Code). Therefore: explain
non-obvious choices, prefer clarity over cleverness, and never silently assume
game-dev knowledge on the human's part.

---

## 2. Engine + platform (and WHY — this changed deliberately)

- **Engine:** **Godot 4 (.NET / C# edition).** Not Unity, not Source/S&box.
- **Language:** **C#** (not GDScript). Scripts are `partial` classes that extend
  Godot node types (e.g. `public partial class PlayerController : CharacterBody3D`).
  Use the `Godot.NET.Sdk` in the `.csproj`.
- **Why Godot/C#, for the record (so this isn't relitigated):**
  - Free, open-source, MIT-licensed — no licensing entity that can change terms
    on a multi-year solo project. (This was decisive vs. Unity.)
  - Lightweight and strong on **low-spec hardware** — a stated requirement.
  - C# suits both the existing design notes and Claude Code's strengths.
  - **Known tradeoff:** smaller training-data footprint than Unity, so the API is
    guessed correctly less often, AND a thinner first-party netcode story. Both
    are accepted because (a) we verify against live docs, and (b) we were always
    hand-building custom netcode anyway (see §3), so weak defaults cost us little.
- **History note for context only:** earlier exploration considered Source
  2 / S&box. Rejected because Source 2 isn't licensable standalone, S&box's API
  is one month old (worst case for an AI agent), and its main advantage
  (player-modding platform) is NOT a pillar for us. Do not reintroduce S&box.
- **Platform:** target Windows first; Godot exports cross-platform, so Linux /
  Steam Deck stay *possible* later without committing to them now.

### Verify the API — Godot moves and the C# docs lag
Before writing nontrivial engine-facing code, consult the current Godot C# docs
(https://docs.godotengine.org, C# tab) and the class reference. Many community
examples are GDScript; translate carefully — C# naming is PascalCase, signals and
exports differ, and `partial` is mandatory on node scripts. Flag any API you are
unsure about rather than guessing.

---

## 3. Networking model (highest-risk system — note the corrected reasoning)

- **Model:** server-authoritative + client-side prediction + lag compensation
  (the CS / Source lineage of *design*, implemented in Godot ourselves).
  It is **NOT** peer rollback / GGPO.
- **WHY (corrected — this is now a DESIGN decision, not an engine constraint):**
  An earlier version of this project justified "no rollback" by pointing at
  S&box's simple networking. We are no longer on S&box, so that reason is void.
  The decision stands anyway for design reasons: our identity is competitive
  integrity on **self-hosted dedicated servers**, and rollback is a 2-peer model
  that fights with a client-server dedicated-server architecture. Server-
  authoritative + prediction is the correct fit for dedicated servers.
- **On Godot specifically:** Godot's high-level multiplayer (MultiplayerApi,
  MultiplayerSpawner/Synchronizer, RPCs) is a relatively thin replication/RPC
  layer. It does NOT give you prediction or lag compensation for free — you build
  the tick loop, the client prediction, the server reconciliation, and the lag
  compensation in C# on top of it. Treat the high-level API as transport +
  replication primitives, not as a finished netcode solution.
- **Authority:** server owns the truth; clients predict locally and reconcile to
  server corrections.
- **Community-centric requirement = self-hosted dedicated servers (option "a"):**
  the load-bearing community property is players running their own dedicated
  servers with a server browser / discovery — NOT player-modding, NOT a content
  platform. This is something we BUILD (server browser, discovery, dedicated-
  server export), and it is largely engine-independent. No official ranked /
  matchmaking at launch (CS 1.6 style); ranked is a post-launch milestone.

---

## 4. Locked design decisions (do not relitigate without being asked)

- **Spine:** footwork / spacing — separation creation vs. denial is the core 1v1
  interaction.
- **On top of the spine:** a commitment / mind-game layer — both players read and
  commit; wrong reads are punished.
- **Subordinate systems** (live INSIDE the spacing spine, not co-equal pillars):
  timing windows (shot release, steal, block) and stamina / resource.
- **Input model:** HYBRID. Analog stick = movement, positioning, change of pace
  (continuous neutral game). Discrete buttons / right-stick gestures = committed
  "break" moves (crossover, spin, hesitation, drive) with real startup / recovery
  frames.
- **Right stick:** committed moves (2K-familiar surface), resolved as discrete,
  locked-in commitments — NO flow-cancel.
- **Legibility is a design VALUE, not just code:** committed moves must have
  visibly telegraphed wind-ups. Animation may be deliberately clunkier to keep
  moves readable. Do not "smooth away" commitment frames.
- **Defense:** symmetric core (mirror footwork + committed reads) with a
  deliberate asymmetric tilt toward reaction.
- **Ball:** state-driven with a custom **DETERMINISTIC mini-physics** layer
  (hand-authored arc / bounce / rim math). Do NOT use Godot's general physics
  (Godot Physics / Jolt) for owned ball moments. Reasoning is engine-independent
  and survives the Godot switch unchanged: never bet networked determinism on a
  general physics engine. Keep the ball math self-contained and unit-testable.

---

## 5. Current milestone

> **Milestone 1 — Networked movement proof.**
> Two player-controlled capsules on a flat plane. One peer hosts, the other
> connects (Godot ENet / MultiplayerApi). Each player sees the other move
> smoothly in real time, with local prediction so the controlling player feels
> zero input lag.
>
> NO ball, NO basketball rules, NO mini-physics, NO committed moves yet. This
> proves the single riskiest assumption: that we can get networked, predicted,
> server-authoritative movement working in Godot. Everything else hangs off this.

Next milestones, rough order:
2. Local-only ball mini-physics (dribble attach + shot arc), single player, no net.
3. Hybrid input: analog movement + one discrete committed move with frames.
4. Networking the ball + committed moves (prediction over the hard cases).
5. Win condition / scoring for a minimal 1v1.
6. (Community layer) dedicated-server export + a basic server browser.

Do not build ahead of the current milestone unless asked.

---

## 6. Repo conventions (Godot has no enforced layout — this is ours)

- `project.godot`, the `.sln`, and the `.csproj` live at the **project root**
  (Godot generates the .sln/.csproj there; don't move them — Godot has known
  bugs relocating them).
- **`scripts/`** — all C# code, the part Claude Code owns. Subfolders by
  responsibility: `Player/`, `Networking/`, `Input/`, `Ball/`, `Systems/`.
- **`scenes/`** — `.tscn` scene files. Authored in the Godot editor by the human
  (see EDITOR_TASKS.md). Claude Code writes the C# a scene's nodes reference, but
  the human wires nodes in the editor.
- **`assets/`** — models, textures, sounds. Placeholder/gray is fine for now.
- One script = one node responsibility. `partial` class extending the node type.
- Comment the "why," not the "what," especially around netcode and the
  deterministic ball, because the human is learning the engine.
- When you finish a unit of work, tell the human exactly which EDITOR steps (if
  any) they must do to see it run — you cannot do them.
- Prefer one clear path; explain the tradeoff in a sentence and proceed.
- DECISION DISCIPLINE: If during a session we make or change an architectural
  decision (engine, networking model, input model, anything in §2–§4), do not
  just act on it — write it into the relevant section of this file with the
  reasoning and the rejected alternative, in the same commit as the code. If I
  ask you to do something that contradicts a locked decision in §2–§4, stop and
  flag the contradiction before writing code; don't silently comply.

---

## 7. Open technical risks

- Custom prediction + lag compensation on Godot's thin multiplayer layer is the
  hardest part of the project. Prove it in isolation (Milestone 1) first.
- The deterministic mini-physics ball is second-hardest. Self-contained, tested.
- Godot C# API churn + GDScript-centric examples: verify against live C# docs;
  don't copy GDScript patterns without translating.
