using System;
using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #283 (ADR-0016): proves the THREE BLOCK
// ANIMATION STATES (BlockStartup / BlockActive / BlockRecovery) wired into
// scenes/Player.tscn are real — entered end-to-end by a real BlockMove, bound to
// the right clips, cut to the right windows, and actually MOVING the rig.
//
// Before #283 all three states shared ONE sub-resource
// (AnimationNodeAnimation_mv277ph) pointing at locomotion/idle, so the biggest
// commitment on defence played the same pixels as standing still — for all three
// phases at once. That is the #296 defect in its most extreme form.
//
//   godot --headless --path . res://tests/integration/BlockAnimTest.tscn -- --harness-scenario=block-phases
//   …=block-no-placeholder-leak | block-segment-lengths
//   …=block-airborne-active | control-block-grounded-startup
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//
// ── The read this file exists to protect ────────────────────────────────────
// BLOCK LEAVES THE GROUND. CONTEST DOES NOT. Those two moves raise the same
// arms; the feet are the entire difference, and the commitment ladder
// ContestMove.cs:53-54 prices (contest 6 < steal 8 < block 10 startup ticks)
// rests on an opponent being able to see it. A block that squats and un-squats
// without leaving the floor is visually indistinguishable from a contest, and no
// other assertion in this repo would notice — the phases would still be entered,
// the clips would still have the right durations, the arms would still go up.
//
// So block-airborne-active is the load-bearing scenario here, and it is the exact
// INVERSE of ContestAnimTest's load-bearing contest-stays-grounded.
//
// ── Why the airborne gate needs its own control ─────────────────────────────
// "The hips rose" is not vacuously satisfiable the way "the feet stayed down" is
// — but it is still measured against a BASELINE, and the baseline is where the
// vacuity hides. blender_anim_lib.verify_airborne's docstring states the trap
// directly: comparing the airborne window's peak against that same window's own
// minimum proves nothing, because every sample in it is already elevated.
//
// control-block-grounded-startup supplies the missing half. It asserts the hips
// do NOT rise during Startup, which is what makes the Startup-derived baseline a
// real, independently-established ground level rather than a number read off the
// jump itself. Without it, a clip that begins mid-air and stays there would pass
// block-airborne-active — the rise would be measured from an airborne floor.
//
// It also does a second job the grounded/airborne pairing in ContestAnimTest
// needs a whole different MOVE to do: because both readings come from the same
// run of the same clip through the same MeasureHipHeight path, a dead
// instrument (no Skeleton3D, an unbound clip, README trap 13's "Armature/"
// prefix) cannot produce a passing pair. A dead instrument reads a flat 0 for
// both, which passes the control and FAILS the positive gate.
//
// ── Why ActiveAnimNodeForHarness, not the resolver's return value (#257) ─────
// Travel() to a missing/misnamed state only LOGS; it never throws. Asserting
// MoveAnimResolver.ResolveStateName(...) == "BlockActive" would pass on a
// Player.tscn with no Block states at all, since the resolver has no notion the
// .tscn exists. Only the live AnimationNodeStateMachinePlayback proves wiring.
//
// ── Cosmetic-only (issue #283's standing constraint) ────────────────────────
// #283 is a CLIP issue. Nothing here observes or feeds DefensiveResolution,
// #214's block reach gate, BlockMove.DefaultBlockGraceTicks or any ADR-0018
// window. This harness begins the move via BeginMoveForHarness — downstream of
// every gameplay gate — precisely so it cannot become a second, weaker test of
// the block→turnover coupling BlockTurnoverTest already owns.
public partial class BlockAnimTest : Node
{
    private const double TimeoutSeconds = 15.0;
    private const int ArmFrames = 2;      // ticks for TryAssignTipoffHolder to run
    private const int ActFrames = 2;      // ticks after tipoff before Begin (position/heading settle)
    // > block startup(10)+active(8)+recovery(20)=38, with generous slack.
    private const int ObserveFrames = 70;

    private static readonly Vector3 ActorSpot = new(0f, 0f, 2f);
    private static readonly Vector3 FarSpot = new(12f, 0f, 12f); // out of PickupRadius
    private static readonly Vector3 RimCenter = new(0f, 3.05f, 0f); // BallController.DefaultRimCenter

