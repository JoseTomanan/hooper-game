using System;
using System.Collections.Generic;
using System.Globalization;
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

/// <summary>Issue #427 production-scene diagnostic. Capture completion does not accept presentation or feel.</summary>
public partial class ReboundPresentationCapture : Node
{
    private string _role, _actor, _output, _hand, _commit, _view, _action;
    private bool _images, _moving, _ready, _sentReady, _finished, _done, _paced, _contested, _legacyPose, _longLoose;
    private float _height;
    private int _seedDelay;
    private int _tick, _start = -1, _subjectId = 1, _clientPeer, _recoveries, _serverRecoveries;
    private int _previousHolder = -1, _doneTick = -1, _lastSeedTick = -1000;
    private BallState _previousState;
    private NetworkManager _network;
    private Node _players;
    private BallController _ball;
    private Skeleton3D _skeleton;
    private Camera3D _camera;
    private StreamWriter _trace;
    private int _samples;
    private int _lastConfirmedReceipt;
    private bool _resetNeutral, _resetPerformed, _actionAccepted, _feintRequested, _feintAccepted, _suppressGrab, _actionSawActive;
    private int _seedOrdinal, _actionSeedOrdinal, _actionRequests, _acceptedActions, _feintRequests, _acceptedFeints;
    private int _actionTick = -1, _shootReleaseTick = -1, _feintReleaseTick = -1, _actionReceipt;
    private string _controlFailure = "";
    private ulong _posePhysics;
    private long _posePresentation = -1;
    private object _pose = new { };
    private readonly Dictionary<string, Vector3> _boneWorld = new();
    private readonly List<object> _events = new();
    private readonly System.Diagnostics.Stopwatch _wall = System.Diagnostics.Stopwatch.StartNew();
    private static readonly string[] Bones = { "Head", "LeftHand", "RightHand", "LeftHandMiddle1", "RightHandMiddle1", "LeftHandIndex1", "RightHandIndex1", "LeftHandPinky1", "RightHandPinky1", "LeftFoot", "RightFoot", "LeftToeBase", "RightToeBase" };
    private bool IsClient => _role is "client" or "owner";
    private PlayerController Subject => _players?.GetNodeOrNull<PlayerController>(_subjectId.ToString(CultureInfo.InvariantCulture));

