using System;
using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #306 (ADR-0016): proves the THREE
// STEP-BACK ANIMATION STATES (StepBackStartup / StepBackActive /
// StepBackRecovery) wired into scenes/Player.tscn are real — entered
// end-to-end by a real StepBack, bound to the right clips, cut to the right
// windows, and actually MOVING the rig.
//
// Before #306 "stepback" fell through MoveAnimResolver.ResolveStateName's
// default case onto the shared generic Startup/Active/Recovery states, which
// per #296 render a 7-tick LOOPING IDLE for Startup (StepBack.cs's own class
// doc: "the biggest separation move telegraphs the longest" — the game's
// most deliberately-telegraphed move rendering as an idle) and a looping
// SPRINT for Active while the player bursts BACKWARD — an actively false
// read, not merely a missing one.
//
//   godot --headless --path . res://tests/integration/StepBackAnimTest.tscn -- --harness-scenario=stepback-phases
//   …=stepback-no-placeholder-leak | stepback-segment-lengths | stepback-edges
//   …=stepback-startup-differs-from-recovery
//   …=stepback-active-displaces-back | control-stepback-startup-displaces-forward
//   …=stepback-recovery-hands-off-to-jumpshot
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//
// ── moveId IS "stepback" ─────────────────────────────────────────────────
// StepBack.cs:95 constructs with `id: "stepback"`, so the ClippedMovePrefixes
// key and the moveId coincide, same as retreatdribble.
//
// ── Setup mirrors StepBackTest.cs's own "step-back-gathers" scenario ───────
// A live dribble, then BeginStepBackForHarness() (StepBackHarnessSeam.cs) —
// the SAME production entry point StepBackTest already exercises, downstream
// of every gate that test owns. This harness is COSMETIC-ONLY (#306's
// standing constraint): it never observes or feeds BallState, HasDribbled,
// StepBackBurstSpeed, or StepBackExitConeDegrees — those remain
// StepBackTest's `step-back-gathers` scenario's job, and it stays green
// throughout this issue.
//
// ── Why ActiveAnimNodeForHarness, not the resolver's return value (#257) ────
// Travel() to a missing/misnamed state only LOGS; it never throws. Only the
// live AnimationNodeStateMachinePlayback proves wiring.
//
// ── "Hips displace back" is measured RELATIVE TO THE TRAIL FOOT, not as
// root translation ─────────────────────────────────────────────────────────
// author_stepback.py's own `_verify_hips_stay_in_place` (reused verbatim
// from retreat dribble) proves the Hips bone's WORLD/CLIP-SPACE position
// moves along `up` ONLY — never fore/aft. That is deliberate: the game
// already applies the real backward burst via Velocity on JustEnteredActive
// (StepBackBurstMath), so a clip that ALSO translates the Hips plays the
// burst twice. The clip instead depicts "hips travel back" by moving the
// FEET forward relative to a vertically-anchored Hips — the body's base is
// left behind. So THIS scenario measures the same claim from the opposite
// side of that same relationship: Hips position RELATIVE TO the TRAIL
// (rear, LEFT) foot specifically, projected along the rig's own forward
// axis — not an average of both feet (measured, not assumed: an average
// washed out to near-zero during Startup, because the LEAD foot advancing
// and the TRAIL foot retreating largely cancel each other; see
// `MeasureHipsRelativeToFeet`'s own doc for the full reasoning). The trail
// foot's own trajectory genuinely reverses between the two phases — it
// retreats slightly during Startup (unweighting as the plant commits onto
// the lead foot) then swings sharply forward during Active (kicking through
// as the exploding body leaves it behind) — which is what gives this
// scenario pair a real, large-margin, non-vacuous opposite-sign contrast.
// `stepback-active-displaces-back` compares Active's end against STARTUP'S
// OWN end pose (isolating the burst from the entry-stance jump against the
// pre-move dribble crouch); the control compares Startup's end against the
// pre-move baseline (a fair comparison there, since Startup's own arc is
// what is being measured).
public partial class StepBackAnimTest : Node
{
    private const double TimeoutSeconds = 15.0;
    private const int ArmFrames = 2;      // ticks for TryAssignTipoffHolder to run
    private const int ActFrames = 3;      // ticks after tipoff before Begin (StepBackTest's own ActionMarginFrames)
    // startup(7)+active(4)+recovery(8)=19 ticks, generous slack.
    private const int ObserveFrames = 45;

