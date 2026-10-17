using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Networking;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

/// <summary>Issue #414 reproduction instrument; successful capture does not certify continuity or feel.</summary>
public partial class CrossoverContinuityCapture : Node
{
    private string _role, _output, _commit, _motion, _origin;
    private int _count, _port, _tick, _startTick = -1, _pulseTick = -1, _completed, _neutralTick = -1;
    private bool _images, _finished, _remoteReady, _clientReadySent, _preparing, _measuring, _inCycle;
    private bool _measurementAck, _measurementPulse, _recoveryReleased, _finishSent, _finishAcknowledged, _clientCompletionReceived;
    private int _finishFlushTick = -1;
    private int _driveTick = -1;
    private Vector3 _lastInactiveVelocity;
    private float _lastInactiveHeading;
    private int _lastInactiveTick;
    private bool _disableCrossoverPose, _poseMutationApplied, _poseApplied, _holdMovement, _remotePaced;
    private float _poseMaxReachError;
    private int _poseClampCount, _poseSampleFrame;
    private string _loseBall = "none";
    private bool _ballLost, _sawLossRecovery;
    private int _lossRecipient;
    private string _expectedHand = "";
    private string _failure = "";
    private ulong _lastPhysics;
    private int _samples, _gaps, _duplicateFrames, _poseFrameMismatches, _delayMs, _maxPhysicsGap, _observedMaxPhysicsGap;
    private float? _fixtureSpawnX;
    private readonly System.Diagnostics.Stopwatch _wallClock = System.Diagnostics.Stopwatch.StartNew();
    private StreamWriter _trace;
    private NetworkManager _network;
    private Node _players;
    private BallController _ball;
    private Camera3D _camera;
    private Skeleton3D _skeleton;
    private Dictionary<string, float[]> _finalBones = new();
    private Dictionary<string, float[]> _finalBoneRotations = new();
    private ulong _posePhysics, _poseRender;
    private string _appliedAnimation = "", _appliedFadingFrom = "";
    private double _appliedPlaybackTime, _appliedFadingTime;
    private string _poseDisplayMove = "", _poseDisplayPhase = "Inactive", _poseDisplayHand = "";
    private int _poseDisplayFrame;
    private bool _poseDisplaySimulatedLocally;
    private readonly List<object> _events = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public override void _Ready()
    {
        var args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        string Read(string name, string fallback) => HarnessArgs.ReadArg(args, name, fallback);
        _role = Read("--capture-role", "local");
        _remotePaced = Read("--capture-remote-paced", "false") == "true";
        _motion = Read("--capture-motion", "stationary");
        _origin = Read("--capture-origin", "Right");
        _commit = Read("--capture-commit", "unknown");
        _images = Read("--capture-images", "true") != "false";
        _disableCrossoverPose = Read("--capture-disable-crossover-pose", "false") == "true";
        _holdMovement = Read("--capture-hold-movement", "false") == "true";
        _loseBall = Read("--capture-lose-ball", "none");
        _count = int.Parse(Read("--capture-count", "1"));
        _port = int.Parse(Read("--capture-port", "23464"));
        _delayMs = int.Parse(Read("--capture-delay-ms", "0"));
        _maxPhysicsGap = int.Parse(Read("--capture-max-physics-gap", _role == "client" ? "2" : "1"));
        _output = Read("--capture-output", "");
        if (_output.Length == 0 || !new[] { "local", "server", "client" }.Contains(_role) ||
            !new[] { "stationary", "moving" }.Contains(_motion) || !new[] { "Left", "Right" }.Contains(_origin) ||
            (_count != 1 && _count != 3) || !new[] { "none", "early", "late" }.Contains(_loseBall) || (_loseBall != "none" && (_role == "local" || _count != 1)) || (_holdMovement && (_motion != "moving" || _count != 1)) || _delayMs < 0 || _delayMs > 200 || (_delayMs > 0 && _role != "server") || _maxPhysicsGap < 1 || _maxPhysicsGap > 2) { Finish("invalid capture arguments"); return; }
        _output = ProjectSettings.GlobalizePath(_output);
        if (Directory.Exists(_output) && Directory.EnumerateFileSystemEntries(_output).Any(path => Path.GetFileName(path) != "capture.avi"))
        { _output = ""; Finish("capture output must be fresh"); return; }
        Directory.CreateDirectory(_output);
        _trace = new StreamWriter(Path.Combine(_output, "trace.jsonl"));
        var main = GetNode<Node3D>("Main");
        _network = main.GetNode<NetworkManager>("NetworkManager");
        _players = main.GetNode("Players");
        _ball = main.GetNode<BallController>("Ball");
        _camera = main.GetNode<Camera3D>("Camera3D");
        if (_role == "client")
        {
            Multiplayer.ConnectionFailed += () => Finish("connection failed");
            Multiplayer.ServerDisconnected += () =>
            {
                if (!_finished) Finish(_clientCompletionReceived ? "" : "server disconnected before completion notification");
            };
            _network.JoinGame("127.0.0.1", _port);
        }
        else
        {
            if (_motion == "moving" && _count == 3)
            {
                _fixtureSpawnX = _origin == "Right" ? -5.15f : -4.2f;
                Vector3 spawn = _network.HostSpawn;
                _network.HostSpawn = new Vector3(_fixtureSpawnX.Value, spawn.Y, spawn.Z);
                Event("fixture-setup", $"HostSpawn.X={_fixtureSpawnX:R} before HostGame; centers three rightward runs in unchanged camera");
            }
            _network.HostGame(_port);
        }
        // Post-draw samples bind the log to the displayed frame rather than a pending physics request.
        // https://docs.godotengine.org/en/4.7/classes/class_renderingserver.html#signals
        RenderingServer.FramePostDraw += SampleDraw;
        Event("boot", "normal Main scene, normal spawn, unchanged camera and production animation callback");
        if (_holdMovement) Event("fixture-variant", "hold move_right through Recovery and first 8 destination-neutral physics ticks");
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void CaptureReady()
    {
        if (Multiplayer.IsServer() && Multiplayer.GetRemoteSenderId() > 1) _remoteReady = true;
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void CaptureMeasurementStarted()
    {
        if (_role == "client") { _measuring = true; _inCycle = false; _completed = 0; Event("measurement-begin", "server signalled measured window"); RpcId(1, nameof(MeasurementReady)); }
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void MeasurementReady()
    {
        if (Multiplayer.IsServer() && Multiplayer.GetRemoteSenderId() > 1) _measurementAck = true;
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void CaptureFinished()
    {
        if (_role != "client" || Multiplayer.GetRemoteSenderId() != 1 || _clientCompletionReceived) return;
        if (_completed != _count) { Finish("remote cycles incomplete at completion notification"); return; }
        _clientCompletionReceived = true;
        _finishFlushTick = _tick;
        RpcId(1, nameof(FinishAcknowledged));
        Event("completion-notification", "ack queued; wait 5 client physics ticks before exit");
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void FinishAcknowledged()
    {
        if (_role != "server" || Multiplayer.GetRemoteSenderId() <= 1 || !_finishSent || _finishAcknowledged) return;
        _finishAcknowledged = true;
        _finishFlushTick = _tick;
        Event("completion-acknowledged", "wait 30 server physics ticks before exit");
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _tick++;
        // Completion can outlive Players/1: NetworkManager removes players on disconnect.
        if (_role == "client" && _clientCompletionReceived && _tick - _finishFlushTick >= 5)
        { Finish(""); return; }
        // Pacing is diagnostic only. Stop both runaway simulation and stalled network sessions.
        // https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.stopwatch.startnew?view=net-8.0
        int tickLimit = _role == "local" ? 900 : 6000;
        if (_tick > tickLimit || _wallClock.Elapsed.TotalSeconds > 90) { Finish("capture timeout"); return; }
        var player = _players?.GetNodeOrNull<PlayerController>("1");
        if (player == null) return;
        if (_disableCrossoverPose && !_poseMutationApplied)
        {
            CrossoverPose modifier = player.ContinuityCrossoverPose;
            if (modifier == null) { Finish("disable-crossover-pose mutation requested but production modifier is missing"); return; }
            modifier.Active = false;
            _poseMutationApplied = true;
            Event("diagnostic-mutation", "disabled production CrossoverPose.Active on Players/1 before measured input");
        }
        if (_role == "client")
        {
            if (!_clientReadySent && Multiplayer.GetUniqueId() > 1)
            { _clientReadySent = true; RpcId(1, nameof(CaptureReady)); }
            // A genuine remote display is observed without injecting its phase or hand.
            if (player.ContinuityIsLocalPlayer) { Finish("client subject is local"); return; }

            ObserveCycle(player);
            if (_clientCompletionReceived && _tick - _finishFlushTick >= 5) Finish("");
            return;
        }
        if (_role == "server" && !_remoteReady) return;
        if (_startTick < 0) { _startTick = _tick; return; }
        int elapsed = _tick - _startTick;
        if (elapsed == 15) SetInput("move_forward", true);
        if (elapsed == 25) SetInput("move_forward", false);
        if (elapsed < 60 || (!_measuring && _ball.State != BallState.Dribbling)) return;
        if (_pulseTick >= 0 && _tick == _pulseTick + 8)
        { SetInput("aim_left", false); SetInput("aim_right", false); }
        if (!_measuring)
        {
            if (player.HandSide.ToString() != _origin && !_preparing)
            { _preparing = true; Pulse(player); return; }
            if (_preparing && (_pulseTick < 0 || _tick - _pulseTick < 70 || player.DisplayMove().phase != MovePhase.Inactive)) return;
            if (player.Velocity.Length() > 0.03f) return;
            _measuring = true;
            _inCycle = false;
            _neutralTick = _tick;
            Event("measurement-begin", "preparation complete; origin=" + player.HandSide);
            if (_role == "server") Rpc(nameof(CaptureMeasurementStarted));
        }
        if (_role == "server" && !_measurementAck) return;
        ObserveCycle(player);
        if (_finished) return;
        if (_role == "server" && _loseBall != "none" && !_ballLost && _inCycle)
        {
            var display = player.AnimationDisplaySnapshot;
            bool lossTick = _loseBall == "early" ? display.Phase == MovePhase.Active :
                display.Phase == MovePhase.Recovery && display.FrameInPhase >= 9;
            if (lossTick)
            {
                PlayerController recipient = _players.GetChildren().OfType<PlayerController>()
                    .FirstOrDefault(candidate => candidate.Name != "1");
                if (recipient == null || !int.TryParse(recipient.Name.ToString(), out _lossRecipient))
                { Finish("ball-loss fixture requires a genuine connected observer player"); return; }
                _ball.PostAwardForHarness(_lossRecipient);
                if (_ball.StateMachine.HolderPeerId != _lossRecipient)
                { Finish("production possession award did not transfer the ball"); return; }
                _ballLost = true;
                Event("ball-loss-award", $"recipient={_lossRecipient}; phase={display.Phase}; frame={display.FrameInPhase}; variant={_loseBall}; production=AwardPossession");
            }
        }
        if (!_holdMovement && _inCycle && _motion == "moving" && !_recoveryReleased && player.DisplayMove().phase == MovePhase.Recovery)
        {
            SetInput("move_right", false);
            _recoveryReleased = true;
        }
        if (_holdMovement && _completed > 0 && !_recoveryReleased && _tick - _neutralTick >= 8)
        {
            SetInput("move_right", false);
            _recoveryReleased = true;
            Event("held-movement-release", "8 destination-neutral physics ticks completed");
        }
        bool readyForGesture = !_measurementPulse ||
            (!_inCycle && _neutralTick >= 0 && _tick - _neutralTick >= 36 && _tick - _pulseTick >= 30);
        if (_completed < _count && readyForGesture)
        {
            if (_motion == "moving")
            {
                if (_driveTick < 0)
                {
                    if (player.Velocity.Length() > 0.03f) return;
                    _driveTick = _tick;
                    SetInput("move_right", true);
                }
                if (_tick - _driveTick < 16) return;
            }
            else if (player.Velocity.Length() > 0.03f) return;
            _measurementPulse = true;
            _driveTick = -1;
            _recoveryReleased = false;
            Pulse(player);
        }
        if (_completed == _count && _tick - _neutralTick >= 36)
        {
            if (_role == "server")
            {
                if (!_finishSent)
                {
                    _finishSent = true;
                    Rpc(nameof(CaptureFinished));
                    Event("completion-sent", "waiting for client acknowledgment");
                }
                if (_finishAcknowledged && _tick - _finishFlushTick >= 30) Finish("");
            }
            else Finish("");
        }
    }

    private void Pulse(PlayerController player)
    {
        _pulseTick = _tick;
        _expectedHand = player.HandSide == HandSide.Right ? "Left" : "Right";
        SetInput(_expectedHand == "Left" ? "aim_left" : "aim_right", true);
    }

    private void ObserveCycle(PlayerController player)
    {
        if (!_measuring) return;
        if (_role == "client" && _loseBall != "none" && !_ballLost && _inCycle &&
            _ball.StateMachine.HolderPeerId != 1 && _ball.StateMachine.HolderPeerId > 0)
        {
            _ballLost = true;
            _lossRecipient = _ball.StateMachine.HolderPeerId;
            var lossDisplay = player.AnimationDisplaySnapshot;
            Event("ball-loss-observed", $"recipient={_lossRecipient}; phase={lossDisplay.Phase}; frame={lossDisplay.FrameInPhase}; variant={_loseBall}");
        }
        // Same-instance timeline proof: the possession broadcast may arrive after
        // remote Recovery has ended. Do not require unrelated RPC edges to co-occur.
        if (_inCycle && player.DisplayMove().phase == MovePhase.Recovery) _sawLossRecovery = true;
        if (player.DisplayMove().phase == MovePhase.Inactive)
        {
            _lastInactiveVelocity = player.Velocity;
            _lastInactiveHeading = player.Heading;
            _lastInactiveTick = _tick;
        }
        if (!_inCycle && player.DisplayMoveId() == "crossover" && player.DisplayMove().phase != MovePhase.Inactive)
        {
            _inCycle = true;
            // Inferred from Startup's current hand; remote observation may first see Active.
            if (_role == "client") _expectedHand = player.DisplayMove().phase == MovePhase.Startup
                ? (player.HandSide == HandSide.Right ? "Left" : "Right") : player.HandSide.ToString();
            float entrySpeed = new Vector2(_lastInactiveVelocity.X, _lastInactiveVelocity.Z).Length();
            Event("recognized-crossover", $"destination={_expectedHand}; entrySpeed={entrySpeed:R}; entryHeading={_lastInactiveHeading:R}; observedStartupSpeed={player.Velocity.Length():R}; entrySample=last-inactive-physics-tick; entrySampleTick={_lastInactiveTick}");
            if (_role != "client" && _motion == "moving" && entrySpeed < 5.9f)
            { Finish($"moving entry speed {entrySpeed:R} below 5.9"); return; }
        }
        bool expectedNeutral = _loseBall == "none"
            ? player.HandSide.ToString() == _expectedHand && player.ActiveAnimNodeForHarness == "Dribble" + _expectedHand
            : _ballLost && _sawLossRecovery && !player.DisplayDribbling() && player.ActiveAnimNodeForHarness == "Locomotion";
        if (_inCycle && player.DisplayMove().phase == MovePhase.Inactive && expectedNeutral && !_ball.SweepActiveForHarness)
        {
            _inCycle = false; _completed++; _neutralTick = _tick;
            Event(_loseBall == "none" ? "destination-dribble" : "destination-locomotion-after-loss", "cycle=" + _completed);
        }
    }

    // action_press updates the state read by production Input.GetVector, without a Begin move seam.
    // https://docs.godotengine.org/en/4.7/classes/class_input.html#class-input-method-action-press
    private void SetInput(string action, bool down)
    {
        if (down) Input.ActionPress(action); else Input.ActionRelease(action);
        Event(down ? "input-down" : "input-up", action);
    }

    public override void _Process(double delta)
    {
        // Deliberately block only this diagnostic server's wall clock, preserving --fixed-fps
        // simulation delta. This lets a slower native observer capture every received phase.
        // No real-time network/performance conclusion can be drawn from a paced run.
        // Official 4.7.1 GodotSharp XML confirms DelayMsec(int); web reference:
        // https://docs.godotengine.org/en/stable/classes/class_os.html#class-os-method-delay-msec
        if (!_finished && _role == "server" && _delayMs > 0) OS.DelayMsec(_delayMs);
        // Headless has no draw signals: this mode provides diagnostic state traces only,
        // and never claims image/rendered-pose evidence. Native image runs use FramePostDraw.
        if (!_images && DisplayServer.GetName() == "headless") SampleDraw();
    }

    private void SampleDraw()
    {
        if (_finished || _trace == null) return;
        var p = _players.GetNodeOrNull<PlayerController>("1");
        if (p == null) return;
        ulong physics = Engine.GetPhysicsFrames();
        if (_measuring && _lastPhysics != 0)
        {
            int gap = (int)(physics - _lastPhysics);
            _observedMaxPhysicsGap = Math.Max(_observedMaxPhysicsGap, gap);
            if (gap > _maxPhysicsGap) _gaps++;
        }
        if (_measuring && physics == _lastPhysics) _duplicateFrames++;
        _lastPhysics = physics;
        if (_skeleton == null)
        {
            _skeleton = FindSkeleton(p);
            if (_skeleton != null) _skeleton.SkeletonUpdated += SnapshotFinalBones;
        }
        var bones = _finalBones;
        string file = "";
        if (_images)
        {
            file = $"frame-{_samples:D5}.png";
            // https://docs.godotengine.org/en/4.7/classes/class_viewport.html#class-viewport-method-get-texture
            var image = GetViewport().GetTexture().GetImage();
            if (image == null || image.IsEmpty() || image.SavePng(Path.Combine(_output, file)) != Error.Ok)
            { Finish("image capture failed"); return; }
        }
        var model = p.GetNodeOrNull<Node3D>("CharacterModel");
        Vector3 anchor = p.GlobalPosition + Vector3.Up;
        Vector2 screen = _camera.UnprojectPosition(anchor);
        bool framed = !_camera.IsPositionBehind(anchor) && _camera.IsPositionInFrustum(anchor) && GetViewport().GetVisibleRect().HasPoint(screen);
        bool visibleMeshExists = HasVisibleMesh(p);
        bool poseFrameMatches = _posePhysics == physics;
        if (_measuring && !poseFrameMatches) _poseFrameMismatches++;
        if (_measuring && (!_camera.Current || !framed || (_images && (!visibleMeshExists || bones.Count < 65))))
        { Finish($"render fixture invalid: cameraCurrent={_camera.Current}, framed={framed}, visibleMesh={visibleMeshExists}, bones={bones.Count}"); return; }
        var row = new
        {
            sample = _samples++, tick = _tick, physicsFrame = physics, renderFrame = Engine.GetProcessFrames(), file,
            measuring = _measuring, preparing = _preparing && !_measuring, trial = _completed + 1, completedCycles = _completed, role = _role, localPlayer = p.ContinuityIsLocalPlayer,
            authoritativeMove = p.CurrentMoveIdForHarness, authoritativePhase = p.PhaseForHarness.ToString(), authoritativeFrame = p.FrameInPhaseForHarness,
            displayMove = p.DisplayMoveId(), displayPhase = p.DisplayMove().phase.ToString(), hand = p.HandSide.ToString(),
            inferredOrigin = p.DisplayMove().phase == MovePhase.Startup ? p.HandSide.ToString() : p.HandSide == HandSide.Right ? "Left" : "Right",
            originInference = "phase/hand inference; inactive value is not an origin latch",
            cameraCurrent = _camera.Current, framed, visibleMeshExists, poseFrameMatches, screen = new[] { screen.X, screen.Y },
            heading = p.Heading, velocity = Vec(p.Velocity), root = Transform(p.GlobalTransform), model = model == null ? Array.Empty<float>() : Transform(model.GlobalTransform),
            ball = Vec(_ball.GlobalPosition), ballRadius = _ball.BallRadius, ballState = _ball.State.ToString(), ballHolderPeerId = _ball.StateMachine.HolderPeerId, displayDribbling = p.DisplayDribbling(), dribblePhase = _ball.DribblePhaseForHarness,
            neutralApex = p.DisplayDribbling() && _ball.State == BallState.Dribbling && !_ball.SweepActiveForHarness && p.DisplayMove().phase == MovePhase.Inactive &&
                Math.Min(_ball.DribblePhaseForHarness, 1f - _ball.DribblePhaseForHarness) <= 0.035f,
            sweepActive = _ball.SweepActiveForHarness, sweepTicks = _ball.ContinuitySweepTicks,
            sweepDurationTicks = _ball.ContinuitySweepDurationTicks, sweepProgress = _ball.ContinuitySweepProgress,
            animation = p.ActiveAnimNodeForHarness, playbackTime = p.ContinuityPlaybackTime,
            fadingFrom = p.ContinuityFadingFrom, fadingTime = p.ContinuityFadingTime,
            poseDisplayMove = _poseDisplayMove, poseDisplayPhase = _poseDisplayPhase,
            poseDisplayFrame = _poseDisplayFrame, poseDisplayHand = _poseDisplayHand,
            poseDisplaySimulatedLocally = _poseDisplaySimulatedLocally,
            crossoverPoseApplied = _poseApplied, crossoverPoseMaxReachError = _poseMaxReachError,
            crossoverPoseClampCount = _poseClampCount, crossoverPoseSampleFrame = _poseSampleFrame, crossoverPoseSamplePhysicsFrame = _posePhysics,
            animationCallback = p.ContinuityAnimationCallback, playbackNodeAtPoseSnapshot = _appliedAnimation, playbackTimeAtPoseSnapshot = _appliedPlaybackTime,
            fadingFromAtPoseSnapshot = _appliedFadingFrom, fadingTimeAtPoseSnapshot = _appliedFadingTime,
            playbackSnapshotMeaning = "playback state observed at skeleton signal; does not certify which clip contributed this pose", poseRotations = _finalBoneRotations, posePhysicsFrame = _posePhysics, poseRenderFrame = _poseRender, bones
        };
        _trace.WriteLine(JsonSerializer.Serialize(row, JsonOptions));
    }

    // Godot specifies SkeletonUpdated after all SkeletonModifier3D processing. Snapshot here:
    // modifiers can restore their input poses before a later FramePostDraw query.
    // https://docs.godotengine.org/en/4.7/classes/class_skeleton3d.html#signals
    private void SnapshotFinalBones()
    {
        if (_finished || !GodotObject.IsInstanceValid(_skeleton) || !_skeleton.IsInsideTree()) return;
        var bones = new Dictionary<string, float[]>();
        var rotations = new Dictionary<string, float[]>();
        for (int i = 0; i < _skeleton.GetBoneCount(); i++)
        {
            string name = _skeleton.GetBoneName(i);
            bones[name] = Vec(_skeleton.ToGlobal(_skeleton.GetBoneGlobalPose(i).Origin));
            Quaternion q = _skeleton.GetBonePoseRotation(i);
            rotations[name] = new[] { q.X, q.Y, q.Z, q.W };
        }
        _finalBoneRotations = rotations;
        _finalBones = bones;
        var player = _players.GetNodeOrNull<PlayerController>("1");
        if (player != null)
        {
            // Freeze the adapter's exact display input beside the modified final pose.
            // A remote RPC may advance live phase/hand before the later draw callback.
            var display = player.AnimationDisplaySnapshot;
            _poseDisplayMove = display.MoveId;
            _poseDisplayPhase = display.Phase.ToString();
            _poseDisplayFrame = display.FrameInPhase;
            _poseDisplaySimulatedLocally = display.SimulatedLocally;
            _poseDisplayHand = display.Hand.ToString();
            var metrics = player.ContinuityCrossoverPoseMetrics;
            _poseApplied = metrics.Applied;
            _poseMaxReachError = metrics.MaxReachError;
            _poseClampCount = metrics.ClampCount;
            _poseSampleFrame = metrics.SampleFrame;
            _appliedAnimation = player.ActiveAnimNodeForHarness;
            _appliedPlaybackTime = player.ContinuityPlaybackTime;
            _appliedFadingFrom = player.ContinuityFadingFrom;
            _appliedFadingTime = player.ContinuityFadingTime;
        }
        _posePhysics = Engine.GetPhysicsFrames();
        _poseRender = Engine.GetProcessFrames();
    }

    private static bool HasVisibleMesh(Node root)
    {
        if (root is MeshInstance3D mesh && mesh.Mesh != null && mesh.IsVisibleInTree()) return true;
        foreach (Node child in root.GetChildren()) if (HasVisibleMesh(child)) return true;
        return false;
    }

    private static Skeleton3D FindSkeleton(Node root)
    {
        if (root is Skeleton3D skeleton) return skeleton;
        foreach (Node child in root.GetChildren()) { var found = FindSkeleton(child); if (found != null) return found; }
        return null;
    }
    private void Event(string kind, string detail) => _events.Add(new { tick = _tick, kind, detail });
    private void Finish(string failure)
    {
        if (_finished) return;
        _finished = true;
        _failure = failure.Length > 0 ? failure : _samples == 0 ? "no trace samples" : _gaps > 0 ? "skipped physics frames during measurement" : "";
        RenderingServer.FramePostDraw -= SampleDraw;
        foreach (string action in new[] { "move_forward", "move_right", "aim_left", "aim_right" }) Input.ActionRelease(action);
        if (GodotObject.IsInstanceValid(_skeleton)) _skeleton.SkeletonUpdated -= SnapshotFinalBones;
        _trace?.Dispose();
        if (!string.IsNullOrEmpty(_output) && Directory.Exists(_output))
            File.WriteAllText(Path.Combine(_output, "manifest.json"), JsonSerializer.Serialize(new
            {
                schema = "hooper-crossover-continuity/v1", mutation = _disableCrossoverPose ? "disable-crossover-pose" : "none", mutationApplied = _poseMutationApplied, result = _failure.Length == 0 ? "captured" : "failed", failure = _failure,
                commit = _commit, role = _role, motion = _motion, origin = _origin, requestedCycles = _count, completedCycles = _completed,
                loseBall = _loseBall, ballLossApplied = _ballLost, ballLossRecipient = _lossRecipient, holdMovementThroughReturn = _holdMovement, remotePaced = _remotePaced, remoteTiming = _remotePaced ? "paced fixed-fps diagnostic; not real-time" : _role == "local" ? "not a remote capture" : "real-time", diagnosticServerDelayMs = _delayMs, pacingMeaning = "wall-clock-only diagnostic pacing with fixed simulation delta; no real-time latency/performance claim", fixtureInitialHostSpawnX = _fixtureSpawnX,
                engine = Engine.GetVersionInfo()["string"].AsString(), physicsTicksPerSecond = Engine.PhysicsTicksPerSecond,
                commandLine = OS.GetCmdlineArgs(), userArgs = OS.GetCmdlineUserArgs(), renderer = RenderingServer.GetVideoAdapterName(), displayDriver = DisplayServer.GetName(),
                camera = _camera == null ? Array.Empty<float>() : Transform(_camera.GlobalTransform), cameraFov = _camera?.Fov, ballRadius = _ball?.BallRadius, betweenCycleNeutralTicks = 36, finalTailNeutralTicks = 36,
                sampleTiming = (!_images && DisplayServer.GetName() == "headless" ? "HEADLESS DIAGNOSTIC _Process state only; " : "FramePostDraw state and image; ") + " SkeletonUpdated final modifier pose with its own frame stamps; production animation callback unchanged", samples = _samples, skippedPhysicsFrames = _gaps, allowedMaxPhysicsGap = _maxPhysicsGap, observedMaxPhysicsGap = _observedMaxPhysicsGap,
                cadenceMeaning = _maxPhysicsGap == 1 ? "every physics tick rendered" : "up to two 60Hz physics ticks per rendered frame; normal real-time 30Hz display; not full physics-frame coverage", duplicateRenderedPhysicsFrames = _duplicateFrames, poseFrameMismatches = _poseFrameMismatches, events = _events,
                verdict = "diagnostic capture only; no continuity or feel acceptance threshold"
            }, new JsonSerializerOptions(JsonOptions) { WriteIndented = true }));
        GD.Print($"[crossover-continuity] {(_failure.Length == 0 ? "CAPTURED" : "FAIL: " + _failure)} cycles={_completed} samples={_samples}");
        GetTree().Quit(_failure.Length == 0 ? 0 : 1);
    }
    private static float[] Vec(Vector3 p) => new[] { p.X, p.Y, p.Z };
    private static float[] Transform(Transform3D t) => Vec(t.Origin).Concat(Vec(t.Basis.X)).Concat(Vec(t.Basis.Y)).Concat(Vec(t.Basis.Z)).ToArray();
}
