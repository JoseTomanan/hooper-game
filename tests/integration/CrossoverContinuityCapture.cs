using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
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
    private string _move = "crossover";
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
    private BlockingCollection<(Image Image, string Path)> _imageQueue;
    private Task _imageWriter;
    private readonly ConcurrentQueue<string> _imageFailures = new();
    private NetworkManager _network;
    private Node _players;
    private BallController _ball;
    private Camera3D _camera;
    private Skeleton3D _skeleton;
    private Dictionary<string, float[]> _finalBones = new();
    private Dictionary<string, float[]> _finalBoneRotations = new();
    private ulong _posePhysics, _poseRender;
    private string _appliedAnimation = "", _appliedFadingFrom = "", _appliedClip = "";
    private double _appliedPlaybackTime, _appliedFadingTime;
    private string _poseDisplayMove = "", _poseDisplayPhase = "Inactive", _poseDisplayHand = "";
    private int _poseDisplayFrame;
    private long _posePresentationTick = -1;
    private int _presentationDelayTicks;
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
        _move = Read("--capture-move", "crossover");
        _commit = Read("--capture-commit", "unknown");
        _images = Read("--capture-images", "true") != "false";
        _disableCrossoverPose = Read("--capture-disable-crossover-pose", "false") == "true";
        _holdMovement = Read("--capture-hold-movement", "false") == "true";
        _loseBall = Read("--capture-lose-ball", "none");
        _count = int.Parse(Read("--capture-count", "1"));
        _port = int.Parse(Read("--capture-port", "23464"));
        _delayMs = int.Parse(Read("--capture-delay-ms", "0"));
        _presentationDelayTicks = int.Parse(Read("--capture-presentation-delay-ticks", "0"));
        _maxPhysicsGap = int.Parse(Read("--capture-max-physics-gap", _role == "client" ? "2" : "1"));
        _output = Read("--capture-output", "");
        if (!new[] { "crossover", "behindtheback", "hesitation", "stepback", "idle" }.Contains(_move) || _output.Length == 0 || !new[] { "local", "server", "client" }.Contains(_role) ||
            !new[] { "stationary", "moving" }.Contains(_motion) || !new[] { "Left", "Right" }.Contains(_origin) ||
            (_count != 1 && _count != 3) || !new[] { "none", "early", "late" }.Contains(_loseBall) || (_loseBall != "none" && (_role == "local" || _count != 1)) || (_holdMovement && (_motion != "moving" || _count != 1)) || _delayMs < 0 || _delayMs > 200 || (_delayMs > 0 && _role != "server") || (_presentationDelayTicks != 0 && (_presentationDelayTicks != 6 || _role != "client" || _count != 1)) || _maxPhysicsGap < 1 || _maxPhysicsGap > 2) { Finish("invalid capture arguments"); return; }
        _output = ProjectSettings.GlobalizePath(_output);
        if (Directory.Exists(_output) && Directory.EnumerateFileSystemEntries(_output).Any(path => Path.GetFileName(path) != "capture.avi"))
        { _output = ""; Finish("capture output must be fresh"); return; }
        Directory.CreateDirectory(_output);
        _trace = new StreamWriter(Path.Combine(_output, "trace.jsonl"));
        if (_images)
        {
            _imageQueue = new BlockingCollection<(Image, string)>(8);
            _imageWriter = Task.Run(WriteImages);
        }
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
                // #424's left-origin cut travels toward the camera. Put this
                // three-cycle fixture deeper on court so its final rightward
                // run stays framed, without changing camera or gameplay motion.
                float spawnZ = _origin == "Left" ? spawn.Z - 1.5f : spawn.Z;
                _network.HostSpawn = new Vector3(_fixtureSpawnX.Value, spawn.Y, spawnZ);
                Event("fixture-setup", $"HostSpawn.X={_fixtureSpawnX:R} before HostGame; centers three rightward runs in unchanged camera");
                Event("fixture-setup", $"HostSpawn.Z={spawnZ:R} before HostGame; depth accommodates moving lateral cut");
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
        if (_role == "client")
        {
            _measuring = true; _inCycle = false; _completed = 0;
            _ball.ConfigureContinuityPresentationDelay(_presentationDelayTicks, () =>
            {
                var host = _players.GetNodeOrNull<PlayerController>("1");
                return host != null && host.HandSide.ToString() != _origin
                    && host.ContinuityReceivedMoveId == _move
                    && host.ContinuityReceivedPhase is MovePhase.Active or MovePhase.Recovery;
            });
            Event("measurement-begin", "server signalled measured window");
            RpcId(1, nameof(MeasurementReady));
        }
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
        // Downward hold now commits on sample 13; preparation and horizontal
        // captures retain their original pulses.
        if (_pulseTick >= 0 && _tick == _pulseTick + (_measuring && _move == "stepback" ? 18 : 8))
        { SetInput("aim_left", false); SetInput("aim_right", false); SetInput("aim_down", false); SetInput("move_size_up", false); }
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
        if (_measuring && _move == "idle") { _expectedHand = player.HandSide.ToString(); _inCycle = true; return; }
        if (_measuring && _move is "behindtheback" or "hesitation") SetInput("move_size_up", true);
        bool swap = !_measuring || _move is "crossover" or "behindtheback";
        _expectedHand = swap ? (player.HandSide == HandSide.Right ? "Left" : "Right") : player.HandSide.ToString();
        SetInput(_measuring && _move == "stepback" ? "aim_down" : _expectedHand == "Left" ? "aim_left" : "aim_right", true);
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
            _lastInactiveVelocity = player.DisplayVelocity;
            _lastInactiveHeading = player.DisplayHeading;
            _lastInactiveTick = _tick;
        }
        if (!_inCycle && player.DisplayMoveId() == _move && player.DisplayMove().phase != MovePhase.Inactive)
        {
            _inCycle = true;
            // Inferred from Startup's current hand; remote observation may first see Active.
            if (_role == "client") _expectedHand = _move is "hesitation" or "stepback" ? player.DisplayHandSide.ToString() : player.DisplayMove().phase == MovePhase.Startup
                ? (player.DisplayHandSide == HandSide.Right ? "Left" : "Right") : player.DisplayHandSide.ToString();
            float entrySpeed = new Vector2(_lastInactiveVelocity.X, _lastInactiveVelocity.Z).Length();
            Event("recognized-" + _move, $"destination={_expectedHand}; entrySpeed={entrySpeed:R}; entryHeading={_lastInactiveHeading:R}; observedStartupSpeed={player.DisplayVelocity.Length():R}; entrySample=last-inactive-physics-tick; entrySampleTick={_lastInactiveTick}");
            if (_role != "client" && _motion == "moving" && entrySpeed < 5.9f)
            { Finish($"moving entry speed {entrySpeed:R} below 5.9"); return; }
        }
        if (_role == "client" && _move == "idle" && !_inCycle && _completed == 0)
        { _inCycle = true; _pulseTick = _tick; _expectedHand = player.DisplayHandSide.ToString(); }
        bool expectedNeutral = _move == "stepback"
            ? _ball.State == BallState.Held && player.ActiveAnimNodeForHarness == "Held" + _expectedHand
            : _loseBall == "none"
            ? player.DisplayHandSide.ToString() == _expectedHand && player.ActiveAnimNodeForHarness == "Dribble" + _expectedHand
            : _ballLost && _sawLossRecovery && !player.DisplayDribbling() && player.ActiveAnimNodeForHarness == "Locomotion";
        if (_inCycle && (_move != "idle" || _tick - _pulseTick >= 20) && player.DisplayMove().phase == MovePhase.Inactive && expectedNeutral && !_ball.SweepActiveForHarness)
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
            if (image == null || image.IsEmpty())
            { Finish("image capture failed"); return; }
            // GetImage/readback stays on the render thread. Each copied Image is
            // handed exclusively to one writer; no live scene or texture crosses
            // threads. Compression/disk I/O must not manufacture network jitter.
            // https://docs.godotengine.org/en/4.7/tutorials/performance/thread_safe_apis.html#resources
            if (!_imageQueue.TryAdd((image, Path.Combine(_output, file))))
            { image.Dispose(); Finish("image writer backlog exceeded eight frames"); return; }
        }
        var model = p.GetNodeOrNull<Node3D>("CharacterModel");
        Vector3 anchor = p.DisplayRootPosition + Vector3.Up;
        Vector2 screen = _camera.UnprojectPosition(anchor);
        bool framed = !_camera.IsPositionBehind(anchor) && _camera.IsPositionInFrustum(anchor) && GetViewport().GetVisibleRect().HasPoint(screen);
        bool visibleMeshExists = HasVisibleMesh(p);
        bool poseFrameMatches = _posePhysics == physics;
        if (_measuring && !poseFrameMatches) _poseFrameMismatches++;
        if (_measuring && (!_camera.Current || !framed || (_images && (!visibleMeshExists || bones.Count < 65))))
        { Finish($"render fixture invalid: cameraCurrent={_camera.Current}, framed={framed}, visibleMesh={visibleMeshExists}, bones={bones.Count}"); return; }
        var view = _role == "client" ? _ball.GetPresentationFrame() : null;
        var shownBall = _ball.RenderedPresentationTick >= 0 ? view?.Ball : null;
        BallState shownBallState = shownBall?.State ?? _ball.State;
        float shownDribblePhase = shownBall?.DribblePhase ?? _ball.DribblePhaseForHarness;
        bool shownSweepActive = shownBall?.SweepActive ?? _ball.SweepActiveForHarness;
        int shownSweepTicks = shownBall?.SweepTicks ?? _ball.ContinuitySweepTicks;
        int shownSweepDuration = shownBall?.SweepDurationTicks ?? _ball.ContinuitySweepDurationTicks;
        Transform3D displayRoot = p.GlobalTransform;
        displayRoot.Origin = p.DisplayRootPosition;
        var row = new
        {
            sample = _samples++, tick = _tick, physicsFrame = physics, renderFrame = Engine.GetProcessFrames(), file,
            measuring = _measuring, preparing = _preparing && !_measuring, trial = _completed + 1, completedCycles = _completed, role = _role, localPlayer = p.ContinuityIsLocalPlayer,
            authoritativeMove = p.CurrentMoveIdForHarness, authoritativePhase = p.PhaseForHarness.ToString(), authoritativeFrame = p.FrameInPhaseForHarness,
            displayMove = p.DisplayMoveId(), displayPhase = p.DisplayMove().phase.ToString(), hand = p.DisplayHandSide.ToString(),
            liveHand = p.HandSide.ToString(), liveHeading = p.Heading, liveRoot = Transform(p.GlobalTransform),
            receivedMove = p.ContinuityReceivedMoveId, receivedPhase = p.ContinuityReceivedPhase.ToString(), receivedFrame = p.ContinuityReceivedFrame,
            inferredOrigin = p.DisplayMove().phase == MovePhase.Startup ? p.DisplayHandSide.ToString() : p.DisplayHandSide == HandSide.Right ? "Left" : "Right",
            originInference = "phase/hand inference; inactive value is not an origin latch",
            cameraCurrent = _camera.Current, framed, visibleMeshExists, poseFrameMatches, screen = new[] { screen.X, screen.Y },
            heading = p.DisplayHeading, velocity = Vec(p.DisplayVelocity), root = Transform(displayRoot), model = model == null ? Array.Empty<float>() : Transform(model.GlobalTransform),
            ball = Vec(_ball.RenderedWorldPosition), liveBall = Vec(_ball.GlobalPosition), ballRadius = _ball.BallRadius,
            ballState = shownBallState.ToString(), ballHolderPeerId = shownBall?.HolderPeerId ?? _ball.StateMachine.HolderPeerId,
            liveBallState = _ball.State.ToString(), liveBallHolderPeerId = _ball.StateMachine.HolderPeerId,
            displayDribbling = p.DisplayDribbling(), dribblePhase = shownDribblePhase,
            neutralApex = p.DisplayDribbling() && shownBallState == BallState.Dribbling && !shownSweepActive && p.DisplayMove().phase == MovePhase.Inactive &&
                Math.Min(shownDribblePhase, 1f - shownDribblePhase) <= 0.035f,
            sweepActive = shownSweepActive, sweepTicks = shownSweepTicks,
            sweepDurationTicks = shownSweepDuration, sweepProgress = shownSweepDuration > 0 ? shownSweepTicks / (float)shownSweepDuration : 1f,
            resolvedClip = ResolvedClip(p), animation = p.ActiveAnimNodeForHarness, playbackTime = p.ContinuityPlaybackTime,
            fadingFrom = p.ContinuityFadingFrom, fadingTime = p.ContinuityFadingTime,
            poseDisplayMove = _poseDisplayMove, poseDisplayPhase = _poseDisplayPhase,
            poseDisplayFrame = _poseDisplayFrame, poseDisplayHand = _poseDisplayHand,
            poseDisplaySimulatedLocally = _poseDisplaySimulatedLocally,
            posePresentationTick = _posePresentationTick, ballPresentationTick = _ball.RenderedPresentationTick,
            selectedPresentationTick = view?.Tick ?? -1, latestReceivedPresentationTick = _ball.LatestPresentationReceivedTick,
            presentationHolding = _ball.PresentationHolding, presentationResyncCount = _ball.PresentationResyncCount,
            crossoverPoseApplied = _poseApplied, crossoverPoseMaxReachError = _poseMaxReachError,
            crossoverPoseClampCount = _poseClampCount, crossoverPoseSampleFrame = _poseSampleFrame, crossoverPoseSamplePhysicsFrame = _posePhysics,
            animationCallback = p.ContinuityAnimationCallback, playbackNodeAtPoseSnapshot = _appliedAnimation, resolvedClipAtPoseSnapshot = _appliedClip, playbackTimeAtPoseSnapshot = _appliedPlaybackTime,
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
            _posePresentationTick = display.PresentationTick;
            _poseDisplayHand = display.Hand.ToString();
            var metrics = player.ContinuityCrossoverPoseMetrics;
            _poseApplied = metrics.Applied;
            _poseMaxReachError = metrics.MaxReachError;
            _poseClampCount = metrics.ClampCount;
            _poseSampleFrame = metrics.SampleFrame;
            _appliedAnimation = player.ActiveAnimNodeForHarness;
            _appliedClip = ResolvedClip(player);
            _appliedPlaybackTime = player.ContinuityPlaybackTime;
            _appliedFadingFrom = player.ContinuityFadingFrom;
            _appliedFadingTime = player.ContinuityFadingTime;
        }
        _posePhysics = Engine.GetPhysicsFrames();
        _poseRender = Engine.GetProcessFrames();
    }

    // A state name alone cannot prove its AnimationNodeAnimation binding.
    // https://docs.godotengine.org/en/4.7/classes/class_animationnodestatemachine.html#class-animationnodestatemachine-method-get-node
    private static string ResolvedClip(PlayerController player)
    {
        var tree = player.GetNode<AnimationTree>("AnimationTree");
        var machine = (AnimationNodeStateMachine)tree.TreeRoot;
        return machine.GetNode(player.ActiveAnimNodeForHarness) is AnimationNodeAnimation clip
            ? clip.Animation.ToString() : "<blend-space>";
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
        if (_imageQueue != null)
        {
            _imageQueue.CompleteAdding();
            try { _imageWriter.GetAwaiter().GetResult(); }
            catch (Exception error) { _imageFailures.Enqueue("PNG writer task: " + error.Message); }
            if (_imageFailures.TryPeek(out string imageFailure)) _failure = imageFailure;
            _imageQueue.Dispose();
        }
        RenderingServer.FramePostDraw -= SampleDraw;
        foreach (string action in new[] { "move_forward", "move_right", "aim_left", "aim_right", "aim_down", "move_size_up" }) Input.ActionRelease(action);
        if (GodotObject.IsInstanceValid(_skeleton)) _skeleton.SkeletonUpdated -= SnapshotFinalBones;
        _trace?.Dispose();
        if (!string.IsNullOrEmpty(_output) && Directory.Exists(_output))
            File.WriteAllText(Path.Combine(_output, "manifest.json"), JsonSerializer.Serialize(new
            {
                schema = "hooper-crossover-continuity/v1", mutation = _disableCrossoverPose ? "disable-crossover-pose" : "none", mutationApplied = _poseMutationApplied, result = _failure.Length == 0 ? "captured" : "failed", failure = _failure,
                move = _move, commit = _commit, role = _role, motion = _motion, origin = _origin, requestedCycles = _count, completedCycles = _completed,
                loseBall = _loseBall, ballLossApplied = _ballLost, ballLossRecipient = _lossRecipient, holdMovementThroughReturn = _holdMovement, remotePaced = _remotePaced, remoteTiming = _remotePaced ? "paced fixed-fps diagnostic; not real-time" : _role == "local" ? "not a remote capture" : "real-time", diagnosticServerDelayMs = _delayMs, pacingMeaning = "wall-clock-only diagnostic pacing with fixed simulation delta; no real-time latency/performance claim", fixtureInitialHostSpawnX = _fixtureSpawnX,
                presentationBuffered = _role == "client", presentationDelayTicks = _presentationDelayTicks,
                presentationDelayApplied = _ball?.ContinuityPresentationDelayApplied ?? false,
                presentationActualDelayTicks = _ball?.ContinuityActualDelayTicks ?? 0,
                presentationDelayedFrames = _ball?.ContinuityDelayedFrameCount ?? 0,
                presentationDelayedStartupCounter = _ball?.ContinuityDelayedStartupCounter ?? -1,
                presentationDelayUnderflowObserved = _ball?.ContinuityPresentationUnderflowObserved ?? false,
                presentationReleaseWaitTicks = _ball?.ContinuityReleaseWaitTicks ?? 0,
                presentationResyncCount = _ball?.PresentationResyncCount ?? 0,
                presentationEnvelopeBytes = _ball?.PresentationEnvelopeBytes ?? 0,
                imageWriter = "FramePostDraw GPU readback; exclusive copied Images compressed by one background writer; bounded eight-frame queue drained before success",
                engine = Engine.GetVersionInfo()["string"].AsString(), physicsTicksPerSecond = Engine.PhysicsTicksPerSecond,
                commandLine = OS.GetCmdlineArgs(), userArgs = OS.GetCmdlineUserArgs(), renderer = RenderingServer.GetVideoAdapterName(), displayDriver = DisplayServer.GetName(),
                camera = _camera == null ? Array.Empty<float>() : Transform(_camera.GlobalTransform), cameraFov = _camera?.Fov, ballRadius = _ball?.BallRadius, betweenCycleNeutralTicks = 36, finalTailNeutralTicks = 36,
                sampleTiming = (!_images && DisplayServer.GetName() == "headless" ? "HEADLESS DIAGNOSTIC _Process state only; " : "FramePostDraw state and image; ") + " SkeletonUpdated final modifier pose with its own frame stamps; production remote presentation uses its manual source-tick clock", samples = _samples, skippedPhysicsFrames = _gaps, allowedMaxPhysicsGap = _maxPhysicsGap, observedMaxPhysicsGap = _observedMaxPhysicsGap,
                cadenceMeaning = _maxPhysicsGap == 1 ? "every physics tick rendered" : "up to two 60Hz physics ticks per rendered frame; normal real-time 30Hz display; not full physics-frame coverage", duplicateRenderedPhysicsFrames = _duplicateFrames, poseFrameMismatches = _poseFrameMismatches, events = _events,
                verdict = "diagnostic capture only; no continuity or feel acceptance threshold"
            }, new JsonSerializerOptions(JsonOptions) { WriteIndented = true }));
        GD.Print($"[crossover-continuity] {(_failure.Length == 0 ? "CAPTURED" : "FAIL: " + _failure)} cycles={_completed} samples={_samples}");
        GetTree().Quit(_failure.Length == 0 ? 0 : 1);
    }
    private void WriteImages()
    {
        foreach (var pending in _imageQueue.GetConsumingEnumerable())
        {
            try
            {
                if (pending.Image.SavePng(pending.Path) != Error.Ok) _imageFailures.Enqueue("PNG write failed: " + pending.Path);
            }
            catch (Exception error) { _imageFailures.Enqueue("PNG writer: " + error.Message); }
            finally { pending.Image.Dispose(); }
        }
    }

    private static float[] Vec(Vector3 p) => new[] { p.X, p.Y, p.Z };
    private static float[] Transform(Transform3D t) => Vec(t.Origin).Concat(Vec(t.Basis.X)).Concat(Vec(t.Basis.Y)).Concat(Vec(t.Basis.Z)).ToArray();
}
