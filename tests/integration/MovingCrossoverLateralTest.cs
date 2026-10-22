using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Networking;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// #424: real Main/Player/Ball, legal live possession and production input.
// Fixture seams only place inactive players and award the initial possession.
// No seam starts a move or supplies its phase, exit vector or burst velocity.
public partial class MovingCrossoverLateralTest : Node
{
    private readonly record struct Case(HandSide Hand, float Heading, string Exit, bool Sprint = false, bool Stationary = false);
    private readonly List<Case> _cases = new();
    private string _role, _scenario;
    private NetworkManager _network;
    private Node _players;
    private BallController _ball;
    private int _clientId, _ownerId = 1, _case = -1, _caseTick, _driveTick, _stable;
    private bool _networked, _readySent, _peerReady, _fixtureReady, _remoteFixtureReady;
    private bool _driving, _flick, _exitApplied, _activeChanged, _localDone, _remoteDone, _finished;
    private double _elapsed, _exitAt;
    private int _arc, _swaps, _activeSamples;
    private HandSide _previousHand;
    private Vector3 _previousPosition, _activeOrigin, _activeEnd, _activeVelocity;
    private Vector3 _finalStartupVelocity;
    private MovePhase _previousPhase;
    private int _previousPhaseFrame, _finalStartupFrame;
    private long _previousObservedTick;
    private ulong _previousPhysicsFrame;
    private float _entryHeading, _entrySpeed, _startupFirstSpeed, _startupLastSpeed, _startupLastForward;
    private long _lastPresentationTick = -1;
    private string _last = "waiting for production players";
    private static readonly Vector3 Origin = new(-2, 1, 7);
    private static readonly Vector3 DefenderOrigin = new(4, 1, 11);
    private Case Current => _cases[_case];
    private bool OwnsActor => !_networked || (_role == "server" && _scenario == "host") || (_role == "client" && _scenario == "client");

