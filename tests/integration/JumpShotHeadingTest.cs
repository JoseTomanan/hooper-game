using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;
using Hooper.Player;
using Hooper.Moves;
using Hooper.Ball;

namespace HOOPERGAME.Tests.Integration;

/// <summary>Live production-player normal-shot heading proof and retained trace for #428.
/// Diagnostic mode records the pre-fix trajectory without claiming it passes.</summary>
public partial class JumpShotHeadingTest : Node
{
    private PlayerController _actor, _other;
    private BallController _ball;
    private Skeleton3D _skeleton;
    private string _trace = "", _scenario, _entry, _hand;
    private bool _diagnostic, _finished, _feinted, _illegalTried;
    private int _tick, _beginTick, _releaseTick = -1, _releaseCount, _fakeTick, _inactiveTick, _startupSteps;
    private float _angle, _target, _beginHeading, _previousHeading;
    private Vector3 _plant;
    private float _releaseHeading, _releaseAngle, _fakeHeading;
    private bool _releaseFadeaway, _contactDisplaced;
    private readonly List<object> _samples = new();
    private readonly List<string> _failures = new();
    private readonly List<object> _poses = new();

    public override void _Ready()
    {
        try { Initialize(); }
        catch (Exception error) { _failures.Add(error.Message); Finish(); }
    }

