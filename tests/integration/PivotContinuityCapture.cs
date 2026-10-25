using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Networking;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

/// <summary>#416 diagnostic only: production input, scene, callback and remote presentation.</summary>
public partial class PivotContinuityCapture : Node
{
    private string _role, _output, _commit, _case, _gesture, _stage = "boot", _hand;
    private float _pivotXfade = -1;
    private float _angle = 180, _entryHeading, _targetHeading, _initialAngle;
    private Vector2 _gestureWish;
    private int _localRenderDelayMs;
    private bool _probeApplied, _disablePivotPose, _mutationApplied, _paced, _fixedFps, _protection, _resetOwnerPresentation;
    private PivotPose _pivotPose;
    private bool _images, _finished, _ready, _readySent, _finishSent, _finishAck, _clientFinished;
    private bool _sawPivot, _sawNeutral, _measuring, _pendingEnd, _cycleAck;
    private BlockingCollection<(Image Image, string Path)> _imageQueue;
    private Task _imageWriter;
    private readonly ConcurrentQueue<string> _imageErrors = new();
    private int _tick, _start = -1, _stageStart, _cycle, _completed, _samples, _imagesWritten, _finishTick;
    private Node _players;
    private BallController _ball;
    private Camera3D _camera;
    private Skeleton3D _skeleton;
    private StreamWriter _trace;
    private readonly List<object> _events = new();
    private readonly List<object> _gestureEntries = new();
    private readonly System.Diagnostics.Stopwatch _wall = System.Diagnostics.Stopwatch.StartNew();
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly string[] Actions = { "move_forward", "move_backward", "move_left", "move_right", "aim_left", "aim_right", "move_protect" };
    private PlayerController Subject => _players?.GetNodeOrNull<PlayerController>("1");
    private bool ExpectedPivot => _gesture != "forward" && _angle > 90;

