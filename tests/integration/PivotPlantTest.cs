using System.Linq;
using Godot;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for issue #172's flick-to-latch in-place pivot
// (ADR-0016, ADR-0010 amendment). Unit tests (HeadingMathTests) already pin the
// pure HeadingMath.Step state machine; what they CANNOT reach is the live wiring
// this issue actually shipped — a real PlayerController driven by the REAL
// Input singleton through the REAL TickServerOwnPlayer -> SampleMoveInput ->
// Move() path, proving the plant/pivot gate actually withholds MoveAndSlide()
// displacement (not just that the pure function returns the right bool), and
// that the wired exports (BackTurnSlowFactor/MaxTurnRateDeg/PivotThresholdDeg)
// match the values #172's ADR amendment documents.
//
//   godot --headless --path . res://tests/integration/PivotPlantTest.tscn -- --harness-scenario=exports
//   godot --headless --path . res://tests/integration/PivotPlantTest.tscn -- --harness-scenario=flick-180
//   godot --headless --path . res://tests/integration/PivotPlantTest.tscn -- --harness-scenario=held-135
//   godot --headless --path . res://tests/integration/PivotPlantTest.tscn -- --harness-scenario=no-plant-boundary
//   godot --headless --path . res://tests/integration/PivotPlantTest.tscn -- --harness-scenario=committed-cancel
//   Exit: 0 = PASS, 1 = FAIL (via GetTree().Quit) — the ADR-0016 exit-code contract.
//
// ── Why real Input.ActionPress/ActionRelease, not a Move()-calling seam ────
// Unlike StealTurnoverTest (which needs a seam because RequestBeginMove is
// RPC/sender-id gated and there is no second peer to deliver it), the pivot's
// entry point IS local hardware input on the host's own player — exactly the
// TickServerOwnPlayer path a listen-server host already runs. Input.ActionPress/
// ActionRelease are Godot's own supported mechanism for driving the Input
// singleton in automated tests (no display/window needed), so this harness
// exercises the UNMODIFIED production call chain (SampleMoveInput -> ReadInput
// -> Move -> HeadingMath.Step) with no test-only seam at all for the movement
// scenarios. The one seam this file DOES lean on for "committed-move cancels
// the pivot" is the "def_steal" input action itself — also real, not a seam —
// which reaches BeginCommittedMove (the shared Begin()-then-clear-pivot path)
// with no Ball needed (IsBallHolder degrades safely to false with no "ball"
// group node present).
//
// ── Why a single offline instance is enough ─────────────────────────────────
// With no MultiplayerPeer assigned, Godot uses OfflineMultiplayerPeer (is_server()
// hardcoded true, unique_id 1) — same reasoning as StealTurnoverTest/OobTurnoverTest.
// Naming the player "1" makes it BOTH IsServer and IsLocalPlayer, so
// _PhysicsProcess dispatches to TickServerOwnPlayer every tick — the authoritative,
// zero-lag path a listen-server host runs, and the same Move() the client
// prediction and reconciliation replay call identically (ADR-0002/ADR-0010).
//
// ── What is explicitly NOT attempted here (reported, not faked) ────────────
// "client/server no-divergence": there is no dual-instance harness pattern this
// issue can reuse. NetStateSyncTest/run-net-*.sh prove generic wire delivery;
// StealTurnoverTest's shadow-client trick is specific to
// CommittedMoveMachine.ShouldForceRecovery, a FORCE-CORRECTION branch that has
// no analogue here — Heading/_pivot are unconditionally snapped from the
// broadcast every reconcile (ReconcileFromServer's pre-replay snap), a plain
// field copy with no decision logic to discriminate old-vs-new behavior the way
// the steal shadow does. Standing up a second headless process only to assert
// "an assignment statement assigns" would not be a meaningful regression net,
// so it is left to the existing unit tests (HeadingMathTests' determinism) and
// code review of the snap-then-replay wiring, per this file's own doc comment.
public partial class PivotPlantTest : Node
{
    private const double TimeoutSeconds = 10.0;

    // Position deltas smaller than this (metres) count as "did not move" —
    // generous relative to float noise, tight relative to any real step
    // (MoveSpeed 6 m/s means even 1 tick of movement covers several cm).
    private const float StillEpsilon = 0.01f;

    // Position delta required to count as "movement has resumed" after a
    // pivot completes — well above StillEpsilon, comfortably below what a
    // few ticks of acceleration from 0 actually covers.
    private const float MovedEpsilon = 0.05f;

    // Heading-reached tolerance in radians — matches HeadingMath's own
    // AngleEpsilon order of magnitude but a little looser, since we are
    // reading the value out through a whole tick of engine plumbing.
    private const float HeadingEpsilon = 0.01f;

    private string _scenario = "flick-180";
    private PlayerController _player;
    private int _frame;
    private double _elapsed;
    private bool _finished;

