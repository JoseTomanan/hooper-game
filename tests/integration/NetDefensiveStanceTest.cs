using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Networking;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

/// <summary>Dedicated authority and two genuine clients. Setup RPCs establish only
/// fixtures; posture request and effective stance use production RPCs.</summary>
public partial class NetDefensiveStanceTest : Node3D
{
    private string _role, _mutation;
    private Node3D _players;
    private PlayerController _attacker, _defender, _own, _other, _reference;
    private BallController _ball;
    private int _attackerId, _defenderId, _tick, _stage, _baselineAck, _replayTicks;
    private bool _registered, _configured, _running, _finished, _replay, _replayStarted;
    private bool _sawStance, _sawProtection, _sawLoose, _sawRemoteStance;
    private float _largestCorrection;
    private double _elapsed;
    private readonly HashSet<int> _ready = new(), _done = new();
    private double Dt => 1.0 / Engine.PhysicsTicksPerSecond;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs();
        _role = HarnessArgs.ReadArg(args, "--harness-role", "server");
        _mutation = HarnessArgs.ReadArg(args, "--mutation", "none");
        int port = int.Parse(HarnessArgs.ReadArg(args, "--harness-port", "28436"));
        _players = GetNode<Node3D>("Players");
        var net = GetNode<NetworkManager>("NetworkManager");
        if (_role == "server") { net.StartDedicatedServer(port); GD.Print("[stance-net] READY server"); }
        else net.JoinGame("127.0.0.1", port);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        if (_elapsed > 25) { Finish(false, "timeout"); return; }
        try
        {
            foreach (var player in _players.GetChildren().OfType<PlayerController>())
            { player.SetPhysicsProcess(false); player.SetProcess(false); }
            if (_role != "server" && !_registered && _players.GetChildCount() == 2
                && Multiplayer.MultiplayerPeer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected)
            { _registered = true; RpcId(1, MethodName.Register, _role); }
            if (!_running) return;
            _tick++;
            if (_replay)
            {
                if (_role == "defender") ReplayStep();
                else if (_role == "driver" && !_replayStarted)
                { _replayStarted = true; RpcId(1, MethodName.Completed); }
                return;
            }
            if (_role == "server")
            {
                if (_tick == 100) SwitchPossession(1);
                if (_tick == 180) SwitchPossession(2);
                if (_tick == 240) SwitchPossession(0);
                _attacker.SprintTickServerForHarness(Dt);
                _defender.SprintTickServerForHarness(Dt);
                if (_tick == 320)
                {
                    Require(_defender.SprintAckForHarness > 30 && _attacker.SprintAckForHarness > 30,
                        "both real guarded input streams must advance sequence acknowledgements");
                    Require(_defender.IsInDefensiveStance && !_defender.IsSprinting,
                        "authority must independently resolve stance over sprint");
                    _replay = true; Rpc(MethodName.StartReplay);
                }
                return;
            }
            if (_role == "defender")
            {
                Input.ActionPress("move_protect"); Input.ActionPress("move_sprint");
                Input.ActionPress("move_right");
            }
            else
            {
                Input.ActionRelease("move_protect"); Input.ActionRelease("move_sprint");
            }
            // Cosmetic corruption must not supply the raw opponent sample used by Move.
            _other.GlobalPosition = new Vector3(500, 0, -500);
            _other.SetHeadingForHarness(1.7f);
            _own.SprintTickClientForHarness(Dt);
            if (_role == "defender")
            {
                if (_stage == 0 && _tick > 30) _sawStance |= _own.IsInDefensiveStance && !_own.IsSprinting;
                if (_stage == 1) _sawProtection |= _own.IsProtecting && !_own.IsInDefensiveStance && !_own.IsSprinting;
                if (_stage == 2) _sawLoose |= !_own.IsProtecting && !_own.IsInDefensiveStance && !_own.IsSprinting;
                if (_tick == 65) _own.GlobalPosition += new Vector3(.75f, 0, .5f);
            }
            else _sawRemoteStance |= _other.StanceNetReceived && _other.DisplayDefensiveStance
                && !Input.IsActionPressed("move_protect") && !Input.IsActionPressed("move_sprint");
            bool freshRemoteState = _other.ContactNetNewState;
            _other.ProtectionTickRemoteForHarness();
            if (_role == "driver" && _other.StanceNetReceived && freshRemoteState)
                Require(Math.Abs(Mathf.AngleDifference(_other.Heading, _other.ContactNetRawHeading)) < .00001f,
                    "remote stance heading must consume broadcast authoritative heading");
        }
        catch (Exception error) { Finish(false, error.Message); }
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Register(string role)
    {
        if (_role != "server") return;
        int sender = Multiplayer.GetRemoteSenderId();
        if (role == "driver") _attackerId = sender;
        else if (role == "defender") _defenderId = sender;
        else throw new InvalidOperationException("unknown role");
        if (_attackerId == 0 || _defenderId == 0 || _configured) return;
        Configure(_attackerId, _defenderId); Rpc(MethodName.Configure, _attackerId, _defenderId);
    }