    public override void _Ready()
    {
        var args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        string Read(string key, string fallback) => HarnessArgs.ReadArg(args, key, fallback);
        _role = Read("--capture-role", "local"); _case = Read("--capture-case", "idle");
        _gesture = Read("--capture-gesture", "flick"); _commit = Read("--capture-commit", "unknown");
        _angle = float.Parse(Read("--capture-angle", "180"), System.Globalization.CultureInfo.InvariantCulture);
        _images = Read("--capture-images", "false") == "true";
        _localRenderDelayMs = int.Parse(Read("--capture-local-render-delay-ms", "0"));
        _disablePivotPose = Read("--capture-disable-pivot-pose", "false") == "true";
        _paced = Read("--capture-paced", "false") == "true";
        _protection = Read("--capture-protection", "false") == "true";
        _resetOwnerPresentation = Read("--capture-reset-owner-presentation", "false") == "true";
        _fixedFps = Read("--capture-fixed-fps", "false") == "true";
        _pivotXfade = float.Parse(Read("--capture-pivot-xfade", "-1"), System.Globalization.CultureInfo.InvariantCulture);
        _output = ProjectSettings.GlobalizePath(Read("--capture-output", ""));
        if (!new[] { "local", "server", "client" }.Contains(_role) || !new[] { "idle", "run", "left", "right" }.Contains(_case) ||
            !new[] { "flick", "held", "forward" }.Contains(_gesture) || (_protection && (_case != "right" || _gesture != "held")) || (_paced && _role == "local") || (_resetOwnerPresentation && (_role != "local" || _case != "run" || _gesture != "held" || _protection)) || _commit == "unknown" ||
            !float.IsFinite(_angle) || _angle <= 0 || _angle > 180 || _localRenderDelayMs < 0 ||
            (_localRenderDelayMs > 0 && (_role != "local" || !_images || !_fixedFps)) || string.IsNullOrEmpty(_output) || Directory.Exists(_output))
        { _output = ""; Finish("invalid arguments or non-fresh output"); return; }
        Directory.CreateDirectory(_output);
        if (_images) { _imageQueue = new(64); _imageWriter = Task.Run(WriteImages); }
        _trace = new StreamWriter(Path.Combine(_output, "trace.jsonl"));
        var main = GetNode("Main"); _players = main.GetNode("Players");
        _ball = main.GetNode<BallController>("Ball"); _camera = main.GetNode<Camera3D>("Camera3D");
        var network = main.GetNode<NetworkManager>("NetworkManager");
        int port = int.Parse(Read("--capture-port", "23468"));
        if (_role == "client")
        {
            Multiplayer.ConnectionFailed += () => Finish("connection failed");
            Multiplayer.ServerDisconnected += () => { if (!_finished && !_clientFinished) Finish("server disconnected before completion"); };
            network.JoinGame("127.0.0.1", port);
        }
        else network.HostGame(port);
        // Official Godot 4.7: GPU readback belongs after frame_post_draw.
        // https://docs.godotengine.org/en/4.7/classes/class_renderingserver.html#signals
        RenderingServer.FramePostDraw += Draw;
        Event("boot", "normal Main/HostGame; diagnostic only; unchanged gameplay camera");
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ObserverReady() { if (_role == "server" && Multiplayer.GetRemoteSenderId() > 1) _ready = true; }
    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void BeginCycle(int cycle, string hand)
    {
        if (_role != "client") return;
        _cycle = cycle; _hand = hand; _measuring = true; _stage = "lead-in"; _sawPivot = false; _sawNeutral = false;
        Event("cycle-begin", cycle.ToString());
    }
    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void EndCycle()
    {
        if (_role != "client") return;
        _pendingEnd = true;
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void CycleAck() { if (_role == "server" && Multiplayer.GetRemoteSenderId() > 1) _cycleAck = true; }
    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void StageMarker(string stage) { if (_role == "client") { _stage = stage; _stageStart = _tick; if (stage == "measured") { _sawPivot = false; _sawNeutral = false; } } }
    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void GestureInfo(float entry, float target, float actualAngle, Vector2 wish)
    {
        _entryHeading = entry; _targetHeading = target; _initialAngle = actualAngle; _gestureWish = wish;
        _gestureEntries.Add(new { cycle = _cycle, tick = _tick, entryHeading = entry, targetHeading = target,
            requestedAngleDegrees = _angle, actualSignedAngleDegrees = actualAngle, wish = new[] { wish.X, wish.Y } });
    }
    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void CaptureFinished()
    {
        if (_role != "client") return;
        if (_completed != 3) { Finish("remote did not complete all three cycles"); return; }
        _clientFinished = true; _finishTick = _tick; RpcId(1, nameof(FinishedAck));
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void FinishedAck()
    { if (_role == "server" && Multiplayer.GetRemoteSenderId() > 1) { _finishAck = true; _finishTick = _tick; } }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _tick++;
        if (_wall.Elapsed.TotalSeconds > 75 || _tick > 6000) { Finish("capture timeout"); return; }
        if (_clientFinished && _tick - _finishTick > 8) { Finish(""); return; }
        if (_finishAck && _tick - _finishTick > 30) { Finish(""); return; }
        var p = Subject; if (p == null) return;
        _pivotPose ??= p.FindChild("PivotPose", recursive: true, owned: false) as PivotPose;
        if (_disablePivotPose && !_mutationApplied)
        {
            if (_pivotPose == null) { Finish("disable-pivot-pose requested but production modifier missing"); return; }
            _pivotPose.Active = false; _mutationApplied = true;
            Event("diagnostic-mutation", "disabled production PivotPose.Active before measured input");
        }
        if (_pivotXfade >= 0 && !_probeApplied)
        {
            var tree = FindTree(p);
            if (tree?.TreeRoot is not AnimationNodeStateMachine root) { Finish("diagnostic probe missing state machine"); return; }
            // Deep copy isolates this subject's transitions from every other actor.
            // https://docs.godotengine.org/en/4.7/classes/class_resource.html#class-resource-method-duplicate
            var copy = (AnimationNodeStateMachine)root.Duplicate(true);
            int changed = 0;
            for (int i = 0; i < copy.GetTransitionCount(); i++)
                if (copy.GetTransitionFrom(i) == "Pivot" || copy.GetTransitionTo(i) == "Pivot")
                {
                    // Diagnostic hypothesis only: change direct Pivot edges and nothing else.
                    // https://docs.godotengine.org/en/4.7/classes/class_animationnodestatemachinetransition.html#class-animationnodestatemachinetransition-property-xfade-time
                    copy.GetTransition(i).XfadeTime = _pivotXfade; changed++;
                }
            tree.TreeRoot = copy; _probeApplied = true;
            Event("diagnostic-probe", $"deep-duplicated subject state machine; {changed} Pivot transition edges XfadeTime={_pivotXfade:R}");
        }
        if (_skeleton == null)
        {
            _skeleton = FindSkeleton(p);
            // Snapshot final modified bones and all metadata together, never at a later draw callback.
            // https://docs.godotengine.org/en/4.7/classes/class_skeleton3d.html#signals
            if (_skeleton != null) _skeleton.SkeletonUpdated += Snapshot;
        }
        Observe(p);
        if (_role == "client")
        {
            if (p.ContinuityIsLocalPlayer) { Finish("observer subject is local"); return; }
            if (!_readySent) { _readySent = true; RpcId(1, nameof(ObserverReady)); }
            // Completion RPC may precede the delayed presentation tail. Drain actual
            // observed pivot -> correct neutral before acknowledging this cycle.
            if (_pendingEnd && _sawNeutral)
            {
                _pendingEnd = false;
                if (!CompleteCycle()) return;
                _measuring = false; _stage = "between-cycles"; RpcId(1, nameof(CycleAck));
            }
            return;
        }
        if (_role == "server" && !_ready) return;
        if (_start < 0) { _start = _tick; _stageStart = _tick; _stage = "settle"; }
        int age = _tick - _stageStart;
        if (_stage == "settle" && age >= 60)
        {
            if (_case is "left" or "right") { InputDown("move_forward"); Stage("dribble-prep"); }
            else { _ball.SeedLooseBallForHarness(new Vector3(-6f, _ball.BallRadius, 10f)); Event("fixture-setup", "seeded loose ball away from subject for off-ball Locomotion; setup only"); StartReset(); }
        }
        else if (_stage == "dribble-prep")
        {
            if (age == 10) Release();
            if (age >= 45 && _ball.State == BallState.Dribbling)
            {
                if (_case == "left" && p.HandSide != HandSide.Left) { InputDown("aim_left"); Stage("hand-prep"); }
                else StartReset();
            }
        }
        else if (_stage == "hand-prep")
        {
            if (age == 8) Release();
            if (age >= 80 && p.HandSide == HandSide.Left && p.DisplayMove().phase == MovePhase.Inactive) StartReset();
        }
        else if (_stage == "reset")
        {
            if (age == 24) Release();
            if (age >= 55 && !p.DisplayPivoting && p.Velocity.Length() < 0.03f)
            {
                _cycle++; _hand = p.DisplayHandSide.ToString(); _sawPivot = false; _sawNeutral = false; _measuring = true;
                if (_role == "server") Rpc(nameof(BeginCycle), _cycle, _hand);
                Stage("lead-in");
            }
        }
        else if (_stage == "lead-in")
        {
            if (age == 12 && _case == "run") InputDown("move_forward");
            if (age >= 32)
            {
                if (_case == "run" && p.Velocity.Length() < 5.9f) { Finish($"run entry speed {p.Velocity.Length():R} below 5.9"); return; }
                if (_case != "run" && p.Velocity.Length() > 0.03f) { Finish("stationary entry is moving"); return; }
                _sawPivot = false; _sawNeutral = false;
                Release(); BeginGesture(p);
                if (_protection) InputDown("move_protect");
                Stage("measured"); Event("gesture", _gesture);
            }
        }
        else if (_stage == "measured")
        {
            if (_resetOwnerPresentation && age == 4)
            {
                if (!p.IsPivotingInPlace) { Finish("owner history reset requires an active local pivot"); return; }
                _ball.ResetPivotPresentationForHarness();
                Event("owner-remote-history-reset", $"cycle={_cycle}; unrelated remote history epoch={_ball.PresentationEpoch}");
            }
            if (age == (_gesture == "held" ? 25 : _gesture == "forward" ? 12 : 1)) Release();
            if (age >= 72)
            {
                if (!CompleteCycle()) return;
                _measuring = false;
                if (_role == "server")
                {
                    _cycleAck = false; Rpc(nameof(EndCycle)); Stage("cycle-wait"); return;
                }
                AdvanceAfterCycle();
            }
        }
        else if (_stage == "cycle-wait" && _cycleAck) AdvanceAfterCycle();
    }
    private void AdvanceAfterCycle()
    {
        if (_completed == 3)
        {
            Stage("finished");
            if (_role == "server") { _finishSent = true; Rpc(nameof(CaptureFinished)); }
            else Finish("");
        }
        else StartReset();
    }
    private void StartReset()
    {
        Release();
        // Setup only, between measured cycles: keep each approach clear of court
        // bounds and the observer's ordinary spawn. Never teleport during a gesture.
        Subject.GlobalPosition = new Vector3(-3f, Subject.GlobalPosition.Y, 7f);
        Event("fixture-reset", "subject root=(-3,currentY,7); between measured cycles only");
        InputDown("move_forward"); Stage("reset");
    }
    private void Stage(string value) { _stage = value; _stageStart = _tick; Event("stage", value); if (_role == "server" && _measuring) Rpc(nameof(StageMarker), value); }
    private void Observe(PlayerController p)
    {
        if (!_measuring) return;
        if (p.DisplayPivoting) _sawPivot = true;
        if (_stage == "measured" && _tick - _stageStart > 35 && (_sawPivot || !ExpectedPivot) && !p.DisplayPivoting && p.DisplayMove().phase == MovePhase.Inactive &&
            p.DisplayVelocity.Length() < 0.03f && p.DisplayHandSide.ToString() == _hand &&
            p.ActiveAnimNodeForHarness == (_case is "idle" or "run" ? "Locomotion" : "Dribble" + _hand)) _sawNeutral = true;
    }
    private bool CompleteCycle()
    {
        if ((ExpectedPivot && !_sawPivot) || (!ExpectedPivot && _sawPivot) || !_sawNeutral)
        { Finish($"cycle {_cycle} incomplete: pivot={_sawPivot} neutral={_sawNeutral}"); return false; }
        _completed++; Event("cycle-complete", _cycle.ToString()); return true;
    }
    // action_press is read by production Input.GetVector; no direct move/heading injection.
    // https://docs.godotengine.org/en/4.7/classes/class_input.html#class-input-method-action-press
    private void InputDown(string action, float strength = 1) { Input.ActionPress(action, strength); Event("input-down", strength == 1 ? action : $"{action} strength={strength:R}"); }
    private void BeginGesture(PlayerController p)
    {
        float entry = p.Heading;
        if (_gesture == "forward") InputDown("move_forward");
        else if (_angle == 180) InputDown("move_backward"); // Preserve the original #416 driver exactly.
        else
        {
            Vector2 wish;
            if (_angle == 90 && MathF.Abs(MathF.Abs(entry) - MathF.PI) < 1e-5f)
            {
                // +PI -> -PI/2 wraps one float ULP above strict 90 degrees.
                // Use the equivalent opposite cardinal quarter-turn as the exact
                // no-latch control, and retain its actual signed angle in evidence.
                wish = Vector2.Right;
            }
            else wish = HeadingMath.Forward(entry + _angle * MathF.PI / 180).Normalized();
            if (wish.X < 0) InputDown("move_left", -wish.X);
            else if (wish.X > 0) InputDown("move_right", wish.X);
            if (wish.Y < 0) InputDown("move_forward", -wish.Y);
            else if (wish.Y > 0) InputDown("move_backward", wish.Y);
        }
        // Measure the vector production actually consumes, including GetVector's
        // deadzone/normalization, rather than assuming analog strengths survived.
        Vector2 actual = Input.GetVector("move_left", "move_right", "move_forward", "move_backward");
        float target = MathF.Atan2(actual.X, actual.Y), difference = target - entry;
        while (difference > MathF.PI) difference -= 2 * MathF.PI;
        while (difference < -MathF.PI) difference += 2 * MathF.PI;
        GestureInfo(entry, target, difference * 180 / MathF.PI, actual);
        if (_role == "server") Rpc(nameof(GestureInfo), entry, target, _initialAngle, actual);
    }
    private void Release() { foreach (string action in Actions) Input.ActionRelease(action); Event("input-release", "all"); }
    private void Snapshot()
    {
        if (_finished || _trace == null || Subject == null) return;
        var p = Subject; var bones = new Dictionary<string, object>();
        for (int i = 0; i < _skeleton.GetBoneCount(); i++)
        {
            string name = _skeleton.GetBoneName(i);
            Quaternion q = _skeleton.GetBonePoseRotation(i);
            Quaternion world = (_skeleton.GlobalBasis * _skeleton.GetBoneGlobalPose(i).Basis).Orthonormalized().GetRotationQuaternion();
            bones[name] = new { world = Vec(_skeleton.ToGlobal(_skeleton.GetBoneGlobalPose(i).Origin)), localRotation = new[] { q.X, q.Y, q.Z, q.W }, worldRotation = new[] { world.X, world.Y, world.Z, world.W } };
        }
        var display = p.AnimationDisplaySnapshot;
        _trace.WriteLine(JsonSerializer.Serialize(new {
            sample = _samples++, tick = _tick, physicsFrame = Engine.GetPhysicsFrames(), renderFrame = Engine.GetProcessFrames(),
            commit = _commit, role = _role, @case = _case, gesture = _gesture, stage = _stage, stageAge = _tick - _stageStart, cycle = _cycle, measuring = _measuring,
            requestedAngleDegrees = _angle, gestureEntryHeading = _entryHeading, gestureTargetHeading = _targetHeading,
            actualSignedAngleDegrees = _initialAngle, gestureWish = new[] { _gestureWish.X, _gestureWish.Y },
            moveSpeed = p.MoveSpeed, accel = p.Accel, decel = p.Decel, maxTurnRateDeg = p.MaxTurnRateDeg, backTurnSlowFactor = p.BackTurnSlowFactor,
            localPlayer = p.ContinuityIsLocalPlayer, displayPivoting = p.DisplayPivoting, isPivotingInPlace = p.IsPivotingInPlace,
            liveHeading = p.Heading, displayHeading = p.DisplayHeading, liveVelocity = Vec(p.Velocity), displayVelocity = Vec(p.DisplayVelocity),
            liveRoot = Transform(p.GlobalTransform), displayRoot = Vec(p.DisplayRootPosition), skeletonGlobal = Transform(_skeleton.GlobalTransform),
            presentationHolding = _ball.PresentationHolding, presentationResyncCount = _ball.PresentationResyncCount, presentationEpoch = _ball.PresentationEpoch,
            latestReceivedPresentationTick = _ball.LatestPresentationReceivedTick, ballPresentationTick = _ball.RenderedPresentationTick,
            displayProtecting = p.DisplayProtecting, displayHandSide = p.DisplayHandSide.ToString(), liveHandSide = p.HandSide.ToString(), displayDribbling = p.DisplayDribbling(),
            pivotPoseApplied = _pivotPose?.AppliedForHarness ?? false, pivotSupportToe = _pivotPose?.SupportToeForHarness ?? "",
            pivotAge = _pivotPose?.AgeForHarness ?? -1, pivotContactError = _pivotPose?.ContactErrorForHarness ?? 0,
            pivotStanceAge = _pivotPose?.StanceAgeForHarness ?? -1,
            pivotSwingError = _pivotPose?.SwingErrorForHarness ?? 0,
            pivotStepping = _pivotPose?.SteppingForHarness ?? false,
            pivotSwingToe = _pivotPose?.SwingToeForHarness ?? "",
            pivotSupportTarget = Vec(_pivotPose?.SupportTargetForHarness ?? Vector3.Zero),
            pivotSwingTarget = Vec(_pivotPose?.SwingTargetForHarness ?? Vector3.Zero),
            pivotPelvisShift = _pivotPose?.PelvisShiftForHarness ?? 0, mutationApplied = _mutationApplied,
            animation = p.ActiveAnimNodeForHarness, playbackTime = p.ContinuityPlaybackTime,
            fadingFrom = p.ContinuityFadingFrom, fadingTime = p.ContinuityFadingTime, animationCallback = p.ContinuityAnimationCallback,
            displayMove = display.MoveId, displayPhase = display.Phase.ToString(), displayFrame = display.FrameInPhase,
            presentationTick = display.PresentationTick, simulatedLocally = display.SimulatedLocally, bones
        }, Json));
    }
    public override void _Process(double delta)
    {
        // Diagnostic pacing changes wall time only. --fixed-fps still supplies one
        // 1/60 simulation interval; the remote observer can render source holds.
        // https://docs.godotengine.org/en/4.7/classes/class_os.html#class-os-method-delay-msec
        if (!_finished && _paced && _role == "server") OS.DelayMsec(50);
        // Local fixed-fps captures can outrun PNG compression. Wall-time pacing
        // leaves their simulation/source clocks at60Hz; remote timing is untouched.
        if (!_finished && _localRenderDelayMs > 0) OS.DelayMsec(_localRenderDelayMs);
    }
    private void Draw()
    {
        if (_finished || !_images || !_measuring || Subject == null) return;
        // https://docs.godotengine.org/en/4.7/classes/class_viewport.html#class-viewport-method-get-texture
        var image = GetViewport().GetTexture().GetImage();
        if (image == null || image.IsEmpty()) { image?.Dispose(); Finish("image readback failed"); return; }
        if (!_imageQueue.TryAdd((image, Path.Combine(_output, $"frame-{Engine.GetProcessFrames():D6}-pose-{_samples - 1:D6}.png"))))
        { image.Dispose(); Finish("image writer backlog exceeded 64 frames"); return; }
        _imagesWritten++;
    }
    private void Event(string kind, string detail) => _events.Add(new { tick = _tick, kind, detail });
    private void Finish(string failure)
    {
        if (_finished) return; _finished = true;
        if (failure.Length == 0 && (_completed != 3 || _samples == 0)) failure = "incomplete capture";
        if (_imageQueue != null)
        {
            _imageQueue.CompleteAdding(); _imageWriter.GetAwaiter().GetResult(); _imageQueue.Dispose();
            if (_imageErrors.TryPeek(out var imageError)) failure = imageError;
        }
        RenderingServer.FramePostDraw -= Draw;
        if (_skeleton != null) _skeleton.SkeletonUpdated -= Snapshot;
        foreach (string action in Actions) Input.ActionRelease(action);
        _trace?.Dispose();
        if (!string.IsNullOrEmpty(_output) && Directory.Exists(_output))
            File.WriteAllText(Path.Combine(_output, "manifest.json"), JsonSerializer.Serialize(new {
                schema = "hooper-pivot-continuity/v1", result = failure.Length == 0 ? "captured" : "failed", failure,
                commit = _commit, role = _role, @case = _case, gesture = _gesture, requestedAngleDegrees = _angle, expectedPivot = ExpectedPivot, gestureEntries = _gestureEntries,
                protection = _protection, resetOwnerPresentation = _resetOwnerPresentation, localRenderDelayMs = _localRenderDelayMs, requestedCycles = 3, completedCycles = _completed,
                samples = _samples, images = _imagesWritten, diagnosticPivotXfade = _pivotXfade, diagnosticProbeApplied = _probeApplied, mutation = _disablePivotPose ? "disable-pivot-pose" : "none", mutationApplied = _mutationApplied, paced = _paced, fixedFps = _fixedFps, diagnosticServerDelayMs = _paced && _role == "server" ? 50 : 0,
                remoteTiming = _paced ? "paced fixed-fps diagnostic; no real-time timing claim" : _role == "local" ? "not a remote capture" : "real-time",
                pacingMeaning = _paced ? "both peers fixed simulation delta 1/60; server waits50ms per render (~20 source ticks per wall second); client source holds are normal" : _fixedFps ? "local fixed simulation delta1/60 per frame; no real-time performance claim" : "real-time simulation", finishSent = _finishSent, finishAcknowledged = _finishAck,
                engine = Engine.GetVersionInfo()["string"].AsString(), displayDriver = DisplayServer.GetName(),
                commandLine = OS.GetCmdlineArgs(), userArgs = OS.GetCmdlineUserArgs(),
                camera = _camera == null ? Array.Empty<float>() : Transform(_camera.GlobalTransform),
                sampleTiming = "each SkeletonUpdated: final evaluated bones and simultaneous state; PNG filenames reference most recent pose sample",
                verdict = "diagnostic only; headless traces make no image claim; no continuity or feel acceptance", events = _events
            }, new JsonSerializerOptions(Json) { WriteIndented = true }));
        GD.Print($"[pivot-continuity] {(failure.Length == 0 ? "CAPTURED" : "FAIL: " + failure)} cycles={_completed} samples={_samples}");
        GetTree().Quit(failure.Length == 0 ? 0 : 1);
    }
    private void WriteImages()
    {
        // Each copied Image has one exclusive owner; scene/texture access stays on main thread.
        // https://docs.godotengine.org/en/4.7/tutorials/performance/thread_safe_apis.html#resources
        foreach (var pending in _imageQueue.GetConsumingEnumerable())
        {
            try { if (pending.Image.SavePng(pending.Path) != Error.Ok) _imageErrors.Enqueue("PNG save failed: " + pending.Path); }
            catch (Exception error) { _imageErrors.Enqueue(error.Message); }
            finally { pending.Image.Dispose(); }
        }
    }
    private static AnimationTree FindTree(Node node)
    {
        if (node is AnimationTree tree) return tree;
        foreach (Node child in node.GetChildren()) { var found = FindTree(child); if (found != null) return found; }
        return null;
    }
    private static Skeleton3D FindSkeleton(Node node)
    {
        if (node is Skeleton3D skeleton) return skeleton;
        foreach (Node child in node.GetChildren()) { var found = FindSkeleton(child); if (found != null) return found; }
        return null;
    }
    private static float[] Vec(Vector3 v) => new[] { v.X, v.Y, v.Z };
    private static float[] Transform(Transform3D t) => Vec(t.Origin).Concat(Vec(t.Basis.X)).Concat(Vec(t.Basis.Y)).Concat(Vec(t.Basis.Z)).ToArray();
}

