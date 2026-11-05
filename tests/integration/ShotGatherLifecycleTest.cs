using System;
using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Networking;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

/// <summary>
/// Actual ENet client role, with explicitly synthetic snapshot ordering after
/// connection. Paused nodes are stepped through their production ball callback;
/// this proves lifecycle glue, not network latency, rendering or historical replay.
/// </summary>
public partial class ShotGatherLifecycleTest : Node3D
{
    private string _role;
    private Node3D _players;
    private PlayerController _own, _remote;
    private BallController _ball;
    private bool _registered, _proofSent, _finished;
    private int _ownerId, _assertions;
    private ulong _serverQuitAfter;
    private double _elapsed;
    private double Dt => 1.0 / Engine.PhysicsTicksPerSecond;
    private const float PositionTolerance = 0.00001f;

    public override void _Ready()
    {
        try
        {
            string[] args = OS.GetCmdlineUserArgs();
            _role = HarnessArgs.ReadArg(args, "--harness-role", "server");
            Require(_role is "server" or "client", "unknown role");
            int port = int.Parse(HarnessArgs.ReadArg(args, "--harness-port", "28442"));
            _players = GetNode<Node3D>("Players");
            var network = GetNode<NetworkManager>("NetworkManager");
            network.ConnectionFailed += () => Fail("production connection failed");
            if (_role == "server")
            {
                network.ServerStarted += _ => GD.Print("[shot-gather-lifecycle] READY server");
                network.HostGame(port);
            }
            else network.JoinGame("127.0.0.1", port);
        }
        catch (Exception error) { Fail(error.ToString()); }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        if (_elapsed > 30) { Fail("bounded 30-second timeout"); return; }
        try
        {
            foreach (var player in _players.GetChildren().OfType<PlayerController>())
            {
                player.SetPhysicsProcess(false);
                player.SetProcess(false);
                player.GetNode<AnimationTree>("AnimationTree").Active = false;
            }
            if (_role == "server")
            {
                if (_serverQuitAfter > 0 && Engine.GetPhysicsFrames() >= _serverQuitAfter)
                    Pass();
                return;
            }
            if (_registered || _players.GetChildCount() != 2 ||
                Multiplayer.MultiplayerPeer.GetConnectionStatus() != MultiplayerPeer.ConnectionStatus.Connected)
                return;
            _registered = true;
            RpcId(1, MethodName.Register);
        }
        catch (Exception error) { Fail(error.ToString()); }
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Register()
    {
        try
        {
            Require(_role == "server" && Multiplayer.IsServer(), "registration reached a non-authority");
            int sender = Multiplayer.GetRemoteSenderId();
            Require(sender > 1 && _players.GetChildCount() == 2 &&
                _players.GetNodeOrNull(sender.ToString()) != null, "two real replicated players must exist");
            _ownerId = sender;
            RpcId(sender, MethodName.RunProof);
        }
        catch (Exception error) { Fail(error.ToString()); }
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RunProof()
    {
        try
        {
            Require(_role == "client" && !Multiplayer.IsServer() && Multiplayer.GetRemoteSenderId() == 1,
                "proof must run on a genuine connected client");
            _ownerId = Multiplayer.GetUniqueId();
            Require(_ownerId > 1 && _players.GetChildCount() == 2, "owner identity premise");
            _own = _players.GetNode<PlayerController>(_ownerId.ToString());
            _remote = _players.GetNode<PlayerController>("1");
            _ball = new BallController { Name = "Ball", Players = _players };
            AddChild(_ball);
            _ball.SetPhysicsProcess(false);
            Require(!_ball.LifecycleIsServer && _own.SimulationIsLocalPlayer && !_remote.SimulationIsLocalPlayer,
                "ball/client/remote role premise");

            StaleDribbleAndFake();
            ReleasedHeldCorrections();
            ActualLoss(BallState.Loose);
            ActualLoss(BallState.Held);
            ConfirmationIdentity();
            AwardAndReset();
            Rejection();
            RemoteReleaseIdentity();

            Require(_assertions >= 70, "all lifecycle assertions must execute");
            _proofSent = true;
            GD.Print($"[shot-gather-lifecycle] client assertions={_assertions}; synthetic lifecycle proof complete");
            // Wait for the authority's acknowledgement so process teardown cannot
            // swallow the reliable completion message before it is delivered.
            RpcId(1, MethodName.Completed, _assertions);
        }
        catch (Exception error) { Fail(error.ToString()); }
    }

    private void BeginDribbleShot()
    {
        _own.LifecycleReset(_ball, new Vector3(0, .998f, 6));
        _remote.LifecycleReset(_ball, new Vector3(4, .998f, 10));
        _ball.LifecycleResetBall(_ownerId);
        TickBall();
        Require(_ball.State == BallState.Held && !_ball.HasDribbled && _ball.LifecycleIsLocalHolder,
            "fresh local Held possession premise");
        _ball.TryStartDribble(_ownerId);
        Require(_ball.State == BallState.Dribbling, "fresh possession must actually start dribbling");
        TickBall();
        Vector3 incoming = _ball.GlobalPosition;
        Require(_own.BeginJumpShotForHarness(), "production BeginCommittedMove must accept shot");
        Require(_own.LifecycleGather.Valid && _own.PhaseForHarness == MovePhase.Startup &&
            _own.FrameInPhaseForHarness == 0 && _own.LifecycleToken > 1, "accepted shot context premise");
        Require(_ball.State == BallState.Held && _ball.HasDribbled, "Begin must cradle and spend dribble");
        Near(_own.GlobalPosition + _own.LifecycleGather.EntryOffset, incoming, "entry must capture actual center");
    }

    private void StaleDribbleAndFake()
    {
        BeginDribbleShot();
        AdvanceTo(MovePhase.Startup, 3);
        ShotGatherState entry = _own.LifecycleGather;
        int releases = _ball.SuccessfulShotReleasesForHarness;
        _ball.LifecycleSnapshot(BallState.Dribbling, _ownerId, new Vector3(.4f, .15f, 6.5f), false);
        TickBall();
        Require(_ball.BallCorrectionForHarness.Received && _ball.BallCorrectionForHarness.State == "Dribbling",
            "stale Dribbling must traverse production reconciliation");
        Require(_ball.State == BallState.Held && _ball.HasDribbled && _own.LifecycleGather.Valid,
            "Startup stale Dribbling must replay cradle and preserve spent hold");
        Require(_own.LifecycleGather.EntryOffset == entry.EntryOffset, "stale packet must not restart gather anchor");
        Require(_own.FeintForHarness(), "interior legal fake must succeed");
        Require(_own.PhaseForHarness == MovePhase.Recovery && _own.FrameInPhaseForHarness == 12 &&
            _own.LifecycleGather.Faking, "fake must enter existing offset Recovery");
        Vector3 reached = _ball.GlobalPosition;
        _own.LifecycleAdvanceMachine();
        _ball.LifecycleSnapshot(BallState.Dribbling, _ownerId, new Vector3(-.4f, .12f, 6.5f), false);
        TickBall();
        Require(_ball.BallCorrectionForHarness.State == "Dribbling" && _ball.State == BallState.Held &&
            _ball.HasDribbled && _own.LifecycleGather.Faking, "fake Recovery must replay cradle");
        Near(_ball.GlobalPosition, reached, "fake first post-Feint frame must retain last reached center");
        Require(_ball.SuccessfulShotReleasesForHarness == releases, "fake may not emit a release");
        GD.Print("[shot-gather-lifecycle] stale Startup/fake-Recovery cradle PASS");
    }

    private void ReleasedHeldCorrections()
    {
        BeginDribbleShot();
        int before = _ball.SuccessfulShotReleasesForHarness;
        AdvanceTo(MovePhase.Active, 0);
        Require(_ball.State == BallState.InFlight && _own.LifecycleGather.Released &&
            _ball.SuccessfulShotReleasesForHarness == before + 1, "real first-Active predicted release premise");
        Vector3 origin = _ball.LastReleasePositionForHarness;
        Near(_own.LifecycleGather.ReleaseOrigin, origin, "released context must capture actual arc origin");
        AdvanceTo(MovePhase.Active, 1); // clear JustEnteredActive before injecting a later packet
        CorrectHeld(origin, before, "Active");
        AdvanceTo(MovePhase.Recovery, 2);
        _own.GlobalPosition += new Vector3(1.25f, 0, -.75f);
        CorrectHeld(origin, before, "Recovery after body motion");
        Require(_own.PhaseForHarness == MovePhase.Recovery && _own.FrameInPhaseForHarness == 2,
            "ball correction may not re-phase the committed machine");
        GD.Print("[shot-gather-lifecycle] release/stale-Held Active+Recovery absolute origin PASS");
    }

    private void CorrectHeld(Vector3 origin, int before, string label)
    {
        Vector3 lowStation = _own.GlobalPosition + new Vector3(.4f, 0, .5f);
        Require(lowStation.DistanceTo(origin) > .25f, "stale low station must discriminate origin attachment");
        _ball.LifecycleSnapshot(BallState.Held, _ownerId, lowStation, true);
        TickBall();
        Require(_ball.BallCorrectionForHarness.Received && _ball.BallCorrectionForHarness.State == "Held" &&
            _ball.State == BallState.Held, label + ": discrete Held snapshot must still apply");
        Near(_ball.GlobalPosition, origin, label + ": preserve absolute released origin");
        Require(_ball.SuccessfulShotReleasesForHarness == before + 1 && _own.LifecycleGather.Released,
            label + ": correction may not release twice or erase terminal context");
    }

    private void ActualLoss(BallState state)
    {
        BeginDribbleShot();
        AdvanceTo(MovePhase.Startup, 4);
        ShotGatherState valid = _own.LifecycleGather;
        int token = _own.LifecycleToken, counter = _own.FrameInPhaseForHarness;
        int holder = state == BallState.Held ? 1 : 0;
        _ball.LifecycleSnapshot(state, holder, new Vector3(4, 4, 8), false);
        TickBall();
        Require(_ball.BallCorrectionForHarness.State == state.ToString() && _ball.State == state,
            "actual loss must not be hidden by an immediate recovery");
        Require(!_own.LifecycleGather.Valid && _own.PhaseForHarness == MovePhase.Startup &&
            _own.FrameInPhaseForHarness == counter, "loss must clear attachment without refunding commitment");
        _own.LifecycleDeliverGather(token, valid);
        Require(!_own.LifecycleGather.Valid, "late valid confirmation may not resurrect a lost possession");
        GD.Print($"[shot-gather-lifecycle] loss={state} reset/no timeline abort PASS");
    }

    private void ConfirmationIdentity()
    {
        BeginDribbleShot();
        AdvanceTo(MovePhase.Startup, 4);
        int token = _own.LifecycleToken, counter = _own.FrameInPhaseForHarness;
        ShotGatherState valid = _own.LifecycleGather;
        _own.LifecycleDeliverGather(token - 1, default);
        Require(_own.LifecycleGather.Valid && _own.LifecycleToken == token,
            "older Begin invalidation may not clear this newer shot");
        _own.LifecycleDeliverGather(token, default);
        Require(!_own.LifecycleGather.Valid && _ball.State == BallState.Held &&
            _ball.StateMachine.HolderPeerId == _ownerId, "matching invalidation must clear even same-holder Held");
        Require(_own.PhaseForHarness == MovePhase.Startup && _own.FrameInPhaseForHarness == counter,
            "matching gather invalidation may not abort or rewind the move");
        _own.LifecycleDeliverGather(token, valid);
        Require(!_own.LifecycleGather.Valid, "late matching valid context may not resurrect invalidated gather");
        GD.Print("[shot-gather-lifecycle] matching/older invalidation and no resurrection PASS");
    }

    private void AwardAndReset()
    {
        BeginDribbleShot();
        AdvanceTo(MovePhase.Startup, 4);
        _ball.PostAwardForHarness(_ownerId);
        Require(!_own.LifecycleGather.Valid && !_ball.HasDribbled && _ball.State == BallState.Held,
            "same-holder production award must clear context and spent flag");
        Require(_own.PhaseForHarness == MovePhase.Startup && _own.FrameInPhaseForHarness == 4,
            "award may not refund committed time");
        _ball.TryStartDribble(_ownerId);
        Require(_ball.State == BallState.Dribbling, "fresh award must restore ball's dribble admission");

        BeginDribbleShot();
        AdvanceTo(MovePhase.Startup, 4);
        _ball.ResetPivotPresentationForHarness(); // real disconnect/reconnect reset callback
        Require(!_own.LifecycleGather.Valid && _own.PhaseForHarness == MovePhase.Startup &&
            _own.FrameInPhaseForHarness == 4, "disconnect-reset callback must clear gather, not move timeline");
        Require(Multiplayer.MultiplayerPeer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected,
            "reset-callback fixture must retain real client role for remaining checks");
        GD.Print("[shot-gather-lifecycle] same-holder award/fresh dribble and disconnect-reset callback PASS");
    }

    private void Rejection()
    {
        BeginDribbleShot();
        AdvanceTo(MovePhase.Startup, 4);
        int token = _own.LifecycleToken;
        _own.LifecycleReject(token - 1);
        Require(_own.LifecycleGather.Valid && _own.PhaseForHarness == MovePhase.Startup,
            "older request rejection may not clear newer shot");
        _own.LifecycleReject(token);
        Require(!_own.LifecycleGather.Valid && _own.PhaseForHarness == MovePhase.Inactive,
            "matching request rejection must remove move-owned gather context");
        _ball.PostAwardForHarness(_ownerId);
        _ball.TryStartDribble(_ownerId);
        Require(_ball.State == BallState.Dribbling && !_ball.HasDribbled,
            "fresh possession after rejection must remain dribbleable");
        GD.Print("[shot-gather-lifecycle] matching rejection/older rejection exclusion PASS");
    }

    private void RemoteReleaseIdentity()
    {
        const long tick = 100;
        Vector3 released = new(2, 2.2f, 5);
        var player = new PlayerPresentationFrame(1, _remote.GlobalPosition, Vector3.Zero, 0,
            HandSide.Right, MovePhase.Active, 0, "jumpshot", 0,
            false, false, false, false, false, false, false, "JumpshotActive", 0);
        foreach (bool matches in new[] { false, true })
        {
            var ball = new BallPresentationFrame(released, BallState.InFlight, 0, false, 0, false, 0, 0,
                false, matches ? 1 : _ownerId);
            _ball.LifecyclePresentation(new WorldPresentationFrame(tick, new[] { player }, ball));
            _remote.LifecycleDisplay(tick + 1);
            Require(!_ball.TryShotDisplayTarget(_remote, out _), "different display tick must reject binding");
            _remote.LifecycleDisplay(tick);
            bool bound = _ball.TryShotDisplayTarget(_remote, out var target);
            Require(bound == matches, "remote Active0 binding must require same-envelope shooter identity");
            Require(_ball.GetPresentationFrame()?.Tick == tick, "actual history must select the requested tick");
            if (matches) Near(target, released, "matching remote target must be that envelope's ball center");
        }
        GD.Print("[shot-gather-lifecycle] remote same-tick shooter identity control/success PASS");
    }

    private void AdvanceTo(MovePhase phase, int frame)
    {
        for (int i = 0; i < 80; i++)
        {
            if (_own.PhaseForHarness == phase && _own.FrameInPhaseForHarness == frame) return;
            Require(_own.PhaseForHarness != MovePhase.Inactive, "move ended before requested lifecycle boundary");
            _own.LifecycleAdvanceMachine();
            TickBall();
        }
        throw new InvalidOperationException($"did not reach {phase}/{frame}");
    }

    private void TickBall() => _ball._PhysicsProcess(Dt);
    private void Near(Vector3 actual, Vector3 expected, string message)
        => Require(actual.DistanceTo(expected) <= PositionTolerance,
            $"{message}: actual={actual} expected={expected}");
    private void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); _assertions++; }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Completed(int assertions)
    {
        try
        {
            Require(_role == "server" && Multiplayer.GetRemoteSenderId() == _ownerId && assertions >= 70,
                "completion must come from the connected proof owner with all cases executed");
            RpcId(_ownerId, MethodName.Accepted);
            _serverQuitAfter = Engine.GetPhysicsFrames() + 5;
        }
        catch (Exception error) { Fail(error.ToString()); }
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Accepted()
    {
        if (!_proofSent || Multiplayer.GetRemoteSenderId() != 1) { Fail("unexpected proof acknowledgement"); return; }
        Pass();
    }

    private void Pass()
    {
        _finished = true;
        GD.Print($"[shot-gather-lifecycle] {_role} RESULT: PASS (exit 0)");
        GetTree().Quit(0);
    }
    private void Fail(string message)
    {
        if (_finished) return;
        _finished = true;
        GD.PrintErr($"[shot-gather-lifecycle] FAIL {_role}: {message}");
        GetTree().Quit(1);
    }
}
