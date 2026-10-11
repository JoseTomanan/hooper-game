using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

public partial class PostPresentationTest
{
    private bool _captureMode, _capturePending;
    private int _captureIndex, _captureSettle;
    private Camera3D _captureCamera;
    private string _captureOutput, _captureCommit;
    private readonly List<object> _captureEntries = new();
    private static readonly Color CaptureBackground = new(.15f, .17f, .20f);
    private record Sample(string Name, HandSide Hand, bool Post, MovePhase Phase, double Seconds, float Heading);
    private static readonly Sample[] CaptureSamples =
    {
        new("neutral-right", HandSide.Right, false, MovePhase.Inactive, .20, 0),
        new("post-right", HandSide.Right, true, MovePhase.Inactive, .20, 0),
        new("neutral-left", HandSide.Left, false, MovePhase.Inactive, .20, 0),
        new("post-left", HandSide.Left, true, MovePhase.Inactive, .20, 0),
        new("turnaround-entry", HandSide.Right, true, MovePhase.Startup, 0, 0),
        new("turnaround-turn", HandSide.Right, true, MovePhase.Startup, .25, Mathf.Pi / 2),
        new("turnaround-windup", HandSide.Right, true, MovePhase.Startup, .65, Mathf.Pi),
        new("turnaround-release", HandSide.Right, true, MovePhase.Active, .02, Mathf.Pi),
        new("turnaround-recovery", HandSide.Right, true, MovePhase.Recovery, .18, Mathf.Pi),
    };