    public override void _Ready()
    {
        var args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _role = HarnessArgs.ReadArg(args, "--harness-role", "offline");
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "suite");
        _networked = _role != "offline";
        if (!Require((_role == "offline" && _scenario == "suite") ||
            ((_role == "server" || _role == "client") && (_scenario == "host" || _scenario == "client")), "unknown role/scenario")) return;
        // Observe the frozen frame after production simulation and presentation.
        // https://docs.godotengine.org/en/4.7/classes/class_node.html#class-node-property-process-physics-priority
        ProcessPhysicsPriority = 100;
        var main = GetNode("Main");
        _network = main.GetNode<NetworkManager>("NetworkManager");
        _players = main.GetNode("Players");
        _ball = main.GetNode<BallController>("Ball");
        if (!Require(_network.PlayerScenePath == "res://scenes/Player.tscn", "fixture must retain production Player scene")) return;
        foreach (var hand in new[] { HandSide.Right, HandSide.Left })
        foreach (float heading in new[] { MathF.PI, MathF.PI / 2 })
        foreach (string exit in _networked ? new[] { "held", "released" } : new[] { "held", "diagonal", "released" })
        {
            // Four network cases cover both hands, exit contracts and headings.
            if (_networked && (heading == MathF.PI) != (exit == "held")) continue;
            _cases.Add(new(hand, heading, exit));
        }
        if (!_networked)
        {
            foreach (var hand in new[] { HandSide.Right, HandSide.Left })
            {
                _cases.Add(new(hand, MathF.PI, "held", Sprint: true));
                _cases.Add(new(hand, MathF.PI / 2, "neutral", Stationary: true));
            }
            var scene = GD.Load<PackedScene>("res://scenes/Player.tscn");
            foreach (int id in new[] { 1, 2 })
            {
                var player = scene.Instantiate<PlayerController>();
                player.Name = id.ToString();
                _players.AddChild(player);
            }
        }
        else
        {
            int port = int.Parse(HarnessArgs.ReadArg(args, "--harness-port", "23467"));
            _network.ConnectionFailed += () => Require(false, "connection failed");
            if (_role == "server")
            {
                Multiplayer.PeerConnected += id => _clientId = (int)id;
                _network.ServerStarted += _ => GD.Print("[moving-crossover-lateral] READY server");
                _network.HostGame(port);
            }
            else
            {
                Multiplayer.ConnectedToServer += () => _clientId = Multiplayer.GetUniqueId();
                _network.JoinGame("127.0.0.1", port);
            }
        }
        GD.Print($"[moving-crossover-lateral] boot role={_role} scenario={_scenario} cases={_cases.Count}");
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        if (_elapsed > 55) { Require(false, "timeout: " + _last); return; }
        if (_exitAt > 0) { if (_elapsed >= _exitAt) Finish(0); return; }
        if (_networked && _clientId <= 1) return;
        _ownerId = _networked && _scenario == "client" ? _clientId : 1;
        var actor = Actor();
        var defender = _players.GetNodeOrNull<PlayerController>((_ownerId == 1 ? (_networked ? _clientId : 2) : 1).ToString());
        if (actor == null || defender == null || actor.ActiveAnimNodeForHarness == "" || defender.ActiveAnimNodeForHarness == "") return;
        if (!_readySent)
        {
            _readySent = true;
            if (_role == "client") RpcId(1, MethodName.ClientReady);
        }
        if (_case < 0)
        {
            if (!_networked || (_role == "server" && _peerReady)) StartCase(0);
            return;
        }
        _caseTick++;
        if (!_driving)
        {
            // Wait for actual authority/history convergence, including hand and
            // heading, before any input-driven measurement is allowed to start.
            bool ready = _caseTick >= 30 && actor.DisplayMove().phase == MovePhase.Inactive &&
                actor.DisplayHandSide == Current.Hand && actor.DisplayVelocity.Length() < 0.03f &&
                Horizontal(actor.DisplayRootPosition - Origin).Length() < 0.08f &&
                MathF.Abs(Mathf.AngleDifference(actor.DisplayHeading, Current.Heading)) < 0.02f &&
                _ball.StateMachine.HolderPeerId == _ownerId && _ball.State == BallState.Held;
            _stable = ready ? _stable + 1 : 0;
            _last = $"fixture case={_case} tick={_caseTick} pos={actor.DisplayRootPosition} hand={actor.DisplayHandSide} heading={actor.DisplayHeading:F3} ball={_ball.State}/{_ball.StateMachine.HolderPeerId}";
            if (_caseTick % 120 == 0) GD.Print("[moving-crossover-lateral] waiting " + _last);
            if (_stable >= 4 && !_fixtureReady)
            {
                _fixtureReady = true;
                if (_role == "client") RpcId(1, MethodName.ClientFixtureReady, _case);
            }
            if (_fixtureReady && (!_networked || (_role == "server" && _remoteFixtureReady)))
            {
                if (_networked) RpcId(_clientId, MethodName.BeginDrive, _case);
                EnterDrive();
            }
            return;
        }
        _driveTick++;
        if (OwnsActor)
        {
            if (Current.Stationary && _driveTick == 15) Movement(Vector2.Zero);
            int flickTick = Current.Stationary ? 45 : 22;
            if (_driveTick == flickTick)
            {
                if (!Require(_ball.State == BallState.Dribbling && _ball.StateMachine.HolderPeerId == _ownerId,
                    "gesture requires legal live Dribbling: " + _last)) return;
                _entrySpeed = Horizontal(actor.Velocity).Length();
                if (!Require(Current.Stationary ? _entrySpeed < 0.03f : _entrySpeed >= (Current.Sprint ? 8.9f : 5.9f), "wrong approach speed: " + _entrySpeed)) return;
                Input.ActionPress(Current.Hand == HandSide.Right ? "aim_left" : "aim_right");
                _flick = true;
                GD.Print($"[moving-crossover-lateral] input role={_role} case={_case} entrySpeed={_entrySpeed:F3} origin={Current.Hand} heading={actor.Heading:F3} exit={Current.Exit} sprint={Current.Sprint}");
            }
        }
        Observe(actor);
        if (_finished) return;
        if (_localDone && (!_networked || (_role == "server" && _remoteDone)))
        {
            if (_case + 1 < _cases.Count) StartCase(_case + 1);
            else if (_networked)
            {
                RpcId(_clientId, MethodName.Complete);
                _exitAt = _elapsed + 0.5;
            }
            else Finish(0);
        }
    }

    private PlayerController Actor() => _players.GetNodeOrNull<PlayerController>(_ownerId.ToString());
    private void StartCase(int index)
    {
        ReleaseInput();
        _ball.PostAwardForHarness(_ownerId);
        EnterCase(index);
        if (_networked) RpcId(_clientId, MethodName.BeginCase, index);
    }
    private void EnterCase(int index)
    {
        ReleaseInput();
        _case = index; _caseTick = _driveTick = _stable = 0;
        _fixtureReady = _remoteFixtureReady = _driving = _localDone = _remoteDone = false;
        _flick = _exitApplied = _activeChanged = false;
        _arc = _swaps = _activeSamples = 0;
        _previousHand = Current.Hand; _lastPresentationTick = -1;
        _startupFirstSpeed = _startupLastSpeed = -1;
        _finalStartupFrame = -1;
        // Only before the case: establish an unobstructed inactive fixture.
        // On clients reset only the owner; the remote observer must receive
        // the production server envelope rather than a seeded display frame.
        foreach (var player in _players.GetChildren().OfType<PlayerController>())
        {
            if (_role == "client" && player.Name != Multiplayer.GetUniqueId().ToString()) continue;
            bool owner = player.Name == _ownerId.ToString();
            player.ResetContactStateForHarness(owner ? Origin : DefenderOrigin, owner ? Current.Heading : 0, Vector3.Zero);
            if (owner) player.SetHandSideForHarness(Current.Hand);
        }
        GD.Print($"[moving-crossover-lateral] case role={_role} index={index} hand={Current.Hand} heading={Current.Heading:F3} exit={Current.Exit} sprint={Current.Sprint} stationary={Current.Stationary}");
    }
    private void EnterDrive()
    {
        _driving = true; _driveTick = 0;
        var actor = Actor();
        _previousPosition = actor.DisplayRootPosition;
        _previousHand = actor.DisplayHandSide;
        _previousPhase = MovePhase.Inactive;
        _previousPhaseFrame = 0;
        _previousObservedTick = actor.PlayerPresentationTick;
        _previousPhysicsFrame = Engine.GetPhysicsFrames();
        if (OwnsActor)
        {
            Movement(HeadingMath.Forward(Current.Heading));
            if (Current.Sprint) Input.ActionPress("move_sprint");
        }
    }

    private void Observe(PlayerController actor)
    {
        bool historical = _role == "client" && _ownerId == 1;
        var frame = actor.AnimationDisplaySnapshot;
        var position = actor.DisplayRootPosition;
        var velocity = actor.DisplayVelocity;
        long tick = actor.PlayerPresentationTick;
        var phase = frame.Phase;
        if (historical)
        {
            var world = _ball.GetPresentationFrame();
            if (world == null || tick < 0) return;
            if (!Require(tick == world.Tick && frame.PresentationTick == tick, "mixed player/ball/animation presentation ticks")) return;
            if (tick == _lastPresentationTick) return; // explicit history hold
            if (!Require(tick > _lastPresentationTick, "presentation tick moved backward")) return;
            _lastPresentationTick = tick;
        }
        _last = $"case={_case} driveTick={_driveTick} historical={historical} playerTick={tick} ballTick={_ball.GetPresentationFrame()?.Tick ?? -1} move={frame.MoveId} phase={phase}/{frame.FrameInPhase} hand={Current.Hand}->{frame.Hand} heading={actor.DisplayHeading:F3} root={position} velocity={velocity} exit={Current.Exit}";
        if (_localDone) return;
        if (frame.Hand != _previousHand) { _swaps++; _previousHand = frame.Hand; }
        if (phase != MovePhase.Inactive)
        {
            if (!Require(frame.MoveId == "crossover", "input recognized wrong move: " + _last)) return;
            int bit = phase == MovePhase.Startup ? 1 : phase == MovePhase.Active ? 2 : 4;
            if ((_arc & bit) == 0)
            {
                if (!Require(_arc == bit - 1, "committed phase skipped/reordered: " + _last)) return;
                GD.Print("[moving-crossover-lateral] phase role=" + _role + " " + _last);
            }
            _arc |= bit;
        }
        if (phase == MovePhase.Startup)
        {
            if (_startupFirstSpeed < 0)
            {
                _entryHeading = actor.DisplayHeading;
                _startupFirstSpeed = Horizontal(velocity).Length();
            }
            _startupLastSpeed = Horizontal(velocity).Length();
            _startupLastForward = Horizontal(velocity).Dot(HeadingMath.Forward(_entryHeading));
            _finalStartupVelocity = velocity;
            _finalStartupFrame = frame.FrameInPhase;
            if (OwnsActor && !_exitApplied)
            {
                _exitApplied = true;
                Input.ActionRelease("aim_left"); Input.ActionRelease("aim_right");
                if (Current.Exit == "released" || Current.Exit == "neutral") Movement(Vector2.Zero);
                if (Current.Exit == "diagonal") Movement((HeadingMath.Forward(_entryHeading) +
                    HandStateResolver.BurstWorldDir(_entryHeading, Current.Hand == HandSide.Right ? -1 : 1)).Normalized());
            }
        }
        if (phase == MovePhase.Active)
        {
            if (_activeSamples == 0)
            {
                // Previous sample is final Startup AFTER its movement, hence
                // BEFORE the first Active integration. Keep all three ticks.
                _activeOrigin = _previousPosition;
                _activeVelocity = velocity;
                if (!Require(frame.FrameInPhase == 0, "missed first Active integration: " + _last)) return;
                if (!(_role == "client" && OwnsActor))
                {
                    int finalStartupFrame = Crossover.DefaultFrameData.StartupFrames - 1;
                    if (!Require(_previousPhase == MovePhase.Startup && _previousPhaseFrame == finalStartupFrame &&
                        _finalStartupFrame == finalStartupFrame, "measurement incomplete: final Startup survivor was not observed: " + _last)) return;
                    int sign = Current.Hand == HandSide.Right ? -1 : 1;
                    Vector2 expectedExit = Current.Exit == "held" ? HeadingMath.Forward(_entryHeading) :
                        Current.Exit == "diagonal" ? (HeadingMath.Forward(_entryHeading) +
                            HandStateResolver.BurstWorldDir(_entryHeading, sign)).Normalized() : Vector2.Zero;
                    // This oracle checks the production input plumbing against
                    // the case's intended exit, using its observed survivor.
                    // Pure math has separate tests; never substitute an assumed
                    // survivor or call expectedExit the actual captured input.
                    Vector3 expectedVelocity = CrossoverBurstMath.ComposeCrossoverActiveVelocity(
                        _finalStartupVelocity, _entryHeading, sign, expectedExit,
                        actor.BurstSpeed, actor.ForwardBurstScale, actor.ExitDeadzone);
                    GD.Print($"[moving-crossover-lateral] entry-oracle role={_role} case={_case} expectedExit={expectedExit} survivor={_finalStartupVelocity} expectedVelocity={expectedVelocity} actualVelocity={velocity}");
                    if (!Require(Horizontal(velocity - expectedVelocity).Length() < 0.08f,
                        $"first Active input-plumbing mismatch expectedExit={expectedExit} expectedVelocity={expectedVelocity}: " + _last)) return;
                }
                if (Current.Stationary && !Require(MathF.Abs(Horizontal(velocity).Length() - actor.BurstSpeed) < 0.08f,
                    "stationary neutral must preserve legacy lateral burst: " + _last)) return;
                if (OwnsActor)
                {
                    // Change the input AFTER the snapshot, through production
                    // Input, to catch an accidental continuously steered burst.
                    Movement(-HeadingMath.Forward(_entryHeading));
                    _activeChanged = true;
                }
            }
            // TickClientOwnPlayer documents a preexisting replay limitation:
            // ReconcileFromServer replays committed inputs through ordinary
            // Move(zero), so stale Startup snapshots can erase predicted lateral
            // velocity during Active. Observed first Active (-4.5,-11.167) then
            // (0,-1) with raw root X moving -2.075 back to -2. Do not turn that
            // known correction into a continuous-steering claim. Authority and
            // history must retain velocity/sign throughout all Active ticks.
            // Prediction proves its entry composition and the actual corrected
            // root's net cut by Recovery completion; Active-only travel is logged
            // separately and is not misrepresented as a predicted-window proof.
            bool predicting = _role == "client" && OwnsActor;
            if (!predicting)
            {
                bool consecutive = historical ? tick == _previousObservedTick + 1 :
                    Engine.GetPhysicsFrames() == _previousPhysicsFrame + 1;
                bool priorPhase = frame.FrameInPhase == 0 ?
                    _previousPhase == MovePhase.Startup && _previousPhaseFrame == Crossover.DefaultFrameData.StartupFrames - 1 :
                    _previousPhase == MovePhase.Active && _previousPhaseFrame == frame.FrameInPhase - 1;
                if (!Require(consecutive && priorPhase,
                    "measurement incomplete: Active integration needs consecutive final-Startup/Active samples: " + _last)) return;
                // Include the first integration and independently pin every
                // Active tick. Aggregate travel alone could hide one omitted
                // movement tick behind the other two ticks' displacement.
                Vector2 actualStep = Horizontal(position - _previousPosition);
                Vector2 expectedStep = Horizontal(velocity) / Engine.PhysicsTicksPerSecond;
                if (!Require((actualStep - expectedStep).Length() < 0.002f,
                    $"Active root integration mismatch expectedStep={expectedStep} actualStep={actualStep}: " + _last)) return;
            }
            if (_activeSamples > 0 && !predicting && !Require(Horizontal(velocity - _activeVelocity).Length() < 0.35f,
                "Active burst changed after entry snapshot: " + _last)) return;
            float sampleForward = Horizontal(velocity).Dot(HeadingMath.Forward(_entryHeading));
            float sampleLateral = Horizontal(velocity).Dot(HandStateResolver.BurstWorldDir(_entryHeading, Current.Hand == HandSide.Right ? -1 : 1));
            if ((!predicting || _activeSamples == 0) &&
                !Require(sampleLateral > (predicting ? 1f : 0.1f) && (Current.Stationary ? MathF.Abs(sampleForward) < 0.08f : sampleForward > 0.1f),
                    "Active velocity lost signed forward/lateral trajectory: " + _last)) return;
            _activeSamples++;
            _activeEnd = position;
        }
        if (_arc == 7 && phase == MovePhase.Inactive)
        {
            Vector2 displacement = Horizontal(_activeEnd - _activeOrigin);
            float forward = displacement.Dot(HeadingMath.Forward(_entryHeading));
            float lateral = displacement.Dot(HandStateResolver.BurstWorldDir(_entryHeading, Current.Hand == HandSide.Right ? -1 : 1));
            bool predicting = _role == "client" && OwnsActor;
            Vector2 committedDisplacement = Horizontal(position - _activeOrigin);
            float committedForward = committedDisplacement.Dot(HeadingMath.Forward(_entryHeading));
            float committedLateral = committedDisplacement.Dot(HandStateResolver.BurstWorldDir(_entryHeading, Current.Hand == HandSide.Right ? -1 : 1));
            if (!Require(_activeSamples == Crossover.DefaultFrameData.ActiveFrames, "must observe complete Active window: " + _last)) return;
            bool traveled = predicting ? committedLateral > 0.10f && committedForward > 0.02f :
                lateral > 0.10f && (Current.Stationary ? MathF.Abs(forward) < 0.02f : forward > 0.02f);
            if (!Require(traveled,
                $"root did not cut across drive line: activeLateral={lateral:F4} activeForward={forward:F4} committedLateral={committedLateral:F4} committedForward={committedForward:F4} " + _last)) return;
            if (!Require(_swaps == 1 && frame.Hand != Current.Hand, "must swap hand exactly once: " + _last)) return;
            // The same accepted zero-input reconciliation replay described
            // above can also erase a client's final predicted Startup velocity.
            // Authority/history prove the final gather survivor; prediction
            // still proves its first Active entry and actual corrected net cut.
            if (!Current.Stationary && !predicting && !Require(_startupLastForward > 0.1f && _startupLastSpeed < _startupFirstSpeed - 0.1f,
                $"gather must reduce but retain momentum first={_startupFirstSpeed:F3} last={_startupLastSpeed:F3}: " + _last)) return;
            if (OwnsActor && !Require(_flick && _exitApplied && _activeChanged, "input/snapshot controls were not exercised")) return;
            _localDone = true;
            if (OwnsActor) ReleaseInput();
            GD.Print($"[moving-crossover-lateral] proof role={_role} predicting={predicting} activeLateral={lateral:F4} activeForward={forward:F4} committedLateral={committedLateral:F4} committedForward={committedForward:F4} activeSamples={_activeSamples} swaps={_swaps} gather={_startupFirstSpeed:F3}->{_startupLastSpeed:F3} {_last}");
            if (_role == "client") RpcId(1, MethodName.ClientObserved, _case);
        }
        _previousPosition = position;
        _previousPhase = phase;
        _previousPhaseFrame = frame.FrameInPhase;
        _previousObservedTick = tick;
        _previousPhysicsFrame = Engine.GetPhysicsFrames();
    }

    private static Vector2 Horizontal(Vector3 value) => new(value.X, value.Z);
    // ActionPress drives the same Input singleton consumed by ReadInput and
    // SampleMoveInput; it does not call BeginCommittedMove or bypass legality.
    // https://docs.godotengine.org/en/4.7/classes/class_input.html#class-input-method-action-press
    private static void Movement(Vector2 value)
    {
        foreach (string action in new[] { "move_left", "move_right", "move_forward", "move_backward" }) Input.ActionRelease(action);
        if (value.X < -0.001f) Input.ActionPress("move_left", -value.X);
        if (value.X > 0.001f) Input.ActionPress("move_right", value.X);
        if (value.Y < -0.001f) Input.ActionPress("move_forward", -value.Y);
        if (value.Y > 0.001f) Input.ActionPress("move_backward", value.Y);
    }
    private static void ReleaseInput()
    {
        Movement(Vector2.Zero);
        foreach (string action in new[] { "aim_left", "aim_right", "move_sprint" }) Input.ActionRelease(action);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ClientReady()
    { if (Require(_role == "server" && Multiplayer.GetRemoteSenderId() == _clientId && !_peerReady, "invalid readiness sender")) _peerReady = true; }
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void BeginCase(int index)
    { if (Require(_role == "client" && Multiplayer.GetRemoteSenderId() == 1 && index == _case + 1 && index < _cases.Count, "invalid case sender/order")) EnterCase(index); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ClientFixtureReady(int index)
    { if (Require(_role == "server" && Multiplayer.GetRemoteSenderId() == _clientId && index == _case && !_remoteFixtureReady, "invalid fixture-ready sender/case")) _remoteFixtureReady = true; }
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void BeginDrive(int index)
    { if (Require(_role == "client" && Multiplayer.GetRemoteSenderId() == 1 && index == _case && _fixtureReady && !_driving, "invalid drive sender/state")) EnterDrive(); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ClientObserved(int index)
    { if (Require(_role == "server" && Multiplayer.GetRemoteSenderId() == _clientId && index == _case && !_remoteDone, "invalid proof sender/case")) _remoteDone = true; }
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Complete()
    { if (Require(_role == "client" && Multiplayer.GetRemoteSenderId() == 1 && _case == _cases.Count - 1 && _localDone, "completion before both proofs")) Finish(0); }
    private bool Require(bool condition, string diagnostic)
    {
        if (condition) return true;
        GD.PrintErr($"[moving-crossover-lateral] FAIL role={_role} scenario={_scenario}: {diagnostic}");
        Finish(1); return false;
    }
    private void Finish(int code)
    {
        if (_finished) return;
        _finished = true; ReleaseInput();
        GD.Print($"[moving-crossover-lateral] {_role} RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
