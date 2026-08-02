using System;
using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #314 (ADR-0016): proves the THREE
// CONTEST ANIMATION STATES (ContestStartup / ContestActive / ContestRecovery)
// wired into scenes/Player.tscn are real — entered end-to-end by a real
// ContestMove, bound to the right clips, cut to the right windows, and actually
// MOVING the rig.
//
// Before #314 "contest" fell through MoveAnimResolver.ResolveStateName's default
// case onto the shared generic Startup/Active/Recovery states, which per #296
// play locomotion/idle for BOTH Startup and Recovery (pixel-identical — an
// opponent cannot tell "committing" from "in the punish window") and a looping
// locomotion/run for Active.
//
//   godot --headless --path . res://tests/integration/ContestAnimTest.tscn -- --harness-scenario=contest-phases
//   …=contest-no-placeholder-leak | contest-segment-lengths | contest-edges
//   …=contest-startup-differs-from-recovery
//   …=contest-stays-grounded | control-layup-leaves-ground
//   …=contest-arms-rise | control-contest-arms-low-startup
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//
// ── The read this file exists to protect ────────────────────────────────────
// Contest and block raise THE SAME ARMS. The only thing separating them is the
// feet: block leaves the ground, contest stays planted. ContestMove.cs:53-54
// prices the commitment ladder on exactly that (contest 6 < steal 8 < block 10
// startup ticks), so a contest clip that drifts airborne has silently become a
// block and the ladder collapses — the opponent reads "he jumped, I can pump
// fake" off a move that is in fact recoverable.
//
// So the load-bearing scenario here is contest-stays-grounded, which is the
// INVERSE of LayupAnimTest's load-bearing layup-airborne-active. That inversion
// is why its control is unusual — see the next block.
//
// ── Why "grounded" needs a harder control than "airborne" does ───────────────
// "The feet stayed on the floor" is the most vacuously-satisfiable assertion in
// this repo. It passes perfectly on:
//   * a rig that never moves at all;
//   * a clip whose track paths kept Blender's "Armature/" object-wrapper prefix
//     and therefore bind to NOTHING on this rig (#281) — Godot logs "couldn't
//     resolve track", carries on, and the clip is a SILENT no-op;
//   * a Skeleton3D the harness failed to find, where every reading is 0.
// A same-move control (measure the feet during some other contest phase) does
// not rule any of those out, because they are all properties of the whole clip
// rather than of one phase.
//
// The control therefore has to prove the INSTRUMENT can see a foot leave the
// ground at all, which means running a different move through the identical
// measurement path: control-layup-leaves-ground begins a Layup instead of a
// ContestMove, reads the same MeasureLowestToe on the same live Skeleton3D, and
// asserts it DOES go airborne. If the toe measurement were dead for any of the
// reasons above, that control fails, and contest-stays-grounded's green is
// correctly disbelieved.
//
// The handoff asked for control-BLOCK-leaves-ground. Block cannot serve: as of
// this commit scenes/Player.tscn's BlockStartup/Active/Recovery still point at
// AnimationNodeAnimation_mv277ph (locomotion/idle), i.e. block has no clip of
// its own yet and does not leave the ground in the display layer at all — the
// control would fail for a reason that has nothing to do with contest. The
// handoff anticipates this and names the substitute: "if block has not landed
// yet, use the existing layup". #313 landed layup one commit ago with a
// mutation-proven airborne rise, so it is available and honest. Swap the
// control back to block when handoff 03 lands.
//
// ── Mutation evidence (measured, not asserted) ──────────────────────────────
// Four mutations were applied to scenes/Player.tscn and every scenario re-run
// (each mutation reverted from a pristine backup before the next was applied).
// The table is here rather than only in the PR because its real content is that
// NO SINGLE SCENARIO catches everything, and a future reader deleting one of
// them needs to see which defect they are giving up.
//
// All arms-rise figures below are the LOWER of the two wrists (see
// MeasureWristAboveHead); they were re-measured after that reduction changed
// from max to min, because the max readings said something different.
//
//   mutation                                  grounded  arms-rise  leak  su≠re
//   (none — shipped state)                    0.0204 P  +0.3503 P   P     55.2 P
//   all three states -> mv277ph               0.0148 P  -0.6782 R   R      3.6 R
//     (the literal #296 defect)
//   ContestActive -> mv277ph                  0.0204 P  -0.2630 R   R     55.2 P
//     (the mutation #314 names)
//   contestactive clip -> layupactive         0.3211 R  -0.2630 R   R     55.2 P
//     (AIRBORNE *and* one-armed — layup is both)
//   deleted the ContestActive->ContestRecovery edge: contest-edges R,
//     contest-phases still P
//
// Four things that table says out loud:
//
// 1. contest-stays-grounded PASSES on a pure placeholder clip (0.0148), because
//    locomotion/idle is genuinely grounded. The grounded gate ALONE cannot tell
//    a planted contest from a dead one. That is not a weakness to fix by
//    tightening it — it is why contest-arms-rise exists as the positive half.
//
// 2. The layupactive row is the reason MeasureWristAboveHead takes the LOWER
//    wrist. Under the max-wrist reading this file originally shipped, that row
//    read +0.2580 and PASSED: layup raises one arm to +0.2580 and leaves the
//    other at -0.2630, and a one-armed overhead pose is a steal or a one-handed
//    block silhouette, not a contest. Taking the minimum is what makes "both
//    hands up" the measured claim instead of an assumed one. Do not "simplify"
//    it back to max — that is a live hole, closed by mutation, not by argument.
//
// 3. What this table does NOT prove: it contains no mutation where grounded
//    goes red while arms-rise stays green, so contest-stays-grounded's UNIQUE
//    contribution is currently unevidenced. Demonstrating it needs a clip that
//    is airborne with BOTH arms up — which is precisely block's clip, and block
//    is still on the mv277ph placeholder (see the control note below). When
//    block's clip lands, re-run this table; until then treat the grounded gate
//    as justified by argument (ContestMove.cs:53-54's commitment ladder) rather
//    than by measurement, and do not delete it on the strength of arms-rise.
//
// 4. The deleted-edge row is README trap 8 / #279 re-demonstrated live:
//    contest-phases still passed, because Travel() is a pathfinder and routed
//    around the gap. Only the resource-level contest-edges saw it.
//
// ── Why ActiveAnimNodeForHarness, not the resolver's return value (#257) ─────
// Travel() to a missing/misnamed state only LOGS; it never throws. Asserting
// MoveAnimResolver.ResolveStateName(...) == "ContestActive" would pass on a
// Player.tscn with no Contest states at all, since the resolver has no notion
// the .tscn exists. Only the live AnimationNodeStateMachinePlayback proves
// wiring.
//
// ── Cosmetic-only (issue #314's standing constraint) ────────────────────────
// #314 is a CLIP issue. Nothing here observes or feeds DefensiveResolution,
// StealReachRadius, #214's reach gate, the on-ball contest scatter penalty or
// any ADR-0018 window. This harness begins the move via BeginMoveForHarness —
// downstream of every gameplay gate — precisely so it cannot accidentally
// become a second, weaker test of the contest→scatter coupling that
// ContestScatterTest already owns.
public partial class ContestAnimTest : Node
{
    private const double TimeoutSeconds = 15.0;
    private const int ArmFrames = 2;      // ticks for TryAssignTipoffHolder to run
    private const int ActFrames = 2;      // ticks after tipoff before Begin (position/heading settle)
    // > contest startup(6)+active(8)+recovery(20)=34, and > layup's 26, with
    // generous slack for the control scenario sharing this budget.
    private const int ObserveFrames = 70;

