using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using Hooper.Ball;
using Hooper.Networking;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

/// <summary>
/// Rendered-evidence spike for #365.  This is intentionally an executable
/// experiment, not a feel verdict: it proves that a production game viewport
/// can be captured with enough provenance and non-vacuity checks for a human
/// to inspect later.  The local role drives the real host player's movement and
/// committed-move choke points; server/client roles prove one image is from a
/// genuine remote display path.
/// </summary>
public partial class RenderedEvidenceCapture : Node
{
    private const int SettleFrames = 45;
    private const int LocalTimeoutFrames = 520;
    private const int RemoteTimeoutFrames = 600;
    private const int RemotePortFallback = 23461;
    private const int MinWidth = 320;
    private const int MinHeight = 180;
    private const float MinMeanLuma = 0.01f;
    private const float MinLumaRange = 0.02f;

    private string _role = "local";
    private string _outputRoot = "";
    private string _commit = "unknown";
    private int _port = RemotePortFallback;
    private bool _brokenScenario;
    private bool _finished;
    private int _frame;
    private int _captureIndex;
    private int _remotePeerId = -1;
    private bool _serverSawClient;
    private bool _serverStartedMove;
    private bool _sawLocalDribble;
    private bool _sawLeftHand;
    private bool _sawRightHand;
    private bool _sawDirectionChange;
    private Vector3 _velocityBeforeDirectionChange;
    private Vector3 _velocityAfterDirectionChange;
    private bool _sawFadeaway;
    private bool _sawPivot;
    private bool _sawRemoteDisplay;
    private bool _shotBegun;
    private bool _pivotStarted;
    private int _pivotStartFrame = -1;
    private readonly List<CaptureEntry> _captures = new();
    private readonly List<CaptureAttempt> _attempts = new();
    private readonly List<EventEntry> _events = new();
    private readonly Dictionary<string, CaptureRequest> _pendingCaptures = new();

    private Node3D _main = null!;
    private NetworkManager _network = null!;
    private Node _players = null!;
    private BallController _ball = null!;
    private Camera3D _camera = null!;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _role = HarnessArgs.ReadArg(args, "--capture-role", "local");
        _outputRoot = HarnessArgs.ReadArg(args, "--capture-output", "");
        _commit = HarnessArgs.ReadArg(args, "--capture-commit", "unknown");
        _brokenScenario = HarnessArgs.ReadArg(args, "--capture-mutation", "") == "missing-intended-state";
        _port = int.TryParse(HarnessArgs.ReadArg(args, "--capture-port", RemotePortFallback.ToString()), out int port)
            ? port : RemotePortFallback;

        if (string.IsNullOrWhiteSpace(_outputRoot))
        {
            Fail("--capture-output is required; refusing to write an unproven or stale evidence location.");
            Finish(1);
            return;
        }
        _outputRoot = ProjectSettings.GlobalizePath(_outputRoot);
        // Movie Maker opens its AVI before this scene reaches _Ready. Permit
        // only that one known writer-owned file; anything else remains stale
        // evidence and is rejected before a manifest can reuse it.
        if (Directory.Exists(_outputRoot) && Directory.EnumerateFileSystemEntries(_outputRoot)
            .Any(path => !string.Equals(Path.GetFileName(path), "capture.avi", StringComparison.Ordinal)))
        {
            Fail($"capture output '{_outputRoot}' is not empty. Each run needs a fresh directory so old frames cannot satisfy a new manifest.");
            Finish(1);
            return;
        }
        Directory.CreateDirectory(_outputRoot);

        _main = GetNode<Node3D>("Main");
        _network = _main.GetNode<NetworkManager>("NetworkManager");
        _players = _main.GetNode("Players");
        _ball = _main.GetNode<BallController>("Ball");
        _camera = _main.GetNode<Camera3D>("Camera3D");

        // A missing/currently-disabled gameplay camera is a capture failure, not
        // an opportunity to synthesize a test camera with easier framing.
        if (!_camera.Current)
        {
            Fail("Main/Camera3D is not the active gameplay camera.");
            Finish(1);
            return;
        }

