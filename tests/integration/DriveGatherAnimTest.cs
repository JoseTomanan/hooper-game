using System;
using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #311 (ADR-0016): proves the THREE
// DRIVE-GATHER ANIMATION STATES (DriveGatherStartup / DriveGatherActive /
// DriveGatherRecovery) wired into scenes/Player.tscn are real — entered
// end-to-end by a real DriveGather, bound to the right clips, cut to the right
// windows, and actually MOVING the rig.
//
// Before #311 "drivegather" fell through MoveAnimResolver.ResolveStateName's
// default case onto the shared generic Startup/Active/Recovery states. For this
// move that fallback is not merely uninformative, it is a RULES lie: the gather
// is the frame after which the DRIBBLE IS DEAD (ADR-0022), and per #296 the
// generic Active state plays a looping locomotion/run — advertising a drive the
// holder can no longer legally make. MoveAnimState's own doc names exactly that
// as "an actively FALSE read, which is worse than no signal."
//
//   godot --headless --path . res://tests/integration/DriveGatherAnimTest.tscn -- --harness-scenario=drivegather-phases
//   …=drivegather-no-placeholder-leak | drivegather-segment-lengths | drivegather-edges
//   …=drivegather-startup-differs-from-recovery | drivegather-clip-drives-the-rig
//   …=drivegather-active-brings-both-hands-to-ball | control-drivegather-startup-hands-apart
//   …=drivegather-active-displaces-forward | control-drivegather-startup-loads-back
//   …=drivegather-recovery-hands-off-to-layup
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//
// ── moveId IS "drivegather" ───────────────────────────────────────────────
// DriveGather.cs constructs with `id: "drivegather"`, so the
// ClippedMovePrefixes key and the moveId coincide.
//
// ── Setup, and why it goes through the generic seam ────────────────────────
// A live dribble, then DefensiveMoveHarnessSeam's BeginMoveForHarness(new
// DriveGather()) — the same route MoveKindAnimTest already drives this move
// through, downstream of every production gate (#193's dead-dribble gate in
// particular, which is why the dribble has to be live first). There is no
// DriveGatherHarnessSeam and one move-typed passthrough is not worth a new
// file.
//
// This harness is COSMETIC-ONLY (#311's standing constraint): it never observes
// or feeds DriveGatherBurstSpeed, DriveGatherDecel, BallState or HasDribbled.
// Those remain DriveGatherTest's job — its `dead-dribble-gate` scenario asserts
// behaviour this file cannot reach, and stays green throughout.
//
// One consequence of the move's own semantics is worth naming, because it looks
// like a bug the first time it is seen: DriveGather CRADLES at Startup-begin
// (PlayerController.cs — the ADR-0022 "the gather IS the move" branch), so the
// ball leaves Dribbling the instant the move starts and the holder settles on
// "Locomotion" rather than a Dribble state afterwards. Nothing here asserts the
// settled node, so that is inert for this file; it is recorded so the next
// reader does not go looking for a regression.
//
// ── Why ActiveAnimNodeForHarness, not the resolver's return value (#257) ────
// Travel() to a missing/misnamed state only LOGS; it never throws. Only the live
// AnimationNodeStateMachinePlayback proves wiring.
//
// ── "Displaces forward" is measured RELATIVE TO THE TRAIL FOOT ─────────────
// author_drivegather.py's own `_verify_hips_stay_in_place` proves the Hips
// bone's clip-space position moves along `up` ONLY, never fore/aft. That is
// deliberate: PlayerController already applies the real 1.50 m forward burst via
// DriveGatherMath.ComposeActiveVelocity on JustEnteredActive, so a clip that
// ALSO translated the Hips would play the burst twice. The clip instead depicts
// "the hips travelled forward" by leaving the TRAIL foot behind, so this
// scenario measures the same claim from the other side of that relationship:
// Hips position relative to the TRAIL (rear, LEFT) foot, projected along the
// rig's own forward axis.
//
// The trail foot specifically, not an average of both — the same reasoning
// StepBackAnimTest records for its own mirror-image gate. The LEAD foot swings
// hugely forward during Active (it is the 0.70 m gather step), so an average
// would be dominated by the swing rather than by the body's travel over its
// base. The trail foot's own trajectory genuinely REVERSES between the two
// phases — the gather sinks the weight back over it during Startup, then drives
// off it during Active — which is what gives this scenario pair a real,
// opposite-sign contrast rather than two floors that both happen to pass.
public partial class DriveGatherAnimTest : Node
{
    private const double TimeoutSeconds = 15.0;
    private const int ArmFrames = 2;   // ticks for TryAssignTipoffHolder to run
    private const int ActFrames = 3;   // ticks after tipoff before Begin
    // startup(6)+active(10)+recovery(14)=30 ticks, generous slack.
    private const int ObserveFrames = 60;

    private static readonly Vector3 ActorSpot = new(0f, 0f, 4f);
    private static readonly Vector3 FarSpot = new(12f, 0f, 12f); // out of PickupRadius
    private static readonly Vector3 RimCenter = new(0f, 3.05f, 0f); // BallController.DefaultRimCenter

    // Bones for the Startup-vs-Recovery pose comparison and the travel
    // accumulator. Covers the whole base plus the arms, because this move's read
    // is a WHOLE-BODY event: the legs carry the step and the arms carry the
    // gather.
    private static readonly string[] ComparedBones =
    {
        "mixamorig_LeftArm", "mixamorig_RightArm",
        "mixamorig_LeftForeArm", "mixamorig_RightForeArm",
        "mixamorig_Spine",
        "mixamorig_LeftUpLeg", "mixamorig_RightUpLeg",
        "mixamorig_LeftLeg", "mixamorig_RightLeg",
        "mixamorig_LeftFoot", "mixamorig_RightFoot",
        "mixamorig_LeftToeBase", "mixamorig_RightToeBase",
    };

    // Landmarks for the recovery -> layup hand-off gate, matching
    // rebuild_drivegather_clips.gd's G6 exactly (same list, same
    // "relative to Hips" metric) — this is the SECOND independent
    // re-measurement of that claim (Blender authoring time cannot reach it at
    // all, since layupstartup lives in a different source pipeline). Written
    // fresh in C# against the Godot API rather than sharing code with the .gd
    // tool, so a bug in one measurement path is unlikely to be replicated in
    // the other.
    private static readonly string[] HandoffLandmarkBones =
    {
        "mixamorig_LeftHand", "mixamorig_RightHand", "mixamorig_Head",
        "mixamorig_LeftFoot", "mixamorig_RightFoot",
    };