    private static readonly Vector3 ActorSpot = new(0f, 0f, 2f);
    private static readonly Vector3 FarSpot = new(12f, 0f, 12f); // out of PickupRadius
    private static readonly Vector3 RimCenter = new(0f, 3.05f, 0f); // BallController.DefaultRimCenter

    // Upper-body bones for the Startup-vs-Recovery pose comparison. Same set
    // LayupAnimTest/BehindTheBackAnimTest use, and for its reason: these are the
    // bones a spectator reads a commitment arc off.
    private static readonly string[] UpperBodyBones =
    {
        "mixamorig_LeftArm", "mixamorig_RightArm",
        "mixamorig_LeftForeArm", "mixamorig_RightForeArm",
        "mixamorig_Spine",
    };

    // Thresholds are MEASURED, not assumed — each is set well inside the
    // observed working value and well outside the observed broken value; the
    // per-scenario comments carry both numbers.
    private const float StartupVsRecoveryMinDeg = 15.0f;

    // The contest ceiling and the control's floor, in skeleton-local units (see
    // the geometry-helpers block). The gap between them is deliberately wide:
    // if these two ever meet, the control has stopped discriminating and both
    // scenarios should be treated as unproven rather than retuned.
    private const float GroundedMaxToeExcursion = 0.08f;
    private const float AirborneMinToeRise = 0.15f;

    // The wind-up must keep the hands DOWN by a real margin, not merely "<". A
    // bare inequality would accept a clip holding the arms up for the entire
    // move, which is exactly the un-telegraphed pose ADR-0003 forbids — the
    // wind-up has to be readable as a wind-up.
    private const float ArmRiseMinMargin = 0.10f;

