using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Networking;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

/// <summary>Production-input diagnostic for #418. Capturing is not feel acceptance.</summary>
public partial class JumpshotFormCapture : Node
{
    private string _role, _output, _variant, _hand, _commit, _view;
    private int _tick, _start = -1, _shotTick = -1, _swapTick = -1, _samples, _releases;
    private bool _ready, _sentReady, _done, _finished, _feinted, _images, _sawShot, _sawFeintRecovery;
    private bool _paced, _legacyShot, _legacyApplied;
    private int _pacingMs;
    private string _entry = "", _subjectRole = "host";
    private int _entryOffset, _subjectPeer = 1, _observerPeer, _matrixStage, _stageTick;
    private int _resetFence = -1, _resetTicket, _resetCompleted, _completionTick = -1;
    private bool _matrix, _registered, _matrixStarted, _before440, _before440Applied;
    private bool _resumeObserved, _laterDribbleObserved;
    private bool _headingFixture;
    private string _fadeawaySubstitute;
    private bool _fadeawaySubstituteApplied;
    private float _headingAngle, _headingRate, _fixedHeadingTarget;
    private string _headingEntry;
    private int _headingMoveStart = -1;
    private NetworkManager _network;
    private Node _players;
    private BallController _ball;
    private Camera3D _camera;
    private Skeleton3D _skeleton;
    private StreamWriter _trace;
    private ulong _posePhysics;
    private object _pose = new { };
    private readonly List<object> _events = new();
    private readonly System.Diagnostics.Stopwatch _wall = System.Diagnostics.Stopwatch.StartNew();
    private static readonly string[] Actions = { "move_forward", "move_backward", "aim_left", "aim_right", "ball_shoot", "move_feint" };

