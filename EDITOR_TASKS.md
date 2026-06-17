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

---

## Milestone 2 editor tasks — the ball (issue #12)

**Do these only after the `feat/8-ball-mini-physics` PR is merged.** The ball
math is finished and unit-tested (94 tests); this step proves it in a live
scene. Milestone 2 is single-player and local — no networking is involved.

### Step 1 — Build first
Click the **hammer icon**. Godot must see the new `BallController` class before
it appears in the Attach Script list. If the build fails, read the Output panel
and tell Claude Code.

### Step 2 — Add the shoot input action
**Project → Project Settings → Input Map.** Add a new action named exactly
`ball_shoot` and bind a key to it (e.g. the spacebar). The ball script reads
this action to fire a shot. (The name is the `ShootAction` export — change one
to match the other if you prefer a different name.)

### Step 3 — Build the Ball scene
1. New scene, root node type **Node3D**, rename it `Ball`. Save as `scenes/Ball.tscn`.
2. Add a child **MeshInstance3D** with a **SphereMesh**; set its radius to
   ~0.12 (matches the `BallRadius` export). Gray placeholder material is fine.
3. Select the `Ball` root → **Attach Script** → `BallController.cs`.

### Step 4 — Place a hoop and wire the geometry
1. In `Main.tscn`, add a simple visual hoop (a `Node3D` with a torus/cylinder
   mesh for the rim and a box mesh for the backboard) so you can see where the
   basket is. Note its world position.
2. Instance `Ball.tscn` into `Main.tscn`.
3. Select the `Ball` instance and, in the **Inspector**, fill in the exports so
   they match the scene:
   - **Holder** → drag the player node the ball should attach to.
   - **Rim Center** → the world-space centre of your rim mesh (collision geometry).
   - **Shot Target** → where the shot arc aims. Defaults to the same value as Rim
     Center (a clean make). To test a rim bounce, offset this slightly off-centre
     (e.g. add 0.5 to X) while leaving Rim Center unchanged.
   - **Board Center / Board Normal** → centre of the backboard face, and its
     normal pointing toward the court (for a board at +Z, normal is `(0, 0, -1)`).
   - Leave the other tunables (radii, restitution, apex, gravity) at defaults
     unless the feel needs tuning.

### Step 5 — Verify (the acceptance criteria for #12)
Run `Main.tscn` and confirm:
- ✅ The ball is visible and **dribbles** (bounces in a cycle) at the holder.
- ✅ Pressing `ball_shoot` fires the ball in a **parabolic arc** toward the rim.
- ✅ An off-target shot **bounces** off the rim/backboard and the ball goes
  **loose**, then settles on the floor.
- ✅ An on-target shot drops **cleanly through** the hoop (Output prints
  `[Ball] Clean make.`).

When all four are true, **Milestone 2 is proven**. Close issue #12, then the
epic issue #8. (Issues #9, #10, #11 are code-only and close with the PR merge.)

### Troubleshooting

| Symptom | Likely cause |
|---------|--------------|
| Build errors on open | C# classes not found — hit the hammer icon first |
| Lobby appears but Host/Join do nothing | Export fields not assigned in Inspector |
| Only one capsule visible after joining | MultiplayerSpawner not configured (step 2) |
| Remote capsule teleports / snaps | Check Output for `[PlayerController]` errors |
| "Unauthorized SubmitInput" in Output | Node naming mismatch — confirm NetworkManager spawns by peer ID |

---

## Milestone 3 editor tasks — committed moves (issue #18)

**Do these only after the `feat/milestone-3` PR is merged.** The C# must
compile before committed-move input is wired.

### Step 1 — Build first

Click the **hammer icon** (top-right). Godot must see the new classes
(`CommittedMoveMachine`, `RightStickGestureRecognizer`, etc.) before the
new `PlayerController` exports appear in the Inspector.

### Step 2 — Verify input actions exist