        if (_role == "client")
        {
            Multiplayer.ConnectedToServer += OnClientConnected;
            Multiplayer.ConnectionFailed += OnClientFailed;
            _network.JoinGame("127.0.0.1", _port);
        }
        else
        {
            Multiplayer.PeerConnected += OnServerPeerConnected;
            _network.HostGame(_port);
        }

        RecordEvent("boot", "role=" + _role);
        GD.Print($"[rendered-evidence] role={_role} output={_outputRoot} port={_port} booted");
    }

    private void OnClientConnected()
    {
        _remotePeerId = Multiplayer.GetUniqueId();
        RecordEvent("client-connected", "peer=" + _remotePeerId);
        if (_remotePeerId == 1)
        {
            Fail("client received peer id 1, so Players/1 would be local and remote display proof would be void.");
            Finish(1);
        }
    }

    private void OnClientFailed()
    {
        Fail("ENet client connection failed.");
        Finish(1);
    }

    private void OnServerPeerConnected(long peerId)
    {
        _serverSawClient = true;
        RecordEvent("server-peer-connected", "peer=" + peerId);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _frame++;

        if (_role == "local") TickLocal();
        else if (_role == "server") TickServer();
        else if (_role == "client") TickClient();
        else
        {
            Fail($"unknown --capture-role '{_role}'.");
            Finish(1);
            return;
        }

        int limit = _role == "local" ? LocalTimeoutFrames : RemoteTimeoutFrames;
        if (!_finished && _frame > limit)
        {
            Fail($"timed out at physics frame {_frame}: local dribble={_sawLocalDribble}, left={_sawLeftHand}, right={_sawRightHand}, directionChange={_sawDirectionChange}, fadeaway={_sawFadeaway}, pivot={_sawPivot}, remote={_sawRemoteDisplay}.");
            Finish(1);
        }
    }

    private void TickLocal()
    {
        var player = PlayerOne();
        if (player == null) return;

        // Production input, retained in the manifest. The host owns player 1,
        // so this reaches PlayerController.Move()/auto-dribble exactly as a
        // local gameplay session does.
        if (_frame == SettleFrames)
        {
            // Main/Camera3D faces the court from its baseline. Moving the
            // host forward exercises the normal dribble path while keeping
            // its actual production model inside that unchanged camera view.
            Input.ActionPress("move_forward", 1f);
            RecordEvent("input-down", "move_forward");
        }
        if (_frame == SettleFrames + 50)
        {
            _velocityBeforeDirectionChange = player.Velocity;
            Input.ActionRelease("move_forward");
            Input.ActionPress("move_right", 1f);
            RecordEvent("input-direction-change", "move_forward->move_right");
        }
        if (_frame == SettleFrames + 95)
        {
            Input.ActionRelease("move_right");
            RecordEvent("input-up", "move_right");
        }

        if (_ball.State == BallState.Dribbling)
        {
            _sawLocalDribble = true;
            if (player.HandSide == HandSide.Left) _sawLeftHand = true;
            if (player.HandSide == HandSide.Right) _sawRightHand = true;
        }
        if (_frame >= SettleFrames + 70 && player.Velocity.LengthSquared() > 0.01f)
        {
            _velocityAfterDirectionChange = player.Velocity;
            if (_velocityBeforeDirectionChange.LengthSquared() > 0.01f &&
                _velocityBeforeDirectionChange.Normalized().Dot(_velocityAfterDirectionChange.Normalized()) < 0.8f)
                _sawDirectionChange = true;
        }

        if (_frame == SettleFrames + 75)
            QueueCapture("stationary-to-moving-dribble", player, "Dribble", false);

        // A real committed-move path is used after the movement input has
        // supplied the live-dribble precondition. The existing harness seam
        // calls PlayerController.BeginCommittedMove, the production choke point;
        // it avoids only hardware gesture timing, which is not deterministic in
        // a scripted renderer run.
        if (_frame == SettleFrames + 110)
        {
            bool began = player.BeginBehindTheBackForHarness(+1f);
            RecordEvent("committed-move", "behindtheback began=" + began);
            if (!began) { Fail("BehindTheBack did not begin from the recorded live dribble."); Finish(1); return; }
        }

        // Bind the evidence to the exact production StartupRight state, not to
        // HandSide: the latter flips at Active entry while the renderer keeps
        // the origin-hand suffix. Once that one frame is queued, the next
        // physics tick may legitimately advance to a left-hand Active variant.
        bool awaitingBehindTheBackCapture = !_captures.Any(c => c.Label == "both-hands-direction-change") &&
                                         !_pendingCaptures.ContainsKey("both-hands-direction-change");
        string behindTheBackState = player.ActiveAnimNodeForHarness;
        if (player.DisplayMoveId() == "behindtheback" && awaitingBehindTheBackCapture &&
            behindTheBackState == "BehindTheBackStartupLeft")
        {
            Fail("BehindTheBack AnimationTree entered the left-hand Startup variant, expected BehindTheBackStartupRight.");
            Finish(1);
            return;
        }
        if (player.DisplayMoveId() == "behindtheback" && awaitingBehindTheBackCapture &&
            behindTheBackState == "BehindTheBackStartupRight")
        {
            _sawRightHand = true; // records the displayed entry, not the later swapped ball hand.
            QueueCaptureOnce("both-hands-direction-change", player, "BehindTheBackStartupRight", false);
        }

        if (!_shotBegun && _frame > SettleFrames + 190 && player.PhaseForHarness == Hooper.Moves.MovePhase.Inactive)
        {
            // A back-facing heading makes the real JumpShot resolver select its
            // FadeawayActive state. This is controlled setup (as in
            // JumpshotAnimTest), while BeginJumpShotForHarness still runs the
            // same production begin path and subsequent AnimationTree drive.
            Vector3 rimDelta = _ball.RimCenter - player.GlobalPosition;
            player.SetHeadingForHarness(Mathf.Atan2(rimDelta.X, rimDelta.Z) + Mathf.Pi);
            _shotBegun = player.BeginJumpShotForHarness();
            RecordEvent("committed-move", "jumpshot-fadeaway began=" + _shotBegun);
            if (!_shotBegun) { Fail("JumpShot did not begin after BehindTheBack recovery."); Finish(1); return; }
        }

        if (_shotBegun && player.ActiveAnimNodeForHarness == "FadeawayActive")
        {
            _sawFadeaway = true;
            QueueCaptureOnce("shot-fadeaway", player, "FadeawayActive", false);
        }

        if (!_pivotStarted && _shotBegun && _sawFadeaway && player.PhaseForHarness == Hooper.Moves.MovePhase.Inactive)
        {
            _pivotStarted = true;
            _pivotStartFrame = _frame;
            Input.ActionPress("move_forward", 1f);
            RecordEvent("input-down", "move_forward (pivot reversal)");
        }
        // Match PivotAnimTest's one-tick flick. A global frame-parity release
        // could release the action in the same tick it was pressed, before
        // PlayerController samples it and creates the pivot latch.
        if (_pivotStarted && _frame == _pivotStartFrame + 1)
        {
            Input.ActionRelease("move_forward");
            RecordEvent("input-up", "move_forward (pivot reversal)");
        }
        if (_pivotStarted && player.IsPivotingInPlace && player.ActiveAnimNodeForHarness == "Pivot")
        {
            _sawPivot = true;
            QueueCaptureOnce("pivot", player, "Pivot", false);
        }

        if (_captures.Count >= 4 && _captures.All(c => c.ByteLength > 0) && _sawLocalDribble && _sawLeftHand && _sawRightHand && _sawDirectionChange && _sawFadeaway && _sawPivot)
            Finish(0);
    }

    private void TickServer()
    {
        if (!_serverSawClient || _frame < SettleFrames) return;
        var player = PlayerOne();
        if (player == null || _ball.StateMachine.HolderPeerId != 1) return;
        if (_ball.State != BallState.Dribbling) _ball.TryStartDribble(1);
        if (!_serverStartedMove && _ball.State == BallState.Dribbling)
        {
            _serverStartedMove = player.BeginBehindTheBackForHarness(+1f);
            RecordEvent("server-remote-source", "behindtheback began=" + _serverStartedMove);
            if (!_serverStartedMove) { Fail("server could not begin remote BehindTheBack."); Finish(1); }
        }
        // Client owns the remote rendered verdict. Keep broadcasts alive until it
        // exits or the bounded timeout fires.
    }

    private void TickClient()
    {
        if (_remotePeerId <= 0) return;
        if (Multiplayer.IsServer()) { Fail("client process is server; remote proof is invalid."); Finish(1); return; }
        var remote = PlayerOne();
        if (remote == null) return;
        bool remoteRole = remote.Name == "1" && remote.Name != _remotePeerId.ToString();
        bool displayedMove = remote.DisplayMoveId() == "behindtheback";
        bool liveTree = remote.ActiveAnimNodeForHarness.Contains("Behind", StringComparison.Ordinal);
        if (remoteRole && displayedMove && liveTree)
        {
            _sawRemoteDisplay = true;
            QueueCaptureOnce("remote-player-display", remote, "BehindTheBack", true);
        }
        if (_sawRemoteDisplay && _captures.Count == 1 && _captures[0].ByteLength > 0) Finish(0);
    }

    private PlayerController PlayerOne() => _players.GetNodeOrNull<PlayerController>("1");

    private void QueueCaptureOnce(string label, PlayerController subject, string expectedState, bool remote)
    {
        if (_captures.Any(c => c.Label == label) || _pendingCaptures.ContainsKey(label)) return;
        QueueCapture(label, subject, expectedState, remote);
    }

    private void QueueCapture(string label, PlayerController subject, string expectedState, bool remote)
    {
        string wanted = _brokenScenario && label == "stationary-to-moving-dribble" ? "DefinitelyMissingState" : expectedState;
        _pendingCaptures[label] = new CaptureRequest(label, subject, wanted, remote, _frame);
        // Godot documents frame_post_draw as the point at which a viewport image
        // is safe to store. Capture-time state and framing must be sampled there
        // too, so the manifest cannot bind a prior physics-tick state to a later
        // rendered image. Source: https://docs.godotengine.org/en/4.7/classes/class_viewport.html
        CallDeferred(nameof(CaptureAfterDraw), label);
    }

    private async void CaptureAfterDraw(string label)
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        if (!_pendingCaptures.Remove(label, out CaptureRequest request)) return;

        CaptureAttempt attempt = SnapshotAttempt(request);
        _attempts.Add(attempt);
        if (!attempt.StateMatches || !attempt.ProductionCameraIsActive || attempt.AnchorBehind ||
            !attempt.AnchorInFrustum || !attempt.AnchorInViewport)
        {
            attempt.Outcome = "rejected";
            attempt.Reason = DescribeRejection(attempt);
            Fail($"{label}: {attempt.Reason}");
            Finish(1);
            return;
        }

        string file = $"{++_captureIndex:D2}-{label}.png";
        Image image = GetViewport().GetTexture().GetImage();
        if (!ValidateImage(image, label, out string imageFailure))
        {
            attempt.Outcome = "rejected";
            attempt.Reason = imageFailure;
            Fail(imageFailure);
            Finish(1);
            return;
        }
        string path = Path.Combine(_outputRoot, file);
        Error save = image.SavePng(path);
        if (save != Error.Ok || !File.Exists(path) || new FileInfo(path).Length == 0)
        {
            attempt.Outcome = "rejected";
            attempt.Reason = $"SavePng failed ({save}) or wrote no bytes.";
            Fail($"{label}: {attempt.Reason}");
            Finish(1);
            return;
        }
        var entry = new CaptureEntry
        {
            Label = label, File = file, PhysicsFrame = request.QueuedPhysicsFrame,
            SavedAfterRenderFrame = Engine.GetProcessFrames(), ExpectedAnimationState = request.ExpectedState,
            ObservedAnimationState = attempt.ObservedAnimationState, DisplayMoveId = attempt.DisplayMoveId,
            HandSide = attempt.HandSide, RemoteDisplay = request.Remote, SubjectPosition = attempt.SubjectPosition,
            SubjectAnchor = attempt.SubjectAnchor, SubjectScreen = attempt.SubjectScreen,
            SubjectNodePath = attempt.SubjectNodePath, SubjectPeerName = attempt.SubjectPeerName,
            SubjectAuthority = attempt.SubjectAuthority, BallPosition = Vec(_ball.GlobalPosition),
            DirectionBefore = Vec(_velocityBeforeDirectionChange),
            DirectionAfter = Vec(_velocityAfterDirectionChange), DirectionChanged = _sawDirectionChange,
            CameraTransform = attempt.CameraTransform, CameraFov = _camera.Fov,
            Viewport = attempt.Viewport, AttemptIndex = _attempts.Count - 1
        };
        entry.ByteLength = new FileInfo(path).Length;
        entry.Width = image.GetWidth();
        entry.Height = image.GetHeight();
        attempt.Outcome = "accepted";
        attempt.File = file;
        GD.Print($"[rendered-evidence] captured {entry.File} physics={entry.PhysicsFrame} render={entry.SavedAfterRenderFrame}");
        _captures.Add(entry);
    }

    private CaptureAttempt SnapshotAttempt(CaptureRequest request)
    {
        // These queries intentionally use the SAME head-height anchor. Camera3D
        // documents that IsPositionBehind only rules out the behind-camera case;
        // IsPositionInFrustum and the projected visible-rect check are separate
        // constraints. Source: https://docs.godotengine.org/en/4.7/classes/class_camera3d.html
        Vector3 anchor = request.Subject.GlobalPosition + Vector3.Up;
        Vector2 screen = _camera.UnprojectPosition(anchor);
        Rect2 viewport = GetViewport().GetVisibleRect();
        Camera3D activeCamera = GetViewport().GetCamera3D();
        return new CaptureAttempt
        {
            Label = request.Label, QueuedPhysicsFrame = request.QueuedPhysicsFrame,
            RenderFrame = Engine.GetProcessFrames(), ExpectedAnimationState = request.ExpectedState,
            ObservedAnimationState = request.Subject.ActiveAnimNodeForHarness,
            DisplayMoveId = request.Subject.DisplayMoveId(), HandSide = request.Subject.HandSide.ToString(),
            SubjectNodePath = request.Subject.GetPath().ToString(), SubjectPeerName = request.Subject.Name.ToString(),
            SubjectAuthority = request.Subject.GetMultiplayerAuthority(), SubjectPosition = Vec(request.Subject.GlobalPosition),
            SubjectAnchor = Vec(anchor), SubjectScreen = new[] { screen.X, screen.Y },
            Viewport = new[] { viewport.Position.X, viewport.Position.Y, viewport.Size.X, viewport.Size.Y },
            CameraTransform = Transform(_camera.GetCameraTransform()), ProductionCameraIsActive = activeCamera == _camera,
            AnchorBehind = _camera.IsPositionBehind(anchor), AnchorInFrustum = _camera.IsPositionInFrustum(anchor),
            AnchorInViewport = IsFinite(screen) && viewport.Size.X > 0f && viewport.Size.Y > 0f && viewport.HasPoint(screen),
            StateMatches = StateMatches(request.ExpectedState, request.Subject.ActiveAnimNodeForHarness)
        };
    }

    private static bool StateMatches(string expected, string observed) => expected switch
    {
        "Dribble" => observed is "DribbleLeft" or "DribbleRight",
        "BehindTheBack" => observed.StartsWith("BehindTheBack", StringComparison.Ordinal),
        _ => string.Equals(expected, observed, StringComparison.Ordinal)
    };

    private static bool IsFinite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static string DescribeRejection(CaptureAttempt attempt)
    {
        if (!attempt.StateMatches) return $"intended live AnimationTree state '{attempt.ExpectedAnimationState}' was not observed at capture (actual '{attempt.ObservedAnimationState}').";
        if (!attempt.ProductionCameraIsActive) return "Main/Camera3D was not the viewport's active camera at capture.";
        if (attempt.AnchorBehind) return "the recorded head-height capture anchor was behind Main/Camera3D.";
        if (!attempt.AnchorInFrustum) return "the recorded head-height capture anchor was outside Main/Camera3D's frustum.";
        return "the recorded head-height capture anchor projected outside the viewport.";
    }

    private static bool ValidateImage(Image image, string label, out string failure)
    {
        failure = "";
        if (image == null || image.IsEmpty() || image.GetWidth() < MinWidth || image.GetHeight() < MinHeight)
        {
            failure = $"{label}: image is empty or too small ({image?.GetWidth()}x{image?.GetHeight()}); renderer output is unusable.";
            return false;
        }
        float min = 1f, max = 0f, sum = 0f;
        const int samples = 32;
        for (int y = 0; y < samples; y++)
        for (int x = 0; x < samples; x++)
        {
            Color c = image.GetPixel(x * (image.GetWidth() - 1) / (samples - 1), y * (image.GetHeight() - 1) / (samples - 1));
            float luma = 0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B;
            min = Mathf.Min(min, luma); max = Mathf.Max(max, luma); sum += luma;
        }
        float mean = sum / (samples * samples);
        if (mean < MinMeanLuma || max - min < MinLumaRange)
        {
            failure = $"{label}: black/flat renderer output (mean={mean:F4}, range={max - min:F4}).";
            return false;
        }
        return true;
    }

    private void RecordEvent(string name, string detail) => _events.Add(new EventEntry { Name = name, Detail = detail, PhysicsFrame = _frame });

    private void Finish(int code)
    {
        if (_finished) return;
        _finished = true;
        Input.ActionRelease("move_backward");
        Input.ActionRelease("move_right");
        Input.ActionRelease("move_forward");
        WriteManifest(code);
        GD.Print($"[rendered-evidence] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }

    private void WriteManifest(int code)
    {
        if (string.IsNullOrWhiteSpace(_outputRoot)) return;
        try
        {
            Directory.CreateDirectory(_outputRoot);
            bool cameraReady = _camera != null;
            var manifest = new Manifest
            {
                Schema = "hooper-rendered-evidence/v1",
                Result = code == 0 ? "pass" : "fail",
                Role = _role,
                Commit = _commit,
                EngineVersion = Engine.GetVersionInfo()["string"].AsString(),
                PhysicsTicksPerSecond = Engine.PhysicsTicksPerSecond,
                RendererSetting = ProjectSettings.GetSetting("rendering/renderer/rendering_method", "unknown").AsString(),
                // Godot 4.7 exposes the renderer backend through the command
                // line, but exposes the *actual active adapter* through
                // RenderingServer. Record both; an empty adapter/API string is
                // a capture failure in the external verifier.
                EffectiveRenderingMethod = HarnessArgs.ReadArg(OS.GetCmdlineArgs(), "--rendering-method", "unknown"),
                EffectiveDisplayDriver = DisplayServer.GetName(),
                AdapterName = RenderingServer.GetVideoAdapterName(),
                AdapterVendor = RenderingServer.GetVideoAdapterVendor(),
                AdapterApiVersion = RenderingServer.GetVideoAdapterApiVersion(),
                Camera = cameraReady ? Transform(_camera.GlobalTransform) : Array.Empty<float>(),
                CameraFov = cameraReady ? _camera.Fov : 0f,
                InputsAndEvents = _events,
                Attempts = _attempts,
                Captures = _captures
            };
            File.WriteAllText(Path.Combine(_outputRoot, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));
        }
        catch (Exception ex)
        {
            GD.PrintErr("[rendered-evidence] failed to write manifest: " + ex);
        }
    }

    private void Fail(string message) => GD.PrintErr("[rendered-evidence] FAIL: " + message);
    private static float[] Vec(Vector3 v) => new[] { v.X, v.Y, v.Z };
    private static float[] Transform(Transform3D t) => new[] { t.Origin.X, t.Origin.Y, t.Origin.Z, t.Basis.X.X, t.Basis.X.Y, t.Basis.X.Z, t.Basis.Y.X, t.Basis.Y.Y, t.Basis.Y.Z, t.Basis.Z.X, t.Basis.Z.Y, t.Basis.Z.Z };

    private sealed class EventEntry { public string Name { get; set; } = ""; public string Detail { get; set; } = ""; public int PhysicsFrame { get; set; } }
    private sealed class CaptureEntry
    {
        public string Label { get; set; } = ""; public string File { get; set; } = ""; public int PhysicsFrame { get; set; }
        public ulong SavedAfterRenderFrame { get; set; } public string ExpectedAnimationState { get; set; } = ""; public string ObservedAnimationState { get; set; } = "";
        public string DisplayMoveId { get; set; } = ""; public string HandSide { get; set; } = ""; public bool RemoteDisplay { get; set; } public float[] SubjectPosition { get; set; } = Array.Empty<float>();
        public float[] BallPosition { get; set; } = Array.Empty<float>(); public float[] SubjectAnchor { get; set; } = Array.Empty<float>(); public float[] SubjectScreen { get; set; } = Array.Empty<float>(); public string SubjectNodePath { get; set; } = ""; public string SubjectPeerName { get; set; } = ""; public int SubjectAuthority { get; set; } public float[] DirectionBefore { get; set; } = Array.Empty<float>(); public float[] DirectionAfter { get; set; } = Array.Empty<float>(); public bool DirectionChanged { get; set; } public float[] CameraTransform { get; set; } = Array.Empty<float>();
        public float CameraFov { get; set; } public float[] Viewport { get; set; } = Array.Empty<float>(); public int AttemptIndex { get; set; } public int Width { get; set; } public int Height { get; set; } public long ByteLength { get; set; }
    }
    private sealed record CaptureRequest(string Label, PlayerController Subject, string ExpectedState, bool Remote, int QueuedPhysicsFrame);
    private sealed class CaptureAttempt
    {
        public string Label { get; set; } = ""; public string Outcome { get; set; } = "pending"; public string Reason { get; set; } = ""; public string File { get; set; } = "";
        public int QueuedPhysicsFrame { get; set; } public ulong RenderFrame { get; set; } public string ExpectedAnimationState { get; set; } = ""; public string ObservedAnimationState { get; set; } = ""; public string DisplayMoveId { get; set; } = ""; public string HandSide { get; set; } = "";
        public string SubjectNodePath { get; set; } = ""; public string SubjectPeerName { get; set; } = ""; public int SubjectAuthority { get; set; } public float[] SubjectPosition { get; set; } = Array.Empty<float>(); public float[] SubjectAnchor { get; set; } = Array.Empty<float>(); public float[] SubjectScreen { get; set; } = Array.Empty<float>(); public float[] Viewport { get; set; } = Array.Empty<float>(); public float[] CameraTransform { get; set; } = Array.Empty<float>();
        public bool ProductionCameraIsActive { get; set; } public bool AnchorBehind { get; set; } public bool AnchorInFrustum { get; set; } public bool AnchorInViewport { get; set; } public bool StateMatches { get; set; }
    }
    private sealed class Manifest
    {
        public string Schema { get; set; } = ""; public string Result { get; set; } = ""; public string Role { get; set; } = ""; public string Commit { get; set; } = "";
        public string EngineVersion { get; set; } = ""; public int PhysicsTicksPerSecond { get; set; } public string RendererSetting { get; set; } = ""; public string EffectiveRenderingMethod { get; set; } = ""; public string EffectiveDisplayDriver { get; set; } = ""; public string AdapterName { get; set; } = ""; public string AdapterVendor { get; set; } = ""; public string AdapterApiVersion { get; set; } = "";
        public float[] Camera { get; set; } = Array.Empty<float>(); public float CameraFov { get; set; } public List<EventEntry> InputsAndEvents { get; set; } = new(); public List<CaptureAttempt> Attempts { get; set; } = new(); public List<CaptureEntry> Captures { get; set; } = new();
    }
}