    public override void _Ready()
    {
        // Observe after production player and ball physics; queued input is consumed next tick.
        ProcessPhysicsPriority = 100;
        var args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        string Read(string name, string fallback) => HarnessArgs.ReadArg(args, name, fallback);
        _role = Read("--capture-role", "local");
        _variant = Read("--capture-variant", "normal");
        _hand = Read("--capture-origin", "Right");
        _view = Read("--capture-view", "gameplay");
        _commit = Read("--capture-commit", "unknown");
        _images = Read("--capture-images", "true") != "false";
        _paced = Read("--capture-paced", "false") == "true";
        _pacingMs = int.Parse(Read("--capture-pacing-ms", "80"));
        if (_pacingMs is not (80 or 160)) throw new ArgumentException("unsupported diagnostic pacing");
        _legacyShot = Read("--capture-legacy-shot", "false") == "true";
        _entry = Read("--capture-entry", "");
        _entryOffset = int.Parse(Read("--capture-entry-offset", "0"));
        _subjectRole = Read("--capture-subject-role", "host");
        _before440 = Read("--capture-before440", "false") == "true";
        _matrix = _entry != "" || _subjectRole == "owner";
        if (_entry == "" && _matrix) _entry = "dribble";
        if (_entry is not ("" or "held" or "dribble" or "moving") || _subjectRole is not ("host" or "owner") || _entryOffset < 0)
            throw new ArgumentException("invalid #440 entry fixture");
        if (_subjectRole == "owner") _subjectPeer = 0;
        _fadeawaySubstitute = Read("--capture-fadeaway-substitute", "");
        if (_fadeawaySubstitute is not ("" or "before429" or "normal"))
            throw new ArgumentException("unknown fadeaway substitute");
        _headingFixture = Read("--capture-heading-angle", "") != "";
        _headingAngle = float.Parse(Read("--capture-heading-angle", "0"), System.Globalization.CultureInfo.InvariantCulture);
        _headingRate = float.Parse(Read("--capture-heading-rate", "900"), System.Globalization.CultureInfo.InvariantCulture);
        _headingEntry = Read("--capture-heading-entry", "stationary");
        _output = ProjectSettings.GlobalizePath(Read("--capture-output", ""));
        if (!new[] { "normal", "early", "late", "fadeaway" }.Contains(_variant) || !new[] { "Right", "Left" }.Contains(_hand) ||
            !new[] { "local", "server", "client", "owner", "observer" }.Contains(_role) || !new[] { "gameplay", "front", "side" }.Contains(_view) || Directory.Exists(_output))
        { Finish("invalid arguments or nonfresh output"); return; }
        Directory.CreateDirectory(_output);
        _trace = new StreamWriter(Path.Combine(_output, "trace.jsonl"));
        var main = GetNode<Node3D>("Main");
        _network = main.GetNode<NetworkManager>("NetworkManager");
        _players = main.GetNode("Players");
        _ball = main.GetNode<BallController>("Ball");
        _camera = main.GetNode<Camera3D>("Camera3D");
        int port = int.Parse(Read("--capture-port", "23468"));
        if (_role is "client" or "owner" or "observer")
        {
            Multiplayer.ConnectionFailed += () => Finish("connection failed");
            Multiplayer.ServerDisconnected += () => Finish(_done ? "" : "unexpected disconnect");
            _network.JoinGame("127.0.0.1", port);
        }
        else if (_role == "server" && _subjectRole == "owner") _network.StartDedicatedServer(port);
        else _network.HostGame(port);
        // Read back only after drawing. Headless runs explicitly provide state only.
        // https://docs.godotengine.org/en/4.7/classes/class_renderingserver.html#signals
        RenderingServer.FramePostDraw += Sample;
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ObserverReady() { if (Multiplayer.IsServer() && Multiplayer.GetRemoteSenderId() > 1) _ready = true; }
    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Completed() { _done = true; }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _tick++;
        if (_wall.Elapsed.TotalSeconds > 80 || _tick > 6000) { Finish("timeout"); return; }
        if (_matrix) { TickMatrix(); return; }
        var p = Subject();
        if (p == null) return;
        // #428's production rate finishes the ordinary turn. This retained
        // comparison explicitly exercises an unfinished turn at a fixture rate.
        if (_variant == "fadeaway") p.MaxTurnRateDeg = 90f;
        if (_headingFixture) p.MaxTurnRateDeg = _headingRate;
        if (_legacyShot && !_legacyApplied) ApplyLegacyShot(p);
        if (_fadeawaySubstitute != "" && !_fadeawaySubstituteApplied) ApplyFadeawaySubstitute(p);
        ObserveRelease(p);
        if (p.DisplayMoveId() == "jumpshot" && p.DisplayMove().phase == MovePhase.Startup)
        {
            if (!_sawShot && _role != "client")
            {
                if (_headingFixture && _headingEntry == "moving" && p.ShotInitiationSpeed < .1f)
                { Finish("moving-entry fixture did not have nonzero pre-plant speed"); return; }
                _events.Add(new { tick = _tick, kind = "accepted-begin-observed", phaseFrame = p.FrameInPhaseForHarness,
                    heading = p.Heading, fixedTarget = _fixedHeadingTarget, initiationSpeed = p.ShotInitiationSpeed });
            }
            _sawShot = true;
        }
        if (_feinted && p.CurrentMoveIdForHarness == "jumpshot" && p.PhaseForHarness == MovePhase.Recovery && p.FrameInPhaseForHarness >= 12)
            _sawFeintRecovery = true;
        if (_role == "client")
        {
            if (p.ContinuityIsLocalPlayer) { Finish("observer subject is local"); return; }
            if (!_sentReady && Multiplayer.GetUniqueId() > 1) { RpcId(1, nameof(ObserverReady)); _sentReady = true; }
            if (_done) Finish("");
            return;
        }
        if (_role == "server" && !_ready) return;
        if (_start < 0) _start = _tick;
        int t = _tick - _start;
        if (t == 15) Input.ActionPress("move_forward");
        if (t == 25) Input.ActionRelease("move_forward");
        if (t < 65) return;
        if (_swapTick >= 0 && _tick == _swapTick + 8) Input.ActionRelease("aim_left");
        if (_shotTick < 0)
        {
            if (p.HandSide.ToString() != _hand)
            {
                if (_swapTick < 0) { _swapTick = _tick; Input.ActionPress("aim_left"); }
                return;
            }
            if (p.PhaseForHarness != MovePhase.Inactive || (_swapTick >= 0 && _tick - _swapTick < 65)) return;
            if (_headingFixture && _headingEntry == "moving")
            {
                if (_headingMoveStart < 0) _headingMoveStart = _tick;
                // Six ticks in the established forward direction retain normal
                // shot range. Reversing here would spend the fixture in a pivot
                // plant and falsely label zero-velocity entry as moving.
                if (_tick - _headingMoveStart < 6) { Input.ActionPress("move_forward"); return; }
                Input.ActionRelease("move_forward");
            }
            if (_variant == "fadeaway" && t < 105) { Input.ActionPress("move_backward"); return; }
            Input.ActionRelease("move_backward");
            if (_variant != "fadeaway" && !(_headingFixture && _headingEntry == "moving") && p.Velocity.Length() > .03f) return;
            if (_headingFixture)
            {
                var toward = _ball.RimCenter - p.GlobalPosition;
                _fixedHeadingTarget = Mathf.Atan2(toward.X, toward.Z);
                p.SetHeadingForHarness(_fixedHeadingTarget + Mathf.DegToRad(_headingAngle));
                _events.Add(new { tick = _tick, kind = "heading-fixture", initialAngle = _headingAngle,
                    entry = _headingEntry, rate = _headingRate, fixedTarget = _fixedHeadingTarget, velocity = Vec(p.Velocity) });
            }
            else if (_variant == "fadeaway")
            {
                // (#429) The Left-hand swap can outlast t=105, which used to
                // skip the backward movement and silently capture a normal
                // shot. Set the explicit unfinished-turn fixture at the shot,
                // independently of how long reaching either hand took.
                var toward = _ball.RimCenter - p.GlobalPosition;
                p.SetHeadingForHarness(Mathf.Atan2(toward.X, toward.Z) + Mathf.Pi);
                _events.Add(new { tick = _tick, kind = "fadeaway-heading-fixture",
                    rate = p.MaxTurnRateDeg, heading = p.Heading });
            }
            _shotTick = _tick;
            Input.ActionPress("ball_shoot");
            _events.Add(new { tick = _tick, kind = "shoot-input", hand = p.HandSide.ToString(), heading = p.Heading });
        }
        if (_tick == _shotTick + 1) Input.ActionRelease("ball_shoot");
        if (!_feinted && _variant is "early" or "late" && p.PhaseForHarness == MovePhase.Startup && p.FrameInPhaseForHarness == (_variant == "early" ? 3 : 11))
        {
            Input.ActionPress("move_feint"); _feinted = true;
            _events.Add(new { tick = _tick, kind = "feint-input", phaseFrameBeforeInput = p.FrameInPhaseForHarness });
        }
        else if (_feinted) Input.ActionRelease("move_feint");
        if (_tick - _shotTick > 85)
        {
            if (_role == "server" && !_done) { Rpc(nameof(Completed)); _done = true; }
            if (_role != "server" || _tick - _shotTick > 110)
                Finish(!_sawShot ? "no production shot Startup" :
                    _variant is "early" or "late" && (!_feinted || !_sawFeintRecovery) ? "no accepted feint Recovery" :
                    _ball.SuccessfulShotReleasesForHarness != (_variant is "early" or "late" ? 0 : 1) ? "unexpected release count" : "");
        }
    }

