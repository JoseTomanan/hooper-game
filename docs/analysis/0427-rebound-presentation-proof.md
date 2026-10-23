# #427 — Rebound onset, pose continuity and carrying contact

Date: 2026-10-23. Baseline: `b74c68c`. Production repairs: `dce36aa`, `e1e146d`.
Related: #431 (owning-client recovery observation), #432 (pose/contact),
ADR-0008/0012/0016/0027 and human appearance/readability pass #173.

## Finding and repair boundary

The reported half-second onset delay was **not reproduced as a measured delay**.
On the authority, recovery occurred at physics tick 62 and the catch began at
63. The 30-tick setting is display duration. Two separate defects were reproduced:

- The compressed goalkeeper catch moves the carrying palm away from the held
  ball. Maximum palm-to-ball-surface error was 1.285937 m; adjacent body-space
  hand/finger displacement reached 0.790607 m and feet reached 0.392765 m.
- An owning client can miss the short Loose state between possession snapshots.
  The old latch then plays zero catches for two genuine authoritative recoveries.
  `baseline-owner` initially exposed a capture-completion bug; the retained
  `probe-owner-completion-fix` capture fixes that observer bug and independently
  confirms the missing owning-client edge. A failed capture is not a proof pass.

Floor-to-held relocation is a third, distinct observation: the live ball moves
about 1.4 m into its existing attachment after an instantaneous XZ-only recovery.
This repair does not make that pickup continuous. It does not change pickup
height/range, possession timing, authoritative heading/hand/attachment, movement
prediction, committed moves or the remote history policy. Those changes would
need their own scope. The maintainer explicitly authorized bounded repairs after
investigation. Independent build children #431/#432 record the selected work,
with #432 blocked by #431 for its complete owner proof.

## Method and clocks

`ReboundPresentationCapture.tscn` instances production `Main.tscn`, including
the real Player/Ball scenes, mesh, AnimationTree callback, rig and modifiers.
It seeds only fixture positions and a Loose ball, then lets production contest,
possession, input, reconciliation and presentation execute. Network captures use
separate server/client processes and real ENet. Two separated recoveries are
required. A completion manifest alone means capture succeeded, not behavior.

The trace retains authority recovery events, live holder/state/ball, tree
node/time, final skeleton bones, displayed ball, correction roots, heading,
selected player/ball source ticks, holds and reset state. `SkeletonUpdated`
captures final poses after modifiers. The analyzer requires bones and ball from
the same observation, matching physics frame, adjacent source ticks and complete
entry/catch/exit coverage. Remote observer time is the selected server source
tick; local/server/owner time is local physics time. Owner onset is measured
after the confirmed receipt becomes observable, **not** by subtracting peer clocks.
These are source-time checks, not a network wall-clock latency claim.

Contact uses the midpoint of the carrying Index1/Pinky1 finger bases as a palm
surface proxy: `abs(distance(palm, displayed ball center) - ball radius)`.
It does not substitute the wrist for the palm. Capture v2 saves the final
skeleton-relative bones at `SkeletonUpdated`, then applies the **current sampled
model transform** when observing the displayed ball. Body-space displacement
removes that model’s rigid rotation and translation, retaining world-metre scale,
then compares adjacent final Head, wrists, finger bases, feet and toes. The drawn
skeleton matrix and body-space points are both retained for independent checks.
Missing bones, timestamps, eligible
contact samples or source ticks fail rather than disappearing from an average.

Frozen repair gates are onset within two ticks, stationary Held contact error
at most 0.025 m on every sampled age 6–29 (at least 15 samples per recovery),
hands/fingers at most 0.30 m/tick and feet/toes/head at most 0.24 m/tick.
Continuity includes the preceding pose, the full catch and 21 subsequent source
ticks. The modifier must release to the real neutral pose by tick 20;
the following unmodified tick is also checked. These are regression bounds, not human acceptance
of animation quality. Moving cases require at least 15 Dribbling observations,
speed reaching 1 m/s and carrying-wrist vertical travel of at least 0.1 m;
palm contact is not asserted while the ball bounces.

## Implementation and sources

The existing authority snapshot now repeats cosmetic recovery identity,
recipient, authority-computed age and owning input sequence. Reconciliation
publishes these fields together with holder/clear state, avoiding a player tick
consuming a new receipt against the old holder. The owner consumes each receipt
once. Existing predicted catches are not restarted; stale, wrong-recipient,
lost-possession and pre-interruption receipts cannot revive the flourish.
The server sends four additional integer fields; no additional RPC is sent.
Their serialized transport overhead is not characterized here.

