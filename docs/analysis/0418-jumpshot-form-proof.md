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

`tools/author_jumpshot.py` retains the owned source's jump/landing and authors
the six shooting/guide arm-chain rotations. It reads the source import rate
(30 Hz) before choosing output 60 Hz. Wrist directions interpolate spherically
over Startup 8–18; linear interpolation through nearly opposing directions
produced a measured 0.384 m fingertip jump and was rejected. Follow-through is
retained through the first four normal Recovery poses. The short feint's last
displayed pose is Recovery animation key 6, despite its eight gameplay ticks:
the first phase label still carries the prior pose, and the neutral handoff
still displays Recovery's tail. The authored arms return to the source by
that measured endpoint. Gameplay timing and release position are untouched.

The rebuild preserves all inherited non-arm channels exactly, and preserves
the original arm entry/short-feint tail. FadeawayActive's missing immutable
channels continue to inherit from the **legacy** Active, so normal reauthoring
does not change the separately authored fadeaway. Exact track/key fingerprints
confirm all 69 unrelated animations, including FadeawayActive, are unchanged.
Turnaround's separate library intentionally clones the shot arc after its
thirty-tick turn. The exhaustive suite caught that derived library retaining
the old arms. The normal rebuild now invokes the existing Turnaround builder
and refreshes those dependent clips, preserving Turnaround's 48/4/20 timeline
and existing pose-equivalence gate. This is a source dependency, not a new
Turnaround design or tuning change.

| Final live normal form (both origins) | Before | After |
|---|---:|---:|
| Loaded gather maximum shooting elbow (degrees) | 149.733 | 65.606 |
| Settled release mean shooting elbow (degrees) | 159.312 | 164.493 |
| Release minimum shooting minus guide elbow (degrees) | −2.557 | +86.055 |
| Release minimum wrist above elbow (m) | 0.161961 | 0.261790 |
| Early Recovery minimum shooting elbow (degrees) | 133.977 | 164.493 |
| Forward/downward wrist-flexion change (degrees) | +15.640 | +133.550 |

The final wrist axis uses the mean foot→toe direction projected orthogonal to
Spine→Neck up. This corrects the initial arbitrary cross-product convention in
the baseline table: loaded flexion mean is −29.267° and early Recovery −13.627°,
so wrist flexion was already a preserved property. It is not claimed as a new
reproduction. Every selected sample must exist. Idle substitution fails wrist
height; immutable goalkeeper substitution fails release asymmetry and retained
follow-through, at the same live AnimationTree call site. Both substitutions
still perform one successful gameplay release and twenty Recovery ticks.

Normal releases are latched exactly once at Active/0 by an independent physics
observer after Ball. Early/late legal feints have zero releases, retain Held
possession and eight gameplay Recovery ticks. Existing phase selection,
non-looping/duration, material rotation coverage, grounded/airborne, upright
normal and distinct-fadeaway proofs retain their original thresholds.

Live segment measurements across both origins remain approximately 0.274047 m
(humerus) and 0.276145 m (forearm); observed ranges differ by less than one
micrometre. Rest lengths/scales are not edited, and inherited position channels
remain the exact original keys.

## Endpoint precision and controls

`tests/fixtures/jumpshot_boundary_baseline.json` freezes the original per-case
motion bounds. Each seam measures three adjacent one-tick edges around the
phase transition, including the reporting lag. A later explicit legacy-fixture
control repeated the late feint three times and matched original body positions
exactly; deterministic repeat noise remains zero.

