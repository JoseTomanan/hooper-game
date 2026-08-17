using System;
using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #310 (ADR-0016): proves the THREE SPIN
// ANIMATION STATES (SpinStartup / SpinActive / SpinRecovery) wired into
// scenes/Player.tscn are real — entered end-to-end by a real Spin, bound to the
// right clips, cut to the right windows, carrying the shoulder twist that IS
// this move's read, and — the whole point of the issue — NOT rotating the root.
//
// Before #310 "spin" fell through MoveAnimResolver.ResolveStateName's default
// case onto the shared generic Startup/Active/Recovery states, which per #296
// render a looping IDLE for Startup/Recovery (pixel-identical, so an opponent
// cannot tell "committing" from "in the punish window") and a looping SPRINT
// for Active.
//
//   godot --headless --path . res://tests/integration/SpinAnimTest.tscn -- --harness-scenario=spin-phases
//   …=spin-no-placeholder-leak | spin-segment-lengths | spin-edges
//   …=spin-stays-unsuffixed | spin-startup-differs-from-recovery
//   …=spin-clip-drives-the-rig | spin-clip-does-not-rotate-root
//   …=spin-shoulder-twist-reverses | control-spin-startup-twist-does-not-reverse
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//
// ── moveId IS "spin" ───────────────────────────────────────────────────────
// Spin.cs constructs with `id: "spin"`, so the ClippedMovePrefixes key and the
// moveId coincide (unlike jab step's "jab").
//
// ── TRAP A is the reason this file exists in the shape it does ─────────────
// Player heading is SERVER-AUTHORITATIVE (ADR-0010): SpinHeadingMath drives the
// ~180 deg arc as gameplay state, integrated into Move(). A clip that ALSO
// rotated the root would double-rotate on the authoritative roles and fight
// reconciliation on the client's remote copy, whose broadcast heading is ~1 RTT
// stale. That is a defect that appears ONLY under network conditions, so no
// visual report would find it — hence `spin-clip-does-not-rotate-root`.
//
// Note WHY that scenario can be measured here at all: Skeleton3D bone poses are
// in SKELETON-LOCAL space, so the PlayerController node's own authoritative
// rotation does not enter the reading. What this file measures is purely the
// CLIP's contribution, which is exactly the quantity under contract.
//
// ── TRAP B: spin is UNHANDED, and NOT because it lacks a direction ─────────
// MoveAnimResolver.HandedMoves' own docstring: spin swaps the ball hand on the
// LAST Active tick (PlayerController's Spin branch, FrameInPhase ==
// ActiveFrames - 1), not at Active-entry, so OriginHand's phase-conditioned
// formula would be right for Startup and INVERTED for five of Active's six
// ticks. `spin-stays-unsuffixed` is the standing regression guard against a
// future author "fixing" that by adding spin to HandedMoves.
//
// ── Setup mirrors HesitationAnimTest.cs's own pattern ──────────────────────
// A live dribble, then BeginMoveForHarness — downstream of every gate
// PlayerController.BeginCommittedMove imposes (including the #193 dead-Held
// rule, which SpinTest's own `dead-dribble-gate` scenario owns). This harness
// is COSMETIC-ONLY: it never observes or feeds BallState, HasDribbled,
// SpinHeadingMath's arc, or any gameplay constant. SpinTest.cs owns that half;
// this file owns only the display layer.
//
// ── Why ActiveAnimNodeForHarness, not the resolver's return value (#257) ───
// Travel() to a missing/misnamed state only LOGS; it never throws. Only the
// live AnimationNodeStateMachinePlayback proves wiring.
//
// ── The phase-label-lag trap (#316/#340) ──────────────────────────────────
// Even with CallbackModeProcess=Physics, the first tick GetCurrentNode() names
// a phase still holds the PREVIOUS phase's pose. Every geometric measurement
// below is latched starting from the SECOND observed tick of its phase, and
// each gate asserts it got enough usable samples before trusting a reading.
//
// Spin's phases are 8/6/10 ticks, the longest Startup in the dribble family, so
// unlike hesitation (4-tick Startup, which yields exactly ONE clean interval)
// every phase here has comfortable headroom and every premise below requires
// >= 3 raw observed ticks.
public partial class SpinAnimTest : Node
{
    private const double TimeoutSeconds = 15.0;
    private const int ArmFrames = 2;      // ticks for TryAssignTipoffHolder to run
    private const int ActFrames = 3;      // ticks after tipoff before Begin
    // startup(8)+active(6)+recovery(10)=24 ticks, generous slack.
    private const int ObserveFrames = 48;

    private static readonly Vector3 ActorSpot = new(0f, 0f, 2f);
    private static readonly Vector3 FarSpot = new(12f, 0f, 12f); // out of PickupRadius
    private static readonly Vector3 RimCenter = new(0f, 3.05f, 0f); // BallController.DefaultRimCenter

    // Bones for the Startup-vs-Recovery pose comparison and the
    // departure-from-rest readings — the whole-body set every dribble-family
    // script in this batch uses, since a spin's read (torso twist + hip drop +
    // pivot/trail footwork + tucked arms) touches all of them.
    private static readonly string[] MeasuredBones =
    {
        "mixamorig_LeftArm", "mixamorig_RightArm",
        "mixamorig_LeftForeArm", "mixamorig_RightForeArm",
        "mixamorig_Spine",
        "mixamorig_LeftUpLeg", "mixamorig_RightUpLeg",
        "mixamorig_LeftLeg", "mixamorig_RightLeg",
        "mixamorig_LeftFoot", "mixamorig_RightFoot",
    };

    // The two spans the twist and root-rotation scenarios measure. The HIP span
    // must not yaw at all (trap A); the SHOULDER span is where the entire turn
    // is allowed to live. They are read with the SAME code on the SAME ticks,
    // which is what makes "the hips did not turn" a discriminating claim rather
    // than a reading that could be zero because the measurement died.
    private const string HipLeftBone = "mixamorig_LeftUpLeg";
    private const string HipRightBone = "mixamorig_RightUpLeg";
    private const string ArmLeftBone = "mixamorig_LeftArm";
    private const string ArmRightBone = "mixamorig_RightArm";

    // ── Thresholds ──────────────────────────────────────────────────────────
    // EVERY floor below is set from a value MEASURED BY THIS HARNESS ON THE
    // LIVE RIG (StepBackAnimTest's documented discipline — the Blender,
    // resource and live-rig spaces disagree on absolute readings, so every gate
    // here is either a RELATIVE claim off a live measurement or re-measured
    // fresh in this frame). Each constant records its own live-rig measurement
    // below; those are DATED READINGS (2026-08-17, Godot 4.7.1), not invariants
    // — re-measure rather than trusting them if a clip is re-authored (#335).

