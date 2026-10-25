# #429 — Fadeaway lower-body silhouette

Measured 2026-10-25 with Godot .NET 4.7.1, .NET SDK 8.0.421,
Blender 5.2.0 LTS, Python 3.14 and Pillow 12.0. This implements the
maintainer's legs-forward/upper-body-back direction from #429. Visual strength
and feel remain human judgments in #185/#173; this does not accept either.

## Method and provenance

`JumpshotFormCapture` loads production `Main.tscn`/`Player.tscn`, drives the
existing input path, and records each final `SkeletonUpdated` pose after rig
scaling and modifiers. `FramePostDraw` associates the pose with its rendered
frame and camera. A genuine ENet client observes the server's player; it is
not another locally driven actor. Player/ball/pose must share the selected
presentation tick. Reset spans and missing source ticks cannot prove continuity.

The original animation library was frozen at main `62a2819`, SHA-256
`7d50b57e043e7db710a51ee8c2ba88534fb9b756a3a393465b97c7b43fea5c2b`.
The selected original clip is preserved in
`tests/fixtures/fadeaway_before_429.res`. Later before captures explicitly
substitute only that clip in a duplicated subject library, keeping the same
production selection seam and clock. Their manifests distinguish the on-disk
library hash from the substituted fixture hash. Original unsubstituted
captures are retained in the scratch run directories.

The replacement asset is commit `8e4e001`; the capture instrument and registered
proof are `d2708f5`. The rebuilt library SHA-256 is
`e36a16be4c3313a906518d922b0c48b7ebed614522f8379f5e0ad2390d5cdda9`.
`71188c9` updates the live catalog ordering/weight tests for the two new cases.

After #428, normal shots turn during Startup and an ordinary back-facing Begin
can square up before release. This capture deliberately uses the existing
90-degrees/s unfinished-turn fixture, then independently checks actual residual
release angle >=90 degrees and the production `FadeawayActive` selection.
Production turning, heading, accuracy and classification are unchanged.

The strengthened premise check exposed an old Left-hand fixture defect:
reaching that hand outlasted the fixed backward-movement interval, so its
"fadeaway" was an ordinary shot. The instrument now sets an explicit away-facing
heading immediately before shooting for either hand. Those invalid original
captures are excluded from the claimed silhouette comparison.

## Discriminating form contract

Body forward is **+Z after inverse display heading/root**, independent of the
legs or spine being measured. Upper-torso front is the greater projection of
`mixamorig_Spine2` and `mixamorig_Neck`. Each knee (`RightLeg`, `LeftLeg`) and
ankle (`RightFoot`, `LeftFoot`) must lie ahead of **both** landmarks and ahead
of **Hips**. The extra hip-relative ordering rejects merely bending the spine
behind an unchanged lower body. The gate
uses the minimum of all four leads, never the better leg or an averaged torso.
Every constituent projection is retained in the reports and raw traces.

Core samples are final evaluated **Active/2 and Active/3**. All Active/0–3
observations are published: /0 still carries Startup's endpoint; /1 evaluates
the shared normal entry key. Neither is claimed as the bilateral core pose.
This retains the production phase/pose reporting lag rather than changing the
animation clock or relabelling its samples.

Independent baseline repeats have **zero component difference** over all 41
shared shot phase/frame keys and every bone origin. The ordering margin is the
existing float32 coordinate-resolution calculation, not a taste threshold.
New torso-relative core leads exceed half a metre, and hip-relative leads are
at least +0.304862 m at Active/2 and +0.345760 m at Active/3. Precision is below
two micrometres.

The [complete verification report](evidence/0429/verification.json) publishes
each constituent projection, release window, boundary edge and playback clock
for all **22 selected pairs**. There are 16 gameplay comparisons (both hands,
local and genuine remote, normal/early fake/late fake/fadeaway), four local side
comparisons (both hands, normal/fadeaway), and two remote side fadeaways.
Normal and fake observer controls use the complete gameplay comparisons;
redundant remote-side normal attempts are excluded, not claimed as passing.

