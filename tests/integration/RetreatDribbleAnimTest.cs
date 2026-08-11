using System;
using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #305 (ADR-0016): proves the THREE
// RETREAT DRIBBLE ANIMATION STATES (RetreatDribbleStartup / RetreatDribbleActive
// / RetreatDribbleRecovery) wired into scenes/Player.tscn are real — entered
// end-to-end by a real RetreatDribble, bound to the right clips, cut to the
// right windows, and actually MOVING the rig.
//
// Before #305 "retreatdribble" fell through MoveAnimResolver.ResolveStateName's
// default case onto the shared generic Startup/Active/Recovery states, which
// per #296 render a 3-tick LOOPING IDLE for Startup/Recovery (pixel-identical)
// and a 2-tick slice of a SPRINT STRIDE for Active — an actively false read for
// a move whose entire purpose is to sell "I am leaving" (RetreatDribble.cs's
// own class doc: "a light backward hop off a live dribble that creates a sliver
// of separation without spending the dribble").
//
//   godot --headless --path . res://tests/integration/RetreatDribbleAnimTest.tscn -- --harness-scenario=retreatdribble-phases
//   …=retreatdribble-no-placeholder-leak | retreatdribble-segment-lengths
//   …=retreatdribble-edges | retreatdribble-startup-differs-from-recovery
//   …=retreatdribble-torso-goes-back-in-active
//   …=control-retreatdribble-torso-modest-in-startup
//   …=retreatdribble-hips-stay-in-place
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//
// ── moveId IS "retreatdribble" ───────────────────────────────────────────────
// Unlike jab step — whose CommittedMove.Id is "jab" while its states are
// "JabStep*" — RetreatDribble.cs:47 constructs with `id: "retreatdribble"`, so
// the ClippedMovePrefixes key and the moveId coincide. That lookup is an EXACT
// TryGetValue, not a prefix match, despite the dictionary's name (the name
// describes the VALUE, which is a state-name prefix).
//
// ── #294 had already landed ──────────────────────────────────────────────────
// scenes/Player.tscn's Dribble state is split into DribbleLeft/DribbleRight, so
// the dribble-family edges are DOUBLED: this harness asserts 12 edges (6
// standard + 6 dribble-family), matching JabStepAnimTest/LayupAnimTest's own
// shape rather than the issue's stale 9-edge count.
//
// ── Cosmetic-only (issue #305's standing constraint) ────────────────────────
// #305 is a CLIP issue. It does not observe or feed BallState, HasDribbled, or
// RetreatDribbleBurstSpeed. This harness begins the move via
// BeginMoveForHarness — downstream of every gate StepBackTest already owns
// (`retreat-dribble-no-gather`, `retreat-dribble-dead-dribble-gate`) —
// precisely so it cannot accidentally become a second, weaker test of them.
//
// ── Why ActiveAnimNodeForHarness, not the resolver's return value (#257) ─────
// Travel() to a missing/misnamed state only LOGS; it never throws. Asserting
// MoveAnimResolver.ResolveStateName(...) == "RetreatDribbleActive" would pass on
// a Player.tscn with no RetreatDribble states at all. Only the live
// AnimationNodeStateMachinePlayback proves wiring.
//
// ── The jab-step contrast (#304), and where it is asserted ──────────────────
// Retreat dribble and jab step are 3/2/4 ticks off the SAME assets/Dribble.fbx
// with the same three-held-poses structure, so at 0.150 s the only read that
// survives is the torso lean SIGN. This file proves this clip's own half of
// that ("the torso goes upright-to-BACK"); the CROSS-MOVE opposite-sign
// assertion lives in JabStepAnimTest's `jabstep-differs-from-retreatdribble`
// (#333), where it was already written and registered in ci.yml.
public partial class RetreatDribbleAnimTest : Node
{
    private const double TimeoutSeconds = 15.0;
    private const int ArmFrames = 2;      // ticks for TryAssignTipoffHolder to run
    private const int ActFrames = 2;      // ticks after tipoff before Begin (position/heading settle)
    // startup(3)+active(2)+recovery(4)=9, with generous slack — this move ties
    // jab step as the smallest committed move in the game, so even a 3x margin
    // is cheap.
    private const int ObserveFrames = 30;

    private static readonly Vector3 ActorSpot = new(0f, 0f, 2f);
    private static readonly Vector3 FarSpot = new(12f, 0f, 12f); // out of PickupRadius
    private static readonly Vector3 RimCenter = new(0f, 3.05f, 0f); // BallController.DefaultRimCenter

    // Bones for the Startup-vs-Recovery pose comparison. Like jab step's set
    // this extends down the whole leg chain rather than covering only the
    // upper body: this move's read is a WHOLE-BASE event (both feet drift
    // forward relative to the hips, then re-plant into a lower loaded stance),
    // so the largest deltas live in the legs. The rebuild script's own G3,
    // which sweeps all 65 bones, measured 35.1 deg.
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

    // ── Thresholds ──────────────────────────────────────────────────────────
    // EVERY floor below is set from a value MEASURED BY THIS HARNESS ON THE
    // LIVE RIG, never from author_retreatdribble.py's keypose table or from the
    // rebuild script's gates. Those two live in different spaces — armature
    // space through an FBX round-trip, and a manual-FK reconstruction off Y
    // Bot's rest — and #308's own "Known-red" comment went stale exactly by
    // quoting an authored number (0.2177) at a gate whose live value was
    // 0.1483. The live numbers each constant was derived from are recorded
    // alongside it.
    //
    // Concretely, the three spaces disagree on where "vertical" is: the same
    // Active-end pose reads -0.0402 m Blender-side, -0.0460 m resource-side and
    // +0.0503 m here. That is why every gate in THIS file is a RELATIVE claim
    // (travel off a pre-move baseline) and the ABSOLUTE "past vertical" claim
    // is made only where the reference is well-defined — see
    // rebuild_retreatdribble_clips.gd's G5.

