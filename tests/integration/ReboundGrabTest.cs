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
