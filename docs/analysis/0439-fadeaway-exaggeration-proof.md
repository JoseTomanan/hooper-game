# #439 - Further fadeaway exaggeration and routing diagnosis

Measured 2026-11-06 using Godot .NET 4.7.1, Blender 5.2.0 LTS,
.NET 8 and Python 3.14. This implements the maintainer's further
legs-forward / upper-body-back request. Visual strength and feel remain
human judgments in #185/#173.

## Method and provenance

The baseline is main `30ca87d`, including #440's normal-shot repair. The
final asset is `c586dc3`. `JumpshotFormCapture` loads production Main/Player
scenes and shoots through production input. It records authoritative release
heading/classification, selected AnimationTree state/clip/playback time and
the final modified skeleton at `SkeletonUpdated`. Native review frames come
from `FramePostDraw`; headless traces are explicitly state diagnostics.

The pre-439 selected clip is frozen in `tests/fixtures/fadeaway_before_439.res`
(SHA-256 `69aafc47e8a9c8785c2d3d93df4978b40269a79803268c8658f6dbddd268c709`).
Registered proofs substitute it only into a duplicated subject library.
Classification, playback, scene transitions and other animations stay live.
The final production library SHA-256 is
`349fa8dbe342e83828fa5fff8080c2cad1dcda471df660e3939e622f42ff80a6`.

## Routing diagnosis

Production-input shots starting 90 and 180 degrees from the rim, with the
default 900 degrees/s turn rate and either entering hand, all finish Startup
with **zero residual release angle**, classify ordinary and select
`JumpshotActive`. Their same-tick evaluated poses and complete traces are
retained in [diagnosis/summary.json](evidence/0439/diagnosis/summary.json) and
the adjacent compressed records. The 44 existing heading harness cases pass,
including explicit release-angle boundary controls.

Thus an approximately 90-degree starting heading does not imply a fadeaway.
ADR-0010's #428 amendment deliberately classifies actual release heading.
These controlled observations do not reconstruct the maintainer's unspecified
session or establish that every possible routing problem is absent.

For a genuinely unfinished turn, the existing diagnostic fixture uses 90
degrees/s and an explicit away-facing entry. Both hands release at about
155.7 degrees residual divergence, classify fadeaway, select `FadeawayActive`
and evaluate the authored pose. This fixture proves the conditional
presentation path; its slower turn rate is not a production tuning change.

## Authored result and discriminating proof

The final targets retain the 0.50 m hip retreat, increase torso pitch from
55 to 78 degrees and move ankle targets from 0.35 m forward / 0.70 m down to
0.60 m forward / 0.50 m down. The five-key envelope stays `0, .8, 1, .5, 0`.
These are authored presentation values following the human direction, not
measurements of basketball or accepted feel settings.

The existing normal arm solver runs again after applying the target lean.
This preserves the overhead shooting reach instead of carrying the arms
backward rigidly with the spine. Final blended core poses independently
check shooting/guide elbow asymmetry and wrist height above the shooting
elbow. A trace counterfactual lowering that wrist fails specifically on
`fadeaway shooting reach missing at Active:2`. Its report is retained as
[arm-negative.json](evidence/0439/arm-negative.json); the throwaway mutation
script is not committed. Reproduce it by copying a headless final trace,
putting `RightHand.body[1]` below `RightForeArm.body[1]` at the coherent
Active/2 pose, and rerunning the fadeaway analyzer.

Body forward is +Z after inverse display heading/root. Every knee and ankle
must improve its lead over both upper-torso landmarks AND over Hips at
Active/2 and Active/3. Each improvement must exceed the sum of the paired
float32 coordinate resolutions; no taste threshold is inferred. The
unchanged pre-439 asset fails this new comparison. Existing missing/stale
pose and coherent remote tick checks remain required.

Local headless measurements (metres, either hand, rounded):

