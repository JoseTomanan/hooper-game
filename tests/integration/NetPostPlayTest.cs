using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Networking;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

/// <summary>Three real ENet peers: dedicated authority, owner and observer.
/// Setup RPCs supply fixtures; input, admission, release and broadcasts remain production.</summary>
public partial class NetPostPlayTest : Node3D
{
    private string _role, _mutation;
    private Node3D _players;
    private PlayerController _driver, _observer, _own, _other, _reference;
    private BallController _ball;
    private int _driverId, _observerId, _tick, _stage, _stageTick, _replayTick, _baselineAck;
    private bool _registered, _configured, _running, _finished, _replay;
    private bool _entry, _idle, _lateral, _backward, _released, _held, _loose, _lost;
    private bool _converged;
    private bool _remotePost, _remoteStartup, _remoteLater, _localTurn, _serverTurn;
    private int _releaseCount, _serverPostTicks, _earlyChecks, _nonholderChecks;
    private float _largestCorrection, _forcedCorrection;
    private int _remoteStartupSamples, _remoteActiveSamples, _remoteRecoverySamples;
    private int _firstStartup, _firstActive, _firstRecovery;
    private float _remoteHipsDelta, _remoteArmDelta, _remoteForearmDelta;
    private double _elapsed;
    private Skeleton3D _remoteSkeleton;
    private float _preHips;
    private Quaternion _preArm, _preForearm;
    private int _remotePoseSamples;
    private bool _remoteGuard;
    private readonly HashSet<int> _ready = new(), _done = new();
    private double Dt => 1.0 / Engine.PhysicsTicksPerSecond;
    private static readonly int[] Durations = { 180, 120, 60, 60, 60, 60, 60, 160, 90 };