    // #296's legibility floor, live-rig. Blender-side measured 55.579 deg and
    // resource-side 46.2 deg on the same pair; this harness reads a smaller
    // quantity for the reason StartupVsRecoveryMinDeg documents in
    // HesitationAnimTest (the last OBSERVABLE Startup tick lands short of the
    // authored Startup end). MEASURED on the live rig: worst bone delta
    // 28.37 deg (mixamorig_RightArm), i.e. 1.9x this floor.
    private const float StartupVsRecoveryMinDeg = 15.0f;

    // The "clip physically drives the rig" floor — README's verification floor
    // / #281's mutation lesson: max-departure-from-rest and
    // max-change-across-the-arc both pass on a deliberately-unbound clip; only
    // the FINAL-tick reading separates a bound clip from a collapsed one.
    // MEASURED on the live rig: 177.04 deg off rest on the last Recovery tick.
    // The floor stays low (30) on purpose — this gate separates "bound" from
    // "collapsed to rest", and a tight floor near 177 would instead redden on
    // any legitimate re-author of the Recovery pose.
    private const float DrivesRigMinDeg = 30.0f;

    // TRAP A's live gate, in DEGREES: the largest yaw excursion of the HIP SPAN
    // (in SKELETON-LOCAL space, so the node's authoritative heading does not
    // enter it) across every usable observed tick of the move.
    //
    // Zero by construction — author_spin.py pins the Hips basis and keys it
    // every frame, and rebuild_spin_clips.gd's G4 re-proves the exported track
    // is constant to 0.0000 deg — so this is float/skinning noise headroom, not
    // a budget. Deliberately far below the SHOULDER span's own excursion over
    // the same ticks (see TwistExcursionMinDeg): the gap between the two IS the
    // proof, since both come out of the same measurement.
    //
    // MEASURED on the live rig: 0.000 deg — the pin survives the whole
    // Blender -> FBX -> import -> slice -> AnimationTree chain exactly. The
    // ceiling is therefore set at 1.0, not 3.0: at 3.0 a real regression that
    // leaked a couple of degrees of root yaw into the clip would still pass.
    private const float HipYawExcursionMaxDeg = 1.0f;

    // The PREMISE for the gate above, and the reason it is not vacuous: over
    // the SAME observed ticks, measured by the SAME code, the SHOULDER span
    // must yaw a LOT. If the bone lookup died, the clip went inert, or the
    // states stopped being entered, this collapses toward zero and the scenario
    // reddens — instead of the hip reading passing at a confident 0.00 deg for
    // the wrong reason. Authored swing is +30 -> -30 deg (60 deg of travel);
    // this floor sits well under what survives the phase-lag tick drop.
    // MEASURED on the live rig: 38.85 deg of shoulder-span yaw excursion over
    // the same 20 usable ticks that read 0.000 for the hips — 1.55x this floor.
    private const float TwistExcursionMinDeg = 25.0f;

    // `spin-shoulder-twist-reverses`: the signed shoulder-vs-hip yaw must be at
    // least this positive on Startup's last usable tick and at least this
    // negative on Active's last usable tick. Resource-side reads +30.44 /
    // -30.40; the live rig reads less at both ends because the last OBSERVABLE
    // tick of each phase falls short of the authored boundary pose. MEASURED on
    // the live rig: +16.55 deg at Startup's end and -22.55 deg at Active's end,
    // so the tighter of the two sits 1.38x above this floor. That is the
    // smallest margin in this file — it is the cost of measuring the reversal on
    // observable ticks rather than authored keyframes, and raising the floor
    // toward 16 would make the gate fragile against a one-tick retune (#316).
    private const float TwistReversalMinDeg = 12.0f;

    private static readonly string[] KnownScenarios =
    {
        "spin-phases",
        "spin-no-placeholder-leak",
        "spin-segment-lengths",
        "spin-edges",
        "spin-stays-unsuffixed",
        "spin-startup-differs-from-recovery",
        "spin-clip-drives-the-rig",
        "spin-clip-does-not-rotate-root",
        "spin-shoulder-twist-reverses",
        "control-spin-startup-twist-does-not-reverse",
    };

    // Scenarios that need no live tree — pure resource/scene inspection.
    private static readonly string[] StaticScenarios =
    {
        "spin-no-placeholder-leak",
        "spin-segment-lengths",
        "spin-edges",
        "spin-stays-unsuffixed",
    };

    private string _scenario = "spin-phases";

    private BallController _ball;
    private PlayerController _actor; // peer "1" — the tipoff holder (ADR-0007)
    private PlayerController _other; // peer "2"

    private int _frame;
    private double _elapsed;
    private bool _finished;

    private enum Step { AwaitTipoff, AwaitDribble, Act, Observe }
    private Step _step = Step.AwaitTipoff;
    private int _stepDeadlineFrame;

    private bool _sawStartup;
    private bool _sawActive;
    private bool _sawRecovery;
    private bool _sawGenericPlaceholder;
    private bool _sawSuffixedState;

    // Per-phase observed-tick counts (RAW, including the lagged first tick —
    // #316/#340). "Usable" ticks for a geometric measurement are this minus 1.
    private int _startupTicks;
    private int _activeTicks;
    private int _recoveryTicks;

    // Startup-vs-Recovery pose comparison (#296) — latched on each phase's LAST
    // observed tick (overwritten every tick, so it ends up holding the last).
    private Quaternion[] _poseAtLastStartupTick;
    private Quaternion[] _poseAtLastRecoveryTick;

    // Departure from rest on the LAST tick of the WHOLE MOVE, i.e. Recovery's
    // own last observed tick.
    private float _departureFromRestAtLastRecoveryTick = float.NaN;

    // ── Yaw accumulators (trap A + the twist) ───────────────────────────────
    // Both spans are read with the SAME helper on the SAME ticks. `_hipYaw*`
    // and `_shoulderYaw*` track the excursion relative to the FIRST usable
    // observed tick of the move; `_twistAt*` latch the SIGNED shoulder-vs-hip
    // twist at the two phase ends the reversal claim is made across.
    private float _hipYawRef = float.NaN;
    private float _shoulderYawRef = float.NaN;
    private float _hipYawExcursion;
    private float _shoulderYawExcursion;
    private int _yawSamples;

