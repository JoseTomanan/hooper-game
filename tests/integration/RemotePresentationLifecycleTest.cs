using System.Diagnostics;
using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Networking;
using Hooper.Player;
using Hooper.Systems;

namespace HOOPERGAME.Tests.Integration;

// Fixture scoring isolates visual draining after game-over; this is not a shot
// or basket proof. Reconnection replaces only the transport after clearing old
// replicated clones; NetworkManager's one-shot lobby is deliberately not tested.
public partial class RemotePresentationLifecycleTest : Node
{
    private const int AwaitReconnectHandshake = 45;
    private readonly Stopwatch _wall = Stopwatch.StartNew();
    private string _role;
    private int _port, _clientId, _localId, _oldLocalId, _stage, _tick, _startTick = -1, _pulseTick = -1;
    private int _finishTick = -1, _reconnectTick = -1, _oldEpoch;
    private bool _finished, _readySent, _expectedReceived, _gameOverObserved, _lagProved;
    private bool _reconnectPrepared, _disconnected, _reconnected, _finishAcknowledged;
    private long _freezeTick, _oldDisplayedTick;
    private Vector3 _freezePosition;
    private MovePhase _freezePhase;
    private int _freezeFrame;
    private HandSide _freezeHand;
    private PlayerController.AnimationDisplayFrame _atGameOver;
    private NetworkManager _network;
    private Node _players;
    private BallController _ball;
    private GameManager _game;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _role = HarnessArgs.ReadArg(args, "--harness-role", "server");
        _port = int.Parse(HarnessArgs.ReadArg(args, "--harness-port", "23467"));
        if (!Require(_role is "server" or "client", "unknown role")) return;
        Node main = GetNode("Main");
        _network = main.GetNode<NetworkManager>("NetworkManager");
        _players = main.GetNode("Players");
        _ball = main.GetNode<BallController>("Ball");
        _game = main.GetNode<GameManager>("GameManager");
        _network.ConnectionFailed += () => Require(false, "connection failed");
        // Production Ball subscribed during child _Ready, so these observations
        // run after its connection/disconnection epoch resets.
        // https://docs.godotengine.org/en/4.7/tutorials/networking/high_level_multiplayer.html#managing-connections
        if (_role == "server")
        {
            Multiplayer.PeerConnected += ServerPeerConnected;
            Multiplayer.PeerDisconnected += id => Require(_stage >= 4 && id == _clientId,
                $"unexpected server-side disconnect {id} at stage {_stage}");
            _network.ServerStarted += _ => GD.Print("[remote-lifecycle] READY server");
            _network.HostGame(_port);
        }
        else
        {
            Multiplayer.ConnectedToServer += ClientConnected;
            Multiplayer.ServerDisconnected += ClientDisconnected;
            _game.GameOver += _ => LatchGameOver();
            _network.JoinGame("127.0.0.1", _port);
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _tick++;
        if (_wall.Elapsed.TotalSeconds > 75 || _tick > 9000)
        { Require(false, $"timeout stage={_stage} lag={_lagProved} reconnect={_reconnected}"); return; }
        if (_role == "server") TickServer(); else TickClient();
    }

    private void ServerPeerConnected(long id)
    {
        if (!Require(id > 1 && (_clientId == 0 || _stage == 4), $"unexpected peer {id} stage={_stage}")) return;
        _clientId = (int)id;
    }

    private void TickServer()
    {
        if (_pulseTick >= 0 && _tick == _pulseTick + 8) Input.ActionRelease("aim_left");
        if (_stage == 0 || _stage >= 3)
        {
            if (_stage == 6 && _finishAcknowledged && _tick - _finishTick >= 30) Finish();
            return;
        }
        PlayerController host = _players.GetNodeOrNull<PlayerController>("1");
        if (!Require(host != null, "host disappeared")) return;
        int elapsed = _tick - _startTick;
        if (_stage == 1)
        {
            if (elapsed == 15) Input.ActionPress("move_forward");
            if (elapsed == 25) Input.ActionRelease("move_forward");
            if (elapsed < 60 || _ball.State != BallState.Dribbling) return;
            if (!Require(host.HandSide == HandSide.Right && host.PhaseForHarness == MovePhase.Inactive,
                "fixture requires a neutral right-origin dribble")) return;
            Input.ActionPress("aim_left"); _pulseTick = _tick; _stage = 2;
            return;
        }
        if (host.PhaseForHarness != MovePhase.Startup || host.FrameInPhaseForHarness < 1) return;
        _freezeTick = (long)Engine.GetPhysicsFrames();
        _freezePosition = host.GlobalPosition;
        _freezePhase = host.PhaseForHarness;
        _freezeFrame = host.FrameInPhaseForHarness;
        _freezeHand = host.HandSide;
        // Fixture reaches real GameManager game-over synchronously; the machine
        // is frozen before its next tick. No player or ball state is injected.
        for (int i = 0; i < _game.TargetScore; i++) _game.RegisterBasket(1);
        if (!Require(_game.IsGameOver, "score fixture did not reach authoritative game-over")) return;
        Send(_clientId, MethodName.ExpectedFinal, _freezeTick, _freezePosition,
            (int)_freezePhase, _freezeFrame, (int)_freezeHand);
        _stage = 3;
        GD.Print($"[remote-lifecycle] server froze Startup frame={_freezeFrame} sourceTick={_freezeTick}");
    }

