using System;
using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #304 (ADR-0016): proves the THREE
// JAB STEP ANIMATION STATES (JabStepStartup / JabStepActive / JabStepRecovery)
// wired into scenes/Player.tscn are real — entered end-to-end by a real
// JabStep, bound to the right clips, cut to the right windows, and actually
// MOVING the rig.
//
// Before #304 "jab" fell through MoveAnimResolver.ResolveStateName's default
// case onto the shared generic Startup/Active/Recovery states, which per #296
// render a 3-tick LOOPING IDLE for Startup/Recovery (pixel-identical) and a
// 2-tick slice of a SPRINT STRIDE for Active — an actively false read for the
// smallest, most deliberately subtle committed move in the game
// (JabStepLegalityResolver.cs's own class doc: "a quick, honest foot-stab that
// sells 'I might drive' without surrendering the pivot").
//
//   godot --headless --path . res://tests/integration/JabStepAnimTest.tscn -- --harness-scenario=jabstep-phases
//   …=jabstep-no-placeholder-leak | jabstep-segment-lengths | jabstep-edges
//   …=jabstep-startup-differs-from-recovery
//   …=jabstep-torso-pitches-forward-in-active | control-jabstep-torso-modest-in-startup
//   …=jabstep-differs-from-retreatdribble (two-move cross-clip comparison, #333)
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//
// ── moveId is "jab", not "jabstep" ───────────────────────────────────────────
// JabStep.cs's constructor is `base(id: "jab", ...)`. MoveAnimResolver's
// ClippedMovePrefixes therefore keys on "jab" — the STATE names are
// "JabStep*" (the PascalCase display prefix), but the moveId fed to
// BeginMoveForHarness / read by DisplayMoveId is "jab". Do not confuse the two.
//
// ── #294 landed before this issue was picked up ──────────────────────────────
// The issue handoff was written against a `main` where #294
// (DribbleLeft/DribbleRight split) had NOT yet landed, so it specified 3
// dribble-family edges. Checked live against this branch's base commit: #294
// IS already merged (Dribble is DribbleLeft/DribbleRight), so this harness —
// like LayupAnimTest before it — asserts the DOUBLED 6-edge shape (12 edges
// total with the 6 standard ones), matching #313/#314's own precedent.
//
// ── Cosmetic-only (issue #304's standing constraint) ────────────────────────
// #304 is a CLIP issue. It does not observe or feed JabStepLegalityResolver,
// BallState, or any PlayerController move-begin gate. This harness begins the
// move via BeginMoveForHarness — downstream of every legality gate, which
// JabStepTest already owns — precisely so it cannot accidentally become a
// second, weaker test of that gating.
//
// ── Why ActiveAnimNodeForHarness, not the resolver's return value (#257) ─────
// Travel() to a missing/misnamed state only LOGS; it never throws. Asserting
// MoveAnimResolver.ResolveStateName(...) == "JabStepActive" would pass on a
// Player.tscn with no JabStep states at all. Only the live
// AnimationNodeStateMachinePlayback proves wiring.
//
// ── The retreat-dribble contrast (#305/#333) ─────────────────────────────────
// Jab step and retreat dribble share the identical 3/2/4-tick shape off the
// same source (assets/Dribble.fbx); the issue's own motion spec names torso
// lean SIGN as the only automated defence against the two clips converging.
//
// #305 has now landed, so jabstep-differs-from-retreatdribble is LIVE (#333).
// It is no longer a static resource check: it runs BOTH moves back to back on
// one actor — the jab first, then a retreat dribble after starting a real
// dribble, which RetreatDribble's dead-dribble gate requires and JabStep's does
// not — and asserts their Active-phase torso travel has opposite sign, each
// clearing the same magnitude floor its own dedicated scenario uses. See
// VerdictDiffersFromRetreatDribble for why the comparison is of DELTAS off each
// move's own baseline and not of absolute leans.
public partial class JabStepAnimTest : Node
{
    private const double TimeoutSeconds = 15.0;
    private const int ArmFrames = 2;      // ticks for TryAssignTipoffHolder to run
    private const int ActFrames = 2;      // ticks after tipoff before Begin (position/heading settle)
    // startup(3)+active(2)+recovery(4)=9, with generous slack — this is the
    // smallest committed move in the game, so even a 5x margin is cheap.
    private const int ObserveFrames = 30;

    private static readonly Vector3 ActorSpot = new(0f, 0f, 2f);
    private static readonly Vector3 FarSpot = new(12f, 0f, 12f); // out of PickupRadius
    private static readonly Vector3 RimCenter = new(0f, 3.05f, 0f); // BallController.DefaultRimCenter

