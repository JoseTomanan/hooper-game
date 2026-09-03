using System;
using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;
using Hooper.Systems;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #284: proves the cosmetic rebound-grab
// one-shot (MoveAnimState.ReboundGrab, PlayerController.TickReboundGrabLatch, the
// scenes/Player.tscn ReboundGrab state) is REACHED end-to-end when a player
// secures a LIVE rebound, and — the load-bearing controls — that it does NOT
// fire on a clean-catch tipoff or a made-basket inbound.
//
//   godot --headless --path . res://tests/integration/ReboundGrabTest.tscn -- --harness-scenario=grab-fires
//   godot --headless --path . res://tests/integration/ReboundGrabTest.tscn -- --harness-scenario=made-basket-no-grab
//   godot --headless --path . res://tests/integration/ReboundGrabTest.tscn -- --harness-scenario=consecutive-rebounds
//   godot --headless --path . res://tests/integration/ReboundGrabTest.tscn -- --harness-scenario=rebound-clip-contract
//   godot --headless --path . res://tests/integration/ReboundGrabTest.tscn -- --harness-scenario=rebound-display-duration
//   godot --headless --path . res://tests/integration/ReboundGrabTest.tscn -- --harness-scenario=rebound-latch-mutation-control
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//   Omitting --harness-scenario defaults to "grab-fires".
//
// ── Why ActiveAnimNodeForHarness, not the resolver's own decision (#257) ────
// Same discipline as MoveKindAnimTest/PivotAnimTest: reading
// AnimationNodeStateMachinePlayback.GetCurrentNode() (via the live AnimationTree)
// asserts what the state machine ACTUALLY did, not merely that ResolveStateName
// returned "ReboundGrab" in isolation (already covered by MoveAnimResolverTests).
// Travel() to a missing/misnamed state only LOGS — so this is the only honest
// proof scenes/Player.tscn's ReboundGrab state and its transitions are wired.
//
// ── Why the positive scenario makes the controls meaningful ─────────────────
// "grab-fires" proves the SAME rig genuinely reaches ReboundGrab off an
// observably-Loose recovery. The two "no-grab" controls then run the same rig
// through paths that must NOT fire it (a cleared tipoff catch; a real made
// basket that the server collapses InFlight->Held atomically with cleared:true),
// asserting "ReboundGrab" never appears on ANY frame — so their pass proves the
// discriminator (was-Loose AND !IsCleared), not a harness that could never fire.
//
// ── Why Player.tscn instances (a live AnimationTree) + a live ball ──────────
// A live AnimationTree only exists on the REAL scenes/Player.tscn (bare
// `new PlayerController()` has no mesh/tree — Travel() would no-op). The ball is
// a real BallController so the tipoff, the SeedLooseBallForHarness scramble, and
// the made shot all run their genuine possession transitions.
//
// ── Out of scope ────────────────────────────────────────────────────────────
// Whether the catch clip LOOKS right is #173's deferred human feel judgment
// (ADR-0021) — this harness only asserts state-machine reachability, never clip
// content. It also does not re-prove the clip's track binding (that is
// LocomotionClipTest's job / the retarget commit's reload check).
public partial class ReboundGrabTest : Node
{
    private const double TimeoutSeconds = 12.0;
    private const int ArmFrames = 2;            // ticks for TryAssignTipoffHolder to run
    private const int SettleMarginFrames = 8;   // ticks to keep watching after a key event

    // The resource-duration acceptance permits one physics tick of serialization
    // precision. The assertion must follow the LIVE engine rate, not bake the
    // current 60 Hz project default into a second source.
    private const int DurationToleranceTicks = 1;

    // "Materially differs" is a silhouette claim, not merely a non-identical
    // float claim. Fifteen degrees is the same conservative upper-body floor
    // used by the #296 phase-read harnesses: well above interpolation noise,
    // but comfortably below an actual catch/reach motion.
    private const float EarlyVsLatePoseMinDeg = 15.0f;
    private const float EarlySampleFraction = 0.15f;
    private const float LateSampleFraction = 0.85f;

