using System.Collections.Generic;
using System.Linq;
using Godot;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #267 — proves the retargeted
// locomotion clips (idle/run/pivot in assets/locomotion.res) actually
// resolve against Y Bot's REAL Skeleton3D as it sits in the live
// scenes/Player.tscn (CharacterModel is an instance of Y Bot.fbx, whose own
// hierarchy is "Y Bot -> Skeleton3D" with NO "Root" wrapper — confirmed by
// dumping Y Bot.fbx's node tree headlessly during #267's Stage 1 spike), not
// just in an isolated FindBone check against a loaded-but-unattached
// skeleton. Before #267, EVERY bone track in every clip failed to resolve
// (the clips were authored against Kenney deform-bone names — Hips, Chest,
// LeftUpLeg, ... — while Y Bot's bones are all "mixamorig_"-prefixed), which
// is the literal T-pose bug. This harness is the RigScaleHarnessTest-style
// "don't guess, print the real names and assert against them" proof for that
// fix, generalized across all three clips.
//
// The header used to say driving AnimationTree.Advance() and sampling
// rendered bone poses headlessly needed a custom MainLoop frame pump and was
// "out of scope here" (spike #87). That claim is now STALE as of #287: a
// disposable diagnostic probe proved `AnimationTree.Advance(dt)` pumps and
// samples real `Skeleton3D` bone poses perfectly headlessly from an ordinary
// Node's _Ready/_PhysicsProcess — no custom MainLoop needed — and family 5
// below now does exactly that, in this same harness. Eight bounded
// clip-property/pose assertion families sit on top of track resolution:
//   1. loop_mode (#271 — the import default LOOP_NONE shipped once, freezing
//      run after a single pass);
//   2. a T-pose-anchor guard for idle/run (#271 — each arm chain's first
//      rotation key must sit well off the skeleton's rest, because the rest
//      fixer without fix_silhouette anchors clips at the target rest);
//   3. a rest-delta guard for pivot (#273 — every rotation key on pivot's 4
//      AUTHORED PLANT tracks (Hips/Spine/LeftUpLeg/RightUpLeg, matched by
//      name) must sit NEAR Y Bot's rest instead of far from it — the
//      OPPOSITE polarity of (2) — because pivot's hand-authored keys were
//      Kenney-rest-relative and Godot's absolute ROTATION_3D tracks handed
//      Y Bot's bones the raw Kenney rest orientations verbatim. Scoped to the
//      plant bones because family 6 added off-rest upper-body hold tracks);
//   4. an idle<->run blend-compatibility guard (#275 — cross-clip signed-dot
//      >= 0 on shared rotation tracks, an anatomical <= 90 deg bound on the
//      UpLeg bones' cross-clip angle, and intra-track consecutive-key
//      hemisphere continuity — because the BlendSpace1D interpolates the two
//      clips together at intermediate speeds, where a hemisphere flip or a
//      retarget twist transits garbage poses invisible at either endpoint);
//   5. a continuous-drive corridor sweep (#275's own predecessor #287 — data-
//      level key compatibility (family 4) turned out NOT sufficient: the
//      live AnimationNodeBlendSpace1D mixer still produces out-of-corridor
//      leg poses at INTERMEDIATE blend weights during a real 0->6 ramp, a
//      mixer-accumulation degeneracy, not a data defect. Drives the actual
//      live Player.tscn AnimationTree with real Advance() calls across a
//      90-frame/1.5s ramp and asserts every leg-chain bone pose stays within
//      (reference-gap + 10 deg) of at least one of two phase-matched
//      reference rigs pinned at blend 0 and blend 6. #285 runs this a SECOND
//      time against the Dribble BlendSpace1D — a second partial-weight blend
//      surface is exposed to the same degeneracy — on its own never-advanced
//      rig trio, travelled into the Dribble state and proven to have arrived
//      before sweeping. #294 then splits that single Dribble BlendSpace1D
//      into DribbleLeft/DribbleRight, so this now runs a THIRD and FOURTH
//      time, once per hand-side polarity, each against its own dedicated
//      never-advanced rig trio. Every pass also requires its two endpoint
//      rigs to genuinely differ, since the corridor threshold is
//      self-referential).
//   6. a pivot upper-body completeness guard (turning-T-pose bug — the Pivot
//      state is a single clip at full weight, so every bone pivot did NOT
//      track was reset to Y Bot's REST = a Mixamo T-pose, snapping the arms
//      horizontal the instant a turn began. pivot now carries idle's frame-0
//      hold pose for the arm chain / upper body; assert those tracks exist and
//      sit clearly OFF rest, plus a minimum total track count so it can't
//      silently revert to the 4-track clip);
//   7. the jump-shot clip family (#279 — four one-shots sliced from `Goalkeeper
//      Catch Stationary`: segment lengths must equal JumpShot.DefaultFrameData's
//      own tick windows read from the C# side (which is what makes the rebuild
//      tool's duplicated 18/4/20 safe rather than merely regrettable), full-body
//      track coverage and an off-rest upper body against the a45bd1d trap, and
//      fadeawayactive must differ measurably from jumpshotactive or the separate
//      #243 state would be decorative);
//   8. the crossover clip family (#280 — the same three checks over SIX clips,
//      three phases x two hand-side polarities, against
//      Crossover.DefaultFrameData; PLUS two the jump shot had no need for,
//      because a crossover is DIRECTIONAL and ball-hand-side is authoritative
//      (ADR-0012), so a wrong polarity is a FALSE TELEGRAPH rather than a
//      blemish: each phase's two variants must differ by >= 15 deg (the asset
//      could be corrupted long after the tool proved the direction at build
//      time), and each of the six .tscn states must point at its OWN clip —
//      read off the AnimationNodeStateMachine resource directly, because a
//      copy-pasted SubResource id leaves every state NAME correct and is
//      therefore invisible to CrossoverAnimTest. Note families 7 and 8 grade
//      different bone sets off rest: see (c)'s comment for why the crossover
//      excludes the clavicles that the jump shot legitimately elevates).
// Whether the corrected pose actually looks RIGHT remains the deferred human
// feel judgment (#178/#173, ADR-0021) — but as of #273, pivot's pose is now
// numerically anchored to Y Bot's own rests via the rest-delta correction,
// the same footing as idle/run's rest-fixer pass; only visual pose quality
// is still unverified, not track-level correctness.
//
//   godot --headless --path . res://tests/integration/LocomotionClipTest.tscn
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
public partial class LocomotionClipTest : Node
{
    private const double TimeoutSeconds = 10.0;

    private PlayerController _player;
    private int _frame;
    private double _elapsed;
    private bool _finished;

    // #287 corridor-sweep rigs: three independent live instances of the SAME
    // scenes/Player.tscn (not a hand-built stand-in tree) so the sweep drives
    // the exact production AnimationTree/BlendSpace1D/Skeleton3D wiring. Test
    // ramps blend 0->6; ref0/ref6 are phase-matched controls pinned at the two
    // endpoints, advanced with the identical dt sequence so "same frame index"
    // means "same elapsed animation time" across all three.
    private PlayerController _sweepTest;
    private PlayerController _sweepRef0;
    private PlayerController _sweepRef6;

    // #285 adds a SECOND BlendSpace1D (the Dribble neutral stance,
    // dribbleidle@0 <-> dribblemove@6), so the #287 corridor sweep now runs
    // twice. Separate rigs rather than reuse: the Locomotion sweep leaves its
    // three rigs pinned mid-ramp at blend 6 with an already-elapsed animation
    // clock, and the "first Advance() only primes" gotcha InstantiateSweepRig
    // documents can only be reproduced honestly on a tree that has never been
    // advanced.
    //
    // (#294) That single Dribble BlendSpace1D is now TWO — DribbleLeft and
    // DribbleRight, one per hand-side polarity — which doubles the number of
    // partial-weight blend surfaces sharing the single BlendRestAnchor-
    // mutated rest #287's degeneracy lives in. A green Right sweep and an
    // unmeasured Left is exactly the gap #294 exists to close, so this trio
    // becomes two trios, one per polarity, each still its own never-advanced
    // rig set for the same "first Advance() only primes" reason as above.
    private PlayerController _dribbleSweepTestRight;
    private PlayerController _dribbleSweepRef0Right;
    private PlayerController _dribbleSweepRef6Right;
    private PlayerController _dribbleSweepTestLeft;
    private PlayerController _dribbleSweepRef0Left;
    private PlayerController _dribbleSweepRef6Left;

    // #298 adds a THIRD family of rigs — a foot-stride measurement, not a
    // corridor sweep. `dribble-corridor` above asserts poses stay NEAR two
    // near-identical endpoints, which frozen legs satisfy trivially (both
    // endpoints are ~static, so "close to both" is cheap); this trio exists
    // to measure actual fore/aft foot travel instead. Separate, never-
    // advanced rigs for the same reason #285's trio is separate from #287's:
    // an already-advanced tree cannot honestly reproduce the "first
    // Advance() only primes" gotcha InstantiateSweepRig documents, and these
    // three must each start from a genuinely fresh AnimationTree.
    //
    // (#294) Doubled the same way as the corridor-sweep trio above, one
    // move/idle pair per hand-side polarity. `_strideRun` stays singular —
    // Locomotion has no hand-side split, so the one run control rig covers
    // both polarity passes below.
    private PlayerController _strideDribbleMoveRight;
    private PlayerController _strideDribbleIdleRight;
    private PlayerController _strideDribbleMoveLeft;
    private PlayerController _strideDribbleIdleLeft;
    private PlayerController _strideRun;

    // #287 (BlendRestAnchor): scenes/Player.tscn now mutates TWO bone rests
    // (mixamorig_LeftUpLeg/RightUpLeg) at _Ready, on every instance including
    // `_player` above. The #271 T-pose-anchor and #273 pivot rest-delta
    // families below compare clip keys against "Y Bot's rest" — that
    // reference must stay the RAW, un-anchored rest, or those two families
    // would silently start grading against a moving target. A separate,
    // freshly-instantiated res://assets/Y Bot.fbx (NOT Player.tscn — it has
    // no BlendRestAnchor node of its own) supplies that untouched ground
    // truth. `_player`'s own skeleton remains the source for bone
    // existence/track-resolution checks (structurally identical either way)
    // and for #287's own pose sampling (which is SUPPOSED to reflect the
    // anchored rest — that's the fix under test).
    private Skeleton3D _rawYBotSkeleton;

    public override void _Ready()
    {
        GD.Print("[locomotion-clip] booting headless…");

        var scene = GD.Load<PackedScene>("res://scenes/Player.tscn");
        var inst = scene.Instantiate<PlayerController>();
        inst.Name = "1";
        AddChild(inst);
        _player = inst;

        var ybotScene = GD.Load<PackedScene>("res://assets/Y Bot.fbx");
        var ybotInst = ybotScene.Instantiate();
        ybotInst.Name = "RawYBotReference";
        AddChild(ybotInst);
        _rawYBotSkeleton = FindSkeleton(ybotInst);

        _sweepTest = InstantiateSweepRig("SweepTest");
        _sweepRef0 = InstantiateSweepRig("SweepRef0");
        _sweepRef6 = InstantiateSweepRig("SweepRef6");

        _dribbleSweepTestRight = InstantiateSweepRig("DribbleSweepTestRight");
        _dribbleSweepRef0Right = InstantiateSweepRig("DribbleSweepRef0Right");
        _dribbleSweepRef6Right = InstantiateSweepRig("DribbleSweepRef6Right");
        _dribbleSweepTestLeft = InstantiateSweepRig("DribbleSweepTestLeft");
        _dribbleSweepRef0Left = InstantiateSweepRig("DribbleSweepRef0Left");
        _dribbleSweepRef6Left = InstantiateSweepRig("DribbleSweepRef6Left");

        _strideDribbleMoveRight = InstantiateSweepRig("StrideDribbleMoveRight");
        _strideDribbleIdleRight = InstantiateSweepRig("StrideDribbleIdleRight");
        _strideDribbleMoveLeft = InstantiateSweepRig("StrideDribbleMoveLeft");
        _strideDribbleIdleLeft = InstantiateSweepRig("StrideDribbleIdleLeft");
        _strideRun = InstantiateSweepRig("StrideRun");
    }

    // Sets up a Player.tscn instance for #287's manual-drive corridor sweep.
    // PlayerController.ApplyAnimation() re-derives parameters/Locomotion/
    // blend_position from live horizontal speed EVERY _PhysicsProcess tick
    // (scripts/Player/PlayerController.cs) -- left running, it would fight
    // this harness's own blend ramp, so physics/process are disabled here.
    // AnimationTree.ProcessCallback is flipped to Manual in this SAME _Ready
    // call that PlayerController's own _Ready sets Active=true in, before the
    // engine's SceneTree has processed even one physics frame -- this is what
    // guarantees the harness's own later Advance(0.0) prime call genuinely IS
    // the first Advance() this tree ever receives, reproducing the exact
    // "first advance after Active=true only primes at t=0, swallows dt"
    // gotcha the #287 diagnostic probe confirmed (not a no-op after already-
    // elapsed automatic ticks, which an engine-driven Physics-mode tree would
    // have accumulated by the time RunCorridorSweep() gets around to it).
    private PlayerController InstantiateSweepRig(string name)
    {
        var scene = GD.Load<PackedScene>("res://scenes/Player.tscn");
        var inst = scene.Instantiate<PlayerController>();
        inst.Name = name;
        AddChild(inst);
        inst.SetPhysicsProcess(false);
        inst.SetProcess(false);

        var tree = inst.GetNodeOrNull<AnimationTree>("AnimationTree");
        if (tree != null)
            tree.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Manual;

        return inst;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        _frame++;

        // Give the instanced scene a couple of frames to run _Ready on every
        // node (mirrors RigScaleHarnessTest's own settle window) before
        // trusting the skeleton is fully resolved.
        if (_frame < 2) return;

        RunCheck();

        if (!_finished && _elapsed > TimeoutSeconds)
        {
            Fail("timed out without reaching a verdict.");
            Finish(1);
        }
    }