    private void Initialize()
    {
        // Observe after production Player and Ball, including actual release.
        // https://docs.godotengine.org/en/4.7/classes/class_node.html#class-node-property-process-physics-priority
        ProcessPhysicsPriority = 100;
        string[] args = OS.GetCmdlineUserArgs();
        string Read(string key, string fallback) => HarnessArgs.ReadArg(args, key, fallback);
        _scenario = Read("--harness-scenario", "bounded");
        string matrixAngle = null, matrixEntry = null, matrixHand = null;
        // Encode each matrix invocation as a stable executable-catalog scenario;
        // the diagnostic runner can still override the three values explicitly.
        if (_scenario.StartsWith("matrix-"))
        {
            string[] parts = _scenario.Split('-');
            if (parts.Length != 4 || Array.IndexOf(new[] { "0", "45", "89", "90", "91", "135", "180" }, parts[1]) < 0)
                throw new ArgumentException($"invalid encoded matrix scenario {_scenario}");
            matrixAngle = parts[1]; matrixEntry = parts[2];
            matrixHand = parts[3] switch { "left" => "Left", "right" => "Right", _ => parts[3] };
            _scenario = "matrix";
        }
        if (Array.IndexOf(new[] { "bounded", "opposite", "matrix", "unfinished", "feint-early", "feint-late", "illegal-early",
            "illegal-late", "loss", "oob", "missing", "degenerate", "retarget", "contact-retarget", "release-below", "release-at", "release-above" }, _scenario) < 0)
            throw new ArgumentException($"unknown shot-heading scenario {_scenario}");
        _entry = Read("--entry", matrixEntry ?? "stationary"); _hand = Read("--hand", matrixHand ?? "Right");
        if ((_entry != "stationary" && _entry != "moving") || (_hand != "Left" && _hand != "Right"))
            throw new ArgumentException("invalid shot-heading entry or hand");
        string defaultAngle = _scenario switch { "release-below" => "89.9", "release-at" => "90", "release-above" => "90.1", "opposite" => "-135", _ => "180" };
        _angle = float.Parse(Read("--angle", matrixAngle ?? defaultAngle), System.Globalization.CultureInfo.InvariantCulture);
        _diagnostic = Read("--diagnostic", "false") == "true";
        _trace = Read("--proof-trace", "");
        var players = new Node3D { Name = "Players" };
        var scene = GD.Load<PackedScene>("res://scenes/Player.tscn");
        _actor = scene.Instantiate<PlayerController>(); _actor.Name = "1";
        _other = scene.Instantiate<PlayerController>(); _other.Name = "2";
        _other.Position = new Vector3(6, 0, 10);
        players.AddChild(_actor); players.AddChild(_other); AddChild(players);
        _ball = new BallController { Name = "Ball", Players = players, RimCenter = new Vector3(0, 3.05f, 0) };
        if (_scenario != "missing") AddChild(_ball);
        _actor.Position = new Vector3(0, 0, 5);
        _actor.GetNode<AnimationTree>("AnimationTree").CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Physics;
        _skeleton = FindSkeleton(_actor) ?? throw new InvalidOperationException("production final-pose skeleton missing");
        // Final modified pose, rather than a resource key/pre-modifier signal.
        // https://docs.godotengine.org/en/4.7/classes/class_skeleton3d.html#class-skeleton3d-signal-skeleton-updated
        _skeleton.SkeletonUpdated += ObservePose;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        try
        {
            _tick++;
            if (_tick == 5)
            {
                if (_scenario != "missing") Require(_ball.StateMachine.HolderPeerId == 1, "tipoff holder");
                _actor.Position = _scenario == "degenerate" ? Vector3.Zero : new Vector3(0, 0, 5);
                _plant = _actor.Position;
                _target = MathF.Atan2(_ball.RimCenter.X - _actor.Position.X, _ball.RimCenter.Z - _actor.Position.Z);
                _beginHeading = Wrap(_target + _angle * MathF.PI / 180f);
                if (_scenario is "missing" or "degenerate") _target = _beginHeading = .7f;
                _actor.SetHeadingForHarness(_beginHeading);
                _previousHeading = _beginHeading;
                _actor.SetHandSideForHarness(_hand == "Left" ? HandSide.Left : HandSide.Right);
                if (_scenario != "missing") Require(_ball.StateMachine.StartDribble(), "live dribble entry");
                if (_scenario == "unfinished") _actor.MaxTurnRateDeg = 90f;
                if (_scenario.StartsWith("release-")) _actor.MaxTurnRateDeg = 0f;
                _actor.Velocity = _entry == "moving" ? new Vector3(3, 0, 0) : Vector3.Zero;
                _actor.ApplyRequestedMoveForHarness("jumpshot", 0, true);
                Require(_actor.CurrentMoveIdForHarness == "jumpshot", "accepted normal JumpShot");
                if (_scenario == "contact-retarget")
                {
                    // Freeze only the opposing fixture, keeping its production
                    // capsule and raw snapshot available. This selects the actual
                    // IntegrateContactMotion path rather than automatic pair staging.
                    _other.SetPhysicsProcess(false);
                    _other.Position = _plant + new Vector3(.2f, 0, 0);
                    _other.SetHeadingForHarness(-MathF.PI / 2);
                    _other.Velocity = Vector3.Zero;
                }
                _beginTick = _tick;
                _samples.Add(new { kind = "begin", tick = _tick, heading = _actor.Heading, target = _target,
                    instanceTarget = _actor.ShotHeadingTargetForHarness, token = _actor.ShotHeadingTokenForHarness,
                    phase = _actor.PhaseForHarness.ToString(), frame = _actor.FrameInPhaseForHarness,
                    hand = _actor.HandSide.ToString(), speed = _actor.ShotInitiationSpeed,
                    rate = _actor.MaxTurnRateDeg, slow = _actor.BackTurnSlowFactor });
                return;
            }
            if (_beginTick == 0) return;
            float angle = MathF.Abs(Wrap(_target - _actor.Heading));
            float step = MathF.Abs(Wrap(_actor.Heading - _previousHeading));
            bool startup = _actor.PhaseForHarness == MovePhase.Startup;
            Require(float.IsFinite(_actor.Heading), "finite heading");
            if (!_diagnostic && startup)
            {
                _startupSteps++;
                float previousAngle = MathF.Abs(Wrap(_target - _previousHeading));
                if (previousAngle > 1e-4f && _actor.MaxTurnRateDeg > 0f) Require(step > .001f, "Startup must advance toward target");
                Require(step <= _actor.MaxTurnRateDeg / Engine.PhysicsTicksPerSecond * MathF.PI / 180f + 1e-6f,
                    "Startup step exceeds ordinary bound");
                Require(angle <= previousAngle + 1e-6f, "Startup angle grows/overshoots");
                float rate = _actor.MaxTurnRateDeg * (1f - (1f - _actor.BackTurnSlowFactor) * previousAngle / MathF.PI);
                float expectedStep = MathF.Min(previousAngle, rate / Engine.PhysicsTicksPerSecond * MathF.PI / 180f);
                Require(MathF.Abs(expectedStep - step) < 1e-6f, "Startup must use ordinary nonlinear rate exactly once");
                if (_tick == _beginTick + 1 && MathF.Abs(previousAngle - MathF.PI) < 1e-6f && _actor.MaxTurnRateDeg > 0f)
                    Require(Wrap(_actor.Heading - _previousHeading) > 0, "exact half-turn convention must be positive");
                Require(MathF.Abs(Wrap(_actor.ShotHeadingTargetForHarness - _target)) < 1e-6f,
                    "shot instance retargeted after Begin");
            }
            if (!_diagnostic && _actor.PhaseForHarness is MovePhase.Active or MovePhase.Recovery)
                Require(step < 1e-6f, "shot-specific turn continues after Startup");
            if (_scenario is not ("retarget" or "contact-retarget" or "oob" or "loss") && _actor.PhaseForHarness != MovePhase.Inactive)
            {
                Require((_actor.Position - _plant).Length() < 1e-6f, "normal-shot plant translates");
                Require(_actor.Velocity.Length() < 1e-6f, "normal-shot plant retains momentum");
            }
            if (_scenario == "contact-retarget" && _tick == _beginTick + 1)
            {
                Vector3 correction = _actor.ShotHeadingContactCorrectionForHarness;
                Vector3? opponent = _actor.ShotHeadingContactOpponentForHarness;
                Require(opponent.HasValue && opponent.Value.DistanceTo(_other.Position) < 1e-6f,
                    "contact fixture did not supply the real opponent snapshot");
                Require(correction.Length() > .05f && _actor.Position.DistanceTo(_plant) > .05f,
                    "overlap did not cause actual committed contact depenetration");
                Require(_actor.ProductionContactCommittedForHarness, "contact response was not a committed shot tick");
                Require(MathF.Abs(Wrap(MathF.Atan2(-_actor.Position.X, -_actor.Position.Z) - _target)) > .01f,
                    "contact displacement did not change the live rim direction");
                _contactDisplaced = true;
                _samples.Add(new { kind = "contact-depenetration", tick = _tick,
                    correction = new[] { correction.X, correction.Y, correction.Z },
                    displacement = new[] { _actor.Position.X - _plant.X, _actor.Position.Y - _plant.Y, _actor.Position.Z - _plant.Z },
                    fixedTarget = _actor.ShotHeadingTargetForHarness,
                    actualRimDirection = MathF.Atan2(-_actor.Position.X, -_actor.Position.Z) });
            }
            if (_actor.PhaseForHarness == MovePhase.Inactive && _inactiveTick == 0) _inactiveTick = _tick;
            if (_ball.SuccessfulShotReleasesForHarness > _releaseCount)
            {
                _releaseTick = _tick;
                Require(_actor.PhaseForHarness == MovePhase.Active && _actor.FrameInPhaseForHarness == 0,
                    "successful release must be first Active");
                _releaseHeading = _actor.Heading;
                _releaseAngle = ShotFacing.AngleFromTarget(_actor.Heading, _actor.Position, _ball.RimCenter);
                _releaseFadeaway = _actor.DisplayFadeaway();
                if (!_diagnostic)
                {
                    Require(MathF.Abs(_ball.LastFacingFactorForHarness - ShotFacing.Multiplier(_actor.Heading,
                        _actor.Position, _ball.RimCenter, _ball.FacingScatterK)) < 1e-6f,
                        "facing factor must read actual release heading");
                    Require(_releaseFadeaway == (_releaseAngle >= MathF.PI / 2), "fadeaway release-angle boundary");
                    float speed = _entry == "moving" ? 3f : 0f;
                    Require(MathF.Abs(_actor.ShotInitiationSpeed - speed) < 1e-6f, "pre-plant speed sample changed");
                    Require(MathF.Abs(_ball.LastMovementFactorForHarness - ShotMovement.Factor(speed,
                        _actor.MoveSpeed, _actor.SprintSpeed, _ball.MovementScatterK)) < 1e-6f,
                        "movement factor re-sampled planted speed");
                }
            }
            _releaseCount = _ball.SuccessfulShotReleasesForHarness;
            _samples.Add(new { kind = "tick", tick = _tick, heading = _actor.Heading, target = _target,
                instanceTarget = float.IsFinite(_actor.ShotHeadingTargetForHarness) ? _actor.ShotHeadingTargetForHarness : (float?)null,
                token = _actor.ShotHeadingTokenForHarness,
                phase = _actor.PhaseForHarness.ToString(), frame = _actor.FrameInPhaseForHarness,
                position = new[] { _actor.Position.X, _actor.Position.Y, _actor.Position.Z },
                velocity = new[] { _actor.Velocity.X, _actor.Velocity.Y, _actor.Velocity.Z },
                angle, step, fadeaway = _actor.DisplayFadeaway(), releases = _releaseCount,
                facingFactor = _ball.LastFacingFactorForHarness, movementFactor = _ball.LastMovementFactorForHarness,
                move = _actor.CurrentMoveIdForHarness, animation = _actor.ActiveAnimNodeForHarness,
                initiationSpeed = _actor.ShotInitiationSpeed });
            _previousHeading = _actor.Heading;
            Intervene();
            if (_tick - _beginTick >= 43) Finish();
            if (_tick > 100) throw new Exception("timeout");
        }
        catch (Exception error) { _failures.Add(error.Message); Finish(); }
    }