    public override void _Process(double delta)
    {
        // Deliberate producer pacing preserves fixed 1/60 simulation steps but
        // lets the real ENet observer draw each source tick. This is diagnostic
        // coverage evidence, never a real-time network/performance claim.
        // https://docs.godotengine.org/en/4.7/classes/class_os.html#class-os-method-delay-msec
        if (!_finished && _paced && (_role == "server" || _role == "owner")) OS.DelayMsec(_pacingMs);
        // A fixed-fps headless observer otherwise runs as fast as the CPU and
        // can exhaust its fixture tick guard before the paced producers finish.
        // Keep several observations per 80ms source tick without pretending
        // this diagnostic pacing is a real-time network/performance measurement.
        if (!_finished && _matrix && _paced && _role == "observer") OS.DelayMsec(16);
        if (!_images && DisplayServer.GetName() == "headless") Sample();
    }

    private void SnapshotPose()
    {
        var p = Subject();
        if (p == null || _finished) return;
        var display = p.AnimationDisplaySnapshot;
        var body = new Transform3D(Basis.FromEuler(new Vector3(0, p.DisplayHeading, 0)), p.DisplayRootPosition);
        var bones = new Dictionary<string, object>();
        for (int i = 0; i < _skeleton.GetBoneCount(); i++)
        {
            var world = _skeleton.GlobalTransform * _skeleton.GetBoneGlobalPose(i);
            var relative = body.AffineInverse() * world;
            bones[_skeleton.GetBoneName(i)] = new { world = Transform(world), body = Transform(relative) };
        }
        _posePhysics = Engine.GetPhysicsFrames();
        _pose = new { physicsFrame = _posePhysics, renderFrame = Engine.GetProcessFrames(), move = display.MoveId,
            phase = display.Phase.ToString(), frame = display.FrameInPhase, presentationTick = display.PresentationTick,
            hand = display.Hand.ToString(), root = Vec(p.DisplayRootPosition), heading = p.DisplayHeading,
            requestedState = p.CurrentAnimStateForHarness, animation = p.ActiveAnimNodeForHarness,
            clip = ResolvedClip(p), playbackTime = p.ContinuityPlaybackTime, recoveryBlend = RecoveryBlend(p),
            fadingFrom = p.ContinuityFadingFrom, fadingTime = p.ContinuityFadingTime,
            callback = p.ContinuityAnimationCallback,
            // Skeleton-space isolates authored hip travel from valid network
            // mesh smoothing; world/body positions remain the continuity oracle.
            hipInSkeleton = Vec(_skeleton.GetBoneGlobalPose(_skeleton.FindBone("mixamorig_Hips")).Origin),
            skeletonTransform = Transform(_skeleton.GlobalTransform), bones };
    }