    // Shared "flick" scenario state (flick-180, committed-cancel both drive a
    // held-then-released 180-ish flick).
    private Vector3 _flickStartPos;
    private int _flickStartFrame = -1;
    private bool _pivotSeenActive;
    private float _maxDeviationDuringPivot;
    private int _completionFrame = -1;
    private Vector3 _completionPos;

    // held-135 scenario state.
    private int _resumeCheckFrame = -1;

    // committed-cancel scenario state.
    private bool _stealPressed;
    private int _stealPressFrame = -1;

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs().Concat(OS.GetCmdlineArgs()).ToArray();
        _scenario = HarnessArgs.ReadArg(args, "--harness-scenario", "flick-180");
        GD.Print($"[pivot-plant] scenario={_scenario} booting headless…");

        if (_scenario == "exports")
        {
            RunExportsCheck();
            return;
        }

        // Bare PlayerController, named "1" so OfflineMultiplayerPeer's
        // unique_id==1 makes it both IsServer and IsLocalPlayer — the
        // TickServerOwnPlayer path (see class doc). No Players/Ball wrapper
        // needed: SampleMoveInput's ball-holder read degrades to false with
        // no "ball" group node, and no other code path here touches the ball.
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
            case "flick-180": TickFlick180(); break;
            case "held-135": TickHeld135(); break;
            case "no-plant-boundary": TickNoPlantBoundary(); break;
            case "committed-cancel": TickCommittedCancel(); break;
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

