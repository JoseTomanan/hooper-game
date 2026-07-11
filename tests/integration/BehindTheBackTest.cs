using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #194 (ADR-0016): BehindTheBack's
// three harness-checkable acceptance criteria that CrossoverBurstMathExitConeTests
// / BehindTheBackTests (xUnit, pure math/class) cannot reach — the LIVE glue
// through a real PlayerController + BallController.
//
//   godot --headless --path . res://tests/integration/BehindTheBackTest.tscn -- --harness-scenario=shielded-sweep
//   godot --headless --path . res://tests/integration/BehindTheBackTest.tscn -- --harness-scenario=narrower-exit-cone
//   godot --headless --path . res://tests/integration/BehindTheBackTest.tscn -- --harness-scenario=dead-dribble-gate
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//   Omitting --harness-scenario defaults to "shielded-sweep".
//
// ── Scenario "shielded-sweep": the behind-body ball transit ───────────────
// Mirrors CrossoverSweepTest's "crossover-sweep" no-teleport proof, but on
// the FORWARD axis instead of lateral, and asserting BehindTheBack pulls the
// ball BEHIND the holder (a negative forward offset) rather than merely
// dipping it (CrossoverBallSweep's existing vertical dip, shared by both
// moves, is not the discriminator here — BallController.SweepIsBehindBodyForHarness
// and the forward-offset sign are).
//
// ── Scenario "narrower-exit-cone": reachability through REAL input ────────
// Drives the REAL SampleMoveInput -> RequestBeginMove -> TickCommittedMoveBehavior
// path (no harness seam) with a PURE LATERAL left-stick exit vector — the
// exact input CrossoverBurstMathTests.Stationary_PushLateral pins as
// producing Z==0 (no forward component) for a plain Crossover. BehindTheBack's
// narrower cone must bend that SAME input into a real forward component,
// proving the move_size_up modifier + "behindtheback" RPC dispatch + the
// narrower-cone parameter are actually wired end-to-end, not just correct in
// isolation (CrossoverBurstMathExitConeTests already proves the pure math).
//
// ── Scenario "dead-dribble-gate": #193's Held-holder gate applies here too ──
// Mirrors TripleThreatTest's "dead-Held-crossover-refused" step for
// BehindTheBack specifically — the locked design call that BehindTheBack
// "must respect the same Held-holder gate Crossover/Hesitation use."
public partial class BehindTheBackTest : Node
{
    private const double TimeoutSeconds = 10.0;
    private const int ArmFrames = 2;
    private const int ActionMarginFrames = 3;
    private const int SweepObservationWindow = 30;
    private const float FloorClipEpsilon = 0.001f;

    // RightStickGestureRecognizer commits on the 5th consecutive
    // above-threshold tick (FeintWindowFrames default 4) — same constant
    // MovingCrossoverTest uses.
    private const int FlickHoldTicks = 7;

    private string _scenario = "shielded-sweep";

    private BallController _ball;
    private PlayerController _p1;
    private PlayerController _p2;

    private int _frame;
    private double _elapsed;
    private bool _finished;

    // ── "shielded-sweep" state ───────────────────────────────────────────────
    private enum SweepStep { AwaitTipoff, DriveChecked, BeforeCaptured, AwaitFlip, ObservingSweep }
    private SweepStep _sweepStep = SweepStep.AwaitTipoff;
    private int _holderId;
    private float _forwardOffsetBefore;
    private HandSide _handSideBefore;
    private int _observeDeadlineFrame;
    private float _mostNegativeForwardOffset = float.PositiveInfinity;
    private bool _sawBehindBodySweep;

    // ── "narrower-exit-cone" state ───────────────────────────────────────────
    private bool _flickStarted;
    private int _flickStartFrame = -1;
    private bool _pushedLateralExit;
    private bool _sawStartup;
    private bool _sawActive;
    private float _maxForwardDuringActive = float.MinValue;
    private float _maxLateralAbsDuringActive = float.MinValue;