    // ── Thresholds ──────────────────────────────────────────────────────────
    // EVERY floor below is set from a value MEASURED BY THIS HARNESS ON THE LIVE
    // RIG. The Blender, resource and live-rig spaces disagree on absolute
    // readings (the #316 phase-label lag alone means the last observable tick of
    // a phase lands short of that phase's authored end pose), so a number
    // carried over from author_drivegather.py would be a guess wearing a
    // measurement's clothes.

    // MEASURED on the live rig: worst bone delta 47.90 deg, i.e. 3.2x the floor.
    // Floor matches every sibling harness's 15.0.
    //
    // WHAT THIS GATE DOES AND DOES NOT CATCH, measured rather than assumed --
    // and it is NOT what _new-move-wiring.md's mutation table predicts, so the
    // correction is recorded here rather than left for the next author to
    // rediscover:
    //
    //   all three states -> the pre-#296 generic fallback   2.59 deg  RED
    //   Recovery repointed at the STARTUP clip             26.36 deg  GREEN
    //
    // The first is the real defect #296 reports (idle for BOTH phases, pixel-
    // identical) and this gate separates it from healthy by 18x. The second is
    // a sub-resource copy-paste, and this gate CANNOT see it -- because the two
    // phases are 6 and 14 ticks long while the shared clip is 6, so Recovery
    // holds that clip's END pose while Startup's last OBSERVABLE tick lands
    // short of it under the #316 label lag. The gate then compares two different
    // instants of one clip and finds 26 deg of honest difference.
    //
    // That is a real limitation, not a tuning problem: healthy 47.90 against
    // defective 26.36 is 1.8x, far too tight to place a floor in safely, and
    // raising it would be fitting to one mutation. The copy-paste case is
    // instead owned by drivegather-no-placeholder-leak, which went RED on it and
    // whose failure message names that exact scenario. Clip IDENTITY lives
    // there; this gate owns the POSE claim.
    private const float StartupVsRecoveryMinDeg = 15.0f;

    // "The clip physically drives the rig", in accumulated tick-to-tick pose
    // TRAVEL. Deliberately NOT departure-from-rest, which #310 mutation-proved
    // vacuous on this rig: with all three states repointed at nonexistent clips
    // it still read 145.57 deg and passed, because locomotion.res's retargeted
    // clips sit 150-180 deg from the Y Bot's own T-pose rest, so ANY pose the
    // rig merely coasts on reads as an enormous departure.
    //
    // Travel is summed strictly WITHIN a phase — see AccumulateTravel for why
    // crossing a boundary silently restores most of the weakness this
    // measurement exists to remove. MEASURED live: 197.36 deg over 23 within-
    // phase deltas. Floor stays at 30 rather than near the healthy reading
    // because the failure it excludes is "the rig barely moves"; pinning it high
    // would redden on any legitimate re-author that made the move calmer.
    //
    // Mutation-measured on this move:
    //   all three states -> nonexistent clips (unbound)      0.00 deg  RED
    //   one clip name misspelled                           142.22 deg  GREEN
    //   all three -> the pre-#296 generic fallback          76.16 deg  GREEN
    //
    // The last row is worth reading carefully, because it differs from #310's
    // equivalent (2.14 deg, RED) and the difference is honest rather than a
    // weakness here. Spin's mutation put a STATIC idle in all three slots; the
    // true pre-#296 fallback for an Active phase is a LOOPING locomotion/run,
    // which genuinely animates -- so travel stays high and this gate correctly
    // declines to call it frozen. It never claimed the clips were the RIGHT
    // ones; drivegather-no-placeholder-leak owns identity and went RED on both.
    private const float DrivesRigMinDeg = 30.0f;

    // THE RULES SIGNAL. Wrist-to-wrist distance at the last usable Active tick
    // must be at most this; at the last usable Startup tick, at least the
    // second figure. MEASURED live: 0.2391 m converged against 0.5552 m apart,
    // so the two bands are separated by 0.10 m of no-man's-land, the ceiling
    // sits 25% above its reading and the floor 28% below its own.
    //
    // Do NOT widen the ceiling to make a retune pass. Handoff 11 is explicit
    // that the off-hand must come clearly ONTO the ball, not near it, and an
    // ambiguous gather is precisely the actively-false read this clip exists to
    // replace.
    private const float HandsConvergedMaxM = 0.30f;
    private const float HandsApartMinM = 0.40f;

    // Hips travel forward relative to the trail foot, across Active, measured
    // from Startup's OWN last tick rather than the pre-move baseline (see
    // VerdictActiveDisplacesForward). MEASURED live: +0.1837 m, 3.7x the floor.
    private const float HipsForwardActiveMinM = 0.05f;

    // ...and travel BACKWARD over the same measure during Startup: a gather
    // LOADS before it goes. MEASURED live: -0.0497 m between Startup's second
    // and last usable ticks, 2.5x the floor. This is the opposite-sign control
    // that makes the figure above mean something.
    //
    // That 2.5x was bought in the CLIP, not here. Startup is six ticks and its
    // `ease_in` curve puts most of the motion at the end, but this gate cannot
    // see the end: the #316 label lag costs the first observed tick and the
    // phase boundary costs the authored last one, so it differences ticks 2..5
    // of 6. An earlier draft authored -0.10 m of load and this read -0.0248 --
    // green, but 1.24x is a flake waiting to happen. author_drivegather.py's
    // load was doubled to -0.20 m instead of lowering this floor.
    private const float HipsBackStartupMinM = 0.02f;

    // Recovery -> Layup hand-off. Shares rebuild_drivegather_clips.gd's
    // RECOVERY_LAYUP_HANDOFF_MAX_M rather than inventing a second number for one
    // question; that constant's own comment carries the measured breakdown and
    // the pre-existing cross-source hip offset it is dominated by.
    private const float RecoveryLayupHandoffMaxM = 0.45f;

    private static readonly string[] KnownScenarios =
    {
        "drivegather-phases",
        "drivegather-no-placeholder-leak",
        "drivegather-segment-lengths",
        "drivegather-edges",
        "drivegather-startup-differs-from-recovery",
        "drivegather-clip-drives-the-rig",
        "drivegather-active-brings-both-hands-to-ball",
        "control-drivegather-startup-hands-apart",
        "drivegather-active-displaces-forward",
        "control-drivegather-startup-loads-back",
        "drivegather-recovery-hands-off-to-layup",
    };