    private void Intervene()
    {
        int frame = _actor.FrameInPhaseForHarness;
        if (_actor.PhaseForHarness != MovePhase.Startup) return;
        if (!_illegalTried && ((_scenario == "illegal-early" && frame == 2) || (_scenario == "illegal-late" && frame == 12)))
        { _illegalTried = true; Require(!_actor.FeintForHarness(), "illegal feint accepted"); }
        if (!_feinted && ((_scenario == "feint-early" && frame == 3) || (_scenario == "feint-late" && frame == 11)))
        {
            _fakeHeading = _actor.Heading; _fakeTick = _tick;
            Require(_actor.FeintForHarness(), "legal pump-fake refused"); _feinted = true;
            Require(MathF.Abs(Wrap(_actor.Heading - _fakeHeading)) < 1e-6f, "feint snapped/rolled back heading");
            Require(MathF.Abs(Wrap(_target - _fakeHeading)) > .001f, "fake fixture must retain partial turn");
            Require(_ball.HasDribbled && _ball.State == BallState.Held, "fake lost dead-dribble cradle cost");
        }
        if (frame == 5)
        {
            if (_scenario == "loss") _ball.PostAwardForHarness(2);
            if (_scenario == "oob") _actor.Position = new Vector3(_ball.CourtMax.X + 2, 0, 5);
            if (_scenario == "retarget")
            { _actor.Position = new Vector3(3, 0, 5); Input.ActionPress("move_right"); }
        }
    }