    // MEASURED: 30.42 deg on the live rig (35.1 deg resource-side, 30.118 deg
    // Blender-side). The floor sits well below all three and well above the
    // #296 defect's ~0 deg (both phases sharing the generic idle placeholder).
    private const float StartupVsRecoveryMinDeg = 15.0f;

    // MEASURED: the spine->head forward projection travels -0.2016 m from the
    // pre-move dribble stance (+0.2519) to Active's last tick (+0.0503). ~10x
    // headroom. Deliberately the SAME NUMBER as JabStepAnimTest's
    // TorsoForwardGrowthMinM so the two sibling gates are comparable line for
    // line — this move simply clears it in the opposite direction, and by a
    // wider margin (jab step measures +0.1356).
    private const float TorsoBackwardGrowthMinM = 0.02f;

    // MEASURED: Startup's own travel is -0.0951 and Active's is -0.2016, so the
    // margin is 0.1065. ~2.7x headroom.
    //
    // Set materially HIGHER than JabStepAnimTest's equivalent (0.015 against a
    // measured 0.0234) because this move genuinely has the room and the jab
    // does not: the jab's keypose table brings its wind-up almost all the way
    // to the stab pose, while this clip's Startup completes only 25 deg of its
    // 35 deg counter-rotation. A floor of 0.015 here would clear by 7x and
    // stop being a claim about anything.
    private const float TorsoBackwardSettleMinM = 0.04f;

    // The Hips bone's horizontal drift in SKELETON space over the whole move —
    // the #305-specific gate; see VerdictHipsStayInPlace for why it exists.
    //
    // MEASURED: 0.0004 m, i.e. float noise plus the position track's own
    // interpolation. The defect it guards is not subtle — handoff 05's motion
    // spec names 0.25 m — so this sits 25x above the honest reading and still
    // reddens on any root translation of a centimetre or more. Not set at the
    // measured value: that would make the gate fire on interpolation noise
    // rather than on a doubled retreat.
    private const float HipsHorizontalDriftMaxM = 0.01f;

    private static readonly string[] KnownScenarios =
    {
        "retreatdribble-phases",
        "retreatdribble-no-placeholder-leak",
        "retreatdribble-segment-lengths",
        "retreatdribble-edges",
        "retreatdribble-startup-differs-from-recovery",
        "retreatdribble-torso-goes-back-in-active",
        "control-retreatdribble-torso-modest-in-startup",
        "retreatdribble-hips-stay-in-place",
    };

    // Scenarios that need no live tree — pure resource/scene inspection.
    private static readonly string[] StaticScenarios =
    {
        "retreatdribble-no-placeholder-leak",
        "retreatdribble-segment-lengths",
        "retreatdribble-edges",
    };

    private string _scenario = "retreatdribble-phases";

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

    // Per-phase observed-tick counts. Printed and, for Active, ASSERTED: the
    // #316/#340 trap is that the first tick GetCurrentNode() names a phase can
    // still hold the PREVIOUS phase's pose. Sampling each phase's LAST tick
    // sidesteps that — but only if the phase was observed for MORE THAN ONE
    // tick, because on a single-tick observation the last tick IS the first
    // one. Active is only 2 ticks here, so this is the tightest window in the
    // batch and the count is a premise, not a diagnostic.
    private int _startupTicks;
    private int _activeTicks;
    private int _recoveryTicks;

    // Geometry, latched at event time (never recomputed at verdict time — by
    // then the move is over and the rig has returned to Locomotion/Dribble).
    // Each "lean at last X tick" value is OVERWRITTEN every tick of phase X, so
    // it ends up holding the LAST one.
    private Vector3? _cachedForward;                // derived once, see MeasureSpineHeadForward
    private float _leanBeforeMove = float.NaN;      // sampled one tick before BeginMoveForHarness
    private float _leanAtLastStartupTick = float.NaN;
    private float _leanAtLastActiveTick = float.NaN;
    private Quaternion[] _poseAtLastStartupTick;
    private Quaternion[] _poseAtLastRecoveryTick;

    // Hips bone origin in SKELETON space, sampled pre-move and then on every
    // tick of the move. The horizontal SPAN of the move's own samples is the
    // in-place proof; see VerdictHipsStayInPlace.
    private Vector3? _hipsBeforeMove;
    private float _worstHipsBaselineOffset;   // informational only, not gated
    private bool _hipsBoneMissing;            // a resolution failure must be RED, not 0.0000
    private int _hipsObservedTicks;
    private int _hipsSpannedTicks;            // observed ticks minus the #316 lead-in tick
    private float _hipsMinX = float.PositiveInfinity;
    private float _hipsMaxX = float.NegativeInfinity;
    private float _hipsMinZ = float.PositiveInfinity;
    private float _hipsMaxZ = float.NegativeInfinity;

