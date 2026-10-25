using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Live final-pose proof for #418. Geometry is skeleton-space (including the
// production rig scaler and modifiers), never the unscaled FBX/resource pose.
public partial class JumpshotFormTest : Node
{
    private PlayerController _actor;
    private BallController _ball;
    private Skeleton3D _skeleton;
    private string _scenario;
    private string _trace;
    private string _mutation;
    private int _tick;
    private int _began;
    private int _releaseCount;
    private int _recoveryTicks;
    private int _lastSampleTick = -1;
    private bool _feinted;
    private bool _finished;
    private readonly List<Sample> _samples = new();
    private readonly List<object> _events = new();
    private readonly HashSet<int> _startup = new(), _active = new(), _recovery = new();

    private sealed record Sample(int Tick, string Phase, int Frame, string Node,
        double Elbow, double GuideElbow, double WristHeight, double WristFlexion,
        double Asymmetry, double WristX, double WristY, double WristZ);

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "normal-right");
        _trace = HarnessArgs.ReadArg(args, "--proof-trace", "");
        _mutation = HarnessArgs.ReadArg(args, "--proof-mutation", "");
        if (!new[] { "normal-right", "normal-left", "feint-early", "feint-late", "fadeaway", "diagnose", "control-idle", "control-goalkeeper" }.Contains(_scenario))
            throw new ArgumentException($"unknown jumpshot form scenario {_scenario}");
        if (_scenario.StartsWith("control-")) _mutation = _scenario.Substring("control-".Length);
        var scene = GD.Load<PackedScene>("res://scenes/Player.tscn");
        _actor = scene.Instantiate<PlayerController>(); _actor.Name = "1";
        var other = scene.Instantiate<PlayerController>(); other.Name = "2";
        var players = new Node3D { Name = "Players" };
        foreach (var player in new[] { _actor, other })
        {
            player.GetNode<AnimationTree>("AnimationTree").CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Physics;
            players.AddChild(player);
        }
        _ball = new BallController { Name = "Ball", Players = players };
        AddChild(players); AddChild(_ball);
        // Node.process_physics_priority orders callbacks before tree order.
        // Observe the release AFTER Ball's default-priority physics callback,
        // independently of whether a final skeleton signal is delivered.
        // https://docs.godotengine.org/en/latest/classes/class_node.html#class-node-property-process-physics-priority
        AddChild(new JumpshotFormReleaseObserver { ProcessPhysicsPriority = 100, Observe = ObserveRelease });
        _actor.Position = new Vector3(0, 0, 5); other.Position = new Vector3(12, 0, 12);
        _actor.SetHeadingForHarness(_scenario == "fadeaway" ? 0 : Mathf.Pi);
        // Explicit unfinished-turn control; production 900/.95 squares up.
        if (_scenario == "fadeaway") _actor.MaxTurnRateDeg = 90f;
        _skeleton = FindSkeleton(_actor) ?? throw new InvalidOperationException("production skeleton missing");
        // skeleton_updated is emitted after ALL SkeletonModifier3D processing;
        // pose_updated explicitly excludes modifiers. Never force/recompute a
        // pose here: observe the transform which the skin actually receives.
        // https://docs.godotengine.org/en/latest/classes/class_skeleton3d.html#class-skeleton3d-signal-skeleton-updated
        _skeleton.SkeletonUpdated += CaptureFinalPose;
        ApplyMutation();
    }

    private void ApplyMutation()
    {
        if (_mutation == "") return;
        if (_mutation != "idle" && _mutation != "goalkeeper") throw new ArgumentException($"unknown mutation {_mutation}");
        var player = _actor.GetNode<AnimationPlayer>("AnimationPlayer");
        // Duplicate the library so mutation does not change a shared/on-disk
        // resource. Official Resource.duplicate(true) duplicates subresources.
        // https://docs.godotengine.org/en/latest/classes/class_resource.html#class-resource-method-duplicate
        var library = (AnimationLibrary)player.GetAnimationLibrary("locomotion").Duplicate(true);
        Animation idle = library.GetAnimation("idle");
        var baseline = _mutation == "goalkeeper" ? GD.Load<AnimationLibrary>("res://tests/fixtures/jumpshot_goalkeeper.res") : null;
        foreach (string name in new[] { "jumpshotstartup", "jumpshotactive", "jumpshotrecovery" })
        { library.RemoveAnimation(name); library.AddAnimation(name, baseline == null ? idle : baseline.GetAnimation(name)); }
        player.RemoveAnimationLibrary("locomotion"); player.AddAnimationLibrary("locomotion", library);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _tick++;
        if (_tick == 12)
        {
            Require(_ball.StateMachine.HolderPeerId == 1, "tipoff holder changed");
            _actor.SetHandSideForHarness(_scenario == "normal-left" ? HandSide.Left : HandSide.Right);
            Require(_actor.BeginJumpShotForHarness(), "shot did not begin");
            Require(_actor.CurrentMoveIdForHarness == "jumpshot", "starting hand selected a different move");
            _events.Add(new { tick = _tick, kind = "begin", hand = _actor.HandSide.ToString(), move = _actor.CurrentMoveIdForHarness });
            _began = _tick;
        }
        if (_began == 0) return;
        if (!_feinted && _scenario.StartsWith("feint-") && _actor.PhaseForHarness == MovePhase.Startup
            && _actor.FrameInPhaseForHarness == (_scenario == "feint-early" ? 3 : 11))
        {
            _events.Add(new { tick = _tick, kind = "feint", frame = _actor.FrameInPhaseForHarness });
            Require(_actor.FeintForHarness(), "legal feint rejected"); _feinted = true;
        }
        if (_actor.PhaseForHarness == MovePhase.Startup) _startup.Add(_actor.FrameInPhaseForHarness);
        if (_actor.PhaseForHarness == MovePhase.Active) _active.Add(_actor.FrameInPhaseForHarness);
        if (_actor.PhaseForHarness == MovePhase.Recovery)
        { _recoveryTicks++; _recovery.Add(_actor.FrameInPhaseForHarness); }
        if (_tick - _began >= 65) Finish();
        if (_tick > 300) Require(false, "timeout");
    }

    private void CaptureFinalPose()
    {
        if (_finished || _began == 0 || _lastSampleTick == _tick) return;
        _lastSampleTick = _tick;
        CaptureGeometry();
    }

    private void ObserveRelease()
    {
        if (_finished || _began == 0) return;
        // The event is observed even if the displayed pose still contains
        // Startup's tail. Retain that prior final pose for seam diagnosis.
        if (_ball.SuccessfulShotReleasesForHarness != _releaseCount)
        {
            _releaseCount = _ball.SuccessfulShotReleasesForHarness;
            Vector3 p = _ball.LastReleasePositionForHarness;
            Require(_actor.PhaseForHarness == MovePhase.Active && _actor.FrameInPhaseForHarness == 0,
                "release did not occur at first Active tick");
            Require(p.IsFinite(), "nonfinite release event position");
            float releaseAngle = ShotFacing.AngleFromTarget(_actor.Heading, _actor.GlobalPosition, _ball.RimCenter);
            Require(_scenario == "fadeaway" ? releaseAngle >= Mathf.Pi / 2 : releaseAngle < 1e-4f,
                $"actual release-angle premise changed: {releaseAngle}");
            Require(_actor.DisplayFadeaway() == (_scenario == "fadeaway"), "release classification mismatch");
            _events.Add(new { tick = _tick, kind = "release", count = _releaseCount,
                phase = _actor.PhaseForHarness.ToString(), frame = _actor.FrameInPhaseForHarness,
                position = new[] { p.X, p.Y, p.Z }, previousFinalPose = _samples.LastOrDefault() });
        }
    }

    private void CaptureGeometry()
    {
        Vector3 shoulder = Bone("RightArm"), elbow = Bone("RightForeArm"), wrist = Bone("RightHand");
        Vector3 guideShoulder = Bone("LeftArm"), guideElbow = Bone("LeftForeArm"), guideWrist = Bone("LeftHand");
        Vector3 up = (Bone("Neck") - Bone("Spine")).Normalized();
        // Anatomical forward comes from the feet, never an assumed FBX axis
        // or an arbitrary cross-product handedness.
        Vector3 forward = ((Bone("RightToeBase") - Bone("RightFoot"))
            + (Bone("LeftToeBase") - Bone("LeftFoot"))).Slide(up).Normalized();
        Vector3 forearm = (wrist - elbow).Normalized();
        Vector3 hand = (Bone("RightHandMiddle1") - wrist).Normalized();
        double shootingAngle = Mathf.RadToDeg((shoulder - elbow).AngleTo(wrist - elbow));
        double guideAngle = Mathf.RadToDeg((guideShoulder - guideElbow).AngleTo(guideWrist - guideElbow));
        // Signed sagittal angle, positive from up toward anatomical forward.
        double flexion = Mathf.RadToDeg(Mathf.Wrap(Mathf.Atan2(hand.Dot(forward), hand.Dot(up))
            - Mathf.Atan2(forearm.Dot(forward), forearm.Dot(up)), -Mathf.Pi, Mathf.Pi));
        var sample = new Sample(_tick, _actor.PhaseForHarness.ToString(), _actor.FrameInPhaseForHarness,
            _actor.ActiveAnimNodeForHarness, shootingAngle, guideAngle, (wrist-elbow).Dot(up),
            flexion, shootingAngle-guideAngle, wrist.X, wrist.Y, wrist.Z);
        Require(new[] { sample.Elbow, sample.GuideElbow, sample.WristHeight, sample.WristFlexion, sample.Asymmetry,
            sample.WristX, sample.WristY, sample.WristZ }.All(double.IsFinite), "nonfinite final pose");
        _samples.Add(sample);
    }

    private Vector3 Bone(string name)
    {
        int bone = _skeleton.FindBone("mixamorig_" + name);
        if (bone < 0) throw new InvalidOperationException($"bone missing: {name}");
        return _skeleton.GetBoneGlobalPose(bone).Origin;
    }

    private static Skeleton3D FindSkeleton(Node node)
    {
        if (node is Skeleton3D skeleton) return skeleton;
        foreach (Node child in node.GetChildren()) { var found = FindSkeleton(child); if (found != null) return found; }
        return null;
    }

    private void Finish()
    {
        // Always preserve diagnostics, including red proofs and mutations.
        if (_trace != "") System.IO.File.WriteAllText(ProjectSettings.GlobalizePath(_trace),
            JsonSerializer.Serialize(new { scenario = _scenario, mutation = _mutation, events = _events, samples = _samples },
                new JsonSerializerOptions { WriteIndented = true }));
        bool feint = _scenario.StartsWith("feint-");
        Require(_samples.Count >= 20, "final-pose coverage missing");
        Require(_actor.PhaseForHarness == MovePhase.Inactive, "did not return neutral");
        Require(_releaseCount == (feint ? 0 : 1), "release count changed");
        Require(_recoveryTicks == (feint ? 8 : 20), $"recovery duration {_recoveryTicks}");
        Require(feint ? _feinted && _active.Count == 0 : _startup.Count == 18 && _active.Count == 4,
            $"phase coverage startup={_startup.Count} active={_active.Count}");
        if (!feint && _scenario != "fadeaway")
        {
            List<string> failures = FormFailures();
            GD.Print($"[jumpshot-form] form failures=[{string.Join("; ", failures)}]");
            if (_scenario == "control-idle")
                Require(failures.Contains("release wrist not above elbow"), "idle mutation escaped its intended wrist-height detector");
            else if (_scenario == "control-goalkeeper")
                Require(failures.Any(s => s.StartsWith("release not one-handed"))
                    && failures.Any(s => s.StartsWith("followthrough elbow")), "goalkeeper mutation escaped its two original defects");
            else if (_scenario != "diagnose") Require(failures.Count == 0, string.Join("; ", failures));
        }
        if (_scenario == "diagnose")
            foreach (var group in _samples.GroupBy(s => s.Phase))
                GD.Print($"[jumpshot-form] {_scenario} {group.Key}: " + JsonSerializer.Serialize(group));
        _finished = true;
        GD.Print($"[jumpshot-form] PASS {_scenario} releaseCount={_releaseCount} recoveryTicks={_recoveryTicks}");
        GetTree().Quit(0);
    }

    private List<string> FormFailures()
    {
        var failures = new List<string>();
        Sample[] gather = _samples.Where(s => s.Phase == "Startup" && s.Frame >= 8 && s.Frame <= 11).ToArray();
        Sample[] release = _samples.Where(s => s.Phase == "Active" && s.Frame >= 1 && s.Frame <= 3).ToArray();
        Sample[] follow = _samples.Where(s => s.Phase == "Recovery" && s.Frame >= 0 && s.Frame <= 3).ToArray();
        Require(gather.Length == 4 && release.Length == 3 && follow.Length == 4,
            $"form sample coverage gather={gather.Length} release={release.Length} follow={follow.Length}");
        // Frozen before authoring: three identical production baseline runs
        // measured 0.0 inter-run delta for every metric. Accordingly these are
        // strict DIRECTIONAL contracts with zero noise, not target pose angles.
        double loadedMax = gather.Max(s => s.Elbow);
        double releaseMean = release.Average(s => s.Elbow);
        double flexionChange = follow.Average(s => s.WristFlexion) - gather.Average(s => s.WristFlexion);
        void Gate(bool pass, string message) { if (!pass) failures.Add(message); }
        Gate(loadedMax < 180, $"loaded elbow not bent: {loadedMax:F4}");
        Gate(releaseMean > loadedMax, $"release elbow {releaseMean:F4} <= loaded max {loadedMax:F4}");
        Gate(release.Min(s => s.WristHeight) > 0, "release wrist not above elbow");
        Gate(release.Min(s => s.Asymmetry) > 0, $"release not one-handed: min asym={release.Min(s => s.Asymmetry):F4}");
        Gate(flexionChange > 0, $"wrist did not flex toward anatomical forward/down: change={flexionChange:F4}");
        Gate(follow.Min(s => s.Elbow) > loadedMax, $"followthrough elbow {follow.Min(s => s.Elbow):F4} <= loaded max {loadedMax:F4}");
        Gate(follow.Min(s => s.Asymmetry) > 0, $"followthrough not one-handed: min asym={follow.Min(s => s.Asymmetry):F4}");
        GD.Print($"[jumpshot-form] gatherMax={loadedMax:F4} releaseMean={releaseMean:F4} " +
            $"releaseMinHeight={release.Min(s => s.WristHeight):F4} releaseMinAsym={release.Min(s => s.Asymmetry):F4} " +
            $"flexionChange={flexionChange:F4} followMinElbow={follow.Min(s => s.Elbow):F4} followMinAsym={follow.Min(s => s.Asymmetry):F4}");
        return failures;
    }

    private void Require(bool pass, string message)
    {
        if (pass) return;
        _finished = true; GD.PrintErr($"[jumpshot-form] FAIL {_scenario}: {message}"); GetTree().Quit(1);
        throw new InvalidOperationException(message);
    }
}

public partial class JumpshotFormReleaseObserver : Node
{
    public Action Observe;
    public override void _PhysicsProcess(double delta) => Observe?.Invoke();
}