    public override void _Ready()
    {
        var args = OS.GetCmdlineUserArgs();
        _role = HarnessArgs.ReadArg(args, "--harness-role", "server");
        _mutation = HarnessArgs.ReadArg(args, "--mutation", "none");
        int port = int.Parse(HarnessArgs.ReadArg(args, "--harness-port", "28438"));
        _players = GetNode<Node3D>("Players");
        var net = GetNode<NetworkManager>("NetworkManager");
        if (_role == "server") { net.StartDedicatedServer(port); GD.Print("[post-net] READY server"); }
        else net.JoinGame("127.0.0.1", port);
    }
    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        if (_elapsed > 40) { Finish(false, "timeout"); return; }
        try
        {
            foreach (var player in _players.GetChildren().OfType<PlayerController>())
            { player.SetPhysicsProcess(false); player.SetProcess(false); }
            if (_role != "server" && !_registered && _players.GetChildCount() == 2
                && Multiplayer.MultiplayerPeer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected)
            { _registered = true; RpcId(1, MethodName.Register, _role); }
            if (!_running) return;
            _tick++; _stageTick++;
            if (_replay) { if (_role == "driver") ReplayStep(); return; }
            if (_role == "server") { ServerStep(); return; }
            ClientStep();
        }
        catch (Exception error) { Finish(false, error.Message); }
    }
    private void ServerStep()
    {
        _driver.SprintTickServerForHarness(Dt); _observer.SprintTickServerForHarness(Dt);
        if (_stage == 0 && _stageTick > 100)
        {
            Require(_driver.IsPosting && _driver.DisplayPostEstablished && !_driver.IsProtecting && !_driver.IsSprinting,
                "authority must resolve post over protection and sprint without stacking");
            _serverPostTicks++;
        }
        if (_stage == 5 && _stageTick > 15)
        { Require(_driver.ContactNetPhase == MovePhase.Inactive, "nonholder turnaround must remain inactive without fallback"); _nonholderChecks++; }
        if (_stage == 6 && _stageTick < 20)
        { Require(_driver.ContactNetPhase == MovePhase.Inactive, "early turnaround must remain inactive without jumpshot substitution"); _earlyChecks++; }
        if (_stage == 7)
        {
            _serverTurn |= _driver.DisplayMoveId() == "turnaround" && _driver.ContactNetPhase == MovePhase.Startup;
            BallState before = _ball.State;
            _ball._PhysicsProcess(Dt);
            if (before != BallState.InFlight && _ball.State == BallState.InFlight) _releaseCount++;
        }
        if (_stageTick < Durations[_stage]) return;
        if (_stage < 8) { int next = _stage + 1; SetStage(next); Rpc(MethodName.SetStage, next); return; }
        Require(_serverPostTicks > 40 && _earlyChecks > 5 && _nonholderChecks > 20,
            "authority post/rejection assertions must execute sustained samples");
        Require(_driver.SprintAckForHarness > 100 && _observer.SprintAckForHarness > 100,
            "both guarded owner input streams must advance seq/ack");
        Require(_serverTurn && _releaseCount == 1, $"real server turnaround release must occur exactly once turn={_serverTurn} releases={_releaseCount}");
        GD.Print($"[post-net] authority postTicks={_serverPostTicks} earlyChecks={_earlyChecks} nonholderChecks={_nonholderChecks} releases={_releaseCount} ack={_driver.SprintAckForHarness}/{_observer.SprintAckForHarness}");
        _replay = true; Rpc(MethodName.StartReplay);
    }
    private void ClientStep()
    {
        bool driver = _role == "driver";
        foreach (string action in new[] { "move_post", "move_protect", "move_sprint", "move_right", "move_forward", _ball.ShootAction }) Input.ActionRelease(action);
        if (driver)
        {
            if (_stage != 2 && _stage != 8) Input.ActionPress("move_post");
            Input.ActionPress("move_protect"); Input.ActionPress("move_sprint");
            if (_stage == 0 && _stageTick < 15) Input.ActionPress("move_right");
            if (_stage == 1) Input.ActionPress(_stageTick < 60 ? "move_right" : "move_forward");
            if (_stage == 5 && _stageTick == 1 || _stage == 6 && _stageTick == 1) _own.PostNetRejectRequest(_tick);
            if (_stage == 7 && _stageTick == 60) Input.ActionPress(_ball.ShootAction);
            if (_stage == 1 && _stageTick == 30) _own.GlobalPosition += new Vector3(.75f, 0, .5f);
        }
        _own.SprintTickClientForHarness(Dt);
        bool fresh = _other.ContactNetNewState;
        _other.ProtectionTickRemoteForHarness();
        if (!driver) _other.PostNetApplyPresentation(Dt);
        if (_stage == 7) _ball._PhysicsProcess(Dt);
        if (driver)
        {
            _entry |= _stage == 0 && _stageTick < 20 && _own.IsPosting && !_own.DisplayPostEstablished;
            _idle |= _stage == 0 && _stageTick > 100 && _own.DisplayPostEstablished && _own.Velocity.Length() < .001f;
            if (_stage == 1 && _stageTick > 15)
            {
                float away = MathF.Atan2(_own.GlobalPosition.X, _own.GlobalPosition.Z);
                Require(Math.Abs(Mathf.AngleDifference(_own.Heading, away)) < _own.PostEntryToleranceDeg * Mathf.Pi / 180f + _own.PostTurnRateDeg * (float)Dt * Mathf.Pi / 180f, "post translation must retain away-from-rim authoritative heading");
                Require(_own.Velocity.Length() < _own.PostSpeed + .001f, "post translation must obey the settled post pace");
                _lateral |= _stageTick < 60 && Math.Abs(_own.Velocity.X) > .1f;
                _backward |= _stageTick > 65 && _own.Velocity.Z < -.1f;
            }
            _released |= _stage == 2 && _stageTick > 15 && !_own.IsPosting;
            _converged |= _stage == 2 && _stageTick > 35 && _own.Velocity.Length() < .0001f
                && _own.GlobalPosition.DistanceTo(_own.ContactNetRawPosition) < .0001f;
            _held |= _stage == 3 && _stageTick > 15 && !_own.IsPosting;
            _loose |= _stage == 4 && _stageTick > 15 && !_own.IsPosting;
            _lost |= _stage == 5 && _stageTick > 15 && !_own.IsPosting;
            _localTurn |= _stage == 7 && _own.DisplayMoveId() == "turnaround" && _own.ContactNetPhase == MovePhase.Startup;
        }
        else if (fresh)
        {
            Require(Math.Abs(Mathf.AngleDifference(_other.Heading, _other.ContactNetRawHeading)) < .00001f,
                "observer heading must consume same live broadcast");
            _remotePost |= _other.PostNetReceived && _other.DisplayPosting && _other.DisplayPostEstablished
                && !Input.IsActionPressed("move_post");
            if (_other.PostNetReceivedMove == "turnaround")
            {
                Require(_other.DisplayMoveId() == "turnaround", "same received turnaround snapshot must retain distinct display identity");
                string expected = "Turnaround" + _other.PostNetReceivedPhase;
                Require(_other.PostNetReceivedPhase == MovePhase.Inactive || _other.ActiveAnimNodeForHarness == expected,
                    $"actual observer AnimationTree must select same-snapshot {expected}, got {_other.ActiveAnimNodeForHarness}");
                _remoteStartup |= _other.PostNetReceivedPhase == MovePhase.Startup && _other.DisplayMove().phase == MovePhase.Startup;
                _remoteLater |= (_other.PostNetReceivedPhase == MovePhase.Active || _other.PostNetReceivedPhase == MovePhase.Recovery)
                    && _other.DisplayMove().phase == _other.PostNetReceivedPhase;
                if (_other.PostNetReceivedPhase == MovePhase.Startup)
                { _remoteStartupSamples++; if (_firstStartup == 0) _firstStartup = _tick; }
                if (_other.PostNetReceivedPhase == MovePhase.Active)
                { _remoteActiveSamples++; if (_firstActive == 0) _firstActive = _tick; }
                if (_other.PostNetReceivedPhase == MovePhase.Recovery)
                { _remoteRecoverySamples++; if (_firstRecovery == 0) _firstRecovery = _tick; }
            }
        }
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Register(string role)
    {
        if (_role != "server") return;
        if (role == "driver") _driverId = Multiplayer.GetRemoteSenderId();
        else if (role == "observer") _observerId = Multiplayer.GetRemoteSenderId();
        else throw new InvalidOperationException("unknown role");
        if (_driverId == 0 || _observerId == 0 || _configured) return;
        Configure(_driverId, _observerId); Rpc(MethodName.Configure, _driverId, _observerId);
    }
    [Rpc(TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Configure(int driver, int observer)
    {
        try
        {
            Require(_players.GetChildCount() == 2 && driver != observer && driver != 1 && observer != 1
                && !_players.HasNode("1"), "dedicated server must have two actual client owners and no player 1");
            _driverId = driver; _observerId = observer;
            _driver = _players.GetNode<PlayerController>(driver.ToString());
            _observer = _players.GetNode<PlayerController>(observer.ToString());
            _ball = new BallController { Name = "Ball", Players = _players }; AddChild(_ball); _ball.SetPhysicsProcess(false);
            foreach (var player in new[] { _driver, _observer })
            {
                player.SetPhysicsProcess(false); player.SetProcess(false);
                player.GetNode<AnimationTree>("AnimationTree").Active = false; player.SprintBindBallForHarness(_ball);
            }
            SetStage(0); _configured = true;
            if (_role != "server")
            {
                _own = _role == "driver" ? _driver : _observer; _other = _role == "driver" ? _observer : _driver;
                Require(_own.ContactNetPeerId == Multiplayer.GetUniqueId(), "real local ownership premise");
                _own.ContactNetObserveReconciliation((before, after, count) =>
                {
                    float correction = before.DistanceTo(after);
                    _largestCorrection = Math.Max(_largestCorrection, correction);
                    if (_role == "driver" && _stage == 1 && _stageTick >= 30 && _stageTick <= 50)
                        _forcedCorrection = Math.Max(_forcedCorrection, correction);
                });
                if (_role == "observer") ObserveRemotePose();
                RpcId(1, MethodName.ReadyToRun);
            }
        }
        catch (Exception error) { Finish(false, error.Message); }
    }
    private void ObserveRemotePose()
    {
        _remoteSkeleton = (Skeleton3D)_other.FindChildren("*", nameof(Skeleton3D), true, false)[0];
        var protection = _remoteSkeleton.GetNode<ProtectionPose>("ProtectionPose");
        var post = _remoteSkeleton.GetNode<PostPose>("PostPose");
        // Capture the immediate predecessor's authored pose, then the real post modifier's
        // output on this same animation frame. No synthetic posture/state is supplied.
        // https://docs.godotengine.org/en/4.7/classes/class_skeletonmodifier3d.html#class-skeletonmodifier3d-signal-modification-processed
        protection.ModificationProcessed += () =>
        {
            _preHips = _remoteSkeleton.GetBonePosePosition(_remoteSkeleton.FindBone("mixamorig_Hips")).Y;
            string off = _other.HandSide == HandSide.Right ? "Left" : "Right";
            _preArm = _remoteSkeleton.GetBonePoseRotation(_remoteSkeleton.FindBone("mixamorig_" + off + "Arm"));
            _preForearm = _remoteSkeleton.GetBonePoseRotation(_remoteSkeleton.FindBone("mixamorig_" + off + "ForeArm"));
        };
        post.ModificationProcessed += () =>
        {
            if (!_other.PostNetReceived || !_other.DisplayPosting || _other.DisplayMove().phase != MovePhase.Inactive
                || _other.IsPivotingInPlace || Input.IsActionPressed("move_post")) return;
            float hips = _remoteSkeleton.GetBonePosePosition(_remoteSkeleton.FindBone("mixamorig_Hips")).Y;
            string off = _other.HandSide == HandSide.Right ? "Left" : "Right";
            var arm = _remoteSkeleton.GetBonePoseRotation(_remoteSkeleton.FindBone("mixamorig_" + off + "Arm"));
            var forearm = _remoteSkeleton.GetBonePoseRotation(_remoteSkeleton.FindBone("mixamorig_" + off + "ForeArm"));
            float Angle(Quaternion a, Quaternion b)
            {
                var d = a.Inverse() * b;
                return 2f * Mathf.Atan2(new Vector3(d.X, d.Y, d.Z).Length(), Mathf.Abs(d.W));
            }
            _remotePoseSamples++;
            _remoteHipsDelta = hips - _preHips;
            _remoteArmDelta = Angle(_preArm, arm); _remoteForearmDelta = Angle(_preForearm, forearm);
            _remoteGuard |= Math.Abs(_remoteHipsDelta + .06f) < .0001f
                && _remoteArmDelta > .1f && _remoteForearmDelta > .1f;
        };
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReadyToRun()
    {
        if (_role != "server") return;
        int sender = Multiplayer.GetRemoteSenderId(); Require(sender == _driverId || sender == _observerId, "ready sender");
        _ready.Add(sender); if (_ready.Count != 2) return;
        _running = true; Rpc(MethodName.BeginRun);
    }
    [Rpc(TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)] private void BeginRun() => _running = true;
    [Rpc(TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SetStage(int stage)
    {
        _stage = stage; _stageTick = 0;
        _ball.StateMachine.ForceState(stage == 3 ? BallState.Held : stage == 4 ? BallState.Loose : BallState.Dribbling,
            stage == 4 ? 0 : stage == 5 ? _observerId : _driverId);
        if (stage == 0 || stage == 6 || stage == 7)
        {
            _driver.ResetContactStateForHarness(new Vector3(0, 0, 8), Mathf.Pi, Vector3.Zero);
            _observer.ResetContactStateForHarness(new Vector3(5, 0, 10), 0, Vector3.Zero);
        }
        
    }
    [Rpc(TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void StartReplay()
    {
        try
        {
            if (_role == "driver")
                Require(_entry && _idle && _lateral && _backward && _released && _held && _loose && _lost && _localTurn && _converged && _forcedCorrection > .1f,
                    $"live driver evidence entry={_entry} idle={_idle} lateral={_lateral} backward={_backward} released={_released} held={_held} loose={_loose} lost={_lost} turn={_localTurn} converged={_converged} correction={_largestCorrection}");
            else
            {
                Require(_remotePost && _remoteStartup && _remoteLater && _remoteGuard && _remotePoseSamples > 20
                    && _remoteStartupSamples > 10 && _remoteActiveSamples > 0 && _remoteRecoverySamples > 5
                    && _firstStartup < _firstActive && _firstActive < _firstRecovery && _ball.SawAuthoritativeFlightForHarness,
                    $"observer same-snapshot evidence post={_remotePost} startup={_remoteStartup} later={_remoteLater} guard={_remoteGuard} poseSamples={_remotePoseSamples} ballFlight={_ball.SawAuthoritativeFlightForHarness}");
                GD.Print($"[post-net] observer actualNodes startup={_remoteStartupSamples} active={_remoteActiveSamples} recovery={_remoteRecoverySamples} firstTicks={_firstStartup}/{_firstActive}/{_firstRecovery} postPoseSamples={_remotePoseSamples} hipsDelta={_remoteHipsDelta} armDelta={_remoteArmDelta} forearmDelta={_remoteForearmDelta}; hardwarePost=false");
                RpcId(1, MethodName.Completed);
            }
            _replay = true;
        }
        catch (Exception error) { Finish(false, error.Message); }
    }
    private void ReplayStep()
    {
        if (++_replayTick < 25) return;
        if (_replayTick == 25)
        {
            _baselineAck = _own.SprintPreparePendingForHarness();
            var viewport = new SubViewport { Name = "Reference", OwnWorld3D = true }; AddChild(viewport);
            var root = new Node3D { Name = "World" }; viewport.AddChild(root);
            _reference = ResourceLoader.Load<PackedScene>("res://scenes/Player.tscn").Instantiate<PlayerController>();
            root.AddChild(_reference); _reference.SetPhysicsProcess(false); _reference.SetProcess(false);
            _reference.GetNode<AnimationTree>("AnimationTree").Active = false;
            foreach (string action in new[] { "move_protect", "move_sprint", "move_forward", "move_right", _ball.ShootAction }) Input.ActionRelease(action);
            Input.ActionPress("move_right");
            return;
        }
        if (_replayTick <= 49)
        {
            int sample = _replayTick - 26;
            if (sample < 8 || sample >= 16) Input.ActionPress("move_post"); else Input.ActionRelease("move_post");
            _ball.StateMachine.ForceState(sample >= 16 && sample < 20 ? BallState.Loose : BallState.Dribbling, sample >= 16 && sample < 20 ? 0 : _driverId);
            _own.SprintTickClientForHarness(Dt, suppressCorrection: true); return;
        }
        Require(_own.SprintPendingForHarness == 24, "history must contain 24 actual client prediction samples");
        Vector3 authPosition = new(0, 0, 8), authVelocity = Vector3.Zero; float authHeading = 0f;
        _reference.ResetContactStateForHarness(authPosition, authHeading, authVelocity);
        var opponent = _own.StanceNetOpponent;
        for (int i = 0; i < 24; i++)
            _reference.Move(Vector2.Right, Dt, opponent, postHeld: i < 8 || i >= 16, postEligible: i < 16 || i >= 20, rimSample: _ball.RimCenter);
        // Current possession intentionally disagrees with twenty historical eligible samples.
        _ball.StateMachine.ForceState(BallState.Loose, 0);
        if (_mutation != "none") _own.PostNetMutateHistory(_mutation);
        Vector3 before = _own.GlobalPosition;
        _own.PostNetCorrection(authPosition, authVelocity, authHeading, _baselineAck, Dt);
        float p = _own.GlobalPosition.DistanceTo(_reference.GlobalPosition);
        float v = _own.Velocity.DistanceTo(_reference.Velocity);
        float h = Math.Abs(Mathf.AngleDifference(_own.Heading, _reference.Heading));
        Require(before.DistanceTo(_own.GlobalPosition) > .1f, "correction must move physics body");
        Require(p < .0001f && v < .0001f && h < .0001f && _own.PostNetEffective == _reference.PostNetEffective
            && _own.PostNetEstablished == _reference.PostNetEstablished && _own.PostNetPivot == _reference.PostNetPivot
            && _own.DisplayMoveId() == _reference.DisplayMoveId(),
            $"independent post historical replay mismatch p={p} v={v} h={h} mutation={_mutation}");
        GD.Print($"[post-net] replay samples=24 heldEligible=12 releasedEligible=8 heldIneligible=4 p={p} v={v} h={h} correction={_largestCorrection} forcedStageCorrection={_forcedCorrection} converged={_converged}; latest raw opponent approximation retained");
        RpcId(1, MethodName.Completed); SetPhysicsProcess(false);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Completed()
    {
        if (_role != "server") return;
        int sender = Multiplayer.GetRemoteSenderId(); Require(sender == _driverId || sender == _observerId, "completion sender");
        _done.Add(sender); if (_done.Count != 2) return;
        Rpc(MethodName.AllPassed); Finish(true, "dedicated authority and two real client gates");
    }
    [Rpc(TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)] private void AllPassed() => Finish(true, "post wire, rejection controls, turnaround release, remote phase and historical replay");
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private void Finish(bool pass, string message)
    {
        if (_finished) return; _finished = true;
        GD.Print($"[harness] {(pass ? "PASS" : "FAIL")} post-net {_role}: {message}");
        if (pass) GetTree().CreateTimer(.5).Timeout += () => GetTree().Quit(0); else GetTree().Quit(1);
    }
}

