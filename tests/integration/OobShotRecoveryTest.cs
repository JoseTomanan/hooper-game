using System;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;
using Hooper.Systems;

namespace HOOPERGAME.Tests.Integration;

public partial class OobShotRecoveryTest : Node
{
    private const int ReturnTicks = 60;
    private const int OutwardControlTicks = 12;
    private PlayerController _shooter;
    private PlayerController _opponent;
    private BallController _ball;
    private GameManager _game;
    private string _scenario;
    private int _tick, _firstOutsideTick, _begin, _recoveryStart, _awards;
    private Vector3 _returnOrigin;
    private bool _shotPressed, _active, _recovery, _finished;

    public override void _Ready()
    {
        // Sample after both production physics callbacks.
        // https://docs.godotengine.org/en/4.7/classes/class_node.html#class-node-property-process-physics-priority
        ProcessPhysicsPriority = 100;
        _scenario = HarnessArgs.ReadArg(OS.GetCmdlineUserArgs(), "--harness-scenario", "solo");
        if (_scenario is not ("solo" or "both-outside" or "turnover" or "legal-shot" or "pump-fake"))
            throw new ArgumentException($"unknown scenario {_scenario}");
        var players = new Node3D { Name = "Players" };
        var playerScene = GD.Load<PackedScene>("res://scenes/Player.tscn");
        _ball = GD.Load<PackedScene>("res://scenes/Ball.tscn").Instantiate<BallController>();
        _ball.Players = players;
        _shooter = playerScene.Instantiate<PlayerController>();
        _shooter.Name = "1";
        _shooter.Position = new Vector3(StartsOutside ? _ball.CourtMax.X - .12f : 0f, 0, 5);
        players.AddChild(_shooter);
        if (_scenario is "both-outside" or "turnover")
        {
            _opponent = playerScene.Instantiate<PlayerController>();
            _opponent.Name = "2";
            _opponent.Position = new Vector3(_scenario == "both-outside" ? _ball.CourtMin.X - .5f : -4f, 0, 5);
            players.AddChild(_opponent);
        }
        _game = new GameManager { Name = "GameManager" };
        AddChild(_game);
        AddChild(players);
        AddChild(_ball);
        _ball.PossessionChanged += (holder, _) => { if (holder == 2) _awards++; };
    }

