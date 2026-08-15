using System;
using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #307 (ADR-0016): proves the THREE
// HESITATION ANIMATION STATES (HesitationStartup / HesitationActive /
// HesitationRecovery) wired into scenes/Player.tscn are real — entered
// end-to-end by a real Hesitation, bound to the right clips, cut to the right
// windows, and (unlike every other move in this batch) genuinely HOLD during
// Active rather than moving through it.
//
// Before #307 "hesitation" fell through MoveAnimResolver.ResolveStateName's
// default case onto the shared generic Startup/Active/Recovery states, which
// per #296 render a looping IDLE for Startup/Recovery (pixel-identical, so an
// opponent cannot tell "committing" from "in the punish window") and a
// looping SPRINT for Active — an outright false read for the one move in this
// batch whose entire content is standing tall and freezing.
//
//   godot --headless --path . res://tests/integration/HesitationAnimTest.tscn -- --harness-scenario=hesitation-phases
//   …=hesitation-no-placeholder-leak | hesitation-segment-lengths
//   …=hesitation-startup-differs-from-recovery | hesitation-clip-drives-the-rig
//   …=hesitation-active-raises-hips | control-hesitation-recovery-lowers-hips
//   …=hesitation-active-is-held | control-hesitation-startup-is-not-held
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//
// ── moveId IS "hesitation" ────────────────────────────────────────────────
// Hesitation.cs:53 constructs with `id: "hesitation"`, so the
// ClippedMovePrefixes key and the moveId coincide, same as stepback/
// retreatdribble (unlike jab step's "jab").
//
// ── Dispatch path this harness drives, and the one it does NOT ─────────────
// This harness begins the move via BeginMoveForHarness(new Hesitation()) —
// DefensiveMoveHarnessSeam.cs's generic seam — which reaches BeginCommittedMove
// DIRECTLY, bypassing BOTH of PlayerController.SampleMoveInput's two hesitation
// dispatch sites (the held-gesture GestureKind.Crossover branch around line
// 3396, and the quick-return GestureKind.QuickReturn branch around line 3422 —
// see that method's own comment: "a flick TOWARD the ball hand is a
// hesitation" on either gesture shape). Both construct an identical
// `new Hesitation()` with no payload, so this harness proves the clip/wiring
// half of BOTH paths equally — but it does NOT exercise
// RightStickGestureRecognizer's own gesture classification (which of the two
// GestureKinds fires for a given stick motion). That classification is
// RightStickGestureRecognizerTests' job (xUnit), not this file's — say so
// rather than implying broader coverage than this harness actually has.
//
// ── Setup mirrors StepBackAnimTest.cs's own pattern ─────────────────────────
// A live dribble, then BeginMoveForHarness — downstream of every gate
// PlayerController.BeginCommittedMove imposes (the #193 dead-Held rule, which
// Hesitation shares with Crossover/BehindTheBack/StepBack/RetreatDribble/etc).
// This harness is COSMETIC-ONLY: it never observes or feeds BallState,
// HasDribbled, or any gameplay constant — HesitationTests (if any exist) own
// that half; this file owns only the display layer.
//
// ── Why ActiveAnimNodeForHarness, not the resolver's return value (#257) ────
// Travel() to a missing/misnamed state only LOGS; it never throws. Only the
// live AnimationNodeStateMachinePlayback proves wiring.
//
// ── The phase-label-lag trap (#316/#340), and why every geometric gate here
// drops each phase's FIRST observed tick ──────────────────────────────────
// Even with CallbackModeProcess=Physics, the first tick GetCurrentNode()
// names a phase still holds the PREVIOUS phase's pose. Every hip-height and
// bone-pose measurement below is latched starting from the SECOND observed
// tick of its phase, and Active/Recovery (8/6 ticks) assert >= 3 usable
// (post-drop) ticks before trusting a mean or a hold-delta computed from it.
//
// Startup is the exception, and it is MEASURED, not assumed: Startup is only
// 4 ticks, and on the live rig this harness observes only 3 raw
// "HesitationStartup" ticks for it, not the naively-expected 4 — one more is
// lost somewhere in the Begin()->Travel() pipeline beyond the well-known
// #316/#340 pose-lag tick. (Tried forcing an extra Observe() call in the
// SAME physics tick BeginMoveForHarness succeeds, on the theory that the Act
// step's own frame was going unobserved — it made no measured difference,
// so that specific theory is not the mechanism; the exact cause was not
// pinned down further, and is not needed to design correctly around the
// MEASURED count.) That leaves only 2 usable Startup ticks after the drop.
// JabStepAnimTest.cs's own even-shorter (3-tick) Startup already established
// the ">= 2" floor for exactly this regime; Startup-keyed premises here
// follow that precedent rather than the >= 3 used for the longer phases.
public partial class HesitationAnimTest : Node
{
    private const double TimeoutSeconds = 15.0;
    private const int ArmFrames = 2;      // ticks for TryAssignTipoffHolder to run
    private const int ActFrames = 3;      // ticks after tipoff before Begin
    // startup(4)+active(8)+recovery(6)=18 ticks, generous slack.
    private const int ObserveFrames = 40;

    private static readonly Vector3 ActorSpot = new(0f, 0f, 2f);
    private static readonly Vector3 FarSpot = new(12f, 0f, 12f); // out of PickupRadius
    private static readonly Vector3 RimCenter = new(0f, 3.05f, 0f); // BallController.DefaultRimCenter

