using System;
using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Player;
using Hooper.Moves;

namespace HOOPERGAME.Tests.Integration;

public partial class ProtectionTest : Node3D
{
    private PlayerController _holder, _defender;
    private BallController _ball;
    private int _ticks;
    private Skeleton3D _skeleton;
    private Quaternion _left, _right, _neutralLeft, _neutralRight, _protectedLeft;
    private Vector3 _hips, _neutralHips;
    private int _observed;
    private float _leftHandY, _rightHandY, _hipsY, _height, _neutralLeftHandY, _neutralRightHandY;
    private string _captureOutput;
    private Camera3D _captureCamera;
    private int _captureFrames;

    private string _scenario;
    private double Dt => 1.0 / Engine.PhysicsTicksPerSecond;
    public override void _Ready()
    {
        _scenario = HarnessArgs.ReadArg(OS.GetCmdlineUserArgs(), "--harness-scenario", "speed");
        var players = new Node3D { Name = "Players" };
        AddChild(players);
        // Source: https://docs.godotengine.org/en/4.7/classes/class_packedscene.html#class-packedscene-method-instantiate
        _holder = ResourceLoader.Load<PackedScene>("res://scenes/Player.tscn").Instantiate<PlayerController>();
        _holder.Name = "1";
        players.AddChild(_holder);
        _defender = new PlayerController { Name = "2", Position = new Vector3(6, 0, 0) };
        players.AddChild(_defender);
        _ball = new BallController { Name = "Ball", Players = players };
        AddChild(_ball);
        foreach (var player in new[] { _holder, _defender })
        { player.SetPhysicsProcess(false); player.SetProcess(false); }
        _holder.GetNode<AnimationTree>("AnimationTree").Active = false;
        _ball.SetPhysicsProcess(false);
        _ball.StateMachine.ForceState(BallState.Dribbling, 1);
        _holder.ResetContactStateForHarness(new Vector3(0, 0, 10), Mathf.Pi, Vector3.Zero);
        Input.ActionPress("move_forward");
        Input.ActionPress("move_protect");
        if (_scenario == "presentation" || _scenario == "capture-dribble")
        {
            Input.ActionRelease("move_forward"); Input.ActionRelease("move_protect");
            _captureOutput = HarnessArgs.ReadArg(OS.GetCmdlineUserArgs(), "--capture-output", "");
            if (_captureOutput.Length > 0)
            {
                _captureCamera = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal, Current = true };
                AddChild(_captureCamera);
                AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-45, -35, 0), LightEnergy = 1.5f });
                AddChild(new WorldEnvironment { Environment = new Godot.Environment {
                    BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(.15f,.17f,.20f),
                    AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = Colors.White, AmbientLightEnergy = .5f } });
            }
            _skeleton = (Skeleton3D)_holder.FindChildren("*", nameof(Skeleton3D), true, false)[0];
            var modifier = _skeleton.GetNode<ProtectionPose>("ProtectionPose");
            modifier.ModificationProcessed += () =>
            {
                _left = _skeleton.GetBonePoseRotation(_skeleton.FindBone("mixamorig_LeftArm"));
                _right = _skeleton.GetBonePoseRotation(_skeleton.FindBone("mixamorig_RightArm"));
                _hips = _skeleton.GetBonePosePosition(_skeleton.FindBone("mixamorig_Hips"));
                float WorldY(string bone) => _skeleton.ToGlobal(_skeleton.GetBoneGlobalPose(_skeleton.FindBone(bone)).Origin).Y;
                _leftHandY = WorldY("mixamorig_LeftHand"); _rightHandY = WorldY("mixamorig_RightHand");
                _hipsY = WorldY("mixamorig_Hips");
                _height = WorldY("mixamorig_Head") - WorldY("mixamorig_LeftToeBase");
                _observed++;
            };
            var tree = _holder.GetNode<AnimationTree>("AnimationTree");
            tree.Active = true;
            tree.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Manual;
        }
    }
    private void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    public override void _PhysicsProcess(double delta)
    {
        try
        {
            if (_scenario == "capture-dribble") { CaptureDribble(); return; }
            if (!string.IsNullOrEmpty(_captureOutput))
            {
                _captureFrames++;
                if (_captureFrames < 60 || _captureFrames % 10 != 0)
                {
                    _holder.GetNode<AnimationTree>("AnimationTree").Advance(0);
                    return;
                }
                FrameCamera();
            }
            _ticks++;
            if (_ticks == 1)
            {
                // Verify the shipped mappings, not just an action name usable by ActionPress.
                // Source: https://docs.godotengine.org/en/4.7/classes/class_inputmap.html#class-inputmap-method-action-get-events
                // Source: https://docs.godotengine.org/en/4.7/classes/class_@globalscope.html#enum-globalscope-joyaxis
                Require(Math.Abs(_holder.ProtectSpeed - 1f) < .0001f, "shipped Player scene overrides protected speed");
                // #217 insurance: code-built gameplay fixtures do not see scene export overrides.
                var shippedBall = ResourceLoader.Load<PackedScene>("res://scenes/Ball.tscn").Instantiate<BallController>();
                try
                {
                    Require(Math.Abs(shippedBall.ProtectStealLoExposed - .45f) < .0001f
                        && Math.Abs(shippedBall.ProtectStealHiExposed - .55f) < .0001f,
                        "shipped Ball scene overrides protected exposure band");
                }
                finally { shippedBall.Free(); }
                var bindings = InputMap.ActionGetEvents("move_protect");
                Require(bindings.Any(binding => binding is InputEventJoypadMotion motion
                    && motion.Axis == JoyAxis.TriggerLeft && motion.AxisValue > 0), "LT positive-axis binding missing");
                Require(bindings.Any(binding => binding is InputEventKey key
                    && key.PhysicalKeycode == Key.Ctrl), "Ctrl binding missing");
            }
            switch (_scenario)
            {
                case "speed":
                    _holder.SprintTickHostForHarness(Dt);
                    if (_ticks == 1)
                    {
                        _holder.Velocity = new Vector3(0, 0, -_holder.MoveSpeed);
                        _holder.SprintTickHostForHarness(Dt);
                        Require(_holder.Velocity.Length() > 1f && _holder.Velocity.Length() < _holder.MoveSpeed, "entry discarded momentum");
                    }
                    if (_ticks < 45) return;
                    Require(Math.Abs(_holder.Velocity.Length() - 1.0f) < 0.001f,  $"protected speed {_holder.Velocity.Length()}");
                    var settled = _holder.GlobalPosition;
                    for (int i = 0; i < 60; i++) _holder.SprintTickHostForHarness(Dt);
                    Require(Math.Abs(_holder.GlobalPosition.Z - settled.Z) <= 1.01f, "protected rim progress exceeded 1m/s");
                    Input.ActionRelease("move_protect");
                    _holder.SprintTickHostForHarness(Dt);
                    Require(_holder.Velocity.Length() > 1.0f && _holder.Velocity.Length() < _holder.MoveSpeed, "release did not restore finite acceleration");
                    for (int i = 0; i < 60; i++) _holder.SprintTickHostForHarness(Dt);
                    Require(Math.Abs(_holder.Velocity.Length() - _holder.MoveSpeed) < .001f, "neutral paired control failed");
                    break;
                case "steal-neutral": case "steal-protect": case "steal-center": case "steal-wrong-hand":
                    if (_scenario == "steal-neutral") Input.ActionRelease("move_protect");
                    _holder.SprintTickHostForHarness(Dt);
                    float phase = _scenario == "steal-center" || _scenario == "steal-wrong-hand" ? .5f : .4f;
                    var target = _scenario == "steal-wrong-hand" ? HandSide.Left : _holder.HandSide;
                    Require(_scenario != "steal-wrong-hand" || target != _holder.HandSide, "wrong hand premise invalid");
                    Require(_defender.BeginMoveForHarness(new StealMove(target, 1f)), "steal did not begin");
                    _defender.SprintEnterActiveForHarness();
                    Require(!_ball.SweepActiveForHarness, "transit invalidates phase proof");
                    _ball.SprintResolveStealForHarness(phase);
                    bool connects = _scenario == "steal-neutral" || _scenario == "steal-center";
                    Require(connects ? _ball.State == BallState.Loose && _ball.LastToucherPeerIdForHarness == 2
                        : _ball.State == BallState.Dribbling && _ball.StateMachine.HolderPeerId == 1,
                        $"phase={phase} protect={_holder.IsProtecting} state={_ball.State}");
                    break;
                case "gates":
                    Input.ActionPress("move_sprint");
                    _holder.SprintTickHostForHarness(Dt);
                    Require(_holder.IsProtecting && !_holder.IsSprinting, "protection lost precedence");
                    foreach (var state in new[] { BallState.Held, BallState.Loose, BallState.InFlight })
                    {
                        _ball.StateMachine.ForceState(state, 1);
                        Require(!_holder.IsProtecting && !_holder.IsSprinting, $"ineligible {state} benefit");
                    }
                    _ball.StateMachine.ForceState(BallState.Dribbling, 2);
                    Require(!_holder.IsProtecting && !_holder.IsSprinting, "off-ball benefit");
                    _ball.StateMachine.ForceState(BallState.Dribbling, 1);
                    Require(_holder.IsProtecting, "held request did not resume eligibility");
                    Require(_holder.BeginMoveForHarness(new Hesitation()), "commitment did not begin");
                    Require(!_holder.IsProtecting && !_holder.IsSprinting, "committed benefit");
                    _holder.SprintFinishMoveForHarness();
                    Require(_holder.IsProtecting, "request did not resume after commitment");
                    Input.ActionRelease("move_protect");
                    _holder.SprintTickHostForHarness(Dt);
                    Require(_holder.IsSprinting, "sprint release control failed");
                    break;
                case "held-static":
                    _holder.SprintTickHostForHarness(Dt);
                    Require(_holder.SprintPumpFakeForHarness(), "real pumpfake failed");
                    _holder.SprintFinishMoveForHarness();
                    Require(_ball.State == BallState.Held && _ball.HasDribbled && !_holder.IsProtecting, "not dead Held");
                    _holder.ResetContactStateForHarness(Vector3.Zero, 0, Vector3.Zero);
                    _defender.GlobalPosition = new Vector3(-.4f, 0, 0);
                    Require(_defender.BeginMoveForHarness(new StealMove(_holder.HandSide, 1f)), "Held steal failed begin");
                    _defender.SprintEnterActiveForHarness();
                    _ball.ProtectionResolveHeldForHarness();
                    Require(_ball.State == BallState.Loose && _ball.LastToucherPeerIdForHarness == 2, "protect made static dead-Held immune");
                    break;
                case "presentation":
                    var animation = _holder.GetNode<AnimationTree>("AnimationTree");
                    animation.Advance(0);
                    _holder.SprintTickHostForHarness(Dt);
                    if (_ticks == 4)
                    {
                        Require(_observed > 0, "modifier never processed real skeleton");
                        _neutralLeft = _left; _neutralRight = _right; _neutralHips = _hips;
                        _neutralLeftHandY = _leftHandY; _neutralRightHandY = _rightHandY;
                        Capture("neutral");
                        Input.ActionPress("move_protect");
                    }
                    if (_ticks == 8)
                    {
                        Require(_neutralLeft.AngleTo(_left) > .2f && _neutralRight.AngleTo(_right) < .0001f, "right-hand shield did not isolate left off-arm");
                        Require(_leftHandY >= _hipsY - .10f * _height && _leftHandY >= _neutralLeftHandY + .10f * _height,
                            $"off-hand did not lift to guard: hand={_leftHandY} hips={_hipsY} neutral={_neutralLeftHandY} height={_height}");
                        Require(_hips.Y < _neutralHips.Y - .03f, "shield posture did not lower");
                        _protectedLeft = _left;
                        Capture("right");
                    }
                    if (_ticks == 12)
                    {
                        Require(_protectedLeft.AngleTo(_left) < .0001f, "pose accumulated across frames");
                        _holder.SetHandSideForHarness(HandSide.Left);
                    }
                    if (_ticks == 16)
                    {
                        Require(_neutralRight.AngleTo(_right) > .2f && _neutralLeft.AngleTo(_left) < .0001f, "left-hand shield did not mirror off-arm");
                        Require(_rightHandY >= _hipsY - .10f * _height && _rightHandY >= _neutralRightHandY + .10f * _height,
                            $"mirrored off-hand did not lift to guard: hand={_rightHandY} hips={_hipsY} neutral={_neutralRightHandY} height={_height}");
                        Capture("left");
                        Input.ActionRelease("move_protect");
                    }
                    if (_ticks == 20)
                    {
                        Require(_neutralLeft.AngleTo(_left) < .0001f && _neutralRight.AngleTo(_right) < .0001f
                            && _hips.DistanceTo(_neutralHips) < .0001f, "release did not restore authored pose");
                        Input.ActionPress("move_protect");
                        Require(_holder.BeginMoveForHarness(new Hesitation()), "presentation commitment failed");
                    }
                    if (_ticks == 24)
                    {
                        Require(_neutralLeft.AngleTo(_left) < .0001f && _neutralRight.AngleTo(_right) < .0001f, "shield overwrote commitment pose");
                        _holder.SprintFinishMoveForHarness();
                        _holder.ProtectionPivotForHarness(true);
                    }
                    if (_ticks >= 25 && _ticks <= 28) _holder.ProtectionPivotForHarness(true);
                    if (_ticks == 28)
                    {
                        Require(_holder.IsPivotingInPlace && _neutralLeft.AngleTo(_left) < .0001f && _neutralRight.AngleTo(_right) < .0001f,
                            "shield overwrote pivot pose");
                        _holder.ProtectionPivotForHarness(false);
                    }
                    if (_ticks < 32) return;
                    Require(_neutralRight.AngleTo(_right) > .2f && _neutralLeft.AngleTo(_left) < .0001f, "shield did not resume after pivot");
                    break;
                default: throw new InvalidOperationException("unknown scenario");
            }
            GD.Print($"[protection] PASS {_scenario}");
            GetTree().Quit(0);
        }
        catch (Exception error) { GD.Print($"[protection] FAIL {_scenario}: {error.Message}"); GetTree().Quit(1); }
    }

    private void FrameCamera()
    {
                var head = _skeleton.ToGlobal(_skeleton.GetBoneGlobalPose(_skeleton.FindBone("mixamorig_Head")).Origin);
                var foot = _skeleton.ToGlobal(_skeleton.GetBoneGlobalPose(_skeleton.FindBone("mixamorig_LeftToeBase")).Origin);
                float height = head.DistanceTo(foot);
                var center = (head + foot) * .5f;
                _captureCamera.Size = height * 1.5f;
                _captureCamera.GlobalPosition = center + new Vector3(height * .8f, height * .15f, height * 2f);
                _captureCamera.LookAt(center);
    }

    // Render-only companion: use the real production dribble animation selection.
    // The fixed-pose assertion scenario above isolates modifier changes separately.
    private void CaptureDribble()
    {
        Require(!string.IsNullOrEmpty(_captureOutput), "capture-dribble requires --capture-output");
        _captureFrames++;
        _holder.SprintTickHostForHarness(Dt);
        _holder.ProtectionApplyAnimationForHarness();
        _holder.GetNode<AnimationTree>("AnimationTree").Advance(Dt);
        FrameCamera();
        if (_captureFrames == 60) CaptureVerified("neutral-right", "DribbleRight");
        if (_captureFrames == 80) Input.ActionPress("move_protect");
        if (_captureFrames == 120) CaptureVerified("protected-right", "DribbleRight");
        if (_captureFrames == 140) { Input.ActionRelease("move_protect"); _holder.SetHandSideForHarness(HandSide.Left); }
        if (_captureFrames == 180) CaptureVerified("neutral-left", "DribbleLeft");
        if (_captureFrames == 200) Input.ActionPress("move_protect");
        if (_captureFrames == 240) CaptureVerified("protected-left", "DribbleLeft");
        if (_captureFrames == 260) { GD.Print("[protection] captured production dribble poses"); GetTree().Quit(0); }
    }

    private void CaptureVerified(string name, string expected)
    {
        var playback = _holder.GetNode<AnimationTree>("AnimationTree").Get("parameters/playback").As<AnimationNodeStateMachinePlayback>();
        var current = playback.GetCurrentNode().ToString();
        Require(current == expected, $"capture actual animation state {current} expected {expected}");
        GD.Print($"[protection] capture state={current} hand={_holder.HandSide} protect={_holder.IsProtecting}");
        Capture(name);
    }

    private async void Capture(string name)
    {
        if (string.IsNullOrEmpty(_captureOutput)) return;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        System.IO.Directory.CreateDirectory(_captureOutput);
        var path = System.IO.Path.Combine(_captureOutput, name + ".png");
        var result = GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"[protection] capture={path} result={result}");
    }
}