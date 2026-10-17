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
    private bool _measurementAck, _measurementPulse, _recoveryReleased;
    private int _driveTick = -1;
    private Vector3 _lastInactiveVelocity;
    private float _lastInactiveHeading;
    private int _lastInactiveTick;
    private string _expectedHand = "";
    private string _failure = "";
    private ulong _lastPhysics;
    private int _samples, _gaps, _duplicateFrames, _poseFrameMismatches, _delayMs;
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
    private readonly List<object> _events = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public override void _Ready()
    {
        var args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        string Read(string name, string fallback) => HarnessArgs.ReadArg(args, name, fallback);
        _role = Read("--capture-role", "local");
        _motion = Read("--capture-motion", "stationary");
        _origin = Read("--capture-origin", "Right");
        _commit = Read("--capture-commit", "unknown");
        _images = Read("--capture-images", "true") != "false";
        _count = int.Parse(Read("--capture-count", "1"));
        _port = int.Parse(Read("--capture-port", "23464"));
        _delayMs = int.Parse(Read("--capture-delay-ms", "0"));
        _output = Read("--capture-output", "");
        if (_output.Length == 0 || !new[] { "local", "server", "client" }.Contains(_role) ||
            !new[] { "stationary", "moving" }.Contains(_motion) || !new[] { "Left", "Right" }.Contains(_origin) ||
            (_count != 1 && _count != 3) || _delayMs < 0 || _delayMs > 200 || (_delayMs > 0 && _role != "server")) { Finish("invalid capture arguments"); return; }
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
    private void CaptureFinished() { if (_role == "client") Finish(_completed == _count ? "" : "remote cycles incomplete"); }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _tick++;
        // Pacing is diagnostic only. Stop both runaway simulation and stalled network sessions.
        // https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.stopwatch.startnew?view=net-8.0
        int tickLimit = _role == "local" ? 900 : 6000;
        if (_tick > tickLimit || _wallClock.Elapsed.TotalSeconds > 90) { Finish("capture timeout"); return; }
        var player = _players?.GetNodeOrNull<PlayerController>("1");
        if (player == null) return;
        if (_role == "client")
        {
            if (!_clientReadySent && Multiplayer.GetUniqueId() > 1)
            { _clientReadySent = true; RpcId(1, nameof(CaptureReady)); }
            // A genuine remote display is observed without injecting its phase or hand.
            if (player.ContinuityIsLocalPlayer) { Finish("client subject is local"); return; }

            ObserveCycle(player);
            return;
        }
        if (_role == "server" && !_remoteReady) return;
        if (_startTick < 0) { _startTick = _tick; return; }
        int elapsed = _tick - _startTick;
        if (elapsed == 15) SetInput("move_forward", true);
        if (elapsed == 25) SetInput("move_forward", false);
        if (elapsed < 60 || _ball.State != BallState.Dribbling) return;
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
        if (_inCycle && _motion == "moving" && !_recoveryReleased && player.DisplayMove().phase == MovePhase.Recovery)
        {
            SetInput("move_right", false);
            _recoveryReleased = true;
        }
        bool readyForGesture = !_measurementPulse ||
            (!_inCycle && _neutralTick >= 0 && _tick - _neutralTick >= 15 && _tick - _pulseTick >= 30);
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
        if (_completed == _count && _tick - _neutralTick >= 15)
        {
            if (_role == "server") Rpc(nameof(CaptureFinished));
            Finish("");
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
        if (_inCycle && player.DisplayMove().phase == MovePhase.Inactive && player.HandSide.ToString() == _expectedHand &&
            player.ActiveAnimNodeForHarness == "Dribble" + _expectedHand && !_ball.SweepActiveForHarness)
        {
            _inCycle = false; _completed++; _neutralTick = _tick;
            Event("destination-dribble", "cycle=" + _completed);
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
        if (_measuring && _lastPhysics != 0 && physics > _lastPhysics + 1) _gaps++;
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
            ball = Vec(_ball.GlobalPosition), ballState = _ball.State.ToString(), dribblePhase = _ball.DribblePhaseForHarness,
            sweepActive = _ball.SweepActiveForHarness, sweepTicks = _ball.ContinuitySweepTicks,
            sweepDurationTicks = _ball.ContinuitySweepDurationTicks, sweepProgress = _ball.ContinuitySweepProgress,
            animation = p.ActiveAnimNodeForHarness, playbackTime = p.ContinuityPlaybackTime,
            fadingFrom = p.ContinuityFadingFrom, fadingTime = p.ContinuityFadingTime,
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
        if (_skeleton != null) _skeleton.SkeletonUpdated -= SnapshotFinalBones;
        _trace?.Dispose();
        if (!string.IsNullOrEmpty(_output) && Directory.Exists(_output))
            File.WriteAllText(Path.Combine(_output, "manifest.json"), JsonSerializer.Serialize(new
            {
                schema = "hooper-crossover-continuity/v1", result = _failure.Length == 0 ? "captured" : "failed", failure = _failure,
                commit = _commit, role = _role, motion = _motion, origin = _origin, requestedCycles = _count, completedCycles = _completed,
                diagnosticServerDelayMs = _delayMs, pacingMeaning = "wall-clock-only diagnostic pacing with fixed simulation delta; no real-time latency/performance claim", fixtureInitialHostSpawnX = _fixtureSpawnX,
                engine = Engine.GetVersionInfo()["string"].AsString(), physicsTicksPerSecond = Engine.PhysicsTicksPerSecond,
                commandLine = OS.GetCmdlineArgs(), userArgs = OS.GetCmdlineUserArgs(), renderer = RenderingServer.GetVideoAdapterName(), displayDriver = DisplayServer.GetName(),
                camera = _camera == null ? Array.Empty<float>() : Transform(_camera.GlobalTransform), cameraFov = _camera?.Fov,
                sampleTiming = (!_images && DisplayServer.GetName() == "headless" ? "HEADLESS DIAGNOSTIC _Process state only; " : "FramePostDraw state and image; ") + " SkeletonUpdated final modifier pose with its own frame stamps; production animation callback unchanged", samples = _samples, skippedPhysicsFrames = _gaps, duplicateRenderedPhysicsFrames = _duplicateFrames, poseFrameMismatches = _poseFrameMismatches, events = _events,
                verdict = "diagnostic capture only; no continuity or feel acceptance threshold"
            }, new JsonSerializerOptions(JsonOptions) { WriteIndented = true }));
        GD.Print($"[crossover-continuity] {(_failure.Length == 0 ? "CAPTURED" : "FAIL: " + _failure)} cycles={_completed} samples={_samples}");
        GetTree().Quit(_failure.Length == 0 ? 0 : 1);
    }
    private static float[] Vec(Vector3 p) => new[] { p.X, p.Y, p.Z };
    private static float[] Transform(Transform3D t) => Vec(t.Origin).Concat(Vec(t.Basis.X)).Concat(Vec(t.Basis.Y)).Concat(Vec(t.Basis.Z)).ToArray();
}