    private void Sample()
    {
        if (_finished || _trace == null) return;
        var p = Subject();
        if (p == null) return;
        // Preserve the camera which drew this frame before the diagnostic view
        // setter queues a different transform for the NEXT rendered frame.
        // https://docs.godotengine.org/en/4.7/classes/class_camera3d.html
        Transform3D drawnCamera = _camera.GlobalTransform;
        Vector2 viewportSize = GetViewport().GetVisibleRect().Size;
        if (_skeleton == null)
        {
            _skeleton = FindSkeleton(p);
            // Snapshot at this signal: modifiers can restore their input before post-draw.
            // https://docs.godotengine.org/en/4.7/classes/class_skeleton3d.html#signals
            if (_skeleton != null) _skeleton.SkeletonUpdated += SnapshotPose;
        }
        if (_view != "gameplay")
        {
            var body = Basis.FromEuler(new Vector3(0, p.DisplayHeading, 0));
            var target = p.DisplayRootPosition + Vector3.Up * 1.4f;
            _camera.GlobalPosition = target + body * (_view == "side" ? Vector3.Right : Vector3.Forward) * 4f;
            _camera.LookAt(target);
        }
        var tree = p.GetNode<AnimationTree>("AnimationTree");
        string clip = ResolvedClip(p);
        int releases = _ball.SuccessfulShotReleasesForHarness;
        string file = "";
        if (_images)
        {
            file = $"frame-{_samples:D5}.png";
            using var image = GetViewport().GetTexture().GetImage();
            if (image == null || image.IsEmpty() || image.SavePng(Path.Combine(_output, file)) != Error.Ok) { Finish("PNG capture failed"); return; }
        }
        var view = _role is "client" or "observer" ? _ball.GetPresentationFrame() : null;
        _trace.WriteLine(JsonSerializer.Serialize(new { sample = _samples++, tick = _tick, file,
            physicsFrame = Engine.GetPhysicsFrames(), poseMatchesPhysics = _posePhysics == Engine.GetPhysicsFrames(), pose = _pose,
            camera = Transform(drawnCamera), cameraFov = _camera.Fov, cameraProjection = _camera.Projection.ToString(),
            cameraSize = _camera.Size, cameraNear = _camera.Near, cameraFar = _camera.Far,
            viewportSize = new[] { viewportSize.X, viewportSize.Y }, cameraProvenance = _images
                ? "observed before post-draw diagnostic camera update" : "headless camera state only; no rendered camera observation",
            role = _role, subjectPeer = _subjectPeer, subjectRole = _subjectRole,
            locallyPredicted = p.ContinuityIsLocalPlayer && !Multiplayer.IsServer(),
            requestedState = p.CurrentAnimStateForHarness, animation = p.ActiveAnimNodeForHarness, clip, playbackTime = p.ContinuityPlaybackTime,
            recoveryBlend = RecoveryBlend(p),
            fadingFrom = p.ContinuityFadingFrom, fadingTime = p.ContinuityFadingTime,
            move = p.DisplayMoveId(), phase = p.DisplayMove().phase.ToString(), frame = p.AnimationDisplaySnapshot.FrameInPhase,
            authoritativePhase = p.PhaseForHarness.ToString(), authoritativeFrame = p.FrameInPhaseForHarness,
            simulationPhase = p.PhaseForHarness.ToString(), simulationFrame = p.FrameInPhaseForHarness,
            simulationRole = Multiplayer.IsServer() ? "authority" : p.ContinuityIsLocalPlayer ? "owner-prediction" : "received-remote-state",
            hand = p.DisplayHandSide.ToString(), heading = p.DisplayHeading, root = Vec(p.DisplayRootPosition), fadeaway = p.DisplayFadeaway(),
            fixedHeadingTarget = _fixedHeadingTarget, headingFixture = _headingFixture,
            facingFactor = ShotFacing.Multiplier(p.DisplayHeading, p.DisplayRootPosition, _ball.RimCenter, _ball.FacingScatterK),
            initiationSpeed = p.ShotInitiationSpeed,
            ball = Vec(_ball.RenderedWorldPosition), liveBall = Vec(_ball.GlobalPosition), ballState = _ball.State.ToString(),
            shotAttachmentModel = Vec(_ball.State == BallState.Held ? _ball.GlobalPosition : _ball.LastReleasePositionForHarness),
            shotVisualOffset = Vec(_ball.RenderedWorldPosition - _ball.GlobalPosition),
            flightVelocity = Vec(_ball.FlightVelocityForHarness), arcGravity = _ball.Gravity,
            flightStep = new { advanced = _ball.FlightStepForHarness.Advanced,
                position = Vec(_ball.FlightStepForHarness.Position), velocity = Vec(_ball.FlightStepForHarness.Velocity) },
            ballCorrection = new { received = _ball.BallCorrectionForHarness.Received,
                before = Vec(_ball.BallCorrectionForHarness.Before), authoritative = Vec(_ball.BallCorrectionForHarness.Authoritative),
                velocity = Vec(_ball.BallCorrectionForHarness.Velocity), beforeState = _ball.BallCorrectionForHarness.BeforeState,
                state = _ball.BallCorrectionForHarness.State },
            shotGatherValid = p.SimulationShot?.Gather.Valid ?? false,
            holder = _ball.StateMachine.HolderPeerId, hasDribbled = _ball.HasDribbled,
            inputForward = Input.IsActionPressed("move_forward"),
            releaseCount = releases, releasePosition = Vec(_ball.LastReleasePositionForHarness),
            selectedPresentationTick = view?.Tick ?? -1, ballPresentationTick = _ball.RenderedPresentationTick,
            latestPresentationTick = _ball.LatestPresentationReceivedTick, presentationHolding = _ball.PresentationHolding,
            presentationResyncCount = _ball.PresentationResyncCount }));
    }

