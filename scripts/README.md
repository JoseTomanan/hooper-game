# scripts/ — C# gameplay (Claude Code's domain)

All C# lives here. Godot doesn't enforce a layout, so this is our convention.
Scenes (`.tscn`) live in `../scenes`. Coding agents author scene/resource files
and wire nodes by direct text edit under [ADR-0011](../docs/adr/0011-claude-authors-scenes.md).
AnimationTree graph authoring and the proven FBX retarget subset also have
headless paths (#87/#267). Human feel judgments remain deferred to #173;
objective verification runs through the harness.

Each script is a `partial` class extending a Godot node type
(e.g. `public partial class PlayerController : CharacterBody3D`).

Subfolders, by responsibility:

- **Player/** — player node components: movement, state, camera rig.
  Includes the held analog sprint modifier shipped in
  [#325](https://github.com/JoseTomanan/hooper-game/issues/325) (closed 2026-10-11)
  and deterministic predicted contact shipped in
  [#370](https://github.com/JoseTomanan/hooper-game/issues/370) (closed 2026-10-11).
- **Networking/** — lobby/host-join, spawning (MultiplayerSpawner), the tick
  loop, client-side prediction and server reconciliation. Built
  on Godot's MultiplayerApi/ENet as transport+replication ONLY — prediction and
  reconciliation are ours. Contact replay uses the latest raw opponent snapshot;
  historical opponent snapshots/lag compensation remain an unbuilt upgrade
  (ADR-0025). Server-authoritative, NOT rollback. Dedicated discovery,
  browser join and a full five-point production game have automated evidence
  from [#374](https://github.com/JoseTomanan/hooper-game/issues/374)/
  [#375](https://github.com/JoseTomanan/hooper-game/issues/375); exported-build
  and cross-machine verification remain open on #32/#366.
- **Input/** — hybrid input model: analog movement read + discrete committed
  moves with startup/recovery frames + right-stick gesture recognizer.
  Sprint belongs to continuous movement, not the committed-move state machine.
- **Ball/** — state-driven ball + custom deterministic mini-physics (arc/bounce/
  rim). NOT Godot Physics/Jolt for owned moments. Self-contained, unit-testable.
  The winning basket preserves terminal holder 0 (#373, closed 2026-09-11).
- **Systems/** — cross-cutting gameplay systems, including scoring/win condition
  and defensive timing windows. Stamina/resource work remains deferred under
  the explicit M11 hold; server browser/discovery are already built.

See ../AGENTS.md for design identity, current milestone, and repo conventions.
Read all ADRs in ../docs/adr/ before writing engine-facing code — they record
the locked decisions on engine, networking, input, ball physics, and community
model.
