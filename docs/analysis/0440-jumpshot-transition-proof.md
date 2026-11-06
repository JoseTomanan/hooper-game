# #440 — Normal jump-shot body motion and transitions

Issue #440; related ADR-0003/0014/0016/0027 and #418/#428/#429.
Baseline: main `e2034032f9d5f8fab7e8471879640823eb629587`.
Godot: 4.7.1 stable Mono, official `a13da4feb`. Measurement started 2026-10-27.
Delivery validation resumed 2026-11-03.

Human appearance and smoothness acceptance remains in #173. This record proves
specific presentation defects and their repair; it does not accept feel or the
known ball/hand coupling gap.

## Method and reproduced failures

`JumpshotFormCapture` loads production Main, drives actual movement/shot/feint
input, and records the final production-scaled skeleton at `SkeletonUpdated`.
Requested state, actual playback node/clip/time and fading state are recorded
separately. Drawn frames are read after `FramePostDraw`; headless traces are
explicitly diagnostic and supply no rendered evidence.

The matrix distinguishes fresh stationary Held, stationary Dribbling and moving
Dribbling, both authoritative entering hands, two continuous entry offsets,
normal shots and the earliest/latest legal local feints. The separate owning
client predicts its own player; a third ENet process observes that player and
the ball from their shared selected server tick. Owner ticks are never equated
with server ticks. Network feints record prediction and received authority
clocks; exact local edge coverage is not misrepresented as exact network timing.

The current normal path selects `JumpshotStartup`, `JumpshotActive` and
`JumpshotRecovery`. One release occurs at Active/0. A gross missing hookup was
not reproduced. The following failures were reproduced instead:

| Observable, final scaled production pose | Unchanged local baseline |
|---|---:|
| Normal hip horizontal excursion during a planted shot | 0.686524 m |
| Normal return: largest one-tick wrist displacement | 1.102728 m |
| Normal return: largest one-tick toe displacement | 0.932871 m |
| Early fake: one-tick upward hip jump on entering Recovery | 0.629312 m |
| Late fake: one-tick upward hip jump on entering Recovery | 0.261680 m |

The capsule stays stationary through these body jumps. Normal footage and
front/side diagnostic views reproduce the sideways catch/landing and snap to
neutral. The three source families remain frozen in
`tests/fixtures/jumpshot_before_440.res`; the older goalkeeper fixture remains
unchanged and separate.

Four ranked hypotheses were published before repair: inherited goalkeeper root
travel; mismatch with real neutral endpoints; playback routing/evaluation delay;
and ball coupling. Inspecting final poses distinguishes them. The normal author
and rebuild deliberately retained the goalkeeper's non-arm motion. Its root
drifts sideways and its landing is far from neutral. Separately, `Feint()` enters
Recovery at offset `20−8=12`, while `Travel()` restarts the animation at time zero.
The first observed local Recovery is frame 13 after the same-tick machine update;
its playback starts at zero and then evaluates the airborne apex. A remote RPC
can expose the transient authority frame 12 before the next physics tick. These
are different observation orders, not different legal move durations.

## Repair and scope

The normal rebuild retains the established shooting/guide arm form and vertical
load/jump, centers its hip position horizontally in the standing rig's frame,
and settles the full body into standing neutral during the landing. A
limb-preserving bridge connects the actual displayed neutral to the gather.
Body origins and support goals settle over four intervals; rotational and arm
goals use eight, reaching the authored pose at Startup/9. The shared bridge's
optional position weight preserves its existing default path for other moves.
The final eight Recovery keys settle the
body; the last two already match neutral, including the observed evaluation lag.

Shot entry uses the existing `CrossoverPoseBridge` after animation evaluation.
It retains the incoming hands/feet and rig proportions while connecting to the
gather within Startup. Ordinary state-machine entry fades reproduced a leg
excursion caused by rest-relative pose mixing; they were rejected against the
unchanged baseline. A fake bridges from the actual last Startup pose to its
grounded Recovery tail, reaching neutral by frame 19. A three-tick state-machine
fake fade still produced a larger late-fake step and was rejected too.
Return blending happens inside Recovery's last eight frames,
toward Idle or the actual left/right Held pose. Outgoing edges have no fade
which could delay an immediate follow-up move. Shared Held transition resources
and other moves' edges are not retuned. These are provisional technical values
for the later #173/#238 review, not accepted feel values.

