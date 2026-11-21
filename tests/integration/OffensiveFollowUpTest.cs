using System.Collections.Generic;
using System.Linq;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;
using Hooper.Systems;

namespace HOOPERGAME.Tests.Integration;

public partial class OffensiveFollowUpTest : Node
{
    private BallController _ball;
    private PlayerController _player;
    private GameManager _game;
    private string _scenario;
    private int _tick;
    private int _afterRecovery;
    private int _completedAt;
    private bool _queuedTailBeforeRelease;
    private bool _finished;
    private bool _prepared;
    private bool _releasedShot;
    private bool _feinted;
    private bool _terminal;
    private bool UsesShortRecovery => _scenario is "shot" or "pump-fake";
    private readonly List<string> _starts = new();
    private CommittedMove _previous;
    private MovePhase _phase;
    private int _frame;

    public override void _Ready()
    {
        _scenario = HarnessArgs.ReadArg(OS.GetCmdlineUserArgs(), "--harness-scenario", "fifo");
        var players = new Node3D { Name = "Players" };
        _player = new PlayerController { Name = "1", Position = new Vector3(0, 0, 4) };
        players.AddChild(_player);
        _ball = new BallController { Name = "Ball", Players = players, RimCenter = new Vector3(0, 3, 0) };
        _game = new GameManager { Name = "GameManager", TargetScore = 1 };
        AddChild(_game);
        AddChild(players);
        AddChild(_ball);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        if (++_tick > 180)
        {
            Finish(false, $"timeout, starts={string.Join(",", _starts)}");
            return;
        }

        if (!_prepared)
        {
            if (_ball.StateMachine.HolderPeerId != 1) return;
            _prepared = true;
            if (UsesShortRecovery)
                Input.ActionPress("move_jab");
            else if (_scenario == "context")
            {
                _ball.TryStartDribble(1);
                Input.ActionPress("move_drive");
            }
            else
            {
                _ball.TryStartDribble(1);
                Aim(true);
            }
            return;
        }

        var (move, phase, frame) = _player.OffensiveMoveForHarness;

        if (move != null && !ReferenceEquals(move, _previous))
        {
            _starts.Add(move.Id);
            GD.Print($"[offensive-follow-up] begin={move.Id} phase={phase} frame={frame} tick={_tick}");
            if (_phase != MovePhase.Inactive || phase != MovePhase.Startup || frame != 1 ||
                (_completedAt != 0 && _completedAt != _tick - 1))
            {
                Finish(false, "a move skipped the ordinary first Startup tick");
                return;
            }
        }

        if (ReferenceEquals(move, _previous) && move != null && !_terminal && !_feinted
            && phase != _phase && (frame != 0 || _frame != Frames(move, _phase) - 1))
        {
            Finish(false, $"phase advanced early: {move.Id} {_phase}/{_frame} -> {phase}/{frame}");
            return;
        }

        if (_previous != null && move == null && !_terminal)
        {
            if (_phase != MovePhase.Recovery || _frame != _previous.FrameData.RecoveryFrames - 1)
            {
                Finish(false, "a move did not complete Recovery");
                return;
            }
            _completedAt = _tick;
        }

        if (_scenario == "shot" && move is JumpShot && phase == MovePhase.Startup)
            _queuedTailBeforeRelease |= _player.PendingOffensiveInputsForHarness == 1;
        if (_scenario == "shot" && _ball.State == BallState.InFlight &&
            (!_queuedTailBeforeRelease || _player.PendingOffensiveInputsForHarness != 0))
        {
            Finish(false, "release did not clear an observed pending tail");
            return;
        }

        if (!_terminal && _starts.Count == 1 && move != null)
        {
            if (phase == MovePhase.Startup && frame == 1)
            {
                Input.ActionRelease("move_drive");
                Input.ActionRelease("move_jab");
                if (!UsesShortRecovery && _scenario != "context") Aim(false);
            }

            if (_scenario == "outside" && (phase, frame) is (MovePhase.Startup, 2) or (MovePhase.Active, 1))
                Input.ActionPress("ball_shoot");
            if (_scenario == "outside" && (phase, frame) is (MovePhase.Startup, 3) or (MovePhase.Active, 2))
                Input.ActionRelease("ball_shoot");

            if (phase == MovePhase.Recovery)
                DriveRecovery(move.FrameData.RecoveryFrames - frame);
        }

        if (_starts.Count == 2 && move is JumpShot && _scenario == "pump-fake" && phase == MovePhase.Startup && frame == 4)
        {
            _feinted = true;
            Input.ActionPress("move_feint");
        }
        else
            Input.ActionRelease("move_feint");

        _releasedShot |= _ball.State == BallState.InFlight;

        if (_terminal || (_starts.Count > 0 && move == null))
            _afterRecovery++;
        else
            _afterRecovery = 0;

        _previous = move;
        _phase = phase;
        _frame = frame;

        if (_afterRecovery < 8) return;

        string[] expected = _scenario switch
        {
            "fifo" => new[] { "crossover", "behindtheback", "hesitation" },
            "outside" or "loss-regain" or "terminal" or "held-outside" => new[] { "crossover" },
            "held" => new[] { "crossover", "behindtheback" },
            "boundary" => new[] { "crossover", "drivegather" },
            "context" => new[] { "drivegather", "layup" },
            "shot" => new[] { "jab", "jumpshot" },
            "pump-fake" => new[] { "jab", "jumpshot", "jab" },
            _ => System.Array.Empty<string>(),
        };
        bool releaseMatches = _releasedShot == (_scenario is "shot" or "context");
        Finish(_starts.SequenceEqual(expected) && releaseMatches
            && (!_terminal || _player.PendingOffensiveInputsForHarness == 0),
            $"starts={string.Join(",", _starts)}, released={_releasedShot}, pending={_player.PendingOffensiveInputsForHarness}");
    }

