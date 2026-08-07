using System;
using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #308 (ADR-0016): proves the THREE
// IN-AND-OUT ANIMATION STATES (InAndOutStartup / InAndOutActive /
// InAndOutRecovery) wired into scenes/Player.tscn are real — entered
// end-to-end by a real InAndOut, bound to the right clips, cut to the right
// windows, and actually MOVING the rig in the way the move's own NAME claims:
// the ball hand goes IN toward the midline (the fake) and comes back OUT
// PAST where it started (the recovery), while the off hand stays out on its
// own side the whole time.
//
// Before #308 "inandout" fell through MoveAnimResolver.ResolveStateName's
// default case onto the shared generic Startup/Active/Recovery states (#296).
//
//   godot --headless --path . res://tests/integration/InAndOutAnimTest.tscn -- --harness-scenario=inandout-phases
//   …=inandout-no-placeholder-leak | inandout-segment-lengths | inandout-edges
//   …=inandout-startup-differs-from-recovery | inandout-stays-unsuffixed
//   …=inandout-ball-hand-goes-in-then-out | inandout-offhand-stays-out-in-active
//   …=control-inandout-ballhand-does-come-in
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//
// ── Cosmetic-only (issue #308's standing constraint) ────────────────────────
// #308 is a CLIP issue. It does not observe or feed InAndOut's legality gate,
// the dead-dribble rule, or the gesture-recognizer retarget — InAndOutTest.cs
// already owns all of that through RequestMoveForHarness/real synthetic
// input. This harness begins the move via BeginMoveForHarness — downstream of
// every legality gate — precisely so it cannot accidentally become a second,
// weaker copy of that coverage.
//
// ── Why ActiveAnimNodeForHarness, not the resolver's return value (#257) ─────
// Travel() to a missing/misnamed state only LOGS; it never throws. Asserting
// MoveAnimResolver.ResolveStateName(...) == "InAndOutActive" would pass on a
// Player.tscn with no InAndOut states at all. Only the live
// AnimationNodeStateMachinePlayback proves wiring.
//
// ── Why HandRightForHarness/HolderForwardForHarness, not a hand-rolled axis ──
// Every motion gate below reads the lateral axis via BallController's own
// passthroughs (HolderForwardForHarness / HandRightForHarness), never by
// re-deriving Cross(forward, Up) independently. A second, slightly-different
// formula in test code could quietly disagree with production and never be
// caught by a left/right-symmetric control — exactly how #255 shipped a
// mirrored predicate that read green while inverted.
//
// ── The signed-vs-unsigned distinction (do NOT unify these) ─────────────────
// This file measures two DIFFERENT quantities off the same two wrist
// readings, and a later author will be tempted to make them consistent. They
// already are — consistent with their own claims, which differ:
//
//   * Gate 1 (inandout-ball-hand-goes-in-then-out) needs the SIGNED lateral
//     offset ("outness": +toward the ball hand's own side, 0 at the midline,
//     NEGATIVE once the hand has crossed to the far side). Only the sign can
//     tell "the hand is AT the midline" apart from "the hand CROSSED the
//     midline" — Math.Abs reads both as "small-then-large" and cannot
//     distinguish an in-and-out from a crossover, which is exactly the
//     distinction this clip exists to make. An abs()-based alignment gate was
//     mutation-proven blind to precisely this on #339.
//
//   * Gate 2 (inandout-offhand-stays-out-in-active) needs the UNSIGNED
//     distance from the midline for BOTH wrists. Its claim is "the off-hand
//     is FAR from the midline and the ball hand is NOT" — a claim about
//     DISTANCE, not direction. Worked through on both moves' reveal poses (at
//     the last Active tick, ballOut/offOut are the SIGNED "outness" form,
//     ballDist/offDist are UNSIGNED distances from the midline):
//
//       in-and-out (ball at midline, off out):  ballOut~0.00 offOut=+0.25 ->
//         signed diff = +0.25 (looks like a pass); ballDist=0.00 offDist=0.25
//         -> unsigned diff = +0.25 (a REAL pass)
//       crossover (ball crossed, off drawn in):  ballOut=-0.15 offOut~0.00 ->
//         signed diff = +0.15 (ALSO looks like a pass — WRONG, this is a
//         crossover, not an in-and-out); ballDist=0.15 offDist=0.00 ->
//         unsigned diff = -0.15 (correctly fails)
//
//     A crossed ball hand and a withdrawn off-hand produce the SAME positive
//     signed difference as the pose this move actually authors, so the
//     signed form cannot separate the two moves at Gate 2 — the unsigned form
//     is what refuses a crossover impersonating an in-and-out.
//
//   * Gate 3 (control-inandout-ballhand-does-come-in) also uses the UNSIGNED
//     distance, deliberately: a ball hand that crossed FULLY to the far side
//     is NOT "at the midline" either, and only the unsigned form refuses it.
//
// ── Why the control is a real control, not a restatement (README trap 5) ────
// The Y Bot rig is mirror-symmetric to 0.17 mm across X=0 (#255's own
// measurement), so a SYMMETRIC assertion proves nothing about handedness.
// Gate 2's `sep = offDist - ballDist` is strictly asymmetric: it goes
// NEGATIVE if the two wrists were swapped, so a left/right mix-up is visible
// in the sign, not just the magnitude.
//
// ── Reduction discipline (do NOT introduce a Math.Max/Min over the wrists) ──
// Every motion gate below reads a SINGLE NAMED wrist (the ball wrist or the
// off wrist, resolved once from the actor's HandSide) — never a reduction
// over both. A later author "tidying" these into a two-wrist min/max would
// convert a specific-limb claim ("the BALL hand came in") into a both-limbs
// claim, destroying the asymmetry that is the entire point of this move
// (#314's bug, in the other direction).
public partial class InAndOutAnimTest : Node
{
    private const double TimeoutSeconds = 15.0;
    private const int ArmFrames = 2;      // ticks for TryAssignTipoffHolder to run
    private const int ActFrames = 2;      // ticks after tipoff before Begin (position/heading settle)
    // startup(4)+active(3)+recovery(12)=19, with generous slack.
    private const int ObserveFrames = 40;