**Project → Project Settings → Input Map.** Confirm these six actions are
present (they were added by text-edit to `project.godot` in the PR):

| Action | Binding |
|---|---|
| `aim_right` | Joypad Axis 2 (+1.0, deadzone 0.5) — right stick right |
| `aim_left` | Joypad Axis 2 (−1.0, deadzone 0.5) — right stick left |
| `aim_down` | Joypad Axis 3 (+1.0, deadzone 0.5) — right stick down |
| `aim_up` | Joypad Axis 3 (−1.0, deadzone 0.5) — right stick up |
| `move_crossover` | Keyboard **Q** (keyboard fallback, no gamepad needed) |
| `move_feint` | Keyboard **E** + Joypad L1 button (index 9) |

If any are missing, add them manually in the Input Map tab and save.

### Step 3 — Confirm the BurstSpeed export is wired

In the scene panel, select the Player node. In the Inspector you should see:
- **Burst Speed** = 12 (the default — leave it for now)

If the Inspector shows no committed-move exports, the build from Step 1 did
not succeed. Hit the hammer again.

### Step 4 — Verify the committed-move pipeline (issue #18 acceptance criteria)

Run `Main.tscn` with a single instance:

**Test A — Crossover via keyboard (no gamepad required):**
1. Move the capsule with WASD to confirm normal movement still works.
2. Press **Q**. The capsule should:
   - **Freeze** for ~6 physics ticks (~0.1 s) — this is the startup telegraph.
     The freeze must be visible and readable. If it blinks and you miss it,
     the startup window is too short — raise `StartupFrames` in `Crossover.DefaultFrameData`.
   - **Burst laterally** (to the right) for ~3 ticks (~0.05 s) — the Active phase.
     The burst should cover noticeable ground (default BurstSpeed = 12 m/s).
   - **Decelerate** for ~12 ticks (~0.2 s) — the Recovery phase.
     The capsule should be noticeably slower and still moving right, then stop.
   - **Return to normal** movement. WASD should work again cleanly.
   - ✅ All four stages must be visible. If recovery is invisible, the burst
     velocity isn't being sustained into Recovery (bug: burst wasn't SET before
     Recovery; check git diff on `TickCommittedMoveBehavior`).

**Test B — Feint during startup:**
1. Press **Q** to start the crossover.
2. Immediately (within the first 4 physics ticks ≈ first 0.07 s) press **E**.
3. Expected: the capsule unfreezes immediately, returns to normal movement.
   The burst and recovery do NOT occur.
4. If feint has no effect, the feint-window may have passed — practice pressing
   E sooner after Q. The window is `FeintWindowFrames = 4` ticks.

**Test C — Recovery blocks re-input:**
1. Press **Q** to trigger a full crossover (do NOT feint).
2. During the recovery slide, press **Q** again.
3. Expected: the second Q has no effect. A new crossover only starts after
   the recovery phase fully completes.

**Test D — Gamepad (if a controller is connected):**
1. Flick the **right stick** left or right quickly (less than ~0.07 s hold)
   while keeping the stick deflection past center: this is a **gesture feint**
   (stick returned to deadzone within 4 ticks). Expected: no committed move
   starts (the machine was never Begin()-ed; the quick return is a micro-fake).
2. Flick the **right stick** left or right and hold it past center for ~0.1 s
   (more than 4 ticks): this confirms a crossover. The startup telegraph,
   burst, and recovery should all fire.
3. To feint an in-progress crossover on a gamepad: trigger the crossover with
   the right stick (hold >4 ticks so it confirms), then press **L1** during the
   startup freeze. Expected: freeze ends immediately, no burst.

When all four tests pass, **Milestone 3 is proven**. Close issues #14, #15,
#16, #17, then close the epic #13.

### Troubleshooting M3