    // ── "dead-dribble-gate" state ────────────────────────────────────────────
    private enum GateStep { AwaitTipoff, Attempted }
    private GateStep _gateStep = GateStep.AwaitTipoff;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "shielded-sweep");
        GD.Print($"[behind-the-back] scenario={_scenario} booting headless…");

        if (_scenario == "narrower-exit-cone")
        {
            // No Ball wrapper needed — mirrors MovingCrossoverTest, which
            // proved this same "no Ball node" pattern makes the dead-dribble
            // gate a no-op (GetBall() returns null), isolating the burst math
            // from possession state entirely.
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
            case "narrower-exit-cone": TickNarrowerExitCone(); break;
            case "dead-dribble-gate": TickDeadDribbleGate(); break;
            default: TickShieldedSweep(); break;
        }

        if (!_finished && _elapsed > TimeoutSeconds)
        {
            Fail($"timed out at frame {_frame}, scenario={_scenario}.");
            Finish();
        }
    }

    // ── Scenario: "shielded-sweep" ────────────────────────────────────────────
    private void TickShieldedSweep()
    {
        switch (_sweepStep)
        {
            case SweepStep.AwaitTipoff:
                if (_frame < ArmFrames) break;
                if (_ball.StateMachine.HolderPeerId == 0)
                {
                    Fail("tipoff never assigned a holder.");
                    Finish();
                    return;
                }
                _holderId = _ball.StateMachine.HolderPeerId;
                _ball.TryStartDribble(_holderId);
                _sweepStep = SweepStep.DriveChecked;
                _observeDeadlineFrame = _frame + ActionMarginFrames;
                break;

            case SweepStep.DriveChecked:
                if (_frame < _observeDeadlineFrame) break;
                if (_ball.State != BallState.Dribbling)
                {
                    Fail($"expected TryStartDribble to reach Dribbling (BehindTheBack cannot Begin from Held, #193); got state={_ball.State}.");
                    Finish();
                    return;
                }

                PlayerController holderForBefore = NodeForPeer(_holderId);
                _handSideBefore = holderForBefore.HandSide;
                _forwardOffsetBefore = ForwardOffset(_holderId);
                GD.Print($"[behind-the-back] before: hand={_handSideBefore}, forwardOffset={_forwardOffsetBefore:F4}");

                bool began = holderForBefore.BeginBehindTheBackForHarness(1f);
                if (!began)
                {
                    Fail("BeginBehindTheBackForHarness returned false — machine was not Inactive.");
                    Finish();
                    return;
                }
                _sweepStep = SweepStep.AwaitFlip;
                break;

            case SweepStep.AwaitFlip:
                if (!AssertAboveFloor()) return;
                PlayerController holderForFlip = NodeForPeer(_holderId);
                if (holderForFlip.HandSide == _handSideBefore) break; // still in Startup

                GD.Print($"[behind-the-back] flip observed at frame {_frame}: {_handSideBefore} -> {holderForFlip.HandSide}");
                _mostNegativeForwardOffset = ForwardOffset(_holderId);
                _sawBehindBodySweep = _ball.SweepIsBehindBodyForHarness;
                _sweepStep = SweepStep.ObservingSweep;
                _observeDeadlineFrame = _frame + SweepObservationWindow;
                break;

            case SweepStep.ObservingSweep:
                if (!AssertAboveFloor()) return;
                float forwardOffset = ForwardOffset(_holderId);
                _mostNegativeForwardOffset = System.Math.Min(_mostNegativeForwardOffset, forwardOffset);
                _sawBehindBodySweep |= _ball.SweepIsBehindBodyForHarness;

                if (_frame < _observeDeadlineFrame) break;

                // Acceptance: the sweep must have been flagged behind-body at
                // least once, and the ball's forward offset must have gone
                // NEGATIVE at some point — genuinely behind the holder's
                // centerline (BehindTheBackSweepDepth=0.7 > DribbleForwardOffset=0.5
                // by default), not merely a smaller positive offset the way a
                // Crossover's shared vertical-dip curve alone would produce.
                bool wentBehindBody = _sawBehindBodySweep && _mostNegativeForwardOffset < 0f;
                if (!wentBehindBody)
                {
                    Fail($"expected the sweep to pull the ball BEHIND the holder (forwardOffset < 0); sawBehindBodySweep={_sawBehindBodySweep}, mostNegativeForwardOffset={_mostNegativeForwardOffset:F4}.");
                    Finish();
                    return;
                }
                GD.Print($"[behind-the-back] PASS shielded-sweep — mostNegativeForwardOffset={_mostNegativeForwardOffset:F4} < 0, behind-body sweep observed.");

                PlayerController holderAfter = NodeForPeer(_holderId);
                float forwardOffsetAfter = ForwardOffset(_holderId);
                bool settledInFront = forwardOffsetAfter > 0f && holderAfter.HandSide != _handSideBefore;
                if (!settledInFront)
                {
                    Fail($"expected the sweep to finish back IN FRONT (forwardOffset > 0) on the new hand; got hand={holderAfter.HandSide}, forwardOffset={forwardOffsetAfter:F4}.");
                    Finish();
                    return;
                }
                GD.Print($"[behind-the-back] PASS sweep-completes — settled on {holderAfter.HandSide} at forwardOffset={forwardOffsetAfter:F4}.");
                GD.Print("[behind-the-back] RESULT: PASS (exit 0)");
                Finish(0);
                return;
        }
    }

    // ── Scenario: "narrower-exit-cone" ───────────────────────────────────────
    private void TickNarrowerExitCone()
    {
        if (_frame == 1)
        {
            // Held throughout — SampleMoveInput reads this at the SAME tick
            // the flick commits, selecting BehindTheBack over Crossover.
            Input.ActionPress("move_size_up", 1.0f);
            _flickStarted = true;
            _flickStartFrame = _frame;
            Input.ActionPress("aim_right", 1.0f); // flickSign +1 (crossover-classified vs. default HandSide.Left)
            GD.Print($"[behind-the-back] frame {_frame}: pressed move_size_up + aim_right (player stationary)");
        }

        if (_flickStarted && _frame == _flickStartFrame + FlickHoldTicks)
        {
            Input.ActionRelease("aim_right");
            GD.Print($"[behind-the-back] frame {_frame}: released aim_right (BehindTheBack should have committed)");
        }

        var (phase, _) = _p1.DisplayMove();

        // Push a PURE LATERAL exit vector once Startup is confirmed underway
        // — move_left maps to world (-1,0) (wishDir = (inputDir.X, 0, inputDir.Y)),
        // exactly the "player's-right push" CrossoverBurstMathTests.
        // Stationary_PushLateral pins as producing Z==0 for a plain Crossover
        // (Heading stays 0 throughout a committed move, so world (-1,0) IS the
        // rightAxis-aligned pure-lateral input at flickSign +1).
        if (phase == MovePhase.Startup && !_pushedLateralExit)
        {
            _pushedLateralExit = true;
            Input.ActionPress("move_left", 1.0f);
            GD.Print($"[behind-the-back] frame {_frame}: Startup confirmed, pushed a pure-lateral exit vector");
        }

        if (phase == MovePhase.Active)
        {
            _sawActive = true;
            if (_p1.Velocity.Z > _maxForwardDuringActive) _maxForwardDuringActive = _p1.Velocity.Z;
            if (Mathf.Abs(_p1.Velocity.X) > _maxLateralAbsDuringActive) _maxLateralAbsDuringActive = Mathf.Abs(_p1.Velocity.X);
        }
        else if (phase == MovePhase.Startup)
        {
            _sawStartup = true;
        }

        if (_sawStartup && _sawActive && phase == MovePhase.Recovery)
        {
            // Discriminator against the pre-#194 pure-lateral model (and
            // against a plain Crossover under this SAME input): a nonzero
            // forward component proves the narrower cone actually bent the
            // lateral-only exit forward, end-to-end through the real
            // SampleMoveInput -> RequestBeginMove("behindtheback") -> Active
            // path — not just in CrossoverBurstMathExitConeTests' isolated math.
            const float ForwardEpsilon = 0.5f;
            bool pass = _maxForwardDuringActive > ForwardEpsilon && _maxLateralAbsDuringActive > 0f;
            if (pass)
            {
                GD.Print($"[behind-the-back] PASS narrower-exit-cone — maxForwardDuringActive={_maxForwardDuringActive:F3}, maxLateralAbsDuringActive={_maxLateralAbsDuringActive:F3}.");
                GD.Print("[behind-the-back] RESULT: PASS (exit 0)");
                Finish(0);
            }
            else
            {
                Fail($"expected a forward component > {ForwardEpsilon} from a pure-lateral exit (proving the narrower cone bent it forward); got maxForwardDuringActive={_maxForwardDuringActive:F3}, maxLateralAbsDuringActive={_maxLateralAbsDuringActive:F3}.");
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
                // no drive, no dribble. Attempting BehindTheBack here must be
                // refused by the SAME Held-holder gate Crossover/Hesitation use
                // (PlayerController.BeginCommittedMove).
                if (_ball.State != BallState.Held)
                {
                    Fail($"expected a fresh tipoff to land the holder in Held; got state={_ball.State}.");
                    Finish();
                    return;
                }

                PlayerController holder = NodeForPeer(_ball.StateMachine.HolderPeerId);
                bool began = holder.BeginBehindTheBackForHarness(1f);
                bool stayedInactive = holder.MachinePhaseForHarness == MovePhase.Inactive;

                if (began || !stayedInactive)
                {
                    Fail($"expected BeginBehindTheBackForHarness to be REFUSED from a dead-Held possession; began={began}, phase={holder.MachinePhaseForHarness}.");
                    Finish();
                    return;
                }
                GD.Print("[behind-the-back] PASS dead-dribble-gate — BehindTheBack refused from a dead-Held possession, machine stayed Inactive.");
                GD.Print("[behind-the-back] RESULT: PASS (exit 0)");
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

    private void Fail(string message) => GD.PrintErr($"[behind-the-back] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        if (code != 0)
            GD.Print($"[behind-the-back] RESULT: FAIL (exit {code})");
        GetTree().Quit(code);
    }
}
