# Moving crossover lateral step

- **Issue:** #424, under #173; related #198, #209, #210, #301 and #414
- **ADR:** 0003, 0012, 0015/0016, 0021 and 0027
- **Date:** 2026-10-21
- **Purpose:** distinguish missing root travel from pose continuity, revise the
  moving push-cross contract, and retain proof of the bounded repair.

## Diagnosis and revised contract

The maintainer's tested build, input, sprint state, hand and network role were
not recorded. This work does not reconstruct that unrecorded reproduction.
The known composition independently explains the reported result: a forward
exit plus forward gather survivor has zero heading-relative lateral velocity;
a neutral exit fades to momentum only at surviving speed 2 m/s. That was
deliberately accepted by #198, rather than an introduced pose regression.

The first new TDD test called the unchanged composition through a forwarding
Crossover-specific entry point. Both hands failed with velocity `(0, 0, 11)`:
survivor 2 m/s plus forward burst 9 m/s, lateral zero. The new human direction
in #424 authorizes changing that outcome; ADR-0003 records the refinement.

Only Crossover adds an empty-hand lateral contribution to the original shared
composition. The contribution scales from zero survivor to half BurstSpeed
at survivor 2 m/s, then saturates. Neutral exit receives that contribution;
active forward exit weights it by its forward component and fades it to zero
by 30 degrees of lateral steering. The provisional maximum is 4.5 m/s with
the existing BurstSpeed 9 m/s. Forward composition is unchanged. A stationary
cross is exactly unchanged, and 45-degree diagonals retain their old result.

Opposite steering can cancel or override the flick contribution. This remains
a continuous one-time exit choice, not a guarantee that every analog angle
travels toward the empty hand. Unit tests cover the cancellation neighborhood
and saturation boundaries. Sprint does not amplify the extra contribution.
BehindTheBack, BetweenTheLegs, InAndOut and Spin still use the unchanged shared
composition. No new network field, timing window, animation or cancel is added.

## Proof surfaces

`MovingCrossoverLateralTests` covers both hands at headings 0, pi/2 and pi,
forward/released exits, sprint survivors, unchanged stationary/diagonal exits,
and speed/angular continuity. The legacy composition tests stay intact as
sibling controls. The focused new suite has 34 passing cases; the focused
new/legacy composition run has 62.

`MovingCrossoverLateralTest` loads real Main/Player/Ball scenes, awards a live
possession before measurement, and sends production input. Right-hand origin
uses `aim_left`, left-hand origin `aim_right`; modifiers stay off except the
explicit sprint controls. Fixture resets precede the input-driven sequence.
The offline suite covers both hands and headings pi/pi/2 with held-forward,
diagonal and released-before-Active exits, plus stationary and sprint controls.
Two real ENet variants exercise host ownership with a coherent remote observer,
and client ownership with prediction and server-remote authority.

Assertions require legal Dribbling, recognized Crossover, complete ordered
Startup/Active/Recovery, exactly one hand swap, nonzero but reduced gather
momentum, all three Active samples, root lateral travel over 0.10 m, and retained
forward travel. Root displacement starts at the final Startup position, before
the first Active integration. Authority/history compare first Active velocity
against the observed final Startup survivor and the case's intended exit;
this discriminates a held-forward sample incorrectly reused for every exit.
Each authoritative/historical root increment must match sampled velocity times
the fixed physics step within 0.002 m, with consecutive ticks and counters.
Input reverses after Active entry to challenge
the one-time snapshot. A remote observer pairs player, ball and animation
presentation ticks and ignores explicit history holds instead of counting
duplicate samples. Logs retain input variant, hand, heading, root, velocity,
phases and presentation ticks.

Client prediction may correct velocity during Active: the existing replay
path does not reconstruct committed movement. A stale Startup correction was
observed replacing predicted lateral velocity 4.5 m/s with zero in the next
Active sample. The server independently proves the same owner's stable exit
after opposite input. Own-client assertions therefore require the lateral
burst at predicted Active entry and net root travel by Recovery completion;
they log Active-only displacement separately. They do not claim uninterrupted
predicted Active travel, exact server/client velocity equality, or new replay
correctness. Authority and coherent remote display retain the stricter
complete-Active-window displacement and velocity-stability assertions.

## Rendered measurement and scope

The existing native capture instrument, with moving input held through return,
recorded a right-hand-origin cross at heading pi/2. Across three Active ticks,
root lateral travel was 0.225 m and forward travel 0.5833 m. Active velocity
was `(11.6667, 0, -4.5)`; the actual gather survivor was 2.6667 m/s. The extra
cut is therefore actual CharacterBody movement, not a skeletal offset.
Local artifacts are under `.godot/424-rendered-current/right-moving-1/`.

Disabling only MovingCutFraction (0.5 to zero) makes the real-scene regression
fail at the first Active sample: velocity `(0, 0, -11.6667)`, root X unchanged,
while the recognized move and hand transfer still occur. The source was
restored byte-for-byte and rebuilt. The paired native capture is under
`.godot/424-rendered-legacy/right-moving-1/`; this is a controlled legacy
composition, explicitly labeled `a54036e+moving-cut-disabled`, not a claim
to have captured the maintainer's unrecorded build.

The held-movement return variant has a preexisting foot seam at Recovery to
moving Dribble: the old and new compositions report the same four failures
at ticks 101/102 (maximum LeftFoot step 0.4511 m, RightFoot 0.3230 m). The
largest before/after metric difference is below 0.000001 m. This optional
diagnostic does not certify held-return continuity; #424 neither repairs it
nor attributes it to the new cut. Comparison and restoration evidence is
`.godot/424-legacy-control-summary.json`. The default #414 regression remains
a separate gate.

The three-cycle left-origin continuity fixture requires a deeper initial
spawn because the new cut travels toward the unchanged gameplay camera.
Only that capture fixture's pre-run position changes; it does not constrain
or alter gameplay travel. Capture success and numerical bounds never accept
readability or feel. #301 and #173 stay open; tuning remains #238.

## Framework sources

Local validation on the final harness: game build (zero warnings/errors),
1,273 unit passes with five intentional characterization skips, 98 repository
checks with one platform skip, offline 16-case trajectory suite, and both
four-case network ownership variants with both peers required all pass.
The executable catalog registers the three new invocations for exhaustive CI.
The lateral-contribution negative control fails for the intended missing
Active lateral velocity, and the restored build passes.

Pins and local binary: Godot.NET.Sdk/GodotSharp 4.7.1, Godot 4.7.1 Mono,
.NET SDK 8.0.421 targeting net8.0. Context7 was unavailable; official 4.7 docs
were consulted directly. These sources establish engine semantics, not the
game-design profile or human visual acceptance:

- [Vector2.Dot and normalized vector projection](https://docs.godotengine.org/en/4.7/classes/class_vector2.html#class-vector2-method-dot)
- [Input.ActionPress/ActionRelease](https://docs.godotengine.org/en/4.7/classes/class_input.html#class-input-method-action-press)
- [CharacterBody3D.MoveAndSlide and velocity](https://docs.godotengine.org/en/4.7/classes/class_characterbody3d.html#class-characterbody3d-method-move-and-slide)
- [Godot C# API naming and properties](https://docs.godotengine.org/en/4.7/tutorials/scripting/c_sharp/c_sharp_differences.html)
- [Engine physics frame counter](https://docs.godotengine.org/en/4.7/classes/class_engine.html#class-engine-method-get-physics-frames)
- [Node physics processing priority](https://docs.godotengine.org/en/4.7/classes/class_node.html#class-node-property-process-physics-priority)
