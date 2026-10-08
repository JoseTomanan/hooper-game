# Rig verification source-to-proof matrix

- Issue: [#367](https://github.com/JoseTomanan/hooper-game/issues/367)
- Verification parent: [#178](https://github.com/JoseTomanan/hooper-game/issues/178)
- Build contract: [#170](https://github.com/JoseTomanan/hooper-game/issues/170)
- Related decisions: ADR-0011, ADR-0012, ADR-0015, ADR-0016, ADR-0021
- Date: 2026-10-08
- Baseline: `fa5dd48`

This maps the parent checklist and its later UID comments to numerical evidence
or an explicit remaining gap. It does not accept proportions, foot sliding, or
committed-move readability. Those judgments remain open on #178/#173.

## Method and shipped contract

Use Godot `4.7.1.stable.mono.official.a13da4feb`, the pinned Godot .NET SDK
4.7.1, .NET SDK 8.0.421, and the Debug assembly with integration sources enabled.
The harness loads the shipped `Player.tscn`; the remote fixture loads
`Main.tscn` and its production player spawner, rigs, and AnimationTrees.
Comparisons use an absolute tolerance below 0.0001 per component (dimensionless
for scale, metres for capsule dimensions), allowing imported float noise.
Results depend on this asset's named 65-bone population, the default factors,
the fixed capsule dimensions, and the current steal phase/tree mapping. A
change to any of those contracts requires revisiting the corresponding proof.

`PlayerRigScaler.Height` and `Wingspan` default to 1.0. The setters accept
positive factors without an exported or documented anthropometric range;
nonpositive values and NaN are coerced to 1.0. Positive infinity is currently
accepted by the setter; this evidence does not certify that edge case. The
finite samples 0.8, 1.2, and 2.0
exercise the capability; they are not a recommendation or a guarantee for every
possible factor. The tests read local bone pose scales immediately after setters.
They do not prove persistence when animation subsequently writes SCALE tracks.

The independent write sets are exact: height writes the spine/neck/head and leg
chains, wingspan writes the arm chains. The real imported Y Bot has 65 bones:
13 height, 8 wingspan, and 44 unrelated bones. The test oracle names the two
chains independently of `RigScale.Classify`, then checks every bone against its
own authored rest scale. The authored scales are approximately one on this
asset; these tests do not establish arbitrary non-unit-rest rig support.

The remote fixture captures the named bone rest scales and visual ancestor
scales from a fresh scene instance before adding it to the tree. It compares
both peers' runtime rest and pose scales against that independent baseline;
equal rest/pose drift on both peers cannot establish its own expected value.
Only scale is compared: `BlendRestAnchor` intentionally changes two upper-leg
rest rotations. A reliable readiness acknowledgement prevents the one-shot
steal from running before the client's rig and tree are loaded. Final client
and server acknowledgements, followed by both process exit codes, preserve
server assertions in the combined verdict.

Write-set independence is distinct from world-space proportion independence.
`RigScale.cs` documents spine-to-arm inherited scaling because the arms descend
from Spine2. Refining that behavior would change the production contract and is
outside this evidence issue.

The collider contract comes from #170: keep the gameplay capsule unchanged
unless the human chooses to tie rig scaling to collision. `Player.tscn` uses
the capsule defaults recorded in ADR-0025: radius 0.5 m, total height 2 m.
`capsule-contract` checks those dimensions, attachment, enabled state, and unit
local/world scales throughout its ancestor chain before and after both setters.
This proves the fixed-capsule contract and the round-collider scale rule. It
does not invent or prove a capsule-to-deformed-mesh fitting rule.

## Parent checklist

Scenario names below are catalog IDs from `tools/harness_catalog.py`, which CI
executes exhaustively. Existing animation proofs are reused rather than copied.

| #178 criterion | Source and numerical proof | Remaining boundary |
|---|---|---|
| 1. Semi-realistic proportions and believable head/body ratio at gameplay distance | The production visual is the imported `assets/Y Bot.fbx`. New `net-rig-presentation-left` / `net-rig-presentation-right` check authored default scale parity on the actual host and remote rig. | Semi-realistic appearance, head/body ratio, and chosen player-build range still need visual evidence and human judgment. Default parity does not approve proportions. |
| 2. Idle/run retarget and locomotion blend without T-pose or foot-sliding regressions | Existing `locomotion-clip-test` checks nonempty clip populations, track names against real bones, looping, off-rest arm keys, material rotation-track coverage, and live blend-corridor sweeps. Its run/dribble stride controls establish that the pose sampler sees both legs move. | These are structural and pose guards. There is no measured old-Kenney versus Y-Bot world-space foot-sliding comparison or visual acceptance. |
| 3. Startup/Active/Recovery still map visibly on the new mesh | Existing per-move scenarios drive real player AnimationTrees; for example `jumpshot-anim-test-jumpshot-phases`, `crossover-anim-test-crossover-left-origin` / `crossover-anim-test-crossover-right-origin`, `behind-the-back-anim-test-btb-left-origin` / `behind-the-back-anim-test-btb-right-origin`, and `steal-anim-test-steal-left-reach` / `steal-anim-test-steal-right-reach`. `move-kind-anim-test-clipped-reaches-permove` and `move-kind-anim-test-unclipped-stays-generic` cover the intentional resolver fallback. New remote scenarios observe same-tick broadcast phase/move ID and actual tree node in order for both steal reach sides, followed by Locomotion. | State/clip entry does not prove fair telegraphs, readable arcs, or animation taste. Other moves retain their existing local proofs; the new remote test does not claim every move was replayed over the network. |
| 4a. Independent height/wingspan and unrelated chains | Strengthened `rig-scale-harness-test-independent-scaling` checks all 65 real bones at default, height-only and wingspan-only sample factors, repeated setters, sequential mixed setters in both orders, combined build, and identity restoration. Existing xUnit `RigScaleTests` retains the independent classifier-name cases. | This is immediate local write-set independence. Spine-to-arm world-space inheritance and animation overwrite remain explicit limitations. |
| 4b. Collider still matches / no unsupported round-collider scale | New `rig-scale-harness-test-capsule-contract` checks the documented fixed capsule across height, wingspan, and combined setters, including ancestor scale. | The fixed capsule intentionally does not follow changed cosmetic proportions. A numerical mesh-fitting rule or gameplay collision customization has not been decided. |
| 5. Dual-instance remote proportions/animation state | New `net-rig-presentation-left` / `net-rig-presentation-right` use production `HostGame` / `JoinGame` and the real spawned Player scene. Both default factors, named rest/pose scales, and authored visual ancestor scales must agree. Client phase/tree observations require a true remote role with its local move machine Inactive. Existing `net-defensive-telegraph-telegraph`, `net-defensive-telegraph-control`, and `net-behindtheback-sweep` remain transport/display proofs with minimal player fixtures. | The new proof is authored default parity. `PlayerRigScaler` has no production runtime factor replication: server-only `SetBuild` is not networked customization. These tests add no such feature. |
| Later comment: Y Bot / RigScaler UID backfill (PR #268) | `assets/Y Bot.fbx.import` is tracked, but the two corresponding `Player.tscn` ext_resources still resolve by path without `uid=`; `PlayerRigScaler.cs.uid` is ignored/untracked. Scene and rig loading prove today's path resolution only. | UID portability/backfill remains open on #178. No scene re-save or sidecar policy change is included here. |
| Later comment: idle/run BoneMap UID backfill (PR #270) | Both `assets/retarget/idle_bonemap.tres` and `run_bonemap.tres` reference `MixamoProfile.tres` by path without `uid=`. Existing retarget/clip binding proofs establish current import results. | Import-time UID portability remains open on #178; runtime track binding is not proof of portability after resource moves. |

## Source verification

Context7 is unavailable in this session. Version-specific official web
documentation and the pinned C# compile/runtime verify the engine API decisions:

- [Bone rest transforms](https://docs.godotengine.org/en/4.7/classes/class_skeleton3d.html#class-skeleton3d-method-get-bone-rest) and [local pose scale](https://docs.godotengine.org/en/4.7/classes/class_skeleton3d.html#class-skeleton3d-method-get-bone-pose-scale).
- [Packed scene instantiation](https://docs.godotengine.org/en/4.7/classes/class_packedscene.html#class-packedscene-method-instantiate) creates the fresh authored hierarchy; [Node lifecycle](https://docs.godotengine.org/en/4.7/classes/class_node.html#class-node-private-method-ready) places `_Ready` after entry into the tree. [Freeing an unparented instance](https://docs.godotengine.org/en/4.7/classes/class_object.html#class-object-method-free) also frees its children.
- [Basis scale extraction](https://docs.godotengine.org/en/4.7/classes/class_basis.html#class-basis-method-get-scale) and [world transforms](https://docs.godotengine.org/en/4.7/classes/class_node3d.html#class-node3d-property-global-transform), including inherited node scaling.
- [Capsule dimensions](https://docs.godotengine.org/en/4.7/classes/class_capsuleshape3d.html#class-capsuleshape3d-property-height) and [CollisionShape3D scaling/disabled state](https://docs.godotengine.org/en/4.7/classes/class_collisionshape3d.html).
- [Actual state-machine current node](https://docs.godotengine.org/en/4.7/classes/class_animationnodestatemachineplayback.html#class-animationnodestatemachineplayback-method-get-current-node), rather than the last requested state.
- [Multiplayer signals and RPCs](https://docs.godotengine.org/en/4.7/tutorials/networking/high_level_multiplayer.html#remote-procedure-calls), [sender identity](https://docs.godotengine.org/en/4.7/classes/class_multiplayerapi.html#class-multiplayerapi-method-get-remote-sender-id), and [Variant-compatible C# arrays](https://docs.godotengine.org/en/4.7/tutorials/scripting/c_sharp/c_sharp_variant.html#variant-compatible-types).

## Validation and deliberate breaks

The ordinary game build (`IncludeIntegrationHarness=false`) and the harness
build (`true`) passed with zero warnings/errors. xUnit passed 1,155 tests with
the five intentional scatter-characterization skips; all 61 repository
contract tests passed. The focused structural and remote runs passed 4/4,
and the selected pre-existing rig/locomotion/phase proofs passed 15/15.

The shared catalog currently contains 279 scenarios; the command below runs
the exhaustive local equivalent of CI's four integration shards.

All 18 temporary deliberate breaks below compiled/loaded successfully and
produced catalog exit 1. These are assertion or bounded scenario-timeout
failures, not compiler errors, crashes, or launcher setup failures. Every edit
was restored byte-for-byte; SHA-256 hashes of the five touched files matched
their originals, and the final restored harness build passed. The scratch
mutation runner and native logs are local `.godot/` artifacts, not committed
production changes.

`independent` below means `rig-scale-harness-test-independent-scaling`;
`capsule` means `rig-scale-harness-test-capsule-contract`; `remote-left/right`
mean `net-rig-presentation-left/right`.

| Named deliberate break | Scenario | Observed discrimination |
|---|---|---|
| `scale-height-missing-toe` | independent | Skip RightToeBase's write while retaining classifier counts; height 0.8 fails on that bone. |
| `scale-wing-missing-hand` | independent | Skip RightHand's write while retaining counts; wingspan 0.8 fails on that bone. |
| `scale-wing-height-contamination` | independent | RightHand uses height; height-only 0.8 unexpectedly changes its pose scale. |
| `scale-unrelated-finger` | independent | RightHandPinky4 uses wingspan; its unrelated-chain identity check fails. |
| `scale-compounding` | independent | Multiply current pose instead of rest baseline; the second 0.8 call yields 0.64. |
| `height-setter-clobbers-wingspan` | independent | SetHeight resets wingspan; restoring height loses the retained 1.2 shoulder scale. |
| `wingspan-setter-clobbers-height` | independent | SetWingspan resets height; mixed setters lose the retained 0.8 spine scale. |
| `authored-rest-and-pose-drift` | remote-left | CaptureBaseline changes Spine rest to 1.1 before writing pose; live rest and pose agree but both fail the independent authored 1.0 baseline. |
| `capsule-radius` | capsule | Serialized radius 0.51 m fails the fixed 0.5 m contract. |
| `capsule-height` | capsule | Serialized height 2.1 m fails the fixed 2 m contract. |
| `capsule-disabled` | capsule | Disabled collision shape fails the enabled/body attachment check. |
| `capsule-local-nonuniform` | capsule | Local scale (1, 0.9, 1) fails the unit-scale guard. |
| `capsule-ancestor-nonuniform` | capsule | Player scale (1.1, 1, 1.1) fails the inherited world-scale guard. |
| `remote-animation-disabled` | remote-left | Skip ApplyAnimation only on remote copies; the ordered tree-node proof times out at stage 0. |
| `remote-local-machine-display` | remote-left | Make DisplayPhaseResolver always use the local machine; the ordered display proof times out at stage 0. |
| `wrong-remote-reach-side` | remote-right | Flip only the broadcast reach-sign branch; the expected right-hand arc never completes and stage 0 times out. |
| `default-model-scale-drift` | remote-left | Serialized CharacterModel scale 1.1 on both peers fails the authored unit-scale contract. |
| `late-server-failure` | remote-left | Server exits 1 after its final acknowledgement; client exits 0, but the adapter correctly returns failure. |

The timeout diagnostics above retain the final Inactive/Locomotion state, not
the transient wrong node. The named source edits establish what was broken;
no stronger transient-pose trace is claimed. As a green control, the same
remote-animation-disabled mutation left existing
`net-defensive-telegraph-telegraph` and its auto-selected paired control green
(2/2), demonstrating that their minimal transport fixture cannot replace the
new production AnimationTree proof.

The imported rest scales are approximately one. Replacing the captured
baseline with `Vector3.One` would be observationally equivalent at this
tolerance on the shipped asset; no sensitivity to that substitution or proof
of arbitrary non-unit authored rig support is claimed.

Reproduce the unmodified proofs from the project root:

```powershell
$issue367Godot = [Environment]::GetEnvironmentVariable('GODOT', 'User')
dotnet build 'HOOPER GAME.csproj' --configuration Debug -p:IncludeIntegrationHarness=true
python tools/harness_catalog.py run --scene RigScaleHarnessTest.tscn --scene NetRigPresentationTest.tscn --godot $issue367Godot --bash 'C:/Program Files/Git/bin/bash.exe'
python tools/harness_catalog.py run --all --run-id issue367-full --godot $issue367Godot --bash 'C:/Program Files/Git/bin/bash.exe'
```

For a deliberate break, apply only the named edit above, rebuild after a C#
change, select its exact catalog ID with `--id`, and expect exit 1 with the
corresponding diagnostic. Restore the edit and rebuild before any green run.
Local evidence: `.godot/issue367-mutations/results.json` (exact substitutions,
diagnostics, logs, hashes), `.godot/issue367-unit-tests.log`,
`.godot/issue367-repository-tests.log`, and `.godot/issue367-full-output.log`.
CI executes the strengthened existing case and all three added cases through
the shared catalog; the frozen migration fixture remains unchanged.

## Remaining contract gaps and reproducers

These remain separate from the numerical verdict for #367:

- **World-space proportion independence:** run
  `rig-scale-harness-test-independent-scaling` and inspect its height-only 2.0
  observation: every arm-chain local pose scale remains at its rest value.
  `RigScale.Classify` and the imported skeleton place the arms below Spine2,
  so height scaling still propagates to their world transforms. That existing
  contract does not satisfy a stronger interpretation of #178's visual
  independence criterion. Changing the hierarchy/scaling policy needs its
  own production decision and implementation.
- **Collider-to-mesh fitting:** run `rig-scale-harness-test-capsule-contract`.
  At height 2.0 and wingspan 2.0, its measured capsule stays radius 0.5 m and
  height 2 m, as #170 requires. The parent wording “collider still matches the
  mesh” cannot establish a fitting tolerance or a collision-customization
  policy. Such a policy remains undecided; this test does not infer one.
- **Runtime scale replication:** `PlayerRigScaler.SetBuild` changes only the
  local node; production state broadcasts do not carry height/wingspan
  factors. Calling it only on the host therefore does not establish equal
  custom proportions on the client. The new network scenarios exercise the
  shipped default 1.0/1.0 on both peers, without adding an observation RPC
  that changes either peer's rig. Networked customization requires separate
  scope.

## Human verification (no feel acceptance)

No editor setup is needed to run these headless proofs. #178 remains open for
proportion/foot-sliding/readability review in the consolidated #173 pass, visual
evidence where available, and the outstanding UID backfill described above.
