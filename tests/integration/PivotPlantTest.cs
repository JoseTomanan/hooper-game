using System;
using System.Linq;
using Godot;
using Hooper.Player;

namespace HOOPERGAME.Tests.Integration;

// Headless integration harness for #172's facing latch and #430's moving turns
// (ADR-0016, ADR-0010 amendment). Unit tests (HeadingMathTests) already pin the
// pure HeadingMath.Step state machine; what they CANNOT reach is the live wiring
// this issue actually shipped — a real PlayerController driven by the REAL
// Input singleton through the REAL TickServerOwnPlayer -> SampleMoveInput ->
// Move() path, proving released flicks stop within 4 cm while held large turns
// translate by more than 2 cm before facing completes. It also proves
// that the wired exports (BackTurnSlowFactor/MaxTurnRateDeg/PivotThresholdDeg)
// match the values #172's ADR amendment documents.
//
//   godot --headless --path . res://tests/integration/PivotPlantTest.tscn -- --harness-scenario=exports
//   godot --headless --path . res://tests/integration/PivotPlantTest.tscn -- --harness-scenario=flick-180
//   godot --headless --path . res://tests/integration/PivotPlantTest.tscn -- --harness-scenario=held-135
//   godot --headless --path . res://tests/integration/PivotPlantTest.tscn -- --harness-scenario=no-plant-boundary
//   godot --headless --path . res://tests/integration/PivotPlantTest.tscn -- --harness-scenario=committed-cancel
//   godot --headless --path . res://tests/integration/PivotPlantTest.tscn -- --harness-scenario=movement-matrix
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
// -> Move -> HeadingMath.Step) for the original Input-driven scenarios.
// The input action used for "committed-move cancels
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
// #430 adds movement-matrix: direct fixed-step production Move calls and real
// ReceiveState -> ReconcileFromServer buffered replay, checked against an
// independent authority trajectory. Transport delivery remains covered by
// the existing dual-instance networking harnesses.
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
        if (_scenario == "movement-matrix")
        {
            _player.SetPhysicsProcess(false);
            _player.SetProcess(false);
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;
        _elapsed += delta;
        _frame++;

        switch (_scenario)
        {
            case "movement-matrix":
                // MoveAndSlide reads the engine's current physics delta. Start
                // the synchronous matrix in a physics callback, not _Ready.
                try { RunMovementMatrix(); Finish(0); }
                catch (Exception error) { Fail(error.ToString()); Finish(1); }
                return;
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

        bool backTurnOk = Mathf.IsEqualApprox(p.BackTurnSlowFactor, 0.95f);
        bool maxTurnOk = Mathf.IsEqualApprox(p.MaxTurnRateDeg, 900f);
        bool thresholdOk = Mathf.IsEqualApprox(p.PivotThresholdDeg, 90f);
        bool accelOk = Mathf.IsEqualApprox(p.Accel, 45f);
        bool decelOk = Mathf.IsEqualApprox(p.Decel, 70f);
        bool pass = backTurnOk && maxTurnOk && thresholdOk && accelOk && decelOk;

        if (pass)
        {
            GD.Print("[pivot-plant] PASS exports — BackTurnSlowFactor=0.95, MaxTurnRateDeg=900, " +
                     "PivotThresholdDeg=90, Accel=45, Decel=70.");
        }
        else
        {
            Fail($"exports mismatch: BackTurnSlowFactor={p.BackTurnSlowFactor} (want 0.95), " +
                 $"MaxTurnRateDeg={p.MaxTurnRateDeg} (want 900), PivotThresholdDeg={p.PivotThresholdDeg} (want 90), " +
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
        // #430: a flick may travel for its one held tick, then brakes at Decel.
        // It must not keep travelling because the heading latch is still owed.
        bool stoppedAfterRelease = _pivotSeenActive && _maxDeviationDuringPivot < 0.04f
            && _player.Velocity.Length() < StillEpsilon;
        // Generous band (0.15 - 0.30 s) per issue #172's acceptance criterion,
        // as retuned by a #172 follow-up to the ≈0.20 s target (MaxTurnRateDeg 900,
        // BackTurnSlowFactor 0.95) — engine tick jitter and the non-linear
        // rate's integration make an exact 0.20 s assertion too tight for CI.
        bool withinBand = secondsToComplete >= 0.15 && secondsToComplete <= 0.30;

        bool pass = headingReached && stoppedAfterRelease && withinBand;

        if (pass)
        {
            GD.Print($"[pivot-plant] PASS flick-180 — heading={_player.Heading:F4} (target {Mathf.Pi:F4}), " +
                     $"maxDeviation={_maxDeviationDuringPivot:F5}m, ticks={ticksToComplete} ({secondsToComplete:F3}s).");
        }
        else
        {
            Fail($"flick-180 expected heading≈π, maxDeviation<0.04m, stopped speed<{StillEpsilon}, time in [0.15,0.30]s; got " +
                 $"heading={_player.Heading:F4}, maxDeviation={_maxDeviationDuringPivot:F5}, " +
                 $"ticks={ticksToComplete} ({secondsToComplete:F3}s), headingReached={headingReached}, " +
                 $"stoppedAfterRelease={stoppedAfterRelease}, withinBand={withinBand}.");
        }
        Finish(pass ? 0 : 1);
    }

    // Held >90-degree input travels slowly while its facing debt resolves,
    // then reaches ordinary speed while the stick remains held (#430).
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
        // 2 cm exceeds float/solver noise (~1e-6 m) by four orders of magnitude.
        bool movedDuringTurn = _pivotSeenActive && _maxDeviationDuringPivot > 0.02f;
        float resumedDelta = (_player.GlobalPosition - _completionPos).Length();
        bool movementResumed = resumedDelta > MovedEpsilon;

        bool pass = movedDuringTurn && movementResumed;

        if (pass)
        {
            GD.Print($"[pivot-plant] PASS held-135 — maxDeviationDuringPivot={_maxDeviationDuringPivot:F5}m, " +
                     $"resumedDelta={resumedDelta:F3}m after completion.");
        }
        else
        {
            Fail($"held-135 expected slow displacement during the pivot (>0.02m) then movement to " +
                 $"resume (>{MovedEpsilon}) once held input completed the turn; got " +
                 $"maxDeviationDuringPivot={_maxDeviationDuringPivot:F5}, resumedDelta={resumedDelta:F3}, " +
                 $"movedDuringTurn={movedDuringTurn}, movementResumed={movementResumed}.");
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
        // its ~0.20s natural completion), fire a real "def_steal" press — the
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

        // Release one tick after the press (harmless either way — the move
        // already latched on the press edge and does not need the input held).
        if (_stealPressed && _frame == _stealPressFrame + 1)
        {
            Input.ActionRelease("def_steal");
        }

        // ── Frame-stamp semantics discovered empirically (issue #172 CI repro) ──
        // Input.ActionPress() sets IsActionPressed() true immediately, but its
        // "just pressed" EDGE (what IsActionJustPressed reads) is only visible
        // starting the NEXT physics frame — confirmed by instrumenting
        // SampleMoveInput: pressing at frame N still reads
        // IsActionJustPressed()==false when PlayerController (this test's CHILD
        // node) samples input later in that SAME frame N, and only reads true
        // when it samples again on frame N+1. So BeginCommittedMove (and the
        // pivot-clear inside it) actually runs during the CHILD's frame-N+1
        // processing, not frame N.
        //
        // On top of that one-frame input-edge delay, this harness is the PARENT
        // of the PlayerController it drives, and Godot processes a parent's
        // _PhysicsProcess before its children's within the same frame. So the
        // earliest frame on which the PARENT (this Fail/Pass check) can OBSERVE
        // a change the CHILD made is one frame after the child made it — i.e.
        // frame (stealPressFrame + 1) [input edge lands] + 1 [parent sees it
        // next frame] = stealPressFrame + 2. Checking at +1, as an earlier
        // version of this test did, always read the pivot one tick too early
        // (still latched) and failed on every run, including in CI.
        //
        // +2 is asserted here as the earliest correct frame, not padded further,
        // so the test stays a meaningful proof that the cancel is near-immediate
        // (nowhere close to the ~12-tick natural pivot completion).
        if (_stealPressed && _frame == _stealPressFrame + 2)
        {
            bool cancelled = !_player.IsPivotingInPlace;
            if (cancelled)
            {
                GD.Print($"[pivot-plant] PASS committed-cancel — pivot cancelled by frame {_frame} " +
                         $"(steal pressed at {_stealPressFrame}), well before natural completion.");
            }
            else
            {
                Fail($"committed-cancel expected IsPivotingInPlace==false by frame {_frame} " +
                     $"(two ticks after def_steal was pressed at frame {_stealPressFrame} — one tick for the " +
                     "input-just-pressed edge to become visible, one more for this parent node to observe the " +
                     "child PlayerController's same-tick pivot-clear); pivot is still latched. " +
                     "Either the steal did not Begin() (no ball group present should make IsBallHolder false — " +
                     "check that gate) or BeginCommittedMove's pivot-clear did not run.");
            }
            Finish(cancelled ? 0 : 1);
        }
    }

    private const double MatrixDt = 1.0 / 60;
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static Vector2 Direction(float degrees) => degrees == 180 ? new Vector2(0, -1)
        : degrees == 90 ? new Vector2(1, 0) : HeadingMath.Forward(degrees * MathF.PI / 180);
    private static Vector3 World(Vector2 input) => new(input.X, 0, input.Y);
    private float ModeSpeed(int mode) => mode == 1 ? _player.SprintSpeed : mode == 2 ? _player.ProtectSpeed : _player.MoveSpeed;
    private HeadingMath.HeadingStep Baseline(float yaw, HeadingMath.PivotState pivot, Vector2 input, int mode) =>
        HeadingMath.Step(yaw, pivot, input, MatrixDt, _player.MaxTurnRateDeg * (mode == 1 ? _player.SprintTurnRateScale : 1),
            _player.BackTurnSlowFactor, _player.PivotThresholdDeg);

    // Direct fixed-step calls isolate the shared movement/replay step from Input
    // edge latency. The original scenarios above retain real Input coverage.
    private void RunMovementMatrix()
    {
        Require(Engine.PhysicsTicksPerSecond == 60, "matrix requires the shipped 60 Hz configuration");
        int cases = 0;
        foreach (float angle in new float[] { 45, 90, 91, 120, 135, 170, 179.9f, 180 })
        foreach (int mode in new[] { 0, 1, 2 })
        foreach (bool incoming in new[] { false, true })
        foreach (bool release in new[] { false, true })
        {
            RunAngleCase(angle, mode, incoming, release);
            cases++;
        }
        RunRetargetCase();
        RunTransitionCases();
        RunReplayCases();
        RunContactControl();
        GD.Print($"[pivot-plant] PASS movement-matrix: {cases} angle/mode/momentum/release cases; retarget, transition, actual reconciliation and contact controls");
    }

    private void RunAngleCase(float angle, int mode, bool incoming, bool release)
    {
        float speed = ModeSpeed(mode);
        _player.ResetContactStateForHarness(Vector3.Zero, 0, incoming ? new Vector3(0, 0, speed) : Vector3.Zero);
        float yaw = 0;
        var pivot = HeadingMath.PivotState.None;
        bool sawLatch = false;
        int completion = -1;
        float activeDistance = 0;
        Vector3 ordinaryVelocity = _player.Velocity, ordinaryPosition = Vector3.Zero;
        int releaseStopDeadline = -1;
        string tag = $"angle={angle} mode={mode} incoming={incoming} release={release}";
        for (int tick = 0; tick < 60; tick++)
        {
            Vector2 input = release && tick > 0 ? Vector2.Zero : Direction(angle);
            Vector3 previousVelocity = _player.Velocity, previousPosition = _player.GlobalPosition;
            if (release && tick == 1)
                releaseStopDeadline = tick + (int)Math.Ceiling(previousVelocity.Length() / _player.Decel / MatrixDt);
            var expected = Baseline(yaw, pivot, input, mode);
            yaw = expected.NewYaw; pivot = expected.Pivot;
            _player.Move(input, MatrixDt, sprint: mode == 1, protect: mode == 2);
            var actual = _player.CaptureMovingTurnForHarness();
            Require(actual.Heading == yaw && actual.Pivot == pivot, $"{tag} tick={tick}: yaw/latch differs from unchanged HeadingMath.Step");
            float change = (actual.Velocity - previousVelocity).Length();
            Require(change <= Math.Max(_player.Accel, _player.Decel) * MatrixDt + 1e-5, $"{tag} tick={tick}: velocity snapped by {change}");
            if (actual.Pivot.HasLatch)
            {
                sawLatch = true;
                activeDistance += (actual.Position - previousPosition).Length();
                Require(actual.Velocity.Length() <= Math.Max(.65f * speed, previousVelocity.Length()) + 1e-5f,
                    $"{tag} tick={tick}: turn exceeded the momentum/target envelope");
                if (!incoming) Require(actual.Velocity.Length() <= .65f * speed + 1e-5f, $"{tag}: idle turn exceeded mode ceiling");
            }
            else if (sawLatch && completion < 0)
            {
                completion = tick + 1;
                bool braking = input == Vector2.Zero || previousVelocity.Dot(World(input)) < 0;
                Require(change <= (braking ? _player.Decel : _player.Accel) * MatrixDt + 1e-5,
                    $"{tag}: completion introduced an extra velocity kick");
            }
            if (angle <= 90)
            {
                ordinaryVelocity = MovementMath.ComputeVelocity(ordinaryVelocity, World(input), MatrixDt, speed, _player.Accel, _player.Decel);
                ordinaryPosition += ordinaryVelocity * (float)MatrixDt;
                Require(!actual.Pivot.HasLatch && actual.Velocity.DistanceTo(ordinaryVelocity) < 1e-6f
                    && actual.Position.DistanceTo(ordinaryPosition) < 1e-5f, $"{tag}: <=90 ordinary movement changed");
            }
            if (release && tick >= releaseStopDeadline && releaseStopDeadline >= 0)
                Require(actual.Velocity == Vector3.Zero, $"{tag}: release failed to stop by finite Decel deadline");
            if (completion > 0) Require(!actual.Pivot.HasLatch, $"{tag}: latch restarted after completion");
        }
        if (angle > 90)
        {
            Require(sawLatch && completion > 0, $"{tag}: large turn failed to enter/complete latch");
            if (!incoming && !release)
                Require(activeDistance > .02f, $"{tag}: held idle turn travelled only {activeDistance}, still hard-planted");
        }
        if (!release) Require(Math.Abs(_player.Velocity.Length() - speed) < 1e-5f, $"{tag}: held turn never reached mode speed");
        GD.Print($"[pivot-plant] matrix {tag}: completion={completion}, activeDistance={activeDistance:F6}m finalSpeed={_player.Velocity.Length():F6}m/s");
        if (angle == 180 && mode == 0)
        {
            // Integral d(angle)/[rate*(1-.05*angle/pi)] = -pi/(rate*.05)*ln(.95).
            double integral = -Math.PI / (_player.MaxTurnRateDeg * Math.PI / 180 * (1 - _player.BackTurnSlowFactor))
                * Math.Log(_player.BackTurnSlowFactor);
            Require(completion == 13 && Math.Abs(completion * MatrixDt - integral) <= MatrixDt,
                $"{tag}: half-turn={completion} ticks, integral={integral:F6}s exceeds one-tick quantization");
            GD.Print($"[pivot-plant] 180 ordinary incoming={incoming} release={release}: completion={completion}, integral={integral:F6}s activeDistance={activeDistance:F6}m");
        }
    }

    private void RunRetargetCase()
    {
        _player.ResetContactStateForHarness(Vector3.Zero, 0, Vector3.Zero);
        float yaw = 0;
        var pivot = HeadingMath.PivotState.None;
        bool completed = false;
        for (int tick = 0; tick < 16; tick++)
        {
            Vector2 input = tick < 4 ? Direction(170) : Direction(75);
            var expected = Baseline(yaw, pivot, input, 0);
            yaw = expected.NewYaw; pivot = expected.Pivot;
            _player.Move(input, MatrixDt);
            var actual = _player.CaptureMovingTurnForHarness();
            Require(actual.Heading == yaw && actual.Pivot == pivot, $"retarget tick={tick}: extra yaw or altered latch");
            if (tick == 3) Require(actual.Pivot.HasLatch, "retarget premise: original turn already completed");
            if (tick >= 4 && !actual.Pivot.HasLatch) completed = true;
        }
        Require(completed, "nearby retarget failed to complete");
    }

    private ContactOpponent FarOpponent() => new(new Vector3(50, 0, 50), Vector3.Zero, 0, .5f, 2, 1, 2);
    private void RunTransitionCases()
    {
        foreach (string context in new[] { "stance", "post", "locked" })
        {
            _player.ResetContactStateForHarness(Vector3.Zero, 0, new Vector3(0, 0, 6));
            _player.Move(Direction(180), MatrixDt);
            Require(_player.IsPivotingInPlace && _player.Velocity.Length() > 0, $"{context}: transition lacks moving owed turn");
            bool completed = false;
            for (int tick = 0; tick < 30 && !completed; tick++)
            {
                var before = _player.CaptureMovingTurnForHarness();
                _player.Move(Direction(180), MatrixDt, context == "stance" ? FarOpponent() : null,
                    stance: context == "stance", postHeld: context == "post", postEligible: context == "post",
                    rimSample: new Vector3(0, 0, 100), translationLocked: context == "locked");
                var after = _player.CaptureMovingTurnForHarness();
                var expected = Baseline(before.Heading, before.Pivot, context == "locked" ? Direction(180) : Vector2.Zero, 0);
                Require(after.Heading == expected.NewYaw && after.Pivot == expected.Pivot, $"{context}: owed turn advanced extra yaw");
                if (context == "stance") Require(after.Velocity == Vector3.Zero, "stance moved before/at owed-turn completion");
                else
                {
                    Vector3 expectedVelocity = before.Velocity.MoveToward(Vector3.Zero, _player.Decel * (float)MatrixDt);
                    Require(after.Velocity.DistanceTo(expectedVelocity) < 1e-6f, $"{context}: failed finite braking at Decel");
                    if (tick == 0) Require(after.Velocity.Length() > 0, $"{context}: incoming momentum was hard-zeroed");
                }
                if (context == "post") Require(!after.Established, "post established before/final tick of owed turn");
                completed = !after.Pivot.HasLatch;
            }
            Require(completed, $"{context}: owed turn never completed");
            if (context == "post")
            {
                _player.Move(Direction(180), MatrixDt, postHeld: true, postEligible: true, rimSample: new Vector3(0, 0, 100));
                Require(_player.PostEstablishedForHarness, "post failed to establish on following tick");
            }
        }
    }

    private PlayerController MatrixPlayer(string name, Node parent, bool capsule = false)
    {
        var player = new PlayerController { Name = name, CollisionMask = 0 };
        if (capsule) player.AddChild(new CollisionShape3D { Shape = new CapsuleShape3D { Radius = .5f, Height = 2 }, Position = new Vector3(0, 1, 0) });
        parent.AddChild(player);
        player.SetPhysicsProcess(false); player.SetProcess(false);
        return player;
    }

    private void RunReplayCases()
    {
        foreach (int mode in new[] { 0, 1, 2 })
        foreach (string context in new[] { "ordinary", "stance", "post", "locked" })
        foreach (int ackTick in new[] { 0, 3, 8, 15 })
        {
            // Separate sibling groups keep the replay's real raw-snapshot lookup
            // deterministic; a far opponent supplies stance without touching motion.
            var group = new Node3D(); AddChild(group);
            var authority = MatrixPlayer("3", this);
            var replay = MatrixPlayer("4", group, true);
            var opponent = MatrixPlayer("5", group, true);
            opponent.SeedProductionContactRawForHarness(new Vector3(50, 0, 50), 0);
            int ack = replay.ClearMovingTurnHistoryForHarness() + ackTick;
            authority.ResetContactStateForHarness(Vector3.Zero, 0, new Vector3(0, 0, ModeSpeed(mode)));
            PlayerController.MovingTurnSnapshot snapshot = default;
            for (int tick = 0; tick < 40; tick++)
            {
                if (tick == ackTick) snapshot = authority.CaptureMovingTurnForHarness();
                Vector2 input = tick >= 8 ? Vector2.Zero : Direction(tick >= 4 ? 150 : 180);
                bool transition = tick >= 3;
                var frame = new PredictionBuffer.InputFrame(input, mode == 1, mode == 2,
                    PostureHeld: transition && context == "stance", StanceEligible: true,
                    PostHeld: transition && context == "post", PostEligible: true,
                    RimSample: new Vector3(0, 0, 100), TranslationLocked: transition && context == "locked");
                authority.Move(frame.Movement, MatrixDt, FarOpponent(), frame.Sprint, frame.Protect,
                    frame.PostureHeld, frame.PostHeld, frame.PostEligible, frame.RimSample, frame.TranslationLocked);
                // Record the acknowledged prefix as well: the actual production
                // Acknowledge call must prune it before replaying the suffix.
                replay.RecordMovingTurnForHarness(frame);
                if (tick >= ackTick)
                {
                    // Poison local state: only production reconciliation can recover
                    // the ack heading/latch and replay all historical mode/context bits.
                    replay.ResetContactStateForHarness(new Vector3(10, 0, 10), -1, new Vector3(20, 0, 0));
                    replay.ReconcileMovingTurnForHarness(snapshot, ack, MatrixDt);
                    Require(replay.SprintPendingForHarness == tick - ackTick + 1,
                        $"reconcile ackTick={ackTick} tick={tick}: prefix pruning/suffix retention failed");
                    var expected = authority.CaptureMovingTurnForHarness();
                    var actual = replay.CaptureMovingTurnForHarness();
                    Require(actual.Position.DistanceTo(expected.Position) < 2e-5f && actual.Velocity.DistanceTo(expected.Velocity) < 1e-6f
                        && actual.Heading == expected.Heading && actual.Pivot == expected.Pivot
                        && actual.Stance == expected.Stance && actual.Post == expected.Post && actual.Established == expected.Established,
                        $"reconcile mode={mode} context={context} ackTick={ackTick} tick={tick}: mismatch {actual} vs {expected}");
                }
            }
            authority.Free(); group.Free();
        }
    }

    private void RunContactControl()
    {
        var group = new Node3D(); AddChild(group);
        var mover = MatrixPlayer("6", group, true);
        var opponent = MatrixPlayer("7", group, true);
        opponent.SeedProductionContactRawForHarness(new Vector3(0, 0, -.99f), 0);
        ContactOpponent? sample = mover.MovingTurnOpponentForHarness;
        Require(sample.HasValue, "contact premise: production raw-opponent lookup returned no capsule");
        mover.ResetContactStateForHarness(Vector3.Zero, 0, Vector3.Zero);
        mover.Move(Direction(180), MatrixDt);
        var clear = mover.CaptureMovingTurnForHarness();
        mover.ResetContactStateForHarness(Vector3.Zero, 0, Vector3.Zero);
        mover.Move(Direction(180), MatrixDt, sample);
        var contact = mover.CaptureMovingTurnForHarness();
        Require(clear.Pivot.HasLatch && contact.Pivot == clear.Pivot && contact.Heading == clear.Heading,
            "contact changed the owed heading schedule");
        Require(contact.Position.DistanceTo(clear.Position) > .001f || contact.Velocity.DistanceTo(clear.Velocity) > .001f,
            "actual contact path had no effect compared with identical clear-space moving turn");
        group.Free();
    }

    private void Fail(string message) => GD.PrintErr($"[pivot-plant] FAIL: {message}");

    private void Finish(int code = 1)
    {
        _finished = true;
        GD.Print($"[pivot-plant] RESULT: {(code == 0 ? "PASS" : "FAIL")} (exit {code})");
        GetTree().Quit(code);
    }
}