    private static readonly Vector3 ActorSpot = new(0f, 0f, 2f);
    private static readonly Vector3 FarSpot = new(12f, 0f, 12f); // out of PickupRadius
    private static readonly Vector3 RimCenter = new(0f, 3.05f, 0f); // BallController.DefaultRimCenter

    // Upper-body bones for the Startup-vs-Recovery pose comparison — same set
    // ContestAnimTest/LayupAnimTest use, and for the same reason: an
    // in-and-out's read lives in the arms/hands, not the legs (contrast
    // JabStepAnimTest, whose read is the leg stab).
    private static readonly string[] UpperBodyBones =
    {
        "mixamorig_LeftArm", "mixamorig_RightArm",
        "mixamorig_LeftForeArm", "mixamorig_RightForeArm",
        "mixamorig_Spine",
    };

    // House convention for this class of scenario (Contest/Layup/JabStep all
    // ship this floor): well below every clip's own measured Startup-vs-
    // Recovery pose delta, well above the #296 defect's near-zero collapse.
    private const float StartupVsRecoveryMinDeg = 15.0f;

    // ── Motion-gate thresholds — floors with deliberate headroom, NOT the
    // authored keyframe measurements themselves (the harness samples the LAST
    // OBSERVED tick of each phase, which — because of the one-tick
    // phase-label lead, see the Observe() comment below — lands BETWEEN the
    // authored keyframes, so live values will not equal the authoring table).
    private const float InMarginM = 0.02f;
    private const float OutMarginM = 0.05f;
    // Floor for Gate 2's unsigned midline separation at Active's end.
    //
    // Deliberately NOT derived from the authored keyframe. The clip's f7 reveal
    // pose measures 0.2387 - 0.0209 = 0.2177 m, but the harness CANNOT sample
    // that frame: the phase label leads the pose by one tick, Active is only 3
    // ticks, and the last tick attributable to Active therefore lands about one
    // tick short of the clip's true final pose. Live measurement is 0.1483 m
    // (offDist 0.1897, ballDist 0.0413 — the off hand 4.6x further from the
    // midline than the ball hand, a perfectly good read).
    //
    // 0.08 is ~54% of the live value, which is STRICTER than the jabstep
    // precedent's 0.02-floor-on-a-0.1356-measurement (15%). It is nowhere near
    // vacuous: the crossover mutation (InAndOutActive -> locomotion/
    // crossoveractiveleft) drives this to -0.2754, so the discriminating band
    // between the move and the move it impersonates is ~0.42 m wide.
    private const float SeparationFloorM = 0.08f;
    private const float NearMidlineCeilingM = 0.10f;

    private static readonly string[] KnownScenarios =
    {
        "inandout-phases",
        "inandout-no-placeholder-leak",
        "inandout-segment-lengths",
        "inandout-edges",
        "inandout-startup-differs-from-recovery",
        "inandout-stays-unsuffixed",
        "inandout-ball-hand-goes-in-then-out",
        "inandout-offhand-stays-out-in-active",
        "control-inandout-ballhand-does-come-in",
    };

    // Scenarios that need no live tree — pure resource/scene inspection.
    private static readonly string[] StaticScenarios =
    {
        "inandout-no-placeholder-leak", "inandout-segment-lengths", "inandout-edges",
        "inandout-stays-unsuffixed",
    };

    private string _scenario = "inandout-phases";

    private BallController _ball;
    private PlayerController _actor; // peer "1" — the tipoff holder (ADR-0007)
    private PlayerController _other; // peer "2"

    private int _frame;
    private double _elapsed;
    private bool _finished;

    private enum Step { AwaitTipoff, AwaitDribble, Act, Observe }
    private Step _step = Step.AwaitTipoff;
    private int _stepDeadlineFrame;

    // Latched (event-time) observations. The three phase latches can only turn
    // true in order — each guard requires the previous already latched — so
    // "saw all three" IS "saw them in order."
    private bool _sawStartup;
    private bool _sawActive;
    private bool _sawRecovery;
    private bool _sawGenericPlaceholder;

    // ── Bone indices, resolved ONCE off the actor's Skeleton3D (lazy: the
    // first tick a Skeleton3D is found). _ballSign is likewise resolved once
    // — InAndOut never swaps HandSide (see InAndOut.cs's class doc), so the
    // actor's ball hand is fixed for the whole lifecycle and re-reading it
    // every tick would add nothing but risk.
    private int _hipsIdx = -1;
    private int _ballWristIdx = -1;
    private int _offWristIdx = -1;
    private float _ballSign = 1f;

    // Geometry, latched at event time (never recomputed at verdict time — by
    // then the move is over and the rig has returned to Locomotion). Each
    // "at last X tick" value is OVERWRITTEN every tick of phase X (after the
    // phase's first observed tick — see Observe), so it ends up holding the
    // LAST one.
    private float _ballOutAtLastStartupTick = float.NaN;
    private float _ballOutAtLastActiveTick = float.NaN;
    private float _ballOutAtLastRecoveryTick = float.NaN;
    private float _offDistAtLastActiveTick = float.NaN;
    private float _ballDistAtLastActiveTick = float.NaN;
    private Quaternion[] _poseAtLastStartupTick;
    private Quaternion[] _poseAtLastRecoveryTick;

