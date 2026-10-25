using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Networking;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

/// <summary>Dedicated authority, genuine predicting ENet owner, separate observer.
/// Fixture setup is explicit; requests, simulation, correction and presentation
/// use production paths. Synthetic message-ordering proof is separately labelled.</summary>
public partial class NetShotHeadingTest : Node3D
{
    private string _role, _mutation;
    private Node3D _players;
    private PlayerController _driver, _observer, _own, _other;
    private BallController _ball;
    private int _driverId, _observerId, _stage, _tick, _stageBaseline, _corrections, _replaySamples;
    private int _turnSamples, _startup, _active, _recovery, _holds, _resets;
    private int _lastReset, _normalReleases;
    private long _lastPresentation = -1;
    private float _initialTarget, _largestCorrection, _largestReplayError;
    private float _lastDisplayHeading;
    private double _lastAnimationTime;
    private bool _registered, _configured, _running, _finished, _begun, _targetRepair, _feinted;
    private bool _rejectObserved;
    private double _elapsed;
    private readonly HashSet<int> _ready = new(), _done = new();
    private double Dt => 1.0 / Engine.PhysicsTicksPerSecond;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs();
        _role = HarnessArgs.ReadArg(args, "--harness-role", "server");
        _mutation = HarnessArgs.ReadArg(args, "--mutation", "none");
        int port = int.Parse(HarnessArgs.ReadArg(args, "--harness-port", "28428"));
        _players = GetNode<Node3D>("Players");
        var network = GetNode<NetworkManager>("NetworkManager");
        network.ConnectionFailed += () => Finish(false, "production network startup/connection failed");
        if (_role == "server")
        {
            // READY follows the production signal emitted after successful
            // ENet bind and peer installation, never the attempted startup.
            network.ServerStarted += _ => GD.Print("[shot-net] READY server");
            network.StartDedicatedServer(port);
        }
        else network.JoinGame("127.0.0.1", port);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        if (_elapsed > 40) { Finish(false, "40 second bounded timeout"); return; }
        try
        {
            foreach (var player in _players.GetChildren().OfType<PlayerController>())
            { player.SetPhysicsProcess(false); player.SetProcess(false); }
            if (_role != "server" && !_registered && _players.GetChildCount() == 2
                && Multiplayer.MultiplayerPeer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected)
            { _registered = true; RpcId(1, MethodName.Register, _role); }
            if (!_running) return;
            _tick++;
            if (_role == "server") ServerStep(); else ClientStep();
        }
        catch (Exception error) { Finish(false, error.ToString()); }
    }

    private void ServerStep()
    {
        _driver.ShotNetTickServer(Dt); _observer.ShotNetTickServer(Dt);
        _ball._PhysicsProcess(Dt);
        if (_driver.ShotNetPhase == MovePhase.Startup && _driver.ShotNetToken > 0)
        {
            _turnSamples++;
            Require(_driver.Heading > 0f, "authority must actually turn during Startup");
            if (_stage == 0 && _driver.ShotNetFrame == 8)
                GD.Print("[shot-net] observer-delivery-pause 100ms");
        }
        if (_tick != 150) return;
        int releases = _ball.SuccessfulShotReleasesForHarness - _stageBaseline;
        Require(releases == (_stage == 0 ? 1 : 0), $"stage {_stage} actual authoritative releases={releases}");
        if (_stage == 0) _normalReleases = releases;
        GD.Print($"[shot-net] authority stage={_stage} successfulReleases={releases} heading={_driver.Heading} target={_driver.ShotNetTarget} ack={_driver.SprintAckForHarness}");
        Rpc(MethodName.StageVerdict, _stage, releases);
        if (_stage < 3)
        { int next = _stage + 1; SetStage(next); Rpc(MethodName.SetStage, next); }
        else
        {
            Require(_normalReleases == 1 && _turnSamples > 20, "authority release/turn controls must execute");
            Rpc(MethodName.ValidateClients); _running = false;
        }
    }

    private void ClientStep()
    {
        bool owner = _role == "driver";
        foreach (string action in new[] { _ball.ShootAction, "move_feint", "move_forward", "move_right", "move_post", "move_protect", "move_sprint" })
            Input.ActionRelease(action);
        if (owner && _tick == 20)
        {
            // Deliberately disagree with authority's Begin position. The server
            // derives its own target; its result must repair this predicted aim.
            Input.ActionPress(_ball.ShootAction);
        }
        // Interior legal-window requests leave reliable transport margin.
        // Exact earliest3/latest11 bounds are pinned by JumpShotHeadingTest;
        // this network fixture proves correction across accepted feints.
        if (owner && _stage is 1 or 2 && !_feinted && _own.ShotNetPhase == MovePhase.Startup
            && _own.ShotNetFrame >= (_stage == 1 ? 4 : 7))
        {
            Input.ActionPress("move_feint"); _feinted = true;
            GD.Print($"[shot-net] {(_stage == 1 ? "early-network-feint4" : "later-network-feint7")} requested frame={_own.ShotNetFrame}");
        }
        if (owner)
        {
            _own.ShotNetTickOwner(Dt, correction =>
            {
                _corrections++; _replaySamples += correction.Samples;
                _largestCorrection = Math.Max(_largestCorrection, correction.Change);
                _largestReplayError = Math.Max(_largestReplayError, correction.Error);
                Require(correction.Error <= 1e-5f, $"fixed-step heading replay mismatch={correction.Error}");
                Require(correction.MachineStable && correction.ReleasesStable,
                    "production correction must not double-tick the live machine or re-emit payload");
            }, perturbBegin: _tick is 20 or 21);
            if (!_begun && _own.ShotNetPhase == MovePhase.Startup && _own.ShotNetToken > 0)
            {
                _begun = true; _initialTarget = _own.ShotNetTarget;
                GD.Print($"[shot-net] predicted-begin stage={_stage} tick={_tick} token={_own.ShotNetToken} target={_initialTarget} position={_own.GlobalPosition}");
            }
            if (_begun && _own.ShotNetToken > 0 && _own.ShotNetAuthorityToken == _own.ShotNetToken)
            {
                _targetRepair |= Math.Abs(PlayerController.ShotNetWrap(_initialTarget - _own.ShotNetAuthorityTarget)) > .01f
                    && Math.Abs(PlayerController.ShotNetWrap(_own.ShotNetTarget - _own.ShotNetAuthorityTarget)) < 1e-6f;
            }
            // A naturally completed phantom shot must not count as a rejected
            // Begin. Require the explicit result before its first Active tick.
            if (_stage == 3 && _begun && _tick > 25 && _tick < 21 + JumpShot.DefaultFrameData.StartupFrames
                && _own.ShotNetPhase == MovePhase.Inactive)
                _rejectObserved = true;
            if (_feinted && _own.ShotNetPhase == MovePhase.Recovery)
                Require(Math.Abs(PlayerController.ShotNetWrap(_own.Heading - _own.ShotNetBeforePredictionHeading)) < 1e-6f
                    && Math.Abs(_own.Heading) > .04f
                    && Math.Abs(PlayerController.ShotNetWrap(_own.Heading - _own.ShotNetTarget)) > 1f,
                    "fake Recovery retains this tick's corrected partial heading, not target/Begin yaw");
        }
        else _own.ShotNetTickOwner(Dt, _ => { });
        _other.ShotNetTickRemote();
        _ball._PhysicsProcess(Dt);
        if (!owner) ObservePresentation();
    }

    private void ObservePresentation()
    {
        if (_other.ShotNetDisplayed is not { } player || player.MoveId != "jumpshot") return;
        var animation = _other.AnimationDisplaySnapshot;
        Require(_other.PlayerPresentationTick == _ball.RenderedPresentationTick
            && _other.PlayerPresentationTick == animation.PresentationTick,
            "remote heading/pose/ball must consume one ADR-0027 server tick");
        Require(Math.Abs(PlayerController.ShotNetWrap(_other.DisplayHeading - player.Heading)) < 1e-6f
            && Math.Abs(PlayerController.ShotNetWrap(_other.ShotNetMeshYaw - player.Heading)) < 1e-5f,
            "remote visual yaw must equal same-envelope authoritative gameplay heading");
        Require(animation.Phase == player.Phase && animation.FrameInPhase == player.FrameInPhase,
            "remote pose phase/counter must match heading envelope");
        string expected = player.Phase == MovePhase.Active && player.MoveParam != 0 ? "FadeawayActive"
            : player.Phase == MovePhase.Inactive ? "" : "Jumpshot" + player.Phase;
        Require(player.Phase == MovePhase.Inactive || _other.ActiveAnimNodeForHarness == expected,
            $"actual remote AnimationTree expected {expected}, got {_other.ActiveAnimNodeForHarness}");
        if (player.Phase == MovePhase.Startup) _startup++;
        if (player.Phase == MovePhase.Active) _active++;
        if (player.Phase == MovePhase.Recovery) _recovery++;
        if (_lastPresentation == _other.PlayerPresentationTick)
        {
            _holds++;
            Require(_other.DisplayHeading == _lastDisplayHeading
                && Math.Abs(_other.ShotNetAnimationTime - _lastAnimationTime) < 1e-9,
                "coherent underflow hold must freeze actual yaw and AnimationTree playback time");
        }
        if (_ball.PresentationResyncCount != _lastReset) _resets++;
        _lastReset = _ball.PresentationResyncCount; _lastPresentation = _other.PlayerPresentationTick;
        _lastDisplayHeading = _other.DisplayHeading; _lastAnimationTime = _other.ShotNetAnimationTime;
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Register(string role)
    {
        if (_role != "server") return;
        if (role == "driver") _driverId = Multiplayer.GetRemoteSenderId();
        else if (role == "observer") _observerId = Multiplayer.GetRemoteSenderId();
        else throw new InvalidOperationException("unknown registration role");
        if (_driverId == 0 || _observerId == 0 || _configured) return;
        Configure(_driverId, _observerId); Rpc(MethodName.Configure, _driverId, _observerId);
    }

    [Rpc(TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Configure(int driver, int observer)
    {
        try
        {
            Require(driver != observer && driver != 1 && observer != 1 && !_players.HasNode("1"), "dedicated topology premise");
            _driverId = driver; _observerId = observer;
            _driver = _players.GetNode<PlayerController>(driver.ToString());
            _observer = _players.GetNode<PlayerController>(observer.ToString());
            _ball = ResourceLoader.Load<PackedScene>("res://scenes/Ball.tscn").Instantiate<BallController>();
            _ball.Name = "Ball"; _ball.Players = _players;
            _ball.RimCenter = _ball.ShotTarget = new Vector3(0, 3.05f, 0);
            _ball.ShotScatterEnabled = false;
            AddChild(_ball); _ball.SetPhysicsProcess(false); _ball.SetProcess(false);
            foreach (var player in new[] { _driver, _observer })
            { player.SetPhysicsProcess(false); player.SetProcess(false); player.SprintBindBallForHarness(_ball); }
            SetStage(0); _configured = true;
            if (_role != "server")
            {
                _own = _role == "driver" ? _driver : _observer;
                _other = _role == "driver" ? _observer : _driver;
                Require(_own.ContactNetPeerId == Multiplayer.GetUniqueId(), "genuine local owner premise");
                RpcId(1, MethodName.ReadyToRun);
            }
        }
        catch (Exception error) { Finish(false, error.ToString()); }
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReadyToRun()
    {
        if (_role != "server") return;
        _ready.Add(Multiplayer.GetRemoteSenderId());
        if (_ready.Count != 2) return;
        _running = true; Rpc(MethodName.BeginRun);
    }
    [Rpc(TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)] private void BeginRun() => _running = true;

    [Rpc(TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SetStage(int stage)
    {
        _stage = stage; _tick = 0; _begun = _feinted = false;
        _stageBaseline = _ball.SuccessfulShotReleasesForHarness;
        _driver.ShotNetReset(new Vector3(0, 0, 6), _role == "server" && stage == 3);
        _observer.ShotNetReset(new Vector3(5, 0, 8), false);
        _ball.StateMachine.ForceState(BallState.Dribbling, _driverId);
    }

    [Rpc(TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void StageVerdict(int stage, int releases)
    { Require(releases == (stage == 0 ? 1 : 0), "received actual server release count"); }

    [Rpc(TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ValidateClients()
    {
        try
        {
            _running = false;
            if (_role == "driver")
            {
                Require(_corrections > 30 && _replaySamples > 5 && _largestCorrection > .01f
                    && _targetRepair && _rejectObserved,
                    $"genuine prediction proof corrections={_corrections} replaySteps={_replaySamples} headingCorrection={_largestCorrection} targetRepair={_targetRepair} reject={_rejectObserved}");
                _own.ShotNetVerifyInstanceOrdering(_mutation, Dt);
                GD.Print($"[shot-net] owner genuineCorrections={_corrections} startupReplaySteps={_replaySamples} maxError={_largestReplayError} headingCorrection={_largestCorrection} targetRepair={_targetRepair} rejected={_rejectObserved}; positional/contact replay limitations retained");
            }
            else
            {
                Require(_startup > 20 && _active > 0 && _recovery > 10 && _holds > 0,
                    $"coherent observer samples startup={_startup} active={_active} recovery={_recovery} held={_holds}");
                GD.Print($"[shot-net] observer startup={_startup} active={_active} recovery={_recovery} holds={_holds} resets={_resets}; phase/heading/actual AnimationTree/ball same server tick");
            }
            RpcId(1, MethodName.Completed);
        }
        catch (Exception error) { Finish(false, error.ToString()); }
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Completed()
    {
        if (_role != "server") return;
        int sender = Multiplayer.GetRemoteSenderId();
        Require(sender == _driverId || sender == _observerId, "completion sender");
        _done.Add(sender); if (_done.Count != 2) return;
        Rpc(MethodName.AllPassed); Finish(true, "authoritative releases normal=1 fakes=0 rejected=0; two client gates");
    }
    [Rpc(TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AllPassed() => Finish(true, "real ENet prediction/correction and coherent observer proof");
    private static void Require(bool pass, string message) { if (!pass) throw new InvalidOperationException(message); }
    private void Finish(bool pass, string message)
    {
        if (_finished) return; _finished = true;
        GD.Print($"[harness] {(pass ? "PASS" : "FAIL")} shot-net {_role}: {message}");
        if (pass) GetTree().CreateTimer(.5).Timeout += () => GetTree().Quit(0); else GetTree().Quit(1);
    }
}