    // ── Scenario: exports match the #172 ADR-amended defaults ──────────────
    private void RunExportsCheck()
    {
        // A bare, never-added-to-tree PlayerController still carries its
        // exported defaults from the C# property initializers (same reasoning
        // OobTurnoverTest's wall-placement check uses for BallController).
        var p = new PlayerController();

        bool backTurnOk = Mathf.IsEqualApprox(p.BackTurnSlowFactor, 0.90f);
        bool maxTurnOk = Mathf.IsEqualApprox(p.MaxTurnRateDeg, 530f);
        bool thresholdOk = Mathf.IsEqualApprox(p.PivotThresholdDeg, 90f);
        bool accelOk = Mathf.IsEqualApprox(p.Accel, 45f);
        bool decelOk = Mathf.IsEqualApprox(p.Decel, 70f);
        bool pass = backTurnOk && maxTurnOk && thresholdOk && accelOk && decelOk;

        if (pass)
        {
            GD.Print("[pivot-plant] PASS exports — BackTurnSlowFactor=0.90, MaxTurnRateDeg=530, " +
                     "PivotThresholdDeg=90, Accel=45, Decel=70.");
        }
        else
        {
            Fail($"exports mismatch: BackTurnSlowFactor={p.BackTurnSlowFactor} (want 0.90), " +
                 $"MaxTurnRateDeg={p.MaxTurnRateDeg} (want 530), PivotThresholdDeg={p.PivotThresholdDeg} (want 90), " +
                 $"Accel={p.Accel} (want 45), Decel={p.Decel} (want 70).");
        }
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: ~180° flick, held 2 ticks then released ───────────────────
    // Heading starts at 0 (the property default), which is exactly the
    // desiredYaw "move_backward" alone would produce — so pressing
    // "move_forward" instead is already a ~180° reversal from frame 1, no
    // priming tick needed.
    private void TickFlick180()
    {
        if (_flickStartFrame < 0)
        {
            _flickStartFrame = _frame;
            _flickStartPos = _player.GlobalPosition;
            Input.ActionPress("move_forward", 1.0f);
            GD.Print($"[pivot-plant] frame {_frame}: pressed move_forward (~180° flick) at pos {_flickStartPos}");
        }

        // Release on the tick after the press (_flickStartFrame + 1) — the
        // "flick" (one tick held, then released); the pivot keeps resolving
        // to completion per HeadingMath.Step's stick-released branch.
        if (_frame == _flickStartFrame + 1)
        {
            Input.ActionRelease("move_forward");
            GD.Print($"[pivot-plant] frame {_frame}: released move_forward");
        }

        if (_player.IsPivotingInPlace)
        {
            _pivotSeenActive = true;
            float dev = (_player.GlobalPosition - _flickStartPos).Length();
            if (dev > _maxDeviationDuringPivot) _maxDeviationDuringPivot = dev;
        }
        else if (_pivotSeenActive && _completionFrame < 0)
        {
            _completionFrame = _frame;
            _completionPos = _player.GlobalPosition;
        }

        if (_completionFrame >= 0)
        {
            VerdictFlick180();
        }
    }

    private void VerdictFlick180()
    {
        int ticksToComplete = _completionFrame - _flickStartFrame;
        double secondsToComplete = ticksToComplete / (double)Engine.PhysicsTicksPerSecond;

        // HeadingMath.RotateTowardYaw returns the exact target yaw (not just
        // "close enough") on the tick it's reached (see its early-out branch),
        // so a plain difference check is sufficient here — no wraparound
        // handling needed since the target itself (π) is already normalized.
        bool headingReached = Mathf.Abs(_player.Heading - Mathf.Pi) < HeadingEpsilon;
        bool stayedPlanted = _pivotSeenActive && _maxDeviationDuringPivot < StillEpsilon;
        // Generous band (0.25 - 0.50 s) per issue #172's acceptance criterion —
        // engine tick jitter and the non-linear rate's integration make an
        // exact 0.35s assertion too tight for CI.
        bool withinBand = secondsToComplete >= 0.25 && secondsToComplete <= 0.50;

        bool pass = headingReached && stayedPlanted && withinBand;

        if (pass)
        {
            GD.Print($"[pivot-plant] PASS flick-180 — heading={_player.Heading:F4} (target {Mathf.Pi:F4}), " +
                     $"maxDeviation={_maxDeviationDuringPivot:F5}m, ticks={ticksToComplete} ({secondsToComplete:F3}s).");
        }
        else
        {
            Fail($"flick-180 expected heading≈π, maxDeviation<{StillEpsilon}, time in [0.25,0.50]s; got " +
                 $"heading={_player.Heading:F4}, maxDeviation={_maxDeviationDuringPivot:F5}, " +
                 $"ticks={ticksToComplete} ({secondsToComplete:F3}s), headingReached={headingReached}, " +
                 $"stayedPlanted={stayedPlanted}, withinBand={withinBand}.");
        }
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: held input >90° (135°) — zero displacement until reached,
    // then displacement resumes while the stick is STILL held ─────────────
    private void TickHeld135()
    {
        if (_flickStartFrame < 0)
        {
            _flickStartFrame = _frame;
            _flickStartPos = _player.GlobalPosition;
            // move_left + move_forward, held (never released) -> a diagonal
            // ~135° reversal from the default Heading==0 baseline.
            Input.ActionPress("move_left", 1.0f);
            Input.ActionPress("move_forward", 1.0f);
            GD.Print($"[pivot-plant] frame {_frame}: pressed move_left+move_forward (held ~135°) at pos {_flickStartPos}");
        }

        if (_player.IsPivotingInPlace)
        {
            _pivotSeenActive = true;
            float dev = (_player.GlobalPosition - _flickStartPos).Length();
            if (dev > _maxDeviationDuringPivot) _maxDeviationDuringPivot = dev;
        }
        else if (_pivotSeenActive && _completionFrame < 0)
        {
            _completionFrame = _frame;
            _completionPos = _player.GlobalPosition;
            _resumeCheckFrame = _frame + 15; // give ~0.25s of continued held input to accelerate
            GD.Print($"[pivot-plant] frame {_frame}: pivot completed (input still held), heading={_player.Heading:F4}");
        }

        if (_completionFrame >= 0 && _frame >= _resumeCheckFrame)
        {
            VerdictHeld135();
        }
    }

    private void VerdictHeld135()
    {
        bool stayedPlanted = _pivotSeenActive && _maxDeviationDuringPivot < StillEpsilon;
        float resumedDelta = (_player.GlobalPosition - _completionPos).Length();
        bool movementResumed = resumedDelta > MovedEpsilon;

        bool pass = stayedPlanted && movementResumed;

        if (pass)
        {
            GD.Print($"[pivot-plant] PASS held-135 — maxDeviationDuringPivot={_maxDeviationDuringPivot:F5}m, " +
                     $"resumedDelta={resumedDelta:F3}m after completion.");
        }
        else
        {
            Fail($"held-135 expected zero displacement during the pivot (<{StillEpsilon}) then movement to " +
                 $"resume (>{MovedEpsilon}) once held input completed the turn; got " +
                 $"maxDeviationDuringPivot={_maxDeviationDuringPivot:F5}, resumedDelta={resumedDelta:F3}, " +
                 $"stayedPlanted={stayedPlanted}, movementResumed={movementResumed}.");
        }
        Finish(pass ? 0 : 1);
    }

    // ── Scenario: ~85° input never plants; movement is immediate ───────────
    // A pure diagonal-ish input well clear of the 90° threshold (forward-ish,
    // strictly below it), so the assertion doesn't depend on the same-ulp
    // rounding an exactly-90° probe would need between this harness's input
    // construction and HeadingMath.Step's internal threshold-in-radians
    // conversion. The exact-90° knife-edge itself (strict > threshold gates
    // the latch) is pinned precisely by HeadingMathTests' unit tests, which
    // can share the exact same derivation on both sides of the comparison —
    // this harness only needs to prove the wiring doesn't plant for an
    // unambiguously forward-ish turn.
    private void TickNoPlantBoundary()
    {
        if (_flickStartFrame < 0)
        {
            _flickStartFrame = _frame;
            _flickStartPos = _player.GlobalPosition;
            // move_left alone gives Atan2(-1, 0) = -90° exactly (the old
            // boundary probe). Blending in a small move_backward component
            // (backward - forward on the y-axis) pulls the resulting desired
            // yaw to ≈ -85° — the same "turning left-ish" direction, just
            // safely short of the 90° threshold. Input.GetVector's deadzone
            // rescaling only ever changes the vector's LENGTH, never its
            // direction, so the angle these two strengths produce is exact.
            Input.ActionPress("move_left", 0.9962f);     // sin(85°)
            Input.ActionPress("move_backward", 0.08716f); // cos(85°)
            GD.Print($"[pivot-plant] frame {_frame}: pressed move_left+move_backward (~85°, safely below the no-plant boundary) at pos {_flickStartPos}");
        }

        if (_player.IsPivotingInPlace)
        {
            Fail($"no-plant-boundary: IsPivotingInPlace became true at frame {_frame} for a diff of ~85°, " +
                 "well below the 90° threshold HeadingMath.Step documents as forward-ish (no plant).");
            Finish();
            return;
        }

        // Movement must be immediate — no gating tick at all. Give a handful
        // of ticks of acceleration ramp-up (MovementMath's Accel is not
        // instant) before requiring MovedEpsilon of displacement.
        if (_frame == _flickStartFrame + 8)
        {
            float delta = (_player.GlobalPosition - _flickStartPos).Length();
            bool pass = delta > MovedEpsilon;
            if (pass)
                GD.Print($"[pivot-plant] PASS no-plant-boundary — never planted, moved {delta:F3}m within 8 ticks.");
            else
                Fail($"no-plant-boundary expected >{MovedEpsilon}m of movement within 8 ticks of a 90°-exactly " +
                     $"held turn (no plant should ever gate it); got {delta:F3}m.");
            Finish(pass ? 0 : 1);
        }
    }

    // ── Scenario: beginning a committed move (def_steal) cancels an
    // in-progress pivot, well before it would have completed naturally ─────
    private void TickCommittedCancel()
    {
        if (_flickStartFrame < 0)
        {
            _flickStartFrame = _frame;
            _flickStartPos = _player.GlobalPosition;
            Input.ActionPress("move_forward", 1.0f); // ~180° flick, same setup as flick-180
            GD.Print($"[pivot-plant] frame {_frame}: pressed move_forward (~180° flick) at pos {_flickStartPos}");
        }

        if (_frame == _flickStartFrame + 1)
        {
            Input.ActionRelease("move_forward");
        }

        // Once the pivot is confirmed underway (a few ticks in, well before
        // its ~0.35s natural completion), fire a real "def_steal" press — the
        // same production input SampleMoveInput reads — while NOT holding the
        // ball (no "ball" group node exists, so IsBallHolder is false).
        if (!_stealPressed && _frame == _flickStartFrame + 4)
        {
            if (!_player.IsPivotingInPlace)
            {
                Fail($"committed-cancel setup invalid: pivot was not still active at frame {_frame} " +
                     "(the flick completed before def_steal could be fired) — widen the setup window.");
                Finish();
                return;
            }
            Input.ActionPress("def_steal", 1.0f);
            _stealPressed = true;
            _stealPressFrame = _frame;
            GD.Print($"[pivot-plant] frame {_frame}: pressed def_steal while pivoting (IsPivotingInPlace=true)");
            return;
        }

        if (_stealPressed && _frame == _stealPressFrame + 1)
        {
            Input.ActionRelease("def_steal");

            // BeginCommittedMove clears _pivot the SAME tick Begin() succeeds
            // (PlayerController.cs) — so by the very next physics tick after
            // the press, the pivot must already be cancelled, well short of
            // the ~21-tick natural completion a 180° flick would otherwise take.
            bool cancelled = !_player.IsPivotingInPlace;
            if (cancelled)
            {
                GD.Print($"[pivot-plant] PASS committed-cancel — pivot cancelled by frame {_frame} " +
                         $"(steal pressed at {_stealPressFrame}), well before natural completion.");
            }
            else
            {
                Fail($"committed-cancel expected IsPivotingInPlace==false by frame {_frame} " +
                     $"(one tick after def_steal was pressed at frame {_stealPressFrame}); pivot is still latched. " +
                     "Either the steal did not Begin() (no ball group present should make IsBallHolder false — " +
                     "check that gate) or BeginCommittedMove's pivot-clear did not run.");
            }
            Finish(cancelled ? 0 : 1);
        }
    }

    private void Fail(string message) => GD.PrintErr($"[pivot-plant] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[pivot-plant] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