    // Bones for the Startup-vs-Recovery pose comparison and the Active-hold
    // delta measurement — the whole-body set every dribble-family script in
    // this batch uses, since a hesitation's read (torso pitch + leg
    // extension + arm reach) touches all of them, not just one limb.
    private static readonly string[] MeasuredBones =
    {
        "mixamorig_LeftArm", "mixamorig_RightArm",
        "mixamorig_LeftForeArm", "mixamorig_RightForeArm",
        "mixamorig_Spine",
        "mixamorig_LeftUpLeg", "mixamorig_RightUpLeg",
        "mixamorig_LeftLeg", "mixamorig_RightLeg",
        "mixamorig_LeftFoot", "mixamorig_RightFoot",
    };

    // Narrower subset for the Active-hold / Startup-not-held delta
    // measurement (scenarios 8/9) specifically — torso + arms, no legs.
    // MEASURED, not a style preference: with the full MeasuredBones set the
    // leg bones' IK reacts disproportionately to the Active window's own tiny
    // (0.03 m) hip-height hold-drift — extending/retracting the leg to keep
    // the planted ankle on the floor is a rotation at the hip/knee even for a
    // few-cm target change — so a "genuinely held" Active window still read
    // ~10 deg of max per-bone delta, swamping the signal this pair of
    // scenarios needs to discriminate. Torso + arms are what handoff 07's own
    // motion spec actually names as the hold's read ("torso near vertical...
    // ball held high, close to the body"); legs stay in MeasuredBones for the
    // OTHER scenarios (4/5), where their genuine stance-to-stance travel is
    // exactly the signal wanted.
    private static readonly string[] HoldMeasuredBones =
    {
        "mixamorig_LeftArm", "mixamorig_RightArm",
        "mixamorig_LeftForeArm", "mixamorig_RightForeArm",
        "mixamorig_Spine",
    };

    // ── Thresholds ──────────────────────────────────────────────────────────
    // EVERY floor below is set from a value MEASURED BY THIS HARNESS ON THE
    // LIVE RIG (StepBackAnimTest's own documented discipline — the Blender/
    // resource/live-rig spaces disagree on absolute readings, so every gate
    // here is either a RELATIVE claim off a live measurement or re-measured
    // fresh in this frame). See the PR for the actual measured numbers.

    // NOT the same 15.0 floor author_hesitation.py / rebuild_hesitation_clips.gd
    // use Blender/resource-side (55.552 / 42.9 deg) — this harness measures a
    // SMALLER, genuinely live-rig quantity, and it is a real structural gap,
    // not a bug: Startup's own last OBSERVABLE tick on the live rig lands only
    // ~2 ticks into its 4-tick, heavily ease_in-shaped (backloaded) ramp — see
    // the class doc's Act/Observe handoff note — so most of Startup's swing
    // has not happened yet by the time this harness can read it. MEASURED
    // 11.83 deg on the live rig; floor set with ~30% headroom under that.
    private const float StartupVsRecoveryMinDeg = 8.0f;

    // The "clip physically drives the rig" floor — README's verification
    // floor / #281's mutation lesson: max-departure-from-rest and
    // max-change-across-the-arc both pass on a deliberately-unbound clip; only
    // the FINAL-tick reading separates a bound clip from a collapsed one.
    // Matches BetweenTheLegsAnimTest's PosedMinDeg discipline — 30 sits in the
    // empty middle between an unbound clip's ~0 deg and a bound one's 100+.
    private const float DrivesRigMinDeg = 30.0f;

    // hesitation-active-is-held boundness Half B floor (see that verdict's
    // own comment): Active's own baseline pose must differ from Startup's own
    // last pose by at least this much, on HoldMeasuredBones.
    //
    // BOTH populations are MEASURED, and the floor is placed between them
    // rather than just under the passing one. Healthy: 38.30 deg. Blanking
    // HesitationActive's animation string (the frozen-carry-over failure this
    // half exists to catch): 8.93 deg — NOT the 0.00 deg the construction
    // naively suggests, because the pose this harness latched on Startup's
    // last OBSERVED tick lags Startup's last RENDERED pose by one tick of its
    // own ramp (#316/#340), and that one tick of residual ramp motion is the
    // 8.93. Sitting the floor just over that (the first value tried here was
    // 10.0) leaves the mutation only ~11% below the line — far too little for
    // a reading that depends on a lag artefact. 20.0 sits roughly midway in
    // ratio terms: 1.9x under the healthy reading, 2.2x over the mutation's.
    private const float ActiveEntryDiffersFromStartupEndMinDeg = 20.0f;

    // hesitation-active-raises-hips floor: mean(Active) - mean(Startup), in
    // METRES on the live rig. See the PR for the measured number; this sits
    // comfortably under it.
    private const float ActiveRaisesHipsMinM = 0.05f;

    // control-hesitation-recovery-lowers-hips floor: mean(Active) -
    // mean(Recovery), in METRES. The premise for the gate above — without
    // this, "Active raises hips" could pass merely because the whole clip
    // was authored tall. MEASURED 0.0462 m on the live rig (Recovery's own
    // ease_in_out settle only partly completes within its observed window,
    // the same live-rig-vs-authored gap StartupVsRecoveryMinDeg documents);
    // floor set with headroom under that rather than reused from
    // ActiveRaisesHipsMinM's 0.05, which this would otherwise just miss.
    private const float RecoveryLowersHipsMinM = 0.035f;