    // ...and the SAME discipline applied to the positive gate, which previously
    // asked only for "> 0". A wrist grazing 0.0001 m over the head is not "hands
    // in the shooter's eyeline", it is a rounding artefact that reads on screen
    // as arms at shoulder height. Both authoring tools already gate on 0.10 m
    // (author_contest.py WRIST_ABOVE_HEAD_MIN_M, rebuild_contest_clips.gd G5);
    // matching them here costs nothing — the measured value is ~0.35.
    private const float ArmAboveHeadMinM = 0.10f;

    private static readonly string[] KnownScenarios =
    {
        "contest-phases",
        "contest-no-placeholder-leak",
        "contest-segment-lengths",
        "contest-startup-differs-from-recovery",
        "contest-edges",
        "contest-stays-grounded",
        "control-layup-leaves-ground",
        "contest-arms-rise",
        "control-contest-arms-low-startup",
    };

    // Scenarios that need no live tree — pure resource/scene inspection.
    private static readonly string[] StaticScenarios =
    {
        "contest-no-placeholder-leak", "contest-segment-lengths", "contest-edges",
    };

    // The one scenario that drives a LAYUP through this file's measurement path
    // instead of a contest. See the header for why the control has to change the
    // MOVE rather than the phase.
    private const string LayupControlScenario = "control-layup-leaves-ground";

    private string _scenario = "contest-phases";
    private bool _isLayupControl;

    // The three state names this run watches — Contest*, or Layup* for the
    // control. Keeping them in one place is what lets Observe() stay a single
    // code path, which is the point: the control must traverse the SAME
    // instrument, not a parallel copy of it.
    private string _startupState = "ContestStartup";
    private string _activeState = "ContestActive";
    private string _recoveryState = "ContestRecovery";

    private BallController _ball;
    private PlayerController _actor;  // peer "1" — the tipoff holder (ADR-0007)
    private PlayerController _other;  // peer "2"

    private int _frame;
    private double _elapsed;
    private bool _finished;