    private static readonly string[] KnownScenarios =
    {
        "block-phases",
        "block-no-placeholder-leak",
        "block-segment-lengths",
        "block-airborne-active",
        "control-block-grounded-startup",
    };

    // Scenarios that need no live tree — pure resource/scene inspection.
    private static readonly string[] StaticScenarios =
    {
        "block-no-placeholder-leak", "block-segment-lengths",
    };

    // ── Geometry thresholds, in skeleton-local units ────────────────────────
    // Every reading below is a DIFFERENCE between two GetBoneGlobalPose values
    // in the Skeleton3D's own space, never an absolute. PlayerRigScaler rewrites
    // bone pose SCALE at runtime and the Y Bot import carries its own unit scale,
    // so an absolute threshold would be measuring the rig setup as much as the
    // clip; a difference in one consistent space is scale-stable and is what the
    // legibility claim actually rests on.

    // The load-bearing floor. author_block.py authors a 0.30 rise and both
    // upstream instruments measured exactly 0.3000 (Blender `hip_rise_m`,
    // rebuild_block_clips.gd G4), so this floor sits at two thirds of the
    // authored value. The gap is deliberate: if the measured rise ever has to be
    // trimmed to within a centimetre of this floor, the move has stopped being a
    // jump and the fix belongs in the clip, not here.
    private const float MinHipRiseDuringActive = 0.20f;

    // The control's ceiling: how far the hips may rise ABOVE the Startup
    // baseline during Startup itself. Authored motion over that window is purely
    // DOWNWARD (0.00 -> -0.20 m), so the true value is ~0 and this is a noise
    // band for the foot-IK residual and the tick-vs-clip-time resampling, not a
    // drift allowance.
    private const float MaxHipRiseDuringStartup = 0.03f;

    private string _scenario = "block-phases";

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

    // ── Geometry, latched at event time ─────────────────────────────────────
    // Never recomputed at verdict time: by then the move is over and the rig has
    // returned to Locomotion, so a verdict-time reading would describe idle.
    //
    // THE PHASE LABEL LEADS THE POSE BY ONE TICK. Even with
    // CallbackModeProcess=Physics (README trap 6, which fixes the LABEL), the
    // first tick on which GetCurrentNode() names a phase still holds the
    // PREVIOUS phase's pose. So each phase's first observed tick is DROPPED, and
    // each gate asserts it observed more than one tick — otherwise the drop
    // silently empties the sample and the gate goes green on nothing. That
    // failure mode is not hypothetical: it made two #316 gates green and
    // worthless until mutation caught them.
    private int _startupTicks;
    private int _activeTicks;