    private void RunCheck()
    {
        var skeleton = FindSkeleton(_player);
        if (skeleton == null)
        {
            Fail("could not locate a Skeleton3D in the instanced Player.tscn.");
            Finish(1);
            return;
        }
        if (_rawYBotSkeleton == null)
        {
            Fail("could not locate a Skeleton3D in the raw (non-Player.tscn) Y Bot.fbx reference instance " +
                 "— #271/#273's rest-comparison families cannot evaluate against ground truth.");
            Finish(1);
            return;
        }

        GD.Print($"[locomotion-clip] live skeleton '{skeleton.Name}' has {skeleton.GetBoneCount()} bones.");

        var lib = GD.Load<AnimationLibrary>("res://assets/locomotion.res");
        if (lib == null)
        {
            Fail("assets/locomotion.res failed to load.");
            Finish(1);
            return;
        }

        var clipNames = lib.GetAnimationList();
        GD.Print($"[locomotion-clip] locomotion.res clips: {string.Join(", ", clipNames)}");

        // Vacuous-pass guard #1: the library itself must carry the clips
        // Player.tscn's AnimationTree references (locomotion/idle,
        // locomotion/run, locomotion/pivot, and #285's locomotion/dribbleidle +
        // locomotion/dribblemove — themselves split by hand side in #294 into
        // dribbleidleleft/dribbleidleright and dribblemoveleft/dribblemoveright,
        // since scenes/Player.tscn's single Dribble BlendSpace1D became two,
        // DribbleLeft and DribbleRight, each blending its own hand's pair) — an
        // empty or renamed library would trivially "pass" a bare per-clip loop
        // below. The per-clip track resolution loop that follows iterates the
        // WHOLE library, so all four dribble clips get #271's bone-track-match
        // proof automatically; this list is what stops that loop from silently
        // iterating nothing.
        string[] expected =
        {
            "idle", "run", "pivot",
            "dribbleidleleft", "dribbleidleright", "dribblemoveleft", "dribblemoveright",
            // #279's jump-shot family. Listed here for the same reason as the
            // rest: the per-clip track-resolution loop below iterates the WHOLE
            // library, so naming them is what stops that loop from silently
            // iterating nothing if a rebuild ever dropped them.
            "jumpshotstartup", "jumpshotactive", "jumpshotrecovery", "fadeawayactive",
        };
        var missingClips = expected.Where(e => !clipNames.Contains(e)).ToArray();
        if (missingClips.Length > 0)
        {
            Fail($"locomotion.res is missing expected clip(s): {string.Join(", ", missingClips)}.");
            Finish(1);
            return;
        }

        bool allPass = true;
        foreach (var clipName in clipNames)
        {
            var anim = lib.GetAnimation(clipName);
            int total = 0;
            int resolved = 0;
            var unresolved = new List<string>();
            for (int i = 0; i < anim.GetTrackCount(); i++)
            {
                var path = anim.TrackGetPath(i);
                if (path.GetSubNameCount() == 0) continue; // not a bone track
                total++;
                string boneName = path.GetSubName(0);
                if (skeleton.FindBone(boneName) >= 0) resolved++;
                else unresolved.Add(boneName);
            }

            GD.Print($"[locomotion-clip]   '{clipName}': total_bone_tracks={total} resolved={resolved}");

            // Vacuous-pass guard #2: a clip with zero bone tracks would
            // trivially satisfy resolved==total without proving anything —
            // exactly RigScaleHarnessTest's "heightBones <= 0" style check.
            if (total <= 0)
            {
                Fail($"clip '{clipName}' has zero bone tracks — vacuous, not proof.");
                allPass = false;
                continue;
            }

            if (resolved != total)
            {
                Fail($"clip '{clipName}': {total - resolved}/{total} bone tracks did not resolve " +
                     $"(unresolved: {string.Join(", ", unresolved)}).");
                allPass = false;
            }
        }

        if (allPass)
        {
            GD.Print("[locomotion-clip] PASS — idle/run/pivot all fully resolve on the live Y Bot skeleton.");
        }

        // --- Issue #271 assertion family 1: loop mode -----------------------
        // idle/run are the two clips that actually loop during gameplay (a
        // BlendSpace1D between them drives locomotion); pivot already shipped
        // correctly and acts as the CONTROL that proves this assertion itself
        // is discriminating (if pivot ever failed here, the assertion logic —
        // not the fix — would be the suspect).
        //
        // #285 adds dribbleidle/dribblemove here for exactly the #271 reason:
        // both were extracted straight out of a stock Mixamo FBX, whose per-clip
        // import default IS LOOP_NONE, and a dribble stance that plays once and
        // freezes is the most obvious possible regression — a ball-handler
        // standing still would visibly stop bouncing after 2.1s. The rebuild
        // tool (tools/rebuild_dribble_clips.gd) sets LoopModeEnum.Linear
        // explicitly; this is the assertion that keeps it set. #294 splits each
        // of those two clips into a left/right pair — all FOUR must be held to
        // the identical standard, not just the polarity a given tipoff happens
        // to land on, or a non-looping LEFT endpoint (#271's exact failure mode)
        // could hide behind a passing RIGHT.
        string[] mustLoop =
        {
            "idle", "run", "pivot",
            "dribbleidleleft", "dribbleidleright", "dribblemoveleft", "dribblemoveright",
        };
        foreach (var clipName in mustLoop)
        {
            var anim = lib.GetAnimation(clipName);
            var mode = anim.LoopMode;
            GD.Print($"[locomotion-clip]   '{clipName}': loop_mode={mode}");
            if (mode != Animation.LoopModeEnum.Linear)
            {
                Fail($"clip '{clipName}' has loop_mode={mode}, expected Linear (issue #271 — " +
                     "the FBX importer's per-clip default is LOOP_NONE, so run visibly freezes " +
                     "after its first pass unless the import config or rebuild step sets it).");
                allPass = false;
            }
        }

        // The complementary half (#279): the one-shots must NOT loop. Asserting
        // only the mustLoop side would leave "everything loops" passing, which
        // is a real regression shape — a jump shot whose release clip looped
        // would re-play the shooting motion over and over inside a 4-tick state.
        // `catch` (#284, already shipped LOOP_NONE) rides along as the CONTROL
        // proving this assertion itself discriminates: if it ever failed here,
        // the assertion logic — not #279's clips — would be the suspect, the
        // same role pivot plays in the mustLoop list above.
        string[] mustNotLoop =
        {
            "catch", "jumpshotstartup", "jumpshotactive", "jumpshotrecovery", "fadeawayactive",
        };
        foreach (var clipName in mustNotLoop)
        {
            var mode = lib.GetAnimation(clipName).LoopMode;
            GD.Print($"[locomotion-clip]   '{clipName}': loop_mode={mode}");
            if (mode != Animation.LoopModeEnum.None)
            {
                Fail($"clip '{clipName}' has loop_mode={mode}, expected None — these are one-shot " +
                     "committed-move phase clips (issue #279); a looping release clip would re-play " +
                     "the shot motion inside its own state.");
                allPass = false;
            }
        }

        // --- Issue #271 assertion family 2: no T-pose anchor -----------------
        // The retarget's rest-fixer preserved delta-from-source-rest instead of
        // world pose: idle/run's frame-0 arm rotation landed EXACTLY on Y Bot's
        // raw import rest (0.000000 deg deviation) — and Y Bot's rest IS a
        // T-pose (arms-horizontal is baked into the Shoulder/Arm rest). Assert
        // the first key of each arm-chain rotation track deviates from that
        // same skeleton's rest by a clearly non-zero margin. Threshold is 10
        // degrees: the observed bug value is exactly 0.0 deg, and a corrected
        // arms-down idle/run pose is expected in the 40-90 deg range for
        // Arm/Shoulder — 10 deg leaves an order-of-magnitude margin against
        // both floating-point noise and a partial (but still visibly wrong)
        // fix, without being anywhere near tight enough to demand exact pose
        // correctness (that's the deferred human feel judgment, #178/#173).
        const double TposeAngleThresholdDeg = 10.0;
        string[] armBones = { "mixamorig_LeftArm", "mixamorig_RightArm" };
        string[] anchorClips = { "idle", "run" };
        foreach (var clipName in anchorClips)
        {
            var anim = lib.GetAnimation(clipName);
            foreach (var boneName in armBones)
            {
                // Rest is read from the RAW Y Bot.fbx reference (#287's
                // BlendRestAnchor mutates two OTHER bones' rest on Player.tscn's
                // own skeleton, not these arm bones — but resolving both index
                // AND rest against the untouched reference, rather than mixing
                // sources, keeps this assertion's ground truth unambiguous).
                int boneIdx = _rawYBotSkeleton.FindBone(boneName);
                if (boneIdx < 0)
                {
                    Fail($"clip '{clipName}': raw Y Bot reference skeleton has no bone '{boneName}' to " +
                         "check against — cannot evaluate the T-pose-anchor assertion.");
                    allPass = false;
                    continue;
                }
                Quaternion restRot = _rawYBotSkeleton.GetBoneRest(boneIdx).Basis.GetRotationQuaternion();

                int trackIdx = FindRotationTrack(anim, boneName);
                if (trackIdx < 0)
                {
                    Fail($"clip '{clipName}': no rotation track for bone '{boneName}' — " +
                         "cannot evaluate the T-pose-anchor assertion.");
                    allPass = false;
                    continue;
                }
                if (anim.TrackGetKeyCount(trackIdx) <= 0)
                {
                    Fail($"clip '{clipName}': rotation track for '{boneName}' has zero keys — vacuous, not proof.");
                    allPass = false;
                    continue;
                }

                var firstKey = (Quaternion)anim.TrackGetKeyValue(trackIdx, 0);
                double deviationDeg = QuaternionAngleDeg(firstKey, restRot);
                GD.Print($"[locomotion-clip]   '{clipName}' first-key-vs-rest '{boneName}': {deviationDeg:F6} deg");

                if (deviationDeg < TposeAngleThresholdDeg)
                {
                    Fail($"clip '{clipName}': '{boneName}' first key is only {deviationDeg:F6} deg from Y Bot's " +
                         $"rest (T-pose) — expected >= {TposeAngleThresholdDeg} deg (issue #271 T-pose anchor bug).");
                    allPass = false;
                }
            }
        }

        // --- Issue #273 assertion family: pivot rest-delta correction -------
        // pivot (authored in #242 against the KENNEY characterMedium.fbx rig,
        // then bone-name-only remapped in #267) carries rotation keys
        // expressed against KENNEY's rest orientations, not Y Bot's. Godot
        // ROTATION_3D tracks are absolute local rotations, so unlike idle/run
        // (which went through the importer's rest-fixer), pivot's raw Kenney
        // rest quats get handed to Y Bot's bones verbatim — Hips/LeftUpLeg/
        // RightUpLeg land 177-180 deg off Y Bot's rest (confirmed exact in the
        // issue's fact table; Spine happens to coincide across rigs at ~0deg,
        // which is why the pose reads "collapsed", not uniformly rotated).
        //
        // Polarity is the OPPOSITE of the #271 T-pose-anchor guard above:
        // pivot's CORRECT keys sit NEAR Y Bot rest (small authored deltas
        // around a live stance), not far from it. Threshold 15 deg: the
        // observed bug values are 177-180 deg (an order of magnitude beyond),
        // and the authored inter-key motion is only 6-10 deg per track, so 15
        // deg comfortably separates "still broken" from "corrected" without
        // demanding exact pose correctness (that stays the deferred human
        // feel judgment, #178/#173).
        const double PivotRestDeltaThresholdDeg = 15.0;
        // The correction is a left-multiplication by a unit quaternion (an
        // isometry) — it must preserve the authored inter-key motion exactly.
        // Guard against a "fix" that collapses pivot to static rests instead
        // of correcting them: every track's keys must still span at least
        // this much pairwise deviation. Observed authored motion is 6-10 deg;
        // 3 deg leaves margin against floating-point noise while still ruling
        // out a degenerate all-keys-equal "fix".
        const double PivotMinPairwiseMotionDeg = 3.0;
        // SCOPE (turning-T-pose fix, 2026-07-25): this rest-delta/motion guard
        // now applies to pivot's FOUR AUTHORED PLANT BONES only, by name.
        // pivot originally carried exactly these four rotation tracks; the
        // fix (tools/rebuild_pivot_upperbody.gd) added ~25 upper-body/limb
        // "hold" tracks that are INTENTIONALLY far off rest (they hold idle's
        // arms-down stance so a turn no longer reveals the T-pose rest), so a
        // blanket "every pivot key near rest" assertion is no longer correct —
        // the completeness family below checks those added tracks with the
        // OPPOSITE polarity. The plant bones remain the #273 subject: their
        // Kenney-rest-relative keys had to be corrected to sit NEAR Y Bot rest.
        string[] pivotPlantBones =
        {
            "mixamorig_Hips", "mixamorig_Spine",
            "mixamorig_LeftUpLeg", "mixamorig_RightUpLeg",
        };

        var pivotAnim = lib.GetAnimation("pivot");
        var pivotRotBoneToTrack = new Dictionary<string, int>();
        for (int i = 0; i < pivotAnim.GetTrackCount(); i++)
        {
            if (pivotAnim.TrackGetType(i) != Animation.TrackType.Rotation3D) continue;
            var path = pivotAnim.TrackGetPath(i);
            if (path.GetSubNameCount() == 0) continue;
            pivotRotBoneToTrack[path.GetSubName(0)] = i;
        }

        GD.Print($"[locomotion-clip]   'pivot': rotation_track_count={pivotRotBoneToTrack.Count}");

        foreach (var boneName in pivotPlantBones)
        {
            if (!pivotRotBoneToTrack.TryGetValue(boneName, out int trackIdx))
            {
                Fail($"clip 'pivot': missing authored plant rotation track for '{boneName}' " +
                     "(issue #273 — the 4 plant bones must always be present).");
                allPass = false;
                continue;
            }

            // #287: pivot's plant tracks include mixamorig_LeftUpLeg/
            // RightUpLeg — the EXACT two bones BlendRestAnchor re-anchors on
            // Player.tscn's own skeleton. This assertion's whole point is
            // "pivot's authored keys sit near Y BOT'S REAL REST", so it must
            // read rest from the RAW, un-anchored reference skeleton or it
            // would silently start grading pivot against its own fix instead
            // of the ground truth (a false-negative sinkhole: an actually-
            // broken pivot clip could still pass by coincidentally landing
            // near the ANCHORED rest instead of Y Bot's real one).
            int boneIdx = _rawYBotSkeleton.FindBone(boneName);
            if (boneIdx < 0)
            {
                Fail($"clip 'pivot': raw Y Bot reference skeleton has no bone '{boneName}' to check " +
                     "against — cannot evaluate the rest-delta assertion.");
                allPass = false;
                continue;
            }
            Quaternion restRot = _rawYBotSkeleton.GetBoneRest(boneIdx).Basis.GetRotationQuaternion();

            int keyCount = pivotAnim.TrackGetKeyCount(trackIdx);
            if (keyCount <= 0)
            {
                Fail($"clip 'pivot': rotation track for '{boneName}' has zero keys — vacuous, not proof.");
                allPass = false;
                continue;
            }

            var keys = new List<Quaternion>(keyCount);
            for (int k = 0; k < keyCount; k++)
            {
                keys.Add((Quaternion)pivotAnim.TrackGetKeyValue(trackIdx, k));
            }

            double maxRestDeviationDeg = 0.0;
            foreach (var key in keys)
            {
                double deviationDeg = QuaternionAngleDeg(key, restRot);
                if (deviationDeg > maxRestDeviationDeg) maxRestDeviationDeg = deviationDeg;
            }

            double maxPairwiseDeviationDeg = 0.0;
            for (int a = 0; a < keys.Count; a++)
            {
                for (int b = a + 1; b < keys.Count; b++)
                {
                    double devDeg = QuaternionAngleDeg(keys[a], keys[b]);
                    if (devDeg > maxPairwiseDeviationDeg) maxPairwiseDeviationDeg = devDeg;
                }
            }

            GD.Print($"[locomotion-clip]   'pivot' plant '{boneName}': max_vs_ybot_rest={maxRestDeviationDeg:F6} deg, " +
                      $"max_pairwise_key_deviation={maxPairwiseDeviationDeg:F6} deg");

            if (maxRestDeviationDeg >= PivotRestDeltaThresholdDeg)
            {
                Fail($"clip 'pivot': plant '{boneName}' has a key {maxRestDeviationDeg:F6} deg from Y Bot's rest — " +
                     $"expected < {PivotRestDeltaThresholdDeg} deg (issue #273 Kenney-rest-relative bug).");
                allPass = false;
            }

            if (maxPairwiseDeviationDeg < PivotMinPairwiseMotionDeg)
            {
                Fail($"clip 'pivot': plant '{boneName}' keys only span {maxPairwiseDeviationDeg:F6} deg pairwise — " +
                     $"expected >= {PivotMinPairwiseMotionDeg} deg (clip must still actually animate, not " +
                     "collapse to static rests).");
                allPass = false;
            }
        }

        // --- Turning-T-pose assertion family: pivot upper-body completeness -
        // Root cause of the "turning T-poses the arms" bug (confirmed headless,
        // Godot 4.7.1): the Pivot state is a SINGLE clip played at FULL WEIGHT.
        // Godot's AnimationMixer writes every bone the active clip does NOT
        // track to the skeleton's REST transform — and Y Bot's rest is a
        // Mixamo T-pose. pivot originally tracked only the 4 plant bones above,
        // so the entire upper body (arms/shoulders/spine chain/head) snapped to
        // the T-pose the instant a turn entered the Pivot state. idle/run were
        // immune because they DO track the arms.
        //
        // Fix (tools/rebuild_pivot_upperbody.gd): copy idle's frame-0 pose for
        // every rotation bone pivot lacked, held as a constant key, so the
        // upper body holds the neutral idle stance through the plant. Assert
        // pivot now drives the arm chain with each first key clearly OFF rest
        // (same >= 10 deg polarity as #271's idle/run T-pose-anchor guard —
        // the observed pre-fix value is effectively 0 deg / rest). A minimum
        // total rotation-track count guards against silently reverting to the
        // 4-track clip. Whether the held pose LOOKS right stays the deferred
        // human feel judgment (#178/#173, ADR-0021).
        const double PivotArmOffRestThresholdDeg = 10.0;
        const int PivotMinRotationTrackCount = 20; // 4 plant + full upper/limb hold (observed: 29)
        string[] pivotArmChain =
        {
            "mixamorig_LeftShoulder", "mixamorig_RightShoulder",
            "mixamorig_LeftArm", "mixamorig_RightArm",
            "mixamorig_LeftForeArm", "mixamorig_RightForeArm",
        };

        if (pivotRotBoneToTrack.Count < PivotMinRotationTrackCount)
        {
            Fail($"clip 'pivot': only {pivotRotBoneToTrack.Count} rotation tracks — expected >= " +
                 $"{PivotMinRotationTrackCount} (turning-T-pose fix: pivot must carry the upper-body/limb " +
                 "hold tracks, not just the 4 plant bones, or the Pivot state resets untracked bones to the " +
                 "T-pose rest).");
            allPass = false;
        }

        foreach (var boneName in pivotArmChain)
        {
            if (!pivotRotBoneToTrack.TryGetValue(boneName, out int trackIdx))
            {
                Fail($"clip 'pivot': no rotation track for arm-chain bone '{boneName}' — the Pivot state " +
                     "would reset it to Y Bot's T-pose rest during a turn (turning-T-pose bug).");
                allPass = false;
                continue;
            }
            int boneIdx = _rawYBotSkeleton.FindBone(boneName);
            if (boneIdx < 0)
            {
                Fail($"clip 'pivot': raw Y Bot reference skeleton has no bone '{boneName}' to check against.");
                allPass = false;
                continue;
            }
            if (pivotAnim.TrackGetKeyCount(trackIdx) <= 0)
            {
                Fail($"clip 'pivot': arm-chain track for '{boneName}' has zero keys — vacuous, not proof.");
                allPass = false;
                continue;
            }
            Quaternion armRestRot = _rawYBotSkeleton.GetBoneRest(boneIdx).Basis.GetRotationQuaternion();
            var armFirstKey = (Quaternion)pivotAnim.TrackGetKeyValue(trackIdx, 0);
            double armDeviationDeg = QuaternionAngleDeg(armFirstKey, armRestRot);
            GD.Print($"[locomotion-clip]   'pivot' arm '{boneName}': first-key-vs-ybot-rest={armDeviationDeg:F6} deg");

            if (armDeviationDeg < PivotArmOffRestThresholdDeg)
            {
                Fail($"clip 'pivot': arm-chain '{boneName}' first key is only {armDeviationDeg:F6} deg from Y Bot's " +
                     $"rest (T-pose) — expected >= {PivotArmOffRestThresholdDeg} deg (turning-T-pose bug: the arm " +
                     "would sit horizontal during a turn).");
                allPass = false;
            }
        }

        // --- Issue #279 assertion family: the jump-shot clip family ---------
        // #279 drafted four one-shot clips by slicing `Goalkeeper Catch
        // Stationary` (tools/rebuild_jumpshot_clips.gd) and repointed
        // Player.tscn's JumpshotStartup/Active/Recovery + FadeawayActive states
        // off the shared `locomotion/idle` placeholder onto them. Four
        // independent things can silently go wrong, one per block below.
        //
        // NOTE this family deliberately asserts nothing about whether the pose
        // LOOKS like a jump shot — that is #173's deferred human feel judgment
        // (ADR-0021) and does not gate merge (#276). What it pins is that the
        // clips are structurally incapable of the failures this repo has
        // actually shipped before.

        // (a) Segment lengths == the move's real tick windows.
        //
        // Read from JumpShot.DefaultFrameData and Engine.PhysicsTicksPerSecond
        // rather than hardcoded here, on purpose. rebuild_jumpshot_clips.gd has
        // to duplicate 18/4/20 (GDScript cannot read the C# constant), so this
        // assertion is what makes that duplication SAFE instead of merely
        // regrettable: retune JumpShot's frame data without re-running the tool
        // and this goes red and names the tool. A clip longer than its window
        // gets cut off mid-motion; a clip shorter than it freezes on its last
        // frame for the remainder — either way the wind-up an opponent reads
        // stops matching the real window, which is exactly the "no false reads"
        // requirement (#276 point 4, ADR-0003).
        var jsFrames = JumpShot.DefaultFrameData;
        double tps = Engine.PhysicsTicksPerSecond;
        // Animation.length is a 32-bit float; 20/60 round-trips to ~3e-7 of the
        // double. 1e-4 is far below one tick (0.0167 s) yet far above that noise.
        const double LengthToleranceSeconds = 1e-4;
        (string Clip, int Ticks)[] jumpshotWindows =
        {
            ("jumpshotstartup", jsFrames.StartupFrames),
            ("jumpshotactive", jsFrames.ActiveFrames),
            ("jumpshotrecovery", jsFrames.RecoveryFrames),
            // The fadeaway is an Active-phase-only variant (#243's state
            // contract), so it fills the SAME window as the standard release.
            ("fadeawayactive", jsFrames.ActiveFrames),
        };
        foreach (var (clipName, ticks) in jumpshotWindows)
        {
            double expectedSeconds = ticks / tps;
            double actualSeconds = lib.GetAnimation(clipName).Length;
            GD.Print($"[locomotion-clip]   '{clipName}': length={actualSeconds:F6}s " +
                     $"expected={expectedSeconds:F6}s ({ticks} ticks @ {tps} tps)");
            if (System.Math.Abs(actualSeconds - expectedSeconds) > LengthToleranceSeconds)
            {
                Fail($"clip '{clipName}' is {actualSeconds:F6}s, expected {expectedSeconds:F6}s " +
                     $"({ticks} ticks at {tps} tps — JumpShot.DefaultFrameData). Re-run " +
                     "tools/rebuild_jumpshot_clips.gd after retuning the move's frame data.");
                allPass = false;
            }
        }

        // (b) Full-body track coverage — the a45bd1d trap, same shape as the
        // pivot completeness guard above but by construction rather than by
        // repair: each clip is a SLICE of a 52-rotation-track source, so it
        // should inherit every one. A slice that lost tracks would rest-pose
        // (T-pose) the missing bones the instant its state was entered, and
        // would do so silently. 50 rather than 52 leaves headroom for a source
        // re-export without leaving room for the arms to go missing.
        const int JumpshotMinRotationTrackCount = 50;
        // (c) Upper body posed, not at rest — #276's temp-draft verification
        // clause names this explicitly. Same >= 10 deg polarity as the #271
        // idle/run T-pose-anchor guard and the pivot arm-chain guard: the
        // observed bug value is effectively 0 deg (landing exactly ON rest),
        // and a real shooting pose puts the arms nowhere near Y Bot's
        // arms-horizontal rest, so 10 deg is an order of magnitude of margin
        // without demanding pose correctness.
        const double JumpshotArmOffRestThresholdDeg = 10.0;
        string[] jumpshotClips =
        {
            "jumpshotstartup", "jumpshotactive", "jumpshotrecovery", "fadeawayactive",
        };
        // Every arm-chain bone must be TRACKED — that is the structural
        // a45bd1d guard, since only an untracked bone gets written to rest.
        string[] jumpshotArmChain =
        {
            "mixamorig_LeftShoulder", "mixamorig_RightShoulder",
            "mixamorig_LeftArm", "mixamorig_RightArm",
            "mixamorig_LeftForeArm", "mixamorig_RightForeArm",
        };
        // ...but only the SHOULDER and ARM bones are graded on sitting off rest.
        //
        // ForeArm is deliberately excluded, and this is a measurement, not a
        // threshold fudged to turn a red green. ROTATION_3D keys are
        // PARENT-RELATIVE local rotations, so a forearm's own key encodes only
        // the ELBOW BEND — arm elevation lives entirely in Shoulder/Arm. At the
        // top of a shot the elbow is nearly straight, and in Y Bot's T-pose rest
        // the elbow is ALSO nearly straight, so the two local rotations
        // genuinely coincide: 'jumpshotactive' measures RightForeArm at 6.4 deg
        // from rest, which is the anatomically correct answer for a full
        // extension, not a T-pose symptom. (The same bones sit 32-40 deg off
        // rest in 'jumpshotrecovery', where the elbows re-bend on the way down —
        // the value tracks the bend, exactly as it should.) Grading ForeArm here
        // would mean asserting that a shooter's elbow must never straighten.
        //
        // pivot's own arm-chain guard above DOES include ForeArm because it
        // holds idle's arms-down stance, where the elbows carry a natural bend.
        // Nothing about that assertion changes.
        string[] jumpshotElevationBones =
        {
            "mixamorig_LeftShoulder", "mixamorig_RightShoulder",
            "mixamorig_LeftArm", "mixamorig_RightArm",
        };
        foreach (var clipName in jumpshotClips)
        {
            var anim = lib.GetAnimation(clipName);

            int rotTrackCount = 0;
            for (int i = 0; i < anim.GetTrackCount(); i++)
            {
                if (anim.TrackGetType(i) == Animation.TrackType.Rotation3D) rotTrackCount++;
            }
            GD.Print($"[locomotion-clip]   '{clipName}': rotation_track_count={rotTrackCount}");
            if (rotTrackCount < JumpshotMinRotationTrackCount)
            {
                Fail($"clip '{clipName}': only {rotTrackCount} rotation tracks — expected >= " +
                     $"{JumpshotMinRotationTrackCount}. A per-move state plays ONE clip at FULL " +
                     "weight, so every bone the clip omits is written to Y Bot's T-pose rest " +
                     "(a45bd1d).");
                allPass = false;
            }

            foreach (var boneName in jumpshotArmChain)
            {
                int trackIdx = FindRotationTrack(anim, boneName);
                if (trackIdx < 0)
                {
                    Fail($"clip '{clipName}': no rotation track for arm-chain bone '{boneName}' — " +
                         "that bone would sit at Y Bot's T-pose rest for the whole phase.");
                    allPass = false;
                    continue;
                }
                if (anim.TrackGetKeyCount(trackIdx) <= 0)
                {
                    Fail($"clip '{clipName}': arm-chain track for '{boneName}' has zero keys — " +
                         "vacuous, not proof.");
                    allPass = false;
                    continue;
                }

                // Presence + keys is asserted for the whole chain above; the
                // off-rest POSE grade applies only to the elevation-carrying
                // bones (see the comment on jumpshotElevationBones).
                if (!jumpshotElevationBones.Contains(boneName)) continue;

                int boneIdx = _rawYBotSkeleton.FindBone(boneName);
                if (boneIdx < 0)
                {
                    Fail($"clip '{clipName}': raw Y Bot reference skeleton has no bone '{boneName}'.");
                    allPass = false;
                    continue;
                }
                Quaternion jsRestRot = _rawYBotSkeleton.GetBoneRest(boneIdx).Basis.GetRotationQuaternion();
                var jsFirstKey = (Quaternion)anim.TrackGetKeyValue(trackIdx, 0);
                double jsDeviationDeg = QuaternionAngleDeg(jsFirstKey, jsRestRot);
                GD.Print($"[locomotion-clip]   '{clipName}' arm '{boneName}': " +
                         $"first-key-vs-ybot-rest={jsDeviationDeg:F6} deg");
                if (jsDeviationDeg < JumpshotArmOffRestThresholdDeg)
                {
                    Fail($"clip '{clipName}': arm-chain '{boneName}' first key is only " +
                         $"{jsDeviationDeg:F6} deg from Y Bot's rest (T-pose) — expected >= " +
                         $"{JumpshotArmOffRestThresholdDeg} deg (#276 temp-draft bar: the upper body " +
                         "must be POSED, not at rest).");
                    allPass = false;
                }
            }
        }

        // (d) The fadeaway must actually differ from the squared-up release.
        // FadeawayActive exists as a separate state precisely so an off-balance
        // shot READS as one (#243); if the rebuild tool's spine lean were ever
        // dropped, both states would play identical clips and the state would be
        // decorative. rebuild_jumpshot_clips.gd applies a 22 deg lean and proves
        // its DIRECTION geometrically at build time (head displacement against
        // the facing axis); this pins the MAGNITUDE surviving into the asset.
        // 5 deg is well under the authored 22 while ruling out "identical".
        const double FadeawayMinPoseDeltaDeg = 5.0;
        var standardActive = lib.GetAnimation("jumpshotactive");
        var fadeawayActive = lib.GetAnimation("fadeawayactive");
        double worstFadeawayDeltaDeg = 0.0;
        int comparedTracks = 0;
        for (int i = 0; i < standardActive.GetTrackCount(); i++)
        {
            if (standardActive.TrackGetType(i) != Animation.TrackType.Rotation3D) continue;
            var path = standardActive.TrackGetPath(i);
            if (path.GetSubNameCount() == 0) continue;
            int j = FindRotationTrack(fadeawayActive, path.GetSubName(0));
            if (j < 0) continue;
            comparedTracks++;
            int keys = System.Math.Min(standardActive.TrackGetKeyCount(i), fadeawayActive.TrackGetKeyCount(j));
            for (int k = 0; k < keys; k++)
            {
                double d = QuaternionAngleDeg(
                    (Quaternion)standardActive.TrackGetKeyValue(i, k),
                    (Quaternion)fadeawayActive.TrackGetKeyValue(j, k));
                if (d > worstFadeawayDeltaDeg) worstFadeawayDeltaDeg = d;
            }
        }
        GD.Print($"[locomotion-clip]   'fadeawayactive' vs 'jumpshotactive': compared {comparedTracks} shared " +
                 $"rotation tracks, max key delta={worstFadeawayDeltaDeg:F3} deg");
        // Vacuous-pass guard: with zero compared tracks the max would be 0 and
        // this would fail for the wrong reason, so say which reason it is.
        if (comparedTracks < JumpshotMinRotationTrackCount)
        {
            Fail($"'fadeawayactive' and 'jumpshotactive' share only {comparedTracks} rotation tracks — " +
                 $"expected >= {JumpshotMinRotationTrackCount}; the fadeaway is built as a copy of the " +
                 "standard release, so a small overlap means one of them lost tracks.");
            allPass = false;
        }
        else if (worstFadeawayDeltaDeg < FadeawayMinPoseDeltaDeg)
        {
            Fail($"'fadeawayactive' differs from 'jumpshotactive' by only {worstFadeawayDeltaDeg:F3} deg — " +
                 $"expected >= {FadeawayMinPoseDeltaDeg} deg. The two states would show the same pose, so " +
                 "an off-balance shot would be indistinguishable from a squared-up one (#243). Check the " +
                 "spine lean in tools/rebuild_jumpshot_clips.gd.");
            allPass = false;
        }

        // --- Issue #280 assertion family: the crossover clip family ---------
        // #280 drafted SIX one-shots (tools/rebuild_crossover_clips.gd) by
        // slicing `Dribble` and composing a signed cross-body swing onto each
        // slice: three phases x two hand-side polarities. The suffix names the
        // hand the ball STARTED in, so 'left' carries the ball toward the body's
        // RIGHT.
        //
        // Blocks (a)-(c) are the #279 family re-pointed at six clips and one
        // move's frame data; they are the same three failures and need no fresh
        // argument. Blocks (d) and (e) are what is genuinely new here, and both
        // exist because a crossover is DIRECTIONAL: ball-hand-side is
        // authoritative (ADR-0012) and a clip that plays the wrong polarity is
        // not a blemish but a FALSE TELEGRAPH, which ADR-0003 treats as a
        // competitive defect. Both are NON-SYMMETRIC under an L<->R swap on
        // purpose — the #255 mirror bug shipped because its test was symmetric
        // and passed on a broken mirror.
        //
        // As with #279, nothing here asserts the pose LOOKS like a crossover;
        // that is #173's deferred human judgment (ADR-0021) and does not gate.

        // (a) Segment lengths == the move's real tick windows.
        // Read from Crossover.DefaultFrameData, not hardcoded, for the same
        // reason as the jump shot's: rebuild_crossover_clips.gd must duplicate
        // 6/3/12 because GDScript cannot read the C# constant, and this is what
        // makes that duplication safe. The pending #238 consolidated tuning pass
        // is expected to touch these magnitudes, so this tripwire is not
        // hypothetical — retune without re-running the tool and this goes red
        // and names the tool.
        var crFrames = Crossover.DefaultFrameData;
        (string Clip, int Ticks)[] crossoverWindows =
        {
            ("crossoverstartupleft", crFrames.StartupFrames),
            ("crossoveractiveleft", crFrames.ActiveFrames),
            ("crossoverrecoveryleft", crFrames.RecoveryFrames),
            ("crossoverstartupright", crFrames.StartupFrames),
            ("crossoveractiveright", crFrames.ActiveFrames),
            ("crossoverrecoveryright", crFrames.RecoveryFrames),
        };
        foreach (var (clipName, ticks) in crossoverWindows)
        {
            if (!lib.HasAnimation(clipName))
            {
                Fail($"AnimationLibrary has no clip '{clipName}' — run " +
                     "tools/rebuild_crossover_clips.gd.");
                allPass = false;
                continue;
            }
            double expectedSeconds = ticks / tps;
            double actualSeconds = lib.GetAnimation(clipName).Length;
            GD.Print($"[locomotion-clip]   '{clipName}': length={actualSeconds:F6}s " +
                     $"expected={expectedSeconds:F6}s ({ticks} ticks @ {tps} tps)");
            if (System.Math.Abs(actualSeconds - expectedSeconds) > LengthToleranceSeconds)
            {
                Fail($"clip '{clipName}' is {actualSeconds:F6}s, expected {expectedSeconds:F6}s " +
                     $"({ticks} ticks at {tps} tps — Crossover.DefaultFrameData). Re-run " +
                     "tools/rebuild_crossover_clips.gd after retuning the move's frame data.");
                allPass = false;
            }
        }

        // (b) One-shots must not loop. A looping 3-tick Active would re-play the
        // cross for as long as the state was held, reading as a stutter rather
        // than a single commitment. The FBX import default happens to be
        // LOOP_NONE and the tool sets it explicitly anyway; this pins it,
        // because the dribble clips (dribbleidleleft/right, dribblemoveleft/
        // right — #294 split them by hand from the original dribbleidle/
        // dribblemove) needed the OPPOSITE default and that silent asymmetry
        // was the easiest thing to get wrong in #285.
        foreach (var (clipName, _) in crossoverWindows)
        {
            if (!lib.HasAnimation(clipName)) continue;
            var loopMode = lib.GetAnimation(clipName).LoopMode;
            if (loopMode != Animation.LoopModeEnum.None)
            {
                Fail($"clip '{clipName}' has loop_mode={loopMode}, expected None — a committed-move " +
                     "phase clip is a one-shot (#279's rule, unchanged).");
                allPass = false;
            }
        }

        // (c) Full-body coverage + upper body posed, not at rest (a45bd1d).
        // Same threshold as the jump-shot family, and the whole arm chain is
        // still asserted PRESENT and non-empty — that is the structural a45bd1d
        // guard, since only an UNTRACKED bone gets written to rest.
        //
        // The off-rest POSE grade, though, applies to the Arm bones only. This
        // family carves out the CLAVICLES on top of #279's ForeArm carve-out,
        // and for the same kind of reason — a measurement, not a threshold
        // relaxed to turn a red green.
        //
        // Measured on the committed clips, the clavicles sit 2.6-8.3 deg from Y
        // Bot's rest at the first Startup key while the humerus sits 57-120 deg
        // off it. That is anatomy, not a T-pose: a real clavicle has very little
        // range, and in a low dribble stance it barely leaves neutral, whereas a
        // jump shot passes the same assertion only because reaching overhead
        // genuinely elevates it. Grading it here would assert that a
        // ball-handler's collarbone must be wrenched >= 10 deg from neutral.
        //
        // What proves these tracks are live rather than stuck at rest is that
        // the SAME clavicle tracks measure 20.9 / 25.5 / 21.8 / 17.2 deg in the
        // Recovery clips, where the composed swing has ramped up. A bone written
        // to rest reads identically in every clip; one that varies 2.6 -> 25 deg
        // across the family is demonstrably tracked. The Arm bones, still
        // graded, carry the "upper body is posed" proof on their own.
        //
        // Worth knowing when this one goes red: BlendRestAnchor re-anchors both
        // UpLeg rests to `idle`'s first key (#287), so a clip that lost those
        // two tracks would pose into idle's crouch rather than a visible T-pose.
        // The track-count assertion, not a visual check, is what catches it.
        string[] crossoverElevationBones = { "mixamorig_LeftArm", "mixamorig_RightArm" };
        foreach (var (clipName, _) in crossoverWindows)
        {
            if (!lib.HasAnimation(clipName)) continue;
            var anim = lib.GetAnimation(clipName);

            int rotTrackCount = 0;
            for (int i = 0; i < anim.GetTrackCount(); i++)
            {
                if (anim.TrackGetType(i) == Animation.TrackType.Rotation3D) rotTrackCount++;
            }
            GD.Print($"[locomotion-clip]   '{clipName}': rotation_track_count={rotTrackCount}");
            if (rotTrackCount < JumpshotMinRotationTrackCount)
            {
                Fail($"clip '{clipName}': only {rotTrackCount} rotation tracks — expected >= " +
                     $"{JumpshotMinRotationTrackCount}. A per-move state plays ONE clip at FULL " +
                     "weight, so every bone the clip omits is written to rest (a45bd1d).");
                allPass = false;
            }

            foreach (var boneName in jumpshotArmChain)
            {
                int trackIdx = FindRotationTrack(anim, boneName);
                if (trackIdx < 0)
                {
                    Fail($"clip '{clipName}': no rotation track for arm-chain bone '{boneName}' — " +
                         "that bone would sit at rest for the whole phase.");
                    allPass = false;
                    continue;
                }
                if (anim.TrackGetKeyCount(trackIdx) <= 0)
                {
                    Fail($"clip '{clipName}': arm-chain track for '{boneName}' has zero keys — " +
                         "vacuous, not proof.");
                    allPass = false;
                    continue;
                }
                if (!crossoverElevationBones.Contains(boneName)) continue;

                int boneIdx = _rawYBotSkeleton.FindBone(boneName);
                if (boneIdx < 0)
                {
                    Fail($"clip '{clipName}': raw Y Bot reference skeleton has no bone '{boneName}'.");
                    allPass = false;
                    continue;
                }
                Quaternion crRestRot = _rawYBotSkeleton.GetBoneRest(boneIdx).Basis.GetRotationQuaternion();
                var crFirstKey = (Quaternion)anim.TrackGetKeyValue(trackIdx, 0);
                double crDeviationDeg = QuaternionAngleDeg(crFirstKey, crRestRot);
                GD.Print($"[locomotion-clip]   '{clipName}' arm '{boneName}': " +
                         $"first-key-vs-ybot-rest={crDeviationDeg:F6} deg");
                if (crDeviationDeg < JumpshotArmOffRestThresholdDeg)
                {
                    Fail($"clip '{clipName}': arm-chain '{boneName}' first key is only " +
                         $"{crDeviationDeg:F6} deg from Y Bot's rest (T-pose) — expected >= " +
                         $"{JumpshotArmOffRestThresholdDeg} deg (#276 temp-draft bar: the upper body " +
                         "must be POSED, not at rest).");
                    allPass = false;
                }
            }
        }

        // (d) The two polarities must actually be mirrors, not copies.
        //
        // This is the assertion the #255 lesson demands, and the reason it is
        // phrased as a per-phase comparison rather than a single number: the
        // rebuild tool proves the cross DIRECTION geometrically at build time
        // (signed hand-midpoint travel, opposite signs, measured +0.2376 m and
        // -0.2704 m), but a build-time proof only covers the moment the asset
        // was generated. If someone later re-pointed one polarity's clip, copied
        // a clip over its twin, or shipped a tool change that dropped the sign,
        // the ASSET would be wrong and every state-name assertion in
        // CrossoverAnimTest would still pass — because the state names would
        // remain perfectly correct while both states played the same motion.
        //
        // Deliberately compares each phase against its OWN twin rather than
        // comparing, say, both Actives against a stored constant. Two clips
        // being far apart is only meaningful pairwise.
        const double CrossoverMinPolarityDeltaDeg = 15.0;
        (string Left, string Right)[] crossoverTwins =
        {
            ("crossoverstartupleft", "crossoverstartupright"),
            ("crossoveractiveleft", "crossoveractiveright"),
            ("crossoverrecoveryleft", "crossoverrecoveryright"),
        };
        foreach (var (leftName, rightName) in crossoverTwins)
        {
            if (!lib.HasAnimation(leftName) || !lib.HasAnimation(rightName)) continue;
            var leftAnim = lib.GetAnimation(leftName);
            var rightAnim = lib.GetAnimation(rightName);

            double worstPolarityDeltaDeg = 0.0;
            int polarityComparedTracks = 0;
            for (int i = 0; i < leftAnim.GetTrackCount(); i++)
            {
                if (leftAnim.TrackGetType(i) != Animation.TrackType.Rotation3D) continue;
                var path = leftAnim.TrackGetPath(i);
                if (path.GetSubNameCount() == 0) continue;
                int j = FindRotationTrack(rightAnim, path.GetSubName(0));
                if (j < 0) continue;
                polarityComparedTracks++;
                int keys = System.Math.Min(leftAnim.TrackGetKeyCount(i), rightAnim.TrackGetKeyCount(j));
                for (int k = 0; k < keys; k++)
                {
                    double d = QuaternionAngleDeg(
                        (Quaternion)leftAnim.TrackGetKeyValue(i, k),
                        (Quaternion)rightAnim.TrackGetKeyValue(j, k));
                    if (d > worstPolarityDeltaDeg) worstPolarityDeltaDeg = d;
                }
            }
            GD.Print($"[locomotion-clip]   '{leftName}' vs '{rightName}': compared " +
                     $"{polarityComparedTracks} shared rotation tracks, max key delta=" +
                     $"{worstPolarityDeltaDeg:F3} deg");
            // Vacuous-pass guard, same shape as the fadeaway's: with zero shared
            // tracks the max would be 0 and this would fail for the wrong reason.
            if (polarityComparedTracks < JumpshotMinRotationTrackCount)
            {
                Fail($"'{leftName}' and '{rightName}' share only {polarityComparedTracks} rotation " +
                     $"tracks — expected >= {JumpshotMinRotationTrackCount}; both polarities are built " +
                     "from the same source, so a small overlap means one of them lost tracks.");
                allPass = false;
            }
            else if (worstPolarityDeltaDeg < CrossoverMinPolarityDeltaDeg)
            {
                Fail($"'{leftName}' and '{rightName}' differ by only {worstPolarityDeltaDeg:F3} deg — " +
                     $"expected >= {CrossoverMinPolarityDeltaDeg} deg. The two hand-side variants would " +
                     "play the same motion, so a crossover would telegraph the same direction whichever " +
                     "way the ball actually went (ADR-0012/ADR-0003). Check the cross_sign handling in " +
                     "tools/rebuild_crossover_clips.gd.");
                allPass = false;
            }
        }

        // (e) Each of the six .tscn states must point at its OWN clip.
        //
        // The gap this closes, and the reason it reads the SCENE rather than the
        // library: every other crossover assertion — here and in
        // CrossoverAnimTest — verifies either the clips or the state NAMES. None
        // of them verifies the mapping BETWEEN them. Hand-authoring six
        // near-identical AnimationNodeAnimation sub-resources makes a
        // copy-pasted SubResource id the single most likely mistake in the whole
        // change, and its symptom is invisible to both: GetCurrentNode() would
        // still report "CrossoverStartupRight", the clips would still be
        // provably distinct by (d), and the player would still play the wrong
        // one. A wrong mapping is exactly as bad as a wrong clip.
        //
        // This is also the instrument #279 concluded was needed for facts about
        // the tree that Travel() cannot expose: inspect the
        // AnimationNodeStateMachine resource directly instead of driving it.
        var playerScene = GD.Load<PackedScene>("res://scenes/Player.tscn");
        var sceneState = playerScene.GetState();
        AnimationNodeStateMachine stateMachine = null;
        for (int i = 0; i < sceneState.GetNodeCount(); i++)
        {
            if (sceneState.GetNodeType(i) != "AnimationTree") continue;
            for (int p = 0; p < sceneState.GetNodePropertyCount(i); p++)
            {
                if (sceneState.GetNodePropertyName(i, p) != "tree_root") continue;
                stateMachine = sceneState.GetNodePropertyValue(i, p).As<AnimationNodeStateMachine>();
            }
        }
        if (stateMachine == null)
        {
            Fail("could not read an AnimationNodeStateMachine off scenes/Player.tscn's AnimationTree " +
                 "tree_root — the state<->clip mapping is unverified.");
            allPass = false;
        }
        else
        {
            (string State, string Clip)[] expectedStateClips =
            {
                ("CrossoverStartupLeft", "locomotion/crossoverstartupleft"),
                ("CrossoverActiveLeft", "locomotion/crossoveractiveleft"),
                ("CrossoverRecoveryLeft", "locomotion/crossoverrecoveryleft"),
                ("CrossoverStartupRight", "locomotion/crossoverstartupright"),
                ("CrossoverActiveRight", "locomotion/crossoveractiveright"),
                ("CrossoverRecoveryRight", "locomotion/crossoverrecoveryright"),
            };
            var seenClips = new List<string>();
            foreach (var (stateName, expectedClip) in expectedStateClips)
            {
                if (!stateMachine.HasNode(stateName))
                {
                    Fail($"scenes/Player.tscn's state machine has no state '{stateName}' — the resolver " +
                         "emits that name and Travel() to a missing state only LOGS (#257), so the move " +
                         "would silently keep showing whatever was playing.");
                    allPass = false;
                    continue;
                }
                var animNode = stateMachine.GetNode(stateName) as AnimationNodeAnimation;
                if (animNode == null)
                {
                    Fail($"state '{stateName}' is not an AnimationNodeAnimation — a per-move state must " +
                         "be a single full-weight clip, never a blend (#287).");
                    allPass = false;
                    continue;
                }
                string actualClip = animNode.Animation;
                seenClips.Add(actualClip);
                GD.Print($"[locomotion-clip]   state '{stateName}' -> clip '{actualClip}'");
                if (actualClip != expectedClip)
                {
                    Fail($"state '{stateName}' points at clip '{actualClip}', expected '{expectedClip}'. " +
                         "The state name and the clip disagree, so the tree would enter the correctly-" +
                         "named state and play the wrong polarity — invisible to every state-name " +
                         "assertion.");
                    allPass = false;
                }
            }
            // Distinctness as its own check rather than as a consequence of the
            // six equality checks above: if all six expectations were ever
            // edited to the same value in one careless sweep, the loop would
            // pass. This cannot.
            if (seenClips.Count == expectedStateClips.Length &&
                seenClips.Distinct().Count() != expectedStateClips.Length)
            {
                Fail($"the six crossover states point at only {seenClips.Distinct().Count()} distinct " +
                     "clips — at least two share one, so at least one polarity or phase is duplicated.");
                allPass = false;
            }
        }

        // --- Issue #275 assertion family: idle<->run blend-compatibility ----
        // The Locomotion BlendSpace1D (idle@0.0 <-> run@6.0) transits an
        // out-of-corridor pose at intermediate blend weights on the upper-leg
        // bones (the human-visible start/stop-run twitch). Root cause,
        // confirmed by a headless empirical probe (Godot 4.7.1, real Y Bot
        // skeleton, code-built blend space mirroring this scene's config):
        //   - mixamorig_LeftUpLeg: min signed dot between idle/run keys was
        //     -0.962 (near-antipodal REPRESENTATION for orientations only
        //     ~32 deg apart physically -- a hemisphere-flip data defect).
        //   - mixamorig_RightUpLeg: min signed dot was -0.215; worst-pair
        //     physical angle 162 deg -- anatomically absurd for a thigh
        //     between an idle stance and a running stride, pointing at a
        //     genuine ~180 deg twist about the bone's own axis (silhouette-
        //     disambiguation picked the wrong branch for this one bone during
        //     #267's retarget), not just a representation artifact.
        //   - Every OTHER shared rotation track already has a positive min
        //     signed dot (Hips 0.997, Spine 0.994, LeftLeg 0.656, ...) --
        //     these two UpLeg bones are the only violators.
        //
        // Two independent invariants pin what the fix must establish:
        //   (a) signed-dot continuity: every cross pair of (idle key, run
        //       key) on a shared bone must have a non-negative dot product
        //       (no antipodal-quaternion / hemisphere-flip data defect);
        //   (b) anatomical bound: on the UpLeg bones specifically, the worst
        //       cross-pair PHYSICAL angle (dot-sign-independent, via the
        //       existing QuaternionAngleDeg helper) must stay <= 90 deg --
        //       comfortably above every OTHER bone's own worst-pair angle
        //       once the twist is corrected (RightUpLeg drops from 162 deg to
        //       65 deg), while still an order of magnitude under the observed
        //       162 deg bug value, so this threshold cleanly separates
        //       "genuinely close" from "twisted" without demanding exact pose
        //       correctness (pose *quality* stays the deferred human feel
        //       judgment, #178/#173, ADR-0021 -- this pins data-level
        //       correctness only).
        // A third, weaker invariant (c) pins ordinary interpolation hygiene
        // within each clip on its own: consecutive keys in the SAME track
        // must already have a non-negative dot (no internal hemisphere hop),
        // which every OTHER assertion family above implicitly assumes holds.
        const double SignedDotFloor = 0.0;
        const double UpLegAngleThresholdDeg = 90.0;
        string[] upLegBones = { "mixamorig_LeftUpLeg", "mixamorig_RightUpLeg" };

        var idleAnim = lib.GetAnimation("idle");
        var runAnim = lib.GetAnimation("run");

        var idleRotationBones = new List<string>();
        for (int i = 0; i < idleAnim.GetTrackCount(); i++)
        {
            if (idleAnim.TrackGetType(i) != Animation.TrackType.Rotation3D) continue;
            var path = idleAnim.TrackGetPath(i);
            if (path.GetSubNameCount() == 0) continue;
            idleRotationBones.Add(path.GetSubName(0));
        }

        var sharedBones = new List<string>();
        for (int i = 0; i < runAnim.GetTrackCount(); i++)
        {
            if (runAnim.TrackGetType(i) != Animation.TrackType.Rotation3D) continue;
            var path = runAnim.TrackGetPath(i);
            if (path.GetSubNameCount() == 0) continue;
            var bone = path.GetSubName(0);
            if (idleRotationBones.Contains(bone)) sharedBones.Add(bone);
        }

        // Vacuous-pass guard: idle/run share plenty of skeletal rotation
        // tracks (20 in the pre-fix asset: 24 shared bone paths minus the 4
        // that are SCALE_3D on one side, per issue #275's table) -- a
        // near-empty overlap would mean this whole assertion family isn't
        // actually exercising anything.
        if (sharedBones.Count < 10)
        {
            Fail($"clip 'idle'/'run': only {sharedBones.Count} shared ROTATION_3D bone tracks found -- " +
                 "expected >= 10; the #275 blend-compatibility assertion family would be vacuous.");
            allPass = false;
        }

        foreach (var bone in sharedBones)
        {
            int idleTrack = FindRotationTrack(idleAnim, bone);
            int runTrack = FindRotationTrack(runAnim, bone);
            int idleKeyCount = idleAnim.TrackGetKeyCount(idleTrack);
            int runKeyCount = runAnim.TrackGetKeyCount(runTrack);
            if (idleKeyCount <= 0 || runKeyCount <= 0)
            {
                Fail($"clip 'idle'/'run': bone '{bone}' has a zero-key rotation track -- vacuous, not proof.");
                allPass = false;
                continue;
            }

            double minCrossDot = double.PositiveInfinity;
            double maxCrossAngleDeg = 0.0;
            for (int ik = 0; ik < idleKeyCount; ik++)
            {
                var idleKey = (Quaternion)idleAnim.TrackGetKeyValue(idleTrack, ik);
                for (int rk = 0; rk < runKeyCount; rk++)
                {
                    var runKey = (Quaternion)runAnim.TrackGetKeyValue(runTrack, rk);
                    double dot = idleKey.Normalized().Dot(runKey.Normalized());
                    if (dot < minCrossDot) minCrossDot = dot;
                    double angleDeg = QuaternionAngleDeg(idleKey, runKey);
                    if (angleDeg > maxCrossAngleDeg) maxCrossAngleDeg = angleDeg;
                }
            }

            bool isUpLeg = System.Array.IndexOf(upLegBones, bone) >= 0;
            GD.Print($"[locomotion-clip]   #275 '{bone}': min_cross_signed_dot={minCrossDot:F6} " +
                      $"max_cross_angle_deg={maxCrossAngleDeg:F6} is_upleg={isUpLeg}");

            if (minCrossDot < SignedDotFloor)
            {
                Fail($"clip 'idle'/'run': bone '{bone}' has a cross-clip key pair with signed dot " +
                     $"{minCrossDot:F6} (< {SignedDotFloor}) -- antipodal-quaternion / hemisphere-flip " +
                     "defect (issue #275): the idle<->run blend will transit the long way around the " +
                     "sphere at intermediate weights instead of staying in the idle/run pose corridor.");
                allPass = false;
            }

            if (isUpLeg && maxCrossAngleDeg > UpLegAngleThresholdDeg)
            {
                Fail($"clip 'idle'/'run': UpLeg bone '{bone}' has a cross-clip key pair {maxCrossAngleDeg:F6} " +
                     $"deg apart (> {UpLegAngleThresholdDeg} deg) -- anatomically implausible for a thigh " +
                     "between an idle stance and a running stride; points at an uncorrected twist about the " +
                     "bone's own axis (issue #275).");
                allPass = false;
            }
        }

        // Intra-track continuity (c): within EACH clip on its own, every
        // consecutive key pair on every rotation track must already share a
        // non-negative dot, or that clip's own key-to-key interpolation
        // (independent of any cross-clip blending) already hops hemispheres.
        foreach (var (clipName, anim) in new[] { ("idle", idleAnim), ("run", runAnim) })
        {
            for (int i = 0; i < anim.GetTrackCount(); i++)
            {
                if (anim.TrackGetType(i) != Animation.TrackType.Rotation3D) continue;
                int keyCount = anim.TrackGetKeyCount(i);
                if (keyCount < 2) continue;

                var prev = (Quaternion)anim.TrackGetKeyValue(i, 0);
                for (int k = 1; k < keyCount; k++)
                {
                    var cur = (Quaternion)anim.TrackGetKeyValue(i, k);
                    double dot = prev.Normalized().Dot(cur.Normalized());
                    if (dot < SignedDotFloor)
                    {
                        var path = anim.TrackGetPath(i);
                        string boneName = path.GetSubNameCount() > 0 ? path.GetSubName(0) : $"track[{i}]";
                        Fail($"clip '{clipName}': bone '{boneName}' has consecutive keys {k - 1}->{k} with " +
                             $"signed dot {dot:F6} (< {SignedDotFloor}) -- intra-track hemisphere hop " +
                             "(issue #275 continuity invariant).");
                        allPass = false;
                    }
                    prev = cur;
                }
            }
        }

        // --- Issue #287 assertion family: continuous-drive corridor sweep ---
        // #275 (family 4 above) proved data-level key compatibility but a
        // dedicated diagnostic probe found it insufficient: driving the LIVE
        // AnimationNodeBlendSpace1D mixer through a continuous 0->6 ramp (the
        // actual production shape, not a discrete key-pair comparison) still
        // produced out-of-corridor leg poses at INTERMEDIATE blend weights --
        // a mixer rest-anchored-accumulation degeneracy, not a data defect
        // (stored-sign changes were proven inert to blend output in #286).
        // `sharedBones`/`idleAnim`/`runAnim` are reused from family 4 above.
        string[] legChainCandidates =
        {
            "mixamorig_LeftUpLeg", "mixamorig_RightUpLeg",
            "mixamorig_LeftLeg", "mixamorig_RightLeg",
            "mixamorig_LeftFoot", "mixamorig_RightFoot",
            "mixamorig_LeftToeBase", "mixamorig_RightToeBase",
        };
        // mixamorig_LeftToeBase is NOT included: idle carries no ROTATION_3D
        // track for it at all (confirmed via a disposable [DEBUG-287] probe),
        // so there is no idle-side reference to sweep against -- it is simply
        // absent from `sharedBones`, the same intersection family 4 computes.
        var legChainBones = sharedBones.Where(b => legChainCandidates.Contains(b)).ToList();

        // Vacuous-pass guard: the #275 fact table names 2 UpLeg bones as the
        // confirmed violators and the mechanism section says "only leg bones
        // misbehave" across the whole chain -- a near-empty leg-chain set
        // would mean this sweep isn't actually exercising the bug.
        if (legChainBones.Count < 5)
        {
            Fail($"#287 corridor sweep: only {legChainBones.Count} shared leg-chain rotation tracks found -- " +
                 "expected >= 5; the sweep would be vacuous.");
            allPass = false;
        }
        else if (RunCorridorSweep(legChainBones, "Locomotion", _sweepTest, _sweepRef0, _sweepRef6))
        {
            // pass — printed inside RunCorridorSweep
        }
        else
        {
            allPass = false;
        }

        // --- Issue #285/#294: the Dribble BlendSpace1D(s) get the same sweep -
        // The dribble neutral is a partial-weight blend surface, so it is
        // exposed to the exact #287 rest-anchored-accumulation degeneracy the
        // Locomotion sweep above exists to catch — and it is exposed on a rig
        // whose two UpLeg rests BlendRestAnchor deliberately moved to idle's
        // frame-0 pose, i.e. a rest that belongs to a DIFFERENT clip family
        // (Kenney-retargeted) than these stock-Mixamo clips.
        //
        // This comment used to say the two endpoints "differ only by a
        // 20-degree forward lean on mixamorig_Spine", were therefore "a short
        // arc apart", and that the blend "SHOULD be trivially well-behaved".
        // All three statements are now false, and the third one inverted, so
        // the reasoning is restated rather than patched:
        //
        //   * the lean is 38 deg, not 20 (commit 0e3c519), and it is paired
        //     with a 0.12 m mixamorig_Hips POSITION crouch;
        //   * #298 then baked a REAL leg stride into dribblemove (run's leg
        //     motion transplanted as a world-frame delta), so the endpoints
        //     are no longer a short arc apart at all — the measured widest
        //     endpoint gap is ~88 deg, on mixamorig_LeftLeg;
        //   * consequently the old claim that "identical leg keys on both
        //     endpoints" make the corridor TIGHTEST on the legs is exactly
        //     backwards. The leg chain now carries the LARGEST endpoint gap,
        //     which means the corridor is at its most generous there and at
        //     its tightest (10 deg flat) on the bones that barely move.
        //
        // The sweep is therefore a far stronger test than when it was written:
        // it now exercises a genuinely wide partial-weight blend over the two
        // UpLeg bones whose rests BlendRestAnchor moved into a different clip
        // family, which is precisely the #287 degeneracy condition. It was
        // expected to be the assertion most likely to break under #298. It did
        // not (0/90 frames violated) — but it is now load-bearing rather than a
        // formality, so a future change here should not be assumed safe.
        //
        // Swept over ALL shared rotation bones, not just the leg chain, so the
        // spine chain carrying the lean and the near-static bones holding the
        // tightest corridor are both included rather than excluded.
        //
        // (#294) scenes/Player.tscn's single Dribble BlendSpace1D split into
        // DribbleLeft and DribbleRight, doubling the number of partial-weight
        // surfaces sharing that one re-anchored rest — so the sweep now runs
        // once PER POLARITY, against its own dedicated never-advanced rig trio
        // (InstantiateSweepRig's "first Advance() only primes" reproduction
        // requires a tree that has never ticked, so the two polarities cannot
        // share rigs). A green Right and an unmeasured Left is exactly the gap
        // #294 exists to close, so both runs are required, and both print
        // their own numbers so a reviewer can compare the mirrored endpoints
        // side by side.
        (string State, string IdleClip, string MoveClip,
            PlayerController SweepTest, PlayerController SweepRef0, PlayerController SweepRef6)[] dribblePolarities =
        {
            ("DribbleRight", "dribbleidleright", "dribblemoveright",
                _dribbleSweepTestRight, _dribbleSweepRef0Right, _dribbleSweepRef6Right),
            ("DribbleLeft", "dribbleidleleft", "dribblemoveleft",
                _dribbleSweepTestLeft, _dribbleSweepRef0Left, _dribbleSweepRef6Left),
        };
        foreach (var p in dribblePolarities)
        {
            // Deliberately NOT named idleAnim/sharedBones: those are family 4's
            // Locomotion idle/run locals, declared in the enclosing scope and
            // still read further down (see the "reused from family 4" note), so
            // reusing the names here is a compile error rather than a shadow.
            var dribbleIdleAnim = lib.GetAnimation(p.IdleClip);
            var dribbleMoveAnim = lib.GetAnimation(p.MoveClip);
            var dribbleSharedBones = SharedRotationBones(dribbleIdleAnim, dribbleMoveAnim);

            // Vacuous-pass guard: both clips carry the full 53-track stock
            // Mixamo skeleton, so a near-empty overlap means the extraction
            // regressed — checked independently per polarity, since a left-
            // side-only regression must not hide behind a healthy right side.
            if (dribbleSharedBones.Count < 20)
            {
                Fail($"#294 dribble corridor sweep [{p.State}]: only {dribbleSharedBones.Count} shared ROTATION_3D " +
                     $"bone tracks between '{p.IdleClip}'/'{p.MoveClip}' -- expected >= 20; the sweep would be vacuous.");
                allPass = false;
            }
            else if (RunCorridorSweep(dribbleSharedBones, p.State, p.SweepTest, p.SweepRef0, p.SweepRef6))
            {
                // pass — printed inside RunCorridorSweep, tagged with p.State
                // so the two polarities' results are individually visible.
            }
            else
            {
                allPass = false;
            }
        }

        // --- Issue #298: moving-dribble stride detection ---------------------
        // `dribble-corridor` above (the #285 RunCorridorSweep pass) cannot
        // detect the #298 defect: it asserts poses stay NEAR two near-
        // identical endpoints, and frozen legs satisfy that trivially (a
        // motionless pose is "near" a motionless reference by construction).
        // This family measures actual hips-relative fore/aft foot travel
        // instead, ported from the throwaway tools/_probe_dribble_legs.gd
        // probe that produced the reference numbers: run's legs travel
        // 0.6418 m peak-to-peak; locomotion/dribblemove's travel only
        // 0.0101 m — frozen in all but name.
        //
        // Three cases, run in a fixed order because the first is a
        // methodology gate for the other two:
        //   1. NON-VACUITY CONTROL — Locomotion/run at blend 6. If the
        //      measurement can't detect a REAL stride here, a red or green
        //      verdict on dribblemove below proves nothing about dribblemove
        //      and everything about a broken probe. Run once, shared by both
        //      polarities below — Locomotion has no hand-side split.
        //   2. THE #298 SUBJECT — Dribble{Left,Right}/dribblemove{left,right}
        //      at blend 6 (the actual moving endpoint of each polarity's
        //      BlendSpace1D).
        //   3. DISCRIMINATING CONTROL — Dribble{Left,Right}/dribbleidle
        //      {left,right} at blend 0 (the standing dribble stance), which
        //      must stay static — this is what would catch a future fix that
        //      bleeds the stride into the wrong BlendSpace1D endpoint instead
        //      of fixing dribblemove itself.
        //
        // (#294) Cases 2 and 3 now run once PER POLARITY against their own
        // dedicated rig — the #298 stride defect was originally measured and
        // fixed on the single pre-split Dribble state, and there is no reason
        // to assume the LEFT clip pair inherited the same fix just because it
        // shares a rebuild tool with the RIGHT pair proven here.
        bool strideControlOk = MeasureStride(_strideRun, "Locomotion", 6.0f, "run (control)",
            out double runPtpLeft, out double runPtpRight, out double runSplitMin, out double runSplitMax);
        if (!strideControlOk)
        {
            allPass = false;
        }
        else
        {
            bool strideControlPass = runPtpLeft >= StrideMinPeakToPeakM && runPtpRight >= StrideMinPeakToPeakM &&
                                      runSplitMin < 0.0 && runSplitMax > 0.0;
            if (!strideControlPass)
            {
                Fail($"#298 stride NON-VACUITY CONTROL failed on Locomotion/run (L_ptp={runPtpLeft:F4} " +
                     $"R_ptp={runPtpRight:F4} split=[{runSplitMin:F4},{runSplitMax:F4}], floor=" +
                     $"{StrideMinPeakToPeakM} m) — run is a KNOWN-GOOD real stride (measured 0.6418 m " +
                     "peak-to-peak by the #298 probe), so this means the MEASUREMENT ITSELF is broken, " +
                     "not that run stopped striding. Every #298 verdict below is therefore MEANINGLESS " +
                     "until this control passes — do not report a red or green dribblemove result as a " +
                     "confirmed finding while this control is red.");
                allPass = false;
            }

            // Both remaining cases still run and report, even if the control
            // above failed — the point is exactly to surface the numbers so a
            // reviewer can see whether dribblemove looks frozen in the same
            // log where the probe's own credibility is being questioned,
            // rather than silently skipping straight to an unexplained
            // overall FAIL. Run once per polarity so the two sets of numbers
            // sit side by side in the log for comparison.
            (string State, string MoveClip, string IdleClip,
                PlayerController MoveRig, PlayerController IdleRig)[] stridePolarities =
            {
                ("DribbleRight", "dribblemoveright", "dribbleidleright",
                    _strideDribbleMoveRight, _strideDribbleIdleRight),
                ("DribbleLeft", "dribblemoveleft", "dribbleidleleft",
                    _strideDribbleMoveLeft, _strideDribbleIdleLeft),
            };
            foreach (var p in stridePolarities)
            {
                bool strideMoveOk = MeasureStride(p.MoveRig, p.State, 6.0f, $"{p.MoveClip} (#298 subject)",
                    out double dmPtpLeft, out double dmPtpRight, out double dmSplitMin, out double dmSplitMax);
                if (!strideMoveOk)
                {
                    allPass = false;
                }
                else
                {
                    if (!(dmPtpLeft >= StrideMinPeakToPeakM && dmPtpRight >= StrideMinPeakToPeakM))
                    {
                        Fail($"#298: locomotion/{p.MoveClip}'s feet do not clear the peak-to-peak stride floor " +
                             $"(L_ptp={dmPtpLeft:F4} R_ptp={dmPtpRight:F4} m, floor={StrideMinPeakToPeakM} m) — " +
                             $"the moving-dribble BlendSpace1D endpoint's legs are frozen ({p.State}).");
                        allPass = false;
                    }
                    if (!(dmSplitMin < 0.0 && dmSplitMax > 0.0))
                    {
                        Fail($"#298: locomotion/{p.MoveClip}'s fore/aft foot split never changes sign " +
                             $"(split=[{dmSplitMin:F4},{dmSplitMax:F4}]) — the lead foot never alternates " +
                             $"({p.State}). Peak-to-peak alone is not sufficient here: a static " +
                             "one-foot-forward stance (measured: a constant 0.6598 m split that never " +
                             "changes sign) can clear a peak-to-peak floor with both feet sliding the same " +
                             "direction together, which is not a stride.");
                        allPass = false;
                    }
                }

                bool strideIdleOk = MeasureStride(p.IdleRig, p.State, 0.0f, $"{p.IdleClip} (discriminating control)",
                    out double diPtpLeft, out double diPtpRight, out double diSplitMin, out double diSplitMax);
                if (!strideIdleOk)
                {
                    allPass = false;
                }
                else if (!(diPtpLeft <= StrideStaticMaxPeakToPeakM && diPtpRight <= StrideStaticMaxPeakToPeakM))
                {
                    Fail($"#298: locomotion/{p.IdleClip} (the standing dribble stance) strides more than the " +
                         $"static ceiling allows (L_ptp={diPtpLeft:F4} R_ptp={diPtpRight:F4} m, ceiling=" +
                         $"{StrideStaticMaxPeakToPeakM} m, {p.State}) — a future #298 fix must land the stride " +
                         $"on {p.MoveClip}, not bleed it into {p.IdleClip} too (the #285a 'the real clip " +
                         "verbatim' contract this control protects).");
                    allPass = false;
                }
            }
        }

        Finish(allPass ? 0 : 1);
    }