    private void DriveRecovery(int remaining)
    {
        if (_scenario == "boundary")
        {
            if (remaining == 7) Input.ActionPress("move_drive");
            return;
        }

        if (_scenario is "held" or "held-outside")
        {
            int gestureStart = _scenario == "held" ? 9 : 11;
            if (remaining == gestureStart)
            {
                Input.ActionPress("move_size_up");
                Aim(true, right: true);
                Input.FlushBufferedEvents();
            }
            if (remaining == 4) Input.ActionRelease("move_size_up");
            return;
        }

        if (_scenario == "outside")
        {
            if (remaining == 8) Input.ActionPress("ball_shoot");
            if (remaining == 7) Input.ActionRelease("ball_shoot");
            if (remaining == 2) Input.ActionPress("def_steal");
            if (remaining == 1) Input.ActionRelease("def_steal");
            return;
        }

        if (UsesShortRecovery)
        {
            if (remaining == 4) Input.ActionPress("ball_shoot");
            if (remaining == 3) Input.ActionRelease("ball_shoot");
            if (remaining == 2) Input.ActionPress("move_jab");
            return;
        }

        if (_scenario == "context")
        {
            if (remaining == 6) Aim(true, right: true);
            if (remaining == 5) Aim(false, right: true);
            if (remaining == 4) Input.ActionPress("ball_shoot");
            if (remaining == 3) Input.ActionRelease("ball_shoot");
            if (remaining == 1) _player.GlobalPosition = new Vector3(0, 0, 1);
            return;
        }

        if (_scenario is "loss-regain" or "terminal")
        {
            if (remaining == 7) Input.ActionPress("ball_shoot");
            if (remaining == 5)
            {
                Input.ActionRelease("ball_shoot");
                Input.ActionPress("move_jab");
            }
            if (remaining != 1) return;

            if (_player.PendingOffensiveInputsForHarness != 2)
            {
                Finish(false, $"fixture had {_player.PendingOffensiveInputsForHarness} pending before the final Recovery tick");
                return;
            }

            if (_scenario == "loss-regain")
            {
                _ball.StateMachine.GoLoose();
                _ball.StateMachine.Catch(1);
                _ball.StateMachine.StartDribble();
            }
            else
            {
                _game.RegisterBasket(1);
                _terminal = true;
            }
            return;
        }

        // Downward tap uses the gamepad map. The horizontal tail uses the keyboard map.
        if (remaining is 6 or 5)
            Input.ParseInputEvent(new InputEventJoypadMotion
            {
                Axis = JoyAxis.RightY, AxisValue = remaining == 6 ? 1f : 0f,
            });
        if (remaining is 3 or 2) Aim(remaining == 3, right: true);
        Input.FlushBufferedEvents();

        // Button edges injected from a physics callback latch on the next tick.
        if (remaining == 2) Input.ActionPress("ball_shoot");
        if (remaining == 1 && _player.PendingOffensiveInputsForHarness != 2)
            Finish(false, "fixture did not fill both slots before the overflow edge");

    }

    // Inject mapped devices through the same Input singleton the player samples.
    // https://docs.godotengine.org/en/4.7/classes/class_input.html#class-input-method-parse-input-event
    private static void Aim(bool pressed, bool right = false)
        => Input.ParseInputEvent(new InputEventKey
        {
            PhysicalKeycode = right ? Key.L : Key.J, Pressed = pressed,
        });

    private static int Frames(CommittedMove move, MovePhase phase) => phase switch
    {
        MovePhase.Startup => move.FrameData.StartupFrames,
        MovePhase.Active => move.FrameData.ActiveFrames,
        MovePhase.Recovery => move.FrameData.RecoveryFrames,
        _ => 0,
    };

    private void Finish(bool pass, string message)
    {
        _finished = true;
        GD.Print($"[offensive-follow-up] {(pass ? "PASS" : "FAIL")} {_scenario}: {message}");
        GetTree().Quit(pass ? 0 : 1);
    }
}