    private void ClientConnected()
    {
        _localId = Multiplayer.GetUniqueId();
        if (!Require(_localId > 1 && !Multiplayer.IsServer(), "client role invalid")) return;
        if (!_disconnected) return;
        if (!Require(_ball.PresentationEpoch > _oldEpoch && _ball.GetPresentationFrame() == null &&
            _ball.PresentationQueuedFrames == 0, "connected signal reused stale history")) return;
        _reconnected = true; _stage = 5;
    }

    private void LatchGameOver()
    {
        if (_gameOverObserved || _role != "client") return;
        PlayerController remote = _players.GetNodeOrNull<PlayerController>("1");
        if (!Require(remote != null, "game-over arrived before remote rig")) return;
        _atGameOver = remote.AnimationDisplaySnapshot;
        _gameOverObserved = true;
    }

    private void TickClient()
    {
        if (_stage == 4)
        {
            if (_tick - _reconnectTick < 2) return;
            // NetworkManager.JoinGame is intentionally one-shot. This explicit
            // transport fixture preserves its existing ConnectedToServer handler
            // and normal server MultiplayerSpawner replication on the new peer.
            // https://docs.godotengine.org/en/4.7/classes/class_enetmultiplayerpeer.html#class-enetmultiplayerpeer-method-create-client
            var peer = new ENetMultiplayerPeer();
            if (!Require(peer.CreateClient("127.0.0.1", _port) == Error.Ok, "reconnect CreateClient failed")) return;
            Multiplayer.MultiplayerPeer = peer; _stage = AwaitReconnectHandshake;
            return;
        }
        if (_stage == 7)
        {
            if (_tick - _finishTick >= 5) Finish();
            return;
        }
        if (_localId <= 1 || _stage == AwaitReconnectHandshake || _stage == 3) return;
        PlayerController remote = _players.GetNodeOrNull<PlayerController>("1");
        PlayerController own = _players.GetNodeOrNull<PlayerController>(_localId.ToString());
        if (remote == null || own == null) return;
        WorldPresentationFrame world = _ball.GetPresentationFrame();
        if (world == null || !world.Players.Any(p => p.PeerId == _localId)) return;
        if (!_readySent)
        {
            _ball.ConfigureContinuityPresentationDelay(6);
            _readySent = true;
            Send(1, MethodName.ClientReady);
            return;
        }
        // A new local clone in a frozen match never runs ApplyAnimation: its
        // default snapshot is not evidence about role selection. Before the win,
        // the existing clone must have produced a real local display snapshot.
        bool ownRole = _reconnected ? own.ContinuityIsLocalPlayer && own.PlayerPresentationTick == -1 :
            own.AnimationDisplaySnapshot.SimulatedLocally && own.AnimationDisplaySnapshot.PresentationTick == -1;
        if (!Require(ownRole && own.ContinuityAnimationCallback == "Physics",
            "own player acquired remote presentation delay/manual animation")) return;
        if (_stage == 5)
        {
            if (remote.AnimationDisplaySnapshot.PresentationTick <= _oldDisplayedTick) return;
            if (!Require(_reconnected && _localId != _oldLocalId && _ball.PresentationEpoch > _oldEpoch,
                "reconnect failed to establish a new peer/epoch")) return;
            if (!ValidateFinal(remote, "reconnected")) return;
            Send(1, MethodName.ReconnectedProof); _stage = 6;
            GD.Print($"[remote-lifecycle] client reconnected newPeer={_localId} epoch={_ball.PresentationEpoch} sourceTick={remote.PlayerPresentationTick}");
            GD.Print("[remote-lifecycle] post-reconnect own-role proof covers local selection/Physics callback only; gameplay is frozen, so no new input behavior is claimed.");
            return;
        }
        if (!_expectedReceived || !_gameOverObserved || _stage >= 2) return;
        if (!_lagProved)
        {
            if (!Require(_atGameOver.PresentationTick >= 0 && _atGameOver.PresentationTick < _freezeTick &&
                (_atGameOver.Phase != _freezePhase || _atGameOver.FrameInPhase != _freezeFrame),
                $"game-over lag premise absent: observed={_atGameOver} freeze={_freezeTick}/{_freezePhase}/{_freezeFrame}")) return;
            _lagProved = true;
        }
        if (remote.AnimationDisplaySnapshot.PresentationTick < _freezeTick + 1) return;
        if (!Require(_ball.ContinuityPresentationDelayApplied && _ball.ContinuityActualDelayTicks >= 6 &&
            _ball.PresentationResyncCount == 0, "missing delayed-envelope/continuous drain premise")) return;
        if (!ValidateFinal(remote, "game-over drained")) return;
        _oldDisplayedTick = remote.PlayerPresentationTick; _oldEpoch = _ball.PresentationEpoch; _oldLocalId = _localId;
        Send(1, MethodName.Drained); _stage = 2;
        GD.Print($"[remote-lifecycle] client game-over lag/drain proved tick={_oldDisplayedTick} delay={_ball.ContinuityActualDelayTicks}");
    }