| Quantity | Pre-439 | Final |
|---|---:|---:|
| Minimum leg lead over torso, Active/2 | 0.57596 | 0.65442 |
| Minimum leg lead over torso, Active/3 | 0.33992 | 0.41265 |
| Minimum leg lead over Hips, Active/2 | 0.34576 | 0.39756 |
| Minimum leg lead over Hips, Active/3 | 0.18446 | 0.22001 |
| Startup/Active neighborhood maximum bone step | 1.10055 | 1.05706 |
| Active/Recovery neighborhood maximum bone step | 0.69307 | 0.68416 |
| Recovery/neutral neighborhood maximum bone step | 0.03533 | 0.03533 |

The transition detector retains all three adjacent one-source-tick edges,
matching playback clocks and the original motion bounds. Endpoint equality
alone is insufficient. The larger existing entry movement remains visible;
these measurements do not certify smoothness. Both material position and
rotation endpoints exactly match normal Active. All 71 other animations are
fingerprint-identical, including the normal shot family and derived Turnaround.
Independent Blender exports match all 325 frame/bone pairs exactly; a second
Godot rebuild is byte-identical.

## Rendered review evidence

The [capture plan](evidence/0439/capture-plan.json) selects both hands in local
gameplay, local side and genuine ENet observer side views. Before/after pairs
use complete matching phase counters and playback clocks. Observer player,
ball and final pose share one selected server tick. Missing ticks or reset
spans cannot prove continuity. Paced observer capture uses 160 ms producer
delays with fixed 60-Hz simulation steps; it is diagnostic coverage, not a
real-time network or performance claim.

Start with [Right side](evidence/0439/local-side-right-fadeaway.gif),
[Left side](evidence/0439/local-side-left-fadeaway.gif),
[Right gameplay](evidence/0439/local-gameplay-right-fadeaway.gif) and
[Right remote side](evidence/0439/remote-side-right-fadeaway.gif).
The [frame index](evidence/0439/frame-index.json) includes both hands, original
frame hashes, exact retained core PNGs, manifests and compressed raw traces.
Films show uncropped full frames resized with labels outside source pixels,
at 2.4x slow motion. The existing visible ball/wrist separation is retained;
this asset change does not repair attachment.

The final package audit checked all 600 indexed source-frame hashes, all 24
retained originals, every compressed source record and all six films' 50-frame,
40-ms playback clocks. All matched their source captures.

## Validation and reproduction

The game builds with zero warnings/errors. The unit suite has 1,322 passes
and five intentional characterization skips. Repository checks run 130 tests
with one platform skip; on Windows they require working Git Bash and writable
temporary directories. The eleven JumpShot animation cases pass unchanged,
as do the existing normal/fake form and classification controls.

The first CI run exposed a shared capture-launcher compatibility regression:
the transition matrix supplies a namespace without the newly added optional
heading fields. The launcher now defaults a missing heading option to absent.
Two command-construction regressions cover that legacy caller and an explicit
zero-angle request. The real local transition matrix also passes all 36 cases
and its six asset-only negative controls after the repair.

Registered `fadeaway-form` and `net-fadeaway-form` compare the legacy and
pre-439 controls, require the stronger final pose and preserve original
whole-body motion bounds. Upright substitution and the displaced-wrist
boundary counterfactual must fail their own detectors. Release remains once
at first Active with 18/4/20 timing; early/late legal fakes retain no release.

```powershell
& $env:BLENDER --background --python-exit-code 1 --python tools/author_fadeaway.py -- "assets/Goalkeeper Catch Stationary.fbx" assets/fadeaway_authored.fbx
# Headless editor import needs unsandboxed editor-settings access on Windows.
& $env:GODOT --headless --editor --path . --import --quit --log-file .godot/439-import.log
& $env:GODOT --headless --path . --log-file .godot/439-rebuild.log -s tools/rebuild_jumpshot_clips.gd
dotnet build "HOOPER GAME.csproj" --configuration Debug -p:IncludeIntegrationHarness=true
dotnet test tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj --configuration Debug
python tools/harness_catalog.py run --scene JumpshotAnimTest.tscn --godot $env:GODOT
python tools/harness_catalog.py run --id fadeaway-form --id net-fadeaway-form --godot $env:GODOT
```