    private float _twistAtLastStartupTick = float.NaN;
    private float _twistAtLastActiveTick = float.NaN;
    private float _twistStartupMin = float.NaN;
    private int _twistStartupSamples;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "spin-phases");
        GD.Print($"[spin-anim] scenario={_scenario} booting headless…");

        if (!KnownScenarios.Contains(_scenario))
        {
            Fail($"unknown scenario '{_scenario}'.");
            Finish();
            return;
        }

        if (StaticScenarios.Contains(_scenario))
        {
            RunStaticCheck();
            return;
        }

        var scene = GD.Load<PackedScene>("res://scenes/Player.tscn");
        _actor = scene.Instantiate<PlayerController>();
        _actor.Name = "1";
        _other = scene.Instantiate<PlayerController>();
        _other.Name = "2";

        // Physics-callback lockstep so GetCurrentNode() reflects the same-tick
        // Travel() (README trap 6).
        foreach (var p in new[] { _actor, _other })
            p.GetNode<AnimationTree>("AnimationTree").CallbackModeProcess =
                AnimationMixer.AnimationCallbackModeProcess.Physics;

        var players = new Node3D { Name = "Players" };
        players.AddChild(_actor);
        players.AddChild(_other);

        _ball = new BallController { Name = "Ball", Players = players };

        AddChild(players); // matches scenes/Main.tscn: Players before Ball
        AddChild(_ball);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        _frame++;

        switch (_step)
        {
            case Step.AwaitTipoff:
                if (_frame < ArmFrames) break;
                if (_ball.StateMachine.HolderPeerId != 1)
                {
                    Fail($"{_scenario}: tipoff did not assign holder 1 (got {_ball.StateMachine.HolderPeerId}).");
                    Finish();
                    return;
                }
                _actor.GlobalPosition = ActorSpot;
                _other.GlobalPosition = FarSpot;
                _actor.SetHeadingForHarness(
                    Mathf.Atan2(RimCenter.X - ActorSpot.X, RimCenter.Z - ActorSpot.Z));
                _ball.TryStartDribble(1);
                _step = Step.AwaitDribble;
                _stepDeadlineFrame = _frame + ActFrames;
                break;

            case Step.AwaitDribble:
                if (_frame < _stepDeadlineFrame) break;
                if (_ball.State != BallState.Dribbling)
                {
                    Fail($"{_scenario}: TryStartDribble(1) did not reach BallState.Dribbling by frame " +
                         $"{_frame} (got {_ball.State}).");
                    Finish();
                    return;
                }
                _step = Step.Act;
                _stepDeadlineFrame = _frame + ActFrames;
                break;

            case Step.Act:
                if (_frame < _stepDeadlineFrame) break;
                // spinDirection is +1 arbitrarily: this clip is UNHANDED and
                // carries no direction, so the DISPLAY is identical either way.
                // The direction only drives SpinHeadingMath's authoritative
                // heading arc, which is SpinTest.cs's `rotation` scenario, not
                // this file's.
                if (!_actor.BeginMoveForHarness(new Spin(spinDirection: 1f)))
                {
                    Fail($"{_scenario}: BeginMoveForHarness(new Spin(1)) returned false — the actor's " +
                         $"machine was not Inactive, or a begin gate rejected it. Ball state = {_ball?.State}.");
                    Finish();
                    return;
                }
                _step = Step.Observe;
                _stepDeadlineFrame = _frame + ObserveFrames;
                break;

            case Step.Observe:
                Observe();
                if (_frame >= _stepDeadlineFrame) RenderVerdict();
                break;
        }

        if (!_finished && _elapsed > TimeoutSeconds)
        {
            Fail($"timed out at frame {_frame}, scenario={_scenario}, step={_step}, " +
                 $"lastAnimNode={_actor?.ActiveAnimNodeForHarness}, sawStartup={_sawStartup}, " +
                 $"sawActive={_sawActive}, sawRecovery={_sawRecovery}.");
            Finish();
        }
    }

    private void Observe()
    {
        string node = _actor.ActiveAnimNodeForHarness;

        if (!_sawStartup && node == "SpinStartup") _sawStartup = true;
        if (_sawStartup && !_sawActive && node == "SpinActive") _sawActive = true;
        if (_sawActive && !_sawRecovery && node == "SpinRecovery") _sawRecovery = true;
        if (node == "Startup" || node == "Active" || node == "Recovery") _sawGenericPlaceholder = true;
        // A live tripwire for the HandedMoves edit `spin-stays-unsuffixed`
        // guards statically: if a future author added spin to HandedMoves, the
        // resolver would Travel() to "SpinActiveLeft" — which only LOGS (#257)
        // — but if they ALSO added the states, this would catch it at runtime.
        if (node.StartsWith("Spin") && (node.EndsWith("Left") || node.EndsWith("Right")))
            _sawSuffixedState = true;

        var skel = FindSkeleton(_actor);
        if (skel == null) return;

        bool inMove = node is "SpinStartup" or "SpinActive" or "SpinRecovery";
        if (!inMove) return;

        if (node == "SpinStartup")
        {
            _startupTicks++;
            _poseAtLastStartupTick = SampleMeasuredBones(skel);
            if (_startupTicks >= 2)
            {
                AccumulateYaw(skel);
                float t = TwistDeg(skel);
                if (!float.IsNaN(t))
                {
                    _twistAtLastStartupTick = t; // overwritten -> ends up the LAST
                    if (float.IsNaN(_twistStartupMin) || t < _twistStartupMin) _twistStartupMin = t;
                    _twistStartupSamples++;
                }
            }
        }
        else if (node == "SpinActive")
        {
            _activeTicks++;
            if (_activeTicks >= 2)
            {
                AccumulateYaw(skel);
                float t = TwistDeg(skel);
                if (!float.IsNaN(t)) _twistAtLastActiveTick = t; // overwritten -> LAST
            }
        }
        else // SpinRecovery
        {
            _recoveryTicks++;
            _poseAtLastRecoveryTick = SampleMeasuredBones(skel);
            if (_recoveryTicks >= 2) AccumulateYaw(skel);
            // Overwritten every Recovery tick — ends up holding the LAST one,
            // which is what "clip-drives-the-rig" needs (README's verification
            // floor: max-across-the-arc passes vacuously on a clip that
            // collapsed to rest a tick after entry).
            _departureFromRestAtLastRecoveryTick = DepartureFromRestDeg(skel);
        }
    }

    // Folds one observed tick into BOTH span-yaw excursions. Deliberately ONE
    // method feeding both: `spin-clip-does-not-rotate-root` compares the hip
    // reading against the shoulder reading, and that comparison is only
    // meaningful because the two come from the same code on the same ticks.
    private void AccumulateYaw(Skeleton3D skel)
    {
        float hip = SpanYawDeg(skel, HipLeftBone, HipRightBone);
        float sho = SpanYawDeg(skel, ArmLeftBone, ArmRightBone);
        if (float.IsNaN(hip) || float.IsNaN(sho)) return;

        if (float.IsNaN(_hipYawRef))
        {
            _hipYawRef = hip;
            _shoulderYawRef = sho;
        }
        _hipYawExcursion = Math.Max(_hipYawExcursion, Math.Abs(Mathf.AngleDifference(
            Mathf.DegToRad(_hipYawRef), Mathf.DegToRad(hip)) * (180f / Mathf.Pi)));
        _shoulderYawExcursion = Math.Max(_shoulderYawExcursion, Math.Abs(Mathf.AngleDifference(
            Mathf.DegToRad(_shoulderYawRef), Mathf.DegToRad(sho)) * (180f / Mathf.Pi)));
        _yawSamples++;
    }

    private void RenderVerdict()
    {
        GD.Print($"[spin-anim]   observed ticks: startup={_startupTicks} " +
                 $"active={_activeTicks} recovery={_recoveryTicks} yawSamples={_yawSamples}");
        switch (_scenario)
        {
            case "spin-phases":                                  VerdictPhases(); break;
            case "spin-startup-differs-from-recovery":           VerdictStartupDiffersFromRecovery(); break;
            case "spin-clip-drives-the-rig":                     VerdictClipDrivesTheRig(); break;
            case "spin-clip-does-not-rotate-root":               VerdictClipDoesNotRotateRoot(); break;
            case "spin-shoulder-twist-reverses":                 VerdictTwistReverses(); break;
            case "control-spin-startup-twist-does-not-reverse":  VerdictStartupTwistDoesNotReverse(); break;
        }
    }

    // ── Scenario: spin-phases (positive) ──────────────────────────────────
    private void VerdictPhases()
    {
        bool pass = _sawStartup && _sawActive && _sawRecovery && !_sawSuffixedState;
        if (pass)
            GD.Print("[spin-anim] PASS spin-phases — the tree was observed on \"SpinStartup\", then " +
                     "\"SpinActive\", then \"SpinRecovery\", in that order, and never on a hand-suffixed variant.");
        else
            Fail($"spin-phases: expected SpinStartup -> SpinActive -> SpinRecovery, in order; got " +
                 $"sawStartup={_sawStartup}, sawActive={_sawActive}, sawRecovery={_sawRecovery}, " +
                 $"sawSuffixedState={_sawSuffixedState} (must be false — spin is UNHANDED), " +
                 $"sawGenericPlaceholder={_sawGenericPlaceholder}, lastAnimNode={_actor.ActiveAnimNodeForHarness}.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: spin-startup-differs-from-recovery ──────────────────────
    private void VerdictStartupDiffersFromRecovery()
    {
        if (_poseAtLastStartupTick == null || _poseAtLastRecoveryTick == null)
        {
            Fail("spin-startup-differs-from-recovery: never sampled both a Startup and a Recovery tick " +
                 $"(sawStartup={_sawStartup}, sawRecovery={_sawRecovery}) — the premise for comparing them " +
                 "never held, so this fails rather than passes.");
            Finish(1);
            return;
        }

        float worst = MaxDeltaDeg(_poseAtLastStartupTick, _poseAtLastRecoveryTick);
        PrintPerBoneDeltas("su-vs-re", _poseAtLastStartupTick, _poseAtLastRecoveryTick, MeasuredBones);
        GD.Print($"[spin-anim]   worst Startup-vs-Recovery bone delta = {worst:F2} deg " +
                 $"(floor {StartupVsRecoveryMinDeg:F1})");

        bool premise = _sawStartup && _sawRecovery && _startupTicks >= 3 && _recoveryTicks >= 3;
        bool pass = premise && worst >= StartupVsRecoveryMinDeg;
        if (pass)
            GD.Print("[spin-anim] PASS spin-startup-differs-from-recovery — the last Startup pose (the " +
                     $"coiled plant) and the last Recovery pose (unwound) differ by {worst:F2} deg (#296).");
        else
            Fail($"spin-startup-differs-from-recovery: worst delta {worst:F2} deg < {StartupVsRecoveryMinDeg:F1}, " +
                 $"premise={premise} (startupTicks={_startupTicks}, recoveryTicks={_recoveryTicks}, both need >= 3).");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: spin-clip-drives-the-rig (positive) ─────────────────────
    private void VerdictClipDrivesTheRig()
    {
        bool premise = _sawRecovery && _recoveryTicks >= 3 && !float.IsNaN(_departureFromRestAtLastRecoveryTick);
        bool pass = premise && _departureFromRestAtLastRecoveryTick >= DrivesRigMinDeg;
        if (pass)
            GD.Print($"[spin-anim] PASS spin-clip-drives-the-rig — on the last observed Recovery tick the " +
                     $"rig was still {_departureFromRestAtLastRecoveryTick:F2} deg off rest (floor " +
                     $"{DrivesRigMinDeg:F1}), so the clips' tracks bind and hold this rig rather than collapsing it.");
        else
            Fail($"spin-clip-drives-the-rig: departureFromRestAtLastRecoveryTick=" +
                 $"{_departureFromRestAtLastRecoveryTick:F4} deg (need >= {DrivesRigMinDeg:F1}), premise={premise} " +
                 $"(sawRecovery={_sawRecovery}, recoveryTicks={_recoveryTicks}, need >= 3). Most likely the clips' " +
                 "track NODE PATHS do not bind on scenes/Player.tscn (an 'Armature/' prefix), or the clip is a " +
                 "dead no-op that collapsed to rest a tick after entry.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: spin-clip-does-not-rotate-root (TRAP A — the issue) ─────
    // Two halves, and neither is optional.
    //
    // PREMISE: over the same usable ticks, measured by the SAME code, the
    // SHOULDER span must yaw a lot. Without it, "the hips did not yaw" passes
    // at a confident 0.00 deg whenever the measurement has died — an unentered
    // state, a renamed bone, an inert clip. This is the standing "every
    // X-did-not-happen assertion needs a control that asserts its own premise"
    // rule, satisfied INSIDE the scenario rather than beside it, because here
    // the control quantity and the claimed quantity are the same reading taken
    // on two different spans.
    //
    // CLAIM: the HIP span's yaw excursion stays tiny. author_spin.py pins the
    // Hips basis and rebuild_spin_clips.gd's G4 proves the exported track is
    // constant to 0.0000 deg, so this is measuring that the pin survived the
    // FBX round-trip, the importer, the slice, and the AnimationTree.
    private void VerdictClipDoesNotRotateRoot()
    {
        bool premise = _sawStartup && _sawActive && _sawRecovery && _yawSamples >= 6
                       && !float.IsNaN(_hipYawRef)
                       && _shoulderYawExcursion >= TwistExcursionMinDeg;
        if (!premise)
        {
            Fail($"spin-clip-does-not-rotate-root: PREMISE failed — sawStartup={_sawStartup}, " +
                 $"sawActive={_sawActive}, sawRecovery={_sawRecovery}, yawSamples={_yawSamples} (need >= 6), " +
                 $"shoulderYawExcursion={_shoulderYawExcursion:F2} deg (need >= {TwistExcursionMinDeg:F1}). " +
                 "The shoulder reading is this scenario's own control: it comes from the SAME measurement code " +
                 "on the SAME ticks as the hip reading, so if it is not large then the measurement is dead and " +
                 $"the hip reading of {_hipYawExcursion:F2} deg proves nothing. Fix the measurement, not the clip.");
            Finish(1);
            return;
        }

        GD.Print($"[spin-anim]   yaw excursion over {_yawSamples} usable ticks: " +
                 $"HIP span={_hipYawExcursion:F3} deg (ceiling {HipYawExcursionMaxDeg:F1}), " +
                 $"SHOULDER span={_shoulderYawExcursion:F2} deg (control floor {TwistExcursionMinDeg:F1})");

        bool pass = _hipYawExcursion <= HipYawExcursionMaxDeg;
        if (pass)
            GD.Print($"[spin-anim] PASS spin-clip-does-not-rotate-root — the HIP span yawed only " +
                     $"{_hipYawExcursion:F3} deg across the whole move while the SHOULDER span, read by the " +
                     $"same code on the same ticks, yawed {_shoulderYawExcursion:F2} deg. The turn lives " +
                     "entirely in the spine; the root is not animated, so SpinHeadingMath remains the only " +
                     "thing rotating this player (ADR-0010).");
        else
            Fail($"spin-clip-does-not-rotate-root: the HIP span yawed {_hipYawExcursion:F3} deg " +
                 $"(ceiling {HipYawExcursionMaxDeg:F1}). THIS CLIP MUST NOT ROTATE THE ROOT — player heading is " +
                 "server-authoritative (ADR-0010, SpinHeadingMath), so a clip that also turns the body " +
                 "double-rotates on the server and the predicting client, and fights reconciliation on the " +
                 "remote copy, whose broadcast heading is ~1 RTT stale. That divergence appears ONLY under " +
                 "network conditions, which is why it is gated here and not left to a visual check. Express " +
                 "the turn as SHOULDER-relative-to-HIP twist in author_spin.py's `twist_deg` channel.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: spin-shoulder-twist-reverses (positive) ─────────────────
    // With the root pinned, the shoulder twist reversing is the ONLY thing in
    // this clip that says the body came around. Measured as a SIGNED angle, and
    // gated on the sign at both ends: an abs()-based form would be satisfied by
    // a clip that leans one way and stays there (#339's blind-to-sign lesson).
    //
    // Measured across the Startup-end -> Active-end BOUNDARY rather than
    // "somewhere inside Active", and that is deliberate. The Active segment
    // eases OUT (blender_anim_lib's PHASE_EASING maps "active" to ease_out, so
    // the coil unloads fast and decelerates), which front-loads the crossing
    // into the first ticks of a 6-tick window — exactly the ticks the
    // #316/#340 phase-lag drop removes. Gating on the two phase ends measures
    // the same claim on samples the harness reliably observes.
    private void VerdictTwistReverses()
    {
        bool premise = _sawStartup && _sawActive && _startupTicks >= 3 && _activeTicks >= 3
                       && !float.IsNaN(_twistAtLastStartupTick) && !float.IsNaN(_twistAtLastActiveTick);
        if (!premise)
        {
            Fail($"spin-shoulder-twist-reverses: premise failed — sawStartup={_sawStartup}, " +
                 $"sawActive={_sawActive}, startupTicks={_startupTicks}, activeTicks={_activeTicks} " +
                 $"(both need >= 3), twistAtLastStartupTick={_twistAtLastStartupTick:F2}, " +
                 $"twistAtLastActiveTick={_twistAtLastActiveTick:F2}. A NaN means a span bone did not resolve " +
                 "on the live Skeleton3D — the measurement never happened, so this fails closed rather than " +
                 "reporting a confident 0.00 deg.");
            Finish(1);
            return;
        }

        GD.Print($"[spin-anim]   signed shoulder-vs-hip twist: startup-end={_twistAtLastStartupTick:+0.00;-0.00} deg, " +
                 $"active-end={_twistAtLastActiveTick:+0.00;-0.00} deg (each needs magnitude >= " +
                 $"{TwistReversalMinDeg:F1} and the signs must be OPPOSITE)");

        bool led = _twistAtLastStartupTick >= TwistReversalMinDeg;
        bool passed = _twistAtLastActiveTick <= -TwistReversalMinDeg;
        bool pass = led && passed;
        if (pass)
            GD.Print("[spin-anim] PASS spin-shoulder-twist-reverses — the shoulders LEAD the hips by " +
                     $"{_twistAtLastStartupTick:F2} deg at the end of the wind-up and have been PASSED by them " +
                     $"({_twistAtLastActiveTick:F2} deg) by the end of the turn. That reversal is the whole " +
                     "visible content of the move, since the root is not allowed to rotate.");
        else
            Fail($"spin-shoulder-twist-reverses: shouldersLed={led} " +
                 $"({_twistAtLastStartupTick:F2} deg, need >= +{TwistReversalMinDeg:F1}), " +
                 $"hipsPassed={passed} ({_twistAtLastActiveTick:F2} deg, need <= -{TwistReversalMinDeg:F1}). " +
                 "Same-signed at both ends means the shoulders leaned and stayed leaned — the hips never came " +
                 "around, so the clip reads as a lean rather than a turn. Check author_spin.py's `twist_deg` " +
                 "column.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: control-spin-startup-twist-does-not-reverse (control) ───
    // The control for the scenario above. Without it, "the twist reversed"
    // could be satisfied by a clip that oscillates, or by a spine composition
    // whose sign is noise-dominated near zero — in either case the reversal at
    // Active would be an artefact rather than the move's read. Startup must
    // wind up in ONE direction: it is the telegraph, and eight ticks of it is
    // the longest in the dribble family precisely so it can be read.
    private void VerdictStartupTwistDoesNotReverse()
    {
        bool premise = _sawStartup && _startupTicks >= 3 && _twistStartupSamples >= 2
                       && !float.IsNaN(_twistStartupMin);
        if (!premise)
        {
            Fail($"control-spin-startup-twist-does-not-reverse: premise failed — sawStartup={_sawStartup}, " +
                 $"startupTicks={_startupTicks} (need >= 3), twistStartupSamples={_twistStartupSamples} " +
                 $"(need >= 2), twistStartupMin={_twistStartupMin:F2}. A NaN means a span bone did not resolve " +
                 "on the live Skeleton3D — the measurement never happened.");
            Finish(1);
            return;
        }

        GD.Print($"[spin-anim]   minimum signed twist across {_twistStartupSamples} usable Startup ticks = " +
                 $"{_twistStartupMin:+0.00;-0.00} deg (must stay > 0)");

        bool pass = _twistStartupMin > 0f;
        if (pass)
            GD.Print("[spin-anim] PASS control-spin-startup-twist-does-not-reverse — every usable Startup tick " +
                     $"reads a POSITIVE shoulder-vs-hip twist (minimum {_twistStartupMin:F2} deg), so the " +
                     "wind-up is single-signed and the reversal spin-shoulder-twist-reverses observes at Active " +
                     "is a genuine turn rather than an oscillation.");
        else
            Fail($"control-spin-startup-twist-does-not-reverse: the twist reached {_twistStartupMin:F2} deg " +
                 "inside STARTUP, i.e. it already crossed zero during the wind-up. That makes " +
                 "spin-shoulder-twist-reverses vacuous — a clip that oscillates would satisfy it without ever " +
                 "depicting a turn. Startup must wind UP in one direction; it is the telegraph.");
        Finish(pass ? 0 : 1);
    }

    // ── Static scenarios: no live tree, pure resource/scene inspection ─────
    private void RunStaticCheck()
    {
        switch (_scenario)
        {
            case "spin-no-placeholder-leak": RunNoPlaceholderLeakCheck(); break;
            case "spin-segment-lengths":     RunSegmentLengthsCheck(); break;
            case "spin-edges":               RunEdgesCheck(); break;
            case "spin-stays-unsuffixed":    RunStaysUnsuffixedCheck(); break;
        }
    }

    // ── Scenario: spin-stays-unsuffixed (TRAP B regression guard) ─────────
    // THE predictable wrong change to this move is adding "spin" to
    // MoveAnimResolver.HandedMoves — it genuinely swaps the ball hand, so every
    // heuristic short of reading the swap TIMING would admit it. But the swap
    // lands on the LAST Active tick (PlayerController's Spin branch,
    // FrameInPhase == ActiveFrames - 1), while OriginHand's formula assumes
    // Active-ENTRY. The result would be a clip that is correct in Startup and
    // MIRRORED for five of Active's six ticks: a state that exists, plays
    // cleanly, and telegraphs the wrong side — the ADR-0003 false read, and
    // exactly how the #255 mirror bug shipped green.
    //
    // Two halves, catching the two orders that edit could be made in:
    //   A. the RESOLVER still returns unsuffixed names for BOTH hand sides;
    //   B. scenes/Player.tscn does NOT hold any hand-suffixed Spin state.
    // A alone would miss someone adding the six states first; B alone would
    // miss someone flipping the resolver and relying on Travel()'s silent
    // no-op (#257). The failure message carries the REASONING, not just the
    // fact, because the next author needs to know why the obvious edit is wrong.
    private void RunStaysUnsuffixedCheck()
    {
        bool pass = true;

        // Half A — the resolver. Both hand sides must produce the SAME
        // unsuffixed name for all three phases.
        (MoveAnimState Phase, string Expected)[] phases =
        {
            (MoveAnimState.Startup,  "SpinStartup"),
            (MoveAnimState.Active,   "SpinActive"),
            (MoveAnimState.Recovery, "SpinRecovery"),
        };
        foreach (var (phase, expected) in phases)
        {
            foreach (HandSide hand in new[] { HandSide.Left, HandSide.Right })
            {
                // reachSide is deliberately passed OPPOSITE to ballHand so a
                // future edit that routed spin through TargetHandedMoves by
                // mistake could not be masked by the two arguments agreeing.
                HandSide reach = hand == HandSide.Left ? HandSide.Right : HandSide.Left;
                string actual = MoveAnimResolver.ResolveStateName(phase, "spin", hand, reach);
                GD.Print($"[spin-anim]   ResolveStateName({phase}, \"spin\", ballHand={hand}, " +
                         $"reachSide={reach}) -> {actual}");
                if (actual != expected)
                {
                    Fail($"ResolveStateName({phase}, \"spin\", {hand}, {reach}) returned '{actual}', " +
                         $"expected '{expected}'. Spin must resolve to UNSUFFIXED state names. It swaps the " +
                         "ball hand on the LAST Active tick, not at Active-entry, so OriginHand's " +
                         "phase-conditioned formula (Startup -> ballHand, else Opposite(ballHand)) is WRONG " +
                         "for five of Active's six ticks — the clip would be correct in Startup and mirrored " +
                         "afterwards, which is a state that exists, plays cleanly, and telegraphs the wrong " +
                         "side. Do NOT add \"spin\" to MoveAnimResolver.HandedMoves.");
                    pass = false;
                }
            }
        }

        // Half B — the scene. No hand-suffixed Spin state may exist.
        var sm = LoadStateMachine();
        if (sm == null)
        {
            Fail("could not read an AnimationNodeStateMachine off scenes/Player.tscn's AnimationTree tree_root.");
            Finish(1);
            return;
        }
        foreach (string phase in new[] { "Startup", "Active", "Recovery" })
        {
            foreach (string suffix in new[] { "Left", "Right" })
            {
                string suffixed = $"Spin{phase}{suffix}";
                if (sm.HasNode(suffixed))
                {
                    Fail($"scenes/Player.tscn holds a state '{suffixed}'. Spin is UNHANDED by contract " +
                         "(#310): a handed variant can only be reached by adding spin to HandedMoves, whose " +
                         "OriginHand correction is wrong for this move's LAST-Active-tick swap timing. " +
                         "Remove the suffixed states.");
                    pass = false;
                }
            }
        }

        if (pass)
            GD.Print("[spin-anim] PASS spin-stays-unsuffixed — the resolver returns SpinStartup/SpinActive/" +
                     "SpinRecovery for BOTH ball hands, and scenes/Player.tscn holds no hand-suffixed Spin " +
                     "state for either polarity to reach.");
        else
            GD.PrintErr("[spin-anim] FAIL spin-stays-unsuffixed — see the mismatches above.");

        Finish(pass ? 0 : 1);
    }

    // ── Scenario: spin-edges ───────────────────────────────────────────────
    // README trap 8 / #279: a DELETED transition edge is INVISIBLE to
    // GetCurrentNode(). Travel()'s pathfinder simply routes around the gap, so
    // `spin-phases` stays GREEN with an edge missing — this resource-level
    // check is the only thing in the suite that can see one.
    //
    // Spin is a dribble-family OFFENSIVE move, so it needs the six standard
    // edges AND the dribble-family entries/exits — the latter DOUBLED by #294's
    // DribbleLeft/DribbleRight split, because a live-dribbling holder sits in
    // DribbleLeft or DribbleRight, never in the pre-#294 single `Dribble`
    // state. That gives the same 12-edge shape hesitation, retreat dribble and
    // step-back already use.
    //
    // The Startup -> Recovery edge is kept even though Spin.cs sets
    // feintWindowFrames = 0 ("a fake of a dribble move is not a real basketball
    // action", per the family's #202 closure). It is retained for SHAPE
    // consistency with every other move's state graph; a state machine whose
    // edge set varies with a gameplay constant is much harder to reason about
    // than one that is uniform, and an unused edge costs nothing at runtime.
    private void RunEdgesCheck()
    {
        var sm = LoadStateMachine();
        if (sm == null)
        {
            Fail("could not read an AnimationNodeStateMachine off scenes/Player.tscn's AnimationTree tree_root.");
            Finish(1);
            return;
        }

        (string From, string To)[] required =
        {
            ("Locomotion", "SpinStartup"),
            ("SpinStartup", "SpinActive"),
            ("SpinActive", "SpinRecovery"),
            ("SpinRecovery", "Locomotion"),
            ("SpinStartup", "SpinRecovery"),   // feint / early-out path (feintWindowFrames=0, kept for shape)
            ("SpinStartup", "Locomotion"),     // abort
            ("DribbleLeft", "SpinStartup"),
            ("DribbleRight", "SpinStartup"),
            ("SpinRecovery", "DribbleLeft"),
            ("SpinRecovery", "DribbleRight"),
            ("SpinStartup", "DribbleLeft"),
            ("SpinStartup", "DribbleRight"),
        };

        var present = new System.Collections.Generic.HashSet<string>();
        for (int i = 0; i < sm.GetTransitionCount(); i++)
            present.Add($"{sm.GetTransitionFrom(i)}->{sm.GetTransitionTo(i)}");

        bool pass = true;
        foreach (var (from, to) in required)
        {
            bool here = present.Contains($"{from}->{to}");
            GD.Print($"[spin-anim]   edge {from} -> {to}: {(here ? "present" : "MISSING")}");
            if (!here)
            {
                Fail($"scenes/Player.tscn has no transition '{from}' -> '{to}'. Travel()'s pathfinder will " +
                     "route around the gap, so NO runtime scenario can catch this — only this resource-level " +
                     "check can.");
                pass = false;
            }
        }

        if (pass)
            GD.Print($"[spin-anim] PASS spin-edges — all {required.Length} required transitions are present " +
                     "(6 standard + 6 dribble-family, the latter doubled by #294's DribbleLeft/DribbleRight split).");
        else
            GD.PrintErr("[spin-anim] FAIL spin-edges — see missing transitions above.");

        Finish(pass ? 0 : 1);
    }

    // ── Scenario: spin-segment-lengths ────────────────────────────────────
    private void RunSegmentLengthsCheck()
    {
        var lib = GD.Load<AnimationLibrary>("res://assets/locomotion.res");
        if (lib == null)
        {
            Fail("assets/locomotion.res failed to load — cannot evaluate spin-segment-lengths.");
            Finish(1);
            return;
        }

        double tps = Engine.PhysicsTicksPerSecond;
        var frames = Spin.DefaultFrameData;
        const double ToleranceSeconds = 1e-3;

        (string Clip, int Ticks)[] windows =
        {
            ("spinstartup",  frames.StartupFrames),
            ("spinactive",   frames.ActiveFrames),
            ("spinrecovery", frames.RecoveryFrames),
        };

        bool pass = true;
        foreach (var (clipName, ticks) in windows)
        {
            if (!lib.HasAnimation(clipName))
            {
                Fail($"AnimationLibrary has no clip '{clipName}' — run tools/rebuild_spin_clips.gd.");
                pass = false;
                continue;
            }

            double expectedSeconds = ticks / tps;
            double actualSeconds = lib.GetAnimation(clipName).Get("length").AsDouble();
            double deviationSeconds = Math.Abs(actualSeconds - expectedSeconds);
            GD.Print($"[spin-anim]   '{clipName}': length={actualSeconds:F6}s expected={expectedSeconds:F6}s " +
                     $"({ticks} ticks @ {tps} tps), deviation={deviationSeconds:F6}s");

            if (deviationSeconds > ToleranceSeconds)
            {
                Fail($"clip '{clipName}' is {actualSeconds:F6}s, expected {expectedSeconds:F6}s ({ticks} ticks " +
                     $"at {tps} tps — Spin.DefaultFrameData), a deviation of {deviationSeconds:F6}s exceeds the " +
                     $"float-noise tolerance ({ToleranceSeconds:F6}s). Re-run tools/rebuild_spin_clips.gd after " +
                     "retuning the move's frame data.");
                pass = false;
            }
        }

        if (pass)
            GD.Print("[spin-anim] PASS spin-segment-lengths — all three clips' durations match " +
                     "Spin.DefaultFrameData's windows to within float noise.");
        else
            GD.PrintErr("[spin-anim] FAIL spin-segment-lengths — see per-clip deviations above.");

        Finish(pass ? 0 : 1);
    }

    // ── Scenario: spin-no-placeholder-leak ────────────────────────────────
    private void RunNoPlaceholderLeakCheck()
    {
        var stateMachine = LoadStateMachine();
        if (stateMachine == null)
        {
            Fail("could not read an AnimationNodeStateMachine off scenes/Player.tscn's AnimationTree " +
                 "tree_root — the state<->clip mapping is unverified.");
            Finish(1);
            return;
        }

        (string State, string Clip)[] states =
        {
            ("SpinStartup",  "locomotion/spinstartup"),
            ("SpinActive",   "locomotion/spinactive"),
            ("SpinRecovery", "locomotion/spinrecovery"),
        };
        string[] placeholderClips = { "locomotion/idle", "locomotion/run" };

        bool pass = true;
        foreach (var (stateName, expectedClip) in states)
        {
            if (!stateMachine.HasNode(stateName))
            {
                Fail($"scenes/Player.tscn's state machine has no state '{stateName}'.");
                pass = false;
                continue;
            }
            if (stateMachine.GetNode(stateName) is not AnimationNodeAnimation animNode)
            {
                Fail($"state '{stateName}' is not an AnimationNodeAnimation — a per-move state must be a " +
                     "single-clip node.");
                pass = false;
                continue;
            }

            string actualClip = animNode.Animation.ToString();
            GD.Print($"[spin-anim]   {stateName} -> {actualClip}");

            if (actualClip != expectedClip)
            {
                string extra = placeholderClips.Contains(actualClip)
                    ? " — this is the #296 GENERIC PLACEHOLDER; the state was never repointed at its own clip."
                    : " — a real clip, but the wrong one (copy-paste from a neighbouring move's sub-resource).";
                Fail($"state '{stateName}' points at '{actualClip}', expected '{expectedClip}'{extra}");
                pass = false;
            }
        }

        if (pass)
            GD.Print("[spin-anim] PASS spin-no-placeholder-leak — all three Spin states point at their OWN " +
                     "per-move clips, not the shared locomotion/idle placeholder.");
        else
            GD.PrintErr("[spin-anim] FAIL spin-no-placeholder-leak — see per-state mismatches above.");

        Finish(pass ? 0 : 1);
    }

    private static AnimationNodeStateMachine LoadStateMachine()
    {
        var playerScene = GD.Load<PackedScene>("res://scenes/Player.tscn");
        var sceneState = playerScene.GetState();
        for (int i = 0; i < sceneState.GetNodeCount(); i++)
        {
            if (sceneState.GetNodeType(i) != "AnimationTree") continue;
            for (int p = 0; p < sceneState.GetNodePropertyCount(i); p++)
            {
                if (sceneState.GetNodePropertyName(i, p) != "tree_root") continue;
                return sceneState.GetNodePropertyValue(i, p).As<AnimationNodeStateMachine>();
            }
        }
        return null;
    }

    // ── Geometry helpers ────────────────────────────────────────────────────

    // The signed YAW of a left->right bone span, in SKELETON-LOCAL space, about
    // the skeleton's +Y. Local space is the point: the PlayerController node's
    // own authoritative rotation (SpinHeadingMath's arc) does not enter this
    // reading, so what is measured is purely the CLIP's contribution — which is
    // exactly the quantity trap A puts under contract.
    //
    // Returns NaN — not 0 — when either bone fails to resolve or the span is
    // near-vertical (measurement-helpers-must-poison-on-failure, #305). 0.0 is
    // the PASSING value for the hip-yaw claim, so a helper that degraded to it
    // would make the central gate of this issue print a confident PASS while
    // measuring nothing.
    private static float SpanYawDeg(Skeleton3D skel, string leftBone, string rightBone)
    {
        int l = skel.FindBone(leftBone);
        int r = skel.FindBone(rightBone);
        if (l < 0 || r < 0) return float.NaN;
        Vector3 span = skel.GetBoneGlobalPose(r).Origin - skel.GetBoneGlobalPose(l).Origin;
        span.Y = 0f;
        if (span.Length() < 1e-4f) return float.NaN;
        return Mathf.RadToDeg(Mathf.Atan2(span.X, span.Z));
    }

    // The signed shoulder-span-relative-to-hip-span yaw this tick — the move's
    // whole visible read, since the root may not rotate. NaN propagates.
    private static float TwistDeg(Skeleton3D skel)
    {
        float hip = SpanYawDeg(skel, HipLeftBone, HipRightBone);
        float sho = SpanYawDeg(skel, ArmLeftBone, ArmRightBone);
        if (float.IsNaN(hip) || float.IsNaN(sho)) return float.NaN;
        // Wrapped to (-180, 180] so a reading near the +/-180 seam cannot read
        // as a ~360 deg swing.
        return Mathf.RadToDeg(Mathf.AngleDifference(Mathf.DegToRad(hip), Mathf.DegToRad(sho)));
    }

    // POISONS (returns null) rather than substituting Quaternion.Identity for a
    // bone that does not resolve (#305). Identity would contribute a 0 deg
    // delta, which is a PASSING reading for nothing here and a FAILING one for
    // the #296 comparison — silently wrong in both directions.
    private static Quaternion[] SampleBones(Skeleton3D skel, string[] boneNames)
    {
        var poses = new Quaternion[boneNames.Length];
        for (int i = 0; i < boneNames.Length; i++)
        {
            int idx = skel.FindBone(boneNames[i]);
            if (idx < 0) return null;
            poses[i] = skel.GetBonePose(idx).Basis.GetRotationQuaternion().Normalized();
        }
        return poses;
    }

    private static Quaternion[] SampleMeasuredBones(Skeleton3D skel) => SampleBones(skel, MeasuredBones);

    // NaN — not 0 — when either side never sampled, for the same fails-closed
    // reason SampleBones returns null.
    private static float MaxDeltaDeg(Quaternion[] a, Quaternion[] b)
    {
        if (a == null || b == null) return float.NaN;
        float worst = 0f;
        for (int i = 0; i < a.Length && i < b.Length; i++)
            worst = Math.Max(worst, Mathf.RadToDeg(a[i].AngleTo(b[i])));
        return worst;
    }

    // DIAGNOSTIC ONLY: per-bone breakdown, so a reviewer reading the CI log can
    // see WHICH bone drives a reading rather than trusting an opaque max.
    private static void PrintPerBoneDeltas(string label, Quaternion[] a, Quaternion[] b, string[] names)
    {
        if (a == null || b == null) return;
        for (int i = 0; i < a.Length && i < b.Length && i < names.Length; i++)
            GD.Print($"[spin-anim]     {label} {names[i],-24} {Mathf.RadToDeg(a[i].AngleTo(b[i])):F2} deg");
    }

    // Worst MeasuredBones rotation off REST on the live Skeleton3D, this tick.
    // Returns NaN — not 0 — when no bone resolves (#305).
    private static float DepartureFromRestDeg(Skeleton3D skel)
    {
        float worst = 0f;
        int measured = 0;
        foreach (string boneName in MeasuredBones)
        {
            int idx = skel.FindBone(boneName);
            if (idx < 0) continue;
            measured++;
            Quaternion rest = skel.GetBoneRest(idx).Basis.GetRotationQuaternion().Normalized();
            Quaternion pose = skel.GetBonePose(idx).Basis.GetRotationQuaternion().Normalized();
            worst = Math.Max(worst, Mathf.RadToDeg(rest.AngleTo(pose)));
        }
        return measured == 0 ? float.NaN : worst;
    }

    private static Skeleton3D FindSkeleton(Node root)
    {
        if (root is Skeleton3D s) return s;
        foreach (Node child in root.GetChildren())
        {
            Skeleton3D found = FindSkeleton(child);
            if (found != null) return found;
        }
        return null;
    }

    private void Fail(string message) => GD.PrintErr($"[spin-anim] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[spin-anim] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