The final late-feint toe differed from the baseline by only 6.99×10⁻⁸ m. This
is below the native coordinate resolution, not a physical motion regression.
The original late-seam input has largest absolute body/world coordinate
5.945295 m. [Godot Vector3 uses float32 by default](https://docs.godotengine.org/en/4.7/classes/class_vector3.html),
giving component spacing 2⁻²¹ m. Comparing two three-dimensional endpoints
therefore has resolution `2 × sqrt(3) × ULP = 1.65181237×10⁻⁶ m`. The fixture
stores this independently derived resolution separately from its unchanged
motion bound; other cases at larger original coordinates use 3.30362474×10⁻⁶ m.
There is no adjustable tolerance in the proof runner. The original 0.244 m
wrist and 0.106 m feint regressions remain well outside this numerical floor.

All sixteen local final captures (two repeats of both hands/four cases) pass
the original seam bounds at native precision. A deliberate wrist displacement
of twice the measured seam bound plus one metre fails its intended detector.
Remote comparisons measure the legacy and replacement through the same
ADR-0027 manual presentation clock; local clock bounds are not reused for it.
Missing release-window or required form frames fail, even when a capture
process otherwise exits successfully. Holds/resets and pose/render gaps remain
reported explicitly. A green report does not assert unobserved frames.

## Release/visible-ball limitation

The gameplay ball still launches from its fixed Held offset. The right-handed
shot is deliberately identical for both entering dribble hands, while the
ball's authoritative hand side is unchanged. Consequently the visible mismatch
differs by entering hand. At release, shooting-wrist→visible-ball distances are:

| Origin | Before (m) | After (m) |
|---|---:|---:|
| Right | 1.262570 | 1.357512 |
| Left | 1.663030 | 1.635415 |

At the next tick the final Right-origin wrist→visible-ball distance is 1.296232 m
while wrist→authoritative launch origin is 1.411440 m; for Left they are
1.594260 m and 1.700412 m respectively. Full ±2-tick windows are retained in
the reports. These are diagnostics of the known coupling limitation, not a
repair or a claim of hand/ball agreement. No attachment, cosmetic flight offset,
authoritative launch-origin or shot-accuracy change conceals the gap.

## Evidence and reproduction

Matched gameplay GIFs, phase contact sheets, front/side normal comparisons,
compressed raw traces/manifests and a source-frame hash index live under
[`evidence/0418`](evidence/0418/). GIFs are explicitly 2.4× slow motion and
compare observed phase/counter poses, including Active/0. Contact sheets retain
the complete source frame at half resolution. Their source hashes permit
checking the unchanged original PNGs in the workspace capture directories.

Native local captures use the production camera; diagnostic front/side views
reframe it. Older trace camera states are clearly labeled reconstructed from
Main.tscn or the prior post-draw row and committed capture path, not directly
instrumented observations. PNG dimensions are checked. New instrumentation
records the camera before post-draw repositioning. Paced ENet renders advance
one simulation tick per drawn frame and slow the producer solely to permit
image persistence: they establish observed source-tick coverage, not real-time
latency/performance. Separate unpaced headless ENet proofs cover the same cases.

All eight paced native cases pass coherent selected-tick, first-Active event
coverage and form checks. **Native endpoint comparisons are diagnostic only:**
the Right-normal neutral handoff compares different evaluated clip offsets
despite identical source phase/counter labels. At source Recovery/19 the old
capture's playback time is 0.300000012 s; the new capture's is 0.316666663 s.
Their following neutral handoff measures 1.360948746 versus 1.361240304 m.
The strict comparison remains red in `native-endpoint-incomparable.json`; it
is not relaxed or claimed to pass. `native-phase-boundary-comparison.json`
retains the actual source ticks, playback times and sample indexes. The
matching-clock unpaced ENet gate passes all eight cases, and the native films
remain available for human review. General remote animation-clock repair is
outside this animation-authoring issue; no production clock code is changed.

Commands (PowerShell, environment executables already provisioned):

```powershell
& $env:BLENDER --background --python-exit-code 1 --python tools/author_jumpshot.py -- "assets/Goalkeeper Catch Stationary.fbx" assets/jumpshot_authored.fbx
& $env:GODOT --headless --editor --path . --import --quit --log-file .godot/418-import.log
& $env:GODOT --headless --path . --log-file .godot/418-rebuild.log -s tools/rebuild_jumpshot_clips.gd
python tools/harness_catalog.py run --scene JumpshotFormTest.tscn --godot $env:GODOT
python tools/run_jumpshot_proof.py --godot $env:GODOT
```

Two independent final Blender exports compare exactly across 2,795 frame/bone
pairs (zero rotation/translation delta). Rebuilding the Godot library twice is
byte-identical, SHA256 `7d50b57e043e7db710a51ee8c2ba88534fb9b756a3a393465b97c7b43fea5c2b`.
Ordinary build, unit tests, exhaustive harness and independent review are
recorded in the implementation PR. Human shooting-form/readability acceptance
and ball coupling remain #173; fadeaway visual acceptance remains #185/#173.
No editor operation is required before reviewing the committed artifacts.

The local exhaustive run also exposed a pre-existing predictive crossover
oracle applying an authority-only final-gather requirement to reconciled client
velocity. The existing harness already documents that zero-input committed
replay can erase predicted Active velocity; the same replay runs in Startup.
The final-gather assertion now applies to authority/history, with its original
numeric thresholds. Client first-Active entry and actual corrected net-cut
assertions remain. No gameplay/reconciliation code changes. This bounded QA
correction was adversarially reviewed and isolated from the shot asset commit.