    private void SetupCapture()
    {
        _captureOutput = HarnessArgs.ReadArg(OS.GetCmdlineUserArgs(), "--capture-output", "");
        _captureCommit = HarnessArgs.ReadArg(OS.GetCmdlineUserArgs(), "--capture-commit", "");
        Require(_captureOutput.Length > 0 && _captureCommit.Length > 0, "capture requires --capture-output and --capture-commit");
        Require(DisplayServer.GetName() != "headless", "capture requires a native renderer, not --headless");
        _captureOutput = ProjectSettings.GlobalizePath(_captureOutput);
        Require(!Directory.Exists(_captureOutput) || !Directory.EnumerateFileSystemEntries(_captureOutput).Any(),
            "capture output is not empty; supply a fresh per-run directory");
        Directory.CreateDirectory(_captureOutput);
        _captureCamera = new Camera3D { Name = "CaptureCamera", Current = true, Projection = Camera3D.ProjectionType.Orthogonal };
        AddChild(_captureCamera);
        AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-45, -35, 0), LightEnergy = 1.5f });
        AddChild(new WorldEnvironment { Environment = new Godot.Environment {
            BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = CaptureBackground,
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = Colors.White, AmbientLightEnergy = .5f } });
        var model = _player.GetNode<Node3D>("CharacterModel");
        bool realRigVisible = false;
        foreach (Node child in model.FindChildren("*", nameof(MeshInstance3D), true, false))
            if (child is MeshInstance3D mesh && mesh.Mesh != null && mesh.IsVisibleInTree()) realRigVisible = true;
        Require(realRigVisible && model.IsVisibleInTree(), "production rig has no visible mesh for capture");
        _captureMode = true;
    }

    private void TickCapture()
    {
        if (_capturePending) return;
        if (_captureIndex >= CaptureSamples.Length)
        {
            File.WriteAllText(Path.Combine(_captureOutput, "manifest.json"), JsonSerializer.Serialize(new {
                issue = 348, commit = _captureCommit, scene = "res://scenes/Player.tscn",
                harness = "res://tests/integration/PostPresentationTest.tscn",
                source = "local fixed presentation fixtures through actual production rig and AnimationTree",
                verdict = "rendered artifacts for deferred human review; no feel acceptance or transport claim",
                engine = Engine.GetVersionInfo()["string"].AsString(),
                renderer = ProjectSettings.GetSetting("rendering/renderer/rendering_method").AsString(),
                generatedUtc = DateTime.UtcNow.ToString("O"), captures = _captureEntries,
            }, new JsonSerializerOptions { WriteIndented = true }));
            GD.Print($"[post-presentation] PASS capture files={_captureEntries.Count} output={_captureOutput}");
            GetTree().Quit(0); return;
        }
        var sample = CaptureSamples[_captureIndex];
        if (_captureSettle == 0)
        {
            _player.PostSetPresentationForHarness(sample.Post, sample.Post, sample.Hand, sample.Phase);
            _player.PostApplyCaptureHeadingForHarness(sample.Heading);
            string state = sample.Phase == MovePhase.Inactive ? "Dribble" + sample.Hand : "Turnaround" + sample.Phase;
            _playback.Start(state, true);
            _animation.Advance(sample.Seconds);
            Require(_playback.GetCurrentNode().ToString() == state, $"capture failed to enter actual state {state}");
        }
        else _animation.Advance(0);
        // Allow the real SkeletonModifier3D and renderer to settle before capture.
        if (++_captureSettle < 12) return;
        CaptureSample(sample);
    }

    private void FrameCaptureCamera()
    {
        Vector3 Position(string name) => _skeleton.ToGlobal(
            _skeleton.GetBoneGlobalPose(_skeleton.FindBone("mixamorig_" + name)).Origin);
        var head = Position("Head"); var feet = (Position("LeftToeBase") + Position("RightToeBase")) * .5f;
        float height = head.DistanceTo(feet);
        Require(height > .5f && height < 4f, $"rendered rig height invalid: {height}");
        var center = (head + feet) * .5f;
        _captureCamera.Size = height * 1.65f;
        _captureCamera.GlobalPosition = center + new Vector3(height * .8f, height * .15f, height * 2f);
        _captureCamera.LookAt(center);
    }

    private async void CaptureSample(Sample sample)
    {
        _capturePending = true;
        try
        {
            string state = _playback.GetCurrentNode().ToString();
            Require(_player.DisplayMove().phase == sample.Phase && (sample.Phase != MovePhase.Inactive || _player.DisplayPosting == sample.Post),
                $"capture {sample.Name} displayed fixture premise invalid");
            // Read only after the completed draw; this is a real viewport texture.
            // https://docs.godotengine.org/en/4.7/classes/class_viewport.html#class-viewport-method-get-texture
            // https://docs.godotengine.org/en/4.7/classes/class_renderingserver.html#class-renderingserver-signal-frame-post-draw
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            var image = GetViewport().GetTexture().GetImage();
            Require(image != null && !image.IsEmpty() && image.GetWidth() >= 320 && image.GetHeight() >= 180, "capture viewport is empty or too small");
            // A blank background cannot satisfy this. Inspect the actual PNG as well;
            // image diversity establishes content, not telegraph quality.
            var corner = image.GetPixel(0, 0); int foreground = 0;
            float minimum = 1f, maximum = 0f;
            for (int y = 0; y < image.GetHeight(); y += 4)
                for (int x = 0; x < image.GetWidth(); x += 4)
                {
                    var pixel = image.GetPixel(x, y);
                    float difference = Math.Abs(pixel.R-corner.R)+Math.Abs(pixel.G-corner.G)+Math.Abs(pixel.B-corner.B);
                    if (difference > .08f) foreground++;
                    float luma = pixel.R*.2126f+pixel.G*.7152f+pixel.B*.0722f;
                    minimum = Math.Min(minimum,luma); maximum = Math.Max(maximum,luma);
                }
            Require(foreground > 150 && maximum-minimum > .08f, $"capture has no rendered rig content: pixels={foreground} range={maximum-minimum}");
            string filename = sample.Name + ".png";
            Require(image.SavePng(Path.Combine(_captureOutput, filename)) == Error.Ok, "PNG save failed");
            var entry = new {
                file = filename, scene = "res://scenes/Player.tscn", state, phase = _player.DisplayMove().phase.ToString(),
                heading = _player.Heading, hand = _player.HandSide.ToString(), posting = _player.DisplayPosting,
                established = _player.DisplayPostEstablished, animationSeconds = sample.Seconds,
                commit = _captureCommit, tick = _ticks, width = image.GetWidth(), height = image.GetHeight(),
                foregroundSamples = foreground, lumaRange = maximum-minimum,
            };
            File.WriteAllText(Path.Combine(_captureOutput, sample.Name + ".json"), JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true }));
            _captureEntries.Add(entry);
            GD.Print($"[post-presentation] capture={filename} actualState={state} phase={sample.Phase} heading={_player.Heading}");
            _captureIndex++; _captureSettle = 0; _capturePending = false;
        }
        catch (Exception error) { Fail(error); }
    }
}