    // The diagonal of the horizontal bounding box the Hips swept during the
    // move. Zero-width when fewer than two ticks contributed, which the
    // verdict's premise rejects rather than reads as a clean pass.
    private float HipsHorizontalSpan =>
        _hipsSpannedTicks < 2
            ? 0f
            : new Vector2(_hipsMaxX - _hipsMinX, _hipsMaxZ - _hipsMinZ).Length();

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "retreatdribble-phases");
        GD.Print($"[retreatdribble-anim] scenario={_scenario} booting headless…");

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
        // chain runs every tick, same as JabStepAnimTest.
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
                // RetreatDribble is inside BeginCommittedMove's dead-dribble
                // gate (PlayerController.cs's own comment: "'the ball stays
                // Dribbling' only makes sense starting FROM a live dribble —
                // you cannot retreat-dribble a ball you haven't started
                // bouncing"), so a fresh live HELD possession off the tipoff is
                // REFUSED. StepBackTest's `retreat-dribble-dead-dribble-gate`
                // is the scenario that owns proving that refusal; this harness
                // must therefore start a real dribble first, exactly as
                // InAndOutAnimTest does for the same gate.
                //
                // Worth stating because it improves this file's own baseline:
                // it means `_leanBeforeMove` is sampled while the actor is in
                // the Dribble BlendSpace — the crouching dribble stance the
                // clip was authored against — rather than in a neutral
                // Locomotion pose. That is the honest reference for "the torso
                // went upright-to-back FROM the dribble stance".
                _ball.TryStartDribble(1);
                _step = Step.AwaitDribble;
                _stepDeadlineFrame = _frame + ActFrames;
                break;

            case Step.AwaitDribble:
                if (_frame < _stepDeadlineFrame) break;
                if (_ball.State != BallState.Dribbling)
                {
                    Fail($"{_scenario}: TryStartDribble(1) did not reach BallState.Dribbling by frame " +
                         $"{_frame} (got {_ball.State}) — RetreatDribble cannot legally begin without a " +
                         "live dribble.");
                    Finish();
                    return;
                }
                _step = Step.Act;
                _stepDeadlineFrame = _frame + ActFrames;
                break;

            case Step.Act:
                if (_frame < _stepDeadlineFrame) break;
                // Sample the PRE-MOVE baseline one tick before BeginMoveForHarness,
                // while the actor is still unambiguously in Locomotion/Dribble —
                // the one point in this whole run with NO tick-lag ambiguity
                // about which pose is being read (#316's trap). Both the torso
                // scenarios and the in-place scenario compare against this
                // rather than against the move's own first observed tick.
                {
                    var skelPre = FindSkeleton(_actor);
                    if (skelPre != null)
                    {
                        _leanBeforeMove = MeasureSpineHeadForward(skelPre);
                        _hipsBeforeMove = MeasureHipsLocal(skelPre);
                    }
                }
                // The real production choke point (BeginCommittedMove), reached
                // via the generic harness seam — deliberately downstream of the
                // BallState gating StepBackTest owns.
                if (!_actor.BeginMoveForHarness(new RetreatDribble()))
                {
                    // Name BOTH causes, and print the ball state. The
                    // dead-dribble gate (#193 family) rejects RetreatDribble
                    // from a Held ball, and during development THIS message —
                    // naming only the move machine — is what that rejection
                    // surfaced as, which sent the search in the wrong
                    // direction. Two physics ticks separate the AwaitDribble
                    // check from this Begin, so the state can still change in
                    // between; the message must not pre-judge which cause it was.
                    Fail($"{_scenario}: BeginMoveForHarness(new RetreatDribble()) returned false — " +
                         "either a move was already running (the actor's machine was not Inactive), " +
                         $"or a begin gate rejected it. Ball state = {_ball?.State} " +
                         "(RetreatDribble is inside BeginCommittedMove's dead-dribble gate and " +
                         "needs Dribbling, #193).");
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

        if (!_sawStartup && node == "RetreatDribbleStartup") _sawStartup = true;
        if (_sawStartup && !_sawActive && node == "RetreatDribbleActive") _sawActive = true;
        if (_sawActive && !_sawRecovery && node == "RetreatDribbleRecovery") _sawRecovery = true;
        if (node == "Startup" || node == "Active" || node == "Recovery") _sawGenericPlaceholder = true;

        // ── Geometry, sampled at event time ──────────────────────────────────
        var skel = FindSkeleton(_actor);
        if (skel == null) return;

        if (node != "RetreatDribbleStartup" && node != "RetreatDribbleActive" &&
            node != "RetreatDribbleRecovery") return;

        float lean = MeasureSpineHeadForward(skel);

        // The Hips reading is taken on EVERY tick of the move, not just each
        // phase's last, because the in-place claim is about the whole
        // trajectory: a clip that translates out and back would have zero
        // endpoint drift and a large excursion in between.
        //
        // What is GATED is the horizontal SPAN of the move's own samples, not
        // the distance from the pre-move baseline. The baseline is sampled in
        // the Dribble BlendSpace — a DIFFERENT clip — so a baseline-relative
        // gate quietly folds in the constant offset between the two clips' hip
        // positions. It reads 0.0004 m today only because the two happen to
        // agree; a future re-author of dribble_move_authored.fbx that shifted
        // the stance 2 cm fore would redden this scenario while blaming root
        // translation in a retreat clip that never changed. The span measures
        // exactly what the scenario name claims — this clip does not move its
        // own root — and the baseline offset is still printed as information.
        //
        // The move's FIRST observed tick is excluded from the span: the phase
        // label leads the pose by one tick (#316), so that sample still holds
        // the pre-move dribble pose and would drag the inter-clip offset back
        // in through the side door.
        Vector3? hipsNow = MeasureHipsLocal(skel);
        if (hipsNow == null)
        {
            _hipsBoneMissing = true;
        }
        else
        {
            _hipsObservedTicks++;
            if (_hipsObservedTicks > 1)
            {
                // X/Z only: vertical hip motion IS authored (the crouch drops
                // the hips 0.11 m); only the horizontal plane is claimed.
                _hipsMinX = Math.Min(_hipsMinX, hipsNow.Value.X);
                _hipsMaxX = Math.Max(_hipsMaxX, hipsNow.Value.X);
                _hipsMinZ = Math.Min(_hipsMinZ, hipsNow.Value.Z);
                _hipsMaxZ = Math.Max(_hipsMaxZ, hipsNow.Value.Z);
                _hipsSpannedTicks++;
            }

            if (_hipsBeforeMove != null)
            {
                Vector3 d = hipsNow.Value - _hipsBeforeMove.Value;
                d.Y = 0f;
                _worstHipsBaselineOffset = Math.Max(_worstHipsBaselineOffset, d.Length());
            }
        }

        if (node == "RetreatDribbleStartup")
        {
            _startupTicks++;
            _leanAtLastStartupTick = lean;
            _poseAtLastStartupTick = SampleComparedBones(skel);
        }
        else if (node == "RetreatDribbleActive")
        {
            _activeTicks++;
            _leanAtLastActiveTick = lean;
        }
        else
        {
            // Overwritten each Recovery tick, so it ends up holding the LAST one
            // — the "sample the final tick" discipline established by
            // BehindTheBackAnimTest/ContestAnimTest/LayupAnimTest by mutation
            // (an unbound clip collapses to rest within a tick — no xfade on
            // any edge — so the final tick is where bound and unbound separate).
            _recoveryTicks++;
            _poseAtLastRecoveryTick = SampleComparedBones(skel);
        }
    }

    private void RenderVerdict()
    {
        GD.Print($"[retreatdribble-anim]   observed ticks: startup={_startupTicks} " +
                 $"active={_activeTicks} recovery={_recoveryTicks}");
        switch (_scenario)
        {
            case "retreatdribble-phases":                          VerdictPhases(); break;
            case "retreatdribble-startup-differs-from-recovery":   VerdictStartupDiffersFromRecovery(); break;
            case "retreatdribble-torso-goes-back-in-active":       VerdictTorsoGoesBack(); break;
            case "control-retreatdribble-torso-modest-in-startup": VerdictControlTorsoModestInStartup(); break;
            case "retreatdribble-hips-stay-in-place":              VerdictHipsStayInPlace(); break;
        }
    }

    // ── Scenario: retreatdribble-phases (positive) ──────────────────────────
    private void VerdictPhases()
    {
        bool pass = _sawStartup && _sawActive && _sawRecovery;
        if (pass)
            GD.Print("[retreatdribble-anim] PASS retreatdribble-phases — the tree was observed on " +
                     "\"RetreatDribbleStartup\", then \"RetreatDribbleActive\", then " +
                     "\"RetreatDribbleRecovery\", in that order (the .tscn states and their transitions " +
                     "are live).");
        else
            Fail($"retreatdribble-phases: expected RetreatDribbleStartup -> RetreatDribbleActive -> " +
                 $"RetreatDribbleRecovery, in order; got sawStartup={_sawStartup}, sawActive={_sawActive}, " +
                 $"sawRecovery={_sawRecovery}, sawGenericPlaceholder={_sawGenericPlaceholder}, " +
                 $"lastAnimNode={_actor.ActiveAnimNodeForHarness}.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: retreatdribble-startup-differs-from-recovery ──────────────
    // #296's ACTUAL complaint, and unusually load-bearing here: with three held
    // poses (README's "<=3-tick segments are single poses" rule), if Startup
    // and Recovery coincide the move has no arc at all.
    //
    // It is also the HARDER comparison for this move than for the rest of the
    // batch. Handoff 05 specifies a BALANCED recovery ("a retreat dribble is a
    // reset, so the recovery pose should read as balanced, not punished"),
    // which is exactly the instruction most likely to produce a Recovery that
    // drifts back toward the Startup stance. author_retreatdribble.py and
    // rebuild_retreatdribble_clips.gd both re-prove this same pair for that
    // reason; this is the live-rig third measurement.
    private void VerdictStartupDiffersFromRecovery()
    {
        if (_poseAtLastStartupTick == null || _poseAtLastRecoveryTick == null)
        {
            Fail("retreatdribble-startup-differs-from-recovery: never sampled both a Startup and a " +
                 $"Recovery tick (sawStartup={_sawStartup}, sawRecovery={_sawRecovery}) — the premise for " +
                 "comparing them never held, so this fails rather than passes.");
            Finish(1);
            return;
        }

        float worst = 0f;
        for (int i = 0; i < _poseAtLastStartupTick.Length; i++)
            worst = Math.Max(worst, Mathf.RadToDeg(
                _poseAtLastStartupTick[i].AngleTo(_poseAtLastRecoveryTick[i])));

        GD.Print($"[retreatdribble-anim]   worst Startup-vs-Recovery bone delta = {worst:F2} deg " +
                 $"(floor {StartupVsRecoveryMinDeg:F1})");

        // The >= 2 tick premise is not boilerplate. The phase label leads the
        // pose by one tick (#316), so a phase observed for exactly ONE tick
        // leaves _poseAtLastStartupTick holding the pre-move DRIBBLE pose. This
        // verdict would then be comparing the dribble stance against Recovery
        // and would pass green even with both states pointing at the identical
        // clip — precisely the #296 defect it exists to catch. #238's tuning
        // pass is open and free to retune DefaultFrameData to a 1-tick Startup,
        // so this is a reachable state, not a hypothetical one.
        bool premise = _sawStartup && _sawRecovery && _startupTicks >= 2 && _recoveryTicks >= 2;
        bool pass = premise && worst >= StartupVsRecoveryMinDeg;
        if (pass)
            GD.Print("[retreatdribble-anim] PASS retreatdribble-startup-differs-from-recovery — the last " +
                     $"Startup pose and the last Recovery pose differ by {worst:F2} deg, so the wind-up and " +
                     "the settled reset are visibly distinct silhouettes (#296) even at 9 ticks total.");
        else
            Fail($"retreatdribble-startup-differs-from-recovery: worst delta {worst:F2} deg < " +
                 $"{StartupVsRecoveryMinDeg:F1}, premise={premise} (startupTicks={_startupTicks}, " +
                 $"recoveryTicks={_recoveryTicks}, both need >= 2). " +
                 "Either the two states point at the same clip, or the clips " +
                 "bind to nothing on this rig (check for Blender's 'Armature/' track-path prefix) and both " +
                 "poses collapsed to rest.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: retreatdribble-torso-goes-back-in-active (positive) ───────
    // THE READ. Handoff 05's defining contrast with jab step (#304): retreat
    // dribble's torso stays upright-to-BACK over a base moving away, where the
    // jab's pitches FORWARD over an extended front foot. Same 3/2/4 ticks, same
    // source FBX, same three-held-poses structure — at 0.150 s the lean sign is
    // the only read that survives, so this scenario is the whole reason the
    // clip exists.
    //
    // Sign-inverted from JabStepAnimTest's identically-shaped
    // `jabstep-torso-pitches-forward-in-active`, deliberately kept the same
    // shape so the two are comparable line for line.
    //
    // Measured Blender-side (author_retreatdribble.py's
    // `torso_forward_at_active_end_m` = -0.0402, i.e. ~4.6 deg PAST vertical)
    // and resource-side (rebuild_retreatdribble_clips.gd's G4 growth = -0.0836
    // m, G5 absolute = -0.0460 m); this is the third, live-rig measurement.
    //
    // Compares against `_leanBeforeMove` — sampled ONE TICK BEFORE
    // BeginMoveForHarness, while the actor is unambiguously still in
    // Locomotion/Dribble — rather than the move's own first observed Startup
    // tick, because the #316 phase-label-leads-pose-by-one-tick trap means that
    // first tick can still hold the PRE-move pose, and at a 3-tick Startup that
    // ambiguity is a third of the phase.
    private void VerdictTorsoGoesBack()
    {
        float delta = _leanAtLastActiveTick - _leanBeforeMove;
        GD.Print($"[retreatdribble-anim]   torso-forward lean: beforeMove={_leanBeforeMove:F4} " +
                 $"activeEnd={_leanAtLastActiveTick:F4} delta={delta:F4} " +
                 $"(want <= {-TorsoBackwardGrowthMinM:F2})");

        // Premise, and it is a REAL premise rather than a formality: sampling
        // each phase's LAST tick only dodges #316 if the phase was observed for
        // more than one tick (on a single observation the last tick IS the
        // first, which can still hold the previous phase's pose). Active is 2
        // ticks — the tightest window in the batch.
        bool premise = _sawActive && _activeTicks >= 2 && !float.IsNaN(_leanBeforeMove);
        bool pass = premise && delta <= -TorsoBackwardGrowthMinM;
        if (pass)
            GD.Print("[retreatdribble-anim] PASS retreatdribble-torso-goes-back-in-active — the spine->head " +
                     $"vector's forward projection moved {delta:F4} m BACKWARD from the pre-move dribble " +
                     $"stance to Active's last tick (floor {-TorsoBackwardGrowthMinM:F2}), so the retreat " +
                     "genuinely leans AWAY. This is the opposite sign to jab step's identically-shaped gate, " +
                     "which is the only read that separates the two clips at 0.150 s.");
        else
            Fail($"retreatdribble-torso-goes-back-in-active: pre-move -> Active-end delta was {delta:F4}, " +
                 $"need <= {-TorsoBackwardGrowthMinM:F2} (sawActive={_sawActive}, activeTicks={_activeTicks} " +
                 $"(need >= 2), leanBeforeMove={_leanBeforeMove:F4}). A POSITIVE delta means this clip leans " +
                 "INTO the defender exactly like jab step does — the two-clips-converge failure #305 exists " +
                 "to prevent. Either the clip is unbound (silent no-op, README trap 13) or TORSO_PITCH_SIGN " +
                 "regressed in author_retreatdribble.py.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: control-retreatdribble-torso-modest-in-startup (control) ──
    // The paired control: Startup's OWN last tick must have travelled backward
    // measurably LESS than Active's does off the same baseline — the wind-up is
    // a smaller commitment than the push-off, so the deep lean is a localised
    // EVENT at Active rather than something the 3-tick Startup window already
    // fully spent.
    //
    // Framed as "Startup travelled less than Active", not "Startup stayed near
    // zero": author_retreatdribble.py's keypose table intentionally brings the
    // chest most of the way to square-and-vertical by Startup's own end (25 deg
    // of its 35 deg total counter-rotation), because a <=3-tick wind-up has to
    // be readable by the time Active begins. A near-zero ceiling would fight
    // that authored intent instead of testing it.
    private void VerdictControlTorsoModestInStartup()
    {
        float startupTravel = _leanAtLastStartupTick - _leanBeforeMove;
        float activeTravel = _leanAtLastActiveTick - _leanBeforeMove;
        // Both are NEGATIVE (backward). The margin is how much FURTHER back
        // Active got, so it is startup minus active, and it is positive when
        // the clip behaves.
        float margin = startupTravel - activeTravel;
        GD.Print($"[retreatdribble-anim]   torso-backward travel off pre-move baseline: " +
                 $"startup={startupTravel:F4} active={activeTravel:F4} margin={margin:F4} " +
                 $"(floor {TorsoBackwardSettleMinM:F3})");

        // Premise: Active must genuinely have shown the backward lean (the
        // positive gate above), or "Startup travelled less" is trivially true
        // of a clip where nothing ever moved backward in the first place.
        bool premise = _sawStartup && _sawActive && _startupTicks >= 2 && _activeTicks >= 2 &&
                       !float.IsNaN(_leanBeforeMove) && activeTravel <= -TorsoBackwardGrowthMinM;
        bool pass = premise && margin >= TorsoBackwardSettleMinM;
        if (pass)
            GD.Print($"[retreatdribble-anim] PASS control-retreatdribble-torso-modest-in-startup — Startup's " +
                     $"own travel ({startupTravel:F4}) stayed {margin:F4} short of Active's " +
                     $"({activeTravel:F4}, floor {TorsoBackwardSettleMinM:F3}), so the retreat is still " +
                     "deepening when Active begins rather than being fully spent by the end of the wind-up.");
        else
            Fail($"control-retreatdribble-torso-modest-in-startup: startupTravel={startupTravel:F4}, " +
                 $"activeTravel={activeTravel:F4}, margin={margin:F4} (need >= {TorsoBackwardSettleMinM:F3}), " +
                 $"premise={premise} (startupTicks={_startupTicks}, activeTicks={_activeTicks}, both need " +
                 ">= 2). If the premise broke, this fails rather than passes.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: retreatdribble-hips-stay-in-place ─────────────────────────
    // THE TRAP WITH THIS ISSUE'S NAME ON IT, asserted on the live rig.
    //
    // PlayerController already moves the character: its JustEnteredActive
    // branch for RetreatDribble sets Velocity to RetreatDribbleBurstSpeed
    // (4.0 m/s) straight back along Heading. Handoff 05's motion spec nonetheless
    // describes Active as "hips displaced back ~0.25 m", and authoring THAT as
    // clip translation plays the retreat TWICE and slides the mesh off its own
    // collider. So the clip is authored IN PLACE — the retreat is expressed as
    // the FEET drifting forward relative to the hips, never as root motion.
    //
    // The failure this catches is invisible to every other gate in the
    // pipeline: a doubled retreat is a perfectly well-formed clip that binds,
    // resolves, slices to the right length, and passes every pose comparison.
    // Only a measurement of the root's own horizontal travel can see it.
    //
    // Measured in SKELETON space (GetBonePose on the Hips bone), not world
    // space — the character node itself IS moving at 4 m/s during this window,
    // by design, and a world-space reading would be dominated by exactly the
    // motion that is supposed to be there.
    //
    // Only X/Z are measured, because vertical hip motion IS authored: the
    // keypose table drops the hips 0.11 m into the loaded recovery stance. Only
    // the horizontal plane is claimed.
    //
    // Sampled on EVERY tick of the move rather than at the endpoints, because a
    // clip that translated out and back would show zero endpoint drift and a
    // large mid-move excursion. The gate is the SPAN of those samples (see
    // Observe) rather than their distance from the pre-move baseline, so that
    // the reading depends on this clip alone and not on the dribble clip the
    // baseline happens to be sampled in.
    private void VerdictHipsStayInPlace()
    {
        float span = HipsHorizontalSpan;
        GD.Print($"[retreatdribble-anim]   Hips horizontal span across the move (skeleton space) = " +
                 $"{span:F4} m (max {HipsHorizontalDriftMaxM:F2}) over {_hipsSpannedTicks} spanned tick(s); " +
                 $"offset from the pre-move dribble stance = {_worstHipsBaselineOffset:F4} m (informational)");

        bool premise = _sawStartup && _sawActive && _sawRecovery
                       && _hipsBeforeMove != null && !_hipsBoneMissing && _hipsSpannedTicks >= 2;
        bool pass = premise && span <= HipsHorizontalDriftMaxM;
        if (pass)
            GD.Print("[retreatdribble-anim] PASS retreatdribble-hips-stay-in-place — across the whole move " +
                     $"the Hips bone swept a horizontal box only {span:F4} m across in skeleton space, " +
                     "so the clip is authored IN PLACE and the only thing moving the " +
                     "player backward is RetreatDribbleBurstSpeed. No double-counted retreat.");
        else if (!premise)
            // Separated from the drift failure below because the two want
            // opposite investigations, and a premise break wearing the drift
            // message sends the reader to the clip when the problem is the
            // measurement. hipsBoneMissing=True in particular means the gate
            // measured NOTHING — the state that used to read 0.0000 m and print
            // PASS before this scenario stopped trusting a Zero fallback.
            Fail($"retreatdribble-hips-stay-in-place: PREMISE FAILED — " +
                 $"hipsBoneMissing={_hipsBoneMissing} (did the rig's bone naming change? " +
                 "mixamorig_ vs mixamorig: is the standing trap), " +
                 $"spannedTicks={_hipsSpannedTicks} (need >= 2), " +
                 $"baselineSampled={_hipsBeforeMove != null}, " +
                 $"sawStartup={_sawStartup}, sawActive={_sawActive}, sawRecovery={_sawRecovery}. " +
                 "Nothing was measured, so this fails rather than passes.");
        else
            Fail($"retreatdribble-hips-stay-in-place: Hips horizontal span was " +
                 $"{span:F4} m (max {HipsHorizontalDriftMaxM:F2}) over {_hipsSpannedTicks} ticks. " +
                 "The clip is translating its own root, which double-counts the burst " +
                 "PlayerController already applies on JustEnteredActive and slides the mesh off its " +
                 "collider. Express the retreat as the FEET drifting forward relative to the hips " +
                 "(front_fore_m / rear_fore_m in author_retreatdribble.py), never as hip translation.");
        Finish(pass ? 0 : 1);
    }

    // ── Static scenarios: no live tree, pure resource/scene inspection ──────
    private void RunStaticCheck()
    {
        switch (_scenario)
        {
            case "retreatdribble-no-placeholder-leak": RunNoPlaceholderLeakCheck(); break;
            case "retreatdribble-segment-lengths":     RunSegmentLengthsCheck(); break;
            case "retreatdribble-edges":               RunEdgesCheck(); break;
        }
    }

    // ── Scenario: retreatdribble-segment-lengths ────────────────────────────
    // #276 rule 4 / #295. This matters unusually much here: Active is 2 ticks
    // = 0.0333 s, so an off-by-one tick is a 50% length error, not a rounding
    // nicety. Tick windows are read from RetreatDribble.DefaultFrameData, NOT
    // hardcoded, so a future retune that forgets to re-run
    // tools/rebuild_retreatdribble_clips.gd goes red here and names the tool.
    private void RunSegmentLengthsCheck()
    {
        var lib = GD.Load<AnimationLibrary>("res://assets/locomotion.res");
        if (lib == null)
        {
            Fail("assets/locomotion.res failed to load — cannot evaluate retreatdribble-segment-lengths.");
            Finish(1);
            return;
        }

        double tps = Engine.PhysicsTicksPerSecond;
        var frames = RetreatDribble.DefaultFrameData;
        // A ONE-TICK tolerance would defeat this scenario's whole stated
        // purpose on this move specifically: Active is only 2 ticks, so a
        // one-tick bar (1/60 s = 0.0167 s) is HALF the clip's own length and
        // would wave through a 3-tick mis-slice as "within tolerance". The
        // slice is exact to float noise (~1e-5 s), so this is a noise band,
        // not a drift allowance.
        const double ToleranceSeconds = 1e-3;

        (string Clip, int Ticks)[] windows =
        {
            ("retreatdribblestartup",  frames.StartupFrames),
            ("retreatdribbleactive",   frames.ActiveFrames),
            ("retreatdribblerecovery", frames.RecoveryFrames),
        };

        bool pass = true;
        foreach (var (clipName, ticks) in windows)
        {
            if (!lib.HasAnimation(clipName))
            {
                Fail($"AnimationLibrary has no clip '{clipName}' — run tools/rebuild_retreatdribble_clips.gd.");
                pass = false;
                continue;
            }

            double expectedSeconds = ticks / tps;
            // Variant accessor, NOT .Length: that property is `float` in Godot 4.6.x
            // and `double` in 4.7, so a 4.7.1-built assembly throws
            // MissingMethodException under a stale 4.6 binary — and it throws
            // inside _PhysicsProcess, BEFORE the timeout check, so the scenario
            // HANGS instead of failing (#339 measured this across all 8 of
            // JabStepAnimTest's). The Variant accessor binds under both.
            double actualSeconds = lib.GetAnimation(clipName).Get("length").AsDouble();
            double deviationSeconds = Math.Abs(actualSeconds - expectedSeconds);
            GD.Print($"[retreatdribble-anim]   '{clipName}': length={actualSeconds:F6}s " +
                     $"expected={expectedSeconds:F6}s ({ticks} ticks @ {tps} tps), " +
                     $"deviation={deviationSeconds:F6}s");

            if (deviationSeconds > ToleranceSeconds)
            {
                Fail($"clip '{clipName}' is {actualSeconds:F6}s, expected {expectedSeconds:F6}s " +
                     $"({ticks} ticks at {tps} tps — RetreatDribble.DefaultFrameData), a deviation of " +
                     $"{deviationSeconds:F6}s exceeds the float-noise tolerance ({ToleranceSeconds:F6}s). " +
                     "Re-run tools/rebuild_retreatdribble_clips.gd after retuning the move's frame data.");
                pass = false;
            }
        }

        if (pass)
            GD.Print("[retreatdribble-anim] PASS retreatdribble-segment-lengths — all three clips' durations " +
                     "match RetreatDribble.DefaultFrameData's Startup/Active/Recovery windows to within " +
                     $"float noise ({ToleranceSeconds:F6}s). A one-tick retune of the 2-tick Active window " +
                     "(a 50% error) goes red here.");
        else
            GD.PrintErr("[retreatdribble-anim] FAIL retreatdribble-segment-lengths — see per-clip deviations above.");

        Finish(pass ? 0 : 1);
    }

    // ── Scenario: retreatdribble-no-placeholder-leak ────────────────────────
    // The direct statement that #296 is closed for this move. An ALLOWLIST,
    // not a placeholder blocklist — a blocklist alone waves through a
    // copy-paste from a neighbouring move's sub-resource, which on this branch
    // is a live risk: the three sub-resources were spliced into Player.tscn
    // directly adjacent to jab step's and in-and-out's.
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
            ("RetreatDribbleStartup",  "locomotion/retreatdribblestartup"),
            ("RetreatDribbleActive",   "locomotion/retreatdribbleactive"),
            ("RetreatDribbleRecovery", "locomotion/retreatdribblerecovery"),
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
            GD.Print($"[retreatdribble-anim]   {stateName} -> {actualClip}");

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
            GD.Print("[retreatdribble-anim] PASS retreatdribble-no-placeholder-leak — all three " +
                     "RetreatDribble states point at their OWN per-move clips, not the shared " +
                     "locomotion/idle placeholder #305 moved them off of.");
        else
            GD.PrintErr("[retreatdribble-anim] FAIL retreatdribble-no-placeholder-leak — see per-state mismatches above.");

        Finish(pass ? 0 : 1);
    }

    // ── Scenario: retreatdribble-edges ──────────────────────────────────────
    // README trap 8 / #279: a DELETED transition edge is invisible to
    // GetCurrentNode() (Travel() is a pathfinder and routes around the gap),
    // so this reads GetTransitionCount()/From()/To() off the RESOURCE, where a
    // missing edge is simply absent. Retreat dribble is an OFFENSIVE
    // (dribble-family) move — a live-dribbling holder sits in Dribble, not
    // Locomotion — so it needs the six standard edges AND the dribble-family
    // entries/exits, doubled since #294 split Dribble into
    // DribbleLeft/DribbleRight.
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
            ("Locomotion", "RetreatDribbleStartup"),
            ("RetreatDribbleStartup", "RetreatDribbleActive"),
            ("RetreatDribbleActive", "RetreatDribbleRecovery"),
            ("RetreatDribbleRecovery", "Locomotion"),
            // Startup -> Recovery direct. Named "feint / early-out" in the
            // sibling families, but RetreatDribble has feintWindowFrames: 0, so
            // for THIS move it is only the interrupt/abort path. The edge is
            // kept for shape-consistency across the batch.
            ("RetreatDribbleStartup", "RetreatDribbleRecovery"),
            ("RetreatDribbleStartup", "Locomotion"),             // abort
            // The dribble family, doubled by #294.
            ("DribbleLeft", "RetreatDribbleStartup"),
            ("DribbleRight", "RetreatDribbleStartup"),
            ("RetreatDribbleRecovery", "DribbleLeft"),
            ("RetreatDribbleRecovery", "DribbleRight"),
            ("RetreatDribbleStartup", "DribbleLeft"),
            ("RetreatDribbleStartup", "DribbleRight"),
        };

        var present = new System.Collections.Generic.HashSet<string>();
        for (int i = 0; i < sm.GetTransitionCount(); i++)
            present.Add($"{sm.GetTransitionFrom(i)}->{sm.GetTransitionTo(i)}");

        bool pass = true;
        foreach (var (from, to) in required)
        {
            bool here = present.Contains($"{from}->{to}");
            GD.Print($"[retreatdribble-anim]   edge {from} -> {to}: {(here ? "present" : "MISSING")}");
            if (!here)
            {
                Fail($"scenes/Player.tscn has no transition '{from}' -> '{to}'. Travel()'s pathfinder will " +
                     "route around the gap, so NO runtime scenario can catch this — only this resource-level " +
                     "check can.");
                pass = false;
            }
        }

        if (pass)
            GD.Print($"[retreatdribble-anim] PASS retreatdribble-edges — all {required.Length} required " +
                     "transitions are present (6 standard + 6 dribble-family, the latter doubled by #294's " +
                     "DribbleLeft/DribbleRight split).");
        else
            GD.PrintErr("[retreatdribble-anim] FAIL retreatdribble-edges — see missing transitions above.");

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
    // live-rig equivalent of rebuild_retreatdribble_clips.gd's
    // `_spine_head_forward` / author_retreatdribble.py's
    // `_spine_head_forward_m`. POSITIVE is forward.
    //
    // Character-for-character the same helper JabStepAnimTest uses, and that is
    // deliberate rather than lazy: the cross-move claim (#333) is that these
    // two clips produce OPPOSITE SIGNS of this exact quantity, and a comparison
    // of two differently-derived "forward" axes would not be a comparison at
    // all.
    //
    // Deliberately does NOT use the actor's world heading (e.g.
    // `-GlobalTransform.Basis.Z`) — measured live for #304, that gave a
    // NEGATIVE growth for a clip whose Blender and rebuild-side proofs both
    // independently confirmed a FORWARD lean, i.e. "Godot forward" and this
    // rig's own facing do not agree at this authored heading. Instead `forward`
    // is derived ONCE (cached) from the SAME skeleton's own
    // LeftFoot->LeftToeBase vector, projected to the horizontal plane — the toe
    // is anatomically ahead of the ankle, the same anchor the rebuild script's
    // `_derive_body_axes()` uses.
    //
    // Caching it at the PRE-MOVE tick matters more for this move than for the
    // jab: retreat dribble's feet deliberately travel relative to the hips
    // during the clip, so a per-tick re-derivation would let the measurement
    // axis drift with the very motion being measured. The actor's whole-body
    // orientation is frozen for the duration of a committed move
    // (PlayerController skips Move() while the machine is active), so one
    // derivation in the same global space is exact for the whole run.
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

    // The Hips bone's origin in SKELETON space. GetBoneGlobalPose is relative to
    // the Skeleton3D node, not the world, which is exactly what
    // `retreatdribble-hips-stay-in-place` needs: the character node is moving at
    // RetreatDribbleBurstSpeed during the window being measured, and that motion
    // must NOT enter the reading.
    //
    // Returns null — NOT Vector3.Zero — when the bone does not resolve. That
    // distinction is the whole point: a Zero fallback would make every sample
    // AND the baseline identical, so the drift would read exactly 0.0000 m and
    // this scenario would print PASS while measuring nothing at all. The rig
    // has a live way to reach that state (the mixamorig: vs mixamorig_ prefix
    // trap, an fbx/naming_version change, a rig swap), so the failure mode is
    // reachable rather than theoretical. MeasureSpineHeadForward already
    // degrades to NaN for the same reason; this now matches it.
    private static Vector3? MeasureHipsLocal(Skeleton3D skel)
    {
        int hips = skel.FindBone("mixamorig_Hips");
        return hips < 0 ? null : skel.GetBoneGlobalPose(hips).Origin;
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

    private void Fail(string message) => GD.PrintErr($"[retreatdribble-anim] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[retreatdribble-anim] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
