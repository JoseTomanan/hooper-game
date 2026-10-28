using System;
using Godot;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Deliberate sampled-timeline discontinuity, not a network/gameplay proof.
// The control enters the identical Recovery pose without a preceding Startup.
// The continuous-fake sibling proves the setup really can retain that anchor.
public partial class JumpshotPoseResetTest : Node
{
    private const float MatchToleranceM = 1e-5f;
    private const float DistinctFakeFloorM = .1f;
    private PlayerController _actor, _control;
    private Skeleton3D _actorSkeleton, _controlSkeleton;
    private JumpshotPose _actorModifier, _controlModifier;
    private AnimationTree _actorTree, _controlTree;
    private PoseSample _actorSample, _controlSample;
    private string _scenario, _mutation;
    private int _stage, _ticks;
    private bool _finished;

    private sealed record PoseSample(ulong PhysicsFrame, MovePhase Phase, int Frame,
        long SourceTick, bool Applied, Vector3[] Positions);

    public override void _Ready()
    {
        try
        {
            string[] args = OS.GetCmdlineUserArgs();
            _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "gap");
            _mutation = HarnessArgs.ReadArg(args, "--proof-mutation", "");
            Require(_scenario is "gap" or "backward" or "continuous-fake", "unknown scenario");
            Require(_mutation == "" || (_mutation == "keep-anchor" && _scenario == "gap"), "unsupported mutation/scenario");
            var scene = GD.Load<PackedScene>("res://scenes/Player.tscn");
            _actor = scene.Instantiate<PlayerController>(); _actor.Name = "1";
            _control = scene.Instantiate<PlayerController>(); _control.Name = "2";
            var players = new Node3D { Name = "Players" };
            players.AddChild(_actor); players.AddChild(_control); AddChild(players);
            foreach (var player in new[] { _actor, _control })
            {
                // Keep production rigs and modifiers alive, while the fixture
                // exclusively supplies sampled presentation and animation clocks.
                player.SetPhysicsProcess(false); player.SetProcess(false);
                player.GetNode<AnimationTree>("AnimationTree").CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Manual;
            }
            _actorTree = _actor.GetNode<AnimationTree>("AnimationTree");
            _controlTree = _control.GetNode<AnimationTree>("AnimationTree");
            _actorSkeleton = FindSkeleton(_actor) ?? throw new InvalidOperationException("actor skeleton missing");
            _controlSkeleton = FindSkeleton(_control) ?? throw new InvalidOperationException("control skeleton missing");
            _actorModifier = _actorSkeleton.GetNode<JumpshotPose>("JumpshotPose");
            _controlModifier = _controlSkeleton.GetNode<JumpshotPose>("JumpshotPose");
            Require(_actorSkeleton.GetBoneCount() == _controlSkeleton.GetBoneCount(), "rig bone counts differ");
            // Observe only skeleton_updated: mixer/base poses cannot prove what
            // the skin received after the modifier restored or rejected an anchor.
            // https://docs.godotengine.org/en/4.7/classes/class_skeleton3d.html#signals
            _actorSkeleton.SkeletonUpdated += OnActorUpdated;
            _controlSkeleton.SkeletonUpdated += OnControlUpdated;
            GD.Print($"[jumpshot-pose-reset] scenario={_scenario} mutation={_mutation} booting headless...");
        }
        catch (Exception error) { Finish(error.Message); }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        try
        {
            Require(++_ticks <= 180, "timed out waiting for final SkeletonUpdated poses");
            if (_stage == 0)
            {
                _stage = 1;
                _actor.SetJumpshotPoseDisplayForHarness(MovePhase.Startup, 3, 100);
                _control.SetJumpshotPoseDisplayForHarness(MovePhase.Inactive, 0, 100);
                StartAndEvaluate(_actorTree, "JumpshotStartup", 3.0 / 60.0);
                StartAndEvaluate(_controlTree, "Locomotion", 3.0 / 60.0);
                return;
            }
            if (_actorSample == null || _controlSample == null) return;
            Require(_actorSample.PhysicsFrame == _controlSample.PhysicsFrame, "rig observations did not co-occur");
            if (_stage == 1)
            {
                Require(_actorSample.Phase == MovePhase.Startup && _actorSample.Frame == 3 &&
                        _actorSample.SourceTick == 100 && _actorSample.Applied,
                        "prior final Startup/3 anchor was not exercised");
                Require(_controlSample.Phase == MovePhase.Inactive && !_controlSample.Applied,
                        "control inherited a shot anchor");
                long next = _scenario == "gap" ? 132 : _scenario == "backward" ? 99 : 101;
                _actorSample = _controlSample = null;
                _stage = 2;
                _actor.SetJumpshotPoseDisplayForHarness(MovePhase.Recovery, 13, next);
                _control.SetJumpshotPoseDisplayForHarness(MovePhase.Recovery, 13, next);
                if (_mutation == "keep-anchor") _actorModifier.PretendAdjacentJumpshotSourceTickForHarness(next);
                foreach (var tree in new[] { _actorTree, _controlTree })
                {
                    tree.Set("parameters/JumpshotRecovery/RecoverySeek/seek_request", 13.0 / 60.0);
                    tree.Set("parameters/JumpshotRecovery/ReturnBlend/blend_amount", 0.0);
                    StartAndEvaluate(tree, "JumpshotRecovery", 0);
                }
                return;
            }
            Require(_actorSample.Phase == MovePhase.Recovery && _actorSample.Frame == 13 &&
                    _controlSample.Phase == MovePhase.Recovery && _controlSample.Frame == 13 &&
                    _actorSample.SourceTick == _controlSample.SourceTick,
                    "Recovery comparison did not use identical sampled timelines");
            Require(!_controlSample.Applied, "direct-Recovery control applied a fake bridge");
            float largest = 0;
            string largestBone = "";
            for (int i = 0; i < _actorSample.Positions.Length; i++)
            {
                float distance = _actorSample.Positions[i].DistanceTo(_controlSample.Positions[i]);
                if (distance > largest) { largest = distance; largestBone = _actorSkeleton.GetBoneName(i); }
            }
            GD.Print($"[jumpshot-pose-reset] source={_actorSample.SourceTick} actorApplied={_actorSample.Applied} controlApplied={_controlSample.Applied} maxFinalJointDistanceM={largest:F8} bone={largestBone}");
            if (_scenario == "continuous-fake")
                Require(_actorSample.Applied && largest > DistinctFakeFloorM,
                        "continuous-fake control did not retain the distinct prior Startup pose");
            else
                Require(!_actorSample.Applied && largest <= MatchToleranceM,
                        "discontinuous source clock retained stale Startup anchor in final Recovery pose");
            Finish("");
        }
        catch (Exception error) { Finish(error.Message); }
    }

    private static void StartAndEvaluate(AnimationTree tree, string state, double elapsed)
    {
        var playback = tree.Get("parameters/playback").As<AnimationNodeStateMachinePlayback>();
        playback.Start(state, true);
        tree.Advance(elapsed);
    }

    private void OnActorUpdated()
    {
        if (!_finished && _stage is 1 or 2 && _actorSample == null)
            _actorSample = Capture(_actor, _actorSkeleton, _actorModifier);
    }

    private void OnControlUpdated()
    {
        if (!_finished && _stage is 1 or 2 && _controlSample == null)
            _controlSample = Capture(_control, _controlSkeleton, _controlModifier);
    }

    private static PoseSample Capture(PlayerController player, Skeleton3D skeleton, JumpshotPose modifier)
    {
        var positions = new Vector3[skeleton.GetBoneCount()];
        for (int i = 0; i < positions.Length; i++)
        {
            positions[i] = skeleton.GetBoneGlobalPose(i).Origin;
            Require(float.IsFinite(positions[i].X) && float.IsFinite(positions[i].Y) && float.IsFinite(positions[i].Z),
                    "nonfinite final skeleton pose");
        }
        var display = player.AnimationDisplaySnapshot;
        return new PoseSample(Engine.GetPhysicsFrames(), display.Phase, display.FrameInPhase,
                              display.PresentationTick, modifier.AppliedForHarness, positions);
    }

    private static Skeleton3D FindSkeleton(Node node)
    {
        if (node is Skeleton3D skeleton) return skeleton;
        foreach (Node child in node.GetChildren())
        {
            var found = FindSkeleton(child);
            if (found != null) return found;
        }
        return null;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private void Finish(string failure)
    {
        if (_finished) return;
        _finished = true;
        GD.Print($"[jumpshot-pose-reset] {(failure == "" ? "PASS" : "FAIL")} scenario={_scenario} mutation={_mutation} {failure}");
        GetTree().Quit(failure == "" ? 0 : 1);
    }

    public override void _ExitTree()
    {
        if (GodotObject.IsInstanceValid(_actorSkeleton)) _actorSkeleton.SkeletonUpdated -= OnActorUpdated;
        if (GodotObject.IsInstanceValid(_controlSkeleton)) _controlSkeleton.SkeletonUpdated -= OnControlUpdated;
    }
}
