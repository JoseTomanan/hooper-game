using System;
using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Player;
using Hooper.Moves;

namespace HOOPERGAME.Tests.Integration;

public partial class SprintTest : Node3D
{
    private PlayerController _holder, _defender;
    private BallController _ball;
    private int _ticks;
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
        Input.ActionPress("move_sprint");
    }
    private void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    public override void _PhysicsProcess(double delta)
    {
        try
        {
            _ticks++;
            if (_ticks == 1)
            {
                // Verify the shipped mappings, not just an action name usable by ActionPress.
                // Source: https://docs.godotengine.org/en/4.7/classes/class_inputmap.html#class-inputmap-method-action-get-events
                // Source: https://docs.godotengine.org/en/4.7/classes/class_@globalscope.html#enum-globalscope-joyaxis
                var bindings = InputMap.ActionGetEvents("move_sprint");
                Require(bindings.Any(binding => binding is InputEventJoypadMotion motion
                    && motion.Axis == JoyAxis.TriggerRight && motion.AxisValue > 0), "RT positive-axis binding missing");
                Require(bindings.Any(binding => binding is InputEventKey key
                    && key.PhysicalKeycode == Key.Shift), "Shift binding missing");
            }
            switch (_scenario)
            {
                case "speed":
                    _holder.SprintTickHostForHarness(Dt);
                    if (_ticks < 45) return;
                    Require(Math.Abs(_holder.Velocity.Length() - _holder.SprintSpeed) < 0.001f, $"sprint speed {_holder.Velocity.Length()}");
                    Input.ActionRelease("move_sprint");
                    _holder.SprintTickHostForHarness(Dt);
                    Require(!_holder.IsSprinting && _holder.Velocity.Length() < _holder.SprintSpeed, "release did not restore jog");
                    break;
                case "turn":
                    Input.ActionRelease("move_forward"); Input.ActionPress("move_backward");
                    _holder.ResetContactStateForHarness(new Vector3(0, 0, 10), Mathf.Pi, Vector3.Zero);
                    _holder.SprintTickHostForHarness(Dt);
                    float sprintTurn = Math.Abs(Mathf.AngleDifference(Mathf.Pi, _holder.Heading));
                    Input.ActionRelease("move_sprint");
                    _holder.ResetContactStateForHarness(new Vector3(0, 0, 10), Mathf.Pi, Vector3.Zero);
                    _holder.SprintTickHostForHarness(Dt);
                    float jogTurn = Math.Abs(Mathf.AngleDifference(Mathf.Pi, _holder.Heading));
                    Require(sprintTurn > 0f && sprintTurn < jogTurn * 0.9f, $"sprint/jog turn {sprintTurn}/{jogTurn}");
                    GD.Print($"[sprint] reversal sprint={sprintTurn} jog={jogTurn}");
                    break;
                case "gates":
                    _holder.Velocity = new Vector3(0, 0, -_holder.MoveSpeed);
                    _ball.StateMachine.ForceState(BallState.Held, 1);
                    _holder.SprintTickHostForHarness(Dt);
                    Require(Math.Abs(_holder.Velocity.Length() - _holder.MoveSpeed) < 0.001f, "Held step sprinted");
                    Require(_ball.State == BallState.Dribbling, "live Held auto-start control did not dribble");
                    _holder.SprintTickHostForHarness(Dt);
                    Require(_holder.IsSprinting && _holder.Velocity.Length() > _holder.MoveSpeed, "live-dribble control refused sprint");
                    Require(_holder.SprintPumpFakeForHarness(), "could not establish real dead-dribble");
                    Require(_ball.HasDribbled && _ball.State == BallState.Held && !_holder.IsSprinting, "commitment/cradle did not cancel sprint");
                    _holder.SprintFinishMoveForHarness();
                    _holder.Velocity = new Vector3(0, 0, -_holder.MoveSpeed);
                    _holder.SprintTickHostForHarness(Dt);
                    float brakingSpeed = _holder.Velocity.Length();
                    Require(!_holder.IsSprinting && _ball.State == BallState.Held && _ball.HasDribbled &&
                        _ball.StateMachine.HolderPeerId == 1 && brakingSpeed > 0f && brakingSpeed < _holder.MoveSpeed,
                        $"dead Held must brake without sprint/restart; speed={brakingSpeed}");
                    // Gathered neutral movement brakes existing momentum; holding
                    // both movement and sprint must never re-accelerate it.
                    for (int i = 0; i < 12; i++) _holder.SprintTickHostForHarness(Dt);
                    Require(_holder.Velocity.Length() < 0.001f, "dead Held did not settle while movement/sprint stayed pressed");
                    Vector3 settledPosition = _holder.GlobalPosition;
                    for (int i = 0; i < 5; i++)
                    {
                        _holder.SprintTickHostForHarness(Dt);
                        Require(!_holder.IsSprinting && _ball.State == BallState.Held && _ball.HasDribbled &&
                            _ball.StateMachine.HolderPeerId == 1 && _holder.Velocity.Length() < 0.001f &&
                            (_holder.GlobalPosition - settledPosition).Length() < 0.001f,
                            "dead Held moved, sprinted or restarted after settling with input held");
                    }
                    _ball.StateMachine.ForceState(BallState.Dribbling, 2);
                    // Independently retain the off-ball jog-speed control after
                    // the gathered holder's preceding zero-velocity observation.
                    _holder.Velocity = new Vector3(0, 0, -_holder.MoveSpeed);
                    _holder.SprintTickHostForHarness(Dt);
                    Require(!_holder.IsSprinting && Math.Abs(_holder.Velocity.Length() - _holder.MoveSpeed) < 0.001f, "off-ball sprinted");
                    break;
                case "steal-jog": case "steal-sprint": case "steal-outside": case "steal-wrong-hand":
                    if (_scenario == "steal-jog") Input.ActionRelease("move_sprint");
                    _holder.SprintTickHostForHarness(Dt);
                    float phase = _scenario == "steal-outside" ? 0.1f : 0.25f;
                    HandSide target = _scenario == "steal-wrong-hand" ? HandSide.Left : _holder.HandSide;
                    Require(target != _holder.HandSide || _scenario != "steal-wrong-hand", "wrong-hand premise invalid");
                    Require(_defender.BeginMoveForHarness(new StealMove(target, 1f)), "steal did not begin");
                    _defender.SprintEnterActiveForHarness();
                    Require(!_ball.SweepActiveForHarness, "transit sweep would contaminate phase-band proof");
                    _ball.SprintResolveStealForHarness(phase);
                    bool expected = _scenario == "steal-sprint";
					Require(expected
                        ? _ball.State == BallState.Loose && _ball.LastToucherPeerIdForHarness == 2
                        : _ball.State == BallState.Dribbling && _ball.StateMachine.HolderPeerId == 1,
                        $"phase={phase} sprint={_holder.IsSprinting} state={_ball.State}");
                    GD.Print($"[sprint] band sample={phase} sweep=false loose={expected}");
                    break;
                case "shot":
                    _holder.Velocity = new Vector3(0, 0, -_holder.SprintSpeed);
                    Require(_holder.SprintBeginShotForHarness(), "sprint pull-up did not begin");
                    Require(_ball.SprintShootForHarness(_holder), "sprint shot did not release");
                    float sprintPenalty = _ball.LastMovementFactorForHarness;
                    _holder.SprintFinishMoveForHarness();
                    _ball.StateMachine.ForceState(BallState.Dribbling, 1);
                    _holder.Velocity = new Vector3(0, 0, -_holder.MoveSpeed);
                    Require(_holder.SprintBeginShotForHarness() && _ball.SprintShootForHarness(_holder), "jog pull-up did not release");
                    float jogPenalty = _ball.LastMovementFactorForHarness;
                    Require(Math.Abs(jogPenalty - 1.8f) < 0.0001f && sprintPenalty > jogPenalty,
                        $"actual release penalties sprint={sprintPenalty} jog={jogPenalty}");
                    GD.Print($"[sprint] actual release penalties sprint={sprintPenalty} jog={jogPenalty}");
                    break;
                default: throw new InvalidOperationException("unknown scenario");
            }
            GD.Print($"[sprint] PASS {_scenario}");
            GetTree().Quit(0);
        }
        catch (Exception error) { GD.Print($"[sprint] FAIL {_scenario}: {error.Message}"); GetTree().Quit(1); }
    }
}