Use `tools/run_jumpshot_capture.py --heading-angle 90 --variant normal` for
the production-rate heading diagnostic; add `--headless` for state-only output.
The existing explicit fadeaway fixture uses `--variant fadeaway`. For native
before/after reproduction, use the capture plan's views, hands and topology;
`--fadeaway-substitute before439` supplies the frozen selected baseline.
Then package with `tools/package_fadeaway_evidence.py --issue 439 --plan
docs/analysis/evidence/0439/capture-plan.json`.

No editor setup is needed to inspect the films. #185/#173 remain open for
human visual/feel acceptance; numeric improvements do not establish that the
exaggeration is sufficient or desirable.

Official API sources: [final modified Godot skeleton poses](https://docs.godotengine.org/en/4.7/classes/class_skeleton3d.html#class-skeleton3d-signal-skeleton-updated),
[3D animation tracks](https://docs.godotengine.org/en/4.7/classes/class_animation.html),
[isolated resource duplication](https://docs.godotengine.org/en/4.7/classes/class_resource.html#class-resource-method-duplicate)
and [Blender PoseBone matrices](https://docs.blender.org/api/current/bpy.types.PoseBone.html#bpy.types.PoseBone.matrix).
Blender's web reference was unavailable during this run; the installed 5.2
RNA/API documentation verified armature-space matrices, immediate frame
evaluation, key insertion and quaternion interpolation directly.

## CI follow-up 2026-11-07

[Run 42222242696](https://github.com/JoseTomanan/hooper-game/actions/runs/42222242696)
failed only `net-rebound-presentation` on shard 3; `integration-test` propagated
that failure. The retained `feint-owner` manifests report two predicted owning
actions and two predicted feints, but one authoritative shot release. The first
server shot traversed Startup, Active and Recovery; the second went directly
from Startup to Recovery. Five unchanged local owner captures passed, confirming
that a single local green run does not exclude this timing-sensitive failure.

The rebound interruption fixture requested its fake at client Startup frame 3,
the lower edge of the server's independent `[3,12)` legal window. Begin and
Feint use reliable RPCs: delivery preserves order, without preserving their
spacing in server physics ticks. The trace is consistent with an early server
rejection; it does not record the exact frame at RPC receipt. This fixture now
requests frame 7, inside the window, as the existing shot-network fixture does.
The production move, window, transport and release rules are unchanged.

The fixture also counts each authoritative Startup-to-Recovery transition,
requiring a witnessed Startup, no intervening Active and at most one count per
seed ordinal. Both authority feints must be observed in addition to the existing
predicted-owner checks and zero-release assertion. Manifests retain those
authority events and their count. This separates predicted acceptance from
server acceptance and keeps a late or rejected fake red.

Fresh local and ENet owner controls both recorded two authoritative feints
and zero releases. Temporarily suppressing only the second fixture feint made
the local capture exit 1 with `control did not witness two authoritative
accepted feints`, count 1 and one release. The mutation was removed. Ordinary
and harness-enabled builds passed, as did all 1,322 unit tests (five intentional
skips) and 130 repository tests (one platform skip). Repository subprocess/temp
tests required an unsandboxed Windows run with Git Bash on PATH.

Source: [Godot 4.7 RPC transfer modes](https://docs.godotengine.org/en/4.7/tutorials/networking/high_level_multiplayer.html#remote-procedure-calls).
The interior frame is a fixture timing choice, not an engine guarantee or a
change to the legal gameplay window. No editor steps or renewed visual sign-off
are required by this CI repair.
