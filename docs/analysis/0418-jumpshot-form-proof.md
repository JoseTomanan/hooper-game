# #418 — Normal jump-shot form

Issue: #418. Related: ADR-0003/0014/0016/0027, #173/#185.
Date: 2026-10-22. Baseline: main `7c2a4bb` (production assets unchanged).

The human owns shooting-form acceptance in #173. This record separates objective
pose/release proof from that judgment and from the known ball/hand coupling gap.

## Baseline method and frozen comparisons

Before asset replacement, `JumpshotFormTest` exercised the real Player.tscn,
AnimationTree, production rig scaling and final SkeletonUpdated poses. The
shot begins through BeginCommittedMove's existing harness seam. The separate
JumpshotFormCapture uses production Main and input. Release is latched from
SuccessfulShotReleasesForHarness at Active frame 0, separately from pose/render
sampling. The first Active pose can still carry Startup; it is retained in the
trace and event observation, while settled release geometry uses Active 1–3.

Three repeated baseline diagnose runs each produced 65 phase/counter-aligned
samples. Maximum inter-run differences were exactly zero for elbow angles,
wrist height, wrist flexion and arm asymmetry. Thus the measured repeat-noise
band is zero; form comparisons use strict directional order, without fitting
target magnitudes to the replacement. Gather uses Startup 8–11; follow-through
uses Recovery 0–3. Missing sample sets fail rather than producing a vacuous min.

The elbow angle is the interior angle shoulder–elbow–wrist; 180° is straight.
Up derives from Spine→Neck, right from left→right upper arm. Wrist height is
(wrist−elbow) projected on up. Hand direction is wrist→middle-finger base,
measured separately from elbow→wrist forearm direction; signed sagittal wrist
orientation must state its anatomy-derived forward axis in the instrument.

| Baseline relationship | Gather | Settled release | Early Recovery |
|---|---:|---:|---:|
| Shooting elbow mean (degrees) | 148.103916 | 159.312083 | 141.904591 |
| Shooting elbow min/max | 146.760986 / 149.732712 | 145.026154 / 173.616425 | 133.976517 / 147.694702 |
| Shooting minus guide elbow min (degrees) | −13.914749 | −2.557220 | 0.746277 |
| Wrist above elbow min (m) | 0.191513 | 0.161961 | 0.150547 |
| Signed wrist flexion mean (instrument initial convention, degrees) | 28.678408 | 19.786589 | 14.854528 |

Gather bend, extension at initial release and wrist-above-elbow already hold;
they are preserved properties, not reproduced defects. The discriminating
failures are loss of a consistently more extended shooting arm versus guide
during release, and failure to retain shooting extension above the loaded
gather into early Recovery. Wrist-axis direction is checked against anatomical
forward before interpreting flexion as a downward follow-through.

Baseline raw traces: `.godot/form-baseline-{1,2,3}.json`; immutable original
normal family: `tests/fixtures/jumpshot_goalkeeper.res`. Baseline rendered
gameplay frames are under `.godot/418/baseline-native-gameplay-p23469/`.
At first release the production ball is visibly below the raised hands. Its
authoritative launch origin is unchanged by this animation-only scope.

## Source authority

ADR-0014 tier 2: [Jr. NBA shooting](https://jr.nba.com/shooting/) and
[jump shot](https://jr.nba.com/how-to-shoot-a-jump-shot/): shooting hand under
the ball, guide hand beside it, elbow extension, wrist snap, retained follow-through
and balanced landing. These establish structure, not numerical feel targets.

Blender 5.2.0 LTS installed API/RNA documentation confirmed PoseBone.matrix
(armature-object space), matrix_basis (relative parent/rest), keyframe_insert
and baked FBX export flags. The online 5.2 reference fetch failed; runtime
documentation was checked directly rather than assuming signatures.
[PoseBone](https://docs.blender.org/api/current/bpy.types.PoseBone.html),
[keyframe_insert](https://docs.blender.org/api/current/bpy.types.bpy_struct.html#bpy.types.bpy_struct.keyframe_insert),
[FBX export](https://docs.blender.org/api/current/bpy.ops.export_scene.html#bpy.ops.export_scene.fbx).
[Godot final pose signal](https://docs.godotengine.org/en/4.7/classes/class_skeleton3d.html#class-skeleton3d-signal-skeleton-updated)
fires after modifiers; pose_updated does not include those modifications.

## Replacement and validation

Pending implementation and matched after evidence. No human visual acceptance
is inferred from the baseline, the authoring tool or a passing harness.
