using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #199 (ADR-0016): BetweenTheLegs's
// three harness-checkable acceptance criteria that CrossoverBallSweepTests /
// BetweenTheLegsTests (xUnit, pure math/class) cannot reach — the LIVE glue
// through a real PlayerController + BallController.
//
//   godot --headless --path . res://tests/integration/BetweenTheLegsTest.tscn -- --harness-scenario=through-legs-dip
//   godot --headless --path . res://tests/integration/BetweenTheLegsTest.tscn -- --harness-scenario=finesse-selects-betweenthelegs
//   godot --headless --path . res://tests/integration/BetweenTheLegsTest.tscn -- --harness-scenario=dead-dribble-gate
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//   Omitting --harness-scenario defaults to "through-legs-dip".
//
// ── Scenario "through-legs-dip": the through-the-legs ball transit ────────
// Mirrors BehindTheBackTest's "shielded-sweep" no-teleport proof, but the
// discriminator here is the OPPOSITE of BehindTheBack's: the ball's forward
// offset must STAY POSITIVE throughout (never dip behind the holder, unlike
// BehindTheBack) while its world-space Y drops close to the floor (the
// "bounces between the legs" identity, via BallController.BetweenTheLegsDipDepth)
// and BallController.SweepPathForHarness reports ThroughLegs — proving all
// three sweep paths (InFront/BehindBody/ThroughLegs) are genuinely
// distinguishable in the live engine, not just in CrossoverBallSweepTests'
// pure math.
//
// ── Scenario "finesse-selects-betweenthelegs": reachability through REAL input ──
// Drives the REAL SampleMoveInput -> RequestBeginMove -> TickCommittedMoveBehavior
// path (no harness seam) holding "move_finesse" during the crossover flick —
// the modifier that must select BetweenTheLegs over plain Crossover AND over
// BehindTheBack's "move_size_up" modifier. Asserts DisplayMoveId() reports
// "betweenthelegs" once Active begins, proving move_finesse + the
// "betweenthelegs" RPC dispatch are wired end-to-end.
//
// ── Scenario "dead-dribble-gate": #193's Held-holder gate applies here too ──
// Mirrors BehindTheBackTest's "dead-dribble-gate" step for BetweenTheLegs
// specifically — the locked design call that BetweenTheLegs "must respect
// the same Held-holder gate Crossover/Hesitation/BehindTheBack use" (it is
// ALSO a dribble move — the hand-swap and the bounce both need a live
// dribble to work from).
public partial class BetweenTheLegsTest : Node
{
    private const double TimeoutSeconds = 10.0;
    private const int ArmFrames = 2;
    private const int ActionMarginFrames = 3;
    private const int SweepObservationWindow = 30;
    private const float FloorClipEpsilon = 0.001f;

    // RightStickGestureRecognizer commits on the 5th consecutive
    // above-threshold tick (FeintWindowFrames default 4) — same constant
    // BehindTheBackTest/MovingCrossoverTest use.
    private const int FlickHoldTicks = 7;

    private string _scenario = "through-legs-dip";

    private BallController _ball;
    private PlayerController _p1;
    private PlayerController _p2;

    private int _frame;
    private double _elapsed;
    private bool _finished;

    // ── "through-legs-dip" state ─────────────────────────────────────────────
    private enum DipStep { AwaitTipoff, DriveChecked, BeforeCaptured, AwaitFlip, ObservingSweep }
    private DipStep _dipStep = DipStep.AwaitTipoff;
    private int _holderId;
    private HandSide _handSideBefore;
    private int _observeDeadlineFrame;
    private float _mostNegativeForwardOffset = float.PositiveInfinity;
    private float _lowestBallY = float.PositiveInfinity;
    private bool _sawThroughLegsPath;

    // ── "finesse-selects-betweenthelegs" state ───────────────────────────────
    private bool _flickStarted;
    private int _flickStartFrame = -1;
    private bool _sawStartup;
    private bool _sawBetweenTheLegsActive;
    private bool _sawWrongMoveActive;

