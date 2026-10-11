using System;
using Godot;
using Hooper.Ball;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

/// <summary>Real loopback ENet RPCs followed by a deterministic forced correction.
/// The reference uses independent scripted inputs, never reads production buffer flags.</summary>
public partial class NetSprintTest : Node3D
{
    private SceneMultiplayer _serverApi, _clientApi;
    private PlayerController _server, _client, _reference;
    private BallController _clientBall;
    private Node3D _serverRoot, _clientRoot;
    private bool _connected, _finished;
    private int _tick, _baselineAck;
    private double _elapsed;
    private double Dt => 1.0 / Engine.PhysicsTicksPerSecond;
    public override void _Ready()
    {
        try
        {
            var serverPeer = new ENetMultiplayerPeer();
            var clientPeer = new ENetMultiplayerPeer();
            int port = int.Parse(HarnessArgs.ReadArg(OS.GetCmdlineUserArgs(), "--harness-port", "28425"));
            Require(serverPeer.CreateServer(port, 1) == Error.Ok && clientPeer.CreateClient("127.0.0.1", port) == Error.Ok, "ENet setup failed");
            _serverApi = new SceneMultiplayer { MultiplayerPeer = serverPeer };
            _clientApi = new SceneMultiplayer { MultiplayerPeer = clientPeer };
            _serverRoot = Scope("Server", _serverApi);
            _clientRoot = Scope("Client", _clientApi);
            _clientApi.ConnectedToServer += Configure;
            _clientApi.ConnectionFailed += () => Finish(false, "ENet connection failed");
        }
        catch (Exception error) { Finish(false, error.Message); }
    }
    private Node3D Scope(string name, SceneMultiplayer api)
    {
        // Isolate both bodies' collision spaces while retaining a real UDP transport.
        // Source: https://docs.godotengine.org/en/4.7/classes/class_viewport.html#class-viewport-property-own-world-3d
        var viewport = new SubViewport { Name = name, OwnWorld3D = true };
        AddChild(viewport);
        var root = new Node3D { Name = "World" };
        viewport.AddChild(root);
        // Source: https://docs.godotengine.org/en/4.7/classes/class_scenetree.html#class-scenetree-method-set-multiplayer
        GetTree().SetMultiplayer(api, root.GetPath());
        return root;
    }
    private (PlayerController, BallController) Player(Node3D root, int id)
    {
        var players = new Node3D { Name = "Players" };
        root.AddChild(players);
        var player = ResourceLoader.Load<PackedScene>("res://scenes/Player.tscn").Instantiate<PlayerController>();
        player.Name = id.ToString();
        players.AddChild(player);
        player.SetPhysicsProcess(false); player.SetProcess(false);
        player.GetNode<AnimationTree>("AnimationTree").Active = false;
        var ball = new BallController { Name = "Ball", Players = players };
        root.AddChild(ball);
        ball.SetPhysicsProcess(false);
        ball.StateMachine.ForceState(BallState.Dribbling, id);
        // Production discovery is SceneTree-global; scoped peers each need their own ball.
        player.SprintBindBallForHarness(ball);
        player.ResetContactStateForHarness(new Vector3(0, 0, 10), Mathf.Pi, Vector3.Zero);
        return (player, ball);
    }
    private void Configure()
    {
        try
        {
            int id = _clientApi.GetUniqueId();
            (_server, _) = Player(_serverRoot, id);
            (_client, _clientBall) = Player(_clientRoot, id);
            var viewport = new SubViewport { Name = "Reference", OwnWorld3D = true };
            AddChild(viewport);
            var root = new Node3D(); viewport.AddChild(root);
            (_reference, _) = Player(root, id);
            Require(_client.GetWorld3D().Space != _reference.GetWorld3D().Space, "reference space not isolated");
            Input.ActionPress("move_forward"); Input.ActionPress("move_sprint");
            _connected = true;
        }
        catch (Exception error) { Finish(false, error.Message); }
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        if (_elapsed > 12) { Finish(false, "timeout awaiting actual wire/replay evidence"); return; }
        if (!_connected) return;
        try
        {
            _tick++;
            if (_tick <= 80)
            {
                _server.SprintTickServerForHarness(Dt);
                _client.SprintTickClientForHarness(Dt);
                if (_tick < 80) return;
                Require(_server.SprintAckForHarness > 10 && _server.IsSprinting
                    && Math.Abs(_server.Velocity.Length() - _server.SprintSpeed) < 0.001f,
                    $"raw held sprint did not traverse guarded SubmitInput: ack={_server.SprintAckForHarness} speed={_server.Velocity.Length()}");
                GD.Print($"[net-sprint] wire ack={_server.SprintAckForHarness} speed={_server.Velocity.Length()}");
                _baselineAck = _client.SprintPreparePendingForHarness();
                Input.ActionRelease("move_forward"); Input.ActionPress("move_right");
                return;
            }
            // Twelve actual client prediction ticks, with server stepping suspended so
            // none are acknowledged. This deterministic pending history is all Right+sprint.
            if (_tick <= 92) { _client.SprintTickClientForHarness(Dt, suppressCorrection: true); return; }
            Require(_client.SprintPendingForHarness == 12, "pending history not twelve actual client ticks");
            Vector3 before = _client.GlobalPosition;
            Vector3 authPosition = new(0, 0, 4), authVelocity = new(0, 0, -_client.MoveSpeed);
            _reference.ResetContactStateForHarness(authPosition, Mathf.Pi, authVelocity);
            for (int i = 0; i < 12; i++) _reference.Move(Vector2.Right, Dt, sprint: true);
            // The current gate now rejects sprint. Historical allowed sprint must survive.
            _clientBall.StateMachine.ForceState(BallState.Dribbling, 1);
            Require(!_client.IsSprinting, "lost-possession premise did not close current sprint gate");
            _client.SprintForceCorrectionForHarness(authPosition, authVelocity, Mathf.Pi, _baselineAck, Dt);
            float positionError = _client.GlobalPosition.DistanceTo(_reference.GlobalPosition);
            float velocityError = _client.Velocity.DistanceTo(_reference.Velocity);
            float headingError = Math.Abs(Mathf.AngleDifference(_client.Heading, _reference.Heading));
            Require(before.DistanceTo(_client.GlobalPosition) > 0.1f, "correction did not actually move body");
            Require(positionError < 0.0001f && velocityError < 0.0001f && headingError < 0.0001f,
                $"independent server-step reference mismatch: position={positionError} velocity={velocityError} heading={headingError}");
            GD.Print($"[net-sprint] correction={before.DistanceTo(_client.GlobalPosition)} pending=12 positionError={positionError} velocityError={velocityError} headingError={headingError}");
            Finish(true, "real held-input RPC and corrected historical sprint replay");
        }
        catch (Exception error) { Finish(false, error.Message); }
    }
    private void Finish(bool pass, string message)
    { _finished = true; GD.Print($"[net-sprint] {(pass ? "PASS" : "FAIL")} {message}"); GetTree().Quit(pass ? 0 : 1); }
}