`JumpshotRecovery` wraps its Recovery/neutral blend in a child `AnimationNodeTimeSeek`.
The request uses the displayed Recovery frame counter for both inputs. Seeking the child avoids
advancing the outgoing state, fade clock or remote history by twelve ticks. The
ordinary host/owner callback and existing manual observer evaluation remain
responsible for advancement. A zero-elapsed evaluation at a shot state boundary
resolves Travel before the final skeleton update; the one-shot seek request is
then reissued before the ordinary evaluation. The paced baseline reproduced
changed poses on the same held server source tick (40 raw changed edges across
24 of 72 role cases, maximum 1.740011 m). The final proof rejects every changed
duplicate within the shot and its eight neutral neighbors. No extra elapsed
simulation or presentation time is consumed.

Turnaround's existing normal-derived library is rebuilt with its unchanged
48/4/20 timeline. Fadeaway retains its authored rotation keys and relative
backward root displacement while inheriting normal root centering and exact
shared endpoints. This is dependency maintenance; #439's separate silhouette
request is not implemented here.

Gameplay remains 18/4/20, release at first Active, legal feints `[3,12)`, eight
Recovery ticks, existing cradle/dead-dribble behavior, bounded authoritative
Startup turning and actual release-angle classification. Ball attachment,
authoritative release origin, scatter and trajectory are outside this repair.

## Evidence rules

The new centered-body regression is red on the original normal assets. Its
`1e-4 m` floor represents numerical zero for an authored stationary root, rather
than a fitted replacement motion bound. It measures the final scaled skeleton
hip before the skeleton's world transform so an owner's legitimate whole-mesh
reconciliation correction cannot masquerade as authored hip travel.

Transition measurements retain both wrists, feet and toes, hips and torso.
Five adjacent source-tick poses surround each seam, including the first new
state and the two following evaluations. Reports include per-joint displacement,
velocity change, actual playback offsets, ball/wrist distance and source clocks.
At entry only, the comparative repair gate covers the actual boundary and two
following edges, and their outgoing velocity changes. The preceding wholly
neutral edge and incoming acceleration remain in the complete diagnostics. One
owning-client moving entry reproduced a larger **pre-shot** neutral foot step
(1.071305 m versus 1.018720 m full-neighborhood baseline maximum), while its
actual entry step was 0.0131 m. This unrelated neutral/prediction motion is not
claimed repaired. Fake and return gates retain their entire neighborhoods,
including repair-owned motion before the boundary. A first-shot-pose jump
mutation still fails the entry gate; a neutral-only pre-edge mutation remains
visible without being attributed to the shot.
Entry, fake-to-Recovery and return must improve against immutable matched-clock baseline data;
merely passing #418's old no-regression bounds is insufficient. Missing ticks,
changed duplicate poses and reset-spanning neighborhoods fail proof. Identical
held source frames are counted and reported rather than invented as new motion.

The pre-440 asset substitution must fail the root detector at the real playback
seam. Bypassing the Recovery seek child must fail the legal-feint playback-time
regression. Existing idle/goalkeeper substitutions retain their original form
detectors. Gap/duplicate/reset mutations use real captured traces.
The authority's elapsed Startup and Active ticks are checked separately from
frame-counter sets: inserting a repeated Startup/10 or Active/2 on an otherwise
uninterrupted source clock must fail the duration gate.

The dedicated fixture moves the idle observer's body out of the offensive lane;
otherwise it legitimately blocks the later-dribble check at Z=4. Reset waits for
the owner's neutral input sequence on authority before awarding fresh possession.
These fixture changes do not modify game movement, contact or possession rules.
Paced network capture slows producers by 80 ms per fixed simulation tick and the
observer by 16 ms per draw. It proves observed source-tick coverage, not latency
or performance. Earlier incomplete/rejected capture attempts remain scratch
diagnostics and are not substituted for complete evidence.

## Sources and limitations