    private static readonly Vector3 ActorSpot = new(0f, 0f, 2f);
    private static readonly Vector3 FarSpot = new(12f, 0f, 12f); // out of PickupRadius
    private static readonly Vector3 RimCenter = new(0f, 3.05f, 0f); // BallController.DefaultRimCenter

    // Bones for the Startup-vs-Recovery pose comparison. Extends down the
    // whole leg chain (like retreat dribble/jab step's own sets) because this
    // move's read is a WHOLE-BASE event.
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

    // Landmarks for the recovery -> jumpshot hand-off gate, matching
    // rebuild_stepback_clips.gd's G6 exactly (same list, same "relative to
    // Hips" metric) — this is the THIRD independent re-measurement of that
    // claim (Blender authoring time cannot reach it at all, since
    // jumpshotstartup lives in a different source pipeline; the GDScript
    // rebuild tool is the second). Re-implemented here in C# against the
    // Godot API directly, rather than sharing code with the .gd tool, so a
    // bug in one measurement path is unlikely to be replicated in the other.
    private static readonly string[] HandoffLandmarkBones =
    {
        "mixamorig_LeftHand", "mixamorig_RightHand", "mixamorig_Head",
        "mixamorig_LeftFoot", "mixamorig_RightFoot",
    };

    // ── Thresholds ──────────────────────────────────────────────────────────
    // EVERY floor below is set from a value MEASURED BY THIS HARNESS ON THE
    // LIVE RIG (RetreatDribbleAnimTest's own documented discipline — the
    // Blender/resource/live-rig spaces disagree on absolute readings, so
    // every gate here is either a RELATIVE claim off a pre-move baseline or
    // re-measured fresh in this frame).

    // MEASURED on the live rig (see the PR for the exact figure) — matches
    // rebuild_stepback_clips.gd's own STARTUP_VS_RECOVERY_MIN_DEG=15.0 floor.
    private const float StartupVsRecoveryMinDeg = 15.0f;

    // The pre-move baseline -> Active-end delta of (Hips - avgFoot).dot(forward)
    // must go NEGATIVE by at least this much (hips effectively behind the
    // base). Floor is deliberately small — this gate exists to catch a SIGN
    // error or a dead/unbound clip, not to demand a specific magnitude (same
    // discipline as rebuild_stepback_clips.gd's G4-style gates elsewhere in
    // this batch).
    private const float HipsBehindBaseActiveMinM = 0.02f;

    // The control's margin: Startup's own travel (toward MORE POSITIVE,
    // i.e. hips ahead of the base) must clear this floor in the OPPOSITE
    // direction from Active's claim — a non-vacuous premise "free from the
    // motion spec" per the issue.
    private const float HipsAheadOfBaseStartupMinM = 0.01f;

    // Recovery -> Jumpshot hand-off (the ADR-0014 legibility call — see the
    // PR for the full reasoning). Matches rebuild_stepback_clips.gd's
    // RECOVERY_JUMPSHOT_HANDOFF_MAX_M — this is the third, live-rig, C#-side
    // re-measurement of the SAME quantity, not a shared constant, so a bug in
    // either implementation is unlikely to be replicated in the other.
    private const float RecoveryJumpshotHandoffMaxM = 0.45f;

    private static readonly string[] KnownScenarios =
    {
        "stepback-phases",
        "stepback-no-placeholder-leak",
        "stepback-segment-lengths",
        "stepback-edges",
        "stepback-startup-differs-from-recovery",
        "stepback-active-displaces-back",
        "control-stepback-startup-displaces-forward",
        "stepback-recovery-hands-off-to-jumpshot",
    };

    // Scenarios that need no live tree — pure resource/scene inspection.
    private static readonly string[] StaticScenarios =
    {
        "stepback-no-placeholder-leak",
        "stepback-segment-lengths",
        "stepback-edges",
        "stepback-recovery-hands-off-to-jumpshot",
    };

    private string _scenario = "stepback-phases";

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

    // Per-phase observed-tick counts. The #316/#340 trap: the first tick
    // GetCurrentNode() names a phase can still hold the PREVIOUS phase's
    // pose, so a phase must be observed for MORE THAN ONE tick before its
    // "last tick" reading can be trusted.
    private int _startupTicks;
    private int _activeTicks;
    private int _recoveryTicks;

