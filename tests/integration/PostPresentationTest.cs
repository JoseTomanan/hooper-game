using System;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

/// <summary>Production rig/graph proof; provisional visual magnitudes never sign off feel.</summary>
public partial class PostPresentationTest : Node3D
{
    private PlayerController _player;
    private Skeleton3D _skeleton;
    private AnimationTree _animation;
    private AnimationNodeStateMachinePlayback _playback;
    private PostPose _modifier;
    private ENetMultiplayerPeer _peer;
    private Pose _current, _neutral, _protect, _post, _committed;
    private int _ticks, _observations, _stage;
    private bool _remote;
    private Vector3 _origin;
    private record Pose(Vector3 Hips, Quaternion LeftArm, Quaternion RightArm,
        Quaternion LeftForeArm, Quaternion RightForeArm, Quaternion LeftThigh, Quaternion RightThigh,
        Quaternion LeftKnee, Quaternion RightKnee);
    private HandSide Hand => _stage < 14 ? HandSide.Right : HandSide.Left;

    public override void _Ready()
    {
        try
        {
            var players = new Node3D { Name = "Players" }; AddChild(players);
            _player = ResourceLoader.Load<PackedScene>("res://scenes/Player.tscn").Instantiate<PlayerController>();
            _player.Name = "1"; players.AddChild(_player);
            var ball = new BallController { Name = "Ball", Players = players, RimCenter = new Vector3(0, 3.05f, 0) }; AddChild(ball);
            ball.SetPhysicsProcess(false); ball.StateMachine.ForceState(BallState.Dribbling, 1);
            _player.SprintBindBallForHarness(ball);
            _player.ResetContactStateForHarness(new Vector3(0, 0, 10), 0, Vector3.Zero);
            _player.SetPhysicsProcess(false); _player.SetProcess(false);
            _origin = _player.GlobalPosition;
            _animation = _player.GetNode<AnimationTree>("AnimationTree");
            _animation.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Manual;
            _playback = _animation.Get("parameters/playback").As<AnimationNodeStateMachinePlayback>();
            _skeleton = (Skeleton3D)_player.FindChildren("*", nameof(Skeleton3D), true, false)[0];
            _modifier = _skeleton.GetNodeOrNull<PostPose>("PostPose");
            Require(_modifier != null, "shipped player missing PostPose");
            _modifier.ModificationProcessed += Observe;
            CheckClipsAndGraph();
            if (HarnessArgs.ReadArg(OS.GetCmdlineUserArgs(), "--harness-scenario", "presentation") == "capture")
                SetupCapture();
            Configure(false);
        }
        catch (Exception error) { Fail(error); }
    }

    private void Observe()
    {
        // Observe before Godot restores the underlying authored animation pose.
        // https://docs.godotengine.org/en/4.7/classes/class_skeletonmodifier3d.html#class-skeletonmodifier3d-signal-modification-processed
        Quaternion Rotation(string name) => _skeleton.GetBonePoseRotation(_skeleton.FindBone("mixamorig_" + name));
        _current = new Pose(_skeleton.GetBonePosePosition(_skeleton.FindBone("mixamorig_Hips")),
            Rotation("LeftArm"), Rotation("RightArm"), Rotation("LeftForeArm"), Rotation("RightForeArm"),
            Rotation("LeftUpLeg"), Rotation("RightUpLeg"), Rotation("LeftLeg"), Rotation("RightLeg"));
        _observations++;
        if (_captureMode) FrameCaptureCamera();
    }