    private void ObservePose()
    {
        if (_finished || _beginTick == 0) return;
        var bones = new Dictionary<string, object>();
        foreach (string name in new[] { "mixamorig_Hips", "mixamorig_LeftHand", "mixamorig_RightHand", "mixamorig_LeftFoot", "mixamorig_RightFoot" })
        {
            int index = _skeleton.FindBone(name);
            if (index < 0) continue;
            Transform3D pose = _skeleton.GlobalTransform * _skeleton.GetBoneGlobalPose(index);
            var q = pose.Basis.GetRotationQuaternion();
            bones[name] = new { position = new[] { pose.Origin.X, pose.Origin.Y, pose.Origin.Z }, rotation = new[] { q.X, q.Y, q.Z, q.W } };
        }
        _poses.Add(new { tick = _tick, physics = Engine.GetPhysicsFrames(), heading = _actor.Heading,
            phase = _actor.PhaseForHarness.ToString(), frame = _actor.FrameInPhaseForHarness, bones });
    }

    private void Finish()
    {
        if (_finished) return;
        if (!_diagnostic && _failures.Count == 0)
        {
            Check(_poses.Count > 0, "final modified pose trace was never observed");
            bool validShot = _scenario is not ("feint-early" or "feint-late" or "loss" or "oob" or "missing");
            Check(_releaseCount == (validShot ? 1 : 0), "successful release count differs from valid-shot control");
            if (validShot) Check(_releaseTick - _beginTick == JumpShot.DefaultFrameData.StartupFrames, "unchanged first-Active release tick");
            if (_feinted)
            {
                Check(_inactiveTick - _fakeTick == 8, "pump-fake Recovery must cost eight ticks");
                Check(MathF.Abs(Wrap(_actor.Heading - _fakeHeading)) < 1e-6f, "pump-fake retained heading changed");
                Check(_releaseCount == 0 && _ball.HasDribbled, "fake released/erased dead-dribble cost");
            }
            else
            {
                Check(_inactiveTick - _beginTick == 42, "18/4/20 move timeline changed");
                Check(_startupSteps == 17, "Startup integration count changed");
            }
            if (_scenario is "matrix" or "bounded" or "opposite" or "illegal-early" or "illegal-late")
            { Check(_releaseAngle < 1e-5f && !_releaseFadeaway, "default-rate shot must square up and release ordinary"); }
            if (_scenario == "unfinished")
                Check(_releaseAngle > MathF.PI / 2 && _releaseFadeaway, "lower-rate fixture did not prove unfinished fadeaway");
            if (_scenario.StartsWith("release-"))
            {
                Check(MathF.Abs(_releaseAngle - MathF.Abs(_angle) * MathF.PI / 180f) < 1e-6f,
                    "boundary fixture did not reach its stated actual release angle");
                Check(_releaseFadeaway == (_scenario != "release-below"), "below/at/above90 release classification");
            }
            if (_scenario is "illegal-early" or "illegal-late") Check(_illegalTried, "illegal-feint intervention never reached");
            if (_scenario is "feint-early" or "feint-late") Check(_feinted, "legal-feint intervention never reached");
            if (_scenario is "loss" or "oob")
            { Check(_ball.StateMachine.HolderPeerId == 2, "payload-loss fixture did not transfer ownership");
              Check(MathF.Abs(Wrap(_actor.Heading - _target)) < 1e-5f, "loss interrupted remaining Startup turn"); }
            if (_scenario == "contact-retarget")
            {
                Check(_contactDisplaced, "contact displacement premise was not observed");
                Check(MathF.Abs(Wrap(_releaseHeading - _target)) < 1e-5f,
                    "contact displacement retargeted release heading");
            }
        }
        _finished = true;
        var report = new { scenario = _scenario, initialAngle = _angle, entry = _entry, hand = _hand,
            diagnostic = _diagnostic, beginTick = _beginTick, releaseTick = _releaseTick, releases = _releaseCount,
            releaseHeading = _releaseHeading, releaseAngle = _releaseAngle, releaseFadeaway = _releaseFadeaway,
            failures = _failures, samples = _samples, poses = _poses };
        if (_trace != "")
        {
            string path = ProjectSettings.GlobalizePath(_trace);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        GD.Print($"[shot-heading] {(_failures.Count == 0 ? "PASS" : "FAIL")} angle={_angle} entry={_entry} hand={_hand} release={_releaseTick} failures={string.Join("; ", _failures)}");
        Input.ActionRelease("move_right");
        if (_scenario == "missing") _ball.Free();
        GetTree().Quit(_failures.Count == 0 ? 0 : 1);
    }
    private void Check(bool value, string why) { if (!value) _failures.Add(why); }
    private void Require(bool value, string why) { if (!value) throw new Exception(why); }
    private static float Wrap(float x) => x - MathF.Tau * MathF.Floor((x + MathF.PI) / MathF.Tau);
    private static Skeleton3D FindSkeleton(Node node)
    {
        if (node is Skeleton3D skeleton) return skeleton;
        foreach (Node child in node.GetChildren()) { var found = FindSkeleton(child); if (found != null) return found; }
        return null;
    }
}