    // A spectator reads the catch through the torso and reaching arms. Every
    // one must resolve; silently dropping a track cannot be made to look like
    // an unchanged pose by substituting Quaternion.Identity.
    private static readonly string[] ReboundReadBones =
    {
        "mixamorig_Spine",
        "mixamorig_LeftArm", "mixamorig_RightArm",
        "mixamorig_LeftForeArm", "mixamorig_RightForeArm",
    };

    // Comfortably in-bounds resting spot for the seeded loose ball, and the
    // rebounder sits on the same XZ so ReboundContest (XZ distance) awards it.
    private static readonly Vector3 ReboundSpot = new(0f, 0.12f, 0f);
    private static readonly Vector3 FarSpot = new(12f, 0f, 12f); // keeps the other player out of PickupRadius

    // Made-basket geometry (mirrors BlockTurnoverTest's guaranteed clean make):
    // shooter well clear of the rim XZ, scatter disabled, aim == rim.
    private static readonly Vector3 ShooterPosition = new(0f, 0f, 5f);

    private string _scenario = "grab-fires";

    private BallController _ball;
    private GameManager _gameManager;
    private PlayerController _p1;
    private PlayerController _p2;

    private int _frame;
    private double _elapsed;
    private bool _finished;

    private int _holderId;

    private enum Step { AwaitTipoff, Act, Observe, SecondSeed, ObserveSecond }
    private Step _step = Step.AwaitTipoff;
    private int _stepDeadlineFrame;

    // Latched (event-time) observations.
    private bool _sawReboundGrab;        // grab observed after the (first) rebound
    private bool _sawSettleAfterGrab;    // returned to Locomotion after the grab
    private bool _sawSecondReboundGrab;  // grab re-fired after a second rebound
    private bool _sawGapBetween;         // ReboundGrab cleared before the second rebound
    private bool _sawInFlight;           // made-basket control: the shot really flew
    private bool _sawMadeAward;          // made-basket control: cleared make-it-take-it award landed