    private bool ValidateFinal(PlayerController remote, string label)
    {
        var pose = remote.AnimationDisplaySnapshot;
        Vector3 mesh = remote.GetNode<Node3D>("CharacterModel").GlobalPosition;
        return Require(_game.IsGameOver && !pose.SimulatedLocally && pose.Phase == _freezePhase &&
            pose.FrameInPhase == _freezeFrame && pose.Hand == _freezeHand &&
            remote.ActiveAnimNodeForHarness == "CrossoverStartupRight" &&
            remote.ContinuityCrossoverPoseMetrics.Applied && remote.ContinuityAnimationCallback == "Manual" &&
            _ball.RenderedPresentationTick == pose.PresentationTick &&
            Mathf.Abs(mesh.X - _freezePosition.X) <= 0.0001f && Mathf.Abs(mesh.Z - _freezePosition.Z) <= 0.0001f,
            $"{label}: expected frozen real mesh/tree/modifier and coherent ball tick; pose={pose} mesh={mesh} expected={_freezePosition} tree={remote.ActiveAnimNodeForHarness} callback={remote.ContinuityAnimationCallback} applied={remote.ContinuityCrossoverPoseMetrics.Applied} ballTick={_ball.RenderedPresentationTick}");
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ClientReady()
    {
        if (!FromClient(0)) return;
        _startTick = _tick; _stage = 1;
    }
    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ExpectedFinal(long tick, Vector3 position, int phase, int frame, int hand)
    {
        if (!FromServer() || !Require(!_expectedReceived, "duplicate final observation")) return;
        _freezeTick = tick; _freezePosition = position; _freezePhase = (MovePhase)phase;
        _freezeFrame = frame; _freezeHand = (HandSide)hand; _expectedReceived = true;
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Drained()
    {
        if (!FromClient(3)) return;
        Send(_clientId, MethodName.PrepareReconnect); _stage = 4;
    }
    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void PrepareReconnect()
    {
        if (!FromServer() || !Require(_stage == 2 && _lagProved, "premature reconnect preparation")) return;
        _reconnectPrepared = true; _stage = 3;
        Send(1, MethodName.ReconnectPrepared);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReconnectPrepared()
    {
        if (!FromClient(4)) return;
        // Default force=false preserves the actual transport disconnect signal.
        // https://docs.godotengine.org/en/4.7/classes/class_multiplayerpeer.html#class-multiplayerpeer-method-disconnect-peer
        Multiplayer.MultiplayerPeer.DisconnectPeer(_clientId);
    }
    private void ClientDisconnected()
    {
        if (!Require(_reconnectPrepared && !_disconnected && _stage == 3, "unexpected ServerDisconnected")) return;
        if (!Require(_ball.PresentationEpoch > _oldEpoch && _ball.GetPresentationFrame() == null &&
            _ball.PresentationQueuedFrames == 0, "disconnect did not empty history and advance epoch")) return;
        _disconnected = true;
        foreach (Node node in _players.GetChildren()) node.QueueFree();
        _reconnectTick = _tick; _localId = 0; _stage = 4;
        GD.Print($"[remote-lifecycle] client actual disconnect cleared history epoch={_ball.PresentationEpoch}");
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReconnectedProof()
    {
        if (!FromClient(4)) return;
        Send(_clientId, MethodName.FinishProof); _stage = 6;
    }
    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void FinishProof()
    {
        if (!FromServer() || !Require(_stage == 6, "finish before client reconnect proof")) return;
        Send(1, MethodName.FinishAcknowledged); _stage = 7; _finishTick = _tick;
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void FinishAcknowledged()
    {
        if (!FromClient(6)) return;
        _finishAcknowledged = true; _finishTick = _tick;
    }
    private bool FromClient(int stage) => Require(_role == "server" && _stage == stage &&
        Multiplayer.GetRemoteSenderId() == _clientId && Multiplayer.GetPeers().Contains(_clientId),
        $"invalid client RPC sender/stage actual={_stage} expected={stage}");
    private bool FromServer() => Require(_role == "client" && Multiplayer.GetRemoteSenderId() == 1,
        "invalid server RPC sender");
    private void Send(int peer, StringName method, params Variant[] args) =>
        Require(RpcId(peer, method, args) == Error.Ok, $"RPC {method} failed");
    private bool Require(bool condition, string message)
    {
        if (condition) return true;
        if (!_finished)
        {
            _finished = true;
            GD.PrintErr($"[remote-lifecycle] FAIL ({_role}): {message}");
            GD.Print($"[remote-lifecycle] {_role} RESULT: FAIL (exit 1)");
            GetTree().Quit(1);
        }
        return false;
    }
    private void Finish()
    {
        _finished = true;
        Input.ActionRelease("move_forward"); Input.ActionRelease("aim_left");
        GD.Print($"[remote-lifecycle] {_role} RESULT: PASS (exit 0)");
        GetTree().Quit(0);
    }
}
