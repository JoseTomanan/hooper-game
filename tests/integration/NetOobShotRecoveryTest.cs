using System;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

/// <summary>Real ENet authority and predicting owner, each in an isolated physics world.</summary>
public partial class NetOobShotRecoveryTest : Node
{
    private SceneMultiplayer _clientApi;
    private SubViewport _hostWorld, _clientWorld;
    private PlayerController _authority, _owner;
    private BallController _hostBall, _clientBall;
    private int _ownerId, _tick, _returnTick, _ackBeforeReturn;
    private Vector3 _authorityOutside, _ownerOutside;
    private bool _shotPressed, _authorityCommitted, _ownerCommitted, _finished;

    public override void _Ready()
    {
        ProcessPhysicsPriority = 100;
        try
        {
            var hostPeer = new ENetMultiplayerPeer();
            var clientPeer = new ENetMultiplayerPeer();
            // Bind to loopback and have ENet choose an unused port for this fixture.
            // https://docs.godotengine.org/en/4.7/classes/class_enetmultiplayerpeer.html#class-enetmultiplayerpeer-method-create-server
            hostPeer.SetBindIP("127.0.0.1");
            Require(hostPeer.CreateServer(0, 1) == Error.Ok, "host bind failed");
            Require(clientPeer.CreateClient("127.0.0.1", hostPeer.Host.GetLocalPort()) == Error.Ok, "client startup failed");
            var hostApi = new SceneMultiplayer { MultiplayerPeer = hostPeer };
            _clientApi = new SceneMultiplayer { MultiplayerPeer = clientPeer };
            _hostWorld = CreateWorld("Host", hostApi);
            _clientWorld = CreateWorld("Client", _clientApi);
            _clientApi.ConnectedToServer += ConfigureCourts;
            _clientApi.ConnectionFailed += () => Finish(false, "ENet connection failed");
        }
        catch (Exception error) { Finish(false, error.Message); }
    }

    private SubViewport CreateWorld(string name, SceneMultiplayer api)
    {
        // https://docs.godotengine.org/en/4.7/classes/class_viewport.html#class-viewport-property-own-world-3d
        var world = new SubViewport { Name = name, OwnWorld3D = true };
        AddChild(world);
        // Assign the API before the production player and ball enter the tree.
        // https://docs.godotengine.org/en/4.7/classes/class_scenetree.html#class-scenetree-method-set-multiplayer
        GetTree().SetMultiplayer(api, world.GetPath());
        return world;
    }

    private void ConfigureCourts()
    {
        try
        {
            _ownerId = _clientApi.GetUniqueId();
            (_authority, _hostBall) = Populate(_hostWorld);
            (_owner, _clientBall) = Populate(_clientWorld);
        }
        catch (Exception error) { Finish(false, error.Message); }
    }

    private (PlayerController, BallController) Populate(SubViewport world)
    {
        var players = new Node3D { Name = "Players" };
        var ball = GD.Load<PackedScene>("res://scenes/Ball.tscn").Instantiate<BallController>();
        ball.Players = players;
        var player = GD.Load<PackedScene>("res://scenes/Player.tscn").Instantiate<PlayerController>();
        player.Name = _ownerId.ToString();
        player.Position = new Vector3(ball.CourtMax.X - .12f, 0, 5);
        player.SprintBindBallForHarness(ball);
        players.AddChild(player);
        world.AddChild(players);
        world.AddChild(ball);
        return (player, ball);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        try
        {
            Require(++_tick < 400, "timed out awaiting authoritative court re-entry");
            if (_owner == null) return;
            Require(_hostBall.SuccessfulShotReleasesForHarness == 0, "out-of-bounds shot released on the authority");
            if (!_shotPressed)
            {
                if (_clientBall.StateMachine.HolderPeerId != _ownerId) return;
                Input.ActionPress("move_right");
                if (_authority.GlobalPosition.X < _hostBall.CourtMax.X + .15f || _owner.GlobalPosition.X < _clientBall.CourtMax.X + .15f) return;
                Require(_hostBall.State == BallState.Dribbling && _clientBall.State == BallState.Dribbling, "both peers must see a live dribble");
                Input.ActionRelease("move_right");
                Input.ActionPress(_clientBall.ShootAction);
                _shotPressed = true;
                return;
            }

            Input.ActionRelease(_clientBall.ShootAction);
            _authorityCommitted |= _authority.PhaseForHarness == MovePhase.Active;
            _ownerCommitted |= _owner.PhaseForHarness == MovePhase.Active;
            if (!_authorityCommitted || !_ownerCommitted
                || _authority.PhaseForHarness != MovePhase.Inactive || _owner.PhaseForHarness != MovePhase.Inactive) return;
            Require(HasSpentHold(_hostBall), "authority lost the spent hold");
            // The existing client release prediction can briefly put the ball
            // in flight. Wait for its authoritative possession correction.
            if (_returnTick == 0 && !HasSpentHold(_clientBall)) return;
            Require(HasSpentHold(_clientBall), "owner lost the corrected spent hold");
            if (_returnTick == 0)
            {
                _authorityOutside = _authority.GlobalPosition;
                _ownerOutside = _owner.GlobalPosition;
                Require(_authorityOutside.X > _hostBall.CourtMax.X && _ownerOutside.X > _clientBall.CourtMax.X,
                    "host and owner must still be outside after recovery");
                _ackBeforeReturn = _authority.SprintAckForHarness;
                _returnTick = _tick;
                Input.ActionPress("move_left");
            }
            if (_tick - _returnTick < 75) return;
            Vector3 authoritative = _authority.GlobalPosition, predicted = _owner.GlobalPosition;
            Require(!CourtBounds.IsOutOfBounds(authoritative, _hostBall.CourtMin, _hostBall.CourtMax)
                && !CourtBounds.IsOutOfBounds(predicted, _clientBall.CourtMin, _clientBall.CourtMax)
                && _authorityOutside.X - authoritative.X > .1f && _ownerOutside.X - predicted.X > .1f,
                $"host and owner did not re-enter: host X={authoritative.X:F3}, owner X={predicted.X:F3}");
            Require(_authority.SprintAckForHarness > _ackBeforeReturn + 30 && authoritative.DistanceTo(predicted) < .03f,
                $"re-entry did not converge: host={authoritative}, owner={predicted}");
            Finish(true, $"both peers returned and settled within {authoritative.DistanceTo(predicted):F4} m");
        }
        catch (Exception error) { Finish(false, error.Message); }
    }

    private bool HasSpentHold(BallController ball) =>
        ball.State == BallState.Held && ball.HasDribbled && ball.StateMachine.HolderPeerId == _ownerId;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private void Finish(bool pass, string message)
    {
        _finished = true;
        Input.ActionRelease("move_right");
        Input.ActionRelease("move_left");
        if (_clientBall != null) Input.ActionRelease(_clientBall.ShootAction);
        GD.Print($"[harness] {(pass ? "PASS" : "FAIL")} net-oob-shot: {message}");
        GetTree().Quit(pass ? 0 : 1);
    }
}