    public override void _Ready()
    {
        // Keep production animation callbacks intact; run the observer after gameplay physics.
        // https://docs.godotengine.org/en/4.7/classes/class_node.html#class-node-property-process-physics-priority
        ProcessPhysicsPriority = 100;
        var args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        string Read(string name, string fallback) => HarnessArgs.ReadArg(args, name, fallback);
        _role = Read("--capture-role", "local");
        _actor = Read("--capture-actor", _role == "owner" ? "client" : "host");
        _hand = Read("--capture-hand", "Right");
        _commit = Read("--capture-commit", "unknown");
        _images = Read("--capture-images", "false") == "true";
        _moving = Read("--capture-moving", "false") == "true";
        _legacyPose = Read("--capture-legacy-pose", "false") == "true";
        _longLoose = Read("--capture-long-loose", "false") == "true";
        _paced = Read("--capture-paced", "false") == "true";
        _contested = Read("--capture-contested", "false") == "true";
        _view = Read("--capture-view", "gameplay");
        _action = Read("--capture-action", "none");
        _resetNeutral = Read("--capture-reset-neutral", "false") == "true";
        _seedDelay = int.TryParse(Read("--capture-seed-delay", "0"), out int seedDelay) ? seedDelay : -1;
        string height = Read("--capture-height", "floor");
        _height = height == "floor" ? .12f : height == "falling" ? 2f :
            float.TryParse(height, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) ? parsed : float.NaN;
        _output = ProjectSettings.GlobalizePath(Read("--capture-output", ""));
        if (!new[] { "local", "server", "client", "owner" }.Contains(_role) ||
            !new[] { "host", "client" }.Contains(_actor) || !new[] { "Right", "Left" }.Contains(_hand) ||
            (_role == "owner" && _actor != "client") || (_role == "local" && _actor != "host") ||
            !new[] { "gameplay", "side" }.Contains(_view) ||
            !new[] { "none", "immediate", "feint" }.Contains(_action) ||
            _seedDelay < 0 || _seedDelay > 12 ||
            !float.IsFinite(_height) || _height < .12f || string.IsNullOrWhiteSpace(_output) || Directory.Exists(_output))
        { Finish("invalid arguments or nonfresh output"); return; }
        Directory.CreateDirectory(_output);
        _trace = new StreamWriter(Path.Combine(_output, "trace.jsonl"));
        var main = GetNode<Node3D>("Main");
        _network = main.GetNode<NetworkManager>("NetworkManager");
        _players = main.GetNode("Players");
        _ball = main.GetNode<BallController>("Ball");
        _camera = main.GetNode<Camera3D>("Camera3D");
        int port = int.Parse(Read("--capture-port", "23478"), CultureInfo.InvariantCulture);
        if (IsClient)
        {
            Multiplayer.ConnectionFailed += () => Finish("connection failed");
            Multiplayer.ServerDisconnected += () => Finish(_done ? "" : "unexpected disconnect");
            _network.JoinGame("127.0.0.1", port);
        }
        else
        {
            _network.HostGame(port);
            if (_role == "server") GD.Print("[rebound-capture] READY");
        }
        // The viewport is complete at this signal; headless sampling is explicitly diagnostic state.
        // https://docs.godotengine.org/en/4.7/classes/class_renderingserver.html#class-renderingserver-signal-frame-post-draw
        RenderingServer.FramePostDraw += Sample;
    }

