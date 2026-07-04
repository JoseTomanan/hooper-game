using System.Linq;
using Godot;
using Hooper.Player;
using Hooper.Moves;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #198's moving crossover — momentum-
// aware burst with a left-stick exit vector (ADR-0016, ADR-0003 amendment).
// CrossoverBurstMathTests already pin the pure composition function; what
// they CANNOT reach is the live wiring this issue actually shipped — a real
// PlayerController driven by the REAL Input singleton through the REAL
// TickServerOwnPlayer -> SampleMoveInput -> TickCommittedMoveBehavior path,
// proving Startup's GatherDecel genuinely bleeds (not hard-zeroes) surviving
// momentum and that CrossoverBurstMath's composed velocity is what actually
// lands in Velocity/MoveAndSlide, not just that the pure function returns the
// right numbers in isolation.
//
//   godot --headless --path . res://tests/integration/MovingCrossoverTest.tscn -- --harness-scenario=retains-speed
//   godot --headless --path . res://tests/integration/MovingCrossoverTest.tscn -- --harness-scenario=stationary-forward-exit
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//
// ── Why a single offline instance is enough ─────────────────────────────────
// Same reasoning as PivotPlantTest/StealTurnoverTest: with no MultiplayerPeer
// assigned, Godot uses OfflineMultiplayerPeer (is_server() hardcoded true,
// unique_id 1). Naming the player "1" makes it BOTH IsServer and
// IsLocalPlayer, so _PhysicsProcess dispatches to TickServerOwnPlayer every
// tick — the authoritative, zero-lag path that reads real local hardware
// input directly (ReadInput()), which is exactly the "local, real hardware"
// exit-vector source TickCommittedMoveBehavior's exitVectorSample parameter
// takes for this role (see PlayerController's doc on that parameter).
//
// ── What is explicitly NOT attempted here (reported, not faked) ────────────
// The doubt-driven netcode question this issue's PR resolves — whether the
// SERVER's copy of a REMOTE player derives the SAME exit vector via
// _pendingRawStick (fed by the new SubmitInput trailing floats) — has no
// dual-instance harness pattern to reuse here (same limitation PivotPlantTest
// names for reconciliation). That channel is a straightforward field
// assignment with no branching logic to discriminate old-vs-new behavior the
// way StealTurnoverTest's shadow-client trick needs, so it is left to code
// review of the wiring plus this file proving the SAME composition function
// the server-remote path also calls.
//
// ── Scenarios ────────────────────────────────────────────────────────────
// retains-speed: drives the player to speed, throws a crossover with the
//   left stick released to NEUTRAL by Active-entry (the "driving forward +
//   neutral exit" row — CrossoverBurstMath's pass-through branch), and
//   asserts speed stays > 0 through BOTH Startup (proving the gather bleeds,
//   not hard-zeroes) and Active (proving the survivor is redirected, not
//   dropped) — the exact discriminator against the pre-#198 hard-zero model.
// stationary-forward-exit: throws a crossover from a dead stop with the left
//   stick pushed FORWARD by Active-entry, and asserts Velocity gains a
//   forward (+Z) component on Active-entry — the "stationary + push forward"
//   row, impossible under the pre-#198 pure-lateral-only burst model.
public partial class MovingCrossoverTest : Node
{
    private const double TimeoutSeconds = 10.0;

    // Ticks to hold move_forward before throwing the crossover, enough to
    // approach top speed (Accel=45 m/s², MoveSpeed=6 m/s -> ~0.13s -> ~8
    // ticks to reach top speed; 20 gives comfortable margin).
    private const int DriveTicks = 20;

    // RightStickGestureRecognizer commits a Crossover on the tick
    // ticksAboveThreshold > FeintWindowTicks (default 4) -> the 5th
    // consecutive above-threshold tick. Hold a couple extra ticks of margin.
    private const int FlickHoldTicks = 7;

    private string _scenario = "retains-speed";
    private PlayerController _player;
    private int _frame;
    private double _elapsed;
    private bool _finished;

    private bool _flickStarted;
    private int _flickStartFrame = -1;
    private bool _releasedForNeutralExit;
    private bool _pushedForwardExit;