    // #287: drives the three live sweep rigs' AnimationTrees through a
    // 90-frame/1.5s continuous 0->6 blend ramp at a fixed 1/60s dt (the
    // production shape a real start/stop-run transition takes) and asserts
    // every leg-chain bone's pose stays within a corridor around the two
    // phase-matched reference rigs (pinned at blend 0 / blend 6) at every
    // frame. `mixamorig_Hips` rides along as a non-leg CONTROL bone (per the
    // mechanism doc: arms/spine/Hips sit near rest and should stay well
    // inside the corridor) -- printed for evidence but never counted toward
    // a violation, so a Hips violation would flag a methodology bug, not
    // reprove the leg-bone defect.
    private const double CorridorMarginDeg = 10.0;
    private const int SweepFrameCount = 90;
    private const double SweepDt = 1.0 / 60.0;
    private const double SweepDurationSeconds = 1.5; // 90 * 1/60

    // `state` is the AnimationTree state-machine node whose BlendSpace1D is
    // under test ("Locomotion", or #294's "DribbleLeft"/"DribbleRight" — the
    // single Dribble state #285 added is now split by hand side). For
    // anything other than Locomotion the rigs must first be travelled there
    // and PROVEN to have arrived — sweeping the wrong state would silently
    // re-run the Locomotion sweep under a Dribble* label, the most plausible
    // vacuous pass here.
    private bool RunCorridorSweep(
        List<string> sweptBones,
        string state,
        PlayerController testRig, PlayerController ref0Rig, PlayerController ref6Rig)
    {
        string param = $"parameters/{state}/blend_position";
        var testTree = testRig.GetNodeOrNull<AnimationTree>("AnimationTree");
        var ref0Tree = ref0Rig.GetNodeOrNull<AnimationTree>("AnimationTree");
        var ref6Tree = ref6Rig.GetNodeOrNull<AnimationTree>("AnimationTree");
        var testSkel = FindSkeleton(testRig);
        var ref0Skel = FindSkeleton(ref0Rig);
        var ref6Skel = FindSkeleton(ref6Rig);

        if (testTree == null || ref0Tree == null || ref6Tree == null ||
            testSkel == null || ref0Skel == null || ref6Skel == null)
        {
            Fail($"#287 corridor sweep [{state}]: could not resolve AnimationTree/Skeleton3D on one or more sweep rigs.");
            return false;
        }

        // Prime: see InstantiateSweepRig's doc — this is genuinely the FIRST
        // Advance() call each tree has ever received (ProcessCallback was
        // flipped to Manual before any physics frame ran), so it reproduces
        // the confirmed "swallows dt, only enters Start->Locomotion" gotcha
        // safely rather than accidentally landing mid-ramp.
        testTree.Advance(0.0);
        ref0Tree.Advance(0.0);
        ref6Tree.Advance(0.0);

        var trees = new[] { testTree, ref0Tree, ref6Tree };
        if (state != "Locomotion")
        {
            // Start->Locomotion is the tree's only auto-advance edge, so every
            // rig primes into Locomotion; walk them across the one-hop
            // Locomotion->Dribble transition (#285 authored it with the default
            // xfade of 0, hence an immediate switch) and give the machine a few
            // manual Advances to actually process the travel.
            foreach (var t in trees)
            {
                var pb = t.Get("parameters/playback").As<AnimationNodeStateMachinePlayback>();
                if (pb == null)
                {
                    Fail($"#287 corridor sweep [{state}]: no AnimationNodeStateMachinePlayback on a sweep rig.");
                    return false;
                }
                pb.Travel(state);
            }
            for (int i = 0; i < 4; i++)
                foreach (var t in trees)
                    t.Advance(SweepDt);

            // Arrival guard — this is what makes the sweep non-vacuous.
            foreach (var t in trees)
            {
                string current = t.Get("parameters/playback").As<AnimationNodeStateMachinePlayback>().GetCurrentNode();
                if (current != state)
                {
                    Fail($"#287 corridor sweep [{state}]: a sweep rig is in state '{current}', not '{state}' — " +
                         "the sweep would silently measure the wrong BlendSpace1D and pass vacuously.");
                    return false;
                }
            }
        }

        // Pin the two reference rigs at the BlendSpace1D's endpoints (0 and 6,
        // matching both blend spaces' authored blend_point positions in
        // scenes/Player.tscn) for their entire run. Test starts the ramp at 0.
        testTree.Set(param, 0.0);
        ref0Tree.Set(param, 0.0);
        ref6Tree.Set(param, 6.0);

        int violatingFrames = 0;
        int hipsViolatingFrames = 0;
        // Endpoint-separation witness (#285): the corridor threshold is
        // refGap + margin, i.e. self-referential — if the two reference rigs
        // ever collapsed onto the SAME pose (blend parameter not reaching the
        // blend space, both rigs stuck on one clip, a mis-typed `param` path),
        // every gap would be ~0 and "no violations" would mean "nothing was
        // measured". Track the largest gap observed and require it to be
        // genuinely nonzero below.
        double maxRefGapDeg = 0.0;
        string maxRefGapBone = "";
        double worstExcessDeg = 0.0;
        string worstBone = "";
        int worstFrame = -1;
        double worstAngle0 = 0.0, worstAngle6 = 0.0, worstGap = 0.0;

        for (int frame = 1; frame <= SweepFrameCount; frame++)
        {
            double t = frame * SweepDt;
            double blend = 6.0 * System.Math.Min(t / SweepDurationSeconds, 1.0);
            testTree.Set(param, blend);

            testTree.Advance(SweepDt);
            ref0Tree.Advance(SweepDt);
            ref6Tree.Advance(SweepDt);

            bool frameViolated = false;
            foreach (var bone in sweptBones)
            {
                if (!TryCorridorCheck(testSkel, ref0Skel, ref6Skel, bone,
                        out double angle0, out double angle6, out double gap))
                    continue;

                if (gap > maxRefGapDeg)
                {
                    maxRefGapDeg = gap;
                    maxRefGapBone = bone;
                }

                double threshold = gap + CorridorMarginDeg;
                if (angle0 > threshold && angle6 > threshold)
                {
                    frameViolated = true;
                    double excess = System.Math.Min(angle0, angle6) - threshold;
                    if (excess > worstExcessDeg)
                    {
                        worstExcessDeg = excess;
                        worstBone = bone;
                        worstFrame = frame;
                        worstAngle0 = angle0;
                        worstAngle6 = angle6;
                        worstGap = gap;
                    }
                }
            }
            if (frameViolated) violatingFrames++;

            // Control bone: computed and counted for evidence only.
            if (TryCorridorCheck(testSkel, ref0Skel, ref6Skel, "mixamorig_Hips",
                    out double hipsAngle0, out double hipsAngle6, out double hipsGap) &&
                hipsAngle0 > hipsGap + CorridorMarginDeg && hipsAngle6 > hipsGap + CorridorMarginDeg)
            {
                hipsViolatingFrames++;
            }
        }

        GD.Print($"[locomotion-clip]   #287 corridor sweep [{state}]: {violatingFrames}/{SweepFrameCount} frames " +
                  $"violated ({sweptBones.Count} bones swept); mixamorig_Hips control violated " +
                  $"{hipsViolatingFrames}/{SweepFrameCount} frames; widest endpoint gap " +
                  $"{maxRefGapDeg:F1} deg on '{maxRefGapBone}'.");
        if (violatingFrames > 0)
        {
            GD.Print($"[locomotion-clip]   #287 worst [{state}]: '{worstBone}' @ frame {worstFrame} " +
                      $"(t={worstFrame * SweepDt:F3}s, blend={6.0 * System.Math.Min(worstFrame * SweepDt / SweepDurationSeconds, 1.0):F2}) " +
                      $"angle_vs_ref0={worstAngle0:F1} angle_vs_ref6={worstAngle6:F1} ref_gap={worstGap:F1} " +
                      $"excess={worstExcessDeg:F1} deg.");
        }

        // Non-vacuity: the two endpoint rigs must actually render DIFFERENT
        // poses, or the corridor (refGap + margin) measured nothing at all.
        // 5 deg floor: Locomotion's idle-vs-run legs are tens of degrees apart,
        // and each DribbleLeft/DribbleRight pair's endpoints are far enough
        // apart that the rebuild tool's own PROOF 5 measures 179.8 deg between
        // them, so both clear this by a wide margin — it only rules out the
        // degenerate ~0 case. Deliberately NOT quoting a spine-lean constant
        // here: LEAN_DEGREES (38) is dead on the live path, because with
        // USE_AUTHORED_MOVE_CLIP the lean is keyframed in Blender rather than
        // applied by the tool, so citing it would be a number this assertion
        // cannot actually back.
        const double MinEndpointGapDeg = 5.0;
        if (maxRefGapDeg < MinEndpointGapDeg)
        {
            Fail($"'{state}' BlendSpace1D corridor sweep: the two endpoint reference rigs never differed by more " +
                 $"than {maxRefGapDeg:F3} deg on any swept bone (< {MinEndpointGapDeg} deg) — the corridor " +
                 "threshold is (endpoint gap + margin), so a 'no violations' result here would be vacuous. " +
                 $"Most likely the '{param}' blend parameter is not reaching the blend space, or both rigs are " +
                 "playing the same clip.");
            return false;
        }

        if (violatingFrames > 0)
        {
            Fail($"'{state}' BlendSpace1D corridor sweep: {violatingFrames}/{SweepFrameCount} frames had " +
                 "a bone pose further from BOTH phase-matched endpoint reference rigs than " +
                 $"(reference gap + {CorridorMarginDeg} deg) during a continuous 0->6 ramp -- the human-visible " +
                 "start/stop twitch (issue #287, a mixer-accumulation degeneracy distinct from #275's " +
                 "data-level defect).");
            return false;
        }

        GD.Print($"[locomotion-clip]   #287 PASS [{state}] — no frame's pose exits the endpoint corridor across the continuous ramp.");
        return true;
    }