    private Vector3? _cachedForward;
    private float _hipsRelBeforeMove = float.NaN;     // sampled one tick before Begin
    private float _hipsRelAtLastStartupTick = float.NaN;
    private float _hipsRelAtLastActiveTick = float.NaN;
    private Quaternion[] _poseAtLastStartupTick;
    private Quaternion[] _poseAtLastRecoveryTick;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "stepback-phases");
        GD.Print($"[stepback-anim] scenario={_scenario} booting headless…");

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
                // Mirrors StepBackTest.cs's own "step-back-gathers" setup: a
                // live dribble first, THEN StepBack. StepBackHarnessSeam's
                // BeginStepBackForHarness() reaches the same
                // BeginCommittedMove production code the "shoot" input path
                // uses.
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
                // Sample the PRE-MOVE baseline one tick before Begin, while
                // the actor is unambiguously still in the Dribble stance —
                // the one point in this run with no tick-lag ambiguity about
                // which pose is being read (#316's trap).
                {
                    var skelPre = FindSkeleton(_actor);
                    if (skelPre != null)
                        _hipsRelBeforeMove = MeasureHipsRelativeToFeet(skelPre);
                }
                if (!_actor.BeginStepBackForHarness())
                {
                    Fail($"{_scenario}: BeginStepBackForHarness() returned false — the actor's machine was " +
                         $"not Inactive, or a begin gate rejected it. Ball state = {_ball?.State}.");
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

        if (!_sawStartup && node == "StepBackStartup") _sawStartup = true;
        if (_sawStartup && !_sawActive && node == "StepBackActive") _sawActive = true;
        if (_sawActive && !_sawRecovery && node == "StepBackRecovery") _sawRecovery = true;
        if (node == "Startup" || node == "Active" || node == "Recovery") _sawGenericPlaceholder = true;

        var skel = FindSkeleton(_actor);
        if (skel == null) return;

        if (node != "StepBackStartup" && node != "StepBackActive" && node != "StepBackRecovery") return;

        float hipsRel = MeasureHipsRelativeToFeet(skel);

        if (node == "StepBackStartup")
        {
            _startupTicks++;
            _hipsRelAtLastStartupTick = hipsRel;
            _poseAtLastStartupTick = SampleComparedBones(skel);
        }
        else if (node == "StepBackActive")
        {
            _activeTicks++;
            _hipsRelAtLastActiveTick = hipsRel;
        }
        else
        {
            _recoveryTicks++;
            _poseAtLastRecoveryTick = SampleComparedBones(skel);
        }
    }

    private void RenderVerdict()
    {
        GD.Print($"[stepback-anim]   observed ticks: startup={_startupTicks} " +
                 $"active={_activeTicks} recovery={_recoveryTicks}");
        switch (_scenario)
        {
            case "stepback-phases":                             VerdictPhases(); break;
            case "stepback-startup-differs-from-recovery":      VerdictStartupDiffersFromRecovery(); break;
            case "stepback-active-displaces-back":              VerdictActiveDisplacesBack(); break;
            case "control-stepback-startup-displaces-forward":  VerdictControlStartupDisplacesForward(); break;
        }
    }

    // ── Scenario: stepback-phases (positive) ─────────────────────────────
    private void VerdictPhases()
    {
        bool pass = _sawStartup && _sawActive && _sawRecovery;
        if (pass)
            GD.Print("[stepback-anim] PASS stepback-phases — the tree was observed on \"StepBackStartup\", " +
                     "then \"StepBackActive\", then \"StepBackRecovery\", in that order.");
        else
            Fail($"stepback-phases: expected StepBackStartup -> StepBackActive -> StepBackRecovery, in order; " +
                 $"got sawStartup={_sawStartup}, sawActive={_sawActive}, sawRecovery={_sawRecovery}, " +
                 $"sawGenericPlaceholder={_sawGenericPlaceholder}, lastAnimNode={_actor.ActiveAnimNodeForHarness}.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: stepback-startup-differs-from-recovery ─────────────────
    private void VerdictStartupDiffersFromRecovery()
    {
        if (_poseAtLastStartupTick == null || _poseAtLastRecoveryTick == null)
        {
            Fail("stepback-startup-differs-from-recovery: never sampled both a Startup and a Recovery tick " +
                 $"(sawStartup={_sawStartup}, sawRecovery={_sawRecovery}) — the premise for comparing them " +
                 "never held, so this fails rather than passes.");
            Finish(1);
            return;
        }

        float worst = 0f;
        for (int i = 0; i < _poseAtLastStartupTick.Length; i++)
            worst = Math.Max(worst, Mathf.RadToDeg(
                _poseAtLastStartupTick[i].AngleTo(_poseAtLastRecoveryTick[i])));

        GD.Print($"[stepback-anim]   worst Startup-vs-Recovery bone delta = {worst:F2} deg " +
                 $"(floor {StartupVsRecoveryMinDeg:F1})");

        bool premise = _sawStartup && _sawRecovery && _startupTicks >= 2 && _recoveryTicks >= 2;
        bool pass = premise && worst >= StartupVsRecoveryMinDeg;
        if (pass)
            GD.Print("[stepback-anim] PASS stepback-startup-differs-from-recovery — the last Startup pose and " +
                     $"the last Recovery pose differ by {worst:F2} deg (#296).");
        else
            Fail($"stepback-startup-differs-from-recovery: worst delta {worst:F2} deg < {StartupVsRecoveryMinDeg:F1}, " +
                 $"premise={premise} (startupTicks={_startupTicks}, recoveryTicks={_recoveryTicks}, both need >= 2).");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: stepback-active-displaces-back (positive) ──────────────
    // See the class doc's "Hips displace back is measured relative to the
    // feet" section. Compares against `_hipsRelAtLastStartupTick` — Active's
    // OWN entry pose — rather than the pre-move dribble baseline.
    //
    // Measured, not assumed: an earlier draft compared against the pre-move
    // baseline (matching RetreatDribbleAnimTest's own convention) and it was
    // WRONG for this move. The pre-move baseline is the dribble CROUCH, whose
    // trail-foot stance has no relationship to author_stepback.py's own
    // staggered STANCE_HALF_DEPTH_M base spots, so simply ENTERING the move
    // (frame 0, before anything phase-specific happens) already produces a
    // large jump in this metric that has nothing to do with Active's burst.
    // Comparing Active's end against Startup's own end pose isolates the
    // burst itself, uncontaminated by that entry-stance discontinuity —
    // exactly the "compare named phase instants, not whole-clip endpoints"
    // discipline the .gd rebuild scripts' G3 gates already use.
    private void VerdictActiveDisplacesBack()
    {
        float delta = _hipsRelAtLastActiveTick - _hipsRelAtLastStartupTick;
        GD.Print($"[stepback-anim]   hips-relative-to-trail-foot: startupEnd={_hipsRelAtLastStartupTick:F4} " +
                 $"activeEnd={_hipsRelAtLastActiveTick:F4} delta={delta:F4} (want <= {-HipsBehindBaseActiveMinM:F2})");

        bool premise = _sawStartup && _sawActive && _startupTicks >= 2 && _activeTicks >= 2
                       && !float.IsNaN(_hipsRelAtLastStartupTick);
        bool pass = premise && delta <= -HipsBehindBaseActiveMinM;
        if (pass)
            GD.Print("[stepback-anim] PASS stepback-active-displaces-back — the Hips-relative-to-trail-foot " +
                     $"projection moved {delta:F4} m BACKWARD from Startup's own end pose to Active's last tick " +
                     $"(floor {-HipsBehindBaseActiveMinM:F2}): the explosive burst leaves the base behind.");
        else
            Fail($"stepback-active-displaces-back: Startup-end -> Active-end delta was {delta:F4}, need <= " +
                 $"{-HipsBehindBaseActiveMinM:F2} (sawStartup={_sawStartup}, sawActive={_sawActive}, " +
                 $"startupTicks={_startupTicks}, activeTicks={_activeTicks} (both need >= 2)). Either the clip " +
                 "is unbound (silent no-op, README trap 13) or the Active row's foot-drift channels regressed " +
                 "in author_stepback.py.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: control-stepback-startup-displaces-forward (control) ───
    // Non-vacuous, free from the motion spec: Startup's own "sell the drive"
    // lie plants the lead foot FORWARD under the body, which is the OPPOSITE
    // sign from Active's claim.
    private void VerdictControlStartupDisplacesForward()
    {
        float delta = _hipsRelAtLastStartupTick - _hipsRelBeforeMove;
        GD.Print($"[stepback-anim]   hips-relative-to-feet: beforeMove={_hipsRelBeforeMove:F4} " +
                 $"startupEnd={_hipsRelAtLastStartupTick:F4} delta={delta:F4} (want >= {HipsAheadOfBaseStartupMinM:F2})");

        bool premise = _sawStartup && _startupTicks >= 2 && !float.IsNaN(_hipsRelBeforeMove);
        bool pass = premise && delta >= HipsAheadOfBaseStartupMinM;
        if (pass)
            GD.Print("[stepback-anim] PASS control-stepback-startup-displaces-forward — Startup's own travel " +
                     $"was {delta:F4} m FORWARD (floor {HipsAheadOfBaseStartupMinM:F2}), the opposite sign from " +
                     "Active's claim — a non-vacuous premise showing the two phases genuinely differ, not just " +
                     "the two floors happening to both pass.");
        else
            Fail($"control-stepback-startup-displaces-forward: delta={delta:F4}, need >= " +
                 $"{HipsAheadOfBaseStartupMinM:F2}, premise={premise} (startupTicks={_startupTicks}, need >= 2). " +
                 "If the premise broke, this fails rather than passes.");
        Finish(pass ? 0 : 1);
    }

    // ── Static scenarios: no live tree, pure resource/scene inspection ────
    private void RunStaticCheck()
    {
        switch (_scenario)
        {
            case "stepback-no-placeholder-leak":            RunNoPlaceholderLeakCheck(); break;
            case "stepback-segment-lengths":                 RunSegmentLengthsCheck(); break;
            case "stepback-edges":                           RunEdgesCheck(); break;
            case "stepback-recovery-hands-off-to-jumpshot":  RunHandoffCheck(); break;
        }
    }

    // ── Scenario: stepback-segment-lengths ────────────────────────────────
    private void RunSegmentLengthsCheck()
    {
        var lib = GD.Load<AnimationLibrary>("res://assets/locomotion.res");
        if (lib == null)
        {
            Fail("assets/locomotion.res failed to load — cannot evaluate stepback-segment-lengths.");
            Finish(1);
            return;
        }

        double tps = Engine.PhysicsTicksPerSecond;
        var frames = StepBack.DefaultFrameData;
        const double ToleranceSeconds = 1e-3;

        (string Clip, int Ticks)[] windows =
        {
            ("stepbackstartup",  frames.StartupFrames),
            ("stepbackactive",   frames.ActiveFrames),
            ("stepbackrecovery", frames.RecoveryFrames),
        };

        bool pass = true;
        foreach (var (clipName, ticks) in windows)
        {
            if (!lib.HasAnimation(clipName))
            {
                Fail($"AnimationLibrary has no clip '{clipName}' — run tools/rebuild_stepback_clips.gd.");
                pass = false;
                continue;
            }

            double expectedSeconds = ticks / tps;
            double actualSeconds = lib.GetAnimation(clipName).Get("length").AsDouble();
            double deviationSeconds = Math.Abs(actualSeconds - expectedSeconds);
            GD.Print($"[stepback-anim]   '{clipName}': length={actualSeconds:F6}s expected={expectedSeconds:F6}s " +
                     $"({ticks} ticks @ {tps} tps), deviation={deviationSeconds:F6}s");

            if (deviationSeconds > ToleranceSeconds)
            {
                Fail($"clip '{clipName}' is {actualSeconds:F6}s, expected {expectedSeconds:F6}s ({ticks} ticks " +
                     $"at {tps} tps — StepBack.DefaultFrameData), a deviation of {deviationSeconds:F6}s exceeds " +
                     $"the float-noise tolerance ({ToleranceSeconds:F6}s). Re-run tools/rebuild_stepback_clips.gd " +
                     "after retuning the move's frame data.");
                pass = false;
            }
        }

        if (pass)
            GD.Print("[stepback-anim] PASS stepback-segment-lengths — all three clips' durations match " +
                     "StepBack.DefaultFrameData's windows to within float noise.");
        else
            GD.PrintErr("[stepback-anim] FAIL stepback-segment-lengths — see per-clip deviations above.");

        Finish(pass ? 0 : 1);
    }

    // ── Scenario: stepback-no-placeholder-leak ─────────────────────────────
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
            ("StepBackStartup",  "locomotion/stepbackstartup"),
            ("StepBackActive",   "locomotion/stepbackactive"),
            ("StepBackRecovery", "locomotion/stepbackrecovery"),
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
            GD.Print($"[stepback-anim]   {stateName} -> {actualClip}");

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
            GD.Print("[stepback-anim] PASS stepback-no-placeholder-leak — all three StepBack states point at " +
                     "their OWN per-move clips, not the shared locomotion/idle placeholder.");
        else
            GD.PrintErr("[stepback-anim] FAIL stepback-no-placeholder-leak — see per-state mismatches above.");

        Finish(pass ? 0 : 1);
    }

    // ── Scenario: stepback-edges ────────────────────────────────────────────
    // README trap 8 / #279: a DELETED transition edge is invisible to
    // GetCurrentNode(). Step-back is a dribble-family offensive move, so it
    // needs the six standard edges AND the DribbleLeft/DribbleRight-doubled
    // dribble-family entries/exits (#294), matching retreat dribble's own
    // 12-edge shape.
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
            ("Locomotion", "StepBackStartup"),
            ("StepBackStartup", "StepBackActive"),
            ("StepBackActive", "StepBackRecovery"),
            ("StepBackRecovery", "Locomotion"),
            ("StepBackStartup", "StepBackRecovery"),   // feint / abort path (feintWindowFrames=0, kept for shape)
            ("StepBackStartup", "Locomotion"),          // abort
            ("DribbleLeft", "StepBackStartup"),
            ("DribbleRight", "StepBackStartup"),
            ("StepBackRecovery", "DribbleLeft"),
            ("StepBackRecovery", "DribbleRight"),
            ("StepBackStartup", "DribbleLeft"),
            ("StepBackStartup", "DribbleRight"),
        };

        var present = new System.Collections.Generic.HashSet<string>();
        for (int i = 0; i < sm.GetTransitionCount(); i++)
            present.Add($"{sm.GetTransitionFrom(i)}->{sm.GetTransitionTo(i)}");

        bool pass = true;
        foreach (var (from, to) in required)
        {
            bool here = present.Contains($"{from}->{to}");
            GD.Print($"[stepback-anim]   edge {from} -> {to}: {(here ? "present" : "MISSING")}");
            if (!here)
            {
                Fail($"scenes/Player.tscn has no transition '{from}' -> '{to}'. Travel()'s pathfinder will " +
                     "route around the gap, so NO runtime scenario can catch this — only this resource-level " +
                     "check can.");
                pass = false;
            }
        }

        if (pass)
            GD.Print($"[stepback-anim] PASS stepback-edges — all {required.Length} required transitions are " +
                     "present (6 standard + 6 dribble-family, the latter doubled by #294's DribbleLeft/DribbleRight split).");
        else
            GD.PrintErr("[stepback-anim] FAIL stepback-edges — see missing transitions above.");

        Finish(pass ? 0 : 1);
    }

    // ── Scenario: stepback-recovery-hands-off-to-jumpshot (static) ─────────
    // The third independent re-measurement of the recovery -> jumpshot
    // hand-off (see the class doc and rebuild_stepback_clips.gd's G6). Pure
    // resource inspection: loads assets/locomotion.res AND
    // scenes/Player.tscn's Skeleton3D (for bone_rest/parent structure, i.e.
    // FK only — no live committed-move machine involved), independently
    // re-implemented in C# rather than sharing code with the .gd tool.
    private void RunHandoffCheck()
    {
        var lib = GD.Load<AnimationLibrary>("res://assets/locomotion.res");
        if (lib == null || !lib.HasAnimation("stepbackrecovery") || !lib.HasAnimation("jumpshotstartup"))
        {
            Fail("assets/locomotion.res missing 'stepbackrecovery' or 'jumpshotstartup' — cannot evaluate the hand-off.");
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

        Animation recovery = lib.GetAnimation("stepbackrecovery");
        Animation jumpshotStartup = lib.GetAnimation("jumpshotstartup");

        Vector3 hipsRecovery = PoseOrigin(skel, recovery, (float)recovery.Length, "mixamorig_Hips");
        Vector3 hipsJumpshot = PoseOrigin(skel, jumpshotStartup, 0f, "mixamorig_Hips");
        if (float.IsNaN(hipsRecovery.X) || float.IsNaN(hipsJumpshot.X))
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
            Vector3 pJumpshot = PoseOrigin(skel, jumpshotStartup, 0f, bone);
            if (float.IsNaN(pRecovery.X) || float.IsNaN(pJumpshot.X))
            {
                Fail($"landmark bone '{bone}' did not resolve on the rig — poisoned rather than treated as zero.");
                inst.QueueFree();
                Finish(1);
                return;
            }
            Vector3 relRecovery = pRecovery - hipsRecovery;
            Vector3 relJumpshot = pJumpshot - hipsJumpshot;
            float jump = relRecovery.DistanceTo(relJumpshot);
            GD.Print($"[stepback-anim]   {bone,-24} jump={jump:F4} m");
            if (jump > worst)
            {
                worst = jump;
                worstBone = bone;
            }
        }

        float hipHeightJump = Mathf.Abs(hipsRecovery.Y - hipsJumpshot.Y);
        GD.Print($"[stepback-anim]   mixamorig_Hips(height)      jump={hipHeightJump:F4} m (world Y only)");
        if (hipHeightJump > worst)
        {
            worst = hipHeightJump;
            worstBone = "mixamorig_Hips(height)";
        }

        inst.QueueFree();

        GD.Print($"[stepback-anim]   worst hand-off landmark jump = {worst:F4} m ({worstBone}, want <= " +
                 $"{RecoveryJumpshotHandoffMaxM:F2})");
        bool pass = worst <= RecoveryJumpshotHandoffMaxM;
        if (pass)
            GD.Print("[stepback-anim] PASS stepback-recovery-hands-off-to-jumpshot — the worst hand-off " +
                     $"landmark ({worstBone}) jumps {worst:F4} m, within the {RecoveryJumpshotHandoffMaxM:F2} m " +
                     "floor (ADR-0014 legibility call — see the PR for the reasoning and headroom).");
        else
            Fail($"stepback-recovery-hands-off-to-jumpshot: {worstBone} jumped {worst:F4} m (> " +
                 $"{RecoveryJumpshotHandoffMaxM:F2}). Every AnimationTree transition is a hard cut, so this " +
                 "SNAPS visibly at the step-back -> jump-shot chain (#253). Retune stepbackrecovery's final " +
                 "keypose in author_stepback.py.");
        Finish(pass ? 0 : 1);
    }

    // FK a single bone's origin at time `t` in `anim`, walking the parent
    // chain via `skel`'s bone_rest, exactly mirroring
    // rebuild_stepback_clips.gd's `_pose_origin` — a SEPARATE implementation
    // (not shared code) so the two act as independent proofs of the same
    // claim. Returns NaN (poisoned, not Vector3.Zero) if the bone does not
    // resolve — a Zero fallback would make an unresolvable bone read as "no
    // jump" and print PASS while measuring nothing (mutation-proven
    // elsewhere in this batch, #305).
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

    // The Hips bone's origin MINUS the TRAIL (rear, LEFT) foot's origin,
    // projected along the rig's own forward axis (cached from the pre-move
    // LeftFoot->LeftToeBase vector, the same anchor RetreatDribbleAnimTest
    // uses and for the identical reason: the actor's whole-body orientation
    // is frozen for a committed move's duration, so one derivation is exact
    // for the whole run).
    //
    // TRAIL FOOT ONLY, not an average of both feet — deliberately, and the
    // reason is measured rather than assumed. An early draft averaged both
    // ankles and found the two feet's OWN motions largely cancel during
    // Startup (author_stepback.py's table: the LEAD foot advances +0.13 m
    // while the TRAIL foot retreats -0.05 m over the same window), so the
    // averaged signal read a near-noise +0.0034 m — technically non-negative
    // but far too weak to be a meaningful, mutation-catchable control. The
    // TRAIL foot's OWN trajectory, in isolation, genuinely reverses
    // direction between the two phases: it retreats slightly during Startup
    // (unweighting as the plant commits onto the LEAD foot) and then swings
    // sharply forward during Active (kicking through as the exploding body
    // leaves it behind) — see the module docstring for the exact channel
    // values. That reversal is what gives this scenario pair a genuine,
    // large-margin opposite-sign contrast instead of a noise-dominated one.
    //
    // POSITIVE means the hips sit ahead of the trail foot; NEGATIVE means
    // the hips sit behind it — see the class doc's "Hips displace back is
    // measured relative to the feet" section for why this, not raw Hips
    // translation, is the honest live-rig proof of "the base was left
    // behind."
    private float MeasureHipsRelativeToFeet(Skeleton3D skel)
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

    private void Fail(string message) => GD.PrintErr($"[stepback-anim] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[stepback-anim] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