    private enum Step { AwaitTipoff, Act, Observe }
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
    // then the move is over and the rig has returned to Locomotion).
    private bool _haveToeBaseline;
    private float _toeBaseline;            // lowest-toe height on the FIRST move tick
    private float _maxToeRise;             // signed, across ALL THREE phases
    private float _maxToeExcursion;        // absolute, across ALL THREE phases
    // NegativeInfinity, not 0 — a wrist BELOW the head is a legitimate (and for
    // Startup, expected) reading, and seeding these at 0 would floor it there,
    // printing a confident "0.0000" for a hand that is actually well below the
    // head and weakening the control's margin to nothing. (This exact seeding
    // bug produced a false "startup=0.0000" during #313 and was caught by
    // reading the printed measurement, not by the exit code.)
    //
    // Two different reductions stack here and the order matters: MIN across the
    // two WRISTS (both hands must clear — see MeasureWristAboveHead), then MAX
    // across TICKS (the phase's peak). "max" in these names is the time axis
    // only; a one-armed pose can never inflate them.
    private float _maxWristAboveHeadDuringStartup = float.NegativeInfinity;
    private float _maxWristAboveHeadDuringActive = float.NegativeInfinity;
    private Quaternion[] _poseAtLastStartupTick;
    private Quaternion[] _poseAtLastRecoveryTick;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "contest-phases");
        GD.Print($"[contest-anim] scenario={_scenario} booting headless…");

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

        _isLayupControl = _scenario == LayupControlScenario;
        if (_isLayupControl)
        {
            _startupState = "LayupStartup";
            _activeState = "LayupActive";
            _recoveryState = "LayupRecovery";
        }

        // Real Player.tscn instances (live AnimationTree + Skeleton3D), named
        // "1"/"2" so the OfflineMultiplayerPeer makes unique_id 1 both IsServer
        // and IsLocalPlayer — the full TickServerOwnPlayer -> ApplyAnimation
        // chain runs every tick, same as LayupAnimTest/DribbleLoopTest.
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
                // Square up to the rim. A contest leans IN toward the handler
                // and a layup attacks the rim; nothing here reads facing, but a
                // coherent heading keeps the sampled pose meaningful.
                _actor.SetHeadingForHarness(
                    Mathf.Atan2(RimCenter.X - ActorSpot.X, RimCenter.Z - ActorSpot.Z));
                _step = Step.Act;
                _stepDeadlineFrame = _frame + ActFrames;
                break;

            case Step.Act:
                if (_frame < _stepDeadlineFrame) break;
                // The real production choke point (BeginCommittedMove), reached
                // via the generic harness seam — deliberately downstream of
                // every defensive gate, which ContestScatterTest owns.
                CommittedMove move = _isLayupControl ? new Layup() : (CommittedMove)new ContestMove();
                if (!_actor.BeginMoveForHarness(move))
                {
                    Fail($"{_scenario}: BeginMoveForHarness({move.GetType().Name}) returned false — " +
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

        if (!_sawStartup && node == _startupState) _sawStartup = true;
        if (_sawStartup && !_sawActive && node == _activeState) _sawActive = true;
        if (_sawActive && !_sawRecovery && node == _recoveryState) _sawRecovery = true;
        if (node == "Startup" || node == "Active" || node == "Recovery") _sawGenericPlaceholder = true;

        // ── Geometry, sampled at event time ──────────────────────────────────
        var skel = FindSkeleton(_actor);
        if (skel == null) return;

        if (node != _startupState && node != _activeState && node != _recoveryState) return;

        float toe = MeasureLowestToe(skel);
        // Baseline is the FIRST tick of the move — i.e. the stance the move
        // starts from — so every reading is relative to this attempt's own
        // footing rather than to a rest pose the clip may never visit.
        if (!_haveToeBaseline)
        {
            _toeBaseline = toe;
            _haveToeBaseline = true;
        }
        float rise = toe - _toeBaseline;
        _maxToeRise = Math.Max(_maxToeRise, rise);
        _maxToeExcursion = Math.Max(_maxToeExcursion, Math.Abs(rise));

        float wristAboveHead = MeasureWristAboveHead(skel);
        if (node == _startupState)
        {
            _maxWristAboveHeadDuringStartup =
                Math.Max(_maxWristAboveHeadDuringStartup, wristAboveHead);
            _poseAtLastStartupTick = SampleUpperBody(skel);
        }
        else if (node == _activeState)
        {
            _maxWristAboveHeadDuringActive =
                Math.Max(_maxWristAboveHeadDuringActive, wristAboveHead);
        }
        else
        {
            // Overwritten each Recovery tick, so it ends up holding the LAST one
            // — the "sample the final tick" discipline BehindTheBackAnimTest
            // arrived at by mutation: an UNBOUND clip collapses the rig to rest
            // within a tick (no xfade on any edge), so the final tick is where
            // bound and unbound actually separate.
            _poseAtLastRecoveryTick = SampleUpperBody(skel);
        }
    }

    private void RenderVerdict()
    {
        switch (_scenario)
        {
            case "contest-phases":                        VerdictPhases(); break;
            case "contest-startup-differs-from-recovery": VerdictStartupDiffersFromRecovery(); break;
            case "contest-stays-grounded":                VerdictStaysGrounded(); break;
            case "control-layup-leaves-ground":           VerdictControlLayupLeavesGround(); break;
            case "contest-arms-rise":                     VerdictArmsRise(); break;
            case "control-contest-arms-low-startup":      VerdictControlArmsLowStartup(); break;
        }
    }

    // ── Scenario: contest-phases (positive) ─────────────────────────────────
    private void VerdictPhases()
    {
        bool pass = _sawStartup && _sawActive && _sawRecovery;
        if (pass)
            GD.Print("[contest-anim] PASS contest-phases — the tree was observed on \"ContestStartup\", then " +
                     "\"ContestActive\", then \"ContestRecovery\", in that order (the .tscn states and their " +
                     "transitions are live).");
        else
            Fail($"contest-phases: expected ContestStartup -> ContestActive -> ContestRecovery, in order; got " +
                 $"sawStartup={_sawStartup}, sawActive={_sawActive}, sawRecovery={_sawRecovery}, " +
                 $"sawGenericPlaceholder={_sawGenericPlaceholder}, " +
                 $"lastAnimNode={_actor.ActiveAnimNodeForHarness}.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: contest-startup-differs-from-recovery ─────────────────────
    // #296's ACTUAL complaint. On the generic fallback both phases played
    // locomotion/idle, so the wind-up and the punish window were pixel-identical
    // and an opponent could not tell which one they were looking at. Comparing
    // two SAMPLED in-move poses (rather than either against rest) is the honest
    // question: a bound clip poses them differently; an unbound clip collapses
    // both to rest and the delta goes to ~0.
    //
    // The floor mirrors LayupAnimTest's, and rebuild_contest_clips.gd's G3
    // measured 106.7 deg between the same two poses on the sliced resources, so
    // there is a very large margin here. What this does NOT catch is stated in
    // LayupAnimTest's equivalent: pointing Recovery at the Startup CLIP can
    // still measure a healthy delta, because the two states then sample one
    // LOOP_NONE clip at different times. That mutation is caught by
    // contest-no-placeholder-leak instead, which pins each state to its own clip
    // by name. Coverage is complete across the two scenarios; neither covers it
    // alone.
    private void VerdictStartupDiffersFromRecovery()
    {
        if (_poseAtLastStartupTick == null || _poseAtLastRecoveryTick == null)
        {
            Fail("contest-startup-differs-from-recovery: never sampled both a Startup and a Recovery tick " +
                 $"(sawStartup={_sawStartup}, sawRecovery={_sawRecovery}) — the premise for comparing them " +
                 "never held, so this fails rather than passes.");
            Finish(1);
            return;
        }

        float worst = 0f;
        for (int i = 0; i < _poseAtLastStartupTick.Length; i++)
            worst = Math.Max(worst, Mathf.RadToDeg(
                _poseAtLastStartupTick[i].AngleTo(_poseAtLastRecoveryTick[i])));

        GD.Print($"[contest-anim]   worst upper-body Startup-vs-Recovery delta = {worst:F2} deg " +
                 $"(floor {StartupVsRecoveryMinDeg:F1})");

        bool pass = _sawStartup && _sawRecovery && worst >= StartupVsRecoveryMinDeg;
        if (pass)
            GD.Print("[contest-anim] PASS contest-startup-differs-from-recovery — the last Startup pose and " +
                     $"the last Recovery pose differ by {worst:F2} deg on the upper body, so the wind-up and " +
                     "the punish window are visibly distinct silhouettes (#296).");
        else
            Fail($"contest-startup-differs-from-recovery: worst upper-body delta {worst:F2} deg < " +
                 $"{StartupVsRecoveryMinDeg:F1}. Either the two states point at the same clip, or the clips " +
                 "bind to nothing on this rig (check for Blender's 'Armature/' track-path prefix) and both " +
                 "poses collapsed to rest.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: contest-stays-grounded (the load-bearing one) ─────────────
    // The whole legibility read (see the header). Measured on the TOES, not the
    // hips: a contest deliberately DROPS the hips ~0.05 m into a defensive
    // crouch, so a hip-based gate would have to be loose enough to permit that
    // drop and would then be blind to a small genuine hop. The feet are the read
    // and the feet are what is measured.
    //
    // Absolute excursion, not signed rise: a clip that drove the toes THROUGH
    // the floor would be just as wrong as one that lifted them, and the signed
    // form would wave it through.
    //
    // Non-vacuous only because control-layup-leaves-ground proves this same
    // measurement path can register a departure at all.
    private void VerdictStaysGrounded()
    {
        GD.Print($"[contest-anim]   toe excursion across all three phases = {_maxToeExcursion:F4} " +
                 $"(ceiling {GroundedMaxToeExcursion:F2}), signed max rise = {_maxToeRise:F4}");

        // Premise: all three phases must genuinely have been observed. "The feet
        // stayed down" proves nothing about a phase the run never entered, and
        // a partial traversal is exactly how this would pass vacuously.
        bool premise = _sawStartup && _sawActive && _sawRecovery;
        bool pass = premise && _maxToeExcursion <= GroundedMaxToeExcursion;
        if (pass)
            GD.Print($"[contest-anim] PASS contest-stays-grounded — across Startup, Active AND Recovery the " +
                     $"lowest toe never moved more than {_maxToeExcursion:F4} from the stance the move began " +
                     $"in (ceiling {GroundedMaxToeExcursion:F2}). Contest keeps its feet, so it stays " +
                     "distinguishable from a block and ContestMove's commitment ladder holds.");
        else
            Fail($"contest-stays-grounded: toeExcursion={_maxToeExcursion:F4} " +
                 $"(ceiling {GroundedMaxToeExcursion:F2}), sawStartup={_sawStartup}, sawActive={_sawActive}, " +
                 $"sawRecovery={_sawRecovery}. If the phases were not all observed this fails rather than " +
                 "passes — a grounded claim over an untraversed phase is vacuous. If the excursion is real, " +
                 "the contest has gone airborne and is now indistinguishable from a block (#314).");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: control-layup-leaves-ground (the control) ─────────────────
    // Runs a LAYUP through the identical measurement path and asserts the toes
    // DO leave the ground. See the header for why the control has to change the
    // move rather than the phase, and why layup stands in for block.
    private void VerdictControlLayupLeavesGround()
    {
        GD.Print($"[contest-anim]   (layup control) signed max toe rise = {_maxToeRise:F4} " +
                 $"(floor {AirborneMinToeRise:F2}), excursion = {_maxToeExcursion:F4}");

        bool premise = _sawStartup && _sawActive;
        bool pass = premise && _maxToeRise >= AirborneMinToeRise;
        if (pass)
            GD.Print($"[contest-anim] PASS control-layup-leaves-ground — the SAME MeasureLowestToe path that " +
                     $"reads ~0 on a contest reads {_maxToeRise:F4} on a layup, well past the " +
                     $"{AirborneMinToeRise:F2} floor and past contest's {GroundedMaxToeExcursion:F2} ceiling. " +
                     "So contest-stays-grounded's green is a real measurement of planted feet, not a dead " +
                     "instrument, an unbound clip, or a missing Skeleton3D.");
        else
            Fail($"control-layup-leaves-ground: maxToeRise={_maxToeRise:F4} (need >= {AirborneMinToeRise:F2}), " +
                 $"sawStartup={_sawStartup}, sawActive={_sawActive}. The toe instrument cannot see a foot " +
                 "leave the ground, so contest-stays-grounded proves nothing and must be treated as " +
                 "unverified regardless of its own result.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: contest-arms-rise (positive) ──────────────────────────────
    // The other half of the read: contest raises BOTH arms. Grounded feet alone
    // describe a player standing still.
    private void VerdictArmsRise()
    {
        GD.Print($"[contest-anim]   wrist-above-head: startup={_maxWristAboveHeadDuringStartup:F4} " +
                 $"active={_maxWristAboveHeadDuringActive:F4}");

        bool pass = _sawActive && _maxWristAboveHeadDuringActive >= ArmAboveHeadMinM;
        if (pass)
            GD.Print($"[contest-anim] PASS contest-arms-rise — the LOWER wrist reached " +
                     $"{_maxWristAboveHeadDuringActive:F4} above the head bone during Active (floor " +
                     $"{ArmAboveHeadMinM:F2}), so BOTH hands are in the shooter's eyeline rather than one " +
                     "arm up in a steal/one-handed-block silhouette.");
        else
            Fail($"contest-arms-rise: worst (lower) wrist-above-head during Active was " +
                 $"{_maxWristAboveHeadDuringActive:F4}, need >= {ArmAboveHeadMinM:F2} (sawActive={_sawActive}). " +
                 "A one-armed clip fails here BY DESIGN — that is a steal or block pose, not a contest. Note that " +
                 "contest-stays-grounded would still PASS on this clip, which is exactly why this " +
                 "scenario exists.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: control-contest-arms-low-startup (control) ────────────────
    private void VerdictControlArmsLowStartup()
    {
        GD.Print($"[contest-anim]   wrist-above-head: startup={_maxWristAboveHeadDuringStartup:F4} " +
                 $"active={_maxWristAboveHeadDuringActive:F4}");

        // Premise: Active must genuinely have gone overhead, or "Startup did not"
        // is trivially satisfied by a clip in which the arms never move.
        bool premise = _sawStartup && _maxWristAboveHeadDuringActive >= ArmAboveHeadMinM;
        float margin = _maxWristAboveHeadDuringActive - _maxWristAboveHeadDuringStartup;
        bool pass = premise && margin >= ArmRiseMinMargin;
        if (pass)
            GD.Print($"[contest-anim] PASS control-contest-arms-low-startup — the wind-up kept the hands " +
                     $"{margin:F4} lower (startup {_maxWristAboveHeadDuringStartup:F4} vs active " +
                     $"{_maxWristAboveHeadDuringActive:F4}, floor {ArmRiseMinMargin:F2}), so the overhead " +
                     "extension is a phase-localised EVENT the shooter can read rather than a pose the clip " +
                     "holds throughout (ADR-0003).");
        else
            Fail($"control-contest-arms-low-startup: startup={_maxWristAboveHeadDuringStartup:F4}, " +
                 $"active={_maxWristAboveHeadDuringActive:F4}, margin={margin:F4} " +
                 $"(need >= {ArmRiseMinMargin:F2}), sawStartup={_sawStartup}. If the premise broke, " +
                 "'the arms were low in Startup' proves nothing, so this fails rather than passes.");
        Finish(pass ? 0 : 1);
    }

    // ── Static scenarios: no live tree, pure resource/scene inspection ──────
    private void RunStaticCheck()
    {
        switch (_scenario)
        {
            case "contest-no-placeholder-leak": RunNoPlaceholderLeakCheck(); break;
            case "contest-segment-lengths":     RunSegmentLengthsCheck(); break;
            case "contest-edges":               RunEdgesCheck(); break;
        }
    }

    // ── Scenario: contest-segment-lengths ───────────────────────────────────
    // #276 rule 4 / #295. Tick windows are read from ContestMove.DefaultFrameData,
    // NOT hardcoded, so a future #238 retune that forgets to re-run
    // tools/rebuild_contest_clips.gd goes red here and names the tool. (That
    // tool duplicates the 6/8/20 counts for slicing; this is the tripwire that
    // makes the duplication safe.)
    private void RunSegmentLengthsCheck()
    {
        var lib = GD.Load<AnimationLibrary>("res://assets/locomotion.res");
        if (lib == null)
        {
            Fail("assets/locomotion.res failed to load — cannot evaluate contest-segment-lengths.");
            Finish(1);
            return;
        }

        double tps = Engine.PhysicsTicksPerSecond;
        var frames = ContestMove.DefaultFrameData;
        const double ToleranceSeconds = 1.0 / 60.0 + 1e-6; // "within one tick", tiny float-noise margin

        (string Clip, int Ticks)[] windows =
        {
            ("conteststartup",  frames.StartupFrames),
            ("contestactive",   frames.ActiveFrames),
            ("contestrecovery", frames.RecoveryFrames),
        };

        bool pass = true;
        foreach (var (clipName, ticks) in windows)
        {
            if (!lib.HasAnimation(clipName))
            {
                Fail($"AnimationLibrary has no clip '{clipName}' — run tools/rebuild_contest_clips.gd.");
                pass = false;
                continue;
            }

            double expectedSeconds = ticks / tps;
            double actualSeconds = lib.GetAnimation(clipName).Length;
            double deviationSeconds = Math.Abs(actualSeconds - expectedSeconds);
            GD.Print($"[contest-anim]   '{clipName}': length={actualSeconds:F6}s " +
                     $"expected={expectedSeconds:F6}s ({ticks} ticks @ {tps} tps), " +
                     $"deviation={deviationSeconds:F6}s");

            if (deviationSeconds > ToleranceSeconds)
            {
                Fail($"clip '{clipName}' is {actualSeconds:F6}s, expected {expectedSeconds:F6}s " +
                     $"({ticks} ticks at {tps} tps — ContestMove.DefaultFrameData), a deviation of " +
                     $"{deviationSeconds:F6}s exceeds the one-tick tolerance ({ToleranceSeconds:F6}s). " +
                     "Re-run tools/rebuild_contest_clips.gd after retuning the move's frame data.");
                pass = false;
            }
        }

        if (pass)
            GD.Print("[contest-anim] PASS contest-segment-lengths — all three clips' durations are within one " +
                     "tick of ContestMove.DefaultFrameData's Startup/Active/Recovery windows.");
        else
            GD.PrintErr("[contest-anim] FAIL contest-segment-lengths — see per-clip deviations above.");

        Finish(pass ? 0 : 1);
    }

    // ── Scenario: contest-no-placeholder-leak ───────────────────────────────
    // The direct statement that #296 is closed for this move. An ALLOWLIST, not
    // a placeholder blocklist: a blocklist ("is it locomotion/idle or
    // locomotion/run?") closes #296 but waves through the likelier slip — these
    // three sub-resources were hand-authored directly beneath the layup ones, so
    // a state pointing at locomotion/layupactive is a real, non-placeholder clip
    // that a blocklist accepts and GetCurrentNode() cannot see either (the STATE
    // name would still read "ContestActive").
    //
    // This is also the scenario the issue names for the mutation proof: revert a
    // sub-resource to AnimationNodeAnimation_mv277ph and it must go red.
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
            ("ContestStartup",  "locomotion/conteststartup"),
            ("ContestActive",   "locomotion/contestactive"),
            ("ContestRecovery", "locomotion/contestrecovery"),
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
            GD.Print($"[contest-anim]   {stateName} -> {actualClip}");

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
            GD.Print("[contest-anim] PASS contest-no-placeholder-leak — all three Contest states point at " +
                     "their OWN per-move clips, not the shared locomotion/idle placeholder #314 moved them " +
                     "off of.");
        else
            GD.PrintErr("[contest-anim] FAIL contest-no-placeholder-leak — see per-state mismatches above.");

        Finish(pass ? 0 : 1);
    }

    // ── Scenario: contest-edges ─────────────────────────────────────────────
    // README trap 8 / #279 established that a DELETED transition edge is
    // invisible to GetCurrentNode(): Travel() is a pathfinder, so it routes
    // around the gap and still arrives. That verdict is specific to RUNTIME
    // OBSERVATION. This scenario reads the transition list off the RESOURCE,
    // where a missing edge is simply absent — the instrument JumpshotAnimTest's
    // header named but did not build.
    //
    // Contest is a DEFENSIVE move, so unlike layup it asserts exactly the six
    // standard edges and additionally asserts the ABSENCE of the Dribble-family
    // edges. That negative half is a real assertion, not decoration: those six
    // edges are one copy-paste away (they sit adjacent in the .tscn), they can
    // never fire because a defender is not dribbling, and Travel()'s pathfinder
    // would happily route through an edge that models nothing.
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
            ("Locomotion", "ContestStartup"),
            ("ContestStartup", "ContestActive"),
            ("ContestActive", "ContestRecovery"),
            ("ContestRecovery", "Locomotion"),
            ("ContestStartup", "ContestRecovery"), // feint / early-out
            ("ContestStartup", "Locomotion"),      // abort
        };

        var present = new System.Collections.Generic.HashSet<string>();
        for (int i = 0; i < sm.GetTransitionCount(); i++)
            present.Add($"{sm.GetTransitionFrom(i)}->{sm.GetTransitionTo(i)}");

        bool pass = true;
        foreach (var (from, to) in required)
        {
            bool here = present.Contains($"{from}->{to}");
            GD.Print($"[contest-anim]   edge {from} -> {to}: {(here ? "present" : "MISSING")}");
            if (!here)
            {
                Fail($"scenes/Player.tscn has no transition '{from}' -> '{to}'. Travel()'s pathfinder will " +
                     "route around the gap, so NO runtime scenario can catch this — only this resource-level " +
                     "check can.");
                pass = false;
            }
        }

        // The negative half: no Contest<->Dribble edge may exist, and the total
        // count of Contest-touching edges must be exactly the six above.
        int contestTouching = 0;
        for (int i = 0; i < sm.GetTransitionCount(); i++)
        {
            string from = sm.GetTransitionFrom(i);
            string to = sm.GetTransitionTo(i);
            bool touches = from.StartsWith("Contest") || to.StartsWith("Contest");
            if (!touches) continue;
            contestTouching++;
            if (from.StartsWith("Dribble") || to.StartsWith("Dribble"))
            {
                Fail($"scenes/Player.tscn has a Contest<->Dribble transition '{from}' -> '{to}'. Contest is " +
                     "defensive and a defender is not dribbling, so this edge can never fire; it makes the " +
                     "graph lie about reachability and gives Travel()'s pathfinder a route that models " +
                     "nothing (#314).");
                pass = false;
            }
        }

        GD.Print($"[contest-anim]   Contest-touching edges = {contestTouching} (want exactly {required.Length})");
        if (contestTouching != required.Length)
        {
            Fail($"scenes/Player.tscn has {contestTouching} Contest-touching transitions, expected exactly " +
                 $"{required.Length}. A defensive move gets the six standard edges and nothing else.");
            pass = false;
        }

        if (pass)
            GD.Print($"[contest-anim] PASS contest-edges — exactly the {required.Length} standard transitions " +
                     "are present, and none of the Dribble-family edges an offensive move carries.");
        else
            GD.PrintErr("[contest-anim] FAIL contest-edges — see above.");

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
    //
    // All read GLOBAL bone poses (GetBoneGlobalPose), i.e. positions in the
    // Skeleton3D's own space, and every assertion is a DIFFERENCE between two
    // such readings. That matters: PlayerRigScaler rewrites bone pose SCALE at
    // runtime and the Y Bot import carries its own unit scale, so an absolute
    // metre threshold would be measuring the rig setup as much as the clip.
    // A difference in one consistent space is scale-stable and is what the
    // legibility claim actually rests on ("the feet stayed where this attempt
    // started").

    // Lowest of the two toes. The LOWEST on purpose: a contest's stance widens
    // and its weight shifts, so one foot may well rise slightly — the claim is
    // that the player never left the floor, and the floor is defined by
    // whichever foot is still on it.
    private static float MeasureLowestToe(Skeleton3D skel)
    {
        float lowest = float.PositiveInfinity;
        foreach (string toe in new[] { "mixamorig_LeftToeBase", "mixamorig_RightToeBase" })
        {
            int idx = skel.FindBone(toe);
            if (idx < 0) continue;
            lowest = Math.Min(lowest, skel.GetBoneGlobalPose(idx).Origin.Y);
        }
        // Not 0f: a rig whose toe bones could not be found must not silently
        // report a perfectly grounded 0.0000 for every tick, which is precisely
        // the vacuous pass contest-stays-grounded is most exposed to.
        return float.IsPositiveInfinity(lowest) ? float.NaN : lowest;
    }

    // The LOWER wrist relative to the head bone — deliberately the worse of the
    // two, matching author_contest.py::_wrist_above_head_m and
    // rebuild_contest_clips.gd::_wrist_above_head so all three instruments
    // measure the same quantity.
    //
    // Taking the HIGHER wrist would be the bug: a clip that raised one arm and
    // left the other down would satisfy an "arms up" gate, but a one-armed
    // overhead silhouette is a steal or a one-handed block, not a contest. The
    // clip being symmetric is what this file is asserting, not a premise it may
    // assume — so "both wrists cleared" has to be the measured claim, and the
    // minimum is what makes it one. Checking neither side specifically is also
    // what keeps this unhanded: min() names no hand, it just requires both.
    private static float MeasureWristAboveHead(Skeleton3D skel)
    {
        int head = skel.FindBone("mixamorig_Head");
        if (head < 0) return float.NegativeInfinity;
        float headY = skel.GetBoneGlobalPose(head).Origin.Y;

        float worst = float.PositiveInfinity;
        foreach (string wrist in new[] { "mixamorig_LeftHand", "mixamorig_RightHand" })
        {
            int idx = skel.FindBone(wrist);
            if (idx < 0) continue;
            worst = Math.Min(worst, skel.GetBoneGlobalPose(idx).Origin.Y - headY);
        }
        // No wrist resolved at all: report the failing extreme, never a passing
        // one — same rule as MeasureLowestToe's NaN.
        return float.IsPositiveInfinity(worst) ? float.NegativeInfinity : worst;
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

    private void Fail(string message) => GD.PrintErr($"[contest-anim] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[contest-anim] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
