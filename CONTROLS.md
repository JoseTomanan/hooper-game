# Keyboard Controls (Primary)

[Controller controls](CONTROLS-CONTROLLER.md)

These are the current default bindings. Move names use familiar NBA 2K terms;
the inputs are specific to this game. Keys refer to physical QWERTY positions.

## Quick Start

| Action | Keyboard | How it works |
| --- | --- | --- |
| Move / start dribbling | **W A S D** | W/S move up/down the court; A/D move left/right. Moving with a fresh possession starts your dribble. |
| Sprint / turbo | Hold **Shift** while dribbling | Faster movement, slower turning, and more steal exposure. |
| Protect ball | Hold **Ctrl** while dribbling | Slower protected dribble. Overrides sprint when both keys are held. |
| Shoot / layup | Press **Space** | Automatically selects a layup near the rim or a jump shot farther out. |
| Pump fake | **Space**, then **E** early in the jump-shot windup | A separate E press cancels the shot inside its pump-fake window. |
| Steal | Press **Z** | Reach side comes from J/L; see Defense below. |
| Block | Press **X** | Commit to a timed block attempt. |
| Hands up / contest | Press **C** | Commit to a timed shot contest. |
| Defensive stance / intense-D | Hold **Ctrl** while the opponent has the ball | Face the ball handler and use WASD to slide. |

Shooting starts on the Space press. The ball releases automatically after the
windup; there is no hold-and-release shot timing input.

## Pro Stick Moves on Keyboard

**J / L** act as Pro Stick left/right, and **K** acts as Pro Stick down.
**I** is bound to Pro Stick up, but upward gestures currently trigger no move.

The horizontal direction is relative to your player's body and ball hand:
J selects the player's left side; L selects the player's right side. For example,
with the ball in the right hand, J is toward the off hand and L is toward the
ball hand. Crossover-family moves require a live dribble.

| Move | Keyboard | Result / requirement |
| --- | --- | --- |
| Crossover | Hold **J or L toward the off hand** | Switch hands and burst to that side. |
| Hesitation | Tap or hold **J or L toward the ball hand** | Keep the same hand and freeze/bait the defender. |
| In-and-out | Very quickly tap and release **J or L toward the off hand** | Fake the crossover without switching hands. |
| Behind-the-back | Tap and release **K** | Switch to the off hand behind your back. |
| Behind-the-back, alternate input | Hold **Q**, then hold **J or L toward the off hand** | Select the behind-the-back crossover variant. |
| Between-the-legs | Hold **R**, then hold **J or L toward the off hand** | Switch hands through the legs. |
| Spin | Hold **Q + R**, then hold **J or L toward the off hand** | Select a spin instead of the other crossover variants. |
| Retreat dribble | Hold **Q**, then tap and release **K** | Retreat while keeping a live dribble. |
| Stepback gather | Hold **K** | Create space backward and pick up the dribble. Press Space after recovery to shoot. |

Release J/L/K between gestures to reset the input. Hold modifiers until the
gesture registers. Q and R select variants; pressing either alone does no move.
The horizontal modifiers select those variants only toward the off hand;
toward the ball hand, the result remains a hesitation.

Horizontal quick taps have a very short window: up to 4 simulation ticks
(about 67 ms at 60 Hz). Holding through the fifth tick selects the held move.
Downward taps allow 12 ticks (about 200 ms); holding through the thirteenth
selects the stepback gather.

During crossover-family startup, WASD can shape the exit direction within the
move's allowed range. The move still runs through its commitment and recovery.

## Triple Threat, Gathers, and Post Play

| Move | Keyboard | Result / requirement |
| --- | --- | --- |
| Triple-threat jab step | Press **F** while holding the ball | Available before starting a dribble or after picking it up; unavailable while dribbling. |
| Drive gather | Press **G** while dribbling, with no sideways movement relative to your body | Pick up the dribble and commit toward the rim. Neutral or straight-ahead movement selects this version. |
| Euro step | Press **G** while holding a WASD direction sideways relative to your body | Pick up the dribble and step to the selected side. |
| Finish after a gather | Press **Space** after the gather's recovery | Starts a separate shot; close enough to the rim, this becomes a layup. |
| Post up / back down | Hold **T** while dribbling | Turn your back toward the rim, then move slowly with WASD. |
| Post turnaround jumper | Keep **T** held; press **Space** once the post stance is established | Turn toward the basket and shoot. An early Space press is ignored; press again once set. |

Gathers and euro steps do not shoot automatically. The default layup selection
range is 4 metres from the rim, measured along the court.

## Defense

| Action | Keyboard | Detail |
| --- | --- | --- |
| Defensive slide | Hold **Ctrl** + **WASD** | Stay oriented toward the ball handler while moving along the court directions. |
| Steal to your left | **J + Z**, or **Z** with no horizontal Pro Stick input | Reach to your player's body-left. |
| Steal to your right | **L + Z** | Reach to your player's body-right. |
| Block | Press **X** | Timing and proximity to the shot matter. |
| Hands up / contest | Press **C** | Time the contest to the opponent's release. This is a committed action, rather than a hold-to-raise-hands toggle. |
| Rebound / recover loose ball | Move into reach of the ball | Recovery is automatic; there is no separate rebound button. |
| Box out | Use **WASD** to establish position | Body contact handles positioning; there is no dedicated box-out button. |

Steal directions describe your own body sides, which need not match the
opponent's ball-hand labels. Defensive actions are available without the ball.

## Possession and Timing Notes

- After a rebound or turnover, take the ball inside the arc and then back
  outside to clear it before scoring. A made basket keeps possession.
- Picking up a used dribble with a shot, pump fake, stepback, or gather ends
  that dribble. Ordinary movement is locked while holding the gathered ball;
  you cannot restart the dribble. You can still attempt a legal shot or jab.
- Moves have startup, action, and recovery. Wait for recovery to finish before
  pressing the next move; inputs during another move do not queue a combo.
- E only pump-fakes an ordinary jump shot within its early window. It does not
  cancel layups, turnarounds, dribble moves, or gathers. Pressing Space and E
  simultaneously is too early for a pump fake.
- A fadeaway/off-balance jump-shot presentation is selected from your facing
  at release; it has no separate key.

Bindings are defined in [project.godot](project.godot). Move selection lives in
[PlayerController.cs](scripts/Player/PlayerController.cs), with post-play and
stance conditions in [PlayerPostMotion.cs](scripts/Player/PlayerPostMotion.cs)
and [DefensiveStanceMotion.cs](scripts/Player/DefensiveStanceMotion.cs).
