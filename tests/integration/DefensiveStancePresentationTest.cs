using System;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

/// <summary>Observes the shipped rig after the actual modifier has processed.</summary>
public partial class DefensiveStancePresentationTest : Node3D
{
    private PlayerController _defender;
    private Skeleton3D _skeleton;
    private AnimationTree _animation;
    private int _ticks, _observations;
    private Pose _current, _neutral, _stance;
    private Vector3 _origin;
    private Camera3D _camera;
    private string _captureOutput;
    private bool _captureProduction;
    private ENetMultiplayerPeer _remoteRolePeer;
    private readonly CommittedMove[] _moves = { new StealMove(HandSide.Right, 1f), new BlockMove(), new ContestMove() };
    private record Pose(Vector3 Hips, Vector3 Head, Vector3 LeftFoot, Vector3 RightFoot, Vector3 LeftHand, Vector3 RightHand,
        Quaternion LeftArm, Quaternion RightArm, Quaternion LeftKnee, Quaternion RightKnee);

    public override void _Ready()
    {
        try
        {
            var players = new Node3D { Name = "Players" }; AddChild(players);
            var scene = ResourceLoader.Load<PackedScene>("res://scenes/Player.tscn");
            _defender = scene.Instantiate<PlayerController>(); _defender.Name = "1"; players.AddChild(_defender);
            var holder = scene.Instantiate<PlayerController>(); holder.Name = "2";
            holder.Position = new Vector3(4, 0, 0); players.AddChild(holder);
            var ball = new BallController { Name = "Ball", Players = players }; AddChild(ball);
            ball.SetPhysicsProcess(false); ball.StateMachine.ForceState(BallState.Dribbling, 2);
            foreach (var player in new[] { _defender, holder })
            { player.SetPhysicsProcess(false); player.SetProcess(false); }
            holder.GetNode<AnimationTree>("AnimationTree").Active = false;
            _animation = _defender.GetNode<AnimationTree>("AnimationTree");
            _animation.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Manual;
            _skeleton = (Skeleton3D)_defender.FindChildren("*", nameof(Skeleton3D), true, false)[0];
            var modifier = _skeleton.GetNodeOrNull<SkeletonModifier3D>("DefensiveStancePose");
            Require(modifier != null, "shipped player has no DefensiveStancePose modifier");
            modifier.ModificationProcessed += Observe;
            _origin = _defender.GlobalPosition;
            Input.ActionRelease("move_protect");
            _captureOutput = HarnessArgs.ReadArg(OS.GetCmdlineUserArgs(), "--capture-output", "");
            _captureProduction = HarnessArgs.ReadArg(OS.GetCmdlineUserArgs(), "--harness-scenario", "presentation") == "capture";
            if (_captureOutput.Length > 0)
            {
                _camera = new Camera3D { Current = true, Projection = Camera3D.ProjectionType.Orthogonal }; AddChild(_camera);
                AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-45, -35, 0), LightEnergy = 1.5f });
                AddChild(new WorldEnvironment { Environment = new Godot.Environment {
                    BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(.15f, .17f, .20f),
                    AmbientLightSource = Godot.Environment.AmbientSource.Color,
                    AmbientLightColor = Colors.White, AmbientLightEnergy = .5f } });
            }
        }
        catch (Exception error) { Fail(error); }
    }

    private void Observe()
    {
        // Observe inside ModificationProcessed: Godot restores the fresh authored base afterwards.
        // https://docs.godotengine.org/en/4.7/classes/class_skeletonmodifier3d.html#class-skeletonmodifier3d-signal-modification-processed
        Vector3 Position(string bone) => _skeleton.GetBoneGlobalPose(_skeleton.FindBone("mixamorig_" + bone)).Origin;
        Quaternion Rotation(string bone) => _skeleton.GetBonePoseRotation(_skeleton.FindBone("mixamorig_" + bone));
        _current = new Pose(Position("Hips"), Position("Head"), Position("LeftFoot"), Position("RightFoot"),
            Position("LeftHand"), Position("RightHand"),
            Rotation("LeftArm"), Rotation("RightArm"), Rotation("LeftLeg"), Rotation("RightLeg"));
        _observations++;
    }

    public override void _PhysicsProcess(double delta)
    {
        try
        {
            _ticks++;
            if (_ticks == 1) _defender.SprintTickHostForHarness(delta);
            if (_captureProduction) { CaptureProduction(delta); return; }
            _animation.Advance(0); // Same authored animation time in every paired comparison.
            if (_ticks == 4)
            {
                Require(_observations > 0, "real skeleton modifier never processed");
                _neutral = _current; Capture("neutral");
                Input.ActionPress("move_protect"); _defender.SprintTickHostForHarness(delta);
                Require(_defender.DisplayDefensiveStance, "eligible defender never entered displayed stance");
            }
            if (_ticks == 8)
            {
                _stance = _current;
                Require(_stance.Hips.Y < _neutral.Hips.Y - .06f && _stance.Head.Y < _neutral.Head.Y - .04f,
                    $"stance not low: hips delta={_stance.Hips.Y - _neutral.Hips.Y} head delta={_stance.Head.Y - _neutral.Head.Y}");
                float Span(Pose pose) => Math.Abs(pose.LeftFoot.X - pose.RightFoot.X);
                Require(Span(_stance) > Span(_neutral) + .12f, $"feet not wider: neutral={Span(_neutral)} stance={Span(_stance)}");
                Require(_stance.LeftArm.AngleTo(_neutral.LeftArm) > .2f && _stance.RightArm.AngleTo(_neutral.RightArm) > .2f,
                    "both defensive arms must differ from authored idle");
                Require(_stance.LeftHand.Y > _stance.Hips.Y - .1f && _stance.RightHand.Y > _stance.Hips.Y - .1f
                    && _stance.LeftHand.Y > _neutral.LeftHand.Y + .1f && _stance.RightHand.Y > _neutral.RightHand.Y + .1f,
                    $"defensive hands not raised: left={_stance.LeftHand.Y} right={_stance.RightHand.Y} hips={_stance.Hips.Y}");
                Require(_stance.LeftKnee.AngleTo(_neutral.LeftKnee) > .2f && _stance.RightKnee.AngleTo(_neutral.RightKnee) > .2f,
                    "both knees must bend");
                foreach (var rotation in new[] { _stance.LeftArm, _stance.RightArm, _stance.LeftKnee, _stance.RightKnee })
                    Require(rotation.IsFinite() && Math.Abs(rotation.LengthSquared() - 1f) < .001f, "invalid bone rotation");
                Capture("stance");
            }
            if (_ticks == 16)
            {
                Require(Same(_stance, _current), "stance accumulated across fresh authored frames");
                Require(_defender.GlobalPosition.DistanceTo(_origin) < .0001f && _defender.Velocity.IsZeroApprox(), "cosmetic stance moved capsule");
                Input.ActionRelease("move_protect"); _defender.SprintTickHostForHarness(delta);
            }
            if (_ticks == 20)
            {
                Require(Same(_neutral, _current), "release failed to restore authored pose");
                Input.ActionPress("move_protect"); _defender.SprintTickHostForHarness(delta);
                Require(_defender.BeginMoveForHarness(_moves[0]), "steal commitment did not begin");
            }
            for (int i = 0; i < _moves.Length; i++)
                if (_ticks == 24 + i * 4)
                {
                    Require(_defender.DisplayMove().phase != MovePhase.Inactive, "commitment bypass premise inactive");
                    Require(Same(_neutral, _current), $"stance overwrote {_moves[i].GetType().Name} authored pose");
                    Capture("committed-" + _moves[i].GetType().Name);
                    _defender.SprintFinishMoveForHarness();
                    if (i + 1 < _moves.Length) Require(_defender.BeginMoveForHarness(_moves[i + 1]), "next defensive commitment did not begin");
                    else _defender.ProtectionPivotForHarness(true);
                }
            if (_ticks == 36)
            {
                Require(_defender.IsPivotingInPlace && Same(_neutral, _current), "stance overwrote pivot authored pose");
                _defender.ProtectionPivotForHarness(false);
            }
            if (_ticks == 40)
            {
                Require(Same(_stance, _current), "held stance did not resume after pivot");
                Input.ActionRelease("move_protect");
                // ENet client role makes this the remote copy; snapshots enter the real
                // receiver adapter. The separate network harness proves real transport.
                // https://docs.godotengine.org/en/4.7/classes/class_enetmultiplayerpeer.html#class-enetmultiplayerpeer-method-create-client
                // https://docs.godotengine.org/en/4.7/classes/class_scenetree.html#class-scenetree-method-set-multiplayer
                _remoteRolePeer = new ENetMultiplayerPeer();
                Require(_remoteRolePeer.CreateClient("127.0.0.1", 28446) == Error.Ok, "remote client role setup failed");
                GetTree().SetMultiplayer(new SceneMultiplayer { MultiplayerPeer = _remoteRolePeer }, _defender.GetParent().GetPath());
                Require(!_defender.Multiplayer.IsServer() && _defender.Name != _defender.Multiplayer.GetUniqueId().ToString(),
                    "remote display branch premise invalid");
                _defender.StanceReceivePresentationForHarness(true);
            }
            if (_ticks == 44)
            {
                Require(Same(_stance, _current), "remote received stance did not lower/widen actual rig");
                _defender.StanceReceivePresentationForHarness(false);
            }
            if (_ticks == 48)
            {
                Require(Same(_neutral, _current), "remote false snapshot did not restore authored pose");
                _defender.StanceReceivePresentationForHarness(true, MovePhase.Startup, "steal");
            }
            if (_ticks == 52)
            {
                Require(Same(_neutral, _current), "remote commitment snapshot overwritten by stance");
                _defender.StanceReceivePresentationForHarness(true, pivot: true);
            }
            if (_ticks == 56)
            {
                Require(_defender.IsPivotingInPlace && Same(_neutral, _current), "remote pivot snapshot overwritten by stance");
                _defender.StanceReceivePresentationForHarness(true);
            }
            if (_ticks == 60)
            {
                Require(Same(_stance, _current), "remote stance failed to resume after pivot snapshot");
                GD.Print("[defensive-stance-presentation] PASS presentation"); GetTree().Quit(0);
            }
        }
        catch (Exception error) { Fail(error); }
    }

    private static bool Same(Pose a, Pose b) => a.Hips.DistanceTo(b.Hips) < .0001f
        && a.Head.DistanceTo(b.Head) < .0001f && a.LeftFoot.DistanceTo(b.LeftFoot) < .0001f
        && a.RightFoot.DistanceTo(b.RightFoot) < .0001f && a.LeftArm.AngleTo(b.LeftArm) < .001f
        && a.LeftHand.DistanceTo(b.LeftHand) < .0001f && a.RightHand.DistanceTo(b.RightHand) < .0001f
        && a.RightArm.AngleTo(b.RightArm) < .001f && a.LeftKnee.AngleTo(b.LeftKnee) < .001f
        && a.RightKnee.AngleTo(b.RightKnee) < .001f;

    private void CaptureProduction(double delta)
    {
        Require(_captureOutput.Length > 0, "capture scenario requires --capture-output");
        _defender.ProtectionApplyAnimationForHarness(); _animation.Advance(delta);
        if (_ticks == 60) Capture("production-neutral");
        if (_ticks == 80) { Input.ActionPress("move_protect"); _defender.SprintTickHostForHarness(delta); }
        if (_ticks == 120) { Require(_defender.DisplayDefensiveStance, "capture stance ineligible"); Capture("production-stance"); }
        if (_ticks == 140) Require(_defender.BeginMoveForHarness(_moves[0]), "capture steal failed");
        if (_ticks == 144) Capture("production-steal");
        if (_ticks == 160) { _defender.SprintFinishMoveForHarness(); Require(_defender.BeginMoveForHarness(_moves[1]), "capture block failed"); }
        if (_ticks == 164) Capture("production-block");
        if (_ticks == 180) { _defender.SprintFinishMoveForHarness(); Require(_defender.BeginMoveForHarness(_moves[2]), "capture contest failed"); }
        if (_ticks == 184) Capture("production-contest");
        if (_ticks == 200) { Input.ActionRelease("move_protect"); GD.Print("[defensive-stance-presentation] PASS capture"); GetTree().Quit(0); }
    }

    private async void Capture(string name)
    {
        if (_captureOutput.Length == 0) return;
        var head = _skeleton.ToGlobal(_current.Head); var feet = _skeleton.ToGlobal((_current.LeftFoot + _current.RightFoot) * .5f);
        float height = head.DistanceTo(feet); var center = (head + feet) * .5f;
        _camera.Size = height * 1.5f; _camera.GlobalPosition = center + new Vector3(height * .8f, height * .15f, height * 2f); _camera.LookAt(center);
        var playback = _animation.Get("parameters/playback").As<AnimationNodeStateMachinePlayback>();
        if (_captureProduction)
        {
            string expected = name switch { "production-steal" => "StealStartupRight", "production-block" => "BlockStartup",
                "production-contest" => "ContestStartup", _ => "Locomotion" };
            Require(playback.GetCurrentNode().ToString() == expected, $"capture {name} expected actual animation {expected}");
        }
        GD.Print($"[defensive-stance-presentation] capture={name} animation={playback.GetCurrentNode()} phase={_defender.DisplayMove().phase} stance={_defender.DisplayDefensiveStance}");
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        System.IO.Directory.CreateDirectory(_captureOutput);
        var path = System.IO.Path.Combine(_captureOutput, name + ".png");
        Require(GetViewport().GetTexture().GetImage().SavePng(path) == Error.Ok, "capture save failed");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private void Fail(Exception error) { GD.Print($"[defensive-stance-presentation] FAIL: {error.Message}"); Input.ActionRelease("move_protect"); GetTree().Quit(1); }
    public override void _ExitTree() => _remoteRolePeer?.Close();
}
