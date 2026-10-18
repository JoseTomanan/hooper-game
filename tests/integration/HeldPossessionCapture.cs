using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Optional rendered evidence for #415. Captures never accept human visual/feel criteria.
public partial class HeldPossessionTest
{
    private string _captureOutput = "", _captureCommit = "";
    private Camera3D _captureCamera;
    private Skeleton3D _captureSkeleton;
    private readonly List<object> _captureFrames = new();
    private readonly HashSet<string> _captureLabels = new();
    private Dictionary<string, float[]> _captureBones = new();
    private ulong _capturePosePhysics;
    private int _captureLastTick = -1, _captureLastTransitionTick = -1, _captureNeutralCount, _captureRecoveryCount;
    private string _capturePoseNode = "";

    private void ConfigureCapture(string[] args, Node3D main)
    {
        string output = HarnessArgs.ReadArg(args, "--capture-output", "");
        if (output.Length == 0) return;
        if (DisplayServer.GetName() == "headless") { Finish("rendered capture requires a native rendering window; headless is diagnostic only"); return; }
        _captureCommit = HarnessArgs.ReadArg(args, "--capture-commit", "");
        string path = Path.GetFullPath(ProjectSettings.GlobalizePath(output));
        string workspace = Path.GetFullPath(ProjectSettings.GlobalizePath("res://"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(workspace, StringComparison.OrdinalIgnoreCase) || Directory.Exists(path) || File.Exists(path) || _captureCommit.Length == 0)
        { Finish("capture requires a fresh directory under the workspace and --capture-commit provenance"); return; }
        Directory.CreateDirectory(path);
        _captureOutput = path;
        _captureCamera = main.GetNode<Camera3D>("Camera3D");
        _captureSkeleton = FindCaptureSkeleton(_holder);
        if (_captureSkeleton == null) { Finish("capture skeleton missing"); return; }
        // SkeletonUpdated fires after modifiers; a later direct bone query can see restored input poses.
        // https://docs.godotengine.org/en/4.7/classes/class_skeleton3d.html#class-skeleton3d-signal-skeleton-updated
        _captureSkeleton.SkeletonUpdated += SnapshotCaptureBones;
        // Readback after the rendered frame, never a queued semantic label from an earlier physics tick.
        // https://docs.godotengine.org/en/4.7/classes/class_renderingserver.html#class-renderingserver-signal-frame-post-draw
        RenderingServer.FramePostDraw += CaptureRenderedFrame;
    }

    public override void _Process(double delta)
    {
        if (_captureOutput.Length == 0 || _finished) return;
        // Reframe the production Main camera only; gameplay positions and input stages remain unchanged.
        // The CharacterBody origin is already one metre above the floor. Aim
        // at its centre so the viewport retains the feet as well as the carry.
        Vector3 target = _holder.DisplayRootPosition + new Vector3(0, -0.1f, 0);
        Vector2 planarForward = HeadingMath.Forward(_holder.DisplayHeading);
        Vector3 forward = new(planarForward.X, 0, planarForward.Y);
        Vector3 ballSide = forward.Cross(Vector3.Up) * (_holder.DisplayHandSide == HandSide.Right ? 1f : -1f);
        _captureCamera.GlobalPosition = target + forward * 2.8f + ballSide * 1.7f + Vector3.Up * 0.7f;
        _captureCamera.Projection = Camera3D.ProjectionType.Perspective;
        _captureCamera.Fov = 50;
        _captureCamera.LookAt(target, Vector3.Up);
        _captureCamera.Current = true;
    }

    private static Skeleton3D FindCaptureSkeleton(Node node)
    {
        if (node is Skeleton3D skeleton) return skeleton;
        foreach (var child in node.GetChildren())
        {
            var found = FindCaptureSkeleton(child);
            if (found != null) return found;
        }
        return null;
    }

    private void SnapshotCaptureBones()
    {
        var positions = new Dictionary<string, float[]>();
        for (int i = 0; i < _captureSkeleton.GetBoneCount(); i++)
        {
            string name = _captureSkeleton.GetBoneName(i);
            if (name.EndsWith("Hand", StringComparison.Ordinal) || name.EndsWith("ForeArm", StringComparison.Ordinal))
                positions[name] = CaptureVec(_captureSkeleton.ToGlobal(_captureSkeleton.GetBoneGlobalPose(i).Origin));
        }
        _captureBones = positions;
        _capturePosePhysics = Engine.GetPhysicsFrames();
        _capturePoseNode = _holder.ActiveAnimNodeForHarness;
    }

    private void CaptureRenderedFrame()
    {
        if (_finished || _captureOutput.Length == 0 || _tick == _captureLastTick) return;
        var (phase, _) = _holder.DisplayMove();
        string node = _holder.ActiveAnimNodeForHarness;
        bool neutral = phase == MovePhase.Inactive && node == "Held" + _holder.DisplayHandSide;
        float speed = new Vector2(_holder.DisplayVelocity.X, _holder.DisplayVelocity.Z).Length();
        string label = "";
        if (_stage == 0 && _stageTick >= 6 && neutral && Held(false) && speed < 0.02f && !_captureLabels.Contains("fresh-idle")) label = "fresh-idle";
        if (_stage == 9 && _stageTick >= 6 && neutral && Held(true) && speed < 0.02f && !_captureLabels.Contains("spent-idle")) label = "spent-idle";
        if (_stage == 10 && _stageTick >= 6 && neutral && Held(true) && speed >= _holder.MoveSpeed * .9f && !_captureLabels.Contains("spent-moving")) label = "spent-moving";
        bool recovery = _stage == 8 && _holder.DisplayMoveId() == "stepback" && phase == MovePhase.Recovery;
        bool returnToHeld = _stage == 8 && _captureRecoveryCount > 0 && neutral && _captureNeutralCount < 8;
        if (recovery) label = "stepback-recovery";
        if (returnToHeld) label = "stepback-to-held";
        if (label.Length == 0) return;
        // A final-pose snapshot from this physics tick must accompany the actual playback sampled now.
        if (_capturePosePhysics != Engine.GetPhysicsFrames() || _capturePoseNode != node || _captureBones.Count < 4) return;
        if (recovery || returnToHeld)
        {
            if (_captureLastTransitionTick >= 0 && _tick != _captureLastTransitionTick + 1)
                Require(false, "rendered StepBack transition capture has a physics-tick gap");
            _captureLastTransitionTick = _tick;
            if (recovery) _captureRecoveryCount++; else _captureNeutralCount++;
        }
        using var picture = GetViewport().GetTexture().GetImage();
        if (picture == null || picture.IsEmpty()) { Finish("rendered capture image is empty"); return; }
        float maxBrightness = 0;
        for (int y = 0; y < picture.GetHeight(); y += 16)
            for (int x = 0; x < picture.GetWidth(); x += 16)
            {
                Color color = picture.GetPixel(x, y);
                maxBrightness = MathF.Max(maxBrightness, MathF.Max(color.R, MathF.Max(color.G, color.B)));
            }
        if (maxBrightness < .01f) { Finish("rendered capture image is black"); return; }
        string file = $"{_captureFrames.Count:D3}-{label}-tick-{_tick:D4}.png";
        if (picture.SavePng(Path.Combine(_captureOutput, file)) != Error.Ok) { Finish("PNG write failed"); return; }
        _captureFrames.Add(new {
            file, label, tick = _tick, stage = StageNames[_stage], stageTick = _stageTick,
            physicsFrame = Engine.GetPhysicsFrames(), renderFrame = Engine.GetProcessFrames(),
            move = _holder.DisplayMoveId(), phase = phase.ToString(), frameInPhase = _holder.FrameInPhaseForHarness,
            playbackNode = node, boundClips = BoundClips(node), hand = _holder.DisplayHandSide.ToString(),
            ballState = _ball.State.ToString(), holder = _ball.StateMachine.HolderPeerId, hasDribbled = _ball.HasDribbled,
            inputKeys = _keys.ToDictionary(pair => pair.Key.ToString(), pair => _tick - pair.Value),
            speed, boneWorldPositions = _captureBones, posePhysicsFrame = _capturePosePhysics,
            ballWorldPosition = CaptureVec(_ball.RenderedWorldPosition), liveBallWorldPosition = CaptureVec(_ball.GlobalPosition),
            cameraWorldPosition = CaptureVec(_captureCamera.GlobalPosition), cameraTarget = CaptureVec(_holder.DisplayRootPosition + new Vector3(0, -0.1f, 0)),
            width = picture.GetWidth(), height = picture.GetHeight(), maxBrightness
        });
        _captureLabels.Add(label);
        _captureLastTick = _tick;
        GD.Print($"[held-possession] rendered file={file} node={node} phase={phase}");
    }

    private void CompleteCapture()
    {
        if (_captureOutput.Length == 0) return;
        RenderingServer.FramePostDraw -= CaptureRenderedFrame;
        if (_captureSkeleton != null) _captureSkeleton.SkeletonUpdated -= SnapshotCaptureBones;
        Require(new[] { "fresh-idle", "spent-idle", "spent-moving" }.All(_captureLabels.Contains), "rendered capture includes fresh idle, spent idle and spent moving");
        Require(_captureRecoveryCount >= StepBack.DefaultFrameData.RecoveryFrames && _captureNeutralCount == 8,
            "rendered capture includes every StepBack Recovery tick and first eight Held ticks");
        File.WriteAllText(Path.Combine(_captureOutput, "manifest.json"), JsonSerializer.Serialize(new {
            issue = 415, commit = _captureCommit, scenario = _scenario, renderedEvidence = true, humanVisualAcceptance = false,
            engine = Engine.GetVersionInfo()["string"].AsString(), displayDriver = DisplayServer.GetName(),
            renderer = RenderingServer.GetVideoAdapterName(), commandLine = OS.GetCmdlineArgs(), userArgs = OS.GetCmdlineUserArgs(),
            captureSurface = "Main.tscn production camera reframed approximately 3.3m from holder; FramePostDraw readback; final modified skeleton snapshot",
            recoveryFrames = _captureRecoveryCount, neutralFrames = _captureNeutralCount,
            failures = _failures.ToArray(), frames = _captureFrames
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static float[] CaptureVec(Vector3 value) => new[] { value.X, value.Y, value.Z };
}
