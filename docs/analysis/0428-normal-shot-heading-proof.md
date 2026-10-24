# #428 — Normal-shot Startup heading proof

Measured 2026-10-24/25 with Godot .NET 4.7.1, .NET SDK 8.0.421 and
Python 3.14.0. The contract is the maintainer-approved
[2026-10-23 Agent Brief](https://github.com/JoseTomanan/hooper-game/issues/428#issuecomment-6354780795).
This records objective behavior and evidence, not acceptance of feel or visuals.
Those remain #185/#173; tuning remains #238.

## Implemented behavior

Normal `JumpShot` snapshots its horizontal rim-facing target only after accepted
Begin. Authority derives its own target; a client's requested angle is not
accepted as aim. Missing/degenerate context retains finite current heading.
Startup uses the existing ordinary non-linear 900 degrees/s, 0.95 profile,
with positive yaw for an exact half-turn. Input, contact displacement and
possession loss cannot retarget the instance. Active/Recovery do not turn.

The instant plant, 18/4/20 phases, first-Active release, [3,12) feint window and
eight-tick fake Recovery remain. Actual release heading supplies the unchanged
facing penalty and fadeaway classifier. A back-facing Begin that squares up
releases ordinary. ADR-0010's amendment and ADR-0026's clarification travel with
the implementing behavior in `e095924`; post turnaround remains distinct.

Each predicted shot carries a Begin token and fixed target. Historical samples
record phase/target/token. Authority confirmations repair only that instance,
before motion replay; a reliable request result rejects only its matching
instance. An old Inactive snapshot cannot cancel a newer unconfirmed shot.
Matching authoritative release snapshots also repair the owner's fadeaway flag.
Replay advances neither the live machine nor ball payloads. The existing
latest-opponent approximation and other committed positional replay limitations
remain explicit.

## Before/after production trace

The baseline is a detached checkout at `2a74b6c`, before the implementation.
The same capture instrument was copied there; it does not substitute a fake
heading implementation. Its traces expose frozen Startup heading. The after
checkout contains `e095924`'s behavior. The animation library hash is identical
before/after; no shot assets, production scene properties, frame data or accuracy
formula were changed.

The retained [verification report](0428-shot-heading-verification.json) covers
all 28 combinations: initial divergence 0, 45, 89, 90, 91, 135 and 180 degrees;
stationary/moving entry; Left/Right dribbling hands. Every case has a real
separate ENet observer, authority release events and final skeleton poses.
These diagnostic captures use one fixed simulation step per process frame;
they are not network latency or performance measurements.

| Initial divergence | Before release facing factor | After release facing factor |
|---|---:|---:|
| 0 degrees | 1.000000 | 1.000000 |
| 45 degrees | 1.200000 | 1.000000 |
| 89 degrees | 1.395556 | 1.000000 |
| 90 degrees | 1.400000 | 1.000000 |
| 91 degrees | 1.404444 | 1.000000 |
| 135 degrees | 1.600000 | 1.000000 |
| 180 degrees | 1.800000 | 1.000000 |

All default-rate after cases reach their fixed target, release once at first
Active and use ordinary presentation. The capture's first observed accepted
Begin is Startup/1; release is 17 ticks later. The local harness begins at
Startup/0 on tick 5 and releases on tick 23, showing the same 18-tick contract.
There are 17 Startup motion calls after phase advancement, not an extra turn
at Begin or Active. Moving captures enter with measured speed 4.5000005 m/s.
The separate 3 m/s local fixture directly checks the actual movement factor
remains 1.4 after velocity plants to zero.

One initial before observer capture (`91-moving-Left`) missed the entire
four-tick Active phase during a presentation reset. Its original trace remains
under `diagnostics/original-before-reset`; the report uses an explicitly named
successful repeat. No missing sample was invented. Unpaced headless captures
contain complete ordered observer counters in 24/28 before and 26/28 after
cases; the others report their observed gaps/resets. They do not establish
universal remote counter coverage.

## Lifecycle and counterfactual controls

`JumpShotHeadingTest` loads the production player scene and exercises the real
accepted-Begin, motion, contact and successful-release paths. Its 43-case local
sweep checks independently calculated step bounds/rate, monotonicity, plant,
phase timing, release heading and penalties. The executable catalog adds the
28 matrix cases and 16 named/control cases; it also registers both network
delay cases. The catalog's frozen migration fixture is unchanged.

- A 90-degrees/s fixture genuinely remains unfinished: release divergence
  2.7173743 radians (155.694 degrees), facing factor 1.6919737, fadeaway true.
  Production turning is not slowed to preserve this route.
- Explicit zero-rate boundary fixtures release at 1.5690513, 1.5707965 and
  1.5725417 radians: fadeaway false/true/true. Their actual angle premises are
  asserted. The already-squared-up control remains ordinary.
- Earliest/latest legal fakes at Startup/3 and /11 retain their partial heading,
  release zero balls, cost eight Recovery ticks and retain dead-dribble cost.
  Illegal /2 and /12 attempts still release the valid shot once.
- Ownership loss/OOB retain the committed timeline and remaining turn, with no
  later invalid release. A valid-shot control proves the setup can release.
- Missing/degenerate targets, both shortest-arc directions, the exact half-turn,
  later stick input and displacement are covered. Real capsule depenetration
  moves the shooter -0.8 m while retaining target yaw pi. Actual rim yaw becomes
  2.9829373; release heading stays pi, leaving residual 0.1586554 radians.
- Existing fadeaway animation proofs now explicitly use the lower-rate fixture
  and assert their release-angle premises. Original pose thresholds remain;
  `fadeaway-leans-back` measures 40.86 degrees and 0.3844 m hips-back.

Isolated source mutations retain their actual failure logs in
[`evidence/0428/mutations`](evidence/0428/mutations/summary.json): disabling only
the shot turn fails `Startup must advance toward target`; replacing it with an
instant snap fails `Startup step exceeds ordinary bound`. Restored source is
byte-identical and passes. These are behavioral failures, not changed expected
booleans.

## Network and coherent presentation

The dedicated authority, predicting owner and separate observer use real ENet
connections through measured 0/30-ms UDP relays. The owner's predicted Begin
position (1.5,0,6) yields target -2.896614, while authority's (0,0,6) yields pi.
Confirmation must repair that genuine disagreement.

The final [network report](evidence/0428/network/final-v3/summary.json) passes:

| Injection per direction | Real corrections | Startup replay steps | Maximum replay error | Largest heading correction |
|---|---:|---:|---:|---:|
| 0 ms | 571 | 26 | 7.45e-9 radians | 0.04984 radians |
| 30 ms | 443 | 121 | 1.49e-8 radians | 0.24988 radians |

Authority observes normal=1, fake=0, fake=0 and rejected-request=0 successful
releases. The second network fake requests at client Startup/7 to leave a
delivery margin; exact earliest/latest legal boundaries are the local /3 and
/11 proofs above. Rejection must arrive before the phantom shot's first Active,
so natural move completion cannot satisfy it.

Correction is compared with an independently calculated fixed-step heading
reference over retained samples. It proves replay arithmetic, target repair,
and no extra machine tick or payload. Because its acknowledgement boundary
and phase count come from the same recorded history as production, it does not
independently establish input-sequence/authority-wall-tick equivalence under
coalesced or skipped input packets.

Separately labelled synthetic ordering cases cover confirming older A while B
is unconfirmed, a late rejection of A preserving B, and release-flag correction
with older/unrelated flags excluded. `drop-target` and `wrong-instance` controls
reach that intended detector after the genuine network stages pass. Both fail
`synthetic older-A confirmation corrupted historical replay or live unconfirmed B`.
The first exploratory wrong-instance run failed before its mutation because a
late fake missed authority's legal window; it is not counted as mutation proof.

Each final observer sees 32 Startup, four Active and 34 Recovery samples, with
three holds and zero resets. A deliberate 100-ms delivery pause exercises the
hold: actual yaw and AnimationTree playback time remain frozen. Heading,
animation phase/counter and ball use one ADR-0027 presentation tick. These
aggregate network counts do not independently prove every per-instance counter.

## Rendered evidence and remaining ball separation

The native D3D12 Mobile captures use the production gameplay-distance camera,
960x540 viewport and a real separate observer. Each of the four films contains
the complete ordered Startup/1..17, Active/0..3 and Recovery/0..19 counters,
including holds. Final modified skeleton poses and images are observed through
`SkeletonUpdated` and `FramePostDraw`; their selected heading/pose/ball tick is
checked. Producer pacing permits coverage and is not a real-time latency claim.

[Stationary Right before/after](evidence/0428/rendered/stationary-right-comparison.png)
and [moving Left before/after](evidence/0428/rendered/moving-left-comparison.png)
retain whole viewports at half resolution. Twenty-four untouched source PNGs,
their hashes, camera, source ticks and playback times are retained separately.

The fixed-offset ball does **not** follow the animated wrist. Nearest-wrist
distance across observed Startup and first Active is 0.11752–1.24091 m for the
stationary pair and 0.10083–1.31139 m for the moving pair, effectively identical
before/after. These measurements disclose separation; they do not accept it.

The existing held-ball offset already follows Heading. Consequently a stationary
Right 180-degree case releases from (-0.4429,1,6.49999) before and
(0.4429,1,5.49999) after: a 1.33590-m change through that unchanged formula.
Moving Left exhibits the same displacement magnitude. Ball attachment,
release-origin and arc code were not rewritten. Trajectory differences therefore
include both the changed input to the existing facing penalty and the existing
heading-relative held offset following the new heading. Turning does not repair
ball/hand coupling or establish better visual quality.

## Reproduction and retained provenance

The [evidence package](evidence/0428/package.json) is approximately 40.2 MiB.
It retains every observed shot row, including repeated holds and all 52 bones,
plus neighboring neutral rows. Setup/idle rows are omitted explicitly in
`retention.json`, with original row counts, selected indices and hashes. Original
raw captures remain under `.godot/428`; selected evidence is not mislabelled as a
byte-identical full raw trace. Local harness JSON/log gzip copies are lossless.
`SHA256SUMS` covers the retained package.

```powershell
dotnet build "HOOPER GAME.csproj" --configuration Debug
dotnet test "tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj" --configuration Debug
python tools/harness_catalog.py run --scene JumpShotHeadingTest.tscn --godot $env:GODOT
python tools/run_net_shot_heading.py --godot $env:GODOT --all --output .godot/shot-net/repeat
python tools/package_shot_heading_evidence.py --verify-only
```

`run_shot_heading_capture.py --project <checkout> --commit <sha> --output <fresh
directory under .godot/428>` recreates the observer matrix. `--render --paced`
creates actual PNG evidence. `analyze_shot_heading_capture.py` also reads the
retained `.jsonl.gz` files, so its complete 28-case/four-render verification can
be reproduced without the original workspace. Removing a rendered counter or
corrupting its ball presentation tick fails the analyzer's separate controls.

Game build, unit totals, exhaustive catalog and independent review are reported
with the implementation PR. No editor operation is required to review the
artifacts. #428's objective proof does not close #185/#173 or lift the M11 hold.

## Official API sources

- [Godot 4.7 RPC authority, reliability and matching signatures](https://docs.godotengine.org/en/4.7/tutorials/networking/high_level_multiplayer.html#remote-procedure-calls).
- [Fixed physics callbacks and ordering](https://docs.godotengine.org/en/4.7/classes/class_node.html#class-node-method-physics-process).
- [Final modified skeleton signal](https://docs.godotengine.org/en/4.7/classes/class_skeleton3d.html#class-skeleton3d-signal-skeleton-updated).
- [Post-draw capture signal](https://docs.godotengine.org/en/4.7/classes/class_renderingserver.html#class-renderingserver-signal-frame-post-draw).
- [Python 3.14 subprocess lifecycle](https://docs.python.org/3.14/library/subprocess.html#subprocess.Popen)
  and [lossless gzip](https://docs.python.org/3.14/library/gzip.html).
- [Pillow 12 whole-image thumbnail](https://pillow.readthedocs.io/en/12.0.0/reference/Image.html#PIL.Image.Image.thumbnail).
