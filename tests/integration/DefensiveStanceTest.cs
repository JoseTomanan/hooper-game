using System;
using Godot;
using Hooper.Ball;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

public partial class DefensiveStanceTest : Node3D
{
    private PlayerController _defender, _attacker;
    private BallController _ball;
    private int _ticks;
    private double Dt => 1.0 / Engine.PhysicsTicksPerSecond;
    public override void _Ready()
    {
        var players = new Node3D { Name = "Players" }; AddChild(players);
        _defender = ResourceLoader.Load<PackedScene>("res://scenes/Player.tscn").Instantiate<PlayerController>();
        _defender.Name = "1"; players.AddChild(_defender);
        _attacker = ResourceLoader.Load<PackedScene>("res://scenes/Player.tscn").Instantiate<PlayerController>();
        _attacker.Name = "2"; players.AddChild(_attacker);
        foreach (var p in new[] { _defender, _attacker })
        { p.SetPhysicsProcess(false); p.SetProcess(false); p.GetNode<AnimationTree>("AnimationTree").Active = false; }
        _ball = new BallController { Name = "Ball", Players = players }; AddChild(_ball);
        _ball.SetPhysicsProcess(false);
        _ball.StateMachine.ForceState(BallState.Dribbling, 2);
        _defender.SprintBindBallForHarness(_ball); _attacker.SprintBindBallForHarness(_ball);
        _defender.ResetContactStateForHarness(new Vector3(0, 0, 5), Mathf.Pi / 2, Vector3.Zero);
        _attacker.ResetContactStateForHarness(new Vector3(0, 0, 10), 0, Vector3.Zero);
        Input.ActionPress("move_protect"); Input.ActionPress("move_left");
    }
    public override void _PhysicsProcess(double delta)
    {
        try
        {
            _ticks++;
            float before = _defender.Heading;
            _defender.SprintTickHostForHarness(Dt);
            if (Math.Abs(Mathf.AngleDifference(before, _defender.Heading)) > 360f * Mathf.Pi / 180f * (float)Dt + .00001f)
                throw new InvalidOperationException("instant/unbounded stance heading");
            if (_ticks < 30) return;
            if (!_defender.IsInDefensiveStance || _defender.GlobalPosition.X > -.5f || _defender.IsPivotingInPlace)
                throw new InvalidOperationException("held left slide did not translate world-left in effective stance");
            var target = _attacker.GlobalPosition - _defender.GlobalPosition;
            if (Math.Abs(Mathf.AngleDifference(_defender.Heading, MathF.Atan2(target.X, target.Z))) > .04f)
                throw new InvalidOperationException("stance did not face attacker");
            GD.Print("[stance] PASS slides"); GetTree().Quit(0);
        }
        catch (Exception e) { GD.Print("[stance] FAIL " + e.Message); GetTree().Quit(1); }
    }
}
