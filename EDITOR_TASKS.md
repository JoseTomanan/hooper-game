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

## Milestone 5 editor tasks — scoring + win condition (issue #27)

All M5 C# is committed (issues #24/#25/#26). What remains is human-only scene
wiring. Score state, win condition, the broadcast, and the freeze are all in
code; the editor just needs to put two nodes in the scene. **No NodePath
exports to drag this time** — `BallController`, `PlayerController`, and the HUD
all find `GameManager` automatically through the `game_manager` group it joins
itself in `_Ready`, so the only requirement is that the node *exists* in
`Main.tscn`.

### Step 1 — Build first

Build (hammer icon, top-right) so Godot sees the new `GameManager` and
`ScoreHud` classes in the Attach Script list. (See the C# gotcha at the bottom
of this file if they don't show up.)

### Step 2 — Add the GameManager node

1. In `Main.tscn`, add a plain **`Node`** (NOT `Node3D` — it has no transform)
   as a child of the root. Name it `GameManager`.
2. Attach `scripts/Systems/GameManager.cs` to it.
3. (Optional) In the Inspector, set **Target Score** if you want something
   other than the default of 11.

### Step 3 — Add the score HUD

1. Add a **`CanvasLayer`** under the root (so the HUD draws on top of the 3D
   court and ignores the camera), then a **`Label`** under that CanvasLayer.
   Position/anchor the Label wherever you like — a corner is fine, this is
   functional-only.
2. Attach `scripts/Systems/ScoreHud.cs` to the Label.
3. Save the scene.

### Step 4 — Verify (issue #27 acceptance criteria)

Run two debug instances (the same Host / Join flow as M4). Then:

1. **Score visible in both instances** — both windows show `You: 0  Opponent: 0`
   on connect. ("You" is always the local player; the host's opponent is the
   client and vice-versa.)
2. **Score increments on a bucket** — shoot a clean make (an on-target shot;
   see the M4 note about one shot per session). The scorer's `You:` increments
   in their window and their `Opponent:` increments in the other window. The
   Output log should NOT show any `No node in group 'game_manager' found`
   errors from `BallController`/`PlayerController`/`ScoreHud` — if it does,
   Step 2/3 was missed or the scene wasn't saved.
3. **Win condition triggers** — lower **Target Score** to 1 in the Inspector
   (so a single make ends the match) for this test. After the make, BOTH
   players stop moving (game-over freeze) and each window shows
   `You win!` / `You lose.` according to who scored. Reset Target Score to 11
   (or your preference) afterward.

When all three are true, **issue #27 is proven** — close it, then close the
epic #23.

### Troubleshooting M5

| Symptom | Likely cause |
|---------|--------------|
| `No node in group 'game_manager' found` in Output | GameManager node missing from `Main.tscn`, or scene not saved (Step 2) |
| HUD reads `(no GameManager)` | Same as above — the Label loaded before any GameManager existed |
| Score never changes after a make | Only a *clean* make scores; an off-target shot bounces (loose) and is not a basket — aim at the rim centre |
| One window's score updates but not the other | Score broadcast (`ReceiveScoreState`) didn't reach the peer — confirm both windows are on this build |
| Players never freeze on game-over | Target Score not reached, or GameManager missing on the *server* window |

---

## Milestone 6 editor tasks — dedicated server + server browser

**Do these only after the `feat/milestone-6` PR is merged, or checked out locally
for verification before merge.** M6a acceptance is NOT the two-window flow from M1's
step 5 — see "Step 5" below for the M6a-specific procedure and what can't be proven
on a single machine.

### Step 1 — Build first

Click the **hammer icon**. Godot must see the new classes
(`DedicatedServerBootstrap`, plus the discovery/browser classes) before they
appear in the Attach Script list and the Inspector. If the build fails, read the
Output panel and tell Claude Code.

### Step 2 — Add the DedicatedServerBootstrap node to Main.tscn

This node is what makes the *same* Main.tscn run as a headless dedicated server
when launched with `--dedicated` — and stay a normal client/host otherwise.

1. Open `Main.tscn`. Right-click `Main` → Add Child Node → `Node`. Rename it
   `DedicatedServerBootstrap`.
2. **Attach Script** → `scripts/Networking/DedicatedServerBootstrap.cs`.
3. In the **Inspector**, assign its two exports:
   - **Network Manager** → drag in the `NetworkManager` node (sibling in Main).
   - **Lobby** → drag in the `Lobby` instance (sibling in Main).

Then add the **DiscoveryBroadcaster** (so the server advertises itself on the LAN):

4. Right-click `Main` → Add Child Node → `Node`. Rename it `DiscoveryBroadcaster`.
5. **Attach Script** → `scripts/Networking/DiscoveryBroadcaster.cs`.
6. In the **Inspector**, assign:
   - **Network Manager** → the `NetworkManager` node.
   - **Players** → the `Players` spawn root (same node `NetworkManager.Players`
     points at).
   - **Server Name** → optional; defaults to "Hooper Server".
7. Save the scene (**Ctrl+S**).

On a normal launch (no `--dedicated`) the bootstrap does nothing and the Lobby
behaves exactly as before; the broadcaster only starts advertising once a server
actually comes up (Host or `--dedicated`). Confirm now: press **F5** — the lobby
should still appear and Host/Join still work.

### Step 2b — Build the server browser scene (Server Browser, ADR-0007)

This is the discovery-driven join UI. It lists servers found on the LAN and joins
the one you pick — no IP typing.

1. **Scene → New Scene** → root node `CanvasLayer`. Save as `scenes/ServerBrowser.tscn`.
2. Add a child **ItemList** (rename it `ServerListUi`). Size/position it so rows are
   visible; leave it empty (rows are added at runtime).
3. Add a child **Node** and **Attach Script** →
   `scripts/Networking/DiscoveryListener.cs`. (This is the UDP listener the browser
   reads from.)
4. Select the root `CanvasLayer` → **Attach Script** →
   `scripts/Networking/ServerBrowser.cs`.
5. In the root's **Inspector**, assign:
   - **Discovery** → the `DiscoveryListener` child you just added.
   - **Network Manager** → the `NetworkManager` node in `Main.tscn` (drag from the
     Main scene tree — you may need both scenes open, or instance the browser into
     Main first, then assign).
   - **Server List Ui** → the `ServerListUi` ItemList child.
6. Back in `Main.tscn`: right-click `Main` → Instantiate Child Scene →
   `scenes/ServerBrowser.tscn`. Position it where you want the list to appear
   (e.g. beside or below the Lobby). Save.

To **use** it: run a client, and discovered servers appear in the list. Double-click
a row (or select + Enter) to join. On success the browser hides, same as the Lobby.

### Step 3 — Create the dedicated-server export preset

You need an exported binary to run the server headlessly.

1. **Project → Export…** → **Add…** → choose your desktop platform
   (**Windows Desktop**, or **Linux** if that's your server box).
2. *(Recommended, optional)* In the preset, there is a **dedicated server** option
   that strips textures/materials and sets the `dedicated_server` feature tag,
   shrinking the build. Either the dedicated-server preset OR a normal desktop
   export works — a normal export just needs the `--headless` flag at runtime.
   Source: docs.godotengine.org/en/stable/tutorials/export/exporting_for_dedicated_servers.html
3. Export the project to a path you'll remember (e.g. `build/server/`). You may
   need to install export templates the first time (Godot will prompt).

### Step 4 — Run the server headless and the clients

From a terminal, launch the exported server with no display:

```
"HOOPER GAME.exe" --headless -- --dedicated --port 7777
```

- `--headless` is the **engine** flag (no window, no audio) — it must come BEFORE
  the `--` separator.
- `--dedicated` and `--port` are **our** args — they come AFTER `--`.
- The bootstrap also accepts the looser `--headless --dedicated --port 7777`
  (without the `--`), but the form above is the canonical one.

Expected server log lines (no window opens):
- `[DedicatedServerBootstrap] --dedicated detected; starting headless server on port 7777`
- `[NetworkManager] Server up on port 7777`
- `[NetworkManager] Dedicated server running headless; awaiting clients.`

Now launch **two** clients (from the editor via *Debug → Run Multiple Instances → 2*,
or two copies of a normal export). In each, type the server's IP in the Lobby and
press **Join** (on one machine use `127.0.0.1`).

### Step 5 — Verify (M6a acceptance — read which parts are single-machine-provable)

**Provable on ONE machine:**
1. The headless server starts, logs **0 players**, opens no window, and keeps
   ticking.
2. Two clients **Join by IP** → both capsules appear on both clients, the ball's
   tipoff goes to a real client (it does NOT sit frozen at the court centre/origin),
   and M4 movement/ball/committed-move sync still works.
3. The server browser (wired in Step 2b) **may** list the local server,
   depending on whether your OS loops the LAN broadcast back to the same machine.
   If it appears, double-clicking the row should join it — proving the
   discovery→JoinGame handoff end to end on one box.

**NOT provable on one machine — needs a second LAN box (leave unproven until then):**
- A server on machine A appearing in the browser on machine B (true LAN discovery).
- Two clients each running their own discovery listener on the same machine — they
  contend for the UDP discovery port (7777-adjacent 7778); use ONE browser client
  per machine when testing locally.

When the single-machine items pass, M6a's *server + headless + join-by-IP* spine is
proven; the *cross-machine discovery* bar stays open until you have a second
machine on the LAN. Tell Claude Code which items you confirmed.

---

## Milestone 6b editor tasks — possession loop (issues #51, #52)

All M6b gameplay is server-authoritative C# and adds **no required scene wiring**
beyond M5 — make-it-take-it, the live rebound, and the take-it-back/clear rule
all run inside `BallController` and reconcile over the existing broadcast. The
two human steps are: wire the possession HUD label (#51), and play a full game
to verify the loop (#52).

### Step 1 — Build first

In the editor, **Build** (top-right) so the new `PossessionHud` class appears in
the Attach Script list and the new `BallController` exports show in the
Inspector. (See the C# gotcha at the bottom if the class list looks stale.)

### Step 2 — Add the possession HUD (issue #51, hitl)

1. In `Main.tscn`, under the same CanvasLayer/HUD node that holds the M5 score
   `Label`, add a second **Label** node (name it e.g. `PossessionHud`).
2. Position it where it won't overlap the score (e.g. top-centre, or just under
   the score line).
3. Attach `scripts/Systems/PossessionHud.cs` to it.
4. No exports to wire — it finds the ball via the `"ball"` group at runtime,
   the same way `ScoreHud` finds `GameManager`.

### Step 3 — (Optional) tune the possession exports

On the **Ball** node in `Main.tscn`, two new Inspector exports tune the feel —
defaults are sensible, adjust only if play-testing wants it:

- **Pickup Radius** (default `1.0` m) — how close a player must be to recover a
  loose ball.
- **Clear Line Distance** (default `5.8` m) — floor-plane distance from the hoop
  the handler must reach to clear a possession before a basket counts.

> Note: the clear line is measured from `Rim Center`. If your hoop is not at the
> world origin, the clear ring follows the hoop automatically (it's radial), but
> sanity-check that `5.8` m actually sits near your court's top-of-key given the
> court scale you authored.

### Step 4 — Verify (issue #52 acceptance criteria, hitl)

Run two instances (host + client, the M4/M5 dual-instance flow). Set the
`GameManager` **Target Score** to something > 1 (e.g. `5`) first. Then confirm,
watching both windows:

1. **Make-it-take-it** — after a made basket the scorer keeps the ball (it does
   not die on the floor); the score increments on both windows.
2. **Live rebound** — a missed shot leaves the ball loose; whichever player is
   nearer recovers it and can dribble/shoot again. The `PossessionHud` flips to
   the new holder on both windows.
3. **Take-it-back / clear** — right after a make or a rebound the HUD shows
   "Take it back"; a shot made *before* carrying the ball back past the clear
   line does **not** score and the ball turns over; once the handler has cleared
   (HUD shows "Cleared"), a make counts.
4. **Game-over** — play to the target score; the game freezes and the win/lose
   line shows, exactly as M5.

When all four hold across both windows, **#52 is proven** — close it, then close
**#51** (its HUD step is verified here too). Closing #52 lets you proceed to the
M6a dedicated-server verification (#32), which reuses this same completed loop.

---

## Issue #46 additions — court bound walls + visual indicators (hitl)

These steps implement the physical walls that keep players on the court.  The
in-court visuals (clear-line arc, court-bound outline) are built in code by
`CourtVisuals.cs` and need no editor authoring — they appear automatically once
the scene runs.  Only the four wall `StaticBody3D` nodes require your hands.

### Step 1 — Build first

Build in the editor so `CourtVisuals` and the new `BallController` exports
(`CourtMin`, `CourtMax`, `MadeFlashDuration`) appear.

### Step 2 — Verify the procedural indicators appear (no wiring needed)

Run a single instance (F5).  Confirm:

- A flat ring on the court floor at roughly **5.8 m** from the hoop — this is
  the clear-line arc.  It starts **red** (uncleared) once someone holds the ball
  and turns **green** once they carry it behind the ring.
- Four faint white box outlines tracing the court rectangle.

If the ring radius looks wrong for your court scale, adjust **Clear Line
Distance** on the Ball node in the Inspector.

### Step 3 — Place the four half-court walls (hitl)

The ball is clamped in code (`CourtBounds.Clamp` in `TickLoose`); the walls are
for **players** (CharacterBody3D + MoveAndSlide handles them automatically).

For each of the four sides, add a `StaticBody3D` + `CollisionShape3D`
(`BoxShape3D`) directly under the `Main` root in `Main.tscn`.  Suggested layout
matching the default `CourtMin (-4.88, -1.0)` / `CourtMax (4.88, 11.88)` exports:

| Side | Position | Box Size |
|------|----------|----------|
| Near wall (−Z) | `(0, 1, −1.0)` | `(11, 2, 0.2)` |
| Far wall (+Z)  | `(0, 1, 11.88)` | `(11, 2, 0.2)` |
| Left wall (−X) | `(−4.88, 1, 5.44)` | `(0.2, 2, 14)` |
| Right wall (+X) | `(4.88, 1, 5.44)` | `(0.2, 2, 14)` |

> **Matching the code clamp:** the `CourtMin`/`CourtMax` exports on the Ball node
> are the single source of truth for the ball's XZ bound.  Your wall positions
> must match those numbers — if you resize the court or adjust the exports, move
> the walls to match (and vice versa).

### Step 4 — Verify (hitl)

Run two instances.  Confirm:

- Players **cannot walk off the floor** — all four walls stop them.
- A **loose ball** cannot roll past the court edge — it stops at the bound line
  and rests there (the clamp runs in code).
- A **made basket** flashes the ball **green for ~1 second**, then returns to
  orange.  Uncleared makes that turn over do **not** trigger the flash.
- The **clear-line arc** flips red → green as the holder carries the ball back;
  after a *make-it-take-it* hand-back the scorer starts already cleared (no
  forced walk-back); after a **rebound** or **turnover** they start red.

When all four hold, **close issue #46** (the epic).

---

## Milestone 7a editor tasks — static readability pass (issue #53)

**Do these only after the M7a PR has merged.** All changes are visual/cosmetic —
collision, authoritative state, and netcode are unchanged (ADR-0002/ADR-0004).
Epic **#53**; sub-issues #38 (mesh swap), #39 (facing + lean), #40 (shadows).

### Step 1 — Build first

Click the **hammer icon** (top-right). Godot must see the new `VisualRoot`
export on `PlayerController` before it appears in the Inspector. If the build
fails, read the Output panel and tell Claude Code.

### Step 2 — Issue #38: import a humanoid mesh and wire VisualRoot

**Asset sourcing:** Choose a CC0/public-domain humanoid model in glTF/.glb format.
Recommended free sources (verify the license before use — record source + license
in the PR):
- **Quaternius** (quaternius.com) — CC0, many humanoid packs
- **Kenney** (kenney.nl) — CC0, simple stylized characters

A static mesh is fine — no rig, no animations. Keep it simple.

**Import steps:**
1. Drop the `.glb` file into `assets/` (create the folder if needed).
2. In Godot's **FileSystem** panel, select the `.glb` and set its import mode
   to **Scene** (the default). Click **Reimport** if prompted.
3. In `scenes/Player.tscn`:
   - Open the scene in the editor.
   - Right-click the `CharacterBody3D` root → **Instantiate Child Scene** and
     pick the imported `.glb`. This places the humanoid mesh as a child of the
     player body.
   - Select the `CharacterBody3D` root. In the **Inspector**, find **Visual Root**
     (under the script's exported properties) and point it to the newly added
     humanoid root node (the glTF scene root, not any particular bone).
   - **Scale + position:** ensure the mesh's feet sit at or just above Y=0 in
     the player's local space (capsule base). If the mesh arrives at a different
     scale or with its origin at the hips, adjust its `Transform` → `Scale` and
     `Position` until the feet align with the capsule base. Wrong origin causes
     the shadow to read incorrectly and placement to look off.
   - The original `MeshInstance3D` capsule visual can be hidden (`Visible = false`)
     or deleted now that the humanoid replaces it. The `CapsuleShape3D` collision
     must remain untouched.
4. Save the scene (**Ctrl+S**).

**Verify:** Run a single instance. The humanoid mesh should appear on the court
with no physics change. Under the two-instance flow, verify **smooth correction
still hides snaps** — wiggle under latency (move on the client, confirm no visible
teleport on either window). If snapping re-appeared, the `VisualRoot` export is not
pointing to the right node.

### Step 3 — Issue #39: confirm facing + burst lean (dual-instance)

Run two instances (host + client). On either player:
1. **Facing:** move in any direction with WASD/left-stick. The humanoid mesh should
   rotate to face the movement direction and hold the last facing when stationary.
   - If the mesh faces the *opposite* direction: the `Atan2(velocity.X, velocity.Z)`
     sign needs flipping. Tell Claude Code — this is hitl sign-off territory.
   - If it snaps around jerkily: the `SpeedEpsilon` guard in `FacingResolver` may
     need tuning. 0.1 m/s is the default.
2. **Burst lean:** trigger a crossover (Q / right-stick flick). During the Active
   phase burst, the mesh should lean laterally ~12° toward the burst direction.
   - Lean must feel **grounded** — weighted, not floaty or exaggerated (ADR-0003
     bounds). If 12° is too subtle or too much, ask Claude Code to adjust
     `LeanRadians` in `LeanResolver.cs`.
   - No lean during Startup (telegraph) or Recovery (punish) — those phases must
     stay upright.
3. Check the remote player's window: facing and lean should match (they derive from
   the position/velocity stream already synced — no new netcode).

### Step 4 — Issue #40: tune directional shadows

`shadow_enabled = true` was pre-set on the `DirectionalLight3D` by the PR.
Shadow bias/softness/angle require visual tuning in the editor.

**Tuning target (Mobile renderer — ADR-0006, D3D12):**
- `shadow_bias` and `shadow_normal_bias`: start at Godot's defaults (0.1 / 2.0)
  and raise if you see **shadow acne** (dark speckles on surfaces). Lower if you
  see **peter-panning** (shadows detaching from objects).
- Light angle: shadows fall near-under objects at the current 45° angle. Adjust
  if placement legibility needs it.
- Verify: players, ball, rim, and backboard all have visible ground shadows.
- **Ball in air:** the ball's shadow should stay on the floor (that's the
  blob-shadow `BlobShadow.cs` node already on the ball). The new directional
  shadow adds a second shadow during flight — check whether this reads well or
  causes a confusing double-shadow. See the overlap note below.

**⚠️ Overlap with blob shadows:** The blob-shadow system (`scripts/Systems/BlobShadow.cs`,
added in commit a4c22ee) already projects a disc shadow for the player and ball onto
the floor plane. Enabling directional shadows creates a second shadow for every object.
You need to decide one of:
- **Keep both** — blob shadow gives a consistent "grounding disc" + directional shadow
  adds realistic depth. May look doubled.
- **Disable blob shadows** — remove the `Shadow` node from `Player.tscn` and the
  equivalent from `Ball.tscn`, letting directional shadows do the work alone.
  May lose the altitude-gap read for the ball in the air.
- **Disable directional shadows** — revert `shadow_enabled` to the default (false),
  keeping blobs as the sole grounding system. Blob system has no altitude gap.
Whichever you pick, tell Claude Code so it can update the scene accordingly.

### Step 5 — Verify acceptance criteria (issue #53)

Across a dual-instance run:
- ✅ Player reads as a humanoid that faces its movement direction
- ✅ Crossover burst produces a visible lean into the move during Active phase only
- ✅ No lean during Startup (telegraph window) or Recovery (punish window)
- ✅ Smooth correction still hides snaps (no teleport/flicker under latency)
- ✅ Collision shape and netcode/prediction behavior unchanged from M5
- ✅ Grounding (shadows or blob) reads clearly; ball altitude gap visible
- ✅ `dotnet test tests/Hooper.Ball.Tests` shows 0 failed (green gate)

When all pass, close **#38** and **#39** (after human confirms), and **#40** once
the shadow choice is resolved. Then close the epic **#53**.

### Green-before-merge gate (issue #37)

`PlayerController`'s movement math (`MovementMath`, issue #37) and prediction
bookkeeping (`PredictionBuffer`, issue #55) were extracted into pure, headless-
testable classes specifically so M7a's visual changes have a regression net —
a humanoid mesh swap or a cosmetic lean must not be able to silently break
movement or reconciliation. Before any M7a PR merges, run:

```
dotnet test tests/Hooper.Ball.Tests
```

It must show **0 failed** (currently 250 tests). This is a code-only check —
no editor steps required — but it's gating, not optional: if it's red, the PR
doesn't merge regardless of how the visual change looks in the editor.

---

## Milestone 7b editor tasks — rigged humanoid animation pass (issues #68, #41, #69, #73, #74)

**Do these only after the M7b PR (`feat/54-anim-integration`) has merged.** All the
C# is already written and unit-test-covered (`MoveAnimResolver`, `DisplayPhaseResolver`,
`HandSideResolver`, `JumpShotReleaseResolver`) — what's missing is the AnimationTree
itself, which only the editor can author. Epic **#54**. Sub-issues in dependency
order: #68 (rig + locomotion blend, this section's bulk) → #41 (placeholder
committed-move states — same AnimationTree, mostly just adding 3 more nodes) →
#69 (remote-phase display — already code-complete; its "editor task" is just the
dual-instance verification at Step 5) → #73 (ball-on-hand — Step 6, pure
verification, no new nodes; the lateral offset is an export with a working
default) → #74 (jump shot as a committed move — Step 7, also pure verification:
it reuses the SAME Startup/Active/Recovery states Step 4 already builds and the
SAME `ball_shoot` input action from Milestone 2, so there is nothing new to wire).

### Step 1 — Build first

Click the **hammer icon**. Godot must see the new `AnimationTreePath` export on
`PlayerController`, plus `MoveAnimResolver`/`DisplayPhaseResolver`, before the
Inspector shows the new field. If the build fails, read the Output panel and
tell Claude Code.

### Step 2 — Issue #68: get idle/run animations onto the rig

**Source:** `Documents/kenney_animated-characters-protagonists/Animations/idle.fbx`
and `run.fbx` (sibling folder to this repo, CC0 license) — same skeleton as the
`characterMedium.fbx` already imported for M7a, so this should be a clean
retarget with no rebinding. Ignore `jump.fbx` in that folder — no jump mechanic
exists in this game.

1. Copy `idle.fbx` and `run.fbx` into `assets/`.
2. Select each in the **FileSystem** dock. In the **Import** tab, confirm
   **Import As: Scene** (the default for an FBX with mesh/skeleton data) and
   that animation import is enabled. Click **Reimport** if prompted.
   - ⚠️ **Known Godot quirk:** some asset packs ship animation-only FBX files
     with no skeleton node, which blocks the "save as Animation Library" option.
     If idle.fbx/run.fbx import with NO skeleton (just an AnimationPlayer), that's
     this quirk — the animations are still usable, just via a different route:
     instance the imported scene as a temporary child anywhere in a scratch
     scene, copy the AnimationPlayer's animation resource out (drag from its
     Animation panel into the FileSystem dock to save it as a standalone `.res`),
     then delete the scratch instance. If this happens and the steps below don't
     match what you see, stop and tell Claude Code what the Import tab actually
     shows — this is exactly the "Godot API churn" risk CLAUDE.md flags.