    // #298: 2.1 s at 1/60 -- exactly one full dribble-clip loop, long enough
    // for a real stride to complete at least one full fore/aft cycle on
    // either foot regardless of phase offset at the moment sampling starts.
    private const int StrideFrameCount = 126;
    private const double StrideDt = 1.0 / 60.0;

    // Measured by tools/_probe_dribble_legs.gd: run 0.6418 m, frozen
    // locomotion/dribblemove 0.0101 m. 0.15 m sits far above the ~0.01 m
    // noise floor a frozen/near-static pose produces and far below a real
    // stride, so it cleanly separates "moving" from "frozen" without being
    // sensitive to exactly how large a real stride is.
    private const double StrideMinPeakToPeakM = 0.15;

    // dribbleidle is the STANDING dribble stance and must stay static. 5x
    // the ~0.0101 m measured noise floor on the (currently frozen)
    // dribblemove endpoint -- generous enough that ordinary floating-point/
    // interpolation jitter on a genuinely static pose can never trip it, but
    // tight enough that a real stride bleeding into this endpoint would.
    private const double StrideStaticMaxPeakToPeakM = 0.05;

    // #298: measures actual fore/aft foot travel (as opposed to #287's
    // RunCorridorSweep, which only checks a pose stays NEAR a reference —
    // trivially true for a frozen pose near an equally-frozen reference).
    // `rig` must be one of the fresh #298 stride rigs (never advanced
    // before this call — see the field-level comment on
    // _strideDribbleMoveRight/Left et al. for why). `state`/`blendPos` select
    // the BlendSpace1D endpoint under test ("Locomotion"@6 for run,
    // "DribbleRight"/"DribbleLeft"@6 for dribblemoveright/left, "DribbleRight"/
    // "DribbleLeft"@0 for dribbleidleright/left — #294 split the single
    // pre-#285 Dribble state by hand side). Returns false (with a Fail
    // already logged) on any setup/arrival problem; the four `out`
    // measurements are only meaningful when this returns true.
    private bool MeasureStride(
        PlayerController rig, string state, float blendPos, string label,
        out double ptpLeft, out double ptpRight, out double splitMin, out double splitMax)
    {
        ptpLeft = ptpRight = splitMin = splitMax = 0.0;

        var tree = rig.GetNodeOrNull<AnimationTree>("AnimationTree");
        var skel = FindSkeleton(rig);
        if (tree == null || skel == null)
        {
            Fail($"#298 stride sweep [{label}]: could not resolve AnimationTree/Skeleton3D on the rig.");
            return false;
        }

        string param = $"parameters/{state}/blend_position";
        tree.Set(param, blendPos);

        var pb = tree.Get("parameters/playback").As<AnimationNodeStateMachinePlayback>();
        if (pb == null)
        {
            Fail($"#298 stride sweep [{label}]: no AnimationNodeStateMachinePlayback on the rig.");
            return false;
        }
        pb.Travel(state);

        // Prime: InstantiateSweepRig flips CallbackModeProcess to Manual
        // before this rig's tree has processed even one physics frame, so
        // this genuinely IS the tree's first-ever Advance() call — it only
        // primes playback at t=0 and swallows dt (the same confirmed gotcha
        // RunCorridorSweep's own prime call above guards against).
        tree.Advance(0.0);

        // RunCorridorSweep's own Dribble-state arrival needs only 4 pumped
        // advances after Travel(); this cap is a generous multiple of that so
        // a genuine non-arrival trips it rather than an ordinary timing
        // fluke.
        const int ArrivalAttemptCap = 30;
        int attempts = 0;
        string current = pb.GetCurrentNode();
        while (current != state && attempts < ArrivalAttemptCap)
        {
            tree.Advance(StrideDt);
            attempts++;
            current = pb.GetCurrentNode();
        }
        if (current != state)
        {
            Fail($"#298 stride sweep [{label}]: rig never arrived in state '{state}' (stuck in '{current}') " +
                 $"after {ArrivalAttemptCap} pumped advances — the sweep would silently measure the wrong " +
                 "BlendSpace1D and pass vacuously.");
            return false;
        }
        tree.Set(param, blendPos); // defensive re-pin post-transition, mirrors the probe's own re-pin

        // Facing axis from the RAW, un-anchored Y Bot reference — NOT from
        // `rig`'s own skeleton. scenes/Player.tscn's BlendRestAnchor node
        // mutates two bone rests (mixamorig_Left/RightUpLeg) on every
        // Player.tscn instance, including this rig, so `rig`'s own rest is a
        // moving target for a pure rest-geometry question; _rawYBotSkeleton
        // is a bare res://assets/Y Bot.fbx instance with no BlendRestAnchor
        // of its own and is the stable ground truth (same reasoning as the
        // class-level comment on _rawYBotSkeleton, and matches
        // tools/rebuild_dribble_clips.gd:203-223 exactly: toe-minus-foot,
        // flattened to the XZ plane, normalized).
        int rawFoot = _rawYBotSkeleton.FindBone("mixamorig_LeftFoot");
        int rawToe = _rawYBotSkeleton.FindBone("mixamorig_LeftToeBase");
        if (rawFoot < 0 || rawToe < 0)
        {
            Fail($"#298 stride sweep [{label}]: could not find mixamorig_LeftFoot/LeftToeBase on the raw " +
                 "Y Bot reference to derive a facing axis.");
            return false;
        }
        Vector3 forward = _rawYBotSkeleton.GetBoneGlobalRest(rawToe).Origin -
                           _rawYBotSkeleton.GetBoneGlobalRest(rawFoot).Origin;
        forward.Y = 0.0f;
        if (forward.Length() < 0.001f)
        {
            Fail($"#298 stride sweep [{label}]: LeftFoot->LeftToeBase rest vector is vertical " +
                 "(length < 0.001) — cannot derive a facing axis.");
            return false;
        }
        Vector3 facing = forward.Normalized();

        int hipsIdx = skel.FindBone("mixamorig_Hips");
        int leftToeIdx = skel.FindBone("mixamorig_LeftToeBase");
        int rightToeIdx = skel.FindBone("mixamorig_RightToeBase");
        if (hipsIdx < 0 || leftToeIdx < 0 || rightToeIdx < 0)
        {
            Fail($"#298 stride sweep [{label}]: missing mixamorig_Hips/LeftToeBase/RightToeBase on the " +
                 "rig's own skeleton.");
            return false;
        }

        double leftMin = double.PositiveInfinity, leftMax = double.NegativeInfinity;
        double rightMin = double.PositiveInfinity, rightMax = double.NegativeInfinity;
        double diffMin = double.PositiveInfinity, diffMax = double.NegativeInfinity;

        for (int f = 0; f < StrideFrameCount; f++)
        {
            tree.Advance(StrideDt);

            Vector3 hipsPos = skel.GetBoneGlobalPose(hipsIdx).Origin;
            // Relative to hips deliberately: this removes root translation,
            // so it measures STRIDE (the leg swinging relative to the body)
            // rather than TRAVEL (the whole rig moving across the floor).
            double projLeft = (skel.GetBoneGlobalPose(leftToeIdx).Origin - hipsPos).Dot(facing);
            double projRight = (skel.GetBoneGlobalPose(rightToeIdx).Origin - hipsPos).Dot(facing);

            leftMin = System.Math.Min(leftMin, projLeft);
            leftMax = System.Math.Max(leftMax, projLeft);
            rightMin = System.Math.Min(rightMin, projRight);
            rightMax = System.Math.Max(rightMax, projRight);

            double diff = projLeft - projRight;
            diffMin = System.Math.Min(diffMin, diff);
            diffMax = System.Math.Max(diffMax, diff);
        }

        ptpLeft = leftMax - leftMin;
        ptpRight = rightMax - rightMin;
        splitMin = diffMin;
        splitMax = diffMax;

        // Evidence line printed unconditionally -- pass or fail -- so a
        // reviewer can see the raw numbers even when every threshold above
        // happens to clear.
        GD.Print($"[locomotion-clip] #298 {label}: L_ptp={ptpLeft:F4} R_ptp={ptpRight:F4} " +
                  $"split=[{splitMin:F4},{splitMax:F4}]");
        return true;
    }