    // The SINGLE threshold scenarios 8 and 9 share, in DEGREES PER TICK: the
    // largest per-bone rotation delta between any two CONSECUTIVE observed
    // ticks of a phase (HoldMeasuredBones only — see that field's doc).
    // hesitation-active-is-held requires Active to stay UNDER it;
    // control-hesitation-startup-is-not-held requires Startup to go OVER it.
    // One constant, opposite directions — that is what makes the pair a real
    // discriminator rather than two independently-tuned numbers.
    //
    // WHY PER-TICK, and not the phase's total baseline-to-last swing. The two
    // phases yield structurally unequal observation windows: Active is 8 ticks
    // and survives this harness's Act->Observe handoff largely intact (6
    // usable intervals), while Startup is only 4 ticks and yields exactly ONE
    // (3 raw observed ticks, minus the #316/#340 lagged first, leaves 2
    // samples). Comparing TOTALS across those windows is unfair in Startup's
    // disfavour and inverts the inequality: measured, Active totals 5.58 deg
    // over 6 intervals while Startup totals 3.21 deg over 1 — which reads as
    // "the held phase moved more" purely because it was sampled six times as
    // often. Per-tick is the same reduction applied fairly to both, and it
    // recovers the real relationship: Startup moves ~3.5x faster per tick.
    //
    // Per-tick is also strictly STRONGER than baseline-to-last at the thing
    // this pair exists to detect. Baseline-to-last compares only the two
    // endpoints, so a pose that swings away mid-Active and returns reads as
    // perfectly "held"; a max over consecutive pairs sees the swing.
    //
    // MEASURED on the live rig: Active 1.35 deg/tick (over 6 intervals),
    // Startup 3.21 deg/tick (over its 1). Set between them with roughly
    // balanced headroom — 1.5x over Active's reading, 1.6x under Startup's.
    private const float HoldMaxDegPerTick = 2.0f;

    private static readonly string[] KnownScenarios =
    {
        "hesitation-phases",
        "hesitation-no-placeholder-leak",
        "hesitation-segment-lengths",
        "hesitation-startup-differs-from-recovery",
        "hesitation-clip-drives-the-rig",
        "hesitation-active-raises-hips",
        "control-hesitation-recovery-lowers-hips",
        "hesitation-active-is-held",
        "control-hesitation-startup-is-not-held",
    };

    // Scenarios that need no live tree — pure resource/scene inspection.
    private static readonly string[] StaticScenarios =
    {
        "hesitation-no-placeholder-leak",
        "hesitation-segment-lengths",
    };

    private string _scenario = "hesitation-phases";

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

    // Per-phase observed-tick counts (RAW, including the lagged first tick —
    // #316/#340). "Usable" ticks for a geometric measurement are this minus 1.
    private int _startupTicks;
    private int _activeTicks;
    private int _recoveryTicks;

    // Mean hip-height accumulators, dropping each phase's first observed
    // tick (see the class doc). World Y of the Hips bone.
    private double _hipSumStartup, _hipSumActive, _hipSumRecovery;
    private int _hipCountStartup, _hipCountActive, _hipCountRecovery;

    // Startup-vs-Recovery pose comparison (#296) — latched on each phase's
    // LAST observed tick (overwritten every tick, so it ends up holding the
    // last one).
    private Quaternion[] _poseAtLastStartupTick;
    private Quaternion[] _poseAtLastRecoveryTick;

    // "hesitation-clip-drives-the-rig": departure from rest on the LAST tick
    // of the WHOLE MOVE, i.e. Recovery's own last observed tick. Overwritten
    // every Recovery tick so it ends up holding the final one.
    private float _departureFromRestAtLastRecoveryTick = float.NaN;

    // "hesitation-active-is-held"'s boundness premise: departure from rest on
    // the LAST observed ACTIVE tick specifically (not Recovery's). Overwritten
    // every Active tick so it ends up holding the final one.
    private float _departureFromRestAtLastActiveTick = float.NaN;

    // DIAGNOSTIC ONLY (printed, never asserted on): the baseline pose latched
    // at each phase's SECOND observed tick (the first genuinely-that-phase
    // sample per #316/#340) and the phase's LAST observed tick's pose.
    // Retained because the per-bone baseline-to-last breakdown is what makes a
    // CI log readable — but the VERDICTS compare on the per-tick maxima below.
    // See HoldMaxDegPerTick's doc for why: baseline-to-last cannot see a pose
    // that swings away mid-phase and returns, and comparing phase TOTALS
    // across Active's 6 usable intervals vs Startup's 1 inverts the very
    // inequality the control exists to establish.
    private Quaternion[] _activeBaselinePose;
    private Quaternion[] _activeLastHoldPose;
    private Quaternion[] _startupBaselinePose;
    private Quaternion[] _startupLastHoldPose;

