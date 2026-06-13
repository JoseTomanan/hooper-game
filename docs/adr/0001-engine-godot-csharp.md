# ADR-0001 — Engine: Godot 4 (.NET / C#)

- **Status:** Accepted
- **Date:** 2026-05-28
- **Superseded-by:** —

---

## Context

A solo developer with no prior game-dev experience is building a competitive 1v1
basketball game over a multi-year timeline, driving implementation primarily
through AI-written code (Claude Code). The project requirements are:

- **No licensing risk** — the engine must be permanently free with no entity that
  can change terms partway through a multi-year project.
- **Low-spec hardware** — the game must run well on modest machines; a heavy
  engine runtime is a liability.
- **AI-writeable code** — the language must be one Claude Code handles well.
- **Custom netcode** — we are hand-building server-authoritative prediction (see
  ADR-0002), so first-party netcode quality is not a deciding factor.

Earlier exploration evaluated **Source 2 / S&box** as a candidate engine. It was
rejected:

- Source 2 is not licensable standalone; you cannot ship on it without Valve.
- S&box's C# API was approximately one month old at the time of evaluation —
  worst-case for an AI coding agent whose training data predates it.
- S&box's primary advantage is its player-modding / content platform. That is
  not a pillar for this project.

**Do not reintroduce S&box.** The decision against it is permanent unless the
project's goals change fundamentally.

**Unity** was also evaluated and rejected:

- Unity's licensing model has changed once and can change again. A multi-year
  solo project cannot bet on a commercial licensor's policy stability.

## Decision

Use **Godot 4 (.NET / C# edition)** as the engine. All scripts are `partial`
classes extending Godot node types (e.g.
`public partial class PlayerController : CharacterBody3D`). Use `Godot.NET.Sdk`
in the `.csproj`. Do not use GDScript.

Target platform is **Windows first**. Godot exports cross-platform, so Linux /
Steam Deck remain possible later without committing to them now.

## Consequences

**Easier:**
- MIT-licensed, no licensor risk on a multi-year solo timeline.
- Lightweight runtime suits the low-spec hardware requirement.
- C# is well within Claude Code's training data; code suggestions are reliable
  for non-engine-specific logic.

**Harder / accepted tradeoffs:**
- Godot's training-data footprint is smaller than Unity's; the C# API will
  be guessed wrong more often than a Unity equivalent would be. Mitigation:
  **always verify nontrivial engine-facing code against the live Godot C# docs**
  (https://docs.godotengine.org, C# tab) and the class reference before writing
  it. The **Context7 MCP server** (`@upstash/context7-mcp`, installed globally)
  is the primary tool for fetching up-to-date Godot API docs mid-session — use
  it before writing any unfamiliar engine-facing call. Many community examples
  are GDScript — translate carefully. C# naming is PascalCase; signals and
  exports differ; `partial` is mandatory on node scripts. Flag any API you are
  unsure about rather than guessing.
- Godot's high-level multiplayer is a thin replication/RPC layer, not a finished
  netcode solution. This is accepted because we were always building custom
  netcode (see ADR-0002).
- No S&box-style player-modding platform. Accepted: that was never a project
  pillar.