Review the [right remote side film](evidence/0429/remote-side-right-fadeaway.gif)
and [left remote side film](evidence/0429/remote-side-left-fadeaway.gif), then
the corresponding gameplay, normal-shot and fake films in the
[frame index](evidence/0429/frame-index.json). Contact sheets are `.png` beside
each film. Exact selected core/control PNGs, complete raw pose traces and both
observer/authority manifests are retained with hashes. Full review frames are
uncropped and resized with labels outside the source pixels, at 2.4x slow motion.

The [selection record](evidence/0429/pair-selection.json) retains all rejected
whole-case attempts and their reasons. Native side readbacks measured median
94–96 ms per frame, slower than the original 80 ms producer delay; that pace
caused missing presentation windows. Commit `a1c492e` adds an explicit 160 ms
diagnostic option while preserving the 80 ms default and 60-Hz simulation steps.
Both final remote-side pairs use 160 ms in observer and authority manifests.
Remaining attempts with differing animation playback clocks were rejected even
after coverage became complete. Selection uses whole independent cases, never
mixed frames; this is not a universal real-time delivery or performance claim.

Local gameplay measurements (metres, rounded; complete values in the report):

| Hand | Final pose | Before minimum lead | After minimum lead |
|---|---|---:|---:|
| Right | Active/2 | -0.211163 | +0.508698 |
| Right | Active/3 | -0.095599 | +0.575957 |
| Left | Active/2 | -0.211163 | +0.508698 |
| Left | Active/3 | -0.095599 | +0.575957 |

The old pose genuinely fails the two-leg contract. An ordinary Active asset
substituted for `fadeawayactive` at the actual production selection seam also
fails the form check while retaining the valid release/classification premise.
Oracle tests separately put each of the four joints behind the torso, reject a
spine-only substitute whose legs still trail Hips, and reject missing, stale or
incoherent core observations. The spine-only case demonstrated a real failing
test before adding the hip-relative detector; all five oracle tests now pass.

## Authoring, boundaries and preserved behavior

`author_fadeaway.py` samples the owned source at its actual 30-Hz source rate,
then authors output keys at 60 Hz over normal release frames 18–22. It reuses
the #418 shooting arms and the shared bilateral leg solver. The hip/pitch targets
retain 0.50 m/55 degrees; ankle targets are an authored interpretation of the
human direction, not a basketball measurement or accepted tuning value.
The five blend weights are 0, 0.8, 1, 0.5, 0.

The rebuild copies exact normal Active endpoint values after import, including
position and all material rotations. The verifier checks both inventories and
exact endpoint bytes. Godot serializes key times as float32(4/60) while retaining
Animation.length as double(4/60); the verifier accepts those exact representations
without an arbitrary time epsilon.

All **71 other animations** are fingerprint-identical, including the three
normal shot clips. The shared rebuild's derived Turnaround output is unchanged.
Two independent Blender exports have zero pose delta across **325 frame/bone
pairs**. A second Godot rebuild is byte-identical.

Final-pose continuity keeps the original three adjacent one-source-tick boundary
edges and compares matching phase/frame **and playback clocks**. Missing samples
or changed playback offsets fail; no larger motion threshold is fitted to the
replacement. For the Right local gameplay fadeaway:

| Boundary neighborhood | Before maximum bone displacement | After maximum |
|---|---:|---:|
| Startup → Active | 1.293907 m | 0.114445 m |
| Active → Recovery | 1.854183 m | 0.703865 m |
| Recovery → neutral | 1.360949 m | 1.360949 m |

These are measured envelopes, **not claims that every transition is visually
smooth**. In particular, the existing shared neutral return remains large and
unchanged. A deliberately displaced wrist fails the boundary detector against
the new positive envelope. The mutation is sized from new motion; the much
larger old discontinuity would make that particular control inconclusive.
The actual replacement still has to meet the original before/after bounds.