    [Rpc(TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Configure(int attacker, int defender)
    {
        try
        {
            Require(_players.GetChildCount() == 2 && attacker != defender && attacker != 1 && defender != 1,
                "must be dedicated authority without a player 1, and two independent clients");
            _attackerId = attacker; _defenderId = defender;
            _attacker = _players.GetNode<PlayerController>(attacker.ToString());
            _defender = _players.GetNode<PlayerController>(defender.ToString());
            _ball = new BallController { Name = "Ball", Players = _players }; AddChild(_ball);
            _ball.SetPhysicsProcess(false);
            foreach (var player in new[] { _attacker, _defender })
            {
                player.SetPhysicsProcess(false); player.SetProcess(false);
                player.GetNode<AnimationTree>("AnimationTree").Active = false;
                player.SprintBindBallForHarness(_ball);
            }
            _attacker.ResetContactStateForHarness(new Vector3(0, 0, 30), Mathf.Pi, Vector3.Zero);
            _defender.ResetContactStateForHarness(new Vector3(0, 0, 10), 0, Vector3.Zero);
            ApplyPossession(0);
            if (_role != "server")
            {
                _own = _role == "driver" ? _attacker : _defender;
                _other = _role == "driver" ? _defender : _attacker;
                Require(_own.ContactNetPeerId == Multiplayer.GetUniqueId(), "local ownership premise");
                _own.ContactNetObserveReconciliation((before, after, count) =>
                    _largestCorrection = Math.Max(_largestCorrection, before.DistanceTo(after)));
                RpcId(1, MethodName.ReadyToRun);
            }
            _configured = true;
        }
        catch (Exception error) { Finish(false, error.Message); }
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReadyToRun()
    {
        if (_role != "server") return;
        int sender = Multiplayer.GetRemoteSenderId();
        Require(sender == _attackerId || sender == _defenderId, "unexpected ready sender");
        _ready.Add(sender);
        if (_ready.Count != 2) return;
        _running = true; Rpc(MethodName.BeginRun);
    }
    [Rpc(TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void BeginRun() => _running = true;
    private void SwitchPossession(int stage) { ApplyPossession(stage); Rpc(MethodName.ApplyPossession, stage); }
    [Rpc(TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ApplyPossession(int stage)
    {
        _stage = stage;
        _ball.StateMachine.ForceState(stage == 2 ? BallState.Loose : BallState.Dribbling,
            stage == 2 ? 0 : stage == 1 ? _defenderId : _attackerId);
    }
    [Rpc(TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void StartReplay()
    {
        try
        {
            if (_role == "defender")
            {
                Require(_sawStance && _sawProtection && _sawLoose && _largestCorrection > .1f,
                    $"wire transition/correction evidence missing stance={_sawStance} protect={_sawProtection} loose={_sawLoose} correction={_largestCorrection}");
                GD.Print($"[stance-net] wire physical correction={_largestCorrection} ack={_own.SprintAckForHarness}");
            }
            else Require(_sawRemoteStance, "observer never received broadcast effective stance");
            _replay = true;
        }
        catch (Exception error) { Finish(false, error.Message); }
    }

    private void ReplayStep()
    {
        // Allow already-sent broadcasts to drain. No server stepping occurs in this stage.
        if (++_replayTicks < 20) return;
        if (_replayTicks == 20)
        {
            _baselineAck = _own.SprintPreparePendingForHarness();
            Input.ActionRelease("move_sprint");
            var viewport = new SubViewport { Name = "Reference", OwnWorld3D = true }; AddChild(viewport);
            var root = new Node3D { Name = "World" }; viewport.AddChild(root);
            _reference = ResourceLoader.Load<PackedScene>("res://scenes/Player.tscn").Instantiate<PlayerController>();
            root.AddChild(_reference); _reference.SetPhysicsProcess(false); _reference.SetProcess(false);
            _reference.GetNode<AnimationTree>("AnimationTree").Active = false;
            return;
        }
        Vector3 rawPosition = new(0, 0, 60);
        _other.StanceNetRawSnapshot(rawPosition, Vector3.Zero, Mathf.Pi);
        _other.GlobalPosition = new Vector3(500, 0, -500);
        _other.SetHeadingForHarness(1.7f);
        if (_replayTicks <= 32)
        {
            int sample = _replayTicks - 21;
            if (sample < 4 || sample >= 8) Input.ActionPress("move_protect"); else Input.ActionRelease("move_protect");
            _ball.StateMachine.ForceState(sample >= 8 ? BallState.Loose : BallState.Dribbling, sample >= 8 ? 0 : _attackerId);
            _own.SprintTickClientForHarness(Dt, suppressCorrection: true);
            return;
        }
        Require(_own.SprintPendingForHarness == 12, "history must contain twelve real prediction ticks");
        Vector3 authPosition = new(0, 0, 4), authVelocity = new(0, 0, -2);
        float authHeading = Mathf.Pi / 3;
        var opponent = _own.StanceNetOpponent;
        Require(opponent.HasValue && opponent.Value.Position == rawPosition, "identical stationary raw replay sample premise");
        Require(_other.GlobalPosition.DistanceTo(rawPosition) > 100
            && Math.Abs(Mathf.AngleDifference(_other.Heading, opponent.Value.Heading)) > .5f,
            "raw versus cosmetic opponent state must be observably distinct");
        _reference.ResetContactStateForHarness(authPosition, authHeading, authVelocity);
        for (int i = 0; i < 12; i++)
            _reference.Move(Vector2.Right, Dt, opponent, stance: i < 4);
        // Today's loose-ball context disagrees with the first four eligible historical ticks.
        _ball.StateMachine.ForceState(BallState.Loose, 0);
        if (_mutation != "none") _own.StanceNetMutateHistory(_mutation);
        Vector3 before = _own.GlobalPosition;
        _own.StanceNetCorrection(authPosition, authVelocity, authHeading, _baselineAck, Dt);
        float p = _own.GlobalPosition.DistanceTo(_reference.GlobalPosition);
        float v = _own.Velocity.DistanceTo(_reference.Velocity);
        float h = Math.Abs(Mathf.AngleDifference(_own.Heading, _reference.Heading));
        Require(before.DistanceTo(_own.GlobalPosition) > .1f, "forced reconciliation must move the physics body");
        Require(p < .0001f && v < .0001f && h < .0001f,
            $"independent historical replay mismatch p={p} v={v} h={h} mutation={_mutation}");
        GD.Print($"[stance-net] replay pending=12 p={p} v={v} h={h} correction={_largestCorrection}; latest-raw stationary sample only");
        _replayStarted = true; RpcId(1, MethodName.Completed);
        SetPhysicsProcess(false);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Completed()
    {
        if (_role != "server") return;
        int sender = Multiplayer.GetRemoteSenderId();
        Require(sender == _attackerId || sender == _defenderId, "unexpected completion sender");
        _done.Add(sender);
        if (_done.Count != 2) return;
        Rpc(MethodName.AllPassed); Finish(true, "two client gates and authoritative seq/ack gates");
    }
    [Rpc(TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AllPassed() => Finish(true, "posture wire, possession modes, remote broadcast, physical correction and sampled replay");
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private void Finish(bool pass, string message)
    {
        if (_finished) return; _finished = true;
        GD.Print($"[harness] {(pass ? "PASS" : "FAIL")} stance-net {_role}: {message}");
        if (pass) GetTree().CreateTimer(.5).Timeout += () => GetTree().Quit(0); else GetTree().Quit(1);
    }
}
