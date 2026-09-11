using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Player;
using Hooper.Systems;

namespace HOOPERGAME.Tests.Integration;

// Issue #373 regression harness. The two terminal scenarios reach game over
// through a real cleared JumpShot, then hold a 60-tick observation window over
// the exact recovery/OOB award paths suppressed by the production fix. The
// three non-terminal controls prove those same AwardPossession paths remain
// live. Exit 0=PASS, 1=FAIL (ADR-0016).
public partial class TerminalNoHolderTest : Node
{
    private const double TimeoutSeconds = 12.0;
    private const int ArmFrames = 2;
    private const int ObservationTicks = 60;

    private static readonly Vector3 ShooterPosition = new(0f, 0f, 5f);
    private static readonly Vector3 FarPosition = new(-6f, 0f, 10f);
    private static readonly Vector3 ReboundPosition = new(2f, 0f, 2f);

    private string _scenario = "terminal-rebound";
    private PlayerController _p1;
    private PlayerController _p2;
    private BallController _ball;
    private GameManager _gameManager;

    private int _frame;
    private double _elapsed;
    private bool _finished;
    private bool _armed;
    private bool _actionStarted;

    private int _terminalFrame = -1;
    private int _snapshotsAtTerminal;
    private int _awardsAtTerminal;
    private Vector3 _previousPosition;
    private Vector3 _previousVelocity;
    private int _integrationChangeSamples;

