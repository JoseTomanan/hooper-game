# #417 — BehindTheBack diagnostic evidence and committed-clip hookup audit

Measured 2026-10-20 (Asia/Manila); **the reported standing presentation is not
reproduced, and no repair is claimed**. Refs #417, #173 and #178.

## Scope and revision

Gameplay/assets baseline: `f324c1f`, the starting main revision. Capture/audit
instrumentation: `c856e419b09a77a727167c8e1d0974e3a1d265b0`. No gameplay,
frame data, hand-swap, animation resource or scene binding has been changed.
Godot `4.7.1.stable.mono.official.a13da4feb`, .NET SDK `8.0.421`, 60 physics Hz.

The original human playtest revision and exact sequence remain unknown. The
triaged brief expressly permits bounded diagnostic publication when reproduction
fails and forbids closing the report on existing green proofs. This delivery
uses that branch of the brief. It does **not** satisfy repair/red-before-green-after
acceptance, certify hand/ball contact, or accept gameplay-distance readability.

## Experiments and observation

Normal `Main.tscn`, normal host spawning and gameplay camera, and production
`Input.ActionPress` polling drive fresh Held → Dribbling. After settling, a
horizontal gesture toward the empty hand with `move_size_up` held executes
BehindTheBack. Both origins cover the two gesture signs. Moving entry accelerates
world-right for 16 ticks before the gesture; measured entry speed is at least
5.9 m/s. The left-origin setup first uses an ordinary crossover to obtain that
origin, then waits for completion and settlement; its setup is not counted as BTB.

The listen-server host is the local subject; a genuine ENet client observes
`Players/1` remotely. This covers a local holder and remote observer, **not**
a client-owned predicting subject. The trace observes accepted move identity
through the machine/display snapshot, not an internal recognizer-event or Begin
return-value hook. A timed-out/unrecognized gesture is a failed capture.

| Captured matrix | Runs | Measured BTB samples per run | Largest physics gap |
|---|---:|---:|---:|
| Local rendered, both origins × standing/moving | 4 | 18 | 1 tick |
| Remote real-time headless, same matrix | 4 | 18 | 1 tick |
| Remote real-time rendered, same matrix | 4 | 9 | 2 ticks |

All completed one full action and returned to destination-hand Dribble. All
observed Startup/Active/Recovery, the expected origin-suffixed clip bindings,
playback progression, and bone motion. Pose-frame mismatches were zero. Remote
player/ball presentation-tick mismatches were zero in the measured windows.
The recorded ball sweep occurs on the same presentation timeline; that is **not**
a proof of acceptable ball/palm contact at every tick.

For the four local rendered cases, root-translation/display-yaw-removed maximum
hip excursion ranges 0.1808–0.2194 m. Both hands and feet move materially too;
see [raw measurements](evidence/0417/capture-summary.json). These are excursions
within the action, including transitions and existing presentation lean. They
prove movement in these captures, not that the authored motion looks correct.
A resource name and a bone excursion alone cannot certify visible clip contribution
through a blend; current/fading playback and the final skeleton signal remain
separate trace observations. No static-standing causal mismatch was localized.

Production-input identity controls from both origins, stationary:

- Q plus occupied-hand horizontal gesture executes Hesitation, with no hand swap.
- Sustained downward aim executes StepBack, gathers and returns to Held.
- No gesture executes no committed move and remains neutral Dribble. This is
  a dribbling neutral control, not a frozen skeleton: hip excursion is 0.0090 /
  0.0122 m. The analyzer explicitly includes Inactive samples for this control.

These controls do not all execute BehindTheBack. They keep the original K/down
complaint separate from the horizontal empty-hand BTB gesture. They do not replace
static-clip/wrong-hand mutation evidence for a future diagnosed repair.

PNG phase samples at unchanged gameplay-camera distance (640×360):

| Role/origin | Startup | Active | Recovery |
|---|---|---|---|
| Local Left | [frame](evidence/0417/local-left-stationary-1-startup.png) | [frame](evidence/0417/local-left-stationary-1-active.png) | [frame](evidence/0417/local-left-stationary-1-recovery.png) |
| Local Right | [frame](evidence/0417/local-right-stationary-1-startup.png) | [frame](evidence/0417/local-right-stationary-1-active.png) | [frame](evidence/0417/local-right-stationary-1-recovery.png) |
| Remote Left | [frame](evidence/0417/remote-render-left-stationary-1-startup.png) | [frame](evidence/0417/remote-render-left-stationary-1-active.png) | [frame](evidence/0417/remote-render-left-stationary-1-recovery.png) |
| Remote Right | [frame](evidence/0417/remote-render-right-stationary-1-startup.png) | [frame](evidence/0417/remote-render-right-stationary-1-active.png) | [frame](evidence/0417/remote-render-right-stationary-1-recovery.png) |