    // #295 runtime duration proof. The count is taken from the state-machine
    // node while a REAL Loose -> Held recovery drives the production latch, not
    // inferred from Animation.Length. A clip can be perfectly retimed yet still
    // be visibly cut short by an off-by-one latch decrement.
    private bool _durationSawGrab;
    private int _durationDisplayFrames;
    private int _durationExpectedFrames;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "grab-fires");
        GD.Print($"[rebound-grab] scenario={_scenario} booting headless…");

        // Real Player.tscn instances (live AnimationTree), named "1"/"2" so the
        // OfflineMultiplayerPeer makes unique_id 1 both IsServer and
        // IsLocalPlayer (the full TickServerOwnPlayer -> ApplyAnimation chain
        // runs every tick), same as MoveKindAnimTest/PivotAnimTest.
        var scene = GD.Load<PackedScene>("res://scenes/Player.tscn");
        _p1 = scene.Instantiate<PlayerController>();
        _p1.Name = "1";
        _p2 = scene.Instantiate<PlayerController>();
        _p2.Name = "2";

        // This static contract deliberately starts at the resource the LIVE
        // Player.tscn state machine consumes, rather than a copied duration or
        // an editor-only asset. `catch` is the established ReboundGrab binding;
        // #295 retimes that exact one-shot rather than renaming the state graph.
        if (_scenario == "rebound-clip-contract")
        {
            RunReboundClipContract(mutateDurationAuthority: false);
            return;
        }
        if (_scenario == "rebound-latch-mutation-control")
        {
            RunReboundClipContract(mutateDurationAuthority: true);
            return;
        }

        // Physics-callback lockstep so GetCurrentNode() reflects the same-tick
        // Travel() (default Idle callback lags under --headless — see
        // MoveKindAnimTest's long note; harness-only observation fidelity).
        foreach (var p in new[] { _p1, _p2 })
            p.GetNode<AnimationTree>("AnimationTree").CallbackModeProcess =
                AnimationMixer.AnimationCallbackModeProcess.Physics;

        var players = new Node3D { Name = "Players" };
        players.AddChild(_p1);
        players.AddChild(_p2);

        _ball = new BallController { Name = "Ball", Players = players };

        // Made-basket control needs a guaranteed clean make: disable distance
        // scatter and pin the board geometry, exactly as BlockTurnoverTest's
        // control-make does (aim == rim == a certain make when unblocked).
        if (_scenario == "made-basket-no-grab")
        {
            _ball.ShotScatterEnabled = false;
            _ball.BoardCenter = new Vector3(0f, 3.205f, -0.27f);
        }

        AddChild(players); // matches scenes/Main.tscn: Players before Ball
        AddChild(_ball);

        // A GameManager is required only for the made basket (RegisterBasket).
        if (_scenario == "made-basket-no-grab")
        {
            _gameManager = new GameManager { Name = "GameManager" };
            AddChild(_gameManager);
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        _frame++;

        switch (_scenario)
        {
            case "grab-fires":             TickGrabFires();            break;
            case "made-basket-no-grab":    TickMadeBasketNoGrab();     break;
            case "consecutive-rebounds":   TickConsecutiveRebounds();  break;
            case "rebound-display-duration": TickReboundDisplayDuration(); break;
            default:
                Fail($"unknown scenario '{_scenario}'.");
                Finish();
                return;
        }

        if (!_finished && _elapsed > TimeoutSeconds)
        {
            Fail($"timed out at frame {_frame}, scenario={_scenario}, step={_step}, " +
                 $"sawReboundGrab={_sawReboundGrab}, sawSettle={_sawSettleAfterGrab}, " +
                 $"lastAnimNode={_p1?.ActiveAnimNodeForHarness}.");
            Finish();
        }
    }

    // Recover-and-observe helper shared by grab-fires and consecutive-rebounds.
    private bool AwaitTipoffThenPositionForRebound()
    {
        if (_frame < ArmFrames) return false;
        if (_ball.StateMachine.HolderPeerId == 0)
        {
            Fail($"{_scenario}: tipoff never assigned a holder.");
            Finish();
            return false;
        }
        _holderId = _ball.StateMachine.HolderPeerId;
        // Put the tipoff holder ON the rebound spot and the other player far
        // away, so the loose-ball scramble deterministically awards the holder.
        NodeForPeer(_holderId).GlobalPosition = ReboundSpot;
        OtherNode(_holderId).GlobalPosition = FarSpot;
        return true;
    }

    // ── Scenario: grab-fires (positive) ─────────────────────────────────────
    private void TickGrabFires()
    {
        switch (_step)
        {
            case Step.AwaitTipoff:
                if (!AwaitTipoffThenPositionForRebound()) return;
                _step = Step.Act;
                _stepDeadlineFrame = _frame + 2; // let the position take on the ball
                return;

            case Step.Act:
                if (_frame < _stepDeadlineFrame) return;
                // Stage a live loose ball at the rebounder — the next TickLoose
                // runs the real scramble and awards it back (cleared:false).
                _ball.SeedLooseBallForHarness(ReboundSpot);
                _step = Step.Observe;
                return;

            case Step.Observe:
                ObserveGrabThenSettle(VerdictGrabFires);
                return;
        }
    }

    private void VerdictGrabFires()
    {
        bool pass = _sawReboundGrab && _sawSettleAfterGrab;
        if (pass)
            GD.Print("[rebound-grab] PASS grab-fires — the tree entered \"ReboundGrab\" after the " +
                     "live rebound, then settled back onto \"Locomotion\" once the latch expired.");
        else
            Fail($"grab-fires: expected the tree to enter \"ReboundGrab\" after the rebound and settle " +
                 $"to \"Locomotion\"; got sawReboundGrab={_sawReboundGrab}, sawSettle={_sawSettleAfterGrab}, " +
                 $"lastAnimNode={_p1.ActiveAnimNodeForHarness}.");
        Finish(pass ? 0 : 1);
    }

    // Watches the holder's live anim node: latches ReboundGrab, then the return
    // to Locomotion after it, then renders the verdict a margin later.
    private void ObserveGrabThenSettle(System.Action verdict)
    {
        string animNode = NodeForPeer(_holderId).ActiveAnimNodeForHarness;
        if (animNode == "ReboundGrab")
            _sawReboundGrab = true;
        if (_sawReboundGrab && animNode == "Locomotion" && !_sawSettleAfterGrab)
        {
            _sawSettleAfterGrab = true;
            _stepDeadlineFrame = _frame + SettleMarginFrames;
        }
        if (_sawSettleAfterGrab && _frame >= _stepDeadlineFrame)
            verdict();
    }

    // ── Scenario: made-basket-no-grab (control; also covers clean-catch) ─────
    // The tipoff itself is a clean, cleared catch — asserting ReboundGrab never
    // appears from tipoff THROUGH a real made basket covers BOTH named controls
    // (clean pass catch, made-basket inbound) in one honest run.
    private void TickMadeBasketNoGrab()
    {
        // Control assertion enforced EVERY frame: the grab must never appear.
        if (_p1.ActiveAnimNodeForHarness == "ReboundGrab" ||
            _p2.ActiveAnimNodeForHarness == "ReboundGrab")
        {
            Fail($"made-basket-no-grab: \"ReboundGrab\" appeared at frame {_frame} — it must never " +
                 "fire on a clean-catch tipoff or a made-basket inbound (cleared possession).");
            Finish();
            return;
        }

        if (_ball.State == BallState.InFlight) _sawInFlight = true;

        switch (_step)
        {
            case Step.AwaitTipoff:
                if (_frame < ArmFrames) return;
                if (_ball.StateMachine.HolderPeerId == 0)
                {
                    Fail("made-basket-no-grab: tipoff never assigned a holder.");
                    Finish();
                    return;
                }
                _holderId = _ball.StateMachine.HolderPeerId;
                if (!_ball.IsCleared)
                {
                    Fail("made-basket-no-grab: the tipoff possession is not cleared — a make would not count.");
                    Finish();
                    return;
                }
                NodeForPeer(_holderId).GlobalPosition = ShooterPosition;
                OtherNode(_holderId).GlobalPosition = FarSpot;
                _step = Step.Act;
                _stepDeadlineFrame = _frame + 2;
                return;

            case Step.Act:
                if (_frame < _stepDeadlineFrame) return;
                if (!NodeForPeer(_holderId).BeginJumpShotForHarness())
                {
                    Fail("made-basket-no-grab: BeginJumpShotForHarness returned false.");
                    Finish();
                    return;
                }
                _step = Step.Observe;
                return;

            case Step.Observe:
                // The make-it-take-it award lands the ball back as a CLEARED
                // Held possession on the scorer — the observable signature of a
                // counting basket (vs a miss, which would be cleared:false).
                if (_sawInFlight && _ball.State == BallState.Held &&
                    _ball.StateMachine.HolderPeerId == _holderId && _ball.IsCleared)
                {
                    if (!_sawMadeAward)
                    {
                        _sawMadeAward = true;
                        _stepDeadlineFrame = _frame + SettleMarginFrames;
                    }
                }
                if (_sawMadeAward && _frame >= _stepDeadlineFrame)
                    VerdictMadeBasketNoGrab();
                return;
        }
    }

    private void VerdictMadeBasketNoGrab()
    {
        // Non-vacuous: the make MUST actually have happened, else "no grab" is
        // meaningless (a miss would legitimately produce a grab).
        bool pass = _sawInFlight && _sawMadeAward; // and, implicitly, no grab ever fired (guarded each frame)
        if (pass)
            GD.Print("[rebound-grab] PASS made-basket-no-grab — a real cleared make ran (InFlight -> " +
                     "make-it-take-it) and \"ReboundGrab\" never appeared on any frame, for either player.");
        else
            Fail($"made-basket-no-grab: the control never observed a genuine made basket " +
                 $"(sawInFlight={_sawInFlight}, sawMadeAward={_sawMadeAward}) — the 'no grab' result would " +
                 "be vacuous, so this fails rather than pass on a shot that never made.");
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: consecutive-rebounds (re-trigger acceptance) ──────────────
    private void TickConsecutiveRebounds()
    {
        switch (_step)
        {
            case Step.AwaitTipoff:
                if (!AwaitTipoffThenPositionForRebound()) return;
                _step = Step.Act;
                _stepDeadlineFrame = _frame + 2;
                return;

            case Step.Act:
                if (_frame < _stepDeadlineFrame) return;
                _ball.SeedLooseBallForHarness(ReboundSpot);
                _step = Step.Observe;
                return;

            case Step.Observe:
                // First grab + settle back to Locomotion (the gap that proves
                // the second grab is a genuine re-trigger, not a still-latched
                // first one).
                {
                    string animNode = NodeForPeer(_holderId).ActiveAnimNodeForHarness;
                    if (animNode == "ReboundGrab")
                        _sawReboundGrab = true;
                    if (_sawReboundGrab && animNode == "Locomotion")
                    {
                        _sawGapBetween = true;
                        _step = Step.SecondSeed;
                    }
                }
                return;

            case Step.SecondSeed:
                // Re-stage a second live rebound after the first flourish ended.
                NodeForPeer(_holderId).GlobalPosition = ReboundSpot;
                OtherNode(_holderId).GlobalPosition = FarSpot;
                _ball.SeedLooseBallForHarness(ReboundSpot);
                _step = Step.ObserveSecond;
                _stepDeadlineFrame = _frame + SettleMarginFrames;
                return;

            case Step.ObserveSecond:
                if (NodeForPeer(_holderId).ActiveAnimNodeForHarness == "ReboundGrab")
                    _sawSecondReboundGrab = true;
                // Give it a bounded window to re-fire.
                if (_frame >= _stepDeadlineFrame + 30)
                    VerdictConsecutiveRebounds();
                else if (_sawSecondReboundGrab)
                    VerdictConsecutiveRebounds();
                return;
        }
    }

    private void VerdictConsecutiveRebounds()
    {
        bool pass = _sawReboundGrab && _sawGapBetween && _sawSecondReboundGrab;
        if (pass)
            GD.Print("[rebound-grab] PASS consecutive-rebounds — \"ReboundGrab\" fired on the first " +
                     "rebound, cleared to \"Locomotion\", then re-fired on a second rebound (the latch " +
                     "re-arms per live recovery).");
        else
            Fail($"consecutive-rebounds: expected two distinct grabs separated by a Locomotion gap; " +
                 $"got firstGrab={_sawReboundGrab}, gap={_sawGapBetween}, secondGrab={_sawSecondReboundGrab}.");
        Finish(pass ? 0 : 1);
    }

    // ── Issue #295: the live ReboundGrab state -> clip contract ────────────
    // Reads the actual AnimationTree tree_root authored in Player.tscn, then
    // follows its ReboundGrab AnimationNodeAnimation binding into the actual
    // locomotion AnimationLibrary. A state-name reachability test alone cannot
    // detect a state that still says ReboundGrab but plays a stale/wrong clip.
    private void RunReboundClipContract(bool mutateDurationAuthority)
    {
        AnimationNodeStateMachine stateMachine = LoadPlayerStateMachine();
        if (stateMachine == null || !stateMachine.HasNode("ReboundGrab"))
        {
            Fail("rebound-clip-contract: scenes/Player.tscn has no ReboundGrab state on its AnimationTree.");
            Finish(1);
            return;
        }

        var node = stateMachine.GetNode("ReboundGrab") as AnimationNodeAnimation;
        if (node == null)
        {
            Fail("rebound-clip-contract: Player.tscn's ReboundGrab state is not an AnimationNodeAnimation.");
            Finish(1);
            return;
        }

        string boundClip = node.Animation;
        const string ExpectedBinding = "locomotion/catch";
        bool pass = true;
        GD.Print($"[rebound-grab] ReboundGrab -> '{boundClip}' (expected '{ExpectedBinding}')");
        if (boundClip != ExpectedBinding)
        {
            Fail($"rebound-clip-contract: Player.tscn binds ReboundGrab to '{boundClip}', not '{ExpectedBinding}'. " +
                 "#295 retimes the established catch one-shot; do not silently point this state at another clip.");
            pass = false;
        }

        var animationPlayer = _p1.GetNodeOrNull<AnimationPlayer>("AnimationPlayer");
        if (animationPlayer == null || !animationPlayer.HasAnimation(boundClip))
        {
            Fail($"rebound-clip-contract: Player.tscn's AnimationPlayer has no '{boundClip}' animation.");
            Finish(1);
            return;
        }

        // Resolve through the instantiated Player.tscn AnimationPlayer, not a
        // separately loaded resource path: this proves the state binding and
        // its locomotion-library mapping reach the exact clip measured below.
        Animation clip = animationPlayer.GetAnimation(boundClip);
        int originalTicks = _p1.ReboundGrabDisplayTicks;
        if (mutateDurationAuthority)
        {
            // The control changes the SAME live exported authority the normal
            // scenario reads. Two ticks is deliberately beyond the one-tick
            // allowance, so the comparison MUST turn red if it is meaningful.
            _p1.ReboundGrabDisplayTicks = originalTicks + DurationToleranceTicks + 1;
            GD.Print($"[rebound-grab] mutation control: changed this PlayerController instance's " +
                     $"ReboundGrabDisplayTicks {originalTicks} -> {_p1.ReboundGrabDisplayTicks}.");
        }
        int expectedTicks = _p1.ReboundGrabDisplayTicks;
        double tps = Engine.PhysicsTicksPerSecond;
        if (expectedTicks <= 0 || tps <= 0)
        {
            Fail($"rebound-clip-contract: invalid live duration authority: ReboundGrabDisplayTicks={expectedTicks}, " +
                 $"Engine.PhysicsTicksPerSecond={tps}.");
            Finish(1);
            return;
        }

        double expectedSeconds = expectedTicks / tps;
        // Variant avoids the Godot 4.6/4.7 Animation.Length ABI mismatch that
        // otherwise crashes a 4.7.1-built harness under an older local binary.
        double actualSeconds = clip.Get("length").AsDouble();
        double deviationSeconds = Math.Abs(actualSeconds - expectedSeconds);
        double toleranceSeconds = DurationToleranceTicks / tps;
        GD.Print($"[rebound-grab] catch length={actualSeconds:F6}s expected={expectedSeconds:F6}s " +
                 $"({expectedTicks} live latch ticks @ {tps} tps), deviation={deviationSeconds:F6}s " +
                 $"(tolerance <= {DurationToleranceTicks} tick = {toleranceSeconds:F6}s)");
        bool durationComparisonPass = deviationSeconds <= toleranceSeconds;
        if (!durationComparisonPass && !mutateDurationAuthority)
        {
            Fail($"rebound-clip-contract: '{boundClip}' is {actualSeconds:F6}s, but the real PlayerController " +
                 $"instance's ReboundGrabDisplayTicks requires {expectedSeconds:F6}s; deviation " +
                 $"{deviationSeconds:F6}s exceeds one tick ({toleranceSeconds:F6}s). Run " +
                 "tools/rebuild_rebound_grab_clip.gd after retuning the latch.");
            pass = false;
        }

        GD.Print($"[rebound-grab] catch loop_mode={clip.LoopMode}");
        if (clip.LoopMode != Animation.LoopModeEnum.None)
        {
            Fail($"rebound-clip-contract: '{boundClip}' has loop_mode={clip.LoopMode}, expected LOOP_NONE. " +
                 "A rebound grab is a one-shot and must not replay inside the display latch.");
            pass = false;
        }

        if (!MeasureEarlyLatePoseDifference(boundClip, actualSeconds, out float maximumBoneDelta, out string poseDetail))
        {
            Fail($"rebound-clip-contract: could not measure the live Player.tscn rig at 15% vs 85%: {poseDetail}");
            pass = false;
        }
        else
        {
            GD.Print($"[rebound-grab] early-vs-late live-bone deltas: {poseDetail}; " +
                     $"maximum={maximumBoneDelta:F2} deg (floor {EarlyVsLatePoseMinDeg:F1})");
            if (maximumBoneDelta < EarlyVsLatePoseMinDeg)
            {
                Fail($"rebound-clip-contract: the 15%-vs-85% live-rig pose delta is only " +
                     $"{maximumBoneDelta:F2} deg across the required readable bones (need >= " +
                     $"{EarlyVsLatePoseMinDeg:F1}). A still/near-still catch is not a material rebound read.");
                pass = false;
            }
        }

        if (mutateDurationAuthority)
        {
            // Restore before exiting even though each harness scenario owns a
            // fresh process: this remains a tightly-scoped counterfactual over
            // one live node rather than a lingering global test mutation.
            _p1.ReboundGrabDisplayTicks = originalTicks;
            bool controlPass = pass && !durationComparisonPass;
            if (controlPass)
                GD.Print("[rebound-grab] PASS rebound-latch-mutation-control — changing the live exported " +
                         "ReboundGrabDisplayTicks made the SAME clip-duration comparison fail.");
            else
                Fail("rebound-latch-mutation-control: mutating the live exported duration authority did not " +
                     "make the clip-duration comparison fail; the positive duration contract is vacuous.");
            Finish(controlPass ? 0 : 1);
            return;
        }

        if (pass)
            GD.Print("[rebound-grab] PASS rebound-clip-contract — Player.tscn's live ReboundGrab binding " +
                     "loads the non-looping catch clip at the real latch duration; every required bone resolves and " +
                     "the live readable upper-body set changes materially.");
        else
            GD.PrintErr("[rebound-grab] FAIL rebound-clip-contract — see diagnostics above.");
        Finish(pass ? 0 : 1);
    }

    // A real loose-ball recovery shows ReboundGrab for precisely the same number
    // of physics frames as the live controller's latch. This is independent of
    // the resource duration check above: it catches a correctly retimed clip
    // that production still cuts short with an off-by-one countdown.
    private void TickReboundDisplayDuration()
    {
        switch (_step)
        {
            case Step.AwaitTipoff:
                if (!AwaitTipoffThenPositionForRebound()) return;
                _durationExpectedFrames = NodeForPeer(_holderId).ReboundGrabDisplayTicks;
                if (_durationExpectedFrames <= 0)
                {
                    Fail($"{_scenario}: live PlayerController.ReboundGrabDisplayTicks must be positive; got " +
                         $"{_durationExpectedFrames}.");
                    Finish();
                    return;
                }
                _step = Step.Act;
                _stepDeadlineFrame = _frame + 2;
                return;

            case Step.Act:
                if (_frame < _stepDeadlineFrame) return;
                _ball.SeedLooseBallForHarness(ReboundSpot);
                _step = Step.Observe;
                return;

            case Step.Observe:
                PlayerController holder = NodeForPeer(_holderId);
                if (holder.ActiveAnimNodeForHarness == "ReboundGrab")
                {
                    _durationSawGrab = true;
                    _durationDisplayFrames++;

                    return;
                }

                if (!_durationSawGrab) return;

                int deviationTicks = Math.Abs(_durationDisplayFrames - _durationExpectedFrames);
                bool sameDurationComparisonPass = deviationTicks == 0;
                GD.Print($"[rebound-grab] {_scenario}: visible ReboundGrab frames={_durationDisplayFrames}, " +
                         $"live ReboundGrabDisplayTicks={_durationExpectedFrames}, deviation={deviationTicks} ticks " +
                         "(must be exact) => comparison " +
                         $"{(sameDurationComparisonPass ? "PASS" : "FAIL")}");

                if (sameDurationComparisonPass)
                    GD.Print("[rebound-grab] PASS rebound-display-duration — the live one-shot is not cut " +
                             "short before its retimed clip reaches the latch endpoint.");
                else
                    Fail("rebound-display-duration: the real live-rebound display window differs from the " +
                         "same PlayerController instance's ReboundGrabDisplayTicks.");
                Finish(sameDurationComparisonPass ? 0 : 1);
                return;
        }
    }

    private bool MeasureEarlyLatePoseDifference(string boundClip, double lengthSeconds,
        out float maximumBoneDelta, out string detail)
    {
        maximumBoneDelta = float.NaN;
        detail = string.Empty;
        if (lengthSeconds <= 0.0)
        {
            detail = $"bound clip '{boundClip}' has non-positive length {lengthSeconds:F6}s";
            return false;
        }

        var animationPlayer = _p1.GetNodeOrNull<AnimationPlayer>("AnimationPlayer");
        Skeleton3D skeleton = FindSkeleton(_p1);
        if (animationPlayer == null || skeleton == null)
        {
            detail = $"live Player.tscn is missing {(animationPlayer == null ? "AnimationPlayer" : "Skeleton3D")}";
            return false;
        }

        // Disable the tree only while directly sampling its OWN AnimationPlayer.
        // This avoids its normal Locomotion travel overwriting the probe pose;
        // the binding itself was read above from the same tree_root resource.
        var tree = _p1.GetNodeOrNull<AnimationTree>("AnimationTree");
        if (tree != null) tree.Active = false;
        animationPlayer.Play(boundClip);

        double earlyTime = lengthSeconds * EarlySampleFraction;
        double lateTime = lengthSeconds * LateSampleFraction;
        animationPlayer.Seek(earlyTime, update: true);
        Quaternion[] early = SampleRequiredBonePoses(skeleton, out string earlyMissing);
        animationPlayer.Seek(lateTime, update: true);
        Quaternion[] late = SampleRequiredBonePoses(skeleton, out string lateMissing);

        if (tree != null) tree.Active = true;
        if (early == null || late == null)
        {
            detail = early == null ? earlyMissing : lateMissing;
            return false;
        }

        var values = new string[ReboundReadBones.Length];
        maximumBoneDelta = 0f;
        for (int i = 0; i < ReboundReadBones.Length; i++)
        {
            float delta = Mathf.RadToDeg(early[i].AngleTo(late[i]));
            maximumBoneDelta = Math.Max(maximumBoneDelta, delta);
            values[i] = $"{ReboundReadBones[i]}={delta:F2}°";
        }
        detail = string.Join(", ", values);
        return true;
    }

    private static Quaternion[] SampleRequiredBonePoses(Skeleton3D skeleton, out string missing)
    {
        var poses = new Quaternion[ReboundReadBones.Length];
        for (int i = 0; i < ReboundReadBones.Length; i++)
        {
            int bone = skeleton.FindBone(ReboundReadBones[i]);
            if (bone < 0)
            {
                missing = $"live Player.tscn Skeleton3D has no required bone '{ReboundReadBones[i]}'";
                return null;
            }
            poses[i] = skeleton.GetBonePose(bone).Basis.GetRotationQuaternion().Normalized();
        }
        missing = string.Empty;
        return poses;
    }

    private static Skeleton3D FindSkeleton(Node root)
    {
        if (root is Skeleton3D skeleton) return skeleton;
        foreach (Node child in root.GetChildren())
        {
            Skeleton3D found = FindSkeleton(child);
            if (found != null) return found;
        }
        return null;
    }

    private static AnimationNodeStateMachine LoadPlayerStateMachine()
    {
        var playerScene = GD.Load<PackedScene>("res://scenes/Player.tscn");
        if (playerScene == null) return null;
        var sceneState = playerScene.GetState();
        for (int i = 0; i < sceneState.GetNodeCount(); i++)
        {
            if (sceneState.GetNodeType(i) != "AnimationTree") continue;
            for (int p = 0; p < sceneState.GetNodePropertyCount(i); p++)
            {
                if (sceneState.GetNodePropertyName(i, p) == "tree_root")
                    return sceneState.GetNodePropertyValue(i, p).As<AnimationNodeStateMachine>();
            }
        }
        return null;
    }

    private PlayerController NodeForPeer(int peerId) => peerId == 1 ? _p1 : _p2;
    private PlayerController OtherNode(int peerId) => peerId == 1 ? _p2 : _p1;

    private void Fail(string message) => GD.PrintErr($"[rebound-grab] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[rebound-grab] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
