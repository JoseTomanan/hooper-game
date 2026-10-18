using System;
using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Networking;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// #415: real Main.tscn, spawner and ENet peers; only the locally owned player
// receives keyboard input. RPCs below pace observations, never seed replicated
// ball/player/animation state. Possession awards use the real server transition.
public partial class NetHeldPossessionTest : Node
{
    private string _role, _scenario;
    private int _clientId, _ownerId, _port, _stage = -1, _stable, _arc;
    private bool _readySent, _ready, _localDone, _remoteDone, _finished, _releasedMovement;
    private double _elapsed, _exitAt;
    private NetworkManager _network;
    private Node _players;
    private BallController _ball;
    private string _last = "waiting for peers";
    private static readonly string[] Stages = { "fresh-held", "live-dribble", "sustained-k-stepback/spent-idle", "spent-moving", "ownership-loss/reset", "new-possession-dribble" };

    public override void _Ready()
    {
        var args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _role = HarnessArgs.ReadArg(args, "--harness-role", "server");
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "host");
        _port = int.TryParse(HarnessArgs.ReadArg(args, "--harness-port", "23466"), out int port) ? port : 23466;
        if (!Require((_role == "server" || _role == "client") && (_scenario == "host" || _scenario == "client"), "invalid role/scenario")) return;
        // Observe after production players have selected/evaluated their display
        // frame. Lower priorities execute first, avoiding an artificial tick skew.
        // https://docs.godotengine.org/en/4.7/classes/class_node.html#class-node-property-process-physics-priority
        ProcessPhysicsPriority = 100;
        var main = GetNode("Main");
        _network = main.GetNode<NetworkManager>("NetworkManager");
        _players = main.GetNode("Players");
        _ball = main.GetNode<BallController>("Ball");
        if (!Require(_network.PlayerScenePath == "res://scenes/Player.tscn", "production Player scene replaced")) return;
        _network.ConnectionFailed += () => Require(false, "connection failed");
        if (_role == "server")
        {
            Multiplayer.PeerConnected += id => _clientId = (int)id;
            _network.ServerStarted += port => GD.Print("[net-held] READY server");
            _network.HostGame(_port);
        }
        else
        {
            Multiplayer.ConnectedToServer += () => _clientId = Multiplayer.GetUniqueId();
            _network.JoinGame("127.0.0.1", _port);
        }
        GD.Print($"[net-held] boot role={_role} variant={_scenario} production=Main.tscn physical-key-input");
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        if (_elapsed > 55) { Require(false, $"timeout stage={_stage} arc={_arc} stable={_stable} last={_last}"); return; }
        if (_exitAt > 0)
        {
            if (_elapsed >= _exitAt) Finish(0);
            return;
        }
        if (_clientId <= 1) return;
        _ownerId = _scenario == "host" ? 1 : _clientId;
        var holder = _players.GetNodeOrNull<PlayerController>(_ownerId.ToString());
        var other = _players.GetNodeOrNull<PlayerController>((_ownerId == 1 ? _clientId : 1).ToString());
        if (holder == null || other == null) return;
        if (!_readySent)
        {
            if (holder.ActiveAnimNodeForHarness == "" || other.ActiveAnimNodeForHarness == "") return;
            if (!ValidateBindings(holder) || !ValidateBindings(other)) return;
            _readySent = true;
            if (_role == "client") RpcId(1, MethodName.ClientReady);
        }
        if (_role == "server" && _stage < 0 && _ready)
        {
            // Fixture placement precedes observations; no player simulation or
            // prediction is replaced after the input-driven sequence begins.
            holder.GlobalPosition = new Vector3(-2, 0, 7);
            other.GlobalPosition = new Vector3(4, 0, 7);
            StartStage(0);
        }
        if (_stage < 0) return;
        Observe(holder, other);
        if (_finished) return;
        if (_role == "server" && _localDone && _remoteDone)
        {
            if (_stage == Stages.Length - 1)
            {
                RpcId(_clientId, MethodName.Complete);
                _exitAt = _elapsed + 0.5;
            }
            else StartStage(_stage + 1);
        }
    }

    private void StartStage(int stage)
    {
        // Server-only reset/loss fixtures deliberately exercise AwardPossession's
        // real HasDribbled reset; the client must learn both awards via netcode.
        if (stage == 0 || stage == 5) _ball.PostAwardForHarness(_ownerId);
        if (stage == 4) _ball.PostAwardForHarness(_ownerId == 1 ? _clientId : 1);
        RpcId(_clientId, MethodName.BeginStage, stage);
        EnterStage(stage);
    }

    private void EnterStage(int stage)
    {
        _stage = stage; _stable = 0; _localDone = _remoteDone = false;
        if (stage == 2) { _arc = 0; _releasedMovement = false; }
        if (OwnsActor())
        {
            if (stage == 1 || stage == 3 || stage == 5) KeyEvent(Key.W, true);
            if (stage == 2) KeyEvent(Key.K, true);
            if (stage == 4) { KeyEvent(Key.W, false); KeyEvent(Key.K, false); }
        }
        GD.Print($"[net-held] role={_role} stage={stage}/{Stages[stage]} owner={_ownerId}");
    }

    private void Observe(PlayerController actor, PlayerController other)
    {
        bool historical = _role == "client" && _ownerId == 1;
        BallState state = _ball.State;
        int holderId = _ball.StateMachine.HolderPeerId;
        long tick = -1;
        if (historical)
        {
            var world = _ball.GetPresentationFrame();
            if (world == null || actor.PlayerPresentationTick < 0) return;
            if (!Require(world.Tick == actor.PlayerPresentationTick, $"remote world/player tick mismatch {world.Tick}/{actor.PlayerPresentationTick}")) return;
            tick = world.Tick; state = world.Ball.State; holderId = world.Ball.HolderPeerId;
        }
        var phase = actor.DisplayMove().phase;
        string node = actor.ActiveAnimNodeForHarness;
        float speed = new Vector2(actor.DisplayVelocity.X, actor.DisplayVelocity.Z).Length();
        string hand = actor.DisplayHandSide.ToString();
        float blend = actor.GetNode<AnimationTree>("AnimationTree").Get($"parameters/Held{hand}/blend_position").AsSingle();
        _last = $"tick={tick} historical={historical} phase={phase} move={actor.DisplayMoveId()} ball={state}/{holderId} liveSpent={_ball.HasDribbled} node={node} hand={hand} speed={speed:F3} blend={blend:F3}";
        // The coherent envelope carries Held/holder, NOT HasDribbled. LiveSpent
        // below is independently required after stable convergence, never claimed
        // to be historical metadata from the tick printed above.
        bool liveOwner = _ball.StateMachine.HolderPeerId == _ownerId;
        bool held = state == BallState.Held && holderId == _ownerId && phase == MovePhase.Inactive;
        bool heldPlayback = node == "Held" + hand && BoundClips(actor, node) == $"held/idle{hand.ToLowerInvariant()}|held/moving{hand.ToLowerInvariant()}" &&
            float.IsFinite(blend) && MathF.Abs(blend - MathF.Min(speed, actor.MoveSpeed)) < 0.03f;
        bool offBall = other.ActiveAnimNodeForHarness == "Locomotion";
        bool good = false;
        switch (_stage)
        {
            case 0:
                good = held && heldPlayback && speed < 0.02f && liveOwner && !_ball.HasDribbled && offBall;
                break;
            case 1:
            case 5:
                good = state == BallState.Dribbling && holderId == _ownerId && phase == MovePhase.Inactive &&
                    node == "Dribble" + hand && BoundClips(actor, node) ==
                    $"locomotion/dribbleidle{hand.ToLowerInvariant()}|locomotion/dribblemove{hand.ToLowerInvariant()}" && offBall;
                break;
            case 2:
                if (phase != MovePhase.Inactive && actor.DisplayMoveId() == "stepback")
                {
                    if (OwnsActor() && !_releasedMovement) { KeyEvent(Key.W, false); _releasedMovement = true; }
                    string expected = "StepBack" + phase;
                    if (node == expected && BoundClips(actor, node) == "locomotion/stepback" + phase.ToString().ToLowerInvariant())
                    {
                        int bit = phase == MovePhase.Startup ? 1 : phase == MovePhase.Active ? 2 : 4;
                        if ((_arc & bit) == 0)
                        {
                            if (!Require(_arc == bit - 1, "StepBack playback phase skipped/reordered: " + _last)) return;
                            if (_role == "server" && phase == MovePhase.Active &&
                                !Require(state == BallState.Held && holderId == _ownerId && _ball.HasDribbled,
                                    "authoritative Active gather must immediately mark spent Held: " + _last)) return;
                            GD.Print($"[net-held] same-tick StepBack role={_role} {_last}");
                        }
                        _arc |= bit;
                    }
                }
                if (_arc == 7 && phase == MovePhase.Inactive && held)
                {
                    if (!Require(heldPlayback, "StepBack recovery must exit directly into actual Held playback: " + _last)) return;
                    good = speed < 0.02f && liveOwner && _ball.HasDribbled && offBall;
                }
                break;
            case 3:
                good = held && heldPlayback && speed > actor.MoveSpeed * 0.9f && liveOwner && _ball.HasDribbled && offBall;
                break;
            case 4:
                int newHolder = _ownerId == 1 ? _clientId : 1;
                good = state == BallState.Held && holderId == newHolder && node == "Locomotion" &&
                    _ball.StateMachine.HolderPeerId == newHolder && !_ball.HasDribbled &&
                    other.ActiveAnimNodeForHarness == "Held" + other.DisplayHandSide;
                break;
        }
        _stable = good ? _stable + 1 : 0;
        if (_stable < (_stage == 2 ? 20 : 4) || _localDone) return;
        _localDone = true;
        GD.Print($"[net-held] stage-proof role={_role} stage={_stage} arc={_arc} {_last}");
        if (_role == "client") RpcId(1, MethodName.ClientObserved, _stage);
    }

    private bool ValidateBindings(PlayerController player)
    {
        var tree = player.GetNode<AnimationTree>("AnimationTree");
        var root = tree.TreeRoot as AnimationNodeStateMachine;
        var animations = player.GetNode<AnimationPlayer>("AnimationPlayer");
        // Observe actual state-machine blend resources, not the resolver target.
        // https://docs.godotengine.org/en/4.7/classes/class_animationnodeblendspace1d.html
        foreach (string hand in new[] { "Left", "Right" })
        {
            string state = "Held" + hand, suffix = hand.ToLowerInvariant();
            if (!Require(root != null && root.HasNode(state) && root.GetNode(state) is AnimationNodeBlendSpace1D,
                $"{player.Name}: missing {state} blend")) return false;
            var blend = (AnimationNodeBlendSpace1D)root.GetNode(state);
            if (!Require(blend.GetBlendPointCount() == 2 && MathF.Abs(blend.GetBlendPointPosition(0)) < 0.001f &&
                MathF.Abs(blend.GetBlendPointPosition(1) - player.MoveSpeed) < 0.001f &&
                BoundClips(player, state) == $"held/idle{suffix}|held/moving{suffix}" &&
                animations.HasAnimation($"held/idle{suffix}") && animations.HasAnimation($"held/moving{suffix}"),
                $"{player.Name}: {state} endpoints/clips are not bound to the real held library")) return false;
        }
        return true;
    }

    private static string BoundClips(PlayerController player, string state)
    {
        var root = player.GetNode<AnimationTree>("AnimationTree").TreeRoot as AnimationNodeStateMachine;
        if (root == null || !root.HasNode(state)) return "<missing>";
        var node = root.GetNode(state);
        if (node is AnimationNodeAnimation clip) return clip.Animation;
        if (node is AnimationNodeBlendSpace1D blend)
            return string.Join("|", Enumerable.Range(0, blend.GetBlendPointCount()).Select(i =>
                blend.GetBlendPointNode(i) is AnimationNodeAnimation animation ? animation.Animation.ToString() : "<missing>"));
        return "<unknown>";
    }

    private bool OwnsActor() => (_role == "server" && _scenario == "host") || (_role == "client" && _scenario == "client");
    // Actual physical-key events traverse the production InputMap/detector.
    // https://docs.godotengine.org/en/4.7/classes/class_input.html#class-input-method-parse-input-event
    private static void KeyEvent(Key key, bool pressed) => Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = key, Keycode = key, Pressed = pressed });

    // Reliable RPCs exchange readiness, commands and completed observations only.
    // https://docs.godotengine.org/en/4.7/tutorials/networking/high_level_multiplayer.html#remote-procedure-calls
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ClientReady()
    {
        if (Require(_role == "server" && Multiplayer.GetRemoteSenderId() == _clientId && !_ready, "invalid readiness sender/state")) _ready = true;
    }
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void BeginStage(int stage)
    {
        if (Require(_role == "client" && Multiplayer.GetRemoteSenderId() == 1 && stage == _stage + 1 && stage < Stages.Length, "invalid stage sender/order")) EnterStage(stage);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ClientObserved(int stage)
    {
        if (Require(_role == "server" && Multiplayer.GetRemoteSenderId() == _clientId && stage == _stage && !_remoteDone, "invalid observation sender/stage")) _remoteDone = true;
    }
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Complete()
    {
        if (Require(_role == "client" && Multiplayer.GetRemoteSenderId() == 1 && _stage == 5 && _localDone, "completion before both proofs")) Finish(0);
    }
    private bool Require(bool condition, string diagnostic)
    {
        if (condition) return true;
        GD.PrintErr($"[net-held] FAIL role={_role} variant={_scenario}: {diagnostic}"); Finish(1); return false;
    }
    private void Finish(int code)
    {
        if (_finished) return;
        _finished = true; KeyEvent(Key.W, false); KeyEvent(Key.K, false);
        GD.Print($"[net-held] {_role} RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