    // ── "dead-dribble-gate" state ────────────────────────────────────────────
    private enum GateStep { AwaitTipoff, Attempted }
    private GateStep _gateStep = GateStep.AwaitTipoff;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "through-legs-dip");
        GD.Print($"[between-the-legs] scenario={_scenario} booting headless…");

        if (_scenario == "finesse-selects-betweenthelegs")
        {
            // No Ball wrapper needed — mirrors BehindTheBackTest's
            // "narrower-exit-cone" pattern, isolating input dispatch from
            // possession state entirely (GetBall() returns null).
            _p1 = new PlayerController { Name = "1" };
            AddChild(_p1);
            return;
        }

        var players = new Node3D { Name = "Players" };
        _p1 = new PlayerController { Name = "1" };
        _p2 = new PlayerController { Name = "2" };
        players.AddChild(_p1);
        players.AddChild(_p2);

        _ball = new BallController { Name = "Ball", Players = players };

        AddChild(players);
        AddChild(_ball);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        _frame++;

        switch (_scenario)
        {
            case "finesse-selects-betweenthelegs": TickFinesseSelectsBetweenTheLegs(); break;
            case "dead-dribble-gate": TickDeadDribbleGate(); break;
            default: TickThroughLegsDip(); break;
        }

        if (!_finished && _elapsed > TimeoutSeconds)
        {
            Fail($"timed out at frame {_frame}, scenario={_scenario}.");
            Finish();
        }
    }

    // ── Scenario: "through-legs-dip" ─────────────────────────────────────────
    private void TickThroughLegsDip()
    {
        switch (_dipStep)
        {
            case DipStep.AwaitTipoff:
                if (_frame < ArmFrames) break;
                if (_ball.StateMachine.HolderPeerId == 0)
                {
                    Fail("tipoff never assigned a holder.");
                    Finish();
                    return;
                }
                _holderId = _ball.StateMachine.HolderPeerId;
                _ball.TryStartDribble(_holderId);
                _dipStep = DipStep.DriveChecked;
                _observeDeadlineFrame = _frame + ActionMarginFrames;
                break;

            case DipStep.DriveChecked:
                if (_frame < _observeDeadlineFrame) break;
                if (_ball.State != BallState.Dribbling)
                {
                    Fail($"expected TryStartDribble to reach Dribbling (BetweenTheLegs cannot Begin from Held, #193); got state={_ball.State}.");
                    Finish();
                    return;
                }

                PlayerController holderForBefore = NodeForPeer(_holderId);
                _handSideBefore = holderForBefore.HandSide;
                GD.Print($"[between-the-legs] before: hand={_handSideBefore}");

                bool began = holderForBefore.BeginBetweenTheLegsForHarness(1f);
                if (!began)
                {
                    Fail("BeginBetweenTheLegsForHarness returned false — machine was not Inactive.");
                    Finish();
                    return;
                }
                _dipStep = DipStep.AwaitFlip;
                break;

            case DipStep.AwaitFlip:
                if (!AssertAboveFloor()) return;
                PlayerController holderForFlip = NodeForPeer(_holderId);
                if (holderForFlip.HandSide == _handSideBefore) break; // still in Startup

                GD.Print($"[between-the-legs] flip observed at frame {_frame}: {_handSideBefore} -> {holderForFlip.HandSide}");
                _mostNegativeForwardOffset = ForwardOffset(_holderId);
                _lowestBallY = _ball.GlobalPosition.Y;
                _sawThroughLegsPath = _ball.SweepPathForHarness == BallSweepPath.ThroughLegs;
                _dipStep = DipStep.ObservingSweep;
                _observeDeadlineFrame = _frame + SweepObservationWindow;
                break;

            case DipStep.ObservingSweep:
                if (!AssertAboveFloor()) return;
                float forwardOffset = ForwardOffset(_holderId);
                _mostNegativeForwardOffset = System.Math.Min(_mostNegativeForwardOffset, forwardOffset);
                _lowestBallY = System.Math.Min(_lowestBallY, _ball.GlobalPosition.Y);
                _sawThroughLegsPath |= _ball.SweepPathForHarness == BallSweepPath.ThroughLegs;

                if (_frame < _observeDeadlineFrame) break;

                // Acceptance: the sweep must have been flagged ThroughLegs at
                // least once, the ball must have dipped close to the floor
                // (BetweenTheLegsDipDepth=0.85 default: DribbleHandHeight 1.0
                // - 0.85 = 0.15, well below Crossover's shallow 0.15-depth
                // dip which only reaches 1.0-0.15*CrossoverSweepDipDepth's
                // OWN depth of 0.15 => 0.85 — i.e. a genuinely deeper dip
                // than Crossover's own), AND the forward offset must have
                // stayed POSITIVE throughout — the discriminator against
                // BehindTheBack's sweep, which goes negative instead.
                const float DeepDipThreshold = 0.5f; // well above CrossoverSweepDipDepth's own trough, well below hand height
                bool wentThroughLegs = _sawThroughLegsPath && _lowestBallY < DeepDipThreshold;
                if (!wentThroughLegs)
                {
                    Fail($"expected the sweep to dip toward the floor (Y < {DeepDipThreshold}); sawThroughLegsPath={_sawThroughLegsPath}, lowestBallY={_lowestBallY:F4}.");
                    Finish();
                    return;
                }
                if (_mostNegativeForwardOffset < 0f)
                {
                    Fail($"expected the ball to STAY in front (forwardOffset >= 0) throughout a through-the-legs sweep, unlike BehindTheBack; got mostNegativeForwardOffset={_mostNegativeForwardOffset:F4}.");
                    Finish();
                    return;
                }
                GD.Print($"[between-the-legs] PASS through-legs-dip — lowestBallY={_lowestBallY:F4} < {DeepDipThreshold}, forward offset stayed >= 0, ThroughLegs path observed.");

                PlayerController holderAfter = NodeForPeer(_holderId);
                float forwardOffsetAfter = ForwardOffset(_holderId);
                bool settledInFront = forwardOffsetAfter > 0f && holderAfter.HandSide != _handSideBefore;
                if (!settledInFront)
                {
                    Fail($"expected the sweep to finish back IN FRONT (forwardOffset > 0) on the new hand; got hand={holderAfter.HandSide}, forwardOffset={forwardOffsetAfter:F4}.");
                    Finish();
                    return;
                }
                GD.Print($"[between-the-legs] PASS sweep-completes — settled on {holderAfter.HandSide} at forwardOffset={forwardOffsetAfter:F4}.");
                GD.Print("[between-the-legs] RESULT: PASS (exit 0)");
                Finish(0);
                return;
        }
    }

    // ── Scenario: "finesse-selects-betweenthelegs" ───────────────────────────
    private void TickFinesseSelectsBetweenTheLegs()
    {
        if (_frame == 1)
        {
            // Held throughout — SampleMoveInput reads this at the SAME tick
            // the flick commits, selecting BetweenTheLegs over Crossover AND
            // over BehindTheBack (move_size_up is deliberately NOT pressed).
            Input.ActionPress("move_finesse", 1.0f);
            _flickStarted = true;
            _flickStartFrame = _frame;
            Input.ActionPress("aim_right", 1.0f); // flickSign +1 (crossover-classified vs. default HandSide.Left)
            GD.Print($"[between-the-legs] frame {_frame}: pressed move_finesse + aim_right (player stationary)");
        }

        if (_flickStarted && _frame == _flickStartFrame + FlickHoldTicks)
        {
            Input.ActionRelease("aim_right");
            GD.Print($"[between-the-legs] frame {_frame}: released aim_right (BetweenTheLegs should have committed)");
        }

        var (phase, _) = _p1.DisplayMove();
        string moveId = _p1.DisplayMoveId();

        if (phase == MovePhase.Startup)
        {
            _sawStartup = true;
        }
        else if (phase == MovePhase.Active)
        {
            if (moveId == "betweenthelegs") _sawBetweenTheLegsActive = true;
            else _sawWrongMoveActive = true;
        }

        if (_sawStartup && (_sawBetweenTheLegsActive || _sawWrongMoveActive))
        {
            if (_sawBetweenTheLegsActive && !_sawWrongMoveActive)
            {
                GD.Print("[between-the-legs] PASS finesse-selects-betweenthelegs — DisplayMoveId()==\"betweenthelegs\" during Active.");
                GD.Print("[between-the-legs] RESULT: PASS (exit 0)");
                Finish(0);
            }
            else
            {
                Fail($"expected move_finesse + flick to select BetweenTheLegs; sawBetweenTheLegsActive={_sawBetweenTheLegsActive}, sawWrongMoveActive={_sawWrongMoveActive} (moveId={moveId}).");
                Finish();
            }
        }
    }

    // ── Scenario: "dead-dribble-gate" ────────────────────────────────────────
    private void TickDeadDribbleGate()
    {
        switch (_gateStep)
        {
            case GateStep.AwaitTipoff:
                if (_frame < ArmFrames) break;
                if (_ball.StateMachine.HolderPeerId == 0)
                {
                    Fail("tipoff never assigned a holder.");
                    Finish();
                    return;
                }

                // Fresh tipoff already lands the holder in dead Held (#193) —
                // no drive, no dribble. Attempting BetweenTheLegs here must be
                // refused by the SAME Held-holder gate Crossover/Hesitation/
                // BehindTheBack use (PlayerController.BeginCommittedMove).
                if (_ball.State != BallState.Held)
                {
                    Fail($"expected a fresh tipoff to land the holder in Held; got state={_ball.State}.");
                    Finish();
                    return;
                }

                PlayerController holder = NodeForPeer(_ball.StateMachine.HolderPeerId);
                bool began = holder.BeginBetweenTheLegsForHarness(1f);
                bool stayedInactive = holder.MachinePhaseForHarness == MovePhase.Inactive;

                if (began || !stayedInactive)
                {
                    Fail($"expected BeginBetweenTheLegsForHarness to be REFUSED from a dead-Held possession; began={began}, phase={holder.MachinePhaseForHarness}.");
                    Finish();
                    return;
                }
                GD.Print("[between-the-legs] PASS dead-dribble-gate — BetweenTheLegs refused from a dead-Held possession, machine stayed Inactive.");
                GD.Print("[between-the-legs] RESULT: PASS (exit 0)");
                Finish(0);
                _gateStep = GateStep.Attempted;
                return;
        }
    }

    // World-space Z offset of the ball from the given peer's holder, along the
    // holder's forward axis. At this harness's default heading (0, facing +Z
    // per HeadingMath.Forward — the player never moves during a committed
    // move), forward = (0,1) on (X,Z), so this reduces to a plain Z subtraction.
    private float ForwardOffset(int peerId)
    {
        PlayerController holder = NodeForPeer(peerId);
        return _ball.GlobalPosition.Z - holder.GlobalPosition.Z;
    }

    private PlayerController NodeForPeer(int peerId) => peerId == 1 ? _p1 : _p2;

    private bool AssertAboveFloor()
    {
        if (_ball.GlobalPosition.Y >= _ball.BallRadius - FloorClipEpsilon) return true;

        Fail($"ball clipped under the floor mid-sweep at frame {_frame}: GlobalPosition.Y={_ball.GlobalPosition.Y:F4} < BallRadius={_ball.BallRadius:F4}.");
        Finish();
        return false;
    }

    private void Fail(string message) => GD.PrintErr($"[between-the-legs] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        if (code != 0)
            GD.Print($"[between-the-legs] RESULT: FAIL (exit {code})");
        GetTree().Quit(code);
    }
}