    private bool IsTerminal => _scenario == "terminal-rebound" || _scenario == "terminal-oob";
    private bool IsNaturalMake => IsTerminal || _scenario == "nonwinning-make";

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "terminal-rebound");
        GD.Print($"[terminal-no-holder] scenario={_scenario} booting headless...");

        if (_scenario != "terminal-rebound" && _scenario != "terminal-oob" &&
            _scenario != "nonwinning-make" && _scenario != "live-rebound" &&
            _scenario != "oob-award")
        {
            Fail($"unknown scenario '{_scenario}'.");
            Finish();
            return;
        }

        // Peer-id names are assigned before AddChild because holder lookup and
        // authority checks key on Node.Name.
        var players = new Node3D { Name = "Players" };
        _p1 = new PlayerController { Name = "1" };
        _p2 = new PlayerController { Name = "2" };
        players.AddChild(_p1);
        players.AddChild(_p2);

        _ball = new BallController
        {
            Name = "Ball",
            Players = players,
            ShotScatterEnabled = false,
            RimCenter = new Vector3(0f, 3.05f, 0f),
            ShotTarget = new Vector3(0f, 3.05f, 0f),
            BoardCenter = new Vector3(0f, 3.205f, -0.27f),
        };

        _gameManager = new GameManager
        {
            Name = "GameManager",
            TargetScore = IsTerminal ? 1 : 2,
        };

        // Assemble the world off-tree, then ready GameManager before the players.
        // Main.tscn's manager is already live when NetworkManager spawns players,
        // so this preserves normal game-manager discovery in the code-built fixture.
        var world = new Node { Name = "World" };
        world.AddChild(_gameManager);
        world.AddChild(players);
        world.AddChild(_ball);
        AddChild(world);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        _frame++;

        if (!_armed)
        {
            if (_frame < ArmFrames) return;
            if (!AssertTipoffPremise()) return;
            _armed = true;
        }

        if (!_actionStarted)
        {
            StartScenarioAction();
            if (_finished) return;
            _actionStarted = true;
        }

        if (IsTerminal)
            TickTerminal();
        else if (_scenario == "nonwinning-make")
            TickNonwinningMake();
        else if (_scenario == "live-rebound")
            TickLiveRebound();
        else
            TickOobAward();

        if (!_finished && _elapsed > TimeoutSeconds)
        {
            Fail($"timed out at frame {_frame}: state={_ball.State}, holder={_ball.StateMachine.HolderPeerId}, " +
                 $"score1={_gameManager.ScoreOf(1)}, winner={_gameManager.WinnerPeerId}, " +
                 $"gameOver={_gameManager.IsGameOver}, snapshots={_ball.AuthoritativeSnapshotBroadcastCountForHarness}, " +
                 $"awards={_ball.SuccessfulPossessionAwardCountForHarness}.");
            Finish();
        }
    }

    private bool AssertTipoffPremise()
    {
        bool pass = _ball.State == BallState.Held &&
                    _ball.StateMachine.HolderPeerId == 1 &&
                    _ball.IsCleared &&
                    _ball.LastToucherPeerIdForHarness == 1 &&
                    _ball.SuccessfulPossessionAwardCountForHarness == 0;
        if (pass) return true;

        Fail($"tipoff premise failed: expected peer 1 Held+cleared, lastToucher=1, awards=0; got " +
             $"state={_ball.State}, holder={_ball.StateMachine.HolderPeerId}, cleared={_ball.IsCleared}, " +
             $"lastToucher={_ball.LastToucherPeerIdForHarness}, awards={_ball.SuccessfulPossessionAwardCountForHarness}.");
        Finish();
        return false;
    }

    private void StartScenarioAction()
    {
        if (IsNaturalMake)
        {
            _p1.GlobalPosition = ShooterPosition;
            _p2.GlobalPosition = FarPosition;
            if (!_p1.BeginJumpShotForHarness())
            {
                Fail("BeginJumpShotForHarness returned false from the tipoff Held state.");
                Finish();
            }
            return;
        }

        if (_scenario == "live-rebound")
        {
            _p1.GlobalPosition = FarPosition;
            _p2.GlobalPosition = ReboundPosition;
            Vector3 seed = new(ReboundPosition.X, _ball.BallRadius, ReboundPosition.Z);
            float p1Distance = XzDistance(_p1.GlobalPosition, seed);
            float p2Distance = XzDistance(_p2.GlobalPosition, seed);
            if (p1Distance <= _ball.PickupRadius || p2Distance >= _ball.PickupRadius)
            {
                Fail($"live-rebound candidate premise failed: peer1 distance {p1Distance:F3} must be > " +
                     $"PickupRadius {_ball.PickupRadius:F3}, peer2 distance {p2Distance:F3} must be < it.");
                Finish();
                return;
            }
            _ball.SeedLooseBallForHarness(seed);
            return;
        }

        _p1.GlobalPosition = FarPosition;
        _p2.GlobalPosition = new Vector3(-FarPosition.X, 0f, FarPosition.Z);
        Vector3 clamped = CourtBounds.Clamp(ExplicitOobPosition(), _ball.CourtMin, _ball.CourtMax);
        if (XzDistance(_p1.GlobalPosition, clamped) <= _ball.PickupRadius ||
            XzDistance(_p2.GlobalPosition, clamped) <= _ball.PickupRadius)
        {
            Fail("oob-award control premise failed: a player is within rebound range of the clamped fallback point.");
            Finish();
            return;
        }
        _ball.SeedLooseBallForHarness(ExplicitOobPosition());
    }

    private void TickTerminal()
    {
        if (_terminalFrame < 0)
        {
            if (!_gameManager.IsGameOver) return;

            bool naturalWinner = _gameManager.ScoreOf(1) == 1 &&
                                 _gameManager.ScoreOf(2) == 0 &&
                                 _gameManager.WinnerPeerId == 1 &&
                                 _ball.State == BallState.Loose &&
                                 _ball.StateMachine.HolderPeerId == 0;
            if (!naturalWinner)
            {
                Fail($"terminal event premise failed: expected a natural 1-0 peer-1 win with Loose holder=0; got " +
                     $"score={_gameManager.ScoreOf(1)}-{_gameManager.ScoreOf(2)}, winner={_gameManager.WinnerPeerId}, " +
                     $"state={_ball.State}, holder={_ball.StateMachine.HolderPeerId}.");
                Finish();
                return;
            }

            _terminalFrame = _frame;
            _snapshotsAtTerminal = _ball.AuthoritativeSnapshotBroadcastCountForHarness;
            _awardsAtTerminal = _ball.SuccessfulPossessionAwardCountForHarness;
            if (_awardsAtTerminal != 0)
            {
                Fail($"terminal event already crossed AwardPossession before observation began: awards={_awardsAtTerminal}.");
                Finish();
                return;
            }

            if (_scenario == "terminal-rebound")
            {
                if (CourtBounds.IsOutOfBounds(_ball.GlobalPosition, _ball.CourtMin, _ball.CourtMax))
                {
                    Fail($"terminal-rebound premise failed: winning ball is already OOB at {_ball.GlobalPosition}.");
                    Finish();
                    return;
                }
                _p2.GlobalPosition = new Vector3(_ball.GlobalPosition.X, 0f, _ball.GlobalPosition.Z);
                _previousPosition = _ball.GlobalPosition;
                _previousVelocity = _ball.VelocityForHarness;
            }
            else
            {
                // Seed only the live loose-ball trajectory. Score, winner and
                // holder remain the naturally-produced terminal values above.
                _p1.GlobalPosition = FarPosition;
                _p2.GlobalPosition = new Vector3(-FarPosition.X, 0f, FarPosition.Z);
                Vector3 clamped = CourtBounds.Clamp(ExplicitOobPosition(), _ball.CourtMin, _ball.CourtMax);
                if (XzDistance(_p1.GlobalPosition, clamped) <= _ball.PickupRadius ||
                    XzDistance(_p2.GlobalPosition, clamped) <= _ball.PickupRadius)
                {
                    Fail("terminal-oob premise failed: a player is within rebound range of the clamped fallback point.");
                    Finish();
                    return;
                }
                _ball.SeedLooseBallForHarness(ExplicitOobPosition());
            }
        }

        // A point sample could miss a transient award followed by another
        // transition. Enforce holder=0 every tick AND an unchanged successful-
        // award counter over the full post-win interval.
        if (_ball.StateMachine.HolderPeerId != 0)
        {
            Fail($"post-win holder appeared at observation tick {_frame - _terminalFrame}: holder={_ball.StateMachine.HolderPeerId}.");
            Finish();
            return;
        }
        if (_ball.SuccessfulPossessionAwardCountForHarness != _awardsAtTerminal)
        {
            Fail($"post-win AwardPossession succeeded at observation tick {_frame - _terminalFrame}: " +
                 $"baseline={_awardsAtTerminal}, now={_ball.SuccessfulPossessionAwardCountForHarness}.");
            Finish();
            return;
        }

        if (_scenario == "terminal-rebound" && _frame > _terminalFrame)
        {
            if (CourtBounds.IsOutOfBounds(_ball.GlobalPosition, _ball.CourtMin, _ball.CourtMax))
            {
                Fail($"terminal-rebound left the court during its recovery-only window at {_ball.GlobalPosition}; " +
                     "the OOB path would confound the recovery-guard mutation.");
                Finish();
                return;
            }
            Vector3 position = _ball.GlobalPosition;
            Vector3 velocity = _ball.VelocityForHarness;
            if (!position.IsEqualApprox(_previousPosition) || !velocity.IsEqualApprox(_previousVelocity))
                _integrationChangeSamples++;
            _previousPosition = position;
            _previousVelocity = velocity;
        }

        int observedTicks = _frame - _terminalFrame;
        if (observedTicks < ObservationTicks) return;

        int snapshotDelta = _ball.AuthoritativeSnapshotBroadcastCountForHarness - _snapshotsAtTerminal;
        bool exactSnapshotCadence = snapshotDelta == ObservationTicks;
        bool integrationContinued = _scenario != "terminal-rebound" || _integrationChangeSamples >= 10;
        bool pass = exactSnapshotCadence && integrationContinued;
        if (pass)
        {
            GD.Print($"[terminal-no-holder] PASS scenario={_scenario}: window={ObservationTicks}, holder=0 throughout, " +
                     $"awardDelta=0, snapshotDelta={snapshotDelta}, integrationChangeSamples={_integrationChangeSamples}.");
        }
        else
        {
            Fail($"terminal window evidence failed: expected exactly {ObservationTicks} snapshot-site executions and " +
                 $"terminal-rebound >=10 changing integration samples; got snapshotDelta={snapshotDelta}, " +
                 $"integrationChangeSamples={_integrationChangeSamples}.");
        }
        Finish(pass ? 0 : 1);
    }

    private void TickNonwinningMake()
    {
        if (_gameManager.ScoreOf(1) == 0) return;
        bool pass = _gameManager.ScoreOf(1) == 1 && _gameManager.ScoreOf(2) == 0 &&
                    !_gameManager.IsGameOver && _gameManager.WinnerPeerId == 0 &&
                    _ball.State == BallState.Held && _ball.StateMachine.HolderPeerId == 1 &&
                    _ball.IsCleared && _ball.SuccessfulPossessionAwardCountForHarness == 1;
        VerdictControl(pass, "nonwinning-make", "peer 1 score=1, no winner/game-over, peer 1 Held+cleared, awards=1");
    }

    private void TickLiveRebound()
    {
        if (_ball.SuccessfulPossessionAwardCountForHarness == 0) return;
        bool pass = _ball.SuccessfulPossessionAwardCountForHarness == 1 &&
                    _ball.State == BallState.Held && _ball.StateMachine.HolderPeerId == 2 && !_ball.IsCleared;
        VerdictControl(pass, "live-rebound", "peer 2 Held+uncleared via one successful award");
    }

    private void TickOobAward()
    {
        if (_ball.SuccessfulPossessionAwardCountForHarness == 0) return;
        bool pass = _ball.SuccessfulPossessionAwardCountForHarness == 1 &&
                    _ball.LastToucherPeerIdForHarness == 2 &&
                    _ball.State == BallState.Held && _ball.StateMachine.HolderPeerId == 2 && !_ball.IsCleared;
        VerdictControl(pass, "oob-award", "tipoff toucher peer 1 produced peer 2 Held+uncleared via one successful award");
    }

    private Vector3 ExplicitOobPosition() =>
        new(_ball.CourtMax.X + 0.5f, _ball.BallRadius, 0f);

    private static float XzDistance(Vector3 a, Vector3 b) =>
        new Vector2(a.X - b.X, a.Z - b.Z).Length();

    private void VerdictControl(bool pass, string name, string expected)
    {
        if (pass)
            GD.Print($"[terminal-no-holder] PASS scenario={name}: {expected}.");
        else
            Fail($"{name} expected {expected}; got score={_gameManager.ScoreOf(1)}-{_gameManager.ScoreOf(2)}, " +
                 $"winner={_gameManager.WinnerPeerId}, gameOver={_gameManager.IsGameOver}, state={_ball.State}, " +
                 $"holder={_ball.StateMachine.HolderPeerId}, cleared={_ball.IsCleared}, " +
                 $"lastToucher={_ball.LastToucherPeerIdForHarness}, awards={_ball.SuccessfulPossessionAwardCountForHarness}.");
        Finish(pass ? 0 : 1);
    }

    private void Fail(string message) => GD.PrintErr($"[terminal-no-holder] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[terminal-no-holder] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