    public override void _PhysicsProcess(double delta)
    {
        try
        {
            _ticks++;
            if (_captureMode) { TickCapture(); return; }
            _animation.Advance(0); // Paired controls see exactly the same authored animation time.
            if (_ticks % 4 != 0) return;
            Require(_observations >= _ticks - 3 && _current != null, "actual post modifier did not process");
            int step = _stage % 14;
            switch (step)
            {
                case 0: _neutral = _current; Configure(false, protect: true); break;
                case 1: _protect = _current; Configure(true); break;
                case 2:
                    Require(_player.DisplayPosting && _player.DisplayMove().phase == MovePhase.Inactive, "post display premise invalid");
                    _post = _current; AssertDistinct(_post, _neutral, "neutral"); AssertDistinct(_post, _protect, "protection");
                    break;
                case 3: Require(Same(_post, _current), "post pose accumulated across authored frames"); Configure(false); break;
                case 4: Require(Same(_neutral, _current), "post release did not restore authored pose"); Configure(true, pivot: true); break;
                case 5:
                    Require(_player.IsPivotingInPlace && Same(_neutral, _current), "post overwrote ordinary pivot pose");
                    Configure(true, phase: MovePhase.Startup); _modifier.Influence = 0; break;
                case 6: _committed = _current; _modifier.Influence = 1; break;
                case 7:
                    Require(Same(_committed, _current), "post overwrote actual turnaround startup pose");
                    Configure(true, phase: MovePhase.Active); _modifier.Influence = 0; break;
                case 8: _committed = _current; _modifier.Influence = 1; break;
                case 9:
                    Require(Same(_committed, _current), "post overwrote actual turnaround release pose");
                    Configure(true, phase: MovePhase.Recovery); _modifier.Influence = 0; break;
                case 10: _committed = _current; _modifier.Influence = 1; break;
                case 11: Require(Same(_committed, _current), "post overwrote actual turnaround recovery pose"); Configure(true); break;
                case 12: Require(Same(_post, _current), "post failed to resume after commitment"); break;
                case 13:
                    Require(_player.GlobalPosition.DistanceTo(_origin) < .0001f && _player.Velocity.IsZeroApprox(), "cosmetic post moved capsule");
                    break;
            }
            _stage++;
            if (_stage == 14) Configure(false);
            if (_stage == 28 && !_remote)
            {
                _peer = new ENetMultiplayerPeer();
                Require(_peer.CreateClient("127.0.0.1", 28448) == Error.Ok, "remote display role setup failed");
                GetTree().SetMultiplayer(new SceneMultiplayer { MultiplayerPeer = _peer }, _player.GetParent().GetPath());
                Require(!_player.Multiplayer.IsServer() && _player.Name != _player.Multiplayer.GetUniqueId().ToString(), "remote role premise invalid");
                _remote = true; _stage = 0; Configure(false);
            }
            else if (_stage == 28)
            { GD.Print("[post-presentation] PASS presentation"); GetTree().Quit(0); }
        }
        catch (Exception error) { Fail(error); }
    }

    private void Configure(bool posting, MovePhase phase = MovePhase.Inactive, bool pivot = false, bool protect = false)
    {
        if (_remote) _player.PostSetRemotePresentationForHarness(posting, Hand, phase, pivot);
        else _player.PostSetPresentationForHarness(posting, posting, Hand, phase, pivot, protect);
        // Protection's paired control is local only; the remote pass compares post to neutral.
        if (_remote && protect) _skeleton.GetNode<ProtectionPose>("ProtectionPose").Influence = 0;
        string state = phase == MovePhase.Inactive ? "Dribble" + Hand : "Turnaround" + phase;
        _playback.Start(state, true); _animation.Advance(0);
        Require(_playback.GetCurrentNode().ToString() == state, $"actual graph failed to enter {state}");
    }

    private void AssertDistinct(Pose post, Pose control, string name)
    {
        Require(post.Hips.Y < control.Hips.Y - .02f, $"{Hand} post hips not lower than {name}: delta={post.Hips.Y-control.Hips.Y}");
        bool right = Hand == HandSide.Right;
        Require(Angle(right ? post.LeftArm : post.RightArm, right ? control.LeftArm : control.RightArm) >= .1f
            && Angle(right ? post.LeftForeArm : post.RightForeArm, right ? control.LeftForeArm : control.RightForeArm) >= .1f,
            $"{Hand} post off-arm not distinct from {name}");
        Require(Angle(post.LeftThigh, control.LeftThigh) >= .1f && Angle(post.RightThigh, control.RightThigh) >= .1f
            && Angle(post.LeftKnee, control.LeftKnee) >= .1f && Angle(post.RightKnee, control.RightKnee) >= .1f,
            $"{Hand} post must flex both legs relative to {name}");
    }