ADR-0014 tier 2: [Jr. NBA jump-shot instruction](https://jr.nba.com/how-to-shoot-a-jump-shot/)
supplies the structural reference: bent-knee setup, separate shooting/guide arms,
vertical jump, extension/wrist snap, retained follow-through and balanced landing.
It prescribes no numerical pose or blend values. Tier 1 ADR-0003 retains readable
commitment and punish windows.

Official Godot 4.7 documentation verifies
[current/fading playback and positions](https://docs.godotengine.org/en/4.7/classes/class_animationnodestatemachineplayback.html),
[child TimeSeek requests](https://docs.godotengine.org/en/4.7/classes/class_animationnodetimeseek.html),
[BlendTree wiring](https://docs.godotengine.org/en/4.7/classes/class_animationnodeblendtree.html),
[transition crossfades](https://docs.godotengine.org/en/4.7/classes/class_animationnodestatemachinetransition.html),
[typed animation tracks](https://docs.godotengine.org/en/4.7/classes/class_animation.html),
and [the final modified skeleton signal](https://docs.godotengine.org/en/4.7/classes/class_skeleton3d.html#class-skeleton3d-signal-skeleton-updated).
No new Blender API or shared Blender-library change is needed for this repair.

The neutral input uses [Blend2](https://docs.godotengine.org/en/4.7/classes/class_animationnodeblend2.html)
and [BlendSpace1D](https://docs.godotengine.org/en/4.7/classes/class_animationnodeblendspace1d.html).
The existing limb bridge runs through
[SkeletonModifier3D](https://docs.godotengine.org/en/4.7/classes/class_skeletonmodifier3d.html),
after the mixer, preserving the rig's actual local scales. Across the complete
local before/after matrix, upper-arm length remains approximately 0.274047 m
and forearm length 0.276145 m for both sides (extrema differ by less than
0.000001 m). Neither rig dimensions nor bone scale tracks were retuned.

The pose bridge discards anchors on source-clock gaps/regression, display-source
changes and presentation epoch/resync changes. Forward/backward reset scenarios
compare the final modified pose against an identical production rig entering
Recovery directly. Their continuous-fake control must remain distinct. Bypassing
only the gap guard restores a stale Startup pose and fails with a 0.833215 m
joint discrepancy. The hookup audit follows actual connected primary inputs
through the new Recovery tree; disconnecting that input must fail its row.

The visible ball still uses its existing held offset and release origin. Footage
retains the ball and the reports measure gather/release wrist separation for both
origins. A green body/transition result does not establish hand/ball agreement.
No editor operation is needed to review the artifacts. #173 remains the human
appearance/transition acceptance surface; #185/#439 remain separate.

## Final objective results

Tested presentation source: `90a1cf3263d58aff7c8ccc400a815afcdaaa5444`.
Normal library SHA256: `769374D0DB51BF60B1B342C59083336399456FD80F42D454BF6ECA71ECC82766`.

The final executable matrix passed **108 role cases**: 36 local, 36 owning-client
and 36 separate-observer measurements. The network roles share 36 authority
episodes; they are not 72 independent games. All six pre-440 asset substitutions
failed only the intended horizontal-root detector. No changed duplicate pose
occurred within any shot plus its eight neutral neighbors, and no reset crossed
those proof windows. Normal authority phases consume 18/4/20 ticks; fakes consume
eight Recovery ticks, with zero releases. Normal releases occur once at Active/0.

| Maximum across the matched matrix | Before | After |
|---|---:|---:|
| Normal authored horizontal hip excursion | 0.688225 m | 0.000000489 m |
| Entry boundary/following position step | 1.018840 m | 0.310853 m |
| Entry outgoing velocity change | 1.008130 m/tick | 0.302797 m/tick |
| Fake-to-Recovery neighborhood position step | 1.406249 m | 0.310853 m |
| Fake-to-Recovery velocity change | 1.430280 m/tick | 0.310853 m/tick |
| Return neighborhood position step | 1.328909 m | 0.099317 m |
| Return neighborhood velocity change | 1.328909 m/tick | 0.104529 m/tick |

Each individual case must improve its own matched baseline, rather than merely
improving these aggregate maxima. Startup-to-Active and Active-to-Recovery retain
their full measurements and existing form/timing regressions.

The existing network-form proof also detected an excessive Startup/2-to-3
finger step in the initial four-interval bridge. Its
[red report](evidence/0440/startup-speed-regression-before.json.gz) and raw traces
remain available. The repair retains that proof's live legacy comparator and
thresholds. The [focused passing report](evidence/0440/startup-speed-focused-pass.json.gz)
records separate origin and rotation weights; its early-fake maxima
are 0.158389 m (Left origin) and 0.151329 m (Right), below the contemporaneous
legacy bounds of 0.167155 m and 0.175489 m. A
[whole-Startup diagnostic](evidence/0440/startup-compact-summary.json.gz)
checks that the improvement does not conceal a larger later peak: normal
core-joint maxima fall from 0.389529/0.436487 m to 0.204150/0.199876 m.
Later individual gather edges increase as motion is spread out, while remaining
below the previous peak. These focused captures identify HEAD `4c1ba92` with
the working-tree bridge subsequently committed unchanged as `90a1cf3`;
the final role matrix and footage identify the committed source directly.
The subsequent [committed-source network proof](evidence/0440/net-form-proof.json.gz)
passes all eight normal/fake/fadeaway cases against its
[eight live legacy measurements](evidence/0440/net-form-baseline.json.gz).
Its [mismatched-boundary control](evidence/0440/net-form-mismatched-boundary-control.json.gz)
fails specifically at Startup-to-Active as required.

[Local report](evidence/0440/final-local.json.gz),
[owner/observer report](evidence/0440/final-owner.json.gz),
[asset controls](evidence/0440/asset-only-control.json.gz) and
[matrix verdict](evidence/0440/summary.json.gz) retain all per-joint measurements,
phase clocks, release events and limitations. The original before reports remain
[local](evidence/0440/baseline-local.json.gz) and
[network](evidence/0440/baseline-owner.json.gz); the network report deliberately
remains red for its measured old changed-hold defect.

The known coupling gap remains large. For normal local shots, the right wrist
is 0.631--0.981 m from the visible ball during the sampled gather for Right origin,
and 0.848--1.153 m for Left origin. At actual Active/0 release the distances are
1.493 m and 1.637 m respectively. The gameplay panels retain the ball; no framing,
attachment or launch-origin offset conceals this limitation.

Both game build modes pass with zero warnings/errors. The unit suite passes
1,322 tests with five intentional characterization skips; repository discovery
passes 126 tests with one existing skip. The original single-process sweep found
only the bare-leaf hookup-audit assumption introduced by the Recovery wrapper;
its connected-primary correction and disconnect control now pass. All ten form
scenarios and three reset/control scenarios pass. Exact asset verification
preserves all 68 unrelated clips and fadeaway/normal shared endpoints.
The final committed-source rerun passes all 106 selected single-process cases,
including both crossover paths, normal/fadeaway animation, post play and the
heading matrix, plus all three moving-crossover network controls with both peers
passing. The [QA summary](evidence/0440/qa-summary.json.gz) retains the exact
inventory, versions and resolved launcher failures.
The [metric summary](evidence/0440/final-metrics.json.gz)
records the table's extraction rules, extrema, wrist measurements and rig lengths.

Mutation outputs are retained for the
[Recovery clock](evidence/0440/440-clock-mutation-final-console.log.gz),
[stale anchor](evidence/0440/440-reset-mutation-console.log.gz) and
[disconnected primary](evidence/0440/440-audit-mutation-console.log.gz).
The [asset verification log](evidence/0440/440-verify-assets-final-console.log.gz)
records the shared-asset preservation checks.

## Rendered review artifacts

The [evidence index](evidence/0440/README.md) links the full comparison inventory.
The [gameplay comparisons](evidence/0440/gameplay/README.md) cover all three
entry states, both hands and normal/early/late variants at offset zero.
[Front](evidence/0440/front/README.md) and [side](evidence/0440/side/README.md)
views retain the complete body. The [separate ENet observer comparisons](evidence/0440/network/README.md)
cover both entering hands. Each package includes normal-speed source-tick films,
slow-motion films, contact sheets, compressed raw traces/manifests and a frame
index containing original PNG hashes, observed cameras and independent clocks.

The normal-speed films project one observed frame per consecutive simulation
source tick at 60 Hz. They do not reproduce the old implementation's changes
while holding a source tick. The observer's Right-origin baseline includes one
such raw changed edge; its separately linked diagnostic shows both original
observations at 500 ms per frame. This omission from the main projection is
explicit in every package; it is never treated as baseline continuity.

Rendered owning-client captures are a separate diagnostic surface. Their ball
history resets during the review window, so the strict comparison packager
rejects them. The [rejected owner diagnostics](evidence/0440/owner-diagnostics/README.md)
retain both hands, all observed owner physics frames, visible reset markers and
their independent authority events. They provide no rendered-owner continuity verdict. Accepted
owning-client continuity comes from the reset-free headless role matrix above;
the separate observer films establish rendered shared-source-tick coverage.

## Acceptance evidence map

| #440 criterion | Evidence and boundary of the claim |
|---|---|
| Reproduce before repair | Frozen `e2034032` assets, before reports and rendered manifests; ranked hypotheses and measured failures above |
| Full-body repair | Matched gameplay/front/side films, centered authored root and measured landing repair; appearance remains #173 |
| Legal transition coverage | 108 role cases: three entry states, two hands, three variants and two offsets, with real input/possession premises and later legal dribbling |
| Honest continuity oracle | Matched immutable per-case bounds, final scaled joints, actual playback positions, adjacent source ticks, duplicate/reset rejection and diagnostic pre-entry edges |
| Real network presentation | Predicted owner plus separate ENet observer; independent event clocks, coherent remote player/ball ticks, explicit pacing/holds and rejected rendered-owner reset diagnostics |
| Gameplay/shared assets | First-Active release, zero fake releases, unchanged phase durations, normal/fadeaway/turnaround/heading regressions and exact unrelated-clip checks |
| Discriminating failures | Pre-440 root substitution, clock bypass, stale-anchor and disconnected-primary mutations; original idle/goalkeeper controls |
| Reviewable delivery/gates | Compressed reports and raw rendered traces/manifests, normal-speed films, source citations and commands; exhaustive CI and final code review remain merge gates |

## Reproduction commands

Use the repository-pinned Godot 4.7.1 Mono binary in `$env:GODOT`. Each output and
run ID must be fresh. Do not overlap another heavy network capture with this
paced matrix: a late predicted feint can legitimately reach authority outside
its legal window, which is an incomplete fixture episode and fails capture.
Rejected attempts remain diagnostics; they are never accepted as proof.

```powershell
dotnet build "HOOPER GAME.csproj" -p:IncludeIntegrationHarness=false
dotnet build "HOOPER GAME.csproj" -p:IncludeIntegrationHarness=true
dotnet test tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj
python -m unittest discover -s tests/repository -p 'test_*.py'
python tools/harness_catalog.py run --scene JumpshotFormTest.tscn --godot $env:GODOT --run-id <fresh-form-id>
python tools/harness_catalog.py run --scene JumpshotPoseResetTest.tscn --godot $env:GODOT --run-id <fresh-reset-id>
python tools/harness_catalog.py run --id committed-clip-hookup-audit --godot $env:GODOT --run-id <fresh-audit-id>
python tools/run_jumpshot_transition_proof.py --godot $env:GODOT --output .godot/440/<fresh-proof>
python tools/run_jumpshot_transition_capture.py --godot $env:GODOT --output .godot/440/<fresh-gameplay> --offsets 0
python tools/run_jumpshot_transition_capture.py --godot $env:GODOT --output .godot/440/<fresh-front> --entries dribble --variants normal --offsets 0 --view front
python tools/run_jumpshot_transition_capture.py --godot $env:GODOT --output .godot/440/<fresh-side> --entries dribble --variants normal --offsets 0 --view side
python tools/run_jumpshot_transition_capture.py --godot $env:GODOT --output .godot/440/<fresh-owner> --topology owner --entries dribble --variants normal --offsets 0 --paced --pacing-ms 80 --port 24750
python tools/package_jumpshot_transition_evidence.py --before .godot/440/baseline-gameplay --after .godot/440/<fresh-gameplay> --output docs/analysis/evidence/0440/<fresh-package> --label local-gameplay
python tools/package_jumpshot_owner_diagnostics.py --before .godot/440/baseline-owner-native-final-ready --after .godot/440/<fresh-owner> --output docs/analysis/evidence/0440/owner-diagnostics/<fresh-package>
```

`run-jumpshot-transitions.sh` is registered in the executable catalog; exhaustive
CI runs it alongside the existing normal/fadeaway/turnaround/heading/network
regressions. The old fixture restores assets only under current playback, not
an invented historical full implementation. The frozen before films were
captured before the production edits; no baseline asset or measurement was
regenerated to improve a verdict.
