# #440 — Normal jump-shot body motion and transitions

Issue #440; related ADR-0003/0014/0016/0027 and #418/#428/#429.
Baseline: main `e2034032f9d5f8fab7e8471879640823eb629587`.
Godot: 4.7.1 stable Mono, official `a13da4feb`. Measurement started 2026-10-27.

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

| Observable, final scaled production pose | Unchanged baseline |
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
and settles the full body into standing neutral during the landing. A four-interval
limb-preserving bridge connects the actual displayed neutral to the gather.
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

The visible ball still uses its existing held offset and release origin. Footage
retains the ball and the reports measure gather/release wrist separation for both
origins. A green body/transition result does not establish hand/ball agreement.
No editor operation is needed to review the artifacts. #173 remains the human
appearance/transition acceptance surface; #185/#439 remain separate.