    // Reliable RPCs synchronize fixture operation, never substitute for production ball/player replication.
    // https://docs.godotengine.org/en/4.7/tutorials/networking/high_level_multiplayer.html#remote-procedure-calls
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ObserverReady()
    {
        if (!Multiplayer.IsServer() || Multiplayer.GetRemoteSenderId() <= 1) return;
        _clientPeer = Multiplayer.GetRemoteSenderId();
        _ready = true;
    }
    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void CaptureStarted(int subject)
    {
        _subjectId = subject;
        _start = _tick;
        Event("fixture-start", new { subject });
    }
    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void CaptureSeeded(int ordinal, long sourceTick)
    {
        _seedOrdinal = ordinal;
        _lastSeedTick = _tick;
        Event("seed-received", new { ordinal, sourceTick });
        if (_moving && Subject?.ContinuityIsLocalPlayer == true) Input.ActionPress("move_forward");
    }
    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RecoveryObserved(int count, long sourceTick)
    {
        _serverRecoveries = count;
        Event("authoritative-recovery-received", new { count, sourceTick });
    }
    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void FixtureMovementStopped() => Input.ActionRelease("move_forward");
    [Rpc(MultiplayerApi.RpcMode.Authority, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Completed(int recoveries)
    {
        _serverRecoveries = recoveries;
        _done = true;
        _doneTick = _tick;
        Input.ActionRelease("move_forward");
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _tick++;
        if (_wall.Elapsed.TotalSeconds > 75 || _tick > 6000) { Finish("timeout"); return; }
        if (IsClient && !_sentReady && Multiplayer.GetUniqueId() > 1 && _players.GetChildCount() >= 2)
        { RpcId(1, nameof(ObserverReady)); _sentReady = true; }
        if (!IsClient && _start < 0)
        {
            if (Subject == null || (_role == "server" && (!_ready || _players.GetChildCount() < 2))) return;
            _subjectId = _actor == "client" ? _clientPeer : 1;
            if (Subject == null) return;
            _start = _tick;
            if (_role == "server") Rpc(nameof(CaptureStarted), _subjectId);
            Event("fixture-start", new { subject = _subjectId });
        }
        // The owner can finish draining and disconnect before the server's tail
        // deadline. Its production player node is then removed; completion must
        // depend on the latched recovery evidence, not that node still existing.
        if (_role == "server" && _done && _tick - _start >= 270)
        { Finish(_recoveries == 2 ? "" : "did not observe two live recoveries"); return; }
        if (IsClient && _done && _tick - _doneTick >= 30)
        { Finish(_serverRecoveries == 2 ? "" : "authority did not observe two recoveries"); return; }
        var subject = Subject;
        if (subject == null || _start < 0) return;
        AttachSkeleton(subject);
        ObserveRecovery(subject);
        var confirmed = _ball.ConfirmedRecovery;
        if (confirmed.Receipt != 0 && confirmed.Receipt != _lastConfirmedReceipt)
        {
            _lastConfirmedReceipt = confirmed.Receipt;
            Event("confirmed-recovery-received", new { receipt = confirmed.Receipt, recipient = confirmed.Recipient,
                age = confirmed.Age, inputSequence = confirmed.InputSequence });
        }
        if (IsClient && _seedOrdinal > 0 && subject.ContinuityIsLocalPlayer && confirmed.Receipt != 0 && confirmed.Receipt != _actionReceipt &&
            confirmed.Recipient == _subjectId && _ball.StateMachine.HolderPeerId == _subjectId)
        {
            _actionReceipt = confirmed.Receipt;
            _suppressGrab = false;
            TriggerAction(subject, "confirmed-recovery-receipt");
        }
        TickActionControl(subject);
        if (IsClient && _resetNeutral && !_resetPerformed && _tick - _start >= 35)
        {
            int previousEpoch = _ball.PresentationEpoch;
            _ball.ResetReboundPresentationForHarness();
            _resetPerformed = true;
            Event("neutral-presentation-reset", new { previousEpoch, epoch = _ball.PresentationEpoch,
                holder = _ball.StateMachine.HolderPeerId, animation = subject.ActiveAnimNodeForHarness });
        }
        if (IsClient)
        {
            // Give the reliable visual history and the local final skeleton additional drain frames.
            return;
        }
        int t = _tick - _start;
        if (t is 25 or 130) Prepare(subject);
        if (_longLoose && t is 40 or 140)
            _ball.SeedLooseBallForHarness(new Vector3(3, _height, 4.7f));
        if (t == 60 + _seedDelay || t == 150 + _seedDelay) Seed(subject, t == 60 + _seedDelay ? 1 : 2);
        if (t == 105 + _seedDelay || t == 195 + _seedDelay)
        {
            Input.ActionRelease("move_forward");
            if (_role == "server") Rpc(nameof(FixtureMovementStopped));
        }
        if (t == 220)
        {
            Input.ActionRelease("move_forward");
            if (_role == "server") Rpc(nameof(Completed), _recoveries);
            _done = true;
        }
        // Keep the server alive until the observer has had time to consume the tail.
        if (t >= (_role == "server" ? 270 : 220)) Finish(_recoveries == 2 ? "" : "did not observe two live recoveries");
    }

    private void Prepare(PlayerController subject)
    {
        Input.ActionRelease("move_forward");
        subject.GlobalPosition = new Vector3(0f, 0f, 5f);
        subject.Velocity = Vector3.Zero;
        subject.SetHandSideForHarness(_hand == "Right" ? HandSide.Right : HandSide.Left);
        foreach (Node child in _players.GetChildren())
            if (child is PlayerController other && other != subject)
            { other.GlobalPosition = new Vector3(_contested ? .8f : 4f, 0f, 5f); other.Velocity = Vector3.Zero; }
        Event("fixture-positioned", new { position = Vec(subject.GlobalPosition), requestedPreRecoveryHand = _hand,
            meaning = "setup only; production possession contract may reset the hand on recovery" });
    }
    private void Seed(PlayerController subject, int ordinal)
    {
        _seedOrdinal = ordinal;
        _ball.SeedLooseBallForHarness(new Vector3(.3f, _height, 4.7f));
        _lastSeedTick = _tick;
        // Record the actual Loose state before next tick's genuine production contest.
        _previousState = _ball.State;
        _previousHolder = _ball.StateMachine.HolderPeerId;
        Event("seed", new { ordinal, position = Vec(_ball.GlobalPosition), state = _ball.State.ToString(),
            hand = subject.HandSide.ToString(), awardCount = _ball.SuccessfulPossessionAwardCountForHarness });
        if (_role == "server") Rpc(nameof(CaptureSeeded), ordinal, unchecked((long)Engine.GetPhysicsFrames()));
        if (_moving && subject.ContinuityIsLocalPlayer) Input.ActionPress("move_forward");
    }
    private void ObserveRecovery(PlayerController subject)
    {
        int holder = _ball.StateMachine.HolderPeerId;
        if (holder == _subjectId && _previousHolder != _subjectId && _previousState == BallState.Loose)
        {
            _suppressGrab = false;
            _recoveries++;
            Event("live-holder-edge", new { count = _recoveries, holder, state = _ball.State.ToString(),
                position = Vec(_ball.GlobalPosition), hand = subject.HandSide.ToString(), animation = subject.ActiveAnimNodeForHarness,
                playbackTime = subject.ContinuityPlaybackTime, awardCount = _ball.SuccessfulPossessionAwardCountForHarness });
            if (_role == "server") Rpc(nameof(RecoveryObserved), _recoveries, unchecked((long)Engine.GetPhysicsFrames()));
            TriggerAction(subject, "live-holder-edge");
        }
        _previousHolder = holder;
        _previousState = _ball.State;
    }

    private void TriggerAction(PlayerController subject, string trigger)
    {
        if (_action == "none" || !subject.ContinuityIsLocalPlayer || _seedOrdinal == 0 || _actionSeedOrdinal == _seedOrdinal) return;
        _actionSeedOrdinal = _seedOrdinal;
        _actionRequests++;
        _actionTick = _tick;
        _shootReleaseTick = _tick + 1;
        _actionAccepted = _feintRequested = _feintAccepted = _actionSawActive = false;
        _suppressGrab = true;
        // Production input is consumed on the next owning Player tick, before its catch latch.
        // https://docs.godotengine.org/en/4.7/classes/class_input.html#class-input-method-action-press
        Input.ActionPress("ball_shoot");
        Event("action-input", new { action = _action, trigger, ordinal = _seedOrdinal, request = _actionRequests,
            phase = subject.PhaseForHarness.ToString(), frame = subject.FrameInPhaseForHarness,
            confirmedReceipt = _ball.ConfirmedRecovery.Receipt, holder = _ball.StateMachine.HolderPeerId });
    }

    private void TickActionControl(PlayerController subject)
    {
        if (_action == "none" || !subject.ContinuityIsLocalPlayer) return;
        if (_tick == _shootReleaseTick) Input.ActionRelease("ball_shoot");
        if (_tick == _feintReleaseTick) Input.ActionRelease("move_feint");
        if (_actionTick < 0) return;
        bool jumpShot = subject.CurrentMoveIdForHarness == "jumpshot";
        if (jumpShot && subject.PhaseForHarness == MovePhase.Active) _actionSawActive = true;
        if (!_actionAccepted && jumpShot && subject.PhaseForHarness == MovePhase.Startup)
        {
            _actionAccepted = true;
            _acceptedActions++;
            Event("accepted-action-startup", new { ordinal = _actionSeedOrdinal, count = _acceptedActions,
                frame = subject.FrameInPhaseForHarness, actionInputTick = _actionTick });
        }
        if (_action == "feint" && _actionAccepted && !_feintRequested && jumpShot &&
            subject.PhaseForHarness == MovePhase.Startup && subject.FrameInPhaseForHarness >= 3)
        {
            Input.ActionPress("move_feint");
            _feintReleaseTick = _tick + 1;
            _feintRequested = true;
            _feintRequests++;
            Event("feint-input", new { ordinal = _actionSeedOrdinal, count = _feintRequests, phase = "Startup",
                frame = subject.FrameInPhaseForHarness });
        }
        if (_feintRequested && !_feintAccepted && !_actionSawActive && jumpShot && subject.PhaseForHarness == MovePhase.Recovery &&
            subject.FrameInPhaseForHarness >= Hooper.Moves.JumpShot.DefaultFrameData.RecoveryFrames
                - Hooper.Moves.JumpShot.DefaultFrameData.FeintRecoveryFrames)
        {
            _feintAccepted = true;
            _acceptedFeints++;
            Event("accepted-feint-recovery", new { ordinal = _actionSeedOrdinal, count = _acceptedFeints,
                frame = subject.FrameInPhaseForHarness });
        }
        if (_suppressGrab && _tick >= _actionTick + 2 &&
            (subject.IsPlayingReboundGrab || subject.ActiveAnimNodeForHarness == "ReboundGrab") && _controlFailure.Length == 0)
        {
            _controlFailure = "rebound grab replayed after the owning action interrupted it";
            Event("control-failure", new { failure = _controlFailure, ordinal = _actionSeedOrdinal,
                phase = subject.PhaseForHarness.ToString(), animation = subject.ActiveAnimNodeForHarness,
                latch = subject.IsPlayingReboundGrab });
        }
    }
    public override void _Process(double delta)
    {
        // Fixed-step producer pacing is diagnostic coverage, never real-time performance proof.
        // https://docs.godotengine.org/en/4.7/classes/class_os.html#class-os-method-delay-msec
        if (!_finished && _paced && _role == "server") OS.DelayMsec(80);
        if (!_images && DisplayServer.GetName() == "headless") Sample();
    }
    private void AttachSkeleton(PlayerController subject)
    {
        if (_skeleton != null) return;
        if (AnimationHarnessResources.TryFindSkeleton(subject, out _skeleton, out _)) _skeleton.SkeletonUpdated += SnapshotPose;
        if (_legacyPose) _skeleton?.GetNodeOrNull<ReboundPose>("ReboundPose")?.Set("active", false);
    }
    private void SnapshotPose()
    {
        var subject = Subject;
        if (_finished || subject == null) return;
        var bones = new Dictionary<string, float[]>();
        _boneWorld.Clear();
        foreach (string name in Bones)
        {
            int bone = _skeleton.FindBone("mixamorig_" + name);
            if (bone < 0) continue;
            // get_bone_global_pose is skeleton-relative; skeleton_updated includes final modifiers.
            // https://docs.godotengine.org/en/4.7/classes/class_skeleton3d.html#class-skeleton3d-method-get-bone-global-pose
            // https://docs.godotengine.org/en/4.7/classes/class_skeleton3d.html#class-skeleton3d-signal-skeleton-updated
            Vector3 world = (_skeleton.GlobalTransform * _skeleton.GetBoneGlobalPose(bone)).Origin;
            _boneWorld[name] = world;
            bones[name] = Vec(world);
        }
        _posePhysics = Engine.GetPhysicsFrames();
        _posePresentation = subject.AnimationDisplaySnapshot.PresentationTick;
        _pose = new { physicsFrame = _posePhysics, renderFrame = Engine.GetProcessFrames(), presentationTick = _posePresentation,
            animation = subject.ActiveAnimNodeForHarness, playbackTime = subject.ContinuityPlaybackTime,
            hand = subject.DisplayHandSide.ToString(), root = Vec(subject.DisplayRootPosition), bones };
    }
    private void Sample()
    {
        if (_finished || _trace == null || _start < 0) return;
        var subject = Subject;
        if (subject == null) return;
        AttachSkeleton(subject);
        Transform3D drawnCamera = _camera.GlobalTransform;
        if (_view == "side")
        {
            var target = subject.DisplayRootPosition + Vector3.Up * 1.2f;
            _camera.GlobalPosition = target + Vector3.Right * 4f;
            _camera.LookAt(target);
        }
        var view = IsClient ? _ball.GetPresentationFrame() : null;
        Vector3 visible = _ball.RenderedWorldPosition;
        var distances = _boneWorld.Where(p => p.Key.EndsWith("Hand", StringComparison.Ordinal)).ToDictionary(p => p.Key,
            p => new { visibleBallCenter = p.Value.DistanceTo(visible), liveBallCenter = p.Value.DistanceTo(_ball.GlobalPosition),
                visibleBallSurface = p.Value.DistanceTo(visible) - _ball.BallRadius });
        string file = "";
        int elapsedTick = _tick - _start;
        if (_images && (elapsedTick is >= 56 and <= 110 or >= 146 and <= 200 || _tick - _lastSeedTick is >= 0 and <= 48))
        {
            file = $"frame-{_samples:D5}.png";
            using var image = GetViewport().GetTexture().GetImage();
            if (image == null || image.IsEmpty() || image.SavePng(Path.Combine(_output, file)) != Error.Ok)
            { Finish("PNG capture failed"); return; }
        }
        var tree = subject.GetNode<AnimationTree>("AnimationTree");
        var machine = (AnimationNodeStateMachine)tree.TreeRoot;
        string state = subject.ActiveAnimNodeForHarness;
        string clip = machine.HasNode(state) && machine.GetNode(state) is AnimationNodeAnimation node ? node.Animation.ToString() : "<blend-space>";
        _trace.WriteLine(JsonSerializer.Serialize(new { sample = _samples++, tick = _tick, elapsedTick = _tick - _start, file,
            physicsFrame = Engine.GetPhysicsFrames(), poseMatchesPhysics = _posePhysics == Engine.GetPhysicsFrames(), pose = _pose,
            role = _role, actor = _actor, subject = _subjectId, isLocalSubject = subject.ContinuityIsLocalPlayer,
            animation = state, clip, playbackTime = subject.ContinuityPlaybackTime, animationCallback = subject.ContinuityAnimationCallback,
            reboundGrab = subject.IsPlayingReboundGrab, displayReboundGrab = view?.Players.FirstOrDefault(p => p.PeerId == _subjectId)?.ReboundGrab,
            hand = subject.HandSide.ToString(), displayHand = subject.DisplayHandSide.ToString(), heading = subject.DisplayHeading,
            confirmedRecovery = new { receipt = _ball.ConfirmedRecovery.Receipt, recipient = _ball.ConfirmedRecovery.Recipient,
                age = _ball.ConfirmedRecovery.Age, inputSequence = _ball.ConfirmedRecovery.InputSequence },
            liveRoot = Vec(subject.GlobalPosition), displayRoot = Vec(subject.DisplayRootPosition), velocity = Vec(subject.Velocity),
            ball = Vec(visible), liveBall = Vec(_ball.GlobalPosition), ballRadius = _ball.BallRadius,
            ballState = _ball.State.ToString(), holder = _ball.StateMachine.HolderPeerId,
            displayBallState = view?.Ball.State.ToString(), displayHolder = view?.Ball.HolderPeerId, distances,
            awardCount = _ball.SuccessfulPossessionAwardCountForHarness, observedRecoveries = _recoveries,
            selectedPresentationTick = view?.Tick ?? -1, posePresentationTick = _posePresentation,
            ballPresentationTick = _ball.RenderedPresentationTick, latestPresentationTick = _ball.LatestPresentationReceivedTick,
            presentationHolding = _ball.PresentationHolding, presentationResyncCount = _ball.PresentationResyncCount,
            camera = Transform(drawnCamera), cameraFov = _camera.Fov,
            viewportSize = new[] { GetViewport().GetVisibleRect().Size.X, GetViewport().GetVisibleRect().Size.Y } }));
    }
    private void Event(string kind, object detail) => _events.Add(new { tick = _tick, physicsFrame = Engine.GetPhysicsFrames(), kind, detail });
    private void Finish(string failure)
    {
        if (_finished) return;
        if (failure.Length == 0 && _controlFailure.Length != 0) failure = _controlFailure;
        bool owningActionRole = !IsClient && _actor == "host" || IsClient && _actor == "client";
        if (failure.Length == 0 && _action != "none" && owningActionRole)
        {
            if (_actionRequests != 2 || _acceptedActions != 2) failure = "control did not witness two accepted owning shot actions";
            else if (_action == "feint" && (_feintRequests != 2 || _acceptedFeints != 2)) failure = "control did not witness two genuine accepted feints";
        }
        if (failure.Length == 0 && _action != "none" && (!IsClient || owningActionRole) &&
            _ball.SuccessfulShotReleasesForHarness != (_action == "immediate" ? 2 : 0)) failure = "control shot release count mismatch";
        if (failure.Length == 0 && _resetNeutral && IsClient && !_resetPerformed) failure = "neutral presentation reset did not execute";
        _finished = true;
        RenderingServer.FramePostDraw -= Sample;
        if (GodotObject.IsInstanceValid(_skeleton)) _skeleton.SkeletonUpdated -= SnapshotPose;
        Input.ActionRelease("move_forward");
        Input.ActionRelease("ball_shoot");
        Input.ActionRelease("move_feint");
        _trace?.Dispose();
        if (_trace != null) File.WriteAllText(Path.Combine(_output, "manifest.json"), JsonSerializer.Serialize(new {
            schema = "hooper-rebound-presentation/v1", result = failure == "" ? "captured" : "failed", failure,
            role = _role, actor = _actor, subject = _subjectId, commit = _commit, requestedPreRecoveryHand = _hand,
            height = _height, moving = _moving, movementAction = "move_forward", contested = _contested, view = _view,
            legacyPose = _legacyPose, longLoose = _longLoose,
            seedDelay = _seedDelay,
            action = _action, actionRequests = _actionRequests, acceptedActions = _acceptedActions,
            feintRequests = _feintRequests, acceptedFeints = _acceptedFeints,
            successfulShotReleases = _ball.SuccessfulShotReleasesForHarness, controlFailure = _controlFailure,
            resetNeutral = _resetNeutral, resetPerformed = _resetPerformed,
            paced = _paced, pacingMeaning = _paced ? "80ms producer wall-clock delay; diagnostic fixed-step coverage only" : "unpaced",
            images = _images, samples = _samples,
            observedRecoveries = _recoveries, authoritativeRecoveriesReceived = _serverRecoveries, events = _events,
            animationResourceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ProjectSettings.GlobalizePath("res://assets/locomotion.res")))),
            engine = Engine.GetVersionInfo()["string"].AsString(), commandLine = OS.GetCmdlineArgs(), userArgs = OS.GetCmdlineUserArgs(),
            sampleTiming = _images ? "SkeletonUpdated final modifier pose / FramePostDraw image" : "HEADLESS diagnostic state only; SkeletonUpdated final modifier pose / _Process state",
            fixture = "normal Main scene, production host/join and animation callback; explicit position/hand/Loose setup; owning movement uses production Input.ActionPress",
            verdict = "diagnostic only; no presentation correctness or feel acceptance" }, new JsonSerializerOptions { WriteIndented = true }));
        GD.Print($"[rebound-capture] {(failure == "" ? "CAPTURED" : "FAIL " + failure)} recoveries={_recoveries} samples={_samples}");
        GetTree().Quit(failure == "" ? 0 : 1);
    }
    private static float[] Vec(Vector3 v) => new[] { v.X, v.Y, v.Z };
    private static float[] Transform(Transform3D t) => Vec(t.Origin).Concat(Vec(t.Basis.X)).Concat(Vec(t.Basis.Y)).Concat(Vec(t.Basis.Z)).ToArray();
}
