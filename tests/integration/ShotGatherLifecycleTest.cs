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
            var ballMesh = new MeshInstance3D
            {
                Name = "MeshInstance3D", Position = new Vector3(.2f, .3f, .4f),
                Mesh = new SphereMesh { Radius = .12f, Height = .24f, Material = new StandardMaterial3D() }
            };
            _ball.AddChild(ballMesh);
            AddChild(_ball);
            _ball.SetPhysicsProcess(false);
            Require(_ball.RenderedWorldPosition.DistanceTo(_ball.GlobalPosition) > .1f,
                "rendered-position premise must discriminate the real ball mesh from its live root");
            Near(_ball.RenderedWorldPosition, ballMesh.GlobalPosition, "production Ready must resolve the actual ball mesh");
            Require(!_ball.LifecycleIsServer && _own.SimulationIsLocalPlayer && !_remote.SimulationIsLocalPlayer,
                "ball/client/remote role premise");

            StaleDribbleAndFake();
            FakeToNeutralPresentation();
            StaleDribbleAtRelease();
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

    private void FakeToNeutralPresentation()
    {
        BeginDribbleShot();
        AdvanceTo(MovePhase.Startup, 11);
        Vector3 raised = _ball.GlobalPosition;
        int releases = _ball.SuccessfulShotReleasesForHarness;
        Require(_own.FeintForHarness(), "last-legal fake must actually be accepted");
        AdvanceTo(MovePhase.Recovery, 19);
        Vector3 station = _ball.GlobalPosition, root = _own.GlobalPosition;
        HandSide hand = _own.HandSide;
        Vector3 holderOffset = _own.ReboundVisualOffset;
        Require(raised.DistanceTo(station) > .25f && holderOffset.Length() < PositionTolerance,
            "fake handoff premise needs a distinct stale raised center and an uncorrected holder");
        Near(_ball.RenderedWorldPosition, station + holderOffset, "last fake-Recovery frame must share the holder correction");

        // The delayed raised packet arrives exactly when the machine removes its
        // JumpShot. Physical attachment is unchanged; only an independent stale
        // ball-mesh offset could create a jump across this boundary.
        _ball.LifecycleSnapshot(BallState.Held, _ownerId, raised, true);
        _own.LifecycleAdvanceMachine();
        Require(_own.PhaseForHarness == MovePhase.Inactive && _own.SimulationShot == null,
            "neutral handoff must remove the shot context before Ball processes the packet");
        TickBall();
        Require(_ball.BallCorrectionForHarness.Received && _ball.BallCorrectionForHarness.State == "Held",
            "neutral handoff must traverse actual stale Held reconciliation");
        Near(_ball.GlobalPosition, station, "fake-to-neutral handoff must retain the physical Held station");
        Near(_own.GlobalPosition, root, "fake-to-neutral handoff may not move the holder root");
        Near(_own.ReboundVisualOffset, holderOffset, "fake-to-neutral handoff may not change holder correction");
        Near(_ball.RenderedWorldPosition, station + holderOffset,
            "first neutral Held frame must retain the holder presentation frame after fake context disappears");

        Node3D holderMesh = _own.GetNode<Node3D>(_own.VisualRoot);
        Vector3 meshPosition = holderMesh.GlobalPosition;
        Vector3 correction = new(.18f, .04f, -.11f);
        holderMesh.GlobalPosition += correction;
        try
        {
            Near(_own.ReboundVisualOffset, holderOffset + correction,
                "nonzero correction premise must come from the actual holder visual root");
            for (int tick = 0; tick < 4; tick++)
            {
                _ball.LifecycleSnapshot(BallState.Held, _ownerId, raised + new Vector3(.1f * tick, 0, 0), true);
                TickBall();
                Require(_ball.BallCorrectionForHarness.Received && _ball.State == BallState.Held &&
                    _ball.StateMachine.HolderPeerId == _ownerId && _own.PhaseForHarness == MovePhase.Inactive,
                    "repeated neutral stale packets must retain live ownership and phase");
                Near(_ball.GlobalPosition, station, "repeated neutral stale packets may not move physical attachment");
                Near(_own.GlobalPosition, root, "repeated neutral stale packets may not move the holder root");
                Near(_own.ReboundVisualOffset, holderOffset + correction, "holder correction must remain independent of ball reconciliation");
                Near(_ball.RenderedWorldPosition, station + holderOffset + correction,
                    "ordinary neutral Held ball must consistently share the actual holder correction");
                Require(_own.HandSide == hand && _ball.SuccessfulShotReleasesForHarness == releases,
                    "fake handoff presentation may not change hand identity or emit a shot");
            }
        }
        finally { holderMesh.GlobalPosition = meshPosition; }
        GD.Print("[shot-gather-lifecycle] fake-to-neutral Held presentation/no stale mesh offset PASS");
    }

    private void StaleDribbleAtRelease()
    {
        BeginDribbleShot();
        AdvanceTo(MovePhase.Startup, 17);
        int before = _ball.SuccessfulShotReleasesForHarness;
        Vector3 endpoint = _own.GlobalPosition + new Basis(Vector3.Up, _own.Heading) * ShotGatherProfile.Target(18);
        // Advance the real machine without ticking Ball: the delayed pre-Begin
        // packet must be reconciled BEFORE this first-Active release is processed.
        _own.LifecycleAdvanceMachine();
        Require(_own.PhaseForHarness == MovePhase.Active && _own.FrameInPhaseForHarness == 0,
            "stale Dribbling release fixture must reach actual first Active0");
        _ball.LifecycleSnapshot(BallState.Dribbling, _ownerId, _own.GlobalPosition + new Vector3(.4f, -.85f, .5f), false);
        TickBall();
        Require(_ball.BallCorrectionForHarness.Received && _ball.BallCorrectionForHarness.State == "Dribbling",
            "first-Active stale Dribbling must traverse production reconciliation");
        Require(_ball.State == BallState.InFlight && _own.LifecycleGather.Released &&
            _ball.SuccessfulShotReleasesForHarness == before + 1, "first Active0 must release exactly once after stale Dribbling");
        Near(_ball.LastReleasePositionForHarness, endpoint, "stale Dribbling release must use model endpoint, not bounce center");
        Near(_ball.GlobalPosition, endpoint, "release callback must leave the arc at its endpoint origin");
        Near(_own.LifecycleGather.ReleaseOrigin, endpoint, "released context must retain the endpoint origin");

        for (int step = 1; step <= 2; step++)
        {
            Vector3 position = _ball.GlobalPosition, velocity = _ball.FlightVelocityForHarness;
            Vector3 nextVelocity = velocity + Vector3.Down * _ball.Gravity * (float)Dt;
            Vector3 nextPosition = position + .5f * (velocity + nextVelocity) * (float)Dt;
            _own.LifecycleAdvanceMachine();
            TickBall();
            Require(_own.PhaseForHarness == MovePhase.Active && _own.FrameInPhaseForHarness == step &&
                _ball.State == BallState.InFlight && _ball.FlightStepForHarness.Advanced,
                $"post-correction flight step {step} must actually advance during the next Active frame");
            Near(_ball.FlightStepForHarness.Position, position, $"flight step {step} must start on the existing arc");
            Near(_ball.GlobalPosition, nextPosition, $"flight step {step} must integrate from the endpoint arc");
            Near(_ball.FlightVelocityForHarness, nextVelocity, $"flight step {step} must retain gravity integration");
            Require(_ball.SuccessfulShotReleasesForHarness == before + 1, "later Active frames may not release again");
        }

        _own.LifecycleAdvanceMachine(); // Active3, after the release pulse has ended
        _own.GlobalPosition += new Vector3(1.25f, 0, -.75f);
        _ball.LifecycleSnapshot(BallState.Dribbling, _ownerId, _own.GlobalPosition + new Vector3(-.4f, -.85f, .5f), false);
        TickBall();
        Require(_ball.BallCorrectionForHarness.State == "Dribbling" && _ball.State == BallState.Held &&
            _own.LifecycleGather.Released && !_ball.FlightStepForHarness.Advanced,
            "post-release stale Dribbling must replay cradle without advancing or reviving the release");
        Near(_ball.GlobalPosition, endpoint, "post-release stale Dribbling must preserve absolute origin despite root motion");
        Require(_ball.SuccessfulShotReleasesForHarness == before + 1, "post-release stale Dribbling may not release twice");
        GD.Print("[shot-gather-lifecycle] stale Dribbling first-Active endpoint/flight/no-repeat PASS");
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