    private bool StartsOutside => _scenario is "solo" or "both-outside";

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        try
        {
            _tick++;
            Require(_tick < 180 && !_game.IsGameOver && !GetTree().Paused, "physics or game stopped before verdict");
            if (_scenario != "legal-shot")
                Require(_ball.SuccessfulShotReleasesForHarness == 0 && _game.ScoreOf(1) == 0 && _game.ScoreOf(2) == 0,
                    "voided or faked shot released or scored");
            if (StartsOutside)
                Require(_ball.StateMachine.HolderPeerId == 1 && _awards == 0, "possession changed without an eligible recipient");
            if (_scenario == "both-outside")
                Require(CourtBounds.IsOutOfBounds(_opponent.GlobalPosition, _ball.CourtMin, _ball.CourtMax),
                    "the opponent crossed in bounds during the both-outside fixture");
            if (_tick == 2)
            {
                Require(_ball.State == BallState.Held && !_ball.HasDribbled, "expected fresh tipoff");
                // Drive the Input singleton read by the actual player tick.
                // https://docs.godotengine.org/en/4.7/classes/class_input.html#class-input-method-action-press
                Input.ActionPress("move_right");
            }
            if (!_shotPressed && (StartsOutside ? IsOutside() : _tick >= 12))
            {
                Require(_ball.State == BallState.Dribbling && !_ball.HasDribbled && IsOutside() == StartsOutside,
                    "expected live dribble on the designated side of the line");
                if (_firstOutsideTick == 0) _firstOutsideTick = _tick;
                if (StartsOutside && _tick - _firstOutsideTick < 4) return;
                Input.ActionRelease("move_right");
                Input.ActionPress(_ball.ShootAction);
                _shotPressed = true;
                return;
            }
            if (!_shotPressed) return;
            Input.ActionRelease(_ball.ShootAction);
            Input.ActionRelease("move_feint");
            var phase = _shooter.PhaseForHarness;
            if (_begin == 0)
            {
                Require(phase == MovePhase.Startup && _shooter.CurrentMoveIdForHarness == "jumpshot" && IsOutside() == StartsOutside,
                    "shot input did not begin a jump shot on the designated side of the line");
                _begin = _tick - _shooter.FrameInPhaseForHarness;
            }
            if (StartsOutside || _scenario == "pump-fake" || _tick - _begin < 5)
                Require(_ball.State == BallState.Held && _ball.HasDribbled, "gathered possession lost its dead-dribble cost");
            if (_tick - _begin == 5)
            {
                if (_scenario == "pump-fake") Input.ActionPress("move_feint");
                if (_scenario == "turnover")
                    _shooter.GlobalPosition = new Vector3(_ball.CourtMax.X + .5f, 0, 5);
            }
            if (_scenario == "turnover" && _tick - _begin > 5)
                Require(_ball.StateMachine.HolderPeerId == 2 && _awards == 1
                    && _ball.State == BallState.Held && !_ball.HasDribbled && !_ball.IsCleared,
                    "expected exactly one fresh, uncleared possession for the in-bounds opponent");
            _active |= phase == MovePhase.Active;
            _recovery |= phase == MovePhase.Recovery;
            if (phase != MovePhase.Inactive) return;
            if (_scenario == "pump-fake")
            {
                Require(!_active && _recovery && !IsOutside(), "in-bounds fake did not enter Recovery directly");
                if (_recoveryStart == 0)
                {
                    _recoveryStart = _tick;
                    _returnOrigin = _shooter.GlobalPosition;
                    Input.ActionPress("move_left");
                }
                if (_tick - _recoveryStart < ReturnTicks) return;
                Require(_shooter.GlobalPosition.DistanceTo(_returnOrigin) < .001f, "in-bounds pump-fake allowed neutral walking");
                Finish(true, "legal pump-fake retained a planted, spent hold");
                return;
            }
            if (_recoveryStart == 0)
            {
                var frames = JumpShot.DefaultFrameData;
                Require(_active && _recovery && _tick - _begin == frames.StartupFrames + frames.ActiveFrames + frames.RecoveryFrames,
                    $"shot failed to complete the committed timeline: {_tick - _begin} ticks, Active={_active}, Recovery={_recovery}");
                if (_scenario == "legal-shot")
                {
                    Require(_ball.SuccessfulShotReleasesForHarness == 1, "in-bounds live-dribble shot did not release exactly once");
                    Finish(true, "in-bounds live-dribble shot gathered and released once");
                    return;
                }
                Require(IsOutside(), "shooter must still be outside before inward input");
                _returnOrigin = _shooter.GlobalPosition;
                _recoveryStart = _tick;
                if (StartsOutside)
                {
                    Input.ActionPress("move_right");
                    Input.ActionPress("move_backward");
                }
                else Input.ActionPress("move_left");
            }
            int ticksAfterRecovery = _tick - _recoveryStart;
            if (StartsOutside && ticksAfterRecovery == OutwardControlTicks)
            {
                Require(_shooter.GlobalPosition.DistanceTo(_returnOrigin) < .001f,
                    "a stranded holder could move away from or along the sideline");
                Input.ActionRelease("move_right");
                Input.ActionRelease("move_backward");
                Input.ActionPress("move_left");
            }
            int returnTicks = ticksAfterRecovery - (StartsOutside ? OutwardControlTicks : 0);
            if (returnTicks < ReturnTicks) return;
            float currentX = _shooter.GlobalPosition.X;
            Require(!IsOutside() && _returnOrigin.X - currentX >= .05f,
                $"inward input failed to return shooter to court: X {_returnOrigin.X:F3} -> {currentX:F3} after {ReturnTicks} ticks");
            if (StartsOutside)
                Require(_shooter.Velocity.LengthSquared() < .000001f, "continued inward input kept moving a spent holder inside");
            Finish(true, $"shot voided and shooter returned: X {_returnOrigin.X:F3} -> {currentX:F3}");
        }
        catch (Exception error) { Finish(false, error.Message); }
    }

    private bool IsOutside() => CourtBounds.IsOutOfBounds(_shooter.GlobalPosition, _ball.CourtMin, _ball.CourtMax);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private void Finish(bool pass, string message)
    {
        _finished = true;
        Input.ActionRelease("move_right");
        Input.ActionRelease("move_left");
        Input.ActionRelease("move_backward");
        Input.ActionRelease("move_feint");
        Input.ActionRelease(_ball.ShootAction);
        GD.Print($"[harness] {(pass ? "PASS" : "FAIL")} oob-shot {_scenario}: {message}");
        GetTree().Quit(pass ? 0 : 1);
    }
}