    // Scenarios that need no live tree — pure resource/scene inspection.
    private static readonly string[] StaticScenarios =
    {
        "drivegather-no-placeholder-leak",
        "drivegather-segment-lengths",
        "drivegather-edges",
        "drivegather-recovery-hands-off-to-layup",
    };

    private string _scenario = "drivegather-phases";

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

    // Per-phase observed-tick counts, RAW (including the lagged first tick).
    // The #316/#340 trap: the first tick GetCurrentNode() names a phase can
    // still hold the PREVIOUS phase's pose, so a phase must be observed for more
    // than one tick before its "last tick" reading can be trusted.
    private int _startupTicks;
    private int _activeTicks;
    private int _recoveryTicks;

    private Vector3? _cachedForward;

    private float _hipsRelBeforeMove = float.NaN;              // one tick before Begin
    private float _hipsRelAtFirstValidStartupTick = float.NaN; // 2nd observed Startup tick
    private float _hipsRelAtLastStartupTick = float.NaN;
    private float _hipsRelAtLastActiveTick = float.NaN;

    private float _wristGapAtLastStartupTick = float.NaN;
    private float _wristGapAtLastActiveTick = float.NaN;

    private Quaternion[] _poseAtLastStartupTick;
    private Quaternion[] _poseAtLastRecoveryTick;

