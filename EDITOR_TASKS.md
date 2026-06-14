# EDITOR_TASKS.md — Your Godot editor checklist (the part AI can't do)

Claude Code writes the C# under `scripts/`. **You** do everything in this file,
because it lives in the Godot editor and no AI tool can click for you. For
Milestone 1 it's short and beginner-level, and you'll repeat the same few
actions for a while. Defer everything visual (materials, animation, polish)
until much later — none of it is needed to prove the game works.

---

## One-time setup

1. Download **Godot 4 — .NET / C# edition** (the build labeled .NET/Mono, NOT the
   standard build — only the .NET build runs C#). From godotengine.org.
2. Install the **.NET SDK** (Godot's C# build needs it to compile). Match the
   .NET version Godot's current docs specify.
3. Install an editor for the C# itself — **VS Code** or **Visual Studio** — and
   point Godot at it (Editor Settings → Dotnet → external editor). This is where
   Claude Code's files open.
4. Launch Godot → **New Project** → give it a name and folder → renderer:
   **Compatibility** or **Mobile** (lighter, better for your low-spec target;
   you can change later). This creates `project.godot`.
5. In the editor, create your first C# script once (right-click a node → Attach
   Script → language C#). This makes Godot generate the `.sln` and `.csproj` at
   the project root. **Leave those files where they are.**
6. Put the whole project folder under **Git** (`git init`), and drop in the files
   Claude Code prepared: `CLAUDE.md`, `EDITOR_TASKS.md`, the `scripts/` tree,
   `.gitignore`. Point **Claude Code** at this folder.

---

## Milestone 1a editor tasks (DONE — issue #3, closed)

These were completed when the single-player movement was proven. They are kept
here as reference for the scene structure M1b builds on.

1. Created `scenes/Main.tscn` with a `Node3D` root.
2. Added a `StaticBody3D` floor with a `BoxMesh` + `CollisionShape3D`.
3. Built `scenes/Player.tscn`: root `CharacterBody3D` + `MeshInstance3D` (capsule)
   + `CollisionShape3D`. Script: `PlayerController.cs`.
4. Added a fixed overhead `Camera3D` and a `DirectionalLight3D` to `Main.tscn`.

---

## Milestone 1b editor tasks — two networked capsules (issue #7)

**Do these only after Claude Code has pushed the `feat/networking` branch and you
have merged the PR.** The C# must compile before you wire it.

The overall change to `Main.tscn` is: remove the static Player instance (players
are spawned at runtime now) and add the three networking nodes.

### Step 1 — Build first
Click the **hammer icon** (top-right of the editor). Godot must see the new C#
classes (`NetworkManager`, `Lobby`) before they appear in the Attach Script list.
If there are build errors, read the Output panel and tell Claude Code.

### Step 2 — Update Main.tscn

**Remove the static Player instance:**
In the Scene panel, select the `Player` node that is a direct child of `Main`
(it's there from M1a). Right-click → Delete. Players are now spawned at runtime
by `NetworkManager` — you do NOT need a pre-placed one.

**Add a `Node3D` named `Players` (the spawn root):**
Right-click `Main` in the Scene panel → Add Child Node → `Node3D`. Rename it
`Players`. This is where runtime player nodes will appear.

**Add a `MultiplayerSpawner`:**
Right-click `Main` → Add Child Node → `MultiplayerSpawner`. In the Inspector:
- Set **Spawn Path** to the `Players` node (click the NodePath field, navigate
  to `/root/Main/Players`).
- Under **Auto Spawn List**, click the `+` button and add `res://scenes/Player.tscn`.

This is the critical step: the spawner watches `Players` and replicates any child
added there by the server to all clients automatically. Without this, only the
host ever sees player nodes.

**Add a `Node` named `NetworkManager`:**
Right-click `Main` → Add Child Node → `Node`. Rename it `NetworkManager`.
Attach Script → navigate to `scripts/Networking/NetworkManager.cs`.
In the Inspector, assign:
- **Players** → drag the `Players` node in.
- **Player Scene Path** → leave as `res://scenes/Player.tscn` (pre-filled).

### Step 3 — Build the Lobby scene

Create a new scene: Scene → New Scene → root node `CanvasLayer`. Save as
`scenes/Lobby.tscn`.

Add these children to the `CanvasLayer`:
- `VBoxContainer` (to stack controls vertically — optional but tidy)
  - `LineEdit` — rename to `IpField`; placeholder text: `127.0.0.1`
  - `LineEdit` — rename to `PortField`; placeholder text: `7777`
  - `Button`   — rename to `HostButton`; text: `Host`
  - `Button`   — rename to `JoinButton`; text: `Join`

Attach Script on the root `CanvasLayer` → `scripts/Networking/Lobby.cs`.

In the Inspector for the `CanvasLayer`, assign all five exports:
- **Ip Field** → `IpField` node
- **Port Field** → `PortField` node
- **Host Button** → `HostButton` node
- **Join Button** → `JoinButton` node
- **Network Manager** → the `NetworkManager` node in `Main.tscn`
  (drag from the Main scene's Scene panel — you may need to expand the tree).

### Step 4 — Instance Lobby in Main

Back in `Main.tscn`: right-click `Main` → Instantiate Child Scene → choose
`scenes/Lobby.tscn`. The lobby overlay will now appear when the game runs.

### Step 5 — Verify the proof (issue #7 acceptance criteria)

1. Press **F5** to run. The lobby overlay should appear over the empty court.
2. Now open **Debug → Run Multiple Instances → 2** and run again.
   - **Window 1 (Host):** type nothing (defaults are fine) → click **Host**.
     The lobby disappears; you are on the court with one capsule.
   - **Window 2 (Join):** IP is already `127.0.0.1`, port `7777` → click **Join**.
     The lobby disappears; a second capsule appears in both windows.
3. Move each player in its own window. Confirm:
   - ✅ Your own capsule responds instantly (zero perceptible lag — prediction working).
   - ✅ The other capsule moves smoothly with no rubber-banding or visible snap.
   - ✅ Both windows show both capsules.

When all three are true, **Milestone 1 is proven**. Close issue #7, then close
the epic issue #4. Tell Claude Code and we move to Milestone 2.

### Troubleshooting

| Symptom | Likely cause |
|---------|--------------|
| Build errors on open | C# classes not found — hit the hammer icon first |
| Lobby appears but Host/Join do nothing | Export fields not assigned in Inspector |
| Only one capsule visible after joining | MultiplayerSpawner not configured (step 2) |
| Remote capsule teleports / snaps | Check Output for `[PlayerController]` errors |
| "Unauthorized SubmitInput" in Output | Node naming mismatch — confirm NetworkManager spawns by peer ID |

---

## What to deliberately NOT touch yet

- Materials / shaders — gray placeholder surfaces are fine.
- Animation (the telegraphed wind-ups) — that's Milestone 3+.
- Imported 3D models, sounds, UI polish, menus, dedicated-server export.

Keeping these out keeps your learning surface small while you and the AI prove
the hard systems first.

---

## Two Godot C# gotchas worth knowing on day one

- Node scripts MUST be `partial` classes, or they won't build. Claude Code knows
  this; if a build fails mysteriously, check that first.
- After adding/renaming C# files, you sometimes need to **build** (the hammer
  icon, top-right) before Godot sees the new class in the Attach Script list.