    private void CheckClipsAndGraph()
    {
        var graph = (AnimationNodeStateMachine)_animation.TreeRoot;
        var animationPlayer = _player.GetNode<AnimationPlayer>("AnimationPlayer");
        string[] bones = { "Hips", "Spine", "Spine1", "Spine2", "Neck", "Head",
            "LeftShoulder", "RightShoulder", "LeftArm", "RightArm", "LeftForeArm", "RightForeArm",
            "LeftHand", "RightHand", "LeftUpLeg", "RightUpLeg", "LeftLeg", "RightLeg",
            "LeftFoot", "RightFoot", "LeftToeBase", "RightToeBase" };
        var phases = new[] { MovePhase.Startup, MovePhase.Active, MovePhase.Recovery };
        var frameData = new TurnaroundShot(0).FrameData;
        int[] ticks = { frameData.StartupFrames, frameData.ActiveFrames, frameData.RecoveryFrames };
        for (int i = 0; i < phases.Length; i++)
        {
            string state = "Turnaround" + phases[i];
            Require(graph.HasNode(state), $"missing state {state}");
            var node = graph.GetNode(state) as AnimationNodeAnimation;
            Require(node != null && node.Animation.ToString() == "turnaround/turnaround" + phases[i].ToString().ToLowerInvariant(), "turnaround state reuses wrong identity");
            var clip = animationPlayer.GetAnimation(node.Animation);
            Require(Math.Abs(clip.Length - ticks[i] / 60.0) <= .001 && clip.LoopMode == Animation.LoopModeEnum.None, $"{state} frame window mismatch");
            // The established shot segment's manifest covers all 52 authored rotations;
            // the explicit material list below also catches source-library coverage drift.
            var source = animationPlayer.GetAnimation("locomotion/jumpshot" + phases[i].ToString().ToLowerInvariant());
            for (int track = 0; track < source.GetTrackCount(); track++)
            {
                if (source.TrackGetType(track) != Animation.TrackType.Rotation3D) continue;
                int corresponding = clip.FindTrack(source.TrackGetPath(track), Animation.TrackType.Rotation3D);
                Require(corresponding >= 0 && clip.TrackGetKeyCount(corresponding) > 0,
                    $"{state} missing authored rotation {source.TrackGetPath(track)}");
                foreach (double fraction in new[] { 0.0, .5, 1.0 })
                {
                    double time = source.Length * fraction;
                    var actual = clip.RotationTrackInterpolate(corresponding, time + (i == 0 ? .5 : 0));
                    Require(actual.IsFinite() && Angle(actual, source.RotationTrackInterpolate(track, time)) < .0001f,
                        $"{state} stopped preserving established shot arc: {source.TrackGetPath(track)}");
                }
            }
            foreach (string bone in bones)
            {
                bool tracked = false;
                for (int track = 0; track < clip.GetTrackCount(); track++)
                {
                    if (clip.TrackGetType(track) != Animation.TrackType.Rotation3D) continue;
                    var path = clip.TrackGetPath(track);
                    if (path.GetSubNameCount() == 0 || path.GetSubName(0) != "mixamorig_" + bone) continue;
                    Require(clip.TrackGetKeyCount(track) > 0 && _skeleton.FindBone("mixamorig_" + bone) >= 0, $"{state} empty/unresolved {bone}");
                    tracked = true;
                }
                Require(tracked, $"{state} rest/T-pose material gap: {bone}");
            }
            Require(MoveAnimResolver.ResolveStateName(MoveAnimResolver.Resolve(phases[i]), "turnaround", HandSide.Right, HandSide.Right) == state,
                $"resolver aliases {state}");
        }
        Require(graph.HasTransition("DribbleLeft", "TurnaroundStartup") && graph.HasTransition("DribbleRight", "TurnaroundStartup")
            && graph.HasTransition("TurnaroundStartup", "TurnaroundActive") && graph.HasTransition("TurnaroundActive", "TurnaroundRecovery"), "turnaround travel graph incomplete");
    }

    // Component-space distance avoids acos float noise near identical quaternions.
    private static float Angle(Quaternion a, Quaternion b)
    {
        // Imported animation keys have small float norm error. Normalize the measured
        // rotations before taking an inverse, whose Godot contract requires unit input.
        var difference = a.Normalized().Inverse() * b.Normalized();
        return 2f * Mathf.Atan2(new Vector3(difference.X, difference.Y, difference.Z).Length(), Mathf.Abs(difference.W));
    }
    private static bool Same(Pose a, Pose b) => a.Hips.DistanceTo(b.Hips) < .0001f
        && Angle(a.LeftArm,b.LeftArm)<.0001f && Angle(a.RightArm,b.RightArm)<.0001f
        && Angle(a.LeftForeArm,b.LeftForeArm)<.0001f && Angle(a.RightForeArm,b.RightForeArm)<.0001f
        && Angle(a.LeftThigh,b.LeftThigh)<.0001f && Angle(a.RightThigh,b.RightThigh)<.0001f
        && Angle(a.LeftKnee,b.LeftKnee)<.0001f && Angle(a.RightKnee,b.RightKnee)<.0001f;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private void Fail(Exception error) { GD.Print($"[post-presentation] FAIL: {error}"); GetTree().Quit(1); }
    public override void _ExitTree() => _peer?.Close();
}