`ReboundPose`, a final skeleton modifier, bridges the actual incoming displayed
pose into existing contact-authored Held or handed idle/moving Dribble resources
over six consumed source ticks, then bridges back into the evaluated neutral
tree. Moving footwork and hand motion remain live. Remote holds freeze the
bridge; epoch/resync changes invalidate its history. A neutral reset does not
leave a flag that skips the next catch entry. Committed moves bypass it
immediately. The old catch resource remains available as an explicit control.

Hosted CI exposed a capture-v1 error: it combined signal-time **world** bones
with later sampled root/heading after visual yaw or reconciliation changed.
This reported a 0.308386 m moving-owner toe step. A suspected gait-clock repair
was investigated, but a control restoring the original clock **passes** with
the corrected v2 measurement. The extra clock change and its mutation hook were
therefore withdrawn; the original gait sampling clock is retained.
Those comparisons did not establish a gait-clock defect.
`phase-v2-control` retains the passing discriminator. The shifted fixture
(`--seed-delay 7`) changes only recovery setup timing and remains positive
coverage. Bounds and the six-tick bridge duration were not widened.

Broader v2 captures separately exposed real destination changes: Held becomes
Dribbling partway through the bridge, and an owning correction can replace the
neutral playback pose during exit. The final modifier limits correction steps
from the previous final pose across **all bone origins**, in world-metre body
coordinates, to 0.18 m per consumed source tick. A finite eight-candidate search
uses the existing limb-preserving pose bridge; it leaves normal small steps
unchanged. The independent proof bounds remain 0.24/0.30 m. Committed actions
bypass this correction, and resynchronization discards prior source history.

The exit rejoins after the nominal six intervals and six stable uncorrected
source steps. A hard 20-source-tick deadline returns control to the upstream
neutral pose, so this cannot become a permanent neutral-animation filter. The
proof includes that deadline and its next tick: a delayed jump cannot pass by
merely declaring the bridge finished. Independent reanalysis of all 32 final
catches found release by tick 13, with the modifier inactive at both +20/+21.
Recorded transforms reproduce body-space coordinates within 0.00000047 m across
3,899 observations. Movement fixtures use an unobstructed
leftward lane and hold input through this complete window; the earlier forward
lane reached the court boundary, and an earlier input release introduced a
separate neutral-deceleration blend into the tail. Neither is asserted to be
repaired by this rebound change.

During a local/server Held catch, the ball mesh shares the holder's visual
correction offset. The live ball root remains unchanged. Without this small
second repair an owner capture still showed about 0.13 m separation at settled
catch age 6 despite the corrected skeleton. Non-owning remote ball presentation
continues using the coherent selected history frame.

Engine-facing decisions were checked against the pinned Godot 4.7.1 .NET API:

