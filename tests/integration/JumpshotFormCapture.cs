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
        _legacyShot = Read("--capture-legacy-shot", "false") == "true";
        _fadeawaySubstitute = Read("--capture-fadeaway-substitute", "");
        if (_fadeawaySubstitute is not ("" or "before429" or "normal"))
            throw new ArgumentException("unknown fadeaway substitute");
        _headingFixture = Read("--capture-heading-angle", "") != "";
        _headingAngle = float.Parse(Read("--capture-heading-angle", "0"), System.Globalization.CultureInfo.InvariantCulture);
        _headingRate = float.Parse(Read("--capture-heading-rate", "900"), System.Globalization.CultureInfo.InvariantCulture);
        _headingEntry = Read("--capture-heading-entry", "stationary");
        _output = ProjectSettings.GlobalizePath(Read("--capture-output", ""));
        if (!new[] { "normal", "early", "late", "fadeaway" }.Contains(_variant) || !new[] { "Right", "Left" }.Contains(_hand) ||
            !new[] { "local", "server", "client" }.Contains(_role) || !new[] { "gameplay", "front", "side" }.Contains(_view) || Directory.Exists(_output))
        { Finish("invalid arguments or nonfresh output"); return; }
        Directory.CreateDirectory(_output);
        _trace = new StreamWriter(Path.Combine(_output, "trace.jsonl"));
        var main = GetNode<Node3D>("Main");
        _network = main.GetNode<NetworkManager>("NetworkManager");
        _players = main.GetNode("Players");
        _ball = main.GetNode<BallController>("Ball");
        _camera = main.GetNode<Camera3D>("Camera3D");
        int port = int.Parse(Read("--capture-port", "23468"));
        if (_role == "client")
        {
            Multiplayer.ConnectionFailed += () => Finish("connection failed");
            Multiplayer.ServerDisconnected += () => Finish(_done ? "" : "unexpected disconnect");
            _network.JoinGame("127.0.0.1", port);
        }
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
        var p = _players?.GetNodeOrNull<PlayerController>("1");
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
        if (!_finished && _paced && _role == "server") OS.DelayMsec(80);
        if (!_images && DisplayServer.GetName() == "headless") Sample();
    }

    private void SnapshotPose()
    {
        var p = _players.GetNodeOrNull<PlayerController>("1");
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
            hand = display.Hand.ToString(), bones };
    }

    private void Sample()
    {
        if (_finished || _trace == null) return;
        var p = _players.GetNodeOrNull<PlayerController>("1");
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
        var machine = (AnimationNodeStateMachine)tree.TreeRoot;
        string clip = machine.GetNode(p.ActiveAnimNodeForHarness) is AnimationNodeAnimation node ? node.Animation.ToString() : "<blend-space>";
        int releases = _ball.SuccessfulShotReleasesForHarness;
        string file = "";
        if (_images)
        {
            file = $"frame-{_samples:D5}.png";
            using var image = GetViewport().GetTexture().GetImage();
            if (image == null || image.IsEmpty() || image.SavePng(Path.Combine(_output, file)) != Error.Ok) { Finish("PNG capture failed"); return; }
        }
        var view = _role == "client" ? _ball.GetPresentationFrame() : null;
        _trace.WriteLine(JsonSerializer.Serialize(new { sample = _samples++, tick = _tick, file,
            physicsFrame = Engine.GetPhysicsFrames(), poseMatchesPhysics = _posePhysics == Engine.GetPhysicsFrames(), pose = _pose,
            camera = Transform(drawnCamera), cameraFov = _camera.Fov, cameraProjection = _camera.Projection.ToString(),
            cameraSize = _camera.Size, cameraNear = _camera.Near, cameraFar = _camera.Far,
            viewportSize = new[] { viewportSize.X, viewportSize.Y }, cameraProvenance = _images
                ? "observed before post-draw diagnostic camera update" : "headless camera state only; no rendered camera observation",
            role = _role, animation = p.ActiveAnimNodeForHarness, clip, playbackTime = p.ContinuityPlaybackTime,
            fadingFrom = p.ContinuityFadingFrom, fadingTime = p.ContinuityFadingTime,
            move = p.DisplayMoveId(), phase = p.DisplayMove().phase.ToString(), frame = p.AnimationDisplaySnapshot.FrameInPhase,
            authoritativePhase = p.PhaseForHarness.ToString(), authoritativeFrame = p.FrameInPhaseForHarness,
            hand = p.DisplayHandSide.ToString(), heading = p.DisplayHeading, root = Vec(p.DisplayRootPosition), fadeaway = p.DisplayFadeaway(),
            fixedHeadingTarget = _fixedHeadingTarget, headingFixture = _headingFixture,
            facingFactor = ShotFacing.Multiplier(p.DisplayHeading, p.DisplayRootPosition, _ball.RimCenter, _ball.FacingScatterK),
            initiationSpeed = p.ShotInitiationSpeed,
            ball = Vec(_ball.RenderedWorldPosition), liveBall = Vec(_ball.GlobalPosition), ballState = _ball.State.ToString(),
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
            headingFixture = _headingFixture, initialAngle = _headingAngle, headingEntry = _headingEntry, headingRate = _headingRate,
            sawProductionShotStartup = _sawShot, feintRequested = _feinted, sawAcceptedFeintRecovery = _sawFeintRecovery,
            paced = _paced, pacingMeaning = _paced ? "80ms producer wall-clock delay; fixed simulation steps; not real-time" : "unpaced",
            mutation = _legacyShot ? "legacy-normal-shot-three-clips" : "none", mutationApplied = _legacyApplied,
            fadeawaySubstitute = _fadeawaySubstitute, fadeawaySubstituteApplied = _fadeawaySubstituteApplied,
            fadeawayFixtureSha256 = _fadeawaySubstitute == "before429" ? Convert.ToHexString(SHA256.HashData(
                File.ReadAllBytes(ProjectSettings.GlobalizePath("res://tests/fixtures/fadeaway_before_429.res")))) : "",
            legacyFixtureSha256 = _legacyShot ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ProjectSettings.GlobalizePath("res://tests/fixtures/jumpshot_goalkeeper.res")))) : "",
            animationResourceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ProjectSettings.GlobalizePath("res://assets/locomotion.res")))),
            engine = Engine.GetVersionInfo()["string"].AsString(), images = _images,
            sampleTiming = _images ? "SkeletonUpdated modified pose / FramePostDraw image" : "HEADLESS diagnostic state only",
            verdict = "diagnostic only; no feel acceptance" }, new JsonSerializerOptions { WriteIndented = true }));
        GD.Print($"[jumpshot-capture] {(failure == "" ? "CAPTURED" : "FAIL " + failure)} samples={_samples}");
        GetTree().Quit(failure == "" ? 0 : 1);
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
