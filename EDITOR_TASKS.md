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

## Milestone 1 editor tasks: two networked capsules on a plane

Do these in order. Each is a few clicks. Claude Code tells you the exact node
types and script names as it writes them.

1. **Create the main scene.** Scene → New Scene → add a root node (a `Node3D`).
   Save as `scenes/Main.tscn`.
2. **Add a floor.** Add a `StaticBody3D` with a `CSGBox3D` (or a MeshInstance3D +
   collision) scaled wide and flat as a placeholder ground. No material needed.
3. **Build the player scene.** New Scene → root node `CharacterBody3D`, add a
   child mesh (a capsule mesh) so you can see it, and a `CollisionShape3D`. Save
   as `scenes/Player.tscn` — this is the prefab spawned for each connected player.
4. **Attach the player script.** Select the `CharacterBody3D` → Attach Script →
   choose the C# script Claude Code wrote in `scripts/Player/` (e.g.
   `PlayerController.cs`). Godot links the node to the class.
5. **Add a camera.** Either a `Camera3D` child of the player, or let Claude Code's
   code position it.
6. **Set the main scene to run.** Project → Project Settings → set
   `scenes/Main.tscn` as the main scene (or press F6 to run the current scene).
7. **Test locally first.** Run the scene; confirm one capsule moves with the
   input, and that movement feels immediate (prediction).
8. **Test networking.** Godot can run two debug instances at once (Debug → Run
   Multiple Instances → 2). Follow Claude Code's host/join instructions: one
   instance hosts, the other joins over localhost. Confirm the second capsule
   appears and moves smoothly on both windows.

When step 8 looks right in both windows, Milestone 1 is proven. Tell Claude Code
and move to Milestone 2.

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