    // Upper-body bones for the Startup-vs-Recovery pose comparison. Same set
    // LayupAnimTest/ContestAnimTest use.
    // Named "UpperBodyBones" to match the convention every other AnimTest in
    // this batch uses for its Startup-vs-Recovery pose set, but jab step's read
    // is primarily in the LEGS (the 0.35 m foot stab, not an arm gesture), so
    // this set extends down through the jab leg's whole chain — the rebuild
    // script's own G3 gate (measured 35.1 deg across all 65 bones) found its
    // largest delta there, not in the arms/spine the other moves' sets cover.
    private static readonly string[] UpperBodyBones =
    {
        "mixamorig_LeftArm", "mixamorig_RightArm",
        "mixamorig_LeftForeArm", "mixamorig_RightForeArm",
        "mixamorig_Spine",
        "mixamorig_LeftUpLeg", "mixamorig_RightUpLeg",
        "mixamorig_LeftLeg", "mixamorig_RightLeg",
        "mixamorig_LeftFoot", "mixamorig_RightFoot",
        "mixamorig_LeftToeBase", "mixamorig_RightToeBase",
    };

    // Measured on the shipped clip (rebuild_jabstep_clips.gd's G3): 35.1 deg.
    // The floor sits well below that and well above the #296 defect's ~0 deg
    // (both phases sharing the generic idle placeholder).
    private const float StartupVsRecoveryMinDeg = 15.0f;

    // The torso-forward growth gate, off the pre-move baseline
    // (`_leanBeforeMove`). Measured live: 0.1356 m by Active's last tick. Set
    // well inside that.
    private const float TorsoForwardGrowthMinM = 0.02f;
    // The paired control's margin floor: Active's own growth off the same
    // baseline must exceed Startup's own growth by at least this much.
    // Measured live: margin = 0.0234 (startup 0.1122, active 0.1356) — a real
    // but DELIBERATELY THIN margin, because this is the smallest committed
    // move in the game (a <=3-tick Startup) and author_jabstep.py's own
    // keypose table intentionally brings the wind-up most of the way toward
    // the stab pose by Startup's own end (mirrors author_contest.py's
    // identical choice for ITS Startup/Active boundary). Set below the
    // measured value with headroom for float noise, not to manufacture a
    // wider gap the move was never authored to have.
    private const float TorsoForwardSettleMinM = 0.015f;

    private static readonly string[] KnownScenarios =
    {
        "jabstep-phases",
        "jabstep-no-placeholder-leak",
        "jabstep-segment-lengths",
        "jabstep-startup-differs-from-recovery",
        "jabstep-edges",
        "jabstep-torso-pitches-forward-in-active",
        "control-jabstep-torso-modest-in-startup",
        "jabstep-differs-from-retreatdribble",
    };

    // Scenarios that need no live tree — pure resource/scene inspection.
    //
    // `jabstep-differs-from-retreatdribble` USED to be in this list, back when
    // it was a guard that only checked whether a RetreatDribbleActive state
    // existed. #305 landed that state, so the scenario now does the real
    // cross-move comparison and needs a live rig for BOTH moves (#333).
    private static readonly string[] StaticScenarios =
    {
        "jabstep-no-placeholder-leak", "jabstep-segment-lengths", "jabstep-edges",
    };

    private string _scenario = "jabstep-phases";

    private BallController _ball;
    private PlayerController _actor; // peer "1" — the tipoff holder (ADR-0007)
    private PlayerController _other; // peer "2"

    private int _frame;
    private double _elapsed;
    private bool _finished;

    // The cross-move scenario (#333) runs TWO moves back to back on the same
    // actor: the jab step first, then — after starting a real dribble, which
    // RetreatDribble's dead-dribble gate requires and JabStep's does not — a
    // retreat dribble. StartDribble/AwaitDribble/Act2/Observe2 are that second
    // leg and are entered ONLY by that scenario.
    private enum Step { AwaitTipoff, Act, Observe, StartDribble, AwaitDribble, Act2, Observe2 }
    private Step _step = Step.AwaitTipoff;
    private int _stepDeadlineFrame;

    // Latched (event-time) observations. The three phase latches can only turn
    // true in order — each guard requires the previous already latched — so
    // "saw all three" IS "saw them in order."
    private bool _sawStartup;
    private bool _sawActive;
    private bool _sawRecovery;
    private bool _sawGenericPlaceholder;

    // Geometry, latched at event time (never recomputed at verdict time — by
    // then the move is over and the rig has returned to Locomotion). Each
    // "lean at last X tick" value is OVERWRITTEN every tick of phase X, so it
    // ends up holding the LAST one — the same "sample the final tick"
    // discipline BehindTheBackAnimTest/ContestAnimTest/LayupAnimTest arrived at
    // by mutation (an unbound clip collapses to rest within a tick — no xfade
    // on any edge — so the final tick is where bound and unbound actually
    // separate). Sampling the LAST tick of each phase also sidesteps the
    // phase-label-leads-pose-by-one-tick trap (#316) for that phase's FIRST
    // tick, which would otherwise still read the PREVIOUS phase's pose.
    private Vector3? _cachedForward;                // derived once, see MeasureSpineHeadForward
    private float _leanBeforeMove = float.NaN;      // sampled one tick before BeginMoveForHarness
    private float _leanAtLastStartupTick = float.NaN;
    private float _leanAtLastActiveTick = float.NaN;
    private float _leanAtLastRecoveryTick = float.NaN;
    private Quaternion[] _poseAtLastStartupTick;
    private Quaternion[] _poseAtLastRecoveryTick;