3. In `scenes/Player.tscn`, select the humanoid model node (the M7a
   `CharacterModel` child holding `characterMedium.fbx`). Add a child
   **AnimationPlayer**, name it `AnimationPlayer`.
4. Get the idle/run clips into that AnimationPlayer's animation list — open the
   **Animation** panel (bottom dock) with `AnimationPlayer` selected, use
   **Animation → Manage Animations** (or the equivalent load/import button) to
   pull in the clips from the imported idle.fbx/run.fbx. You should end up with
   two playable animations, e.g. named `idle` and `run`, on this one
   AnimationPlayer.

### Step 3 — Issue #68: build the AnimationTree + locomotion blend

1. Still under the humanoid model node, add a child **AnimationTree**, name it
   `AnimationTree`.
2. In the Inspector, set its **Anim Player** property to the `AnimationPlayer`
   you just built (NodePath to the sibling node).
3. Set **Tree Root** to **New AnimationNodeStateMachine**. Double-click the
   state machine resource to open its graph editor (opens in the bottom
   **AnimationTree** panel).
4. Right-click in the graph → **Add Node** → choose **BlendSpace1D** (not "Add
   Animation" — this one needs to blend two clips, not play one). Rename this
   node **`Locomotion`** — the name must be exactly this; `PlayerController`
   reads `"parameters/Locomotion/blend_position"` literally.
5. Double-click the `Locomotion` node to open its blend-space editor. Add two
   animation points:
   - Position **0** → `idle`
   - Position **6** → `run` (this must equal the Player's **Move Speed** export,
     6.0 by default — see the gotcha below if you ever change MoveSpeed).
6. Back in the state machine graph, drag from the **Start** marker to
   `Locomotion` so it's the entry state.
7. Select the `AnimationTree` node → in the Inspector, check **Active**. (The
   code also force-enables this in `_Ready`, but starting it true avoids a blank
   first frame before `_Ready` runs.)
8. On the **Player** node (`CharacterBody3D` root of `Player.tscn`), find the
   **Animation Tree Path** export (under PlayerController's exported
   properties) and point it at the `AnimationTree` node you just built.
9. Save the scene.

> **Gotcha — keep MoveSpeed and the blend-space Max in sync.** The blend
> position is fed from raw horizontal speed (m/s), and the `Locomotion`
> BlendSpace1D's run point is a fixed number you typed in Step 5. If you ever
> tune `MoveSpeed` on the Player node, also move that BlendSpace1D point to
> match — otherwise the run blend will cap out early (or never reach full run)
> and the animation will look wrong relative to actual speed. This is the same
> kind of manual-sync gotcha as the court-bound walls vs. `CourtMin`/`CourtMax`
> earlier in this file.

**Verify (single instance):** Run `Main.tscn`. Standing still, the humanoid
should hold an idle pose. Moving with WASD should blend smoothly into a run —
no popping or T-pose. If it doesn't move at all, double-check **Active** is on
and **Animation Tree Path** is actually assigned (Step 7/8).

### Step 4 — Issue #41: add the committed-move placeholder states

Same `AnimationTree` graph from Step 3 — this just adds three more nodes to it.
There is **no bespoke crossover clip yet** (that's #70, filed under M8) — these
are explicitly placeholder poses using clips you already have from Step 2.

1. Reopen the state machine graph (double-click `Tree Root` in the Inspector,
   or it may still be open in the bottom panel).
2. Right-click → **Add Animation** three times, creating three new state nodes.
   Rename them exactly **`Startup`**, **`Active`**, **`Recovery`** — these
   names are a hard contract with the code: `PlayerController.ApplyAnimation`
   calls `Travel(target.ToString())` where `target` is a `MoveAnimState` value,
   so a typo here means the Travel() call silently fails to find a path.
3. Assign a placeholder clip to each (recommended, since no crossover clip
   exists yet — swap these later for #70's real clip with no code change):
   - **Startup** → `idle` (reads as a frozen wind-up — fits the "movement
     locked, telegraph window" design intent even as a placeholder).
   - **Active** → `run` (reads as motion during the burst).
   - **Recovery** → `idle` (reads as settling/decelerating).
   You're free to pick differently — these are just the lowest-effort choices
   that need zero new assets and aren't thematically wrong.
4. **Connect every state to every other state, both directions** (6 arrows
   total among the 4 states: Locomotion↔Startup, Locomotion↔Active,
   Locomotion↔Recovery, Startup↔Active, Startup↔Recovery, Active↔Recovery).
   This is deliberately over-connected: `Travel()` only follows existing
   transition arrows, and the display-phase code (#69) can jump between any
   two of these states depending on what the broadcast says, so a missing arc
   would make `Travel()` silently fail to switch for that one transition.
5. Select each new transition arrow → in the Inspector, set **Switch Mode** to
   **Immediate** (not "At End" or "Sync"). The phase transitions are driven by
   exact physics ticks in code (`MoveFrameData`'s 6/3/12-frame Crossover
   timing) — Immediate is what makes the animation cut land on the same tick
   the phase actually changes, which is what makes Startup's telegraph and
   Recovery's punish window legible (ADR-0003) rather than a half-second behind.
6. Save the scene.

**Verify (single instance):** Trigger a crossover (Q / right-stick flick).
Confirm you see a **distinct** Startup pose during the freeze, a different pose
during the burst, and a third during recovery — even though they're placeholder
clips, the three phases should be visually distinguishable from each other and
from idle/run. If nothing changes during a crossover, check the transition
arrows from Step 4 — `Travel()` found no path and silently no-oped.

### Step 5 — Issue #69: verify the opponent's commitment renders (dual-instance)

This is the load-bearing fix and the actual done-bar for M7b — all its code
(`DisplayPhaseResolver`, the `ApplyCosmetics`/`ApplyAnimation` role-aware read)
is already merged; this step is purely verification, no further wiring.

Run **Debug → Run Multiple Instances → 2** (Host + Join, same flow as M4/M5/M6b).

1. On **either** window, trigger a crossover.
2. Watch the **OTHER** window's view of that same player. Confirm:
   - The Startup → Active → Recovery animation states from Step 4 play there
     too, in sync with the triggering window (not stuck on Locomotion).
   - The burst **lean** (M7a, revived by #69) tilts on the opponent too — this
     was silently dead before #69 (the opponent's local `_machine` never
     advanced, so it was always reading Inactive).
3. Confirm idle/run locomotion still reads correctly for both players in both
   windows (Step 3's blend, now also proven over netcode).

When all three hold, **#69 is proven** — close it, then close **#41** (after
confirming Step 4's single-instance check too), then close **#68**. Continue to
Step 6 (#73) and Step 7 (#74) below before closing the epic.

### Step 6 — Issue #73: verify the ball switches hands (single + dual-instance)

All the code is already merged — `HandSideResolver` (pure, unit-tested) and
`BallController`'s lateral offset in `TickHeld`/`TickDribbling`. Nothing new to
build; this is sign-off, same as Step 5 was for #69.

1. Run `Main.tscn` solo. Dribble in place, then trigger a crossover (Q /
   right-stick flick). Confirm the ball visibly sits on one side of the
   player before the move and switches to the other side exactly on the
   burst (same tick as the lean/burst, not before or after).
2. The exact offset distance (`HandOffset`, default 0.18m) and which literal
   side reads as "right" are hitl visual sign-off — if the ball overlaps the
   body or sits unrealistically far out, tune `HandOffset` on the **Ball**
   node's Inspector (no code change needed) and re-test.
3. **Dual-instance** (`Debug → Run Multiple Instances → 2`): trigger a
   crossover on one window, confirm the ball's hand-switch is visible on the
   **other** window's view of that player too — this rides the same
   `DisplayMove()` path #69 proved in Step 5, so if Step 5 passed and this
   doesn't, the bug is novel to #73's wiring, not a repeat of the #69 gap.
4. Confirm a NEW possession (rebound, turnover, make-it-take-it) resets the
   ball to its default hand rather than carrying over the previous holder's
   last hand side.

When all four hold, **#73 is proven** — close it.

### Step 7 — Issue #74: verify the jump shot as a committed move

All the code is already merged — `JumpShot`, `JumpShotReleaseResolver` (pure,
unit-tested), and the shoot-input rewire in `PlayerController`/`BallController`.
This REPLACES the old instant shot: pressing shoot no longer releases the ball
immediately.

1. Run `Main.tscn` solo. Get the ball, then press the shoot button (`ball_shoot`
   action from Milestone 2's Input Map — unchanged, nothing new to add).
   Confirm:
   - Movement locks immediately (same "planted" feel as a crossover's Startup).
   - The ball stays in hand through a visible wind-up (~0.3s at default
     tuning) — it must NOT leave the hand on the button press itself.
   - The ball releases (transitions to its shot arc) at the END of the
     wind-up, then movement stays locked through a brief recovery
     (~0.33s) before you can move or shoot again.
   - The Startup/Active/Recovery poses from Step 4 play during the shot —
     same states the crossover uses (no bespoke jump-shot pose exists yet;
     that's art, M8). This is fine and expected, not a bug.
2. Confirm you **cannot** begin a shot while a crossover (or another shot) is
   already running, and vice versa — `Begin()` enforces this for free; if it
   ever doesn't, that's a real bug, not a tuning issue.
3. **Dual-instance**: trigger a shot on one window, confirm the wind-up is
   visible on the **other** window's view of that player (the defender's read
   window, ADR-0003) before the ball actually leaves the hand there too.
4. The exact frame counts (`JumpShot.DefaultFrameData`: 18 startup / 4 active /
   20 recovery) are editor-tunable balance surface, not load-bearing — if the
   windup feels too long/short, ask Claude Code to adjust the constructor's
   defaults rather than hand-editing frame counts in the Inspector (there is
   no export for them today; they're construction-time only).

When all four hold, **#74 is proven** — close it, then close the epic **#54**.
Leave the M8 bespoke-crossover-clip follow-up issue (filed under #61) open —
it replaces the Step 4 placeholder clips later with no code change. Also leave
#76/#77 (ball-hand-as-steal-surface, pump-fake) alone — both are explicitly
deferred to M9, not part of this epic's done-bar.

### Troubleshooting M7b

| Symptom | Likely cause |
|---|---|
| Humanoid doesn't animate at all, stays in bind pose | `AnimationTreePath` not assigned (Step 8) or **Active** unchecked (Step 7 of Step 3) |
| Output shows `AnimationTree resolved but 'parameters/playback' is null` | `Tree Root` isn't an `AnimationNodeStateMachine` — redo Step 3.3 |
| Output shows `AnimationTreePath '...' is set but could not be resolved` | NodePath points to a renamed/deleted node — re-assign in Step 8 of Step 3 |
| Idle/run blend works but crossover never changes pose | A transition arrow is missing (Step 4.4) — `Travel()` found no path, silently no-oped |
| Crossover changes pose late / a beat after the freeze | A transition's Switch Mode is "At End"/"Sync" instead of **Immediate** (Step 4.5) |
| State name typo (e.g. "startup" lowercase) | `MoveAnimState.ToString()` is case-sensitive and must exactly match the state node's name (Step 4.2) |
| Run blend never reaches full speed / blends "too early" | BlendSpace1D's run point doesn't match the current `MoveSpeed` export — see the Step 3 gotcha |
| Opponent's animation/lean still doesn't play on the other window | Confirm both windows are on the merged `feat/54-anim-integration` build — pre-#69 clients never read the broadcast phase for display |
| Ball doesn't visibly switch hands | Check `HandOffset` isn't 0 on the **Ball** node's Inspector; confirm Step 6.1's timing (must be on the burst tick, not before) |
| Pressing shoot still releases the ball instantly | Build is stale (old `BallController.TryShoot` cached) — rebuild (Step 1) and confirm the merged `feat/54-anim-integration` commit includes #74 |
| Shot windup never ends / ball never releases | Check `Output` for `JustReleasedJumpShot` never firing — confirm `RequestBeginMove`'s `"jumpshot"` branch exists (a stale build symptom, same fix as above) |

---

## What to deliberately NOT touch yet

This list was written for the early gameplay milestones. **M7a (#53) now opens the
static visual track** — a humanoid mesh, cosmetic facing/lean, and directional
shadows are in scope under that milestone. Outside M7a's specific sub-issues, the
restraint still holds:

- Materials / shaders beyond what M7a's readability pass calls for — gray
  placeholder surfaces are otherwise fine.
- The bespoke crossover animation clip — M7b (#54, see its editor-tasks section
  above) wires the rig and a placeholder pose; the real clip is M8 (#61, sub-issue
  filed under that umbrella), sourced later via external AI tool / hand-authoring.
- Imported 3D models (beyond the M7a humanoid mesh and M7b's idle/run rig),
  sounds, UI polish, menus.

Keeping these out keeps your learning surface small while you and the AI prove
the hard systems first.

---

## Two Godot C# gotchas worth knowing on day one

- Node scripts MUST be `partial` classes, or they won't build. Claude Code knows
  this; if a build fails mysteriously, check that first.
- After adding/renaming C# files, you sometimes need to **build** (the hammer
  icon, top-right) before Godot sees the new class in the Attach Script list.
