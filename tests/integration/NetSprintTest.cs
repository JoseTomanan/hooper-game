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
    private string _scenario;
    private PlayerController.MovingTurnSnapshot _movingBaseline;
    private float _movingActiveDistance;
    private bool _movingSawLatch, _movingCompleted;
    private double _elapsed;
    private double Dt => 1.0 / Engine.PhysicsTicksPerSecond;
    public override void _Ready()
    {
        try
        {
            _scenario = HarnessArgs.ReadArg(OS.GetCmdlineUserArgs(), "--harness-scenario", "historical-sprint");
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
                if (_scenario == "moving-turn")
                {
                    var position = new Vector3(0, 0, 10);
                    _client.ResetContactStateForHarness(position, 0, Vector3.Zero);
                    _reference.ResetContactStateForHarness(position, 0, Vector3.Zero);
                    _movingBaseline = _client.CaptureMovingTurnForHarness();
                    return;
                }
                Input.ActionRelease("move_forward"); Input.ActionPress("move_right");
                return;
            }
            if (_scenario == "moving-turn") { TickMovingTurn(); return; }
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

    private void TickMovingTurn()
    {
        int step = _tick - 81;
        if (step < 40)
        {
            // This script is independent of the production buffer. Input.GetVector
            // remains exercised by TickClientOwnPlayer, including its real gates.
            bool sprint = step < 4;
            bool protect = step >= 4 && step < 8;
            Vector2 input = step >= 8 ? Vector2.Zero : step >= 4
                ? new Vector2(.5f, -MathF.Sqrt(3) / 2) : new Vector2(0, -1);
            SetMovingTurnInput(input, sprint, protect);
            Vector3 before = _client.GlobalPosition;
            _client.SprintTickClientForHarness(Dt, suppressCorrection: true);
            Require(_client.IsSprinting == sprint && _client.IsProtecting == protect,
                $"moving-turn tick={step}: scripted possession/pace gates did not resolve as expected");
            _reference.Move(input, Dt, sprint: sprint, protect: protect);
            AssertMovingTurnState($"prediction tick={step}");
            if (_client.IsPivotingInPlace)
            {
                _movingSawLatch = true;
                _movingActiveDistance += before.DistanceTo(_client.GlobalPosition);
            }
            else if (_movingSawLatch) _movingCompleted = true;
            return;
        }
        Require(_movingSawLatch && _movingCompleted && _movingActiveDistance > .02f,
            $"moving-turn lacked a completed travelling turn: activeDistance={_movingActiveDistance}");
        Require(_client.SprintPendingForHarness == 40, "moving-turn pending history not forty actual client ticks");
        var expected = _reference.CaptureMovingTurnForHarness();
        // Corrupt the predicted body, then invoke the real received-snapshot /
        // reconciliation path. Replaying today's ordinary mode for every tick
        // cannot reconstruct the independently scripted sprint/protect history.
        _client.ResetContactStateForHarness(new Vector3(10, 0, 10), -1, new Vector3(20, 0, 0));
        _client.ReconcileMovingTurnForHarness(_movingBaseline, _baselineAck, Dt);
        Require(_client.SprintPendingForHarness == 40, "moving-turn replay lost unacknowledged history");
        AssertMovingTurnState("historical pace replay");
        _client.ResetContactStateForHarness(new Vector3(10, 0, 10), -1, new Vector3(20, 0, 0));
        _client.ReconcileMovingTurnForHarness(expected, _client.MovingTurnLastSequenceForHarness, Dt);
        Require(_client.SprintPendingForHarness == 0 && _client.CaptureMovingTurnForHarness() == expected,
            "moving-turn fully acknowledged empty buffer did not adopt exact authoritative state");
        GD.Print($"[net-sprint] moving-turn actualPredictionTicks=40 activeDistance={_movingActiveDistance:F6}m pendingBeforeAck=40 pendingAfterAck=0");
        Finish(true, "moving-turn: real ENet warmup, live input prediction, historical pace replay and empty-buffer correction");
    }

    private static void SetMovingTurnInput(Vector2 input, bool sprint, bool protect)
    {
        foreach (string action in new[] { "move_left", "move_right", "move_forward", "move_backward", "move_sprint", "move_protect" })
            Input.ActionRelease(action);
        if (input.X > 0) Input.ActionPress("move_right", input.X);
        if (input.Y < 0) Input.ActionPress("move_forward", -input.Y);
        if (sprint) Input.ActionPress("move_sprint");
        if (protect) Input.ActionPress("move_protect");
    }

    private void AssertMovingTurnState(string label)
    {
        var actual = _client.CaptureMovingTurnForHarness();
        var expected = _reference.CaptureMovingTurnForHarness();
        Require(actual.Position.DistanceTo(expected.Position) < .0001f
            && actual.Velocity.DistanceTo(expected.Velocity) < .0001f
            && Math.Abs(Mathf.AngleDifference(actual.Heading, expected.Heading)) < .00001f
            && actual.Pivot.HasLatch == expected.Pivot.HasLatch
            && Math.Abs(Mathf.AngleDifference(actual.Pivot.LatchedYaw, expected.Pivot.LatchedYaw)) < .00001f
            && actual.Stance == expected.Stance && actual.Post == expected.Post && actual.Established == expected.Established,
            $"moving-turn {label}: independent reference mismatch {actual} vs {expected}");
    }
    private void Finish(bool pass, string message)
    { _finished = true; GD.Print($"[net-sprint] {(pass ? "PASS" : "FAIL")} {message}"); GetTree().Quit(pass ? 0 : 1); }
}
