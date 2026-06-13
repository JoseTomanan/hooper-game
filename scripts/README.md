# scripts/ — C# gameplay (Claude Code's domain)

All C# lives here. Godot doesn't enforce a layout, so this is our convention.
Scenes (.tscn) live in ../scenes and are authored in the editor by the human;
Claude Code writes the C# those nodes reference but does not wire nodes.

Each script is a `partial` class extending a Godot node type
(e.g. `public partial class PlayerController : CharacterBody3D`).

Subfolders, by responsibility:

- **Player/** — player node components: movement, state, camera rig.
  Milestone 1 lives mostly here.
- **Networking/** — lobby/host-join, spawning (MultiplayerSpawner), the tick
  loop, client-side prediction, server reconciliation, lag compensation. Built
  on Godot's MultiplayerApi/ENet as transport+replication ONLY — prediction and
  lag-comp are ours. Server-authoritative, NOT rollback. Prove in Milestone 1.
- **Input/** — hybrid input model: analog movement read + discrete committed
  moves with startup/recovery frames + right-stick gesture recognizer.
  Milestone 3.
- **Ball/** — state-driven ball + custom deterministic mini-physics (arc/bounce/
  rim). NOT Godot Physics/Jolt for owned moments. Self-contained, unit-testable.
  Milestone 2.
- **Systems/** — cross-cutting systems: stamina/resource, scoring/win condition,
  timing windows, and later the server browser / discovery. Built last.

See ../CLAUDE.md for full design + current milestone before writing code.