    private void Finish(string failure)
    {
        if (_finished) return;
        _finished = true;
        RenderingServer.FramePostDraw -= Sample;
        if (GodotObject.IsInstanceValid(_skeleton)) _skeleton.SkeletonUpdated -= SnapshotPose;
        foreach (string action in Actions) Input.ActionRelease(action);
        _trace?.Dispose();
        if (_trace != null) File.WriteAllText(Path.Combine(_output, "manifest.json"), JsonSerializer.Serialize(new {
            schema = "hooper-jumpshot-form/v1", result = failure == "" ? "captured" : "failed", failure, role = _role, variant = _variant,
            hand = _hand, commit = _commit, view = _view, samples = _samples, releases = _releases, events = _events,
            entry = _entry, entryOffset = _entryOffset, subjectPeer = _subjectPeer, subjectRole = _subjectRole,
            before440 = _before440, before440Applied = _before440Applied,
            resumeObserved = _resumeObserved, laterDribbleObserved = _laterDribbleObserved,
            headingFixture = _headingFixture, initialAngle = _headingAngle, headingEntry = _headingEntry, headingRate = _headingRate,
            sawProductionShotStartup = _sawShot, feintRequested = _feinted, sawAcceptedFeintRecovery = _sawFeintRecovery,
            paced = _paced, pacingMs = _paced ? _pacingMs : 0,
            observerPacingMs = _matrix && _paced && _role == "observer" ? 16 : 0,
            pacingMeaning = _paced ? $"{_pacingMs}ms producer wall-clock delay; fixed simulation steps; not real-time" : "unpaced",
            mutation = _legacyShot ? "legacy-normal-shot-three-clips" : "none", mutationApplied = _legacyApplied,
            fadeawaySubstitute = _fadeawaySubstitute, fadeawaySubstituteApplied = _fadeawaySubstituteApplied,
            fadeawayFixtureSha256 = _fadeawaySubstitute == "before429" ? Convert.ToHexString(SHA256.HashData(
                File.ReadAllBytes(ProjectSettings.GlobalizePath("res://tests/fixtures/fadeaway_before_429.res")))) : "",
            legacyFixtureSha256 = _legacyShot ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ProjectSettings.GlobalizePath("res://tests/fixtures/jumpshot_goalkeeper.res")))) : "",
            animationResourceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ProjectSettings.GlobalizePath("res://assets/locomotion.res")))),
            playerSceneSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ProjectSettings.GlobalizePath("res://scenes/Player.tscn")))),
            captureToolSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ProjectSettings.GlobalizePath("res://tests/integration/JumpshotFormCapture.cs")))),
            engine = Engine.GetVersionInfo()["string"].AsString(), images = _images,
            sampleTiming = _images ? "SkeletonUpdated modified pose / FramePostDraw image" : "HEADLESS diagnostic state only",
            verdict = "diagnostic only; no feel acceptance" }, new JsonSerializerOptions { WriteIndented = true }));
        GD.Print($"[jumpshot-capture] {(failure == "" ? "CAPTURED" : "FAIL " + failure)} samples={_samples}");
        GetTree().Quit(failure == "" ? 0 : 1);
    }
    private PlayerController Subject() => _subjectPeer > 0
        ? _players?.GetNodeOrNull<PlayerController>(_subjectPeer.ToString()) : null;

    private static string ResolvedClip(PlayerController p)
    {
        var machine = (AnimationNodeStateMachine)p.GetNode<AnimationTree>("AnimationTree").TreeRoot;
        string state = p.ActiveAnimNodeForHarness;
        if (!machine.HasNode(state)) return "<missing-state>";
        // Recovery has an explicit primary clip and a possession-dependent
        // neutral input; report the latter and its actual weight separately.
        if (state == "JumpshotRecovery" && machine.GetNode(state) is AnimationNodeBlendTree recovery
            && recovery.HasNode("Recovery") && recovery.GetNode("Recovery") is AnimationNodeAnimation primary)
            return primary.Animation.ToString();
        var leaves = new List<string>();
        void Collect(AnimationNode node)
        {
            if (node is AnimationNodeAnimation leaf) leaves.Add(leaf.Animation.ToString());
            // A state may wrap its sole animation in TimeSeek. Enumerate the
            // actual resource rather than guessing a clip from the state name.
            // https://docs.godotengine.org/en/4.7/classes/class_animationnodeblendtree.html#class-animationnodeblendtree-method-get-node-list
            else if (node is AnimationNodeBlendTree blend)
                foreach (StringName name in blend.GetNodeList()) Collect(blend.GetNode(name));
        }
        Collect(machine.GetNode(state));
        return leaves.Count == 1 ? leaves[0] : "<blend-space-or-multiple-leaves>";
    }

    private static object RecoveryBlend(PlayerController p)
    {
        if (p.ActiveAnimNodeForHarness != "JumpshotRecovery") return null;
        var tree = p.GetNode<AnimationTree>("AnimationTree");
        var machine = (AnimationNodeStateMachine)tree.TreeRoot;
        if (machine.GetNode("JumpshotRecovery") is not AnimationNodeBlendTree recovery || !recovery.HasNode("Neutral")) return null;
        var neutral = (AnimationNodeBlendSpace1D)recovery.GetNode("Neutral");
        double position = tree.Get("parameters/JumpshotRecovery/Neutral/blend_position").AsDouble();
        int index = (int)Math.Round(position);
        return new { neutralPosition = position, neutralClip = ((AnimationNodeAnimation)neutral.GetBlendPointNode(index)).Animation.ToString(),
            amount = tree.Get("parameters/JumpshotRecovery/ReturnBlend/blend_amount").AsDouble() };
    }

    // #440 uses an explicit fixture protocol so the historical #418/#428/#429
    // driver remains unchanged when the new entry selector is omitted.
    // Reliable fixture RPCs do not prove the UnreliableOrdered input stream has
    // drained; each possession reset also waits for its input-sequence fence.
    // https://docs.godotengine.org/en/4.7/tutorials/networking/high_level_multiplayer.html#remote-procedure-calls
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RegisterMatrix(string role)
    {
        if (!Multiplayer.IsServer()) return;
        int sender = Multiplayer.GetRemoteSenderId();
        if (sender <= 1) return;
        if (role == "owner" && _subjectRole == "owner") _subjectPeer = sender;
        else if (role is "observer" or "client") _observerPeer = sender;
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void BeginMatrix(int subject)
    {
        _subjectPeer = subject; _matrixStarted = true; _stageTick = _tick;
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestMatrixReset(int fence, int ticket)
    {
        if (Multiplayer.IsServer() && Multiplayer.GetRemoteSenderId() == _subjectPeer)
        { _resetFence = fence; _resetTicket = ticket; }
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void MatrixResetComplete(int ticket) { _resetCompleted = ticket; }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void MatrixDriverDone()
    {
        if (Multiplayer.IsServer() && Multiplayer.GetRemoteSenderId() == _subjectPeer && _completionTick < 0)
            _completionTick = _tick;
    }

    private void TickMatrix()
    {
        bool authority = Multiplayer.IsServer();
        bool driver = _role is "local" or "owner" || (_role == "server" && _subjectRole == "host");
        if (!authority && !_registered && Multiplayer.GetUniqueId() > 1 && _players.GetChildCount() > 0)
        { RpcId(1, nameof(RegisterMatrix), _role); _registered = true; }
        if (authority && !_matrixStarted)
        {
            bool peersReady = _role == "local" || (_observerPeer > 1 && _subjectPeer > 0);
            var subject = Subject();
            if (!peersReady || subject == null || _ball.StateMachine.HolderPeerId != _subjectPeer) return;
            // The second peer observes the shot; its body is not a defensive
            // stimulus. Keep it outside the subject's movement lane so the
            // later fresh-possession check measures legal dribbling rather
            // than contact against ClientSpawn's idle defender at z=3.
            // Authority's normal player snapshots replicate this fixture pose.
            // https://docs.godotengine.org/en/4.7/classes/class_node3d.html#class-node3d-property-global-position
            foreach (Node child in _players.GetChildren())
                if (child is PlayerController other && other != subject)
                {
                    other.GlobalPosition = new Vector3(5f, other.GlobalPosition.Y, 3f);
                    _events.Add(new { tick = _tick, kind = "fixture-observer-relocated",
                        peer = other.Name.ToString(), position = Vec(other.GlobalPosition) });
                }
            _matrixStarted = true; _stageTick = _tick;
            if (_role == "server") Rpc(nameof(BeginMatrix), _subjectPeer);
            _events.Add(new { tick = _tick, kind = "matrix-ready", subjectPeer = _subjectPeer, observerPeer = _observerPeer });
        }
        if (!_matrixStarted) return;
        // Clients may quit after receiving completion, despawning the subject.
        // Server teardown must not depend on that player node still existing.
        if (authority && _done && _completionTick >= 0 && _tick - _completionTick > 45)
        { Finish(!_sawShot ? "matrix did not observe shot" : ""); return; }
        var p = Subject();
        if (p == null) return;
        if (driver != p.ContinuityIsLocalPlayer && !authority)
        { Finish("matrix subject ownership mismatch"); return; }
        if (_before440 && !_before440Applied) ApplyBefore440(p);
        ObserveRelease(p);
        if (p.DisplayMoveId() == "jumpshot" && p.DisplayMove().phase == MovePhase.Startup)
        {
            if (!_sawShot) _events.Add(new { tick = _tick, physicsFrame = Engine.GetPhysicsFrames(),
                kind = "accepted-begin-observed", role = _role, phaseFrame = p.FrameInPhaseForHarness,
                heading = p.Heading, initiationSpeed = p.ShotInitiationSpeed,
                holder = _ball.StateMachine.HolderPeerId, ballState = _ball.State.ToString(), hasDribbled = _ball.HasDribbled });
            _sawShot = true;
        }
        if (p.DisplayMoveId() == "jumpshot" && p.DisplayMove().phase == MovePhase.Recovery && p.AnimationDisplaySnapshot.FrameInPhase >= 12 && _variant is "early" or "late")
            _sawFeintRecovery = true;
        if (authority && _resetFence >= 0 && p.CrossoverFixtureReceivedSequence >= _resetFence && p.CrossoverFixtureNeutralInput && p.PhaseForHarness == MovePhase.Inactive)
        {
            _ball.PostAwardForHarness(_subjectPeer);
            _resetCompleted = _resetTicket; _resetFence = -1;
            _events.Add(new { tick = _tick, physicsFrame = Engine.GetPhysicsFrames(), kind = "possession-reset",
                ticket = _resetTicket, sequence = p.CrossoverFixtureReceivedSequence,
                neutralInput = p.CrossoverFixtureNeutralInput, holder = _ball.StateMachine.HolderPeerId,
                ballState = _ball.State.ToString(), hasDribbled = _ball.HasDribbled });
            if (_role == "server") Rpc(nameof(MatrixResetComplete), _resetCompleted);
        }
        if (authority && _completionTick >= 0 && _tick - _completionTick > 30)
        {
            if (_ball.SuccessfulShotReleasesForHarness != (_variant is "early" or "late" ? 0 : 1))
            { Finish("matrix authoritative release count mismatch"); return; }
            if (!_done && _role == "server") Rpc(nameof(Completed));
            _done = true;
            // Let the reliable completion leave the server before closing ENet.
            if (_role != "server" || _tick - _completionTick > 45)
                Finish(!_sawShot ? "matrix did not observe shot" : "");
            return;
        }
        if (_done) { Finish(""); return; }
        if (!driver) return;
        if (_start < 0)
        {
            if (_ball.StateMachine.HolderPeerId != _subjectPeer || p.PhaseForHarness != MovePhase.Inactive) return;
            _start = _tick; _stageTick = _tick;
        }
        int elapsed = _tick - _stageTick;
        switch (_matrixStage)
        {
            case 0: // Establish a real live dribble and a stable initial heading.
                if (elapsed == 10) Input.ActionPress("move_forward");
                if (elapsed == 20) Input.ActionRelease("move_forward");
                if (elapsed < 45) return;
                _matrixStage = 1; _stageTick = _tick;
                break;
            case 1:
                if (_swapTick >= 0 && _tick == _swapTick + 8) Input.ActionRelease("aim_left");
                if (p.HandSide.ToString() != _hand)
                {
                    if (_swapTick < 0) { _swapTick = _tick; Input.ActionPress("aim_left"); }
                    return;
                }
                if (p.PhaseForHarness != MovePhase.Inactive || p.Velocity.Length() > .03f || (_swapTick >= 0 && _tick - _swapTick < 65)) return;
                if (_entry == "held")
                { SendMatrixReset(p, 1); _matrixStage = 2; }
                else { _matrixStage = 3; _stageTick = _tick; }
                break;
            case 2:
                if (_resetCompleted != 1 || _ball.State != BallState.Held || _ball.HasDribbled || _ball.StateMachine.HolderPeerId != _subjectPeer) return;
                _matrixStage = 3; _stageTick = _tick;
                break;
            case 3:
                if (elapsed < _entryOffset + 8) return;
                _matrixStage = _entry == "moving" ? 4 : 5; _stageTick = _tick;
                if (_entry == "moving") Input.ActionPress("move_forward");
                break;
            case 4:
                if (elapsed < 6) return;
                _matrixStage = 5;
                goto case 5;
            case 5:
                bool validEntry = _ball.StateMachine.HolderPeerId == _subjectPeer && p.HandSide.ToString() == _hand &&
                    (_entry == "held" ? _ball.State == BallState.Held && !_ball.HasDribbled && p.Velocity.Length() <= .03f :
                     _ball.State == BallState.Dribbling && (_entry == "moving" ? p.Velocity.Length() > .1f : p.Velocity.Length() <= .03f));
                if (!validEntry) { Finish("matrix entry premise failed"); return; }
                _events.Add(new { tick = _tick, physicsFrame = Engine.GetPhysicsFrames(), kind = "shoot-input",
                    entry = _entry, entryOffset = _entryOffset, hand = p.HandSide.ToString(), speed = p.Velocity.Length(),
                    ballState = _ball.State.ToString(), holder = _ball.StateMachine.HolderPeerId, hasDribbled = _ball.HasDribbled,
                    heading = p.Heading, animation = p.ActiveAnimNodeForHarness, playbackTime = p.ContinuityPlaybackTime });
                Input.ActionRelease("move_forward"); Input.ActionPress("ball_shoot");
                _shotTick = _tick; _matrixStage = 6; _stageTick = _tick;
                break;
            case 6:
                if (_tick == _shotTick + 1) Input.ActionRelease("ball_shoot");
                bool networkEarly = _role == "owner" && _variant == "early";
                bool feintFrame = networkEarly
                    ? p.FrameInPhaseForHarness >= 3 && p.FrameInPhaseForHarness < 12 &&
                      p.ContinuityReceivedPhase == MovePhase.Startup && p.ContinuityReceivedFrame >= 3
                    : p.FrameInPhaseForHarness == (_variant == "early" ? 3 : 11);
                if (!_feinted && _variant is "early" or "late" && p.PhaseForHarness == MovePhase.Startup && feintFrame)
                {
                    Input.ActionPress("move_feint"); _feinted = true;
                    _events.Add(new { tick = _tick, physicsFrame = Engine.GetPhysicsFrames(), kind = "feint-input",
                        phaseFrameBeforeInput = p.FrameInPhaseForHarness,
                        receivedAuthorityPhase = p.ContinuityReceivedPhase.ToString(), receivedAuthorityFrame = p.ContinuityReceivedFrame,
                        scheduling = networkEarly ? "first predicted-legal frame after observing authoritative Startup >=3; independent clocks" : "exact local/predicted frame" });
                }
                else if (_feinted) Input.ActionRelease("move_feint");
                if (_tick - _shotTick < 50 || p.PhaseForHarness != MovePhase.Inactive) return;
                Input.ActionPress("move_forward"); _matrixStage = 7; _stageTick = _tick;
                break;
            case 7:
                if (elapsed < 6) return;
                _resumeObserved = true;
                _events.Add(new { tick = _tick, physicsFrame = Engine.GetPhysicsFrames(), kind = "resumed-movement-observed",
                    speed = p.Velocity.Length(), ballState = _ball.State.ToString(), holder = _ball.StateMachine.HolderPeerId,
                    hasDribbled = _ball.HasDribbled, animation = p.ActiveAnimNodeForHarness });
                Input.ActionRelease("move_forward"); _matrixStage = 8; _stageTick = _tick;
                break;
            case 8:
                // Send the fence AFTER a following input sample consumed release.
                if (elapsed < 2) return;
                SendMatrixReset(p, 2); _matrixStage = 9;
                break;
            case 9:
                if (_resetCompleted != 2 || _ball.State != BallState.Held || _ball.HasDribbled || _ball.StateMachine.HolderPeerId != _subjectPeer) return;
                _events.Add(new { tick = _tick, kind = "fresh-held-observed", ticket = 2, hand = p.HandSide.ToString() });
                Input.ActionPress("move_forward"); _matrixStage = 10; _stageTick = _tick;
                break;
            case 10:
                if (elapsed < 8) return;
                _laterDribbleObserved = _ball.State == BallState.Dribbling && p.Velocity.Length() > .1f;
                // An owning client must receive the award and then send fresh
                // movement before authority's post-award freshness gate opens.
                // Wait for that state evidence, rather than assuming eight
                // client ticks imply eight consumed server input ticks.
                if (!_laterDribbleObserved && elapsed < 60) return;
                _events.Add(new { tick = _tick, physicsFrame = Engine.GetPhysicsFrames(), kind = "later-legal-dribble-observed",
                    speed = p.Velocity.Length(), ballState = _ball.State.ToString(), holder = _ball.StateMachine.HolderPeerId,
                    hasDribbled = _ball.HasDribbled, animation = p.ActiveAnimNodeForHarness });
                if (!_laterDribbleObserved) { Finish("later legal dribble did not begin"); return; }
                Input.ActionRelease("move_forward");
                if (_variant is "early" or "late" && (!_feinted || !_sawFeintRecovery))
                { Finish("matrix did not observe legal feint recovery"); return; }
                if (authority) _completionTick = _tick;
                else RpcId(1, nameof(MatrixDriverDone));
                _matrixStage = 11;
                break;
        }
    }

    private void SendMatrixReset(PlayerController p, int ticket)
    {
        foreach (string action in Actions) Input.ActionRelease(action);
        int fence = p.CrossoverFixtureSentSequence;
        _events.Add(new { tick = _tick, kind = "reset-request", ticket, fence });
        if (Multiplayer.IsServer()) { _resetFence = fence; _resetTicket = ticket; }
        else RpcId(1, nameof(RequestMatrixReset), fence, ticket);
    }

    private void ApplyBefore440(PlayerController subject)
    {
        var player = subject.GetNode<AnimationPlayer>("AnimationPlayer");
        var library = (AnimationLibrary)player.GetAnimationLibrary("locomotion").Duplicate(true);
        var baseline = GD.Load<AnimationLibrary>("res://tests/fixtures/jumpshot_before_440.res");
        foreach (string clip in new[] { "jumpshotstartup", "jumpshotactive", "jumpshotrecovery" })
        { library.RemoveAnimation(clip); library.AddAnimation(clip, baseline.GetAnimation(clip)); }
        player.RemoveAnimationLibrary("locomotion"); player.AddAnimationLibrary("locomotion", library);
        _before440Applied = true;
        _events.Add(new { tick = _tick, kind = "fixture-mutation", detail = "pre-440 normal clips only; current playback and scene transitions retained" });
    }

    private void ApplyLegacyShot(PlayerController subject)
    {
        var player = subject.GetNode<AnimationPlayer>("AnimationPlayer");
        // Duplicate the library before replacing three clips. Every unrelated
        // animation and the production on-disk resource stay intact.
        // https://docs.godotengine.org/en/4.7/classes/class_resource.html#class-resource-method-duplicate
        var library = (AnimationLibrary)player.GetAnimationLibrary("locomotion").Duplicate(true);
        var legacy = GD.Load<AnimationLibrary>("res://tests/fixtures/jumpshot_goalkeeper.res");
        foreach (string clip in new[] { "jumpshotstartup", "jumpshotactive", "jumpshotrecovery" })
        { library.RemoveAnimation(clip); library.AddAnimation(clip, legacy.GetAnimation(clip)); }
        player.RemoveAnimationLibrary("locomotion"); player.AddAnimationLibrary("locomotion", library);
        _legacyApplied = true;
        _events.Add(new { tick = _tick, kind = "fixture-mutation", detail = "legacy normal shot clips in duplicated subject AnimationLibrary" });
    }
    private void ApplyFadeawaySubstitute(PlayerController subject)
    {
        // (#429) Replace only the actual selection asset. Classification and
        // the production FadeawayActive state remain live in this control.
        // https://docs.godotengine.org/en/4.7/classes/class_resource.html#class-resource-method-duplicate
        var player = subject.GetNode<AnimationPlayer>("AnimationPlayer");
        var library = (AnimationLibrary)player.GetAnimationLibrary("locomotion").Duplicate(true);
        var replacement = _fadeawaySubstitute == "before429"
            ? GD.Load<AnimationLibrary>("res://tests/fixtures/fadeaway_before_429.res").GetAnimation("fadeawayactive")
            : library.GetAnimation("jumpshotactive");
        library.RemoveAnimation("fadeawayactive");
        library.AddAnimation("fadeawayactive", replacement);
        player.RemoveAnimationLibrary("locomotion"); player.AddAnimationLibrary("locomotion", library);
        _fadeawaySubstituteApplied = true;
        _events.Add(new { tick = _tick, kind = "fadeaway-substitute", detail = _fadeawaySubstitute });
    }
    private void ObserveRelease(PlayerController p)
    {
        int count = _ball.SuccessfulShotReleasesForHarness;
        if (count == _releases) return;
        _events.Add(new { tick = _tick, physicsFrame = Engine.GetPhysicsFrames(), kind = "release", count,
            phase = p.PhaseForHarness.ToString(), frame = p.FrameInPhaseForHarness,
            heading = p.Heading, residualAngle = ShotFacing.AngleFromTarget(p.Heading, p.GlobalPosition, _ball.RimCenter),
            facingFactor = ShotFacing.Multiplier(p.Heading, p.GlobalPosition, _ball.RimCenter, _ball.FacingScatterK),
            fadeaway = p.DisplayFadeaway(), initiationSpeed = p.ShotInitiationSpeed,
            position = Vec(_ball.LastReleasePositionForHarness), liveBall = Vec(_ball.GlobalPosition) });
        _releases = count;
    }
    private static Skeleton3D FindSkeleton(Node n)
    {
        if (n is Skeleton3D s) return s;
        foreach (Node child in n.GetChildren()) { var found = FindSkeleton(child); if (found != null) return found; }
        return null;
    }
    private static float[] Vec(Vector3 v) => new[] { v.X, v.Y, v.Z };
    private static float[] Transform(Transform3D t) => Vec(t.Origin).Concat(Vec(t.Basis.X)).Concat(Vec(t.Basis.Y)).Concat(Vec(t.Basis.Z)).ToArray();
}