    // The grounded reference the airborne claim is measured against: the hip
    // height on the first pose-VALID Startup tick — i.e. the ready stance the
    // block is thrown from, before the crouch.
    //
    // NOT the Startup MINIMUM, and the distinction is the whole scenario. Block
    // deliberately drops the hips 0.20 m during Startup, so a clip that squats
    // and un-squats WITHOUT EVER LEAVING THE FLOOR measures exactly 0.20 m of
    // "rise" against its own trough and would pass a 0.20 m floor — while being
    // precisely the contest-lookalike defect this file exists to catch. Measured
    // against the ready stance instead, that same clip reads 0.00.
    //
    // This also makes control-block-grounded-startup load-bearing rather than
    // decorative: the baseline is only a ground level if the hips genuinely
    // never rose above it during Startup, which is exactly what the control
    // asserts. Against a minimum-based baseline the control would prove nothing.
    private bool _haveHipBaseline;
    private float _hipBaseline;
    private float _maxHipRiseDuringStartup = float.NegativeInfinity;
    private float _maxHipRiseDuringActive = float.NegativeInfinity;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "block-phases");
        GD.Print($"[block-anim] scenario={_scenario} booting headless…");

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
        // chain runs every tick, same as ContestAnimTest/LayupAnimTest.
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
                // Square up to the rim. Nothing here reads facing, but a coherent
                // heading keeps the sampled pose meaningful.
                _actor.SetHeadingForHarness(
                    Mathf.Atan2(RimCenter.X - ActorSpot.X, RimCenter.Z - ActorSpot.Z));
                _step = Step.Act;
                _stepDeadlineFrame = _frame + ActFrames;
                break;

            case Step.Act:
                if (_frame < _stepDeadlineFrame) break;
                // The real production choke point (BeginCommittedMove), reached
                // via the generic harness seam — deliberately downstream of every
                // defensive gate, which BlockTurnoverTest owns.
                if (!_actor.BeginMoveForHarness(new BlockMove()))
                {
                    Fail($"{_scenario}: BeginMoveForHarness(BlockMove) returned false — " +
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

        if (!_sawStartup && node == "BlockStartup") _sawStartup = true;
        if (_sawStartup && !_sawActive && node == "BlockActive") _sawActive = true;
        if (_sawActive && !_sawRecovery && node == "BlockRecovery") _sawRecovery = true;
        if (node == "Startup" || node == "Active" || node == "Recovery") _sawGenericPlaceholder = true;

        // ── Geometry, sampled at event time ──────────────────────────────────
        var skel = FindSkeleton(_actor);
        if (skel == null) return;

        if (node == "BlockStartup")
        {
            // Drop the phase's FIRST observed tick — it still carries the
            // previous phase's pose (see the field block). Everything after it
            // is a real Startup pose.
            _startupTicks++;
            if (_startupTicks == 1) return;

            float hip = MeasureHipHeight(skel);
            if (float.IsNaN(hip)) return;
            if (!_haveHipBaseline)
            {
                _hipBaseline = hip;
                _haveHipBaseline = true;
            }
            _maxHipRiseDuringStartup = Math.Max(_maxHipRiseDuringStartup, hip - _hipBaseline);
        }
        else if (node == "BlockActive")
        {
            _activeTicks++;
            if (_activeTicks == 1) return;

            float hip = MeasureHipHeight(skel);
            if (float.IsNaN(hip) || !_haveHipBaseline) return;
            _maxHipRiseDuringActive = Math.Max(_maxHipRiseDuringActive, hip - _hipBaseline);
        }
    }

    private void RenderVerdict()
    {
        switch (_scenario)
        {
            case "block-phases":                    VerdictPhases(); break;
            case "block-airborne-active":           VerdictAirborneActive(); break;
            case "control-block-grounded-startup":  VerdictControlGroundedStartup(); break;
        }
    }

    // ── Scenario: block-phases (positive) ───────────────────────────────────
    private void VerdictPhases()
    {
        bool pass = _sawStartup && _sawActive && _sawRecovery;
        if (pass)
            GD.Print("[block-anim] PASS block-phases — the tree was observed on \"BlockStartup\", then " +
                     "\"BlockActive\", then \"BlockRecovery\", in that order (the .tscn states and their " +
                     "transitions are live).");
        else
            Fail($"block-phases: expected BlockStartup -> BlockActive -> BlockRecovery, in order; got " +
                 $"sawStartup={_sawStartup}, sawActive={_sawActive}, sawRecovery={_sawRecovery}, " +
                 $"sawGenericPlaceholder={_sawGenericPlaceholder}, " +
                 $"lastAnimNode={_actor.ActiveAnimNodeForHarness}.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: block-airborne-active (THE load-bearing one) ──────────────
    // BLOCK LEAVES THE GROUND. CONTEST DOES NOT. This is the exact inverse of
    // ContestAnimTest's contest-stays-grounded, and it is the only assertion in
    // this repo that can tell the two moves apart — every other gate here
    // (phases entered, segment lengths, distinct clips, arms overhead) is
    // satisfied identically by a contest.
    //
    // Measured on the HIPS rather than the toes, which is the opposite of
    // ContestAnimTest's choice and correct for the opposite reason. Contest
    // measures toes because it deliberately drops the hips into a crouch, so a
    // hip gate there would have to be loose enough to permit the drop and would
    // then be blind to a small hop. Block ALSO crouches — more deeply than any
    // other move — but it is the body's departure that is being claimed, and the
    // toes leave the floor slightly before the centre of mass does and land
    // slightly after. The hips are the honest measure of "the body left", and
    // they keep this instrument on the same quantity as the two upstream ones
    // (blender_anim_lib.verify_airborne and rebuild_block_clips.gd's G4, which
    // both measured exactly 0.3000).
    private void VerdictAirborneActive()
    {
        GD.Print($"[block-anim]   hip rise vs the Startup ready-stance baseline: " +
                 $"active={_maxHipRiseDuringActive:F4} startup={_maxHipRiseDuringStartup:F4} " +
                 $"(floor {MinHipRiseDuringActive:F2}); pose-valid ticks: startup={Math.Max(0, _startupTicks - 1)} " +
                 $"active={Math.Max(0, _activeTicks - 1)}");

        // Premise: both phases must have been observed for MORE THAN ONE tick,
        // or the lead-tick drop left an empty sample and this gate is measuring
        // nothing. Without this it reports NegativeInfinity and fails — but a
        // future retune that shortened a phase to a single tick would be a
        // confusing failure rather than a named one.
        bool premise = _startupTicks > 1 && _activeTicks > 1 && _haveHipBaseline;
        bool pass = premise && _maxHipRiseDuringActive >= MinHipRiseDuringActive;

        if (pass)
            GD.Print($"[block-anim] PASS block-airborne-active — the hips rose {_maxHipRiseDuringActive:F4} " +
                     $"above the grounded ready stance during Active (floor {MinHipRiseDuringActive:F2}), read " +
                     "off the live Skeleton3D. The block genuinely leaves the ground, so it stays " +
                     "distinguishable from a contest and ContestMove's commitment ladder (contest 6 < steal 8 " +
                     "< block 10 startup ticks) holds.");
        else
            Fail($"block-airborne-active: hip rise during Active was {_maxHipRiseDuringActive:F4}, need >= " +
                 $"{MinHipRiseDuringActive:F2} (startupTicks={_startupTicks}, activeTicks={_activeTicks}, " +
                 $"haveBaseline={_haveHipBaseline}). If the premise broke, this fails rather than passes — a " +
                 "rise measured over an unobserved phase is vacuous. If the rise is real but short, the block " +
                 "no longer leaves the ground and is now visually indistinguishable from a contest (#283): a " +
                 "squat-and-un-squat has the same silhouette and every other scenario in this file would " +
                 "still be green.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: control-block-grounded-startup (the control) ──────────────
    // The premise block-airborne-active depends on. It asserts the hips do NOT
    // rise above the ready-stance baseline during Startup — i.e. that the
    // baseline really is a ground level and not a point the clip was already
    // travelling through. A clip that began mid-air and stayed there would sail
    // through the positive gate on a baseline read off the jump itself.
    //
    // It does a second job too, which is why it needs no separate move the way
    // ContestAnimTest's control does. Both readings come from the SAME run of
    // the SAME clip through the SAME MeasureHipHeight path, so a dead instrument
    // — no Skeleton3D, an unbound clip, README trap 13's surviving "Armature/"
    // track prefix — cannot produce a passing pair: it reads a flat 0 for both,
    // which satisfies this ceiling and FAILS the positive gate. That asymmetry
    // is why this control asserts the positive gate's result as its own premise
    // below: without it, "the hips did not rise in Startup" is trivially true of
    // a clip in which nothing moves at all.
    private void VerdictControlGroundedStartup()
    {
        GD.Print($"[block-anim]   (control) hip rise vs the Startup ready-stance baseline: " +
                 $"startup={_maxHipRiseDuringStartup:F4} (ceiling {MaxHipRiseDuringStartup:F2}) " +
                 $"active={_maxHipRiseDuringActive:F4}; pose-valid ticks: " +
                 $"startup={Math.Max(0, _startupTicks - 1)} active={Math.Max(0, _activeTicks - 1)}");

        bool premise = _startupTicks > 1 && _activeTicks > 1 && _haveHipBaseline
                       && _maxHipRiseDuringActive >= MinHipRiseDuringActive;
        bool pass = premise && _maxHipRiseDuringStartup <= MaxHipRiseDuringStartup;

        if (pass)
            GD.Print($"[block-anim] PASS control-block-grounded-startup — through the whole wind-up the hips " +
                     $"never rose more than {_maxHipRiseDuringStartup:F4} above the stance the move began in " +
                     $"(ceiling {MaxHipRiseDuringStartup:F2}), while the SAME instrument read " +
                     $"{_maxHipRiseDuringActive:F4} during Active. So the baseline is a real ground level and " +
                     "block-airborne-active's rise is measured against the floor, not against the jump.");
        else
            Fail($"control-block-grounded-startup: startup rise={_maxHipRiseDuringStartup:F4} " +
                 $"(ceiling {MaxHipRiseDuringStartup:F2}), active rise={_maxHipRiseDuringActive:F4} " +
                 $"(premise floor {MinHipRiseDuringActive:F2}), startupTicks={_startupTicks}, " +
                 $"activeTicks={_activeTicks}. If the premise broke, the instrument never registered a " +
                 "departure at all, so 'the hips stayed down in Startup' proves nothing and this fails rather " +
                 "than passes. If the startup rise is real, the wind-up is already leaving the ground and " +
                 "block-airborne-active's baseline is not a floor — treat its green as unverified.");
        Finish(pass ? 0 : 1);
    }

    // ── Static scenarios: no live tree, pure resource/scene inspection ──────
    private void RunStaticCheck()
    {
        switch (_scenario)
        {
            case "block-no-placeholder-leak": RunNoPlaceholderLeakCheck(); break;
            case "block-segment-lengths":     RunSegmentLengthsCheck(); break;
        }
    }

    // ── Scenario: block-segment-lengths ─────────────────────────────────────
    // #276 rule 4 / #295. Tick windows are read from BlockMove.DefaultFrameData,
    // NOT hardcoded, so a future #238 retune that forgets to re-run
    // tools/rebuild_block_clips.gd goes red here and names the tool. (That tool
    // duplicates the 10/8/20 counts for slicing; this is the tripwire that makes
    // the duplication safe.)
    private void RunSegmentLengthsCheck()
    {
        var lib = GD.Load<AnimationLibrary>("res://assets/locomotion.res");
        if (lib == null)
        {
            Fail("assets/locomotion.res failed to load — cannot evaluate block-segment-lengths.");
            Finish(1);
            return;
        }

        double tps = Engine.PhysicsTicksPerSecond;
        var frames = BlockMove.DefaultFrameData;
        // A ONE-TICK tolerance (the value LayupAnimTest/StealAnimTest inherited)
        // would defeat this scenario's whole purpose: bumping StartupFrames
        // 10 -> 11 deviates by exactly 1/60 s, slips under the bar, and reports
        // green while blockstartup is still cut to 10 ticks and no longer covers
        // the move's Startup window. The slice is exact to ~1e-5 s, so the
        // tolerance only has to absorb float noise — it is a noise band, not a
        // drift allowance. 1e-3 s is ~100x the observed noise and ~17x TIGHTER
        // than the smallest retune that could occur.
        const double ToleranceSeconds = 1e-3;

        (string Clip, int Ticks)[] windows =
        {
            ("blockstartup",  frames.StartupFrames),
            ("blockactive",   frames.ActiveFrames),
            ("blockrecovery", frames.RecoveryFrames),
        };

        bool pass = true;
        foreach (var (clipName, ticks) in windows)
        {
            if (!lib.HasAnimation(clipName))
            {
                Fail($"AnimationLibrary has no clip '{clipName}' — run tools/rebuild_block_clips.gd.");
                pass = false;
                continue;
            }

            double expectedSeconds = ticks / tps;
            double actualSeconds = lib.GetAnimation(clipName).Length;
            double deviationSeconds = Math.Abs(actualSeconds - expectedSeconds);
            GD.Print($"[block-anim]   '{clipName}': length={actualSeconds:F6}s " +
                     $"expected={expectedSeconds:F6}s ({ticks} ticks @ {tps} tps), " +
                     $"deviation={deviationSeconds:F6}s");

            if (deviationSeconds > ToleranceSeconds)
            {
                Fail($"clip '{clipName}' is {actualSeconds:F6}s, expected {expectedSeconds:F6}s " +
                     $"({ticks} ticks at {tps} tps — BlockMove.DefaultFrameData), a deviation of " +
                     $"{deviationSeconds:F6}s exceeds the float-noise tolerance ({ToleranceSeconds:F6}s). " +
                     "Re-run tools/rebuild_block_clips.gd after retuning the move's frame data.");
                pass = false;
            }
        }

        if (pass)
            GD.Print("[block-anim] PASS block-segment-lengths — all three clips' durations match " +
                     "BlockMove.DefaultFrameData's Startup/Active/Recovery windows EXACTLY. A one-tick " +
                     "retune of any window goes red here.");
        else
            GD.PrintErr("[block-anim] FAIL block-segment-lengths — see per-clip deviations above.");

        Finish(pass ? 0 : 1);
    }

    // ── Scenario: block-no-placeholder-leak ─────────────────────────────────
    // The direct statement that #296 is closed for this move, and the scenario
    // #283 names for the mutation proof: revert any one sub-resource to
    // AnimationNodeAnimation_mv277ph and this must go red.
    //
    // An ALLOWLIST, not a placeholder blocklist (JumpshotAnimTest/ContestAnimTest
    // shape). A blocklist ("is it locomotion/idle or locomotion/run?") closes
    // #296 but waves through the likelier slip — these three sub-resources are
    // hand-authored directly beneath the contest ones, so a state left pointing
    // at locomotion/contestactive is a real, non-placeholder clip that a
    // blocklist accepts and GetCurrentNode() cannot see either (the STATE name
    // would still read "BlockActive").
    //
    // Block is the case that makes the distinction concrete rather than
    // theoretical: contest is the ONE move whose clip is genuinely
    // indistinguishable from block's except for the feet, so a block state
    // pointing at a contest clip is the single most plausible wrong-clip error
    // in this file's neighbourhood — and the single least visible.
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
            ("BlockStartup",  "locomotion/blockstartup"),
            ("BlockActive",   "locomotion/blockactive"),
            ("BlockRecovery", "locomotion/blockrecovery"),
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
            GD.Print($"[block-anim]   {stateName} -> {actualClip}");

            if (actualClip != expectedClip)
            {
                string extra = placeholderClips.Contains(actualClip)
                    ? " — this is the #296 GENERIC PLACEHOLDER; the state was never repointed at its own clip."
                    : " — a real clip, but the wrong one (copy-paste from a neighbouring move's sub-resource).";
                Fail($"state '{stateName}' points at '{actualClip}', expected '{expectedClip}'{extra}");
                pass = false;
            }
        }

        // The three states must ALSO be three DISTINCT sub-resources. Before #283
        // they shared one (AnimationNodeAnimation_mv277ph), which is how a single
        // edit could set all three phases to the same clip — and the per-state
        // allowlist above cannot see sharing at all, because a shared node that
        // happens to carry the right clip name would satisfy every row.
        var seen = new System.Collections.Generic.Dictionary<AnimationNode, string>();
        foreach (var (stateName, _) in states)
        {
            if (!stateMachine.HasNode(stateName)) continue;
            var node = stateMachine.GetNode(stateName);
            if (seen.TryGetValue(node, out string other))
            {
                Fail($"states '{other}' and '{stateName}' are backed by the SAME AnimationNodeAnimation " +
                     "sub-resource. Each phase needs its own, or one retune silently repoints all of them " +
                     "(this is precisely the pre-#283 mv277ph shape).");
                pass = false;
                continue;
            }
            seen[node] = stateName;
        }

        if (pass)
            GD.Print("[block-anim] PASS block-no-placeholder-leak — all three Block states point at their OWN " +
                     "per-move clips via three distinct sub-resources, not the shared locomotion/idle " +
                     "placeholder #283 moved them off of.");
        else
            GD.PrintErr("[block-anim] FAIL block-no-placeholder-leak — see per-state mismatches above.");

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
    // Reads a GLOBAL bone pose (GetBoneGlobalPose), i.e. a position in the
    // Skeleton3D's own space, and every assertion is a DIFFERENCE between two
    // such readings — see the threshold block for why an absolute would be
    // measuring the rig setup as much as the clip.
    //
    // NaN, never 0, when the bone cannot be found. A rig whose Hips could not be
    // resolved must not silently report a perfectly grounded 0.0000 for every
    // tick: that is the single vacuous pass this file is most exposed to, since
    // it would satisfy control-block-grounded-startup's ceiling exactly.
    // Returning NaN makes the sample get skipped, which starves the baseline and
    // fails BOTH scenarios on their premises instead of passing one of them.
    private static float MeasureHipHeight(Skeleton3D skel)
    {
        int idx = skel.FindBone("mixamorig_Hips");
        return idx < 0 ? float.NaN : skel.GetBoneGlobalPose(idx).Origin.Y;
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

    private void Fail(string message) => GD.PrintErr($"[block-anim] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[block-anim] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