    // ── `drivegather-clip-drives-the-rig`'s accumulator ─────────────────────
    private float _poseTravelDeg;
    private Quaternion[] _poseAtPreviousUsableTick;
    private int _travelSamples;
    private string _travelPhase;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "drivegather-phases");
        GD.Print($"[drivegather-anim] scenario={_scenario} booting headless…");

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
                // Faced at the rim: DriveGather's Startup steers the drive line
                // toward RimCenter through HeadingMath.RotateToward's bounded
                // turn rate (ADR-0010), so starting already square avoids
                // spending Startup ticks rotating.
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
                         $"{_frame} (got {_ball.State}). DriveGather cannot Begin from Held (#193).");
                    Finish();
                    return;
                }
                _step = Step.Act;
                _stepDeadlineFrame = _frame + ActFrames;
                break;

            case Step.Act:
                if (_frame < _stepDeadlineFrame) break;
                // Sample the PRE-MOVE baseline one tick before Begin, while the
                // actor is unambiguously still in the Dribble stance — the one
                // point in this run with no tick-lag ambiguity about which pose
                // is being read (#316's trap). It is a REPORTED reference, never
                // an anchor: see VerdictControlStartupLoadsBack.
                {
                    var skelPre = FindSkeleton(_actor);
                    if (skelPre != null)
                        _hipsRelBeforeMove = MeasureHipsRelativeToTrailFoot(skelPre);
                }
                if (!_actor.BeginMoveForHarness(new DriveGather()))
                {
                    Fail($"{_scenario}: BeginMoveForHarness(new DriveGather()) returned false — the actor's " +
                         $"machine was not Inactive, or a begin gate refused it. Ball state = {_ball?.State}.");
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

        if (!_sawStartup && node == "DriveGatherStartup") _sawStartup = true;
        if (_sawStartup && !_sawActive && node == "DriveGatherActive") _sawActive = true;
        if (_sawActive && !_sawRecovery && node == "DriveGatherRecovery") _sawRecovery = true;
        if (node == "Startup" || node == "Active" || node == "Recovery") _sawGenericPlaceholder = true;

        var skel = FindSkeleton(_actor);
        if (skel == null) return;

        if (node != "DriveGatherStartup" && node != "DriveGatherActive" && node != "DriveGatherRecovery")
            return;

        float hipsRel = MeasureHipsRelativeToTrailFoot(skel);
        float wristGap = MeasureWristGap(skel);

        int phaseTicks;
        if (node == "DriveGatherStartup")
        {
            phaseTicks = ++_startupTicks;
            // The SECOND observed tick is this phase's first sample whose pose is
            // genuinely Startup's: under the #316 lag the first tick labelled
            // "DriveGatherStartup" still holds the previous (Dribble) pose.
            // Anchoring the control here rather than at the pre-move baseline is
            // what makes it measure the CLIP's own authored load instead of the
            // Dribble -> Startup stance discontinuity.
            if (_startupTicks == 2) _hipsRelAtFirstValidStartupTick = hipsRel;
            if (_startupTicks >= 2)
            {
                _hipsRelAtLastStartupTick = hipsRel;
                _wristGapAtLastStartupTick = wristGap;
                _poseAtLastStartupTick = SampleComparedBones(skel);
            }
        }
        else if (node == "DriveGatherActive")
        {
            phaseTicks = ++_activeTicks;
            if (_activeTicks >= 2)
            {
                _hipsRelAtLastActiveTick = hipsRel;
                _wristGapAtLastActiveTick = wristGap;
            }
        }
        else
        {
            phaseTicks = ++_recoveryTicks;
            if (_recoveryTicks >= 2)
                _poseAtLastRecoveryTick = SampleComparedBones(skel);
        }

        AccumulateTravel(skel, node, phaseTicks);
    }

    // Folds one observed tick into the pose-travel accumulator, EXCLUDING every
    // pose jump that is not travel the clip performed. Both exclusions are
    // load-bearing and both were established by mutation in #310:
    //
    //   1. The phase's FIRST observed tick is skipped outright — under the
    //      #316/#340 label lag it still holds the previous phase's pose, so any
    //      delta touching it is a phase-boundary snap.
    //   2. The phase's SECOND observed tick only SEEDS the reference. Seeding one
    //      tick later than the skip is what actually excludes the boundary;
    //      resetting on the phase change alone would still charge the
    //      stale-tick -> first-real-pose jump to the clip.
    //
    // Without these the accumulator counts the Dribble -> Startup entry snap plus
    // the two internal boundary snaps, and three CONSTANT clips holding three
    // different poses would clear any floor comfortably — even though a
    // single-keyframe clip is one of the exact failure modes this gate claims to
    // catch.
    private void AccumulateTravel(Skeleton3D skel, string phaseNode, int phaseTicks)
    {
        if (phaseTicks < 2) return;

        Quaternion[] now = SampleComparedBones(skel);
        if (now == null) return;

        if (phaseNode != _travelPhase)
        {
            // First USABLE tick of this phase: seed only, never a delta.
            _travelPhase = phaseNode;
            _poseAtPreviousUsableTick = now;
            return;
        }

        if (_poseAtPreviousUsableTick != null)
        {
            float worst = 0f;
            for (int i = 0; i < now.Length && i < _poseAtPreviousUsableTick.Length; i++)
                worst = Math.Max(worst, Mathf.RadToDeg(now[i].AngleTo(_poseAtPreviousUsableTick[i])));
            _poseTravelDeg += worst;
            _travelSamples++;
        }
        _poseAtPreviousUsableTick = now;
    }

    private void RenderVerdict()
    {
        GD.Print($"[drivegather-anim]   observed ticks: startup={_startupTicks} " +
                 $"active={_activeTicks} recovery={_recoveryTicks}");
        switch (_scenario)
        {
            case "drivegather-phases":                          VerdictPhases(); break;
            case "drivegather-startup-differs-from-recovery":   VerdictStartupDiffersFromRecovery(); break;
            case "drivegather-clip-drives-the-rig":             VerdictClipDrivesTheRig(); break;
            case "drivegather-active-brings-both-hands-to-ball": VerdictActiveBringsBothHandsToBall(); break;
            case "control-drivegather-startup-hands-apart":     VerdictControlStartupHandsApart(); break;
            case "drivegather-active-displaces-forward":        VerdictActiveDisplacesForward(); break;
            case "control-drivegather-startup-loads-back":      VerdictControlStartupLoadsBack(); break;
        }
    }

    // ── Scenario: drivegather-phases (positive) ──────────────────────────────
    private void VerdictPhases()
    {
        bool pass = _sawStartup && _sawActive && _sawRecovery;
        if (pass)
            GD.Print("[drivegather-anim] PASS drivegather-phases — the tree was observed on " +
                     "\"DriveGatherStartup\", then \"DriveGatherActive\", then \"DriveGatherRecovery\", " +
                     "in that order.");
        else
            Fail($"drivegather-phases: expected DriveGatherStartup -> DriveGatherActive -> " +
                 $"DriveGatherRecovery, in order; got sawStartup={_sawStartup}, sawActive={_sawActive}, " +
                 $"sawRecovery={_sawRecovery}, sawGenericPlaceholder={_sawGenericPlaceholder}, " +
                 $"lastAnimNode={_actor.ActiveAnimNodeForHarness}.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: drivegather-startup-differs-from-recovery ──────────────────
    // #296's actual complaint: the generic fallback plays locomotion/idle for
    // BOTH, so the two phases are pixel-identical and an opponent cannot tell
    // "committing" from "in the punish window".
    private void VerdictStartupDiffersFromRecovery()
    {
        if (_poseAtLastStartupTick == null || _poseAtLastRecoveryTick == null)
        {
            Fail("drivegather-startup-differs-from-recovery: never sampled both a Startup and a Recovery " +
                 $"tick (sawStartup={_sawStartup}, sawRecovery={_sawRecovery}) — the premise for comparing " +
                 "them never held, so this fails rather than passes.");
            Finish(1);
            return;
        }

        float worst = 0f;
        for (int i = 0; i < _poseAtLastStartupTick.Length; i++)
            worst = Math.Max(worst, Mathf.RadToDeg(
                _poseAtLastStartupTick[i].AngleTo(_poseAtLastRecoveryTick[i])));

        GD.Print($"[drivegather-anim]   worst Startup-vs-Recovery bone delta = {worst:F2} deg " +
                 $"(floor {StartupVsRecoveryMinDeg:F1})");

        bool premise = _sawStartup && _sawRecovery && _startupTicks >= 2 && _recoveryTicks >= 2;
        bool pass = premise && worst >= StartupVsRecoveryMinDeg;
        if (pass)
            GD.Print("[drivegather-anim] PASS drivegather-startup-differs-from-recovery — the last Startup " +
                     $"pose and the last Recovery pose differ by {worst:F2} deg (#296).");
        else
            Fail($"drivegather-startup-differs-from-recovery: worst delta {worst:F2} deg < " +
                 $"{StartupVsRecoveryMinDeg:F1}, premise={premise} (startupTicks={_startupTicks}, " +
                 $"recoveryTicks={_recoveryTicks}, both need >= 2).");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: drivegather-clip-drives-the-rig (positive) ─────────────────
    private void VerdictClipDrivesTheRig()
    {
        bool premise = _sawStartup && _sawActive && _sawRecovery && _travelSamples >= 3;
        bool pass = premise && _poseTravelDeg >= DrivesRigMinDeg;
        GD.Print($"[drivegather-anim]   within-phase pose travel over {_travelSamples} deltas = " +
                 $"{_poseTravelDeg:F2} deg (floor {DrivesRigMinDeg:F1})");
        if (pass)
            GD.Print($"[drivegather-anim] PASS drivegather-clip-drives-the-rig — the rig travelled " +
                     $"{_poseTravelDeg:F2} deg of accumulated tick-to-tick rotation WITHIN phases, so the " +
                     "clips are bound and genuinely animating rather than holding a pose.");
        else
            Fail($"drivegather-clip-drives-the-rig: poseTravel={_poseTravelDeg:F4} deg over " +
                 $"{_travelSamples} within-phase deltas, need >= {DrivesRigMinDeg:F1} with >= 3 samples " +
                 $"(premise={premise}). Either the clips are unbound (README trap 13's silent no-op) or " +
                 "they hold a single pose. NOTE this gate does NOT claim the clips are the RIGHT ones — " +
                 "drivegather-no-placeholder-leak owns clip identity.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: drivegather-active-brings-both-hands-to-ball (positive) ────
    // THE RULES SIGNAL. The gather is the frame after which the dribble is dead,
    // so "both hands arrive on the ball" is the moment the holder's legal options
    // change — asserted rather than assumed.
    //
    // Wrist-to-wrist, not wrist-to-ball, and the distinction is deliberate. The
    // ball's position is BallController's business: it is attached by gameplay
    // state, not by this clip, so "the hands are on the ball" is not a claim a
    // cosmetic clip can make or a clip harness can honestly check. What the clip
    // does own — and what an opponent actually reads at a glance — is the two
    // hands arriving together in front of the body.
    private void VerdictActiveBringsBothHandsToBall()
    {
        GD.Print($"[drivegather-anim]   wrist gap at last usable Active tick = " +
                 $"{_wristGapAtLastActiveTick:F4} m (ceiling {HandsConvergedMaxM:F2})");

        bool premise = _sawActive && _activeTicks >= 2 && !float.IsNaN(_wristGapAtLastActiveTick);
        // Inverted comparison: every comparison against NaN is false, so writing
        // this as `gap > ceiling -> fail` would SKIP the gate on a poisoned
        // reading and print PASS while measuring nothing (#310 needed three such
        // guards and found the last only by mutation).
        bool pass = premise && _wristGapAtLastActiveTick <= HandsConvergedMaxM;
        if (pass)
            GD.Print($"[drivegather-anim] PASS drivegather-active-brings-both-hands-to-ball — the wrists " +
                     $"are {_wristGapAtLastActiveTick:F4} m apart at Active's last usable tick, i.e. both " +
                     "hands have arrived on the ball. The dribble is dead and the clip says so.");
        else
            Fail($"drivegather-active-brings-both-hands-to-ball: wrist gap " +
                 $"{_wristGapAtLastActiveTick:F4} m, need <= {HandsConvergedMaxM:F2}, premise={premise} " +
                 $"(activeTicks={_activeTicks}, need >= 2; NaN means a wrist bone did not resolve). " +
                 "Handoff 11: the off-hand must come clearly ONTO the ball, not near it — an ambiguous " +
                 "gather is the actively-false read MoveAnimState's own doc names.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: control-drivegather-startup-hands-apart (control) ──────────
    // The premise the scenario above needs, and it is not a formality: a
    // convergence ceiling on its own is satisfied by a clip whose hands were
    // NEVER apart — which is exactly what the generic locomotion/idle fallback
    // does, holding both arms in one fixed relationship for every phase. Without
    // this, #296's own defect would pass the convergence gate.
    private void VerdictControlStartupHandsApart()
    {
        GD.Print($"[drivegather-anim]   wrist gap at last usable Startup tick = " +
                 $"{_wristGapAtLastStartupTick:F4} m (floor {HandsApartMinM:F2}) " +
                 $"[Active-end reading, NOT this gate's subject: {_wristGapAtLastActiveTick:F4}]");

        bool premise = _sawStartup && _startupTicks >= 2 && !float.IsNaN(_wristGapAtLastStartupTick);
        bool pass = premise && _wristGapAtLastStartupTick >= HandsApartMinM;
        if (pass)
            GD.Print($"[drivegather-anim] PASS control-drivegather-startup-hands-apart — the wrists are " +
                     $"{_wristGapAtLastStartupTick:F4} m apart during Startup, a genuinely ONE-HANDED " +
                     "dribble. This is what makes the Active convergence mean \"they came together\" " +
                     "rather than \"they were never apart\".");
        else
            Fail($"control-drivegather-startup-hands-apart: wrist gap {_wristGapAtLastStartupTick:F4} m, " +
                 $"need >= {HandsApartMinM:F2}, premise={premise} (startupTicks={_startupTicks}, need " +
                 ">= 2). If this fails the convergence scenario is VACUOUS even when green.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: drivegather-active-displaces-forward (positive) ────────────
    // Compares Active's last usable tick against STARTUP'S OWN last usable tick,
    // not against the pre-move baseline. The baseline is the dribble CROUCH,
    // whose stance has no relationship to author_drivegather.py's own staggered
    // base spots, so simply ENTERING the move already produces a large jump in
    // this metric that has nothing to do with the drive. Comparing two named
    // phase instants isolates the phase's own event — the same discipline the
    // .gd rebuild tools' gates use, and the fix #306 had to make after an
    // earlier draft anchored on the baseline and passed on an inert clip.
    private void VerdictActiveDisplacesForward()
    {
        float delta = _hipsRelAtLastActiveTick - _hipsRelAtLastStartupTick;
        GD.Print($"[drivegather-anim]   hips-relative-to-trail-foot: startupEnd=" +
                 $"{_hipsRelAtLastStartupTick:F4} activeEnd={_hipsRelAtLastActiveTick:F4} " +
                 $"delta={delta:F4} (want >= {HipsForwardActiveMinM:F2})");

        bool premise = _sawStartup && _sawActive && _startupTicks >= 2 && _activeTicks >= 2
                       && !float.IsNaN(_hipsRelAtLastStartupTick)
                       && !float.IsNaN(_hipsRelAtLastActiveTick);
        bool pass = premise && delta >= HipsForwardActiveMinM;
        if (pass)
            GD.Print($"[drivegather-anim] PASS drivegather-active-displaces-forward — the " +
                     $"Hips-relative-to-trail-foot projection moved {delta:F4} m FORWARD from Startup's " +
                     $"own end pose to Active's last tick (floor {HipsForwardActiveMinM:F2}): the body " +
                     "drives out over the base it pushed off.");
        else
            Fail($"drivegather-active-displaces-forward: Startup-end -> Active-end delta was {delta:F4}, " +
                 $"need >= {HipsForwardActiveMinM:F2} (sawStartup={_sawStartup}, sawActive={_sawActive}, " +
                 $"startupTicks={_startupTicks}, activeTicks={_activeTicks}, both need >= 2). Either the " +
                 "clip is unbound (silent no-op, README trap 13) or the Active row's trail-foot drift " +
                 "regressed in author_drivegather.py. Remember the real 1.50 m of world displacement is " +
                 "PlayerController's, not this clip's.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: control-drivegather-startup-loads-back (control) ───────────
    // Non-vacuous and OPPOSITE IN SIGN, which is the point: a gather LOADS the
    // weight back over the rear leg before it explodes forward. Two floors that
    // both happen to pass would prove nothing about the two phases differing;
    // a sign reversal measured by the SAME code on the SAME metric does.
    //
    // Anchored on Startup's own second usable tick, not on the pre-move baseline
    // — that baseline is reported alongside for context but is deliberately not
    // the reference, because the Dribble -> Startup stance discontinuity would
    // swamp the clip's own authored load (mutation-proven in #306, where the
    // baseline-anchored version passed on an inert locomotion/idle).
    private void VerdictControlStartupLoadsBack()
    {
        float delta = _hipsRelAtLastStartupTick - _hipsRelAtFirstValidStartupTick;
        GD.Print($"[drivegather-anim]   hips-relative-to-trail-foot: startupFirstValid=" +
                 $"{_hipsRelAtFirstValidStartupTick:F4} startupEnd={_hipsRelAtLastStartupTick:F4} " +
                 $"delta={delta:F4} (want <= {-HipsBackStartupMinM:F2}) " +
                 $"[pre-move baseline, NOT the anchor: {_hipsRelBeforeMove:F4}]");

        // >= 3 so that dropping the lagged first tick still leaves two real
        // samples to difference. The pre-move baseline is still required
        // non-NaN because a poisoned measurement helper would otherwise go
        // unnoticed.
        bool premise = _sawStartup && _startupTicks >= 3
                       && !float.IsNaN(_hipsRelAtFirstValidStartupTick)
                       && !float.IsNaN(_hipsRelAtLastStartupTick)
                       && !float.IsNaN(_hipsRelBeforeMove);
        bool pass = premise && delta <= -HipsBackStartupMinM;
        if (pass)
            GD.Print($"[drivegather-anim] PASS control-drivegather-startup-loads-back — Startup's own " +
                     $"travel was {delta:F4} m BACKWARD (need <= {-HipsBackStartupMinM:F2}), the opposite " +
                     "sign from Active's claim. The gather sinks the weight before it goes, and the two " +
                     "phases genuinely differ rather than both clearing a one-sided floor.");
        else
            Fail($"control-drivegather-startup-loads-back: delta={delta:F4}, need <= " +
                 $"{-HipsBackStartupMinM:F2}, premise={premise} (startupTicks={_startupTicks}, need >= 3). " +
                 "If the premise broke, this fails rather than passes.");
        Finish(pass ? 0 : 1);
    }

    // ── Static scenarios: no live tree, pure resource/scene inspection ───────
    private void RunStaticCheck()
    {
        switch (_scenario)
        {
            case "drivegather-no-placeholder-leak":       RunNoPlaceholderLeakCheck(); break;
            case "drivegather-segment-lengths":            RunSegmentLengthsCheck(); break;
            case "drivegather-edges":                      RunEdgesCheck(); break;
            case "drivegather-recovery-hands-off-to-layup": RunHandoffCheck(); break;
        }
    }

    // ── Scenario: drivegather-segment-lengths ────────────────────────────────
    private void RunSegmentLengthsCheck()
    {
        var lib = GD.Load<AnimationLibrary>("res://assets/locomotion.res");
        if (lib == null)
        {
            Fail("assets/locomotion.res failed to load — cannot evaluate drivegather-segment-lengths.");
            Finish(1);
            return;
        }

        double tps = Engine.PhysicsTicksPerSecond;
        var frames = DriveGather.DefaultFrameData;
        const double ToleranceSeconds = 1e-3;

        (string Clip, int Ticks)[] windows =
        {
            ("drivegatherstartup",  frames.StartupFrames),
            ("drivegatheractive",   frames.ActiveFrames),
            ("drivegatherrecovery", frames.RecoveryFrames),
        };

        bool pass = true;
        foreach (var (clipName, ticks) in windows)
        {
            if (!lib.HasAnimation(clipName))
            {
                Fail($"AnimationLibrary has no clip '{clipName}' — run tools/rebuild_drivegather_clips.gd.");
                pass = false;
                continue;
            }

            double expectedSeconds = ticks / tps;
            double actualSeconds = lib.GetAnimation(clipName).Get("length").AsDouble();
            double deviationSeconds = Math.Abs(actualSeconds - expectedSeconds);
            GD.Print($"[drivegather-anim]   '{clipName}': length={actualSeconds:F6}s " +
                     $"expected={expectedSeconds:F6}s ({ticks} ticks @ {tps} tps), " +
                     $"deviation={deviationSeconds:F6}s");

            if (deviationSeconds > ToleranceSeconds)
            {
                Fail($"clip '{clipName}' is {actualSeconds:F6}s, expected {expectedSeconds:F6}s ({ticks} " +
                     $"ticks at {tps} tps — DriveGather.DefaultFrameData), a deviation of " +
                     $"{deviationSeconds:F6}s exceeds the float-noise tolerance ({ToleranceSeconds:F6}s). " +
                     "Re-run tools/rebuild_drivegather_clips.gd after retuning the move's frame data.");
                pass = false;
            }
        }

        if (pass)
            GD.Print("[drivegather-anim] PASS drivegather-segment-lengths — all three clips' durations " +
                     "match DriveGather.DefaultFrameData's windows to within float noise (#295).");
        else
            GD.PrintErr("[drivegather-anim] FAIL drivegather-segment-lengths — see per-clip deviations above.");

        Finish(pass ? 0 : 1);
    }

    // ── Scenario: drivegather-no-placeholder-leak ────────────────────────────
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
            ("DriveGatherStartup",  "locomotion/drivegatherstartup"),
            ("DriveGatherActive",   "locomotion/drivegatheractive"),
            ("DriveGatherRecovery", "locomotion/drivegatherrecovery"),
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
            GD.Print($"[drivegather-anim]   {stateName} -> {actualClip}");

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
            GD.Print("[drivegather-anim] PASS drivegather-no-placeholder-leak — all three DriveGather " +
                     "states point at their OWN per-move clips, not the shared locomotion/idle placeholder.");
        else
            GD.PrintErr("[drivegather-anim] FAIL drivegather-no-placeholder-leak — see per-state mismatches above.");

        Finish(pass ? 0 : 1);
    }

    // ── Scenario: drivegather-edges ──────────────────────────────────────────
    // README trap 8 / #279: a DELETED transition edge is INVISIBLE to
    // GetCurrentNode(), because Travel()'s pathfinder simply routes around the
    // gap. No runtime scenario can catch it — only this resource-level check
    // can, which is why edge coverage is asserted here and explicitly NOT
    // claimed by drivegather-phases.
    //
    // Drive-gather is a dribble-family offensive move, so it needs the six
    // standard edges AND the dribble-family entries/exits, the latter doubled by
    // #294's DribbleLeft/DribbleRight split — twelve in total, matching retreat
    // dribble's and step-back's shape.
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
            ("Locomotion", "DriveGatherStartup"),
            ("DriveGatherStartup", "DriveGatherActive"),
            ("DriveGatherActive", "DriveGatherRecovery"),
            ("DriveGatherRecovery", "Locomotion"),
            ("DriveGatherStartup", "DriveGatherRecovery"),  // feint / early-out (feintWindowFrames=0, kept for shape)
            ("DriveGatherStartup", "Locomotion"),           // abort
            ("DribbleLeft", "DriveGatherStartup"),
            ("DribbleRight", "DriveGatherStartup"),
            ("DriveGatherRecovery", "DribbleLeft"),
            ("DriveGatherRecovery", "DribbleRight"),
            ("DriveGatherStartup", "DribbleLeft"),
            ("DriveGatherStartup", "DribbleRight"),
        };

        var present = new System.Collections.Generic.HashSet<string>();
        for (int i = 0; i < sm.GetTransitionCount(); i++)
            present.Add($"{sm.GetTransitionFrom(i)}->{sm.GetTransitionTo(i)}");

        bool pass = true;
        foreach (var (from, to) in required)
        {
            bool here = present.Contains($"{from}->{to}");
            GD.Print($"[drivegather-anim]   edge {from} -> {to}: {(here ? "present" : "MISSING")}");
            if (!here)
            {
                Fail($"scenes/Player.tscn has no transition '{from}' -> '{to}'. Travel()'s pathfinder will " +
                     "route around the gap, so NO runtime scenario can catch this — only this " +
                     "resource-level check can.");
                pass = false;
            }
        }

        if (pass)
            GD.Print($"[drivegather-anim] PASS drivegather-edges — all {required.Length} required " +
                     "transitions are present (6 standard + 6 dribble-family, the latter doubled by #294's " +
                     "DribbleLeft/DribbleRight split).");
        else
            GD.PrintErr("[drivegather-anim] FAIL drivegather-edges — see missing transitions above.");

        Finish(pass ? 0 : 1);
    }

    // ── Scenario: drivegather-recovery-hands-off-to-layup (static) ───────────
    // PlayerController begins the finish as a SEPARATE "layup" request from the
    // displaced position (the comment at PlayerController.cs:2332, and EuroStep's
    // class doc), so DriveGatherRecovery -> LayupStartup genuinely occurs at
    // runtime — and every AnimationTree transition is a hard cut, so a large pose
    // discontinuity there SNAPS at the drive -> finish chain.
    //
    // Handoff 11 says whichever of #311/#313 lands second owns this assertion.
    // #313 landed first and did not take it, so it lives here. This is the SECOND
    // independent measurement of the claim (rebuild_drivegather_clips.gd's G6 is
    // the first; the Blender side cannot reach it at all, since layupstartup
    // comes from a source that script never loads). Re-implemented in C# rather
    // than sharing code with the .gd tool, so a bug in one path is unlikely to be
    // replicated in the other.
    private void RunHandoffCheck()
    {
        var lib = GD.Load<AnimationLibrary>("res://assets/locomotion.res");
        if (lib == null || !lib.HasAnimation("drivegatherrecovery") || !lib.HasAnimation("layupstartup"))
        {
            Fail("assets/locomotion.res missing 'drivegatherrecovery' or 'layupstartup' — cannot evaluate " +
                 "the hand-off.");
            Finish(1);
            return;
        }

        var playerScene = GD.Load<PackedScene>("res://scenes/Player.tscn");
        var inst = playerScene.Instantiate<Node3D>();
        Skeleton3D skel = FindSkeleton(inst);
        if (skel == null)
        {
            Fail("scenes/Player.tscn has no Skeleton3D — cannot FK the hand-off landmarks.");
            inst.QueueFree();
            Finish(1);
            return;
        }

        Animation recovery = lib.GetAnimation("drivegatherrecovery");
        Animation layupStartup = lib.GetAnimation("layupstartup");

        Vector3 hipsRecovery = PoseOrigin(skel, recovery, (float)recovery.Length, "mixamorig_Hips");
        Vector3 hipsLayup = PoseOrigin(skel, layupStartup, 0f, "mixamorig_Hips");
        if (float.IsNaN(hipsRecovery.X) || float.IsNaN(hipsLayup.X))
        {
            Fail("mixamorig_Hips did not resolve on the rig — the hand-off measurement has no reference frame.");
            inst.QueueFree();
            Finish(1);
            return;
        }

        float worst = 0f;
        string worstBone = "";
        foreach (string bone in HandoffLandmarkBones)
        {
            Vector3 pRecovery = PoseOrigin(skel, recovery, (float)recovery.Length, bone);
            Vector3 pLayup = PoseOrigin(skel, layupStartup, 0f, bone);
            if (float.IsNaN(pRecovery.X) || float.IsNaN(pLayup.X))
            {
                Fail($"landmark bone '{bone}' did not resolve on the rig — poisoned rather than treated as zero.");
                inst.QueueFree();
                Finish(1);
                return;
            }
            Vector3 relRecovery = pRecovery - hipsRecovery;
            Vector3 relLayup = pLayup - hipsLayup;
            float jump = relRecovery.DistanceTo(relLayup);
            GD.Print($"[drivegather-anim]   {bone,-24} jump={jump:F4} m");
            if (jump > worst)
            {
                worst = jump;
                worstBone = bone;
            }
        }

        // Measured RELATIVE TO EACH CLIP'S OWN HIPS above, which cancels the
        // coordinate-anchor mismatch between two clips authored off different
        // source FBXs. The Hips landmark is trivially zero against itself, so its
        // own vertical settle is checked separately in world Y — a real hip-height
        // difference at the cut IS a visible pop.
        float hipHeightJump = Mathf.Abs(hipsRecovery.Y - hipsLayup.Y);
        GD.Print($"[drivegather-anim]   mixamorig_Hips(height)      jump={hipHeightJump:F4} m (world Y only)");
        if (hipHeightJump > worst)
        {
            worst = hipHeightJump;
            worstBone = "mixamorig_Hips(height)";
        }

        inst.QueueFree();

        GD.Print($"[drivegather-anim]   worst hand-off landmark jump = {worst:F4} m ({worstBone}, want <= " +
                 $"{RecoveryLayupHandoffMaxM:F2})");
        bool pass = worst <= RecoveryLayupHandoffMaxM;
        if (pass)
            GD.Print($"[drivegather-anim] PASS drivegather-recovery-hands-off-to-layup — the worst hand-off " +
                     $"landmark ({worstBone}) jumps {worst:F4} m, within the {RecoveryLayupHandoffMaxM:F2} m " +
                     "ceiling. NOTE the dominant term is a PRE-EXISTING cross-source hip offset that #311 " +
                     "neither introduced nor can fix — layupstartup is authored off Goalkeeper Catch " +
                     "Stationary.fbx while every Dribble.fbx-sourced clip in this batch sits ~0.19 m lower. " +
                     "See rebuild_drivegather_clips.gd's G6 comment and #311's PR.");
        else
            Fail($"drivegather-recovery-hands-off-to-layup: {worstBone} jumped {worst:F4} m (> " +
                 $"{RecoveryLayupHandoffMaxM:F2}). Every AnimationTree transition is a hard cut, so this " +
                 "SNAPS at the drive -> finish chain. Retune drivegatherrecovery's final keypose in " +
                 "author_drivegather.py — it is already authored at layupstartup's own measured opening " +
                 "pose, so a regression here means one of the two clips moved.");
        Finish(pass ? 0 : 1);
    }

    // FK a single bone's origin at time `t` in `anim`, walking the parent chain
    // via `skel`'s bone_rest, mirroring rebuild_drivegather_clips.gd's
    // `_pose_origin` — a SEPARATE implementation (not shared code) so the two act
    // as independent proofs of the same claim. Returns NaN (poisoned, not
    // Vector3.Zero) if the bone does not resolve: a Zero fallback would make an
    // unresolvable bone read as "no jump" and print PASS while measuring nothing
    // (mutation-proven in #305).
    private static Vector3 PoseOrigin(Skeleton3D skel, Animation anim, float t, string boneName)
    {
        int idx = skel.FindBone(boneName);
        if (idx < 0) return new Vector3(float.NaN, float.NaN, float.NaN);

        var rotTrackOf = new System.Collections.Generic.Dictionary<int, int>();
        var posTrackOf = new System.Collections.Generic.Dictionary<int, int>();
        for (int i = 0; i < anim.GetTrackCount(); i++)
        {
            Animation.TrackType ty = anim.TrackGetType(i);
            if (ty != Animation.TrackType.Rotation3D && ty != Animation.TrackType.Position3D) continue;
            NodePath path = anim.TrackGetPath(i);
            if (path.GetSubNameCount() == 0) continue;
            int b = skel.FindBone(path.GetSubName(0));
            if (b < 0) continue;
            if (ty == Animation.TrackType.Rotation3D) rotTrackOf[b] = i;
            else posTrackOf[b] = i;
        }

        var chain = new System.Collections.Generic.List<int>();
        int walk = idx;
        while (walk >= 0)
        {
            chain.Insert(0, walk);
            walk = skel.GetBoneParent(walk);
        }

        Transform3D acc = Transform3D.Identity;
        foreach (int b in chain)
        {
            Transform3D rest = skel.GetBoneRest(b);
            Transform3D local = rest;
            if (rotTrackOf.TryGetValue(b, out int rIdx))
            {
                Quaternion q = anim.RotationTrackInterpolate(rIdx, t);
                local = new Transform3D(new Basis(q).Scaled(rest.Basis.Scale), rest.Origin);
            }
            if (posTrackOf.TryGetValue(b, out int pIdx))
                local.Origin = anim.PositionTrackInterpolate(pIdx, t);
            acc *= local;
        }
        return acc.Origin;
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

    // Straight-line distance between the two wrists on the LIVE rig. Returns NaN
    // — never 0 — if a bone does not resolve: a zero fallback would read as
    // "perfectly converged" and turn the rules-signal gate into a guaranteed
    // PASS that measures nothing.
    private static float MeasureWristGap(Skeleton3D skel)
    {
        int l = skel.FindBone("mixamorig_LeftHand");
        int r = skel.FindBone("mixamorig_RightHand");
        if (l < 0 || r < 0) return float.NaN;
        return skel.GetBoneGlobalPose(l).Origin.DistanceTo(skel.GetBoneGlobalPose(r).Origin);
    }

    // The Hips bone's origin MINUS the TRAIL (rear, LEFT) foot's origin,
    // projected along the rig's own forward axis (cached from the pre-move
    // LeftFoot->LeftToeBase vector — the actor's whole-body orientation is frozen
    // for a committed move's duration, so one derivation is exact for the run).
    //
    // POSITIVE means the hips sit ahead of the trail foot; NEGATIVE means behind
    // it. See the class doc for why this, and not raw Hips translation, is the
    // honest live-rig proof that the body travelled: the clip is authored IN
    // PLACE, so the Hips bone deliberately has no fore/aft motion to read.
    //
    // Returns NaN rather than 0 on an unresolved bone, for the same reason
    // MeasureWristGap does — and every caller checks it, because a 0 would read
    // as "no displacement" and quietly fail the positive gate while quietly
    // PASSING the control.
    private float MeasureHipsRelativeToTrailFoot(Skeleton3D skel)
    {
        int hips = skel.FindBone("mixamorig_Hips");
        int trailFoot = skel.FindBone("mixamorig_LeftFoot");
        if (hips < 0 || trailFoot < 0) return float.NaN;

        if (_cachedForward == null)
        {
            int toe = skel.FindBone("mixamorig_LeftToeBase");
            if (toe < 0) return float.NaN;
            Vector3 raw = skel.GetBoneGlobalPose(toe).Origin - skel.GetBoneGlobalPose(trailFoot).Origin;
            raw.Y = 0f;
            if (raw.LengthSquared() < 1e-6f) return float.NaN;
            _cachedForward = raw.Normalized();
        }

        Vector3 hipsPos = skel.GetBoneGlobalPose(hips).Origin;
        Vector3 trailFootPos = skel.GetBoneGlobalPose(trailFoot).Origin;
        return (hipsPos - trailFootPos).Dot(_cachedForward.Value);
    }

    private static Quaternion[] SampleComparedBones(Skeleton3D skel)
    {
        var poses = new Quaternion[ComparedBones.Length];
        for (int i = 0; i < ComparedBones.Length; i++)
        {
            int idx = skel.FindBone(ComparedBones[i]);
            poses[i] = idx < 0
                ? Quaternion.Identity
                : skel.GetBonePose(idx).Basis.GetRotationQuaternion().Normalized();
        }
        return poses;
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

    private void Fail(string message) => GD.PrintErr($"[drivegather-anim] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[drivegather-anim] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