    // How many ticks Observe() named each phase on — INCLUDING the first,
    // which the geometry latches then drop (see Observe). The verdicts gate
    // on "> 1" so a phase shortened to a single tick fails loudly instead of
    // silently measuring nothing (the #316/#340 discipline).
    private int _startupTicksObserved;
    private int _activeTicksObserved;
    private int _recoveryTicksObserved;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "inandout-phases");
        GD.Print($"[inandout-anim] scenario={_scenario} booting headless…");

        if (!KnownScenarios.Contains(_scenario))
        {
            // A ci.yml typo must be a RED run, not a silently-defaulted green one.
            Fail($"unknown scenario '{_scenario}'.");
            Finish();
            return;
        }

        if (StaticScenarios.Contains(_scenario))
        {
            RunStaticCheck();
            return;
        }

        // Real Player.tscn instances (live AnimationTree + Skeleton3D), named
        // "1"/"2" so the OfflineMultiplayerPeer makes unique_id 1 both IsServer
        // and IsLocalPlayer — the full TickServerOwnPlayer -> ApplyAnimation
        // chain runs every tick, same as every other *AnimTest in this batch.
        var scene = GD.Load<PackedScene>("res://scenes/Player.tscn");
        _actor = scene.Instantiate<PlayerController>();
        _actor.Name = "1";
        _other = scene.Instantiate<PlayerController>();
        _other.Name = "2";

        // Physics-callback lockstep so GetCurrentNode() reflects the same-tick
        // Travel() (README trap 6 — the default Idle callback lags headless).
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
                // InAndOutTest.cs's own "dead-dribble-gate" scenario established
                // that InAndOut is REFUSED from a fresh live Held possession —
                // it requires a live Dribbling possession specifically (the
                // move IS itself a dribble move; #202's dead-dribble rule bars
                // every dribble-family move from a still/Held ball). So this
                // harness must start a real dribble before BeginMoveForHarness,
                // exactly as that test's "PermittedFromDribbling" step does.
                _ball.TryStartDribble(1);
                _step = Step.AwaitDribble;
                _stepDeadlineFrame = _frame + ActFrames;
                break;

            case Step.AwaitDribble:
                if (_frame < _stepDeadlineFrame) break;
                if (_ball.State != BallState.Dribbling)
                {
                    Fail($"{_scenario}: TryStartDribble(1) did not reach BallState.Dribbling by frame {_frame} " +
                         $"(got {_ball.State}) — InAndOut cannot legally begin without a live dribble.");
                    Finish();
                    return;
                }
                _step = Step.Act;
                _stepDeadlineFrame = _frame + ActFrames;
                break;

            case Step.Act:
                if (_frame < _stepDeadlineFrame) break;
                // The real production choke point (BeginCommittedMove), reached
                // via the generic harness seam — deliberately downstream of
                // every legality gate, which InAndOutTest already owns.
                if (!_actor.BeginMoveForHarness(new InAndOut(burstDirection: 1f)))
                {
                    Fail($"{_scenario}: BeginMoveForHarness(new InAndOut(1f)) returned false — " +
                         "the actor's machine was not Inactive at begin.");
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

        if (!_sawStartup && node == "InAndOutStartup") _sawStartup = true;
        if (_sawStartup && !_sawActive && node == "InAndOutActive") _sawActive = true;
        if (_sawActive && !_sawRecovery && node == "InAndOutRecovery") _sawRecovery = true;
        if (node == "Startup" || node == "Active" || node == "Recovery") _sawGenericPlaceholder = true;

        // ── Geometry, sampled at event time ──────────────────────────────────
        var skel = FindSkeleton(_actor);
        if (skel == null) return;

        if (node != "InAndOutStartup" && node != "InAndOutActive" && node != "InAndOutRecovery") return;

        if (_hipsIdx < 0) CacheBoneIndices(skel);

        float ballLateral = MeasureWristLateral(skel, _ballWristIdx);
        float offLateral = MeasureWristLateral(skel, _offWristIdx);
        // "outness" — signed, positive when the hand sits out on ITS OWN
        // side, ~0 at the midline, NEGATIVE once it has crossed. See the file
        // header's "signed-vs-unsigned" section for why this form is used
        // ONLY for Gate 1 and never reduced against the off hand's own sign.
        float ballOut = ballLateral * _ballSign;
        // Unsigned distance from the midline — used ONLY for Gate 2/3. See
        // the same header section for why this must NOT be replaced with the
        // signed form (it would pass a crossover impersonating this move).
        float ballDist = Math.Abs(ballLateral);
        float offDist = Math.Abs(offLateral);

        // ── Per-phase latches: DROP EACH PHASE'S FIRST OBSERVED TICK ─────────
        // The phase label leads the pose by one tick even with
        // CallbackModeProcess=Physics (#316): the first tick GetCurrentNode()
        // names a phase still holds the PREVIOUS phase's pose. Only assigning
        // once ticksObserved > 1 guarantees the stored value came from THIS
        // phase's own clip, and the matching ">1" premise in every verdict
        // below fails loudly (never passes) if a phase was too short to ever
        // satisfy it — the #340 discipline, re-applied here.
        if (node == "InAndOutStartup")
        {
            _startupTicksObserved++;
            if (_startupTicksObserved > 1)
            {
                _ballOutAtLastStartupTick = ballOut;
                _poseAtLastStartupTick = SampleUpperBody(skel);
            }
        }
        else if (node == "InAndOutActive")
        {
            _activeTicksObserved++;
            if (_activeTicksObserved > 1)
            {
                _ballOutAtLastActiveTick = ballOut;
                _offDistAtLastActiveTick = offDist;
                _ballDistAtLastActiveTick = ballDist;
            }
        }
        else
        {
            _recoveryTicksObserved++;
            if (_recoveryTicksObserved > 1)
            {
                _ballOutAtLastRecoveryTick = ballOut;
                _poseAtLastRecoveryTick = SampleUpperBody(skel);
            }
        }
    }

    private void RenderVerdict()
    {
        switch (_scenario)
        {
            case "inandout-phases":                          VerdictPhases(); break;
            case "inandout-startup-differs-from-recovery":   VerdictStartupDiffersFromRecovery(); break;
            case "inandout-ball-hand-goes-in-then-out":      VerdictBallHandGoesInThenOut(); break;
            case "inandout-offhand-stays-out-in-active":     VerdictOffhandStaysOutInActive(); break;
            case "control-inandout-ballhand-does-come-in":   VerdictControlBallhandDoesComeIn(); break;
        }
    }

    // ── Scenario: inandout-phases (positive) ────────────────────────────────
    private void VerdictPhases()
    {
        bool pass = _sawStartup && _sawActive && _sawRecovery;
        if (pass)
            GD.Print("[inandout-anim] PASS inandout-phases — the tree was observed on \"InAndOutStartup\", then " +
                     "\"InAndOutActive\", then \"InAndOutRecovery\", in that order (the .tscn states and their " +
                     "transitions are live).");
        else
            Fail($"inandout-phases: expected InAndOutStartup -> InAndOutActive -> InAndOutRecovery, in order; got " +
                 $"sawStartup={_sawStartup}, sawActive={_sawActive}, sawRecovery={_sawRecovery}, " +
                 $"sawGenericPlaceholder={_sawGenericPlaceholder}, " +
                 $"lastAnimNode={_actor.ActiveAnimNodeForHarness}.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: inandout-startup-differs-from-recovery ────────────────────
    // #296's ACTUAL complaint: on the generic fallback both phases play
    // locomotion/idle, pixel-identical. Comparing two SAMPLED in-move poses
    // (rather than either against rest) is the honest question — a bound clip
    // poses them differently; an unbound clip collapses both to rest and the
    // delta goes to ~0.
    private void VerdictStartupDiffersFromRecovery()
    {
        if (_poseAtLastStartupTick == null || _poseAtLastRecoveryTick == null)
        {
            Fail("inandout-startup-differs-from-recovery: never sampled both a Startup and a Recovery tick " +
                 $"(sawStartup={_sawStartup}, sawRecovery={_sawRecovery}) — the premise for comparing them " +
                 "never held, so this fails rather than passes.");
            Finish(1);
            return;
        }

        // Assert this scenario's OWN premise: that the two states are actually
        // two different clips.
        //
        // Without it the pose-delta alone is misleading rather than merely
        // weak. Mutation-measured: repointing InAndOutRecovery at the STARTUP
        // clip leaves the delta at 15.60 deg — above this 15.0 floor — because
        // the last Startup tick and the last Recovery tick sample the same
        // short clip at different clip-times, and that sampling offset alone
        // is worth ~15 deg of real motion. The gate would then print "the
        // wind-up and the punish window are visibly distinct silhouettes"
        // about a single clip played twice, which is a FALSE pass, not a
        // near-miss.
        //
        // Raising the floor above 15.6 was the alternative and is worse: it
        // would leave only 2.3 deg of headroom on the real clip (22.26), so a
        // future re-author would trip it for no good reason. The premise check
        // catches the same-clip case deterministically, with no threshold at
        // all. It overlaps inandout-no-placeholder-leak's allowlist by design —
        // a scenario asserting its own premise is this repo's stated
        // discipline, not redundancy to be tidied away.
        var sm = LoadStateMachine();
        string startupClip = ClipOf(sm, "InAndOutStartup");
        string recoveryClip = ClipOf(sm, "InAndOutRecovery");
        bool distinctClips = startupClip != null && recoveryClip != null && startupClip != recoveryClip;

        float worst = 0f;
        for (int i = 0; i < _poseAtLastStartupTick.Length; i++)
            worst = Math.Max(worst, Mathf.RadToDeg(
                _poseAtLastStartupTick[i].AngleTo(_poseAtLastRecoveryTick[i])));

        GD.Print($"[inandout-anim]   worst upper-body Startup-vs-Recovery delta = {worst:F2} deg " +
                 $"(floor {StartupVsRecoveryMinDeg:F1}), startupClip={startupClip} recoveryClip={recoveryClip} " +
                 $"distinctClips={distinctClips}, " +
                 $"ticks su/ac/re = {_startupTicksObserved}/{_activeTicksObserved}/{_recoveryTicksObserved}");

        bool pass = _sawStartup && _sawRecovery && distinctClips && worst >= StartupVsRecoveryMinDeg;
        if (pass)
            GD.Print("[inandout-anim] PASS inandout-startup-differs-from-recovery — the last Startup pose and " +
                     $"the last Recovery pose differ by {worst:F2} deg, so the wind-up and the punish window " +
                     "are visibly distinct silhouettes (#296).");
        else
            Fail($"inandout-startup-differs-from-recovery: distinctClips={distinctClips} " +
                 $"(startup='{startupClip}', recovery='{recoveryClip}'), worst delta {worst:F2} deg vs floor " +
                 $"{StartupVsRecoveryMinDeg:F1}. If distinctClips is false the two states point at the SAME " +
                 "clip and the pose delta is meaningless sampling offset, not a silhouette difference. " +
                 "Otherwise the clips bind to nothing on this rig (check for Blender's 'Armature/' " +
                 "track-path prefix) and both poses collapsed to rest.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: inandout-ball-hand-goes-in-then-out (Gate 1, positive) ────
    // The move's name IS the assertion: the ball hand goes IN toward the
    // midline during Active (the fake), then comes back OUT past where it
    // started by Recovery's end (the sell) — and "out" has to be genuinely
    // outside the midline, not merely "less in than Active was." Uses the
    // SIGNED "outness" form — see the file header for why Gate 1 specifically
    // needs the sign and Gate 2/3 specifically must not use it.
    //
    // Self-contained WITHIN the move — deliberately NOT compared against the
    // pre-move dribble pose (a different clip, the Dribble BlendSpace), which
    // would make this a noisy cross-clip comparison.
    private void VerdictBallHandGoesInThenOut()
    {
        GD.Print($"[inandout-anim]   ballOut (signed): startup={_ballOutAtLastStartupTick:F4} " +
                 $"active={_ballOutAtLastActiveTick:F4} recovery={_ballOutAtLastRecoveryTick:F4} " +
                 $"(InMargin={InMarginM:F2}, OutMargin={OutMarginM:F2}), " +
                 $"ticks su/ac/re = {_startupTicksObserved}/{_activeTicksObserved}/{_recoveryTicksObserved}");

        bool premise = _sawStartup && _sawActive && _sawRecovery
                       && _startupTicksObserved > 1 && _activeTicksObserved > 1 && _recoveryTicksObserved > 1
                       && !float.IsNaN(_ballOutAtLastStartupTick) && !float.IsNaN(_ballOutAtLastActiveTick)
                       && !float.IsNaN(_ballOutAtLastRecoveryTick);

        bool wentIn = premise && _ballOutAtLastActiveTick < _ballOutAtLastStartupTick - InMarginM;
        bool cameOut = premise && _ballOutAtLastRecoveryTick > _ballOutAtLastStartupTick + OutMarginM;
        bool genuinelyOutside = premise && _ballOutAtLastRecoveryTick > 0f;
        bool pass = premise && wentIn && cameOut && genuinelyOutside;

        if (pass)
            GD.Print("[inandout-anim] PASS inandout-ball-hand-goes-in-then-out — the ball hand's signed " +
                     $"outness went startup={_ballOutAtLastStartupTick:F4} -> active={_ballOutAtLastActiveTick:F4} " +
                     $"(in, past the {InMarginM:F2} margin) -> recovery={_ballOutAtLastRecoveryTick:F4} (out, " +
                     $"past the {OutMarginM:F2} margin AND genuinely > 0), so the fake reads as a real in-then-out.");
        else
            Fail($"inandout-ball-hand-goes-in-then-out: premise={premise}, wentIn={wentIn} " +
                 $"({_ballOutAtLastActiveTick:F4} < {_ballOutAtLastStartupTick - InMarginM:F4}?), " +
                 $"cameOut={cameOut} ({_ballOutAtLastRecoveryTick:F4} > {_ballOutAtLastStartupTick + OutMarginM:F4}?), " +
                 $"genuinelyOutside={genuinelyOutside} ({_ballOutAtLastRecoveryTick:F4} > 0?). If the premise " +
                 "broke this fails rather than passes; otherwise the clip either never fakes toward the midline " +
                 "or never recovers past its own start.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: inandout-offhand-stays-out-in-active (Gate 2, positive) ───
    // The tell that separates this move from the crossover it impersonates:
    // at the Active reveal the OFF hand sits further from the midline than
    // the BALL hand does. UNSIGNED distances — see the file header for why
    // this must not be the signed "outness" form (a crossover's off-hand
    // coming IN to meet the ball at the midline would pass the signed form
    // too, for the wrong reason).
    //
    // Non-vacuous only in combination with Gate 1 (the ball genuinely came
    // in) and Gate 3 below (the measurement can see "at the midline" at all)
    // — the three interlock, and none of them alone proves the read.
    private void VerdictOffhandStaysOutInActive()
    {
        float sep = _offDistAtLastActiveTick - _ballDistAtLastActiveTick;
        GD.Print($"[inandout-anim]   at Active end: offDist={_offDistAtLastActiveTick:F4} " +
                 $"ballDist={_ballDistAtLastActiveTick:F4} sep(unsigned)={sep:F4} (floor {SeparationFloorM:F2}), " +
                 $"ticks su/ac/re = {_startupTicksObserved}/{_activeTicksObserved}/{_recoveryTicksObserved}");

        bool premise = _sawActive && _activeTicksObserved > 1
                       && !float.IsNaN(_offDistAtLastActiveTick) && !float.IsNaN(_ballDistAtLastActiveTick);
        bool pass = premise && sep >= SeparationFloorM;

        if (pass)
            GD.Print($"[inandout-anim] PASS inandout-offhand-stays-out-in-active — the off hand sits {sep:F4} m " +
                     $"further from the midline than the ball hand at Active's reveal (floor {SeparationFloorM:F2}), " +
                     "so the off-hand did NOT come in to meet the ball — the tell that separates this move from a " +
                     "crossover.");
        else
            Fail($"inandout-offhand-stays-out-in-active: sep={sep:F4}, need >= {SeparationFloorM:F2} " +
                 $"(premise={premise}, sawActive={_sawActive}). Either the off hand drifted toward the midline " +
                 "(a crossover-shaped pose) or the premise never held.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: control-inandout-ballhand-does-come-in (Gate 3, control) ──
    // Gate 2's claim is a negative ("the off hand did NOT come in"). Its
    // premise is that the measurement can see a hand AT the midline at all.
    // This asserts that premise directly, closing Gate 2's one real
    // loophole: a large `sep` produced by an absurdly wide off-hand rather
    // than a genuine ball-hand fake. UNSIGNED distance, deliberately — a ball
    // hand that crossed FULLY to the far side is NOT "at the midline" either,
    // and only the unsigned form refuses that case. A different quantity
    // from Gate 1 (which tests the arc/direction over the whole lifecycle;
    // this tests absolute position at one instant), so this is a real
    // control, not a restatement.
    private void VerdictControlBallhandDoesComeIn()
    {
        GD.Print($"[inandout-anim]   (control) ballDist at Active end = {_ballDistAtLastActiveTick:F4} " +
                 $"(ceiling {NearMidlineCeilingM:F2}), " +
                 $"ticks su/ac/re = {_startupTicksObserved}/{_activeTicksObserved}/{_recoveryTicksObserved}");

        bool premise = _sawActive && _activeTicksObserved > 1 && !float.IsNaN(_ballDistAtLastActiveTick);
        bool pass = premise && _ballDistAtLastActiveTick <= NearMidlineCeilingM;

        if (pass)
            GD.Print($"[inandout-anim] PASS control-inandout-ballhand-does-come-in — the ball hand's distance " +
                     $"from the midline at Active's reveal was {_ballDistAtLastActiveTick:F4} m (ceiling " +
                     $"{NearMidlineCeilingM:F2}), so the ball genuinely IS near the midline and " +
                     "inandout-offhand-stays-out-in-active's green is a real measurement, not an artefact of an " +
                     "oversized off-hand reading.");
        else
            Fail($"control-inandout-ballhand-does-come-in: ballDist={_ballDistAtLastActiveTick:F4}, need <= " +
                 $"{NearMidlineCeilingM:F2} (premise={premise}, sawActive={_sawActive}). If this fails, " +
                 "inandout-offhand-stays-out-in-active's green cannot be trusted — the instrument cannot see the " +
                 "ball hand reach the midline at all.");
        Finish(pass ? 0 : 1);
    }

    // ── Static scenarios: no live tree, pure resource/scene inspection ──────
    private void RunStaticCheck()
    {
        switch (_scenario)
        {
            case "inandout-no-placeholder-leak": RunNoPlaceholderLeakCheck(); break;
            case "inandout-segment-lengths":     RunSegmentLengthsCheck(); break;
            case "inandout-edges":               RunEdgesCheck(); break;
            case "inandout-stays-unsuffixed":    RunStaysUnsuffixedCheck(); break;
        }
    }

    // ── Scenario: inandout-segment-lengths ──────────────────────────────────
    // #276 rule 4 / #295. Tick windows are read from InAndOut.DefaultFrameData,
    // NOT hardcoded, so a future retune that forgets to re-run the rebuild
    // tool goes red here and names it via the AnimationLibrary clip lookup.
    private void RunSegmentLengthsCheck()
    {
        var lib = GD.Load<AnimationLibrary>("res://assets/locomotion.res");
        if (lib == null)
        {
            Fail("assets/locomotion.res failed to load — cannot evaluate inandout-segment-lengths.");
            Finish(1);
            return;
        }

        double tps = Engine.PhysicsTicksPerSecond;
        var frames = InAndOut.DefaultFrameData;
        // Verified exact in the resource (deviation 0.00000000s), so this is a
        // float-noise band, NOT a drift allowance — a one-tick mis-slice must
        // go red, not slip under a loose tolerance.
        const double ToleranceSeconds = 1e-3;

        (string Clip, int Ticks)[] windows =
        {
            ("inandoutstartup",  frames.StartupFrames),
            ("inandoutactive",   frames.ActiveFrames),
            ("inandoutrecovery", frames.RecoveryFrames),
        };

        bool pass = true;
        foreach (var (clipName, ticks) in windows)
        {
            if (!lib.HasAnimation(clipName))
            {
                Fail($"AnimationLibrary has no clip '{clipName}' — run the in-and-out rebuild tool.");
                pass = false;
                continue;
            }

            double expectedSeconds = ticks / tps;
            // Variant accessor, NOT .Length: that property is `float` in Godot 4.6.x
            // and `double` in 4.7, so a 4.7.1-built assembly throws
            // MissingMethodException under a stale 4.6 binary — and it throws
            // inside _PhysicsProcess, BEFORE the timeout check, so the scenario
            // HANGS instead of failing (#339 measured this across 8 harnesses).
            // The Variant accessor binds correctly under both.
            double actualSeconds = lib.GetAnimation(clipName).Get("length").AsDouble();
            double deviationSeconds = Math.Abs(actualSeconds - expectedSeconds);
            GD.Print($"[inandout-anim]   '{clipName}': length={actualSeconds:F6}s " +
                     $"expected={expectedSeconds:F6}s ({ticks} ticks @ {tps} tps), " +
                     $"deviation={deviationSeconds:F6}s");

            if (deviationSeconds > ToleranceSeconds)
            {
                Fail($"clip '{clipName}' is {actualSeconds:F6}s, expected {expectedSeconds:F6}s " +
                     $"({ticks} ticks at {tps} tps — InAndOut.DefaultFrameData), a deviation of " +
                     $"{deviationSeconds:F6}s exceeds the float-noise tolerance ({ToleranceSeconds:F6}s). " +
                     "Re-run the in-and-out rebuild tool after retuning the move's frame data.");
                pass = false;
            }
        }

        if (pass)
            GD.Print("[inandout-anim] PASS inandout-segment-lengths — all three clips' durations match " +
                     "InAndOut.DefaultFrameData's Startup/Active/Recovery windows to within float noise " +
                     $"({ToleranceSeconds:F6}s).");
        else
            GD.PrintErr("[inandout-anim] FAIL inandout-segment-lengths — see per-clip deviations above.");

        Finish(pass ? 0 : 1);
    }

    // ── Scenario: inandout-no-placeholder-leak ──────────────────────────────
    // The direct statement that #296 is closed for this move. An ALLOWLIST,
    // not a placeholder blocklist — see ContestAnimTest/LayupAnimTest's own
    // comments for why a blocklist alone waves through a copy-paste from a
    // neighbouring move's sub-resource.
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
            ("InAndOutStartup",  "locomotion/inandoutstartup"),
            ("InAndOutActive",   "locomotion/inandoutactive"),
            ("InAndOutRecovery", "locomotion/inandoutrecovery"),
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
            GD.Print($"[inandout-anim]   {stateName} -> {actualClip}");

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
            GD.Print("[inandout-anim] PASS inandout-no-placeholder-leak — all three InAndOut states point at " +
                     "their OWN per-move clips, not the shared locomotion/idle placeholder #308 moved them off of.");
        else
            GD.PrintErr("[inandout-anim] FAIL inandout-no-placeholder-leak — see per-state mismatches above.");

        Finish(pass ? 0 : 1);
    }

    // ── Scenario: inandout-edges ─────────────────────────────────────────────
    // README trap 8 / #279: a DELETED transition edge is invisible to
    // GetCurrentNode() (Travel() is a pathfinder and routes around the gap),
    // so this reads GetTransitionCount()/From()/To() off the RESOURCE, where a
    // missing edge is simply absent. InAndOut is an OFFENSIVE (dribble-family)
    // move, so it needs the six standard edges AND the dribble-family
    // entries/exits, doubled since #294 split Dribble into
    // DribbleLeft/DribbleRight — matching JabStepAnimTest/LayupAnimTest's own
    // precedent.
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
            // The six standard edges.
            ("Locomotion", "InAndOutStartup"),
            ("InAndOutStartup", "InAndOutActive"),
            ("InAndOutActive", "InAndOutRecovery"),
            ("InAndOutRecovery", "Locomotion"),
            ("InAndOutStartup", "InAndOutRecovery"), // feint / early-out
            ("InAndOutStartup", "Locomotion"),       // abort
            // The dribble family, doubled by #294.
            ("DribbleLeft", "InAndOutStartup"),
            ("DribbleRight", "InAndOutStartup"),
            ("InAndOutRecovery", "DribbleLeft"),
            ("InAndOutRecovery", "DribbleRight"),
            ("InAndOutStartup", "DribbleLeft"),
            ("InAndOutStartup", "DribbleRight"),
        };

        var present = new System.Collections.Generic.HashSet<string>();
        for (int i = 0; i < sm.GetTransitionCount(); i++)
            present.Add($"{sm.GetTransitionFrom(i)}->{sm.GetTransitionTo(i)}");

        bool pass = true;
        foreach (var (from, to) in required)
        {
            bool here = present.Contains($"{from}->{to}");
            GD.Print($"[inandout-anim]   edge {from} -> {to}: {(here ? "present" : "MISSING")}");
            if (!here)
            {
                Fail($"scenes/Player.tscn has no transition '{from}' -> '{to}'. Travel()'s pathfinder will " +
                     "route around the gap, so NO runtime scenario can catch this — only this resource-level " +
                     "check can.");
                pass = false;
            }
        }

        if (pass)
            GD.Print($"[inandout-anim] PASS inandout-edges — all {required.Length} required transitions are " +
                     "present (6 standard + 6 dribble-family, the latter doubled by #294's " +
                     "DribbleLeft/DribbleRight split).");
        else
            GD.PrintErr("[inandout-anim] FAIL inandout-edges — see missing transitions above.");

        Finish(pass ? 0 : 1);
    }

    // ── Scenario: inandout-stays-unsuffixed (issue-mandated trap guard) ─────
    // InAndOut is clipped but deliberately UNHANDED (InAndOut.cs's own class
    // doc: it never swaps HandSide). Guards the predictable WRONG change: the
    // move carries a BurstDirection param and sits beside a Left/Right-shaped
    // sibling pattern in MoveAnimResolver (Crossover/BehindTheBack), and
    // "completing" that pattern by adding "inandout" to HandedMoves produces
    // NO compile error and NO runtime exception — just a permanently inverted
    // read, because OriginHand returns Opposite(ballHand) for every
    // non-Startup phase of a move that never actually swaps hands. Written
    // even though nothing is currently broken (per the issue's own mandate).
    private void RunStaysUnsuffixedCheck()
    {
        bool pass = true;

        // Part 1: ResolveStateName must return the bare name for BOTH ball
        // hands — if the move were wrongly added to HandedMoves, Startup
        // would still read correctly (OriginHand(Startup, hand) == hand) but
        // Active/Recovery would silently start carrying a Left/Right suffix.
        foreach (HandSide ballHand in new[] { HandSide.Left, HandSide.Right })
        {
            (MoveAnimState State, string Expect)[] phases =
            {
                (MoveAnimState.Startup,  "InAndOutStartup"),
                (MoveAnimState.Active,   "InAndOutActive"),
                (MoveAnimState.Recovery, "InAndOutRecovery"),
            };
            foreach (var (state, expect) in phases)
            {
                string actual = MoveAnimResolver.ResolveStateName(state, "inandout", ballHand, HandSide.Right);
                GD.Print($"[inandout-anim]   ResolveStateName({state}, \"inandout\", ballHand={ballHand}) = \"{actual}\"");
                if (actual != expect)
                {
                    Fail($"inandout-stays-unsuffixed: ResolveStateName({state}, \"inandout\", {ballHand}) " +
                         $"returned \"{actual}\", expected \"{expect}\" (no Left/Right suffix, either hand).");
                    pass = false;
                }
            }
        }

        // Part 2: scenes/Player.tscn's state machine must carry NO handed
        // variant of any InAndOut state. A resolver-only check cannot prove
        // the .tscn itself stayed clean — this reads the RESOURCE directly.
        // Do NOT try to prove this with Travel() (trap 8): the pathfinder
        // routes around a missing/extra state either way, and Travel() to a
        // missing state only LOGS (#257).
        var sm = LoadStateMachine();
        if (sm == null)
        {
            Fail("inandout-stays-unsuffixed: could not read an AnimationNodeStateMachine off scenes/Player.tscn's " +
                 "AnimationTree tree_root.");
            Finish(1);
            return;
        }

        string[] forbidden =
        {
            "InAndOutStartupLeft", "InAndOutStartupRight",
            "InAndOutActiveLeft", "InAndOutActiveRight",
            "InAndOutRecoveryLeft", "InAndOutRecoveryRight",
        };
        foreach (string name in forbidden)
        {
            bool present = sm.HasNode(name);
            GD.Print($"[inandout-anim]   state '{name}': {(present ? "PRESENT (bad)" : "absent (good)")}");
            if (present)
            {
                Fail($"inandout-stays-unsuffixed: scenes/Player.tscn has a handed state '{name}' — InAndOut " +
                     "must never carry a Left/Right suffix (the ball hand never swaps).");
                pass = false;
            }
        }

        if (pass)
            GD.Print("[inandout-anim] PASS inandout-stays-unsuffixed — ResolveStateName returns the bare " +
                     "InAndOut* names for both ball hands, and scenes/Player.tscn carries no handed InAndOut* state.");
        else
            GD.PrintErr("[inandout-anim] FAIL inandout-stays-unsuffixed — see above.");

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

    /// <summary>
    /// The clip name a single-clip state points at, or null if the state is
    /// missing or is not an <see cref="AnimationNodeAnimation"/>. Returning
    /// null rather than "" matters: callers compare two states for INEQUALITY,
    /// and two missing states would compare equal-and-distinct-from-nothing if
    /// this returned a shared empty sentinel. Null forces the caller's
    /// distinctness check to fail closed.
    /// </summary>
    private static string ClipOf(AnimationNodeStateMachine sm, string stateName)
    {
        if (sm == null || !sm.HasNode(stateName)) return null;
        return sm.GetNode(stateName) is AnimationNodeAnimation animNode
            ? animNode.Animation.ToString()
            : null;
    }

    // ── Geometry helpers ────────────────────────────────────────────────────

    // Resolves the ball/off wrist bone indices and the ball-hand sign ONCE,
    // off the actor's own authoritative HandSide (M9, #83/ADR-0012) — not a
    // hardcoded "the ball hand is Right" assumption, even though that is what
    // this harness's fresh actor happens to start with (InAndOut never swaps,
    // so it stays true for the whole lifecycle).
    private void CacheBoneIndices(Skeleton3D skel)
    {
        _hipsIdx = skel.FindBone("mixamorig_Hips");
        bool ballIsRight = _actor.HandSide == HandSide.Right;
        _ballWristIdx = skel.FindBone(ballIsRight ? "mixamorig_RightHand" : "mixamorig_LeftHand");
        _offWristIdx = skel.FindBone(ballIsRight ? "mixamorig_LeftHand" : "mixamorig_RightHand");
        _ballSign = ballIsRight ? 1f : -1f;
    }

    // Signed lateral offset of a wrist from the pelvis, along the SAME
    // body-right axis BallController uses to place the ball (one source of
    // truth, #255) — see the file header for why this reads
    // BallController's own passthroughs rather than a hand-rolled formula.
    // World-space (skel.GlobalTransform * ...), since the axis itself
    // (HandRightForHarness) is derived from the actor's world Heading, not
    // from the Skeleton3D node's own local space.
    private float MeasureWristLateral(Skeleton3D skel, int wristIdx)
    {
        Vector3 forward = BallController.HolderForwardForHarness(_actor);
        Vector3 right = BallController.HandRightForHarness(forward);
        Vector3 wrist = skel.GlobalTransform * skel.GetBoneGlobalPose(wristIdx).Origin;
        Vector3 hips = skel.GlobalTransform * skel.GetBoneGlobalPose(_hipsIdx).Origin;
        return (wrist - hips).Dot(right); // SIGNED — never Math.Abs here; callers choose sign vs abs.
    }

    private static Quaternion[] SampleUpperBody(Skeleton3D skel)
    {
        var poses = new Quaternion[UpperBodyBones.Length];
        for (int i = 0; i < UpperBodyBones.Length; i++)
        {
            int idx = skel.FindBone(UpperBodyBones[i]);
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

    private void Fail(string message) => GD.PrintErr($"[inandout-anim] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[inandout-anim] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