    // The reduction scenarios 8 and 9 actually COMPARE on: the largest
    // per-bone rotation delta between any two CONSECUTIVE observed ticks of a
    // phase, i.e. degrees per tick. See HoldMaxDegPerTick's doc.
    private Quaternion[] _prevActiveHoldPose;
    private Quaternion[] _prevStartupHoldPose;
    private float _maxPerTickActive;
    private float _maxPerTickStartup;
    private int _perTickIntervalsActive;
    private int _perTickIntervalsStartup;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "hesitation-phases");
        GD.Print($"[hesitation-anim] scenario={_scenario} booting headless…");

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

        // Physics-callback lockstep so GetCurrentNode() reflects the
        // same-tick Travel() (README trap 6).
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
                // BeginMoveForHarness (DefensiveMoveHarnessSeam.cs's generic
                // seam) reaches BeginCommittedMove DIRECTLY — see the class
                // doc's "Dispatch path this harness drives" section for what
                // this does and does not prove.
                if (!_actor.BeginMoveForHarness(new Hesitation()))
                {
                    Fail($"{_scenario}: BeginMoveForHarness(new Hesitation()) returned false — the actor's " +
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

        if (!_sawStartup && node == "HesitationStartup") _sawStartup = true;
        if (_sawStartup && !_sawActive && node == "HesitationActive") _sawActive = true;
        if (_sawActive && !_sawRecovery && node == "HesitationRecovery") _sawRecovery = true;
        if (node == "Startup" || node == "Active" || node == "Recovery") _sawGenericPlaceholder = true;

        var skel = FindSkeleton(_actor);
        if (skel == null) return;

        float hipY = skel.GetBoneGlobalPose(skel.FindBone("mixamorig_Hips")).Origin.Y;

        if (node == "HesitationStartup")
        {
            _startupTicks++;
            if (_startupTicks >= 2)
            {
                _hipSumStartup += hipY;
                _hipCountStartup++;
                var holdPose = SampleHoldBones(skel);
                if (_startupTicks == 2) _startupBaselinePose = holdPose;
                _startupLastHoldPose = holdPose; // overwritten -> ends up the LAST tick's pose
                AccumulatePerTickDelta(ref _prevStartupHoldPose, holdPose,
                                       ref _maxPerTickStartup, ref _perTickIntervalsStartup);
            }
            _poseAtLastStartupTick = SampleMeasuredBones(skel);
        }
        else if (node == "HesitationActive")
        {
            _activeTicks++;
            if (_activeTicks >= 2)
            {
                _hipSumActive += hipY;
                _hipCountActive++;
                var holdPose = SampleHoldBones(skel);
                if (_activeTicks == 2) _activeBaselinePose = holdPose;
                _activeLastHoldPose = holdPose; // overwritten -> ends up the LAST tick's pose
                AccumulatePerTickDelta(ref _prevActiveHoldPose, holdPose,
                                       ref _maxPerTickActive, ref _perTickIntervalsActive);
            }
            // Overwritten every Active tick — ends up holding the LAST one,
            // which is exactly what the boundness premise in
            // hesitation-active-is-held needs (Active's OWN last tick, not
            // Recovery's).
            _departureFromRestAtLastActiveTick = DepartureFromRestDeg(skel);
        }
        else if (node == "HesitationRecovery")
        {
            _recoveryTicks++;
            if (_recoveryTicks >= 2)
            {
                _hipSumRecovery += hipY;
                _hipCountRecovery++;
            }
            _poseAtLastRecoveryTick = SampleMeasuredBones(skel);
            // Overwritten every Recovery tick — ends up holding the LAST one,
            // which is what "clip-drives-the-rig" needs (README's
            // verification floor: max-across-the-arc passes vacuously on a
            // clip that collapsed to rest a tick after entry).
            _departureFromRestAtLastRecoveryTick = DepartureFromRestDeg(skel);
        }
    }

    private void RenderVerdict()
    {
        GD.Print($"[hesitation-anim]   observed ticks: startup={_startupTicks} " +
                 $"active={_activeTicks} recovery={_recoveryTicks}");
        switch (_scenario)
        {
            case "hesitation-phases":                              VerdictPhases(); break;
            case "hesitation-startup-differs-from-recovery":       VerdictStartupDiffersFromRecovery(); break;
            case "hesitation-clip-drives-the-rig":                 VerdictClipDrivesTheRig(); break;
            case "hesitation-active-raises-hips":                  VerdictActiveRaisesHips(); break;
            case "control-hesitation-recovery-lowers-hips":        VerdictRecoveryLowersHips(); break;
            case "hesitation-active-is-held":                      VerdictActiveIsHeld(); break;
            case "control-hesitation-startup-is-not-held":         VerdictStartupIsNotHeld(); break;
        }
    }

    // ── Scenario: hesitation-phases (positive) ────────────────────────────
    private void VerdictPhases()
    {
        bool pass = _sawStartup && _sawActive && _sawRecovery;
        if (pass)
            GD.Print("[hesitation-anim] PASS hesitation-phases — the tree was observed on \"HesitationStartup\", " +
                     "then \"HesitationActive\", then \"HesitationRecovery\", in that order.");
        else
            Fail($"hesitation-phases: expected HesitationStartup -> HesitationActive -> HesitationRecovery, in " +
                 $"order; got sawStartup={_sawStartup}, sawActive={_sawActive}, sawRecovery={_sawRecovery}, " +
                 $"sawGenericPlaceholder={_sawGenericPlaceholder}, lastAnimNode={_actor.ActiveAnimNodeForHarness}.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: hesitation-startup-differs-from-recovery ────────────────
    private void VerdictStartupDiffersFromRecovery()
    {
        if (_poseAtLastStartupTick == null || _poseAtLastRecoveryTick == null)
        {
            Fail("hesitation-startup-differs-from-recovery: never sampled both a Startup and a Recovery tick " +
                 $"(sawStartup={_sawStartup}, sawRecovery={_sawRecovery}) — the premise for comparing them never " +
                 "held, so this fails rather than passes.");
            Finish(1);
            return;
        }

        float worst = MaxDeltaDeg(_poseAtLastStartupTick, _poseAtLastRecoveryTick);
        PrintPerBoneDeltas("su-vs-re", _poseAtLastStartupTick, _poseAtLastRecoveryTick, MeasuredBones);
        GD.Print($"[hesitation-anim]   worst Startup-vs-Recovery bone delta = {worst:F2} deg " +
                 $"(floor {StartupVsRecoveryMinDeg:F1})");

        bool premise = _sawStartup && _sawRecovery && _startupTicks >= 3 && _recoveryTicks >= 3;
        bool pass = premise && worst >= StartupVsRecoveryMinDeg;
        if (pass)
            GD.Print("[hesitation-anim] PASS hesitation-startup-differs-from-recovery — the last Startup pose " +
                     $"and the last Recovery pose differ by {worst:F2} deg (#296).");
        else
            Fail($"hesitation-startup-differs-from-recovery: worst delta {worst:F2} deg < {StartupVsRecoveryMinDeg:F1}, " +
                 $"premise={premise} (startupTicks={_startupTicks}, recoveryTicks={_recoveryTicks}, both need >= 3).");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: hesitation-clip-drives-the-rig (positive) ────────────────
    // Departure from rest on the LAST tick of the MOVE (Recovery's own last
    // observed tick), not the max across it — README's verification floor:
    // both naive metrics (max-departure-from-rest, max-change-across-the-arc)
    // pass on a deliberately-unbound clip, and only the final-tick reading
    // separates 0.0 deg from a genuine hold.
    private void VerdictClipDrivesTheRig()
    {
        bool premise = _sawRecovery && _recoveryTicks >= 3 && !float.IsNaN(_departureFromRestAtLastRecoveryTick);
        bool pass = premise && _departureFromRestAtLastRecoveryTick >= DrivesRigMinDeg;
        if (pass)
            GD.Print($"[hesitation-anim] PASS hesitation-clip-drives-the-rig — on the last observed Recovery " +
                     $"tick the rig was still {_departureFromRestAtLastRecoveryTick:F2} deg off rest " +
                     $"(floor {DrivesRigMinDeg:F1}), so the clips' tracks bind and hold this rig rather than " +
                     "collapsing it.");
        else
            Fail($"hesitation-clip-drives-the-rig: departureFromRestAtLastRecoveryTick=" +
                 $"{_departureFromRestAtLastRecoveryTick:F4} deg (need >= {DrivesRigMinDeg:F1}), premise={premise} " +
                 $"(sawRecovery={_sawRecovery}, recoveryTicks={_recoveryTicks}, need >= 3). Most likely the " +
                 "clips' track NODE PATHS do not bind on scenes/Player.tscn (an 'Armature/' prefix), or the " +
                 "clip is a dead no-op that collapsed to rest a tick after entry.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: hesitation-active-raises-hips (positive) ─────────────────
    private void VerdictActiveRaisesHips()
    {
        // hipCountStartup's floor is 2, not 3: Startup is only 4 ticks, and
        // this harness's Act->Observe handoff plus the #316/#340 pose-lag tick
        // it drops together cap Startup's RAW observed-tick count at 3 (see
        // the class doc) — one fewer than a naive "StartupFrames" count would
        // suggest, and the SAME regime JabStepAnimTest's own even-shorter
        // (3-tick) Startup already established the ">= 2" precedent for.
        // Active/Recovery are long enough (8/6 ticks) that >= 3 holds easily.
        bool premise = _sawStartup && _sawActive && _hipCountStartup >= 2 && _hipCountActive >= 3;
        if (!premise)
        {
            Fail($"hesitation-active-raises-hips: premise failed — sawStartup={_sawStartup}, sawActive={_sawActive}, " +
                 $"hipCountStartup={_hipCountStartup} (need >= 2), hipCountActive={_hipCountActive} (need >= 3).");
            Finish(1);
            return;
        }
        float meanStartup = (float)(_hipSumStartup / _hipCountStartup);
        float meanActive = (float)(_hipSumActive / _hipCountActive);
        float delta = meanActive - meanStartup;
        GD.Print($"[hesitation-anim]   mean Hips height: startup={meanStartup:F4} active={meanActive:F4} " +
                 $"delta={delta:F4} (floor {ActiveRaisesHipsMinM:F2})");

        bool pass = delta >= ActiveRaisesHipsMinM;
        if (pass)
            GD.Print("[hesitation-anim] PASS hesitation-active-raises-hips — mean Hips height across Active " +
                     $"exceeds mean across Startup by {delta:F4} m (floor {ActiveRaisesHipsMinM:F2}).");
        else
            Fail($"hesitation-active-raises-hips: mean(Active) - mean(Startup) = {delta:F4} m, need >= " +
                 $"{ActiveRaisesHipsMinM:F2}.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: control-hesitation-recovery-lowers-hips (control) ───────
    // The premise for the gate above — without this, "Active raises hips"
    // could pass merely because the whole clip was authored tall.
    private void VerdictRecoveryLowersHips()
    {
        bool premise = _sawActive && _sawRecovery && _hipCountActive >= 3 && _hipCountRecovery >= 3;
        if (!premise)
        {
            Fail($"control-hesitation-recovery-lowers-hips: premise failed — sawActive={_sawActive}, " +
                 $"sawRecovery={_sawRecovery}, hipCountActive={_hipCountActive}, " +
                 $"hipCountRecovery={_hipCountRecovery} (both need >= 3).");
            Finish(1);
            return;
        }
        float meanActive = (float)(_hipSumActive / _hipCountActive);
        float meanRecovery = (float)(_hipSumRecovery / _hipCountRecovery);
        float delta = meanActive - meanRecovery;
        GD.Print($"[hesitation-anim]   mean Hips height: active={meanActive:F4} recovery={meanRecovery:F4} " +
                 $"delta={delta:F4} (floor {RecoveryLowersHipsMinM:F2})");

        bool pass = delta >= RecoveryLowersHipsMinM;
        if (pass)
            GD.Print("[hesitation-anim] PASS control-hesitation-recovery-lowers-hips — mean Hips height across " +
                     $"Active exceeds mean across Recovery by {delta:F4} m (floor {RecoveryLowersHipsMinM:F2}); " +
                     "the rise in Active is a real arc, not just a tall clip.");
        else
            Fail($"control-hesitation-recovery-lowers-hips: mean(Active) - mean(Recovery) = {delta:F4} m, need " +
                 $">= {RecoveryLowersHipsMinM:F2}.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: hesitation-active-is-held (positive, two halves) ─────────
    // Half 1 (premise/boundness): on the last observed Active tick, some bone
    // must depart from rest by >= DrivesRigMinDeg — without this, an UNBOUND
    // clip (collapsed to rest within a tick, and therefore perfectly STILL)
    // would satisfy "the claim" (half 2) vacuously. Half 2 (the claim): the
    // per-bone delta between the phase's OWN second observed tick and its OWN
    // LAST observed tick stays under a small ceiling — see the
    // "_activeLastHoldPose" field doc for why this is baseline-to-LAST rather
    // than a running max across every intermediate tick.
    private void VerdictActiveIsHeld()
    {
        bool premise = _sawActive && _activeTicks >= 3 && _activeBaselinePose != null
                       && _activeLastHoldPose != null && _perTickIntervalsActive >= 2;
        if (!premise)
        {
            Fail($"hesitation-active-is-held: premise failed — sawActive={_sawActive}, activeTicks={_activeTicks} " +
                 $"(need >= 3), perTickIntervalsActive={_perTickIntervalsActive} (need >= 2), baselinePose=" +
                 $"{(_activeBaselinePose == null ? "MISSING" : "ok")}, lastPose=" +
                 $"{(_activeLastHoldPose == null ? "MISSING" : "ok")}. A MISSING pose means a HoldMeasuredBones " +
                 "bone did not resolve on the live Skeleton3D — the measurement never happened, so this fails " +
                 "closed rather than reporting a confident 0.00 deg.");
            Finish(1);
            return;
        }
        float delta = _maxPerTickActive;
        PrintPerBoneDeltas("active baseline->last (diagnostic only)",
                           _activeBaselinePose, _activeLastHoldPose, HoldMeasuredBones);

        // The boundness premise has TWO halves, and both are load-bearing —
        // mutation-proven, not merely argued (see the PR).
        //
        // Half A: on the LAST observed ACTIVE tick, some bone must depart
        // from rest by >= DrivesRigMinDeg. Without this, a clip whose TRACKS
        // don't resolve (the classic a45bd1d case — e.g. the "Armature/"
        // prefix trap) collapses the affected bones to skeleton REST, and a
        // literally-at-rest pose would satisfy the claim below vacuously.
        //
        // Half B: Active's OWN baseline (its 2nd observed tick) must differ
        // from STARTUP's own last observed pose by >= a floor. This is a
        // SEPARATE failure mode from Half A, and Half A alone does not catch
        // it: pointing HesitationActive at a BLANK or unresolvable animation
        // NAME (as opposed to a resolvable clip with broken track paths)
        // does not collapse the skeleton to rest at all — Godot's
        // AnimationMixer contributes nothing for that node, so the rig
        // FREEZES at whatever pose Startup's own final tick left it in. That
        // frozen pose reads FAR from skeleton rest (it is Startup's own
        // genuinely-posed final frame), so Half A alone passes it —
        // confirmed by mutation: blanking HesitationActive's animation
        // string read departureFromRestAtLastActiveTick=179.98 deg (comfortably
        // over the 30 deg floor) while baselineToLastDeltaDeg=0.00 (a frozen
        // clip is trivially "held"). Half B catches exactly this: a frozen
        // carry-over pose is, by construction, IDENTICAL to Startup's own
        // last pose, so it fails to clear this floor.
        bool boundnessPremiseA = !float.IsNaN(_departureFromRestAtLastActiveTick)
                                  && _departureFromRestAtLastActiveTick >= DrivesRigMinDeg;
        float activeEntryVsStartupEndDeg = (_poseAtLastStartupTick != null)
            ? MaxDeltaDeg(SampleHoldSubsetOf(_poseAtLastStartupTick), _activeBaselinePose)
            : float.NaN;
        bool boundnessPremiseB = !float.IsNaN(activeEntryVsStartupEndDeg)
                                  && activeEntryVsStartupEndDeg >= ActiveEntryDiffersFromStartupEndMinDeg;
        bool boundnessPremise = boundnessPremiseA && boundnessPremiseB;

        GD.Print($"[hesitation-anim]   Active hold: maxPerTickDeltaDeg={delta:F2} deg/tick over " +
                 $"{_perTickIntervalsActive} intervals (ceiling {HoldMaxDegPerTick:F1}); boundness A " +
                 $"(departureFromRestAtLastActiveTick)={_departureFromRestAtLastActiveTick:F2} deg (floor " +
                 $"{DrivesRigMinDeg:F1}); boundness B (activeEntryVsStartupEndDeg)=" +
                 $"{activeEntryVsStartupEndDeg:F2} deg (floor {ActiveEntryDiffersFromStartupEndMinDeg:F1})");

        bool claimPass = delta <= HoldMaxDegPerTick;
        bool pass = boundnessPremise && claimPass;
        if (pass)
            GD.Print("[hesitation-anim] PASS hesitation-active-is-held — the clip is bound (departs from rest) " +
                     $"AND no two consecutive Active ticks move any measured bone more than {delta:F2} deg " +
                     $"(ceiling {HoldMaxDegPerTick:F1}): Active genuinely reads as arrested.");
        else
            Fail($"hesitation-active-is-held: boundnessPremise={boundnessPremise}, claimPass={claimPass} " +
                 $"(maxPerTickDeltaDeg={delta:F2} deg/tick, ceiling {HoldMaxDegPerTick:F1}). " +
                 "If the premise failed, the clip is an unbound no-op (vacuously still); if the claim failed, " +
                 "Active is moving, not holding.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: control-hesitation-startup-is-not-held (control) ─────────
    // The SAME measurement code path and the SAME constant as
    // hesitation-active-is-held, run against Startup and asserted in the
    // OPPOSITE direction: Startup must EXCEED the very ceiling Active must
    // stay under. That is what makes HoldMaxDegPerTick a discriminator rather
    // than a number tuned to whatever Active happens to read — a measurement
    // that had gone dead (or a bone set that stopped resolving) would read
    // ~0 deg/tick here and redden this scenario on every CI run, not only
    // under a manual mutation.
    private void VerdictStartupIsNotHeld()
    {
        // One interval is all Startup structurally yields: 4 authored ticks,
        // 3 raw observed, minus the #316/#340 lagged first leaves 2 samples.
        // See HoldMaxDegPerTick's doc — this is exactly why the comparison is
        // per-tick and not per-phase-total.
        bool premise = _sawStartup && _startupTicks >= 3 && _startupBaselinePose != null
                       && _startupLastHoldPose != null && _perTickIntervalsStartup >= 1;
        if (!premise)
        {
            Fail($"control-hesitation-startup-is-not-held: premise failed — sawStartup={_sawStartup}, " +
                 $"startupTicks={_startupTicks} (need >= 3), perTickIntervalsStartup=" +
                 $"{_perTickIntervalsStartup} (need >= 1), baselinePose=" +
                 $"{(_startupBaselinePose == null ? "MISSING" : "ok")}, lastPose=" +
                 $"{(_startupLastHoldPose == null ? "MISSING" : "ok")}. A MISSING pose means a HoldMeasuredBones " +
                 "bone did not resolve on the live Skeleton3D — the measurement never happened.");
            Finish(1);
            return;
        }
        float startupDelta = _maxPerTickStartup;
        PrintPerBoneDeltas("startup baseline->last (diagnostic only)",
                           _startupBaselinePose, _startupLastHoldPose, HoldMeasuredBones);

        GD.Print($"[hesitation-anim]   Startup motion: maxPerTickDeltaDeg={startupDelta:F2} deg/tick over " +
                 $"{_perTickIntervalsStartup} interval(s) (floor {HoldMaxDegPerTick:F1} — the SAME constant " +
                 "hesitation-active-is-held uses as its ceiling)");

        bool pass = startupDelta > HoldMaxDegPerTick;
        if (pass)
            GD.Print("[hesitation-anim] PASS control-hesitation-startup-is-not-held — Startup moves " +
                     $"{startupDelta:F2} deg/tick, ABOVE the {HoldMaxDegPerTick:F1} deg/tick ceiling Active must " +
                     "stay under: the same measurement that reads Active as arrested reads Startup as moving.");
        else
            Fail($"control-hesitation-startup-is-not-held: maxPerTickDeltaDeg={startupDelta:F2} deg/tick, need " +
                 $"> {HoldMaxDegPerTick:F1}. If Startup reads as held by the same measurement that calls Active " +
                 "held, then hesitation-active-is-held is not discriminating anything and its green is worthless.");
        Finish(pass ? 0 : 1);
    }

    // ── Static scenarios: no live tree, pure resource/scene inspection ─────
    private void RunStaticCheck()
    {
        switch (_scenario)
        {
            case "hesitation-no-placeholder-leak": RunNoPlaceholderLeakCheck(); break;
            case "hesitation-segment-lengths":       RunSegmentLengthsCheck(); break;
        }
    }

    // ── Scenario: hesitation-segment-lengths ────────────────────────────────
    private void RunSegmentLengthsCheck()
    {
        var lib = GD.Load<AnimationLibrary>("res://assets/locomotion.res");
        if (lib == null)
        {
            Fail("assets/locomotion.res failed to load — cannot evaluate hesitation-segment-lengths.");
            Finish(1);
            return;
        }

        double tps = Engine.PhysicsTicksPerSecond;
        var frames = Hesitation.DefaultFrameData;
        const double ToleranceSeconds = 1e-3;

        (string Clip, int Ticks)[] windows =
        {
            ("hesitationstartup",  frames.StartupFrames),
            ("hesitationactive",   frames.ActiveFrames),
            ("hesitationrecovery", frames.RecoveryFrames),
        };

        bool pass = true;
        foreach (var (clipName, ticks) in windows)
        {
            if (!lib.HasAnimation(clipName))
            {
                Fail($"AnimationLibrary has no clip '{clipName}' — run tools/rebuild_hesitation_clips.gd.");
                pass = false;
                continue;
            }

            double expectedSeconds = ticks / tps;
            double actualSeconds = lib.GetAnimation(clipName).Get("length").AsDouble();
            double deviationSeconds = Math.Abs(actualSeconds - expectedSeconds);
            GD.Print($"[hesitation-anim]   '{clipName}': length={actualSeconds:F6}s expected={expectedSeconds:F6}s " +
                     $"({ticks} ticks @ {tps} tps), deviation={deviationSeconds:F6}s");

            if (deviationSeconds > ToleranceSeconds)
            {
                Fail($"clip '{clipName}' is {actualSeconds:F6}s, expected {expectedSeconds:F6}s ({ticks} ticks " +
                     $"at {tps} tps — Hesitation.DefaultFrameData), a deviation of {deviationSeconds:F6}s exceeds " +
                     $"the float-noise tolerance ({ToleranceSeconds:F6}s). Re-run tools/rebuild_hesitation_clips.gd " +
                     "after retuning the move's frame data.");
                pass = false;
            }
        }

        if (pass)
            GD.Print("[hesitation-anim] PASS hesitation-segment-lengths — all three clips' durations match " +
                     "Hesitation.DefaultFrameData's windows to within float noise.");
        else
            GD.PrintErr("[hesitation-anim] FAIL hesitation-segment-lengths — see per-clip deviations above.");

        Finish(pass ? 0 : 1);
    }

    // ── Scenario: hesitation-no-placeholder-leak ─────────────────────────────
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
            ("HesitationStartup",  "locomotion/hesitationstartup"),
            ("HesitationActive",   "locomotion/hesitationactive"),
            ("HesitationRecovery", "locomotion/hesitationrecovery"),
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
            GD.Print($"[hesitation-anim]   {stateName} -> {actualClip}");

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
            GD.Print("[hesitation-anim] PASS hesitation-no-placeholder-leak — all three Hesitation states point " +
                     "at their OWN per-move clips, not the shared locomotion/idle placeholder.");
        else
            GD.PrintErr("[hesitation-anim] FAIL hesitation-no-placeholder-leak — see per-state mismatches above.");

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

    // Both samplers POISON (return null) rather than substituting
    // Quaternion.Identity for a bone that does not resolve
    // (measurement-helpers-must-poison-on-failure, #305). Identity would be
    // silently WRONG in the vacuous direction for this file specifically:
    // an unresolved bone contributes a 0 deg delta, and 0 is a passing
    // reading for hesitation-active-is-held's "stayed still" ceiling. A
    // renamed/missing bone would then make the hold gate EASIER while still
    // printing a confident number. Null forces every gate to assert its own
    // premise instead.
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

    private static Quaternion[] SampleHoldBones(Skeleton3D skel) => SampleBones(skel, HoldMeasuredBones);

    // Folds one more observed tick into a phase's running MAX CONSECUTIVE-TICK
    // delta — the reduction scenarios 8/9 both compare on. See
    // HoldMaxDegPerTick's doc for why this is per-tick rather than
    // baseline-to-last.
    private static void AccumulatePerTickDelta(
        ref Quaternion[] previous, Quaternion[] current, ref float worst, ref int intervals)
    {
        if (previous != null && current != null)
        {
            float d = MaxDeltaDeg(previous, current);
            if (!float.IsNaN(d))
            {
                worst = Math.Max(worst, d);
                intervals++;
            }
        }
        previous = current;
    }

    // Projects a MeasuredBones-indexed pose array (as SampleMeasuredBones
    // returns) down onto the narrower HoldMeasuredBones subset, matched BY
    // NAME rather than by assumed index order — used to compare
    // _poseAtLastStartupTick (full-body) against _activeBaselinePose
    // (hold-subset) on a common bone set for the boundness-premise Half B
    // check in VerdictActiveIsHeld.
    private static Quaternion[] SampleHoldSubsetOf(Quaternion[] measuredBonesPose)
    {
        if (measuredBonesPose == null) return null;
        var result = new Quaternion[HoldMeasuredBones.Length];
        for (int i = 0; i < HoldMeasuredBones.Length; i++)
        {
            int idx = Array.IndexOf(MeasuredBones, HoldMeasuredBones[i]);
            if (idx < 0 || idx >= measuredBonesPose.Length) return null; // poison, never Identity
            result[i] = measuredBonesPose[idx];
        }
        return result;
    }

    // NaN — not 0 — when either side never sampled, for the same
    // fails-closed reason SampleBones returns null.
    private static float MaxDeltaDeg(Quaternion[] a, Quaternion[] b)
    {
        if (a == null || b == null) return float.NaN;
        float worst = 0f;
        for (int i = 0; i < a.Length && i < b.Length; i++)
            worst = Math.Max(worst, Mathf.RadToDeg(a[i].AngleTo(b[i])));
        return worst;
    }

    // DIAGNOSTIC ONLY: per-bone breakdown, so a reviewer reading the CI log
    // can see WHICH bone drives a hold-delta reading rather than trusting an
    // opaque aggregate max.
    private static void PrintPerBoneDeltas(string label, Quaternion[] a, Quaternion[] b, string[] names)
    {
        for (int i = 0; i < a.Length && i < b.Length && i < names.Length; i++)
            GD.Print($"[hesitation-anim]     {label} {names[i],-24} {Mathf.RadToDeg(a[i].AngleTo(b[i])):F2} deg");
    }

    // Worst MeasuredBones rotation off REST on the live Skeleton3D, this
    // tick. Returns NaN — not 0 — when no bone resolves, so a resolution
    // failure fails the gate closed instead of printing a confident "0.0000
    // deg" that reads as a real measurement of a real defect
    // (measurement-helpers-must-poison-on-failure discipline, #305).
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

    private void Fail(string message) => GD.PrintErr($"[hesitation-anim] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[hesitation-anim] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