Every retained PNG has its original filename/tick in the associated compressed
trace. Retained phase images select the middle observed sample of that phase.
Full local PNG sequences remain under `.godot/btb417-final-local/`; full remote
sequences under `.godot/btb417-render-remote2/`. Manifests and complete traces are
archived here as `*-manifest.json.gz` / `*-trace.jsonl.gz`. Remote-render and
identity-control manifests name `37fc910` plus the then-uncommitted control and
clip-observation instrumentation subsequently committed in `c856e41`; local final
and remote headless final captures name `c856e41`. Production baseline is identical.

A first paced headless attempt ran the observer ahead of the deliberately slowed
server and timed out without an action; the CLI now rejects that combination.
A first native 960×540 paced run overflowed the bounded PNG writer queue and
failed. A concurrent local-control attempt collided with the remote run's port
and failed binding. None is treated as production bug evidence or a successful
capture. Subsequent runs used separate ports and real-time 640×360 rendering.

## Bounded hookup provenance

`CommittedClipHookupAudit` instantiates real `Player.tscn`, disables its selector,
and manually starts each resolver-selected state. This deliberately bypasses
move legality, input and graph travel. **67 forced-state rows passed**, including
all skeletal track paths resolving to actual rig bones; zero invalid paths.
Exact state, resource path and binding counts are in [audit JSON](evidence/0417/hookup-audit.json).
This verifies shipped hookup, not regenerated equivalence or gameplay readability.

Notation: `P=Startup/Active/Recovery`, `p=startup/active/recovery`,
`H=Left/Right`, `h=left/right`. Except turnaround, library is `assets/locomotion.res`
and scene animation keys start `locomotion/`. All rows below were observed in
this forced-state runtime audit; BTB additionally has the production captures.

