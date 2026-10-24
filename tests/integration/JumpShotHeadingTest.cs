using System;
using Godot;
using Hooper.Player;
using Hooper.Moves;
using Hooper.Ball;

namespace HOOPERGAME.Tests.Integration;

/// <summary>Production scene heading tracer bullet for #428.</summary>
public partial class JumpShotHeadingTest : Node
{
    private PlayerController _actor;
    private BallController _ball;
    private int _tick;
    private float _before;
    public override void _Ready()
    {
        var players = new Node3D { Name = "Players" };
        _actor = GD.Load<PackedScene>("res://scenes/Player.tscn").Instantiate<PlayerController>();
        _actor.Name = "1";
        players.AddChild(_actor);
        AddChild(players);
        _ball = new BallController { Name = "Ball", Players = players, RimCenter = new Vector3(0, 3.05f, 0) };
        AddChild(_ball);
        _actor.Position = new Vector3(0, 0, 5);
    }
    public override void _PhysicsProcess(double delta)
    {
        _tick++;
        if (_tick == 5)
        {
            _actor.SetHeadingForHarness(0);
            _before = _actor.Heading;
            if (!_actor.BeginJumpShotForHarness()) throw new Exception("Begin failed");
        }
        if (_tick == 6)
        {
            float step = MathF.Abs(Mathf.AngleDifference(_before, _actor.Heading));
            bool pass = _actor.PhaseForHarness == MovePhase.Startup && step > .01f
                && step <= _actor.MaxTurnRateDeg / 60f * MathF.PI / 180f + 1e-6f;
            GD.Print($"[shot-heading] {(pass ? "PASS" : "FAIL")} startup bounded step={step}");
            GetTree().Quit(pass ? 0 : 1);
        }
    }
}