- [SkeletonModifier3D](https://docs.godotengine.org/en/4.7/classes/class_skeletonmodifier3d.html)
  provides post-animation skeleton modification;
  [SkeletonUpdated](https://docs.godotengine.org/en/4.7/classes/class_skeleton3d.html#class-skeleton3d-signal-skeleton-updated)
  observes the final pose after modifiers. Bone global poses are skeleton-relative.
- [AnimationMixer process callback](https://docs.godotengine.org/en/4.7/classes/class_animationmixer.html#class-animationmixer-property-callback-mode-process)
  and [state-machine travel](https://docs.godotengine.org/en/4.7/classes/class_animationnodestatemachineplayback.html#class-animationnodestatemachineplayback-method-travel)
  distinguish tree selection from final evaluated pose. Production processing
  remains intact in the capture fixture.
- [Animation track interpolation](https://docs.godotengine.org/en/4.7/classes/class_animation.html#class-animation-method-rotation-track-interpolate)
  supplies the existing clip samples; the project’s existing pure pose bridge
  preserves the rig’s rest/scale conventions.
- [Node3D global position](https://docs.godotengine.org/en/4.7/classes/class_node3d.html#class-node3d-property-global-position)
  defines the mesh correction coordinate space.
  [Basis inverse and orthonormalization](https://docs.godotengine.org/en/4.7/classes/class_basis.html#class-basis-method-inverse)
  support measuring articulation independently of the sampled model’s rigid transform.
- [RPC transfer modes](https://docs.godotengine.org/en/4.7/tutorials/networking/high_level_multiplayer.html#remote-procedure-calls)
  support the existing authority/unreliable-ordered snapshot. Repeating a receipt
  addresses a missed intermediate state without introducing a new authority path.
- The Python launcher uses argument sequences, bounded waits and explicit child
  cleanup per [subprocess](https://docs.python.org/3.14/library/subprocess.html).

## Results and discriminating controls

All normal fixtures require two catches. Local, observer and owner stationary
captures measure one-tick onset and a maximum settled palm surface error of
0.001560 m. Owner confirmation also covers the missing-Loose case. A longer
Loose interval exercises entry from actual incoming locomotion rather than
only a prior carrying pose. Floor and falling/contested fixtures pass.
Requested pre-recovery Left is recorded separately: production recovery resets
to Right under ADR-0012, so this is **not** evidence of a Left-handed catch.

| Observation | Before/control | Repaired representative measurement |
|---|---:|---:|
| Held palm surface error, maximum | 1.285937 m | 0.001560 m |
| Hand/finger adjacent displacement, stationary | 0.790607 m | below 0.102 m |
| Foot adjacent displacement, stationary | 0.392765 m | below 0.005 m |
| Owning client catches / authority recoveries | 0 / 2 | 2 / 2 |
| Moving local foot/toe displacement | measured independently | 0.155176 m |
| Moving owner foot/toe displacement, both fixture phases | measured independently | 0.166496 m |

The retained JSON reports contain every sample and per-bone maximum, including
the extended exit window; the table is rounded and does not replace those gates.
Moving captures reach 6 m/s and retain a visibly moving dribble hand. Genuine
remote observer captures require coherent pose/ball source ticks on every
checked frame. A neutral-history reset precedes repeated catch proof.

Controls are deliberately separate from uninterrupted catch acceptance:

- `--legacy-pose` disables only the new modifier at runtime. The real old asset
  fails the ordinary palm/limb detector; no fabricated failure flag is used.
- `--mutate-disconnected` shifts the measured palm finger bases by 1 m and
  fails the same contact predicate. `--mutate-delayed` removes the first six
  catch observations and fails the ordinary onset predicate.
- Immediate-shot and pump-fake controls use production input on both local and
  owning clients, require two actually accepted JumpShot startups, require two
  actual feints where requested, check release counts (two shots / zero feints),
  and reject any later catch replay. These cases intentionally interrupt the
  catch and are not fed into the uninterrupted 30-tick contact proof.
- Existing `ReboundGrabTest` scenarios retain made-basket exclusion, no-possession
  and committed-action exclusions, repeated catch and clip-duration controls.

## Evidence, reproduction and limitations

The compact [evidence directory](evidence/0427/) retains original manifests,
gzip-compressed full traces, reports, selected unmodified gameplay PNGs and
SHA-256 checksums. Capture commit labels identify the production revision;
capture/control instrumentation and analyzer hardening continued afterward on
this branch. The inventory records the analyzer hash used for the retained
reports. `native-before` uses the runtime legacy-pose control on the
repaired revision; it is not mislabeled as an old binary. Original pre-fix owner
evidence is retained separately. Native observer capture uses an explicitly
paced 80 ms producer for diagnostic render coverage; it proves neither ordinary
real-time latency nor human visual approval. Headless traces are state evidence.

```powershell
dotnet build 'HOOPER GAME.csproj' --configuration Debug -p:IncludeIntegrationHarness=true
$godotExe = [Environment]::GetEnvironmentVariable('GODOT','User')
python tools/run_rebound_proof.py --godot $godotExe
python tools/run_rebound_capture.py --godot $godotExe --output .godot/427/native-review --long-loose
```

The proof wrapper runs ten normal variants, four accepted-action controls,
the actual legacy-pose control and both observation mutations. It is registered
as `net-rebound-presentation` in the authoritative harness catalog. To reanalyze
retained evidence, decompress each `trace.jsonl.gz` beside its manifest as
`trace.jsonl`, then run `tools/analyze_rebound_capture.py` on a normal case.
Legacy and interruption cases are expected to fail uninterrupted-catch gates.

Validation: both ordinary and harness game builds pass with zero warnings/errors;
the full unit suite passes 1,273 tests with five intentional characterization
skips, including on the final v2 capture build. The first
exhaustive run passed all 351 single-process cases; its 52 network adapters
could not start because PATH selected the Windows WSL bash shim. The network-only
rerun explicitly used Git Bash and passed all 53 cases, including the new rebound
proof. These broad results precede the target-correction follow-up; the final
v2 handoff proof is distinguished in the retained verification summary. The repository suite passes 101
tests with one platform-specific skip. Initial hosted CI also caught two stale
catalog expectations (scenario order and fallback-weight total); those were
corrected and its focused 26-test suite passes. CI on the final PR head remains
the merge gate; these local results do not replace it.

No editor steps are required to run the repair. Final appearance, the short
pose-bridge duration and readability remain for the human in #173. This record
does not accept feel, repair instantaneous pickup relocation or claim general
ownership-handoff continuity outside these measured fixtures.
