# Controller Controls

[Keyboard controls (primary)](CONTROLS.md)

Move names use familiar NBA 2K terms; these are this game's current bindings,
not NBA 2K's control scheme. Xbox and PlayStation labels are shown together.
**RS** means right stick.

## Current Controller Coverage

**Movement and shooting currently have no controller bindings.** The left
stick is unbound, and no controller button starts a shot. Use **WASD** for
movement and **Space** for shooting alongside the controller. Controller-only
play is therefore incomplete with the current defaults.

| Action | Xbox | PlayStation | Detail |
| --- | --- | --- | --- |
| Move / start dribbling | Unbound; use **WASD** | Unbound; use **WASD** | Movement starts a dribble from a fresh possession. |
| Shoot / layup | Unbound; use **Space** | Unbound; use **Space** | Press once; the release follows automatically after the windup. |
| Sprint / turbo | Hold **RT** | Hold **R2** | While dribbling; faster movement, slower turning, more steal exposure. |
| Protect ball | Hold **LT** | Hold **L2** | While dribbling; slows movement and overrides sprint. |
| Defensive stance / intense-D | Hold **LT** | Hold **L2** | When the opponent has the ball; use WASD to slide. |
| Pump fake | Press **LB** | Press **L1** | After starting a jump shot with Space, press early in the windup. |
| Steal | Press **X** | Press **Square** | RS horizontal direction selects the reach side. |
| Block | Press **Y** | Press **Triangle** | Timed block attempt. |
| Hands up / contest | Press **B** | Press **Circle** | Timed committed contest; pressing once starts the action. |
| Size-up modifier | Hold **RB** | Hold **R1** | Selects dribble variants; does no move by itself. |
| Finesse modifier | Hold **A** | Hold **Cross** | Selects dribble variants; does no move by itself. |
| Drive gather / euro step | Press **View / Back** | Press **Share / Create** | Requires a live dribble; WASD direction selects straight or sideways gather. |
| Triple-threat jab step | Press **Guide / Xbox button** | Press **PS button** | Only while holding the ball, rather than dribbling. |
| Post up / back down | Hold **D-pad Down** | Hold **D-pad Down** | Requires a live dribble; use WASD for slow post movement. |
| Post turnaround jumper | Hold **D-pad Down**, then **Space** | Hold **D-pad Down**, then **Space** | Wait until the away-facing post stance is established before pressing shoot. |

The jab is currently mapped to the controller's system button, which may be
intercepted by the operating system or an overlay. The gather is mapped to
View/Share, rather than a shoulder button. These reflect the actual defaults.

## Pro Stick Dribble Moves

All moves below require a live dribble. RS left/right select your player's
body-left/body-right. "Toward the off hand" means the side opposite the hand
currently holding the ball: RS left when the ball is in the right hand, or
RS right when it is in the left hand.

| Move | Xbox | PlayStation |
| --- | --- | --- |
| Crossover | Hold **RS toward the off hand** | Hold **RS toward the off hand** |
| Hesitation | Flick or hold **RS toward the ball hand** | Flick or hold **RS toward the ball hand** |
| In-and-out | Very quickly flick **RS toward the off hand**, then return to centre | Very quickly flick **RS toward the off hand**, then return to centre |
| Behind-the-back | Flick **RS down**, then return to centre | Flick **RS down**, then return to centre |
| Behind-the-back, alternate input | Hold **RB** + hold **RS toward the off hand** | Hold **R1** + hold **RS toward the off hand** |
| Between-the-legs | Hold **A** + hold **RS toward the off hand** | Hold **Cross** + hold **RS toward the off hand** |
| Spin | Hold **RB + A** + hold **RS toward the off hand** | Hold **R1 + Cross** + hold **RS toward the off hand** |
| Retreat dribble | Hold **RB** + flick **RS down** and return to centre | Hold **R1** + flick **RS down** and return to centre |
| Stepback gather | Hold **RS down** | Hold **RS down** |

Return RS to centre between gestures. Keep modifiers held until the move
registers. Toward the ball hand, the horizontal gesture remains a hesitation
even with a variant modifier held. RS up currently triggers no move.

A horizontal flick must return to centre within 4 simulation ticks (about
67 ms at 60 Hz) to count as an in-and-out; holding through the fifth tick
selects the crossover variant. Downward flicks allow 12 ticks (about 200 ms);
holding through the thirteenth selects the stepback gather.

## Gathers, Defense, and Follow-ups

- **Drive gather:** press View/Share while dribbling with neutral or forward
  WASD input relative to your body.
- **Euro step:** press View/Share while holding a WASD direction sideways
  relative to your body. The direction picks the step side.
- **Finish:** after a gather or stepback finishes recovering, press Space.
  Near the rim this starts a layup; farther out it starts a jump shot.
- **Steal side:** hold RS right while pressing X/Square to reach to your
  body-right. RS left or neutral selects body-left.
- **Rebound / loose-ball recovery:** move into reach with WASD; pickup is
  automatic. There is no separate rebound button.
- **Box out:** use WASD to establish body position; there is no dedicated
  box-out button.

Picking up a used dribble locks ordinary movement and prevents another dribble.
Moves must finish recovery before another move can begin. LB/L1 only cancels
an ordinary jump shot inside its pump-fake window; it cannot cancel a gather,
layup, turnaround, or dribble move.

See the [keyboard guide](CONTROLS.md#possession-and-timing-notes) for clearing
possession and other shared rules. The source of the current bindings is
[project.godot](project.godot).