| Symptom | Likely cause |
|---|---|
| Q does nothing | `move_crossover` action not in Input Map — add it (Step 2) |
| Capsule freezes but never bursts | `ActiveFrames` = 0 or Crossover.DefaultFrameData wrong |
| Burst fires but recovery instant | `RecoveryFrames` = 0 in DefaultFrameData |
| Feint (E) never works | `FeintWindowFrames` = 0, or pressing E after the window closes |
| Normal WASD broken after crossover | Machine stuck in non-Inactive phase — check Output for errors |

---

## Milestone 4 editor tasks — networked ball + committed moves (issue #22)

**Do these only after PR #36 (`feat/milestone-4`) is merged, or checked out
locally for verification before merge.** The C# must compile before you can
re-wire the changed export.

### Step 1 — Build first

Click the **hammer icon**. Godot must see the updated `BallController` class
(its old `Holder` export was renamed to `Players`) before the Inspector shows
the new field.

### Step 2 — Re-wire the Ball's `Players` export

`Main.tscn`'s `Ball` node was wired to the old `Holder` export, which no
longer exists on the script — that wiring is now stale and the new `Players`
field is empty.

1. Open `Main.tscn`. Select the `Ball` node in the Scene panel.
2. In the **Inspector**, find the **Players** field (under the script's
   exported properties). It should be empty/unassigned.
3. Drag the existing `Players` node (the spawn root, direct child of `Main`
   — the same node `NetworkManager`'s own **Players** export already points
   to) into this field.
4. Save the scene (**Ctrl+S**). This is the same spawn-root node-path pattern
   already used by `NetworkManager` and `MultiplayerSpawner` — the ball now
   resolves its holder at runtime by peer ID instead of a fixed node.

If the Inspector still shows no `Players` field, the build in Step 1 did not
succeed — check the Output panel for errors.

### Step 3 — Verify (issue #22 acceptance criteria)

Open **Debug → Run Multiple Instances → 2**, then run.

- **Window 1 (Host):** click **Host**.
- **Window 2 (Join):** click **Join** (defaults to `127.0.0.1:7777`).

With both windows on the court:

1. **Dribble sync** — move the host's player around. In both windows, confirm
   the ball tracks the host's hand smoothly with no snapping.
2. **Shot sync** — as the current holder, press the shoot input
   (`ball_shoot`, from M2). Confirm in both windows: the ball leaves on a
   parabolic arc, and the arc looks the same in both windows (small
   release-point differences under latency are expected and self-correct —
   see code comment on `BallController.ApplyShootLocally`). Off-target shots
   should bounce off the rim/backboard and go loose; on-target shots should
   print `[Ball] Clean make.`
   - Note (M4 known limit, not a bug to chase): the ball can only be shot
     once per session right now — there's no catch/possession-contest yet
     (future issue). One shot per window is enough to verify sync.
3. **Committed move sync** — on EITHER player, trigger a crossover
   (`move_crossover` / right-stick flick, from M3). Confirm in the OTHER
   window:
   - The startup freeze, lateral burst, and recovery slide all appear,
     matching what the triggering window shows.
   - No visible desync or teleport/snap during any phase.
4. **Punish window consistency** — during a player's Recovery phase (from
   step 3), confirm in both windows that the player cannot start a second
   committed move or otherwise act until Recovery completes. This should be
   true on both the triggering window AND the other window's view of that
   player.

When all four are true, **issue #22 is proven** — close it, then close the
epic #19.

### Troubleshooting M4

| Symptom | Likely cause |
|---|---|
| `[BallController] Players is not assigned` in Output | Step 2 wiring missed or scene not saved |
| Ball stays at world origin / doesn't follow holder | Same as above — `Players` export empty |
| `[BallController] Unauthorized RequestShoot from peer X` | A non-holder tried to shoot — expected if input fired on the wrong window, not a bug |
| Remote crossover never appears on the other window | Confirm both windows are on the merged `feat/milestone-4` build (old client code never sent `RequestBeginMove`) |
| Ball or player snaps instead of smoothing | Check Output for `[BallController]`/`[PlayerController]` reconciliation errors |

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