    private bool _sawStartup;
    private bool _sawActive;
    private float _minSpeedDuringMove = float.MaxValue;
    private float _maxForwardDuringActive = float.MinValue;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "retains-speed");
        GD.Print($"[moving-crossover] scenario={_scenario} booting headless…");

        // Bare PlayerController, named "1" so OfflineMultiplayerPeer's
        // unique_id==1 makes it both IsServer and IsLocalPlayer — the
        // TickServerOwnPlayer path (see class doc). No Players/Ball wrapper
        // needed: nothing in this scenario touches the ball.
        _player = new PlayerController { Name = "1" };
        AddChild(_player);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        _frame++;

        switch (_scenario)
        {
            case "retains-speed": TickRetainsSpeed(); break;
            case "stationary-forward-exit": TickStationaryForwardExit(); break;
            case "hesitation-still-hard-zeroes": TickHesitationStillHardZeroes(); break;
            default:
                Fail($"unknown scenario '{_scenario}'.");
                Finish();
                return;
        }

        if (!_finished && _elapsed > TimeoutSeconds)
        {
            Fail($"timed out at frame {_frame} without reaching a verdict.");
            Finish();
        }
    }

    // ── Scenario: driving forward, neutral exit — momentum bleeds, not
    // zeroes, and the survivor carries through Active ─────────────────────
    private void TickRetainsSpeed()
    {
        if (_frame == 1)
        {
            Input.ActionPress("move_forward", 1.0f);
            GD.Print($"[moving-crossover] frame {_frame}: pressed move_forward to build speed");
        }

        if (_frame == DriveTicks && !_flickStarted)
        {
            _flickStarted = true;
            _flickStartFrame = _frame;
            Input.ActionPress("aim_right", 1.0f); // triggers a right-stick flick, +1 (crossover vs HandSide.Left default)
            GD.Print($"[moving-crossover] frame {_frame}: speed={_player.Velocity.Length():F2} m/s, pressed aim_right");
        }

        if (_flickStarted && _frame == _flickStartFrame + FlickHoldTicks)
        {
            Input.ActionRelease("aim_right");
            GD.Print($"[moving-crossover] frame {_frame}: released aim_right (crossover should have committed)");
        }

        // Release the left stick to neutral once Startup is confirmed
        // underway, well before Active begins, so the exit vector sampled at
        // Active-entry reads neutral — isolating the surviving-momentum
        // pass-through row (no added burst impulse) from the exit-driven
        // burst tested by the other scenario.
        var (phase, _) = _player.DisplayMove();
        if (phase == MovePhase.Startup && !_releasedForNeutralExit)
        {
            _releasedForNeutralExit = true;
            Input.ActionRelease("move_forward");
            GD.Print($"[moving-crossover] frame {_frame}: Startup confirmed, released move_forward for a neutral exit");
        }

        TrackSpeedDuringMove(phase);

        if (VerdictReadyRetainsSpeed(phase))
        {
            VerdictRetainsSpeed();
        }
    }

    private bool VerdictReadyRetainsSpeed(MovePhase phase)
    {
        // Ready once we have observed the FULL Startup+Active window and the
        // machine has moved on to Recovery (or beyond).
        return _sawStartup && _sawActive && phase == MovePhase.Recovery;
    }

    private void VerdictRetainsSpeed()
    {
        // The pre-#198 model hard-zeroed Velocity every Startup tick and
        // this scenario's neutral exit adds no burst impulse in Active, so
        // ANY nonzero minimum speed observed across the whole Startup+Active
        // window is proof the gather bleeds (never hits an exact hard zero)
        // and the survivor is redirected through Active rather than dropped.
        bool pass = _sawStartup && _sawActive && _minSpeedDuringMove > 0f;

        if (pass)
        {
            GD.Print($"[moving-crossover] PASS retains-speed — minSpeedDuringMove={_minSpeedDuringMove:F3} m/s " +
                     "(never hit zero across Startup+Active).");
        }
        else
        {
            Fail($"retains-speed expected minSpeedDuringMove > 0 across Startup+Active; got " +
                 $"{_minSpeedDuringMove:F3} (sawStartup={_sawStartup}, sawActive={_sawActive}).");
        }
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: stationary, push-forward exit — the burst gains a forward
    // component impossible under the pre-#198 pure-lateral model ──────────
    private void TickStationaryForwardExit()
    {
        if (_frame == 1)
        {
            _flickStarted = true;
            _flickStartFrame = _frame;
            Input.ActionPress("aim_right", 1.0f);
            GD.Print($"[moving-crossover] frame {_frame}: pressed aim_right (player stationary)");
        }

        if (_flickStarted && _frame == _flickStartFrame + FlickHoldTicks)
        {
            Input.ActionRelease("aim_right");
            GD.Print($"[moving-crossover] frame {_frame}: released aim_right (crossover should have committed)");
        }

        var (phase, _) = _player.DisplayMove();

        // Push the left stick toward the player's CURRENT forward facing once
        // Startup is confirmed underway, so it is held by the time Active
        // begins (model A: snapshotted at Active-entry). The player never
        // moved before this (Move() is what advances Heading, and it is
        // skipped throughout a committed move), so Heading is still its
        // engine default of 0 — which is the WORLD direction "move_backward"
        // drives (PivotPlantTest's own TickFlick180 comment notes the same
        // thing: "Heading starts at 0... exactly the desiredYaw
        // 'move_backward' alone would produce"). Pressing "move_backward"
        // here is therefore the exit vector that is actually ALIGNED with
        // this stationary player's forward-facing axis — the real property
        // CrossoverBurstMath's forward/right decomposition cares about, not
        // the movement-key's arcade label.
        if (phase == MovePhase.Startup && !_pushedForwardExit)
        {
            _pushedForwardExit = true;
            Input.ActionPress("move_backward", 1.0f);
            GD.Print($"[moving-crossover] frame {_frame}: Startup confirmed, pushed the heading-forward-aligned exit vector");
        }

        if (phase == MovePhase.Active)
        {
            _sawActive = true;
            if (_player.Velocity.Z > _maxForwardDuringActive)
                _maxForwardDuringActive = _player.Velocity.Z;
        }
        else if (phase == MovePhase.Startup)
        {
            _sawStartup = true;
        }

        if (_sawStartup && _sawActive && phase == MovePhase.Recovery)
        {
            VerdictStationaryForwardExit();
        }
    }

    private void VerdictStationaryForwardExit()
    {
        // The pre-#198 model's Active burst had zero Z component by
        // construction (a pure lateral BurstWorldDir vector) for ANY flick
        // sign, so any meaningfully positive forward component proves the
        // exit-vector-driven forward burst actually fired.
        const float ForwardEpsilon = 0.5f; // well above float noise, well below ForwardBurstScale's default (9)
        bool pass = _sawStartup && _sawActive && _maxForwardDuringActive > ForwardEpsilon;

        if (pass)
        {
            GD.Print($"[moving-crossover] PASS stationary-forward-exit — maxForwardDuringActive={_maxForwardDuringActive:F3} m/s.");
        }
        else
        {
            Fail($"stationary-forward-exit expected a forward (+Z) component > {ForwardEpsilon} during Active; got " +
                 $"{_maxForwardDuringActive:F3} (sawStartup={_sawStartup}, sawActive={_sawActive}).");
        }
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: driving forward into a HESITATION (not a crossover) —
    // Startup's hybrid gather is scoped to Crossover ONLY (ADR-0003
    // amendment); every other committed move must keep the original
    // instant-zero plant. This is the regression guard for that scoping —
    // a blanket (ungated) bleed would let a driving player slide through a
    // hesitation's "stand still and sell the fake" instead of planting dead.
    private void TickHesitationStillHardZeroes()
    {
        if (_frame == 1)
        {
            Input.ActionPress("move_forward", 1.0f);
            GD.Print($"[moving-crossover] frame {_frame}: pressed move_forward to build speed");
        }

        if (_frame == DriveTicks && !_flickStarted)
        {
            _flickStarted = true;
            _flickStartFrame = _frame;
            // aim_left (flickSign -1) flicks TOWARD the ball hand (default
            // HandSide.Left), which HandStateResolver.IsCrossover classifies
            // as a Hesitation, not a Crossover — see that class's truth table.
            Input.ActionPress("aim_left", 1.0f);
            GD.Print($"[moving-crossover] frame {_frame}: speed={_player.Velocity.Length():F2} m/s, pressed aim_left (hesitation)");
        }

        if (_flickStarted && _frame == _flickStartFrame + FlickHoldTicks)
        {
            Input.ActionRelease("aim_left");
            GD.Print($"[moving-crossover] frame {_frame}: released aim_left (hesitation should have committed)");
        }

        var (phase, _) = _player.DisplayMove();
        if (phase == MovePhase.Startup)
        {
            _sawStartup = true;
            // Hard-zero must still hold EVERY Startup tick for a non-Crossover
            // move — unlike TrackMinSpeed's "never hits zero" proof for the
            // crossover, here we require Velocity to actually BE zero.
            if (_player.Velocity.LengthSquared() > 0.0001f)
            {
                Fail($"hesitation Startup expected Velocity == 0 (unchanged pre-#198 hard-zero plant); " +
                     $"got {_player.Velocity} at frame {_frame} — the gather-bleed leaked into a non-Crossover move.");
                Finish();
                return;
            }
        }
        else if (phase == MovePhase.Active)
        {
            _sawActive = true;
        }

        if (_sawStartup && _sawActive && phase == MovePhase.Recovery)
        {
            GD.Print("[moving-crossover] PASS hesitation-still-hard-zeroes — Velocity stayed exactly 0 through Startup.");
            Finish(0);
        }
    }

    // ── Shared bookkeeping ───────────────────────────────────────────────
    private void TrackSpeedDuringMove(MovePhase phase)
    {
        if (phase == MovePhase.Startup)
        {
            _sawStartup = true;
            TrackMinSpeed();
        }
        else if (phase == MovePhase.Active)
        {
            _sawActive = true;
            TrackMinSpeed();
        }
    }

    private void TrackMinSpeed()
    {
        float speed = _player.Velocity.Length();
        if (speed < _minSpeedDuringMove) _minSpeedDuringMove = speed;
    }

    private void Fail(string message) => GD.PrintErr($"[moving-crossover] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[moving-crossover] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