Existing `JumpshotAnimTest` cases pass with the original pitch, hips-back,
upright normal-shot, airborne, routing, non-looping, duration and complete
material-rotation guards. Gameplay remains 18/4/20 with exactly one first-Active
release, and legal early/late pump-fakes retain zero releases and their existing
Recovery/neutral returns.

## Validation

Final local checks: game build has zero warnings/errors; unit suite has 1,279
passed and five intentional characterization skips. Repository contracts run
106 tests with one platform skip and no failures. Both registered form adapters pass,
including genuine ENet, upright substitutions and the boundary counterfactual.
The original 11 jump-shot animation cases retain their unchanged guards.

[Implementation CI on 71188c9](https://github.com/JoseTomanan/hooper-game/actions/runs/40150615071)
passed all 452 catalog cases, all four integration shards, build/unit tests and
the existing rendered jobs. Subsequent changes strengthen the hip-relative
oracle and add the explicitly recorded diagnostic pace and evidence package;
the PR's checks provide the final-head verdict.

Independent reviews found no introduced correctness defects. The final artifact
audit recomputed all 22 proofs and checked all 1,768 indexed source PNG hashes,
76 byte-identical retained originals, complete compressed observer/authority
records, contiguous selected ticks, and GIF frame counts/40-ms durations.

## Reproduce and recheck

```powershell
& $env:BLENDER --background --python-exit-code 1 --python tools/author_fadeaway.py -- "assets/Goalkeeper Catch Stationary.fbx" assets/fadeaway_authored.fbx
# Import with an unsandboxed headless editor; editor-settings writes require it.
& $env:GODOT --headless --editor --path . --import --quit --log-file .godot/429-import.log
& $env:GODOT --headless --path . --log-file .godot/429-rebuild.log -s tools/rebuild_jumpshot_clips.gd
dotnet build "HOOPER GAME.csproj" --configuration Debug -p:IncludeIntegrationHarness=true
dotnet test tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj --configuration Debug
python tools/harness_catalog.py run --scene JumpshotAnimTest.tscn --godot $env:GODOT
python tools/harness_catalog.py run --id fadeaway-form --id net-fadeaway-form --godot $env:GODOT
```

Native capture: `tools/run_jumpshot_capture.py --commit <revision> --output
.godot/418/<fresh-directory> --view gameplay|side`; add `--remote --paced` for
the diagnostic real observer, and `--fadeaway-substitute before429` for the
frozen selected baseline. Both hands and normal/early/late/fadeaway run by default.
Use `--pacing-ms 160` for the final remote-side diagnostic protocol. The exact
selected source directories are in [capture-plan.json](evidence/0429/capture-plan.json);
after reproducing those fresh captures, package them with
`python tools/package_fadeaway_evidence.py --plan docs/analysis/evidence/0429/capture-plan.json`.
Use a different `--port` for any concurrent session. Paced remote films are
coverage diagnostics, not latency or performance evidence.

No editor setup is needed to inspect the films. The authoritative ball remains
at its existing fixed release origin; the visible ball/wrist gap is retained in
the traces and is **not repaired or concealed** by this pose edit. At the
first-Active release, selected observer gameplay examples measure about 1.358 m
(Right origin) and 1.635 m (Left origin) between wrist and visible ball. Those are
observed gaps, not repaired attachment. Rim-facing semantics remain #428's
actual-release-heading rules. #185/#173 stay open for
the human's silhouette/feel decision; no further gameplay scope is implied.

Official framework sources: [Godot final modified skeleton poses](https://docs.godotengine.org/en/4.7/classes/class_skeleton3d.html),
[3D animation tracks](https://docs.godotengine.org/en/4.7/classes/class_animation.html),
[resource duplication](https://docs.godotengine.org/en/4.7/classes/class_resource.html#class-resource-method-duplicate),
and Blender 5.2's installed API/RNA documentation for
[PoseBone matrices](https://docs.blender.org/api/current/bpy.types.PoseBone.html),
scene frame evaluation, keyframe insertion and baked FBX export.