    // Second leg (#333, `jabstep-differs-from-retreatdribble` only): the same
    // three quantities re-measured for a RETREAT DRIBBLE on the same actor, in
    // the same skeleton space, against the same `_cachedForward` axis.
    private bool _sawRdActive;
    private int _jabActiveTicks;
    private int _rdActiveTicks;
    private float _leanBeforeRetreat = float.NaN;
    private float _leanAtLastRdActiveTick = float.NaN;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "jabstep-phases");
        GD.Print($"[jabstep-anim] scenario={_scenario} booting headless…");

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
        // chain runs every tick, same as LayupAnimTest/ContestAnimTest.
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
                _step = Step.Act;
                _stepDeadlineFrame = _frame + ActFrames;
                break;

            case Step.Act:
                if (_frame < _stepDeadlineFrame) break;
                // Sample the PRE-MOVE lean one tick before BeginMoveForHarness,
                // while the actor is still unambiguously in Locomotion/Dribble —
                // the one point in this whole run with NO tick-lag ambiguity
                // about which pose is being read (#316's trap). This is the
                // clean baseline the torso-lean scenarios compare against,
                // rather than the move's own first observed tick (which the
                // trap can still show as the PRE-move pose).
                {
                    var skelPre = FindSkeleton(_actor);
                    if (skelPre != null) _leanBeforeMove = MeasureSpineHeadForward(skelPre);
                }
                // The real production choke point (BeginCommittedMove), reached
                // via the generic harness seam — deliberately downstream of
                // JabStepLegalityResolver, which JabStepTest owns.
                if (!_actor.BeginMoveForHarness(new JabStep()))
                {
                    Fail($"{_scenario}: BeginMoveForHarness(new JabStep()) returned false — " +
                         "the actor's machine was not Inactive at begin.");
                    Finish();
                    return;
                }
                _step = Step.Observe;
                _stepDeadlineFrame = _frame + ObserveFrames;
                break;

            case Step.Observe:
                Observe();
                if (_frame < _stepDeadlineFrame) break;
                if (_scenario == "jabstep-differs-from-retreatdribble")
                {
                    // The jab leg is done and `_leanAtLastActiveTick` holds its
                    // Active-end reading. Hand off to the retreat-dribble leg
                    // rather than rendering a verdict.
                    _step = Step.StartDribble;
                    break;
                }
                RenderVerdict();
                break;

            // ── Second leg: the retreat dribble (#333) ───────────────────────
            case Step.StartDribble:
                // Re-pin the actor. The jab step's recovery hands control back
                // to Move(), so between the two legs the actor is free to drift
                // — and the comparison is only meaningful if both moves are
                // measured from the same stance and the same heading.
                _actor.GlobalPosition = ActorSpot;
                _actor.SetHeadingForHarness(
                    Mathf.Atan2(RimCenter.X - ActorSpot.X, RimCenter.Z - ActorSpot.Z));
                // RetreatDribble sits inside BeginCommittedMove's dead-dribble
                // gate ("you cannot retreat-dribble a ball you haven't started
                // bouncing"); JabStep does not, which is why leg 1 needed no
                // such step.
                _ball.TryStartDribble(1);
                _step = Step.AwaitDribble;
                _stepDeadlineFrame = _frame + ActFrames;
                break;

            case Step.AwaitDribble:
                if (_frame < _stepDeadlineFrame) break;
                if (_ball.State != BallState.Dribbling)
                {
                    Fail($"{_scenario}: TryStartDribble(1) did not reach BallState.Dribbling by frame " +
                         $"{_frame} (got {_ball.State}) — the retreat-dribble half of this comparison " +
                         "cannot legally begin without a live dribble.");
                    Finish();
                    return;
                }
                _step = Step.Act2;
                _stepDeadlineFrame = _frame + ActFrames;
                break;

            case Step.Act2:
                if (_frame < _stepDeadlineFrame) break;
                {
                    var skelPre = FindSkeleton(_actor);
                    if (skelPre != null) _leanBeforeRetreat = MeasureSpineHeadForward(skelPre);
                }
                if (!_actor.BeginMoveForHarness(new RetreatDribble()))
                {
                    Fail($"{_scenario}: BeginMoveForHarness(new RetreatDribble()) returned false — " +
                         "the actor's machine was not Inactive at begin, or the dead-dribble gate " +
                         $"refused it (ball state {_ball.State}).");
                    Finish();
                    return;
                }
                _step = Step.Observe2;
                _stepDeadlineFrame = _frame + ObserveFrames;
                break;

            case Step.Observe2:
                ObserveRetreatDribble();
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

        if (!_sawStartup && node == "JabStepStartup") _sawStartup = true;
        if (_sawStartup && !_sawActive && node == "JabStepActive") _sawActive = true;
        if (_sawActive && !_sawRecovery && node == "JabStepRecovery") _sawRecovery = true;
        if (node == "Startup" || node == "Active" || node == "Recovery") _sawGenericPlaceholder = true;

        // ── Geometry, sampled at event time ──────────────────────────────────
        var skel = FindSkeleton(_actor);
        if (skel == null) return;

        if (node != "JabStepStartup" && node != "JabStepActive" && node != "JabStepRecovery") return;

        float lean = MeasureSpineHeadForward(skel);

        if (node == "JabStepStartup")
        {
            _leanAtLastStartupTick = lean;
            _poseAtLastStartupTick = SampleUpperBody(skel);
        }
        else if (node == "JabStepActive")
        {
            _jabActiveTicks++;
            _leanAtLastActiveTick = lean;
        }
        else
        {
            // Overwritten each Recovery tick, so it ends up holding the LAST one
            // — the "sample the final tick" discipline established by
            // BehindTheBackAnimTest/ContestAnimTest/LayupAnimTest by mutation.
            _leanAtLastRecoveryTick = lean;
            _poseAtLastRecoveryTick = SampleUpperBody(skel);
        }
    }

    // Second leg of `jabstep-differs-from-retreatdribble` (#333). Deliberately
    // a separate method rather than a prefix-parameterised `Observe()`: the two
    // legs latch into different fields and only one of them needs the pose
    // snapshots, so parameterising would add a branch to every line of the
    // hot path to save nine.
    private void ObserveRetreatDribble()
    {
        string node = _actor.ActiveAnimNodeForHarness;
        if (node != "RetreatDribbleActive") return;

        var skel = FindSkeleton(_actor);
        if (skel == null) return;

        _sawRdActive = true;
        _rdActiveTicks++;
        // Overwritten every Active tick, so it ends up holding the LAST one —
        // the same "sample the final tick" discipline leg 1 uses, and the same
        // reason (#316's phase-label-leads-pose-by-one-tick trap).
        _leanAtLastRdActiveTick = MeasureSpineHeadForward(skel);
    }

    private void RenderVerdict()
    {
        switch (_scenario)
        {
            case "jabstep-phases":                              VerdictPhases(); break;
            case "jabstep-startup-differs-from-recovery":       VerdictStartupDiffersFromRecovery(); break;
            case "jabstep-torso-pitches-forward-in-active":     VerdictTorsoPitchesForward(); break;
            case "control-jabstep-torso-modest-in-startup":     VerdictControlTorsoModestInStartup(); break;
            case "jabstep-differs-from-retreatdribble":         VerdictDiffersFromRetreatDribble(); break;
        }
    }

    // ── Scenario: jabstep-differs-from-retreatdribble (#333) ────────────────
    // The only automated defence against the two clips converging, and the
    // reason it is worth the cross-file coupling: jab step and retreat dribble
    // are 3/2/4 ticks off the SAME assets/Dribble.fbx with the same
    // three-held-poses structure, so at 0.150 s the torso lean SIGN is the only
    // read that separates them (handoffs 04 and 05 both say so in as many
    // words). Two indistinguishable committed moves is an ADR-0003 false read.
    //
    // WAS a guard. Until #305 landed there was no RetreatDribbleActive state,
    // so this scenario detected its absence, printed SKIPPED and exited 0 —
    // and, once the state DID appear, deliberately hard-Failed with "implement
    // it against #305's actual authored shape rather than a guess made before
    // #305 existed". That tripwire fired as designed; this is the implementation
    // it was holding the place for.
    //
    // ── Why the comparison is of DELTAS, not of absolute leans ─────────────
    // Because the absolute reading has no stable zero, and MEASUREMENT SHOWS
    // IT MOVING. This quantity is projected onto `_cachedForward`, a horizontal
    // axis derived once from the skeleton's own foot->toe vector at whichever
    // pose the harness happened to cache it at — which is NOT anatomical
    // vertical. The very same retreat-dribble Active-end pose reads:
    //
    //     -0.0171   here          (axis cached at the JAB's pre-move Locomotion pose)
    //     +0.0503   RetreatDribbleAnimTest (axis cached at the DRIBBLE pose)
    //
    // So an absolute-value comparison would be reporting on where the axis got
    // cached as much as on the clips. The DELTA off each move's own immediately
    // preceding baseline cancels that offset exactly, and it is also precisely
    // the quantity #333 asks for ("sample both moves' Active-phase
    // torso-forward-lean DELTA ... and assert the two deltas have opposite
    // sign"). MEASURED here:
    //
    //     jab step         +0.1356   (leans INTO the stab)
    //     retreat dribble  -0.1804   (leans AWAY over a retreating base)
    //
    // The ABSOLUTE "past vertical" claim about the retreat dribble is a real
    // one, but it is asserted where the reference IS well-defined:
    // rebuild_retreatdribble_clips.gd's G5 (-0.0460 m, against Y Bot's rest
    // chain) and author_retreatdribble.py's `_verify_torso_at_or_past_vertical`
    // (-0.0402 m, against the rig's rest-derived `up`).
    //
    // Both legs run on ONE actor, in ONE skeleton space, against ONE cached
    // forward axis, so this is a genuine comparison rather than two readings
    // taken in different frames.
    private void VerdictDiffersFromRetreatDribble()
    {
        float jabDelta = _leanAtLastActiveTick - _leanBeforeMove;
        float rdDelta = _leanAtLastRdActiveTick - _leanBeforeRetreat;

        GD.Print($"[jabstep-anim]   jab step:        beforeMove={_leanBeforeMove:F4} " +
                 $"activeEnd={_leanAtLastActiveTick:F4} delta={jabDelta:+0.0000;-0.0000} " +
                 $"(activeTicks={_jabActiveTicks})");
        GD.Print($"[jabstep-anim]   retreat dribble: beforeMove={_leanBeforeRetreat:F4} " +
                 $"activeEnd={_leanAtLastRdActiveTick:F4} delta={rdDelta:+0.0000;-0.0000} " +
                 $"(activeTicks={_rdActiveTicks})");

        // Premise, asserted rather than assumed. Both moves must actually have
        // been observed on their Active state for MORE THAN ONE tick — on a
        // single observation the "last" tick IS the first, which under #316 can
        // still hold the previous phase's pose. Active is 2 ticks for both
        // moves, the tightest window in the batch.
        bool premise = _sawActive && _sawRdActive &&
                       _jabActiveTicks >= 2 && _rdActiveTicks >= 2 &&
                       !float.IsNaN(_leanBeforeMove) && !float.IsNaN(_leanBeforeRetreat);

        // Each delta must clear the same magnitude floor its own move's
        // dedicated scenario uses, in its own direction. A bare sign test would
        // be satisfied by two clips that barely moved at all — including two
        // UNBOUND clips, whose collapse to rest would produce arbitrary small
        // deltas of arbitrary sign.
        bool jabLeansIn = jabDelta >= TorsoForwardGrowthMinM;
        bool rdLeansAway = rdDelta <= -TorsoForwardGrowthMinM;
        bool pass = premise && jabLeansIn && rdLeansAway;

        if (pass)
            GD.Print($"[jabstep-anim] PASS jabstep-differs-from-retreatdribble — the two clips' Active-phase " +
                     $"torso travel has OPPOSITE SIGN off their own pre-move baselines (jab step " +
                     $"{jabDelta:+0.0000;-0.0000}, retreat dribble {rdDelta:+0.0000;-0.0000}, each clearing " +
                     $"±{TorsoForwardGrowthMinM:F2}). The jab leans INTO the stab, the retreat leans AWAY — " +
                     "so the two moves, which share tick counts, source FBX and pose structure, remain " +
                     "distinguishable at 0.150 s.");
        else
            Fail($"jabstep-differs-from-retreatdribble: jabDelta={jabDelta:+0.0000;-0.0000} " +
                 $"(need >= +{TorsoForwardGrowthMinM:F2}), rdDelta={rdDelta:+0.0000;-0.0000} " +
                 $"(need <= -{TorsoForwardGrowthMinM:F2}), premise={premise} " +
                 $"(sawActive={_sawActive}, sawRdActive={_sawRdActive}, jabActiveTicks={_jabActiveTicks}, " +
                 $"rdActiveTicks={_rdActiveTicks}, both need >= 2). Same-sign deltas mean the two clips have " +
                 "CONVERGED on one read — check TORSO_PITCH_SIGN in author_jabstep.py " +
                 "(-1.0, forward) and author_retreatdribble.py (+1.0, backward).");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: jabstep-phases (positive) ─────────────────────────────────
    private void VerdictPhases()
    {
        bool pass = _sawStartup && _sawActive && _sawRecovery;
        if (pass)
            GD.Print("[jabstep-anim] PASS jabstep-phases — the tree was observed on \"JabStepStartup\", then " +
                     "\"JabStepActive\", then \"JabStepRecovery\", in that order (the .tscn states and their " +
                     "transitions are live).");
        else
            Fail($"jabstep-phases: expected JabStepStartup -> JabStepActive -> JabStepRecovery, in order; got " +
                 $"sawStartup={_sawStartup}, sawActive={_sawActive}, sawRecovery={_sawRecovery}, " +
                 $"sawGenericPlaceholder={_sawGenericPlaceholder}, " +
                 $"lastAnimNode={_actor.ActiveAnimNodeForHarness}.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: jabstep-startup-differs-from-recovery ─────────────────────
    // #296's ACTUAL complaint, and unusually load-bearing here: with three held
    // poses (README's "<=3-tick segments are single poses" rule), if Startup
    // and Recovery coincide the move has no arc at all.
    private void VerdictStartupDiffersFromRecovery()
    {
        if (_poseAtLastStartupTick == null || _poseAtLastRecoveryTick == null)
        {
            Fail("jabstep-startup-differs-from-recovery: never sampled both a Startup and a Recovery tick " +
                 $"(sawStartup={_sawStartup}, sawRecovery={_sawRecovery}) — the premise for comparing them " +
                 "never held, so this fails rather than passes.");
            Finish(1);
            return;
        }

        float worst = 0f;
        for (int i = 0; i < _poseAtLastStartupTick.Length; i++)
            worst = Math.Max(worst, Mathf.RadToDeg(
                _poseAtLastStartupTick[i].AngleTo(_poseAtLastRecoveryTick[i])));

        GD.Print($"[jabstep-anim]   worst upper-body Startup-vs-Recovery delta = {worst:F2} deg " +
                 $"(floor {StartupVsRecoveryMinDeg:F1})");

        bool pass = _sawStartup && _sawRecovery && worst >= StartupVsRecoveryMinDeg;
        if (pass)
            GD.Print("[jabstep-anim] PASS jabstep-startup-differs-from-recovery — the last Startup pose and " +
                     $"the last Recovery pose differ by {worst:F2} deg, so the wind-up and the punish window " +
                     "are visibly distinct silhouettes (#296) even at 9 ticks total.");
        else
            Fail($"jabstep-startup-differs-from-recovery: worst delta {worst:F2} deg < " +
                 $"{StartupVsRecoveryMinDeg:F1}. Either the two states point at the same clip, or the clips " +
                 "bind to nothing on this rig (check for Blender's 'Armature/' track-path prefix) and both " +
                 "poses collapsed to rest.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: jabstep-torso-pitches-forward-in-active (positive) ────────
    // The issue's defining contrast with retreat dribble (#305): jab step
    // pitches FORWARD over the extended front foot. Measured resource-side by
    // rebuild_jabstep_clips.gd's G4 gate (35.1 deg / 0.0617 m forward growth
    // Startup-end -> Active-end); re-measured here on the LIVE Skeleton3D
    // mid-move.
    //
    // Both this scenario and its control compare against `_leanBeforeMove` —
    // sampled ONE TICK BEFORE BeginMoveForHarness, while the actor is
    // unambiguously still in Locomotion/Dribble — rather than against the
    // move's own first observed Startup tick. That choice is deliberate: the
    // #316 phase-label-leads-pose-by-one-tick trap means the first tick
    // GetCurrentNode() reports "JabStepStartup" can still hold the PRE-move
    // pose, and at a 3-tick Startup window that ambiguity is a third of the
    // whole phase. `_leanBeforeMove` has no such ambiguity: it is sampled
    // before the state transition exists at all.
    private void VerdictTorsoPitchesForward()
    {
        float delta = _leanAtLastActiveTick - _leanBeforeMove;
        GD.Print($"[jabstep-anim]   torso-forward lean: beforeMove={_leanBeforeMove:F4} " +
                 $"activeEnd={_leanAtLastActiveTick:F4} delta={delta:F4} (floor {TorsoForwardGrowthMinM:F2})");

        bool premise = _sawActive && !float.IsNaN(_leanBeforeMove);
        bool pass = premise && delta >= TorsoForwardGrowthMinM;
        if (pass)
            GD.Print($"[jabstep-anim] PASS jabstep-torso-pitches-forward-in-active — the spine->head vector's " +
                     $"forward projection grew {delta:F4} m from the pre-move stance to Active's last tick " +
                     $"(floor {TorsoForwardGrowthMinM:F2}), so the jab genuinely leans INTO the stab.");
        else
            Fail($"jabstep-torso-pitches-forward-in-active: pre-move -> Active-end delta was {delta:F4}, " +
                 $"need >= {TorsoForwardGrowthMinM:F2} (sawActive={_sawActive}, leanBeforeMove={_leanBeforeMove:F4}). " +
                 "Either the clip is unbound (silent no-op, README trap 13) or TORSO_PITCH_SIGN regressed.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: control-jabstep-torso-modest-in-startup (control) ─────────
    // The paired control: Startup's OWN last tick must grow measurably LESS
    // off the pre-move baseline than Active's does — the wind-up is a smaller
    // commitment than the stab itself, so the big lean is a localised EVENT at
    // Active rather than something the 3-tick Startup window already fully
    // committed to.
    //
    // Framed as "Startup's growth < Active's growth", not "Startup's growth
    // stays near zero": author_jabstep.py's own keypose table intentionally
    // brings the wind-up MOST of the way toward the stab pose by the end of a
    // <=3-tick Startup (the same choice author_contest.py made for its 6-tick
    // Startup, and for the same reason — the read has to be mostly there by
    // the time Active begins). A near-zero ceiling would fight that authored
    // intent instead of testing it; a strictly-smaller-than-Active margin is
    // the honest claim.
    private void VerdictControlTorsoModestInStartup()
    {
        float startupGrowth = _leanAtLastStartupTick - _leanBeforeMove;
        float activeGrowth = _leanAtLastActiveTick - _leanBeforeMove;
        float margin = activeGrowth - startupGrowth;
        GD.Print($"[jabstep-anim]   torso-forward growth off pre-move baseline: startup={startupGrowth:F4} " +
                 $"active={activeGrowth:F4} margin={margin:F4} (floor {TorsoForwardSettleMinM:F2})");

        // Premise: Active must genuinely have shown the forward lean (the
        // positive gate above), or "Startup grew less" is trivially true of a
        // clip where nothing ever moved forward in the first place.
        bool premise = _sawStartup && _sawActive && !float.IsNaN(_leanBeforeMove) &&
                       activeGrowth >= TorsoForwardGrowthMinM;
        bool pass = premise && margin >= TorsoForwardSettleMinM;
        if (pass)
            GD.Print($"[jabstep-anim] PASS control-jabstep-torso-modest-in-startup — Startup's own growth " +
                     $"({startupGrowth:F4}) stayed {margin:F4} below Active's ({activeGrowth:F4}, floor " +
                     $"{TorsoForwardSettleMinM:F2}), so the forward commitment is still deepening when Active " +
                     "begins rather than being fully spent by the end of the wind-up.");
        else
            Fail($"control-jabstep-torso-modest-in-startup: startupGrowth={startupGrowth:F4}, " +
                 $"activeGrowth={activeGrowth:F4}, margin={margin:F4} (need >= {TorsoForwardSettleMinM:F2}), " +
                 $"premise={premise}. If the premise broke, this fails rather than passes.");
        Finish(pass ? 0 : 1);
    }

    // ── Static scenarios: no live tree, pure resource/scene inspection ──────
    private void RunStaticCheck()
    {
        switch (_scenario)
        {
            case "jabstep-no-placeholder-leak":         RunNoPlaceholderLeakCheck(); break;
            case "jabstep-segment-lengths":              RunSegmentLengthsCheck(); break;
            case "jabstep-edges":                        RunEdgesCheck(); break;
        }
    }

    // ── Scenario: jabstep-segment-lengths ───────────────────────────────────
    // #276 rule 4 / #295. This matters unusually much here: Active is 2 ticks
    // = 0.0333 s, so an off-by-one tick is a 50% length error, not a rounding
    // nicety. Tick windows are read from JabStep.DefaultFrameData, NOT
    // hardcoded, so a future retune that forgets to re-run
    // tools/rebuild_jabstep_clips.gd goes red here and names the tool.
    private void RunSegmentLengthsCheck()
    {
        var lib = GD.Load<AnimationLibrary>("res://assets/locomotion.res");
        if (lib == null)
        {
            Fail("assets/locomotion.res failed to load — cannot evaluate jabstep-segment-lengths.");
            Finish(1);
            return;
        }

        double tps = Engine.PhysicsTicksPerSecond;
        var frames = JabStep.DefaultFrameData;
        // A ONE-TICK tolerance would defeat this scenario's whole stated
        // purpose on this move specifically: Active is only 2 ticks, so a
        // one-tick bar (1/60 s = 0.0167 s) is HALF the clip's own length and
        // would wave through a 3-tick mis-slice as "within tolerance". The
        // slice is exact to float noise (~1e-5 s), so this is a noise band,
        // not a drift allowance.
        const double ToleranceSeconds = 1e-3;

        (string Clip, int Ticks)[] windows =
        {
            ("jabstepstartup",  frames.StartupFrames),
            ("jabstepactive",   frames.ActiveFrames),
            ("jabsteprecovery", frames.RecoveryFrames),
        };

        bool pass = true;
        foreach (var (clipName, ticks) in windows)
        {
            if (!lib.HasAnimation(clipName))
            {
                Fail($"AnimationLibrary has no clip '{clipName}' — run tools/rebuild_jabstep_clips.gd.");
                pass = false;
                continue;
            }

            double expectedSeconds = ticks / tps;
            // Variant accessor, NOT .Length: that property is `float` in Godot 4.6.x
            // and `double` in 4.7, so a 4.7.1-built assembly throws
            // MissingMethodException under a stale 4.6 binary — and it throws
            // inside _PhysicsProcess, BEFORE the timeout check, so the scenario
            // HANGS instead of failing (#339 measured all 8 of these). The
            // Variant accessor binds correctly under both. See AuthoredClipMcpProbe.
            double actualSeconds = lib.GetAnimation(clipName).Get("length").AsDouble();
            double deviationSeconds = Math.Abs(actualSeconds - expectedSeconds);
            GD.Print($"[jabstep-anim]   '{clipName}': length={actualSeconds:F6}s " +
                     $"expected={expectedSeconds:F6}s ({ticks} ticks @ {tps} tps), " +
                     $"deviation={deviationSeconds:F6}s");

            if (deviationSeconds > ToleranceSeconds)
            {
                Fail($"clip '{clipName}' is {actualSeconds:F6}s, expected {expectedSeconds:F6}s " +
                     $"({ticks} ticks at {tps} tps — JabStep.DefaultFrameData), a deviation of " +
                     $"{deviationSeconds:F6}s exceeds the float-noise tolerance ({ToleranceSeconds:F6}s). " +
                     "Re-run tools/rebuild_jabstep_clips.gd after retuning the move's frame data.");
                pass = false;
            }
        }

        if (pass)
            GD.Print("[jabstep-anim] PASS jabstep-segment-lengths — all three clips' durations match " +
                     "JabStep.DefaultFrameData's Startup/Active/Recovery windows to within float noise " +
                     $"({ToleranceSeconds:F6}s). A one-tick retune of the 2-tick Active window (a 50% error) " +
                     "goes red here.");
        else
            GD.PrintErr("[jabstep-anim] FAIL jabstep-segment-lengths — see per-clip deviations above.");

        Finish(pass ? 0 : 1);
    }

    // ── Scenario: jabstep-no-placeholder-leak ───────────────────────────────
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
            ("JabStepStartup",  "locomotion/jabstepstartup"),
            ("JabStepActive",   "locomotion/jabstepactive"),
            ("JabStepRecovery", "locomotion/jabsteprecovery"),
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
            GD.Print($"[jabstep-anim]   {stateName} -> {actualClip}");

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
            GD.Print("[jabstep-anim] PASS jabstep-no-placeholder-leak — all three JabStep states point at " +
                     "their OWN per-move clips, not the shared locomotion/idle placeholder #304 moved them off of.");
        else
            GD.PrintErr("[jabstep-anim] FAIL jabstep-no-placeholder-leak — see per-state mismatches above.");

        Finish(pass ? 0 : 1);
    }

    // ── Scenario: jabstep-edges ──────────────────────────────────────────────
    // README trap 8 / #279: a DELETED transition edge is invisible to
    // GetCurrentNode() (Travel() is a pathfinder and routes around the gap),
    // so this reads GetTransitionCount()/From()/To() off the RESOURCE, where a
    // missing edge is simply absent. Jab step is an OFFENSIVE (dribble-family)
    // move, so it needs the six standard edges AND the dribble-family
    // entries/exits — doubled since #294 split Dribble into
    // DribbleLeft/DribbleRight (see the file header: #294 had already landed
    // when this issue was picked up, so this asserts 12 edges total, matching
    // LayupAnimTest's own precedent rather than the issue handoff's stale
    // 9-edge count).
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
            ("Locomotion", "JabStepStartup"),
            ("JabStepStartup", "JabStepActive"),
            ("JabStepActive", "JabStepRecovery"),
            ("JabStepRecovery", "Locomotion"),
            ("JabStepStartup", "JabStepRecovery"), // feint / early-out
            ("JabStepStartup", "Locomotion"),      // abort
            // The dribble family, doubled by #294.
            ("DribbleLeft", "JabStepStartup"),
            ("DribbleRight", "JabStepStartup"),
            ("JabStepRecovery", "DribbleLeft"),
            ("JabStepRecovery", "DribbleRight"),
            ("JabStepStartup", "DribbleLeft"),
            ("JabStepStartup", "DribbleRight"),
        };

        var present = new System.Collections.Generic.HashSet<string>();
        for (int i = 0; i < sm.GetTransitionCount(); i++)
            present.Add($"{sm.GetTransitionFrom(i)}->{sm.GetTransitionTo(i)}");

        bool pass = true;
        foreach (var (from, to) in required)
        {
            bool here = present.Contains($"{from}->{to}");
            GD.Print($"[jabstep-anim]   edge {from} -> {to}: {(here ? "present" : "MISSING")}");
            if (!here)
            {
                Fail($"scenes/Player.tscn has no transition '{from}' -> '{to}'. Travel()'s pathfinder will " +
                     "route around the gap, so NO runtime scenario can catch this — only this resource-level " +
                     "check can.");
                pass = false;
            }
        }

        if (pass)
            GD.Print($"[jabstep-anim] PASS jabstep-edges — all {required.Length} required transitions are " +
                     "present (6 standard + 6 dribble-family, the latter doubled by #294's " +
                     "DribbleLeft/DribbleRight split).");
        else
            GD.PrintErr("[jabstep-anim] FAIL jabstep-edges — see missing transitions above.");

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

    // Spine->Head vector's projection along the rig's own FORWARD axis — the
    // live-rig equivalent of rebuild_jabstep_clips.gd's `_spine_head_forward` /
    // author_jabstep.py's `_torso_pitch_sign_is_forward` oracle.
    //
    // Deliberately does NOT use the actor's world heading (e.g.
    // `-GlobalTransform.Basis.Z`) — measured live, that gave a NEGATIVE growth
    // (-0.1835) for a clip whose Blender/rebuild-side proofs both independently
    // confirmed a forward lean, which means the sign convention between
    // "Godot forward" and this rig's own facing do not agree at this
    // particular authored heading. Instead `forward` is derived ONCE (cached)
    // from the SAME skeleton's own LeftFoot->LeftToeBase vector, projected to
    // the horizontal plane — the toe is anatomically ahead of the ankle, the
    // same anchor rebuild_jabstep_clips.gd's `_derive_body_axes()` uses. Since
    // the actor's whole-body orientation is frozen for the duration of a
    // committed move (PlayerController skips Move() while the machine is
    // active), deriving it once from ANY bone at the baseline tick and reusing
    // it for every later measurement in the SAME global space is exact, and it
    // requires no assumption about Godot's world-forward convention at all.
    private float MeasureSpineHeadForward(Skeleton3D skel)
    {
        int spine = skel.FindBone("mixamorig_Spine");
        int head = skel.FindBone("mixamorig_Head");
        if (spine < 0 || head < 0) return float.NaN;

        if (_cachedForward == null)
        {
            int foot = skel.FindBone("mixamorig_LeftFoot");
            int toe = skel.FindBone("mixamorig_LeftToeBase");
            if (foot < 0 || toe < 0) return float.NaN;
            Vector3 raw = skel.GetBoneGlobalPose(toe).Origin - skel.GetBoneGlobalPose(foot).Origin;
            raw.Y = 0f;
            if (raw.LengthSquared() < 1e-6f) return float.NaN;
            _cachedForward = raw.Normalized();
        }

        Vector3 spineToHead = skel.GetBoneGlobalPose(head).Origin - skel.GetBoneGlobalPose(spine).Origin;
        return spineToHead.Dot(_cachedForward.Value);
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

    private void Fail(string message) => GD.PrintErr($"[jabstep-anim] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[jabstep-anim] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