| Move | Expected source → library build tool | Observed clip ← scene state |
|---|---|---|
| Normal JumpShot | deliberately retained `Goalkeeper Catch Stationary.fbx` (#316) → `rebuild_jumpshot_clips.gd` | `jumpshot{p}` ← `Jumpshot{P}` |
| Fadeaway Active | `author_fadeaway.py` → `fadeaway_authored.fbx` → `rebuild_jumpshot_clips.gd` (authored flag true) | `fadeawayactive` ← `FadeawayActive` |
| Turnaround | low gather + retained jumpshot startup; duplicated shot Active/Recovery → `rebuild_turnaround_clips.gd` | `turnaround/turnaround{p}` in `assets/turnaround.res` ← `Turnaround{P}` |
| Crossover | `author_crossover.py` → `crossover_authored.fbx` → `rebuild_crossover_clips.gd` (authored flag true) | `crossover{p}{h}` ← `Crossover{P}{H}` |
| BehindTheBack | `author_behindtheback.py` → `behindtheback_authored.fbx` → `rebuild_behindtheback_clips.gd` | `behindtheback{p}{h}` ← `BehindTheBack{P}{H}` |
| Steal | `author_steal.py` → `steal_authored.fbx` → `rebuild_steal_clips.gd` | `steal{p}{h}` ← `Steal{P}{H}` (defender reach side) |
| Block | `author_block.py` → `block_authored.fbx` → `rebuild_block_clips.gd` | `block{p}` ← `Block{P}` |
| Contest | `author_contest.py` → `contest_authored.fbx` → `rebuild_contest_clips.gd` | `contest{p}` ← `Contest{P}` |
| JabStep | `author_jabstep.py` → `jabstep_authored.fbx` → `rebuild_jabstep_clips.gd` | `jabstep{p}` ← `JabStep{P}` (`moveId=jab`) |
| InAndOut | `author_inandout.py` → `inandout_authored.fbx` → `rebuild_inandout_clips.gd` | `inandout{p}` ← `InAndOut{P}` |
| RetreatDribble | `author_retreatdribble.py` → `retreatdribble_authored.fbx` → `rebuild_retreatdribble_clips.gd` | `retreatdribble{p}` ← `RetreatDribble{P}` |
| StepBack | `author_stepback.py` → `stepback_authored.fbx` → `rebuild_stepback_clips.gd` | `stepback{p}` ← `StepBack{P}` |
| BetweenTheLegs | `author_betweenthelegs.py` → `betweenthelegs_authored.fbx` → `rebuild_betweenthelegs_clips.gd` | `betweenthelegs{p}{h}` ← `BetweenTheLegs{P}{H}` |
| Hesitation | `author_hesitation.py` → `hesitation_authored.fbx` → `rebuild_hesitation_clips.gd` | `hesitation{p}` ← `Hesitation{P}` |
| Spin | `author_spin.py` → `spin_authored.fbx` → `rebuild_spin_clips.gd` | `spin{p}` ← `Spin{P}` (intentionally unsuffixed; late swap) |
| DriveGather | `author_drivegather.py` → `drivegather_authored.fbx` → `rebuild_drivegather_clips.gd` | `drivegather{p}` ← `DriveGather{P}` |
| EuroStep | `author_eurostep.py` → `eurostep_authored.fbx` → `rebuild_eurostep_clips.gd` | `eurostep{p}` ← `EuroStep{P}` |
| Layup | `author_layup.py` → `layup_authored.fbx` → `rebuild_layup_clips.gd` | `layup{p}` ← `Layup{P}` (intentional fixed finishing side) |
| Unknown/synthetic fallback | `author_generic_fallback.py` → three `generic{p}_authored.fbx` → `rebuild_generic_fallback_clips.gd` | `generic{p}` ← bare `{P}` (intentional safety net) |

The audit found no missing/misnamed committed binding. The resolver's historical
comment still described the generic states as idle/run and #296 as unfinished;
that comment is corrected to match the shipped authored fallback. This is a
documentation correction, not the reported animation repair. No unrelated visual
defect was independently established or absorbed into this work.

## Reproduction commands

From the repository root in PowerShell, use a fresh output directory per command:

```powershell
$env:GODOT = [Environment]::GetEnvironmentVariable('GODOT', 'User')
$revision = git rev-parse HEAD
dotnet build 'HOOPER GAME.csproj' --configuration Debug
python tools/run_crossover_capture.py --godot $env:GODOT --move behindtheback --count 1 --commit $revision --output .godot/btb417-local-new --resolution 640x360 --port 23475
python tools/run_crossover_capture.py --godot $env:GODOT --move behindtheback --count 1 --commit $revision --output .godot/btb417-remote-new --remote --headless --port 23474
python tools/run_crossover_capture.py --godot $env:GODOT --move behindtheback --count 1 --commit $revision --output .godot/btb417-render-new --remote --resolution 640x360 --port 23474
python tools/run_crossover_capture.py --godot $env:GODOT --move hesitation --count 1 --motion stationary --commit $revision --output .godot/btb417-hesi-new --headless --port 23475
# Repeat the control command with --move stepback and --move idle and fresh outputs.
python tools/analyze_behindtheback_capture.py .godot/btb417-local-new/right-stationary-1
& $env:GODOT --headless --path . --log-file .godot/btb417-audit-new.log res://tests/integration/CommittedClipHookupAudit.tscn -- --audit-output=.godot/btb417-audit-new.json --audit-commit=$revision
```

Do not run two captures on the same port. `--paced` is a rendered diagnostic
option and makes no real-time claim; `--headless --paced` is rejected.

## Verification and remaining uncertainty

- Game build: 0 warnings/errors.
- Unit suite: 1,232 passed, 5 expected characterization skips, 0 failed.
- 35 focused existing animation/sweep scenarios passed; network BTB sweep passed.
- New 67-row hookup audit passed, including its catalog entry.
- 26 harness-catalog repository tests passed with Git Bash explicitly on PATH.
- Fresh-context diagnostic review found an idle-analysis empty-selection error;
  corrected to measure actual Inactive samples. Final review is recorded in PR.

Existing focused logs: `.godot/harness-runs/20261019T234246799123Z-3768-678a0376/`.
Network logs: `.godot/harness-logs/net-behindtheback-sweep-b58e53146d8e46b8ba62d4bf141db7c5-*`.
This is not an exhaustive-suite or merge-ready claim.

Still needed to repair/close #417: reproduce the human's exact static-standing
branch, identify its first causal mismatch, pin it with a failing regression,
then prove the correction and static/wrong-hand mutations locally and remotely.
A client-owned predicting reproduction and independent motion directions remain
uncovered. The current captures cannot rule out sequence/history-dependent
failures, slower render cadences, possession transitions or other playtest builds.
Human gameplay-distance readability stays open under #173; #178 is separate.
No editor step is needed to run these instruments. Do not close #417 on this PR.

## Official API sources

- [Input action polling](https://docs.godotengine.org/en/4.7/classes/class_input.html#class-input-method-action-press): production input rather than direct Begin injection.
- [Playback state and crossfade semantics](https://docs.godotengine.org/en/4.7/classes/class_animationnodestatemachineplayback.html#class-animationnodestatemachineplayback-method-get-current-node): state changes at fade start, so record fading state and progress separately.
- [State graph node lookup](https://docs.godotengine.org/en/4.7/classes/class_animationnodestatemachine.html#class-animationnodestatemachine-method-get-node) and [clip binding](https://docs.godotengine.org/en/4.7/classes/class_animationnodeanimation.html#class-animationnodeanimation-property-animation): observe the actual selected resource key.
- [Final skeleton signal](https://docs.godotengine.org/en/4.7/classes/class_skeleton3d.html#class-skeleton3d-signal-skeleton-updated): align final modifier poses with their own frame stamps.
- [Manual animation evaluation](https://docs.godotengine.org/en/4.7/classes/class_animationmixer.html#class-animationmixer-method-advance), [Start(reset)](https://docs.godotengine.org/en/4.7/classes/class_animationnodestatemachineplayback.html#class-animationnodestatemachineplayback-method-start), [track paths](https://docs.godotengine.org/en/4.7/classes/class_animation.html#class-animation-method-track-get-path), [physics processing](https://docs.godotengine.org/en/4.7/classes/class_node.html#class-node-method-set-physics-process): bounded forced-state audit, explicitly separate from gameplay routing.