    // Shared per-bone/per-frame corridor math: returns false (skip, don't
    // fail) if the bone doesn't resolve on all three skeletons — every bone
    // this is called with is pre-filtered against `sharedBones` by the
    // caller, so a miss here would indicate a rig-instantiation bug, not a
    // real data gap; skipping (not failing) keeps this helper's contract
    // narrow (pose math only) while RunCorridorSweep's own resolution guard
    // above already covers "rig failed to instantiate at all".
    private static bool TryCorridorCheck(
        Skeleton3D testSkel, Skeleton3D ref0Skel, Skeleton3D ref6Skel, string bone,
        out double angleVsRef0, out double angleVsRef6, out double refGap)
    {
        angleVsRef0 = angleVsRef6 = refGap = 0.0;

        int testIdx = testSkel.FindBone(bone);
        int ref0Idx = ref0Skel.FindBone(bone);
        int ref6Idx = ref6Skel.FindBone(bone);
        if (testIdx < 0 || ref0Idx < 0 || ref6Idx < 0) return false;

        Quaternion testPose = testSkel.GetBonePoseRotation(testIdx);
        Quaternion ref0Pose = ref0Skel.GetBonePoseRotation(ref0Idx);
        Quaternion ref6Pose = ref6Skel.GetBonePoseRotation(ref6Idx);

        angleVsRef0 = QuaternionAngleDeg(testPose, ref0Pose);
        angleVsRef6 = QuaternionAngleDeg(testPose, ref6Pose);
        refGap = QuaternionAngleDeg(ref0Pose, ref6Pose);
        return true;
    }

    // Bones carrying a ROTATION_3D track in BOTH clips — the set a BlendSpace1D
    // between them actually mixes. (#285; the idle/run family above computes the
    // same intersection inline, kept as-is to hold that shipped code still.)
    private static List<string> SharedRotationBones(Animation a, Animation b)
    {
        var inA = new List<string>();
        for (int i = 0; i < a.GetTrackCount(); i++)
        {
            if (a.TrackGetType(i) != Animation.TrackType.Rotation3D) continue;
            var path = a.TrackGetPath(i);
            if (path.GetSubNameCount() == 0) continue;
            inA.Add(path.GetSubName(0));
        }

        var shared = new List<string>();
        for (int i = 0; i < b.GetTrackCount(); i++)
        {
            if (b.TrackGetType(i) != Animation.TrackType.Rotation3D) continue;
            var path = b.TrackGetPath(i);
            if (path.GetSubNameCount() == 0) continue;
            var bone = path.GetSubName(0);
            if (inA.Contains(bone)) shared.Add(bone);
        }
        return shared;
    }

    private static int FindRotationTrack(Animation anim, string boneName)
    {
        for (int i = 0; i < anim.GetTrackCount(); i++)
        {
            if (anim.TrackGetType(i) != Animation.TrackType.Rotation3D) continue;
            var path = anim.TrackGetPath(i);
            if (path.GetSubNameCount() == 0) continue;
            if (path.GetSubName(0) == boneName) return i;
        }
        return -1;
    }

    // Shortest-arc angle between two rotations, in degrees. Mirrors the
    // diagnosis probe's quat_angle_deg helper so the harness reproduces the
    // exact numbers already used to confirm the root cause.
    private static double QuaternionAngleDeg(Quaternion a, Quaternion b)
    {
        double dot = Mathf.Clamp(Mathf.Abs(a.Normalized().Dot(b.Normalized())), -1.0f, 1.0f);
        return Mathf.RadToDeg(2.0 * Mathf.Acos((float)dot));
    }

    private static Skeleton3D FindSkeleton(Node root)
    {
        if (root is Skeleton3D s) return s;
        var matches = root.FindChildren("*", nameof(Skeleton3D), recursive: true, owned: false);
        return matches.Count > 0 ? matches[0] as Skeleton3D : null;
    }

    private static void Fail(string message) => GD.PrintErr($"[locomotion-clip] FAIL: {message}");

    private void Finish(int code)
    {
        _finished = true;
        GD.Print($"[locomotion-clip] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
