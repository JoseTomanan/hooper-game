using Godot;
using Hooper.Player;

namespace Hooper.Ball.Tests;

/// <summary>
/// Unit tests for MovementMath.ComputeVelocity — the pure ground-movement step
/// extracted from PlayerController (issue #37), on the shared prediction/
/// replay path: any divergence here is a netcode bug, not just a feel tweak.
///
/// These tests run without a live Godot instance, using Godot.Vector3 from the
/// GodotSharp NuGet (same pattern as ShotArc/DribbleCycle's tests).
///
/// ── Test naming ──────────────────────────────────────────────────────────────
/// [MethodUnderTest]_[Scenario]_[ExpectedOutcome]
/// Each test contains exactly one logical assertion.
/// </summary>
public class MovementMathTests
{
    // ── Acceleration ──────────────────────────────────────────────────────────

    [Fact]
    public void ComputeVelocity_AcceleratingFromRest_MovesTowardTargetByAccelRate()
    {
        // accel=30, delta=0.1 → max step = 3 m/s; target = wishDir*moveSpeed = 6.
        // Starting at rest, 3 m/s short of target, so the result is exactly 3.
        Vector3 result = MovementMath.ComputeVelocity(
            current: Vector3.Zero, wishDir: new Vector3(1, 0, 0), delta: 0.1,
            moveSpeed: 6f, accel: 30f, decel: 45f);

        Assert.Equal(new Vector3(3, 0, 0), result);
    }

    [Fact]
    public void ComputeVelocity_NearTopSpeed_ClampsAtMoveSpeedWithoutOvershoot()
    {
        // Only 0.1 m/s short of target with a large accel/delta budget (10 m/s) —
        // MoveToward must clamp at the target, not overshoot past it.
        Vector3 result = MovementMath.ComputeVelocity(
            current: new Vector3(5.9f, 0, 0), wishDir: new Vector3(1, 0, 0), delta: 1.0,
            moveSpeed: 6f, accel: 10f, decel: 45f);

        Assert.Equal(new Vector3(6, 0, 0), result);
    }

    // ── Deceleration (zero input) ────────────────────────────────────────────

    [Fact]
    public void ComputeVelocity_ZeroWishDir_DeceleratesTowardZero()
    {
        // wishDir == Vector3.Zero → target = Zero, rate = decel (45).
        // delta=0.1 → max step 4.5 m/s; from 6 m/s that lands at 1.5.
        Vector3 result = MovementMath.ComputeVelocity(
            current: new Vector3(6, 0, 0), wishDir: Vector3.Zero, delta: 0.1,
            moveSpeed: 6f, accel: 30f, decel: 45f);

        Assert.Equal(new Vector3(1.5f, 0, 0), result);
    }

    [Fact]
    public void ComputeVelocity_ZeroWishDir_NearZero_ClampsAtZeroWithoutOvershoot()
    {
        // 0.1 m/s short of stopped, with a decel/delta budget far larger than
        // that — must clamp at zero, not reverse direction past it.
        Vector3 result = MovementMath.ComputeVelocity(
            current: new Vector3(0.1f, 0, 0), wishDir: Vector3.Zero, delta: 1.0,
            moveSpeed: 6f, accel: 30f, decel: 45f);

        Assert.Equal(Vector3.Zero, result);
    }

    // ── Decel > Accel asymmetry ("change of pace", ADR-0003) ────────────────

    [Fact]
    public void ComputeVelocity_DecelExceedsAccel_StopsFasterThanItStartsMoving()
    {
        // Same delta (0.1s), same starting-from-rest-equivalent magnitude:
        // accelerating from 0 toward 6 covers 3 m/s of distance (accel=30);
        // decelerating from 6 toward 0 covers 4.5 m/s of distance (decel=45).
        // Pinning decel's larger distance is what makes a stop read as a
        // deliberate change of pace rather than drift (CLAUDE.md §1, ADR-0003).
        Vector3 accelerating = MovementMath.ComputeVelocity(
            current: Vector3.Zero, wishDir: new Vector3(1, 0, 0), delta: 0.1,
            moveSpeed: 6f, accel: 30f, decel: 45f);
        Vector3 decelerating = MovementMath.ComputeVelocity(
            current: new Vector3(6, 0, 0), wishDir: Vector3.Zero, delta: 0.1,
            moveSpeed: 6f, accel: 30f, decel: 45f);

        float accelDistance = accelerating.X;          // 0 → 3
        float decelDistance = 6f - decelerating.X;      // 6 → 1.5

        Assert.True(decelDistance > accelDistance);
    }

    // ── Determinism ───────────────────────────────────────────────────────────

    [Fact]
    public void ComputeVelocity_SameInputs_ProducesIdenticalResult()
    {
        // Required for the reconciliation replay (PlayerController.cs) to be
        // bit-identical across server simulation, client prediction, and replay.
        Vector3 first = MovementMath.ComputeVelocity(
            current: new Vector3(2, 0, -1), wishDir: new Vector3(0, 0, 1), delta: 0.0167,
            moveSpeed: 6f, accel: 30f, decel: 45f);
        Vector3 second = MovementMath.ComputeVelocity(
            current: new Vector3(2, 0, -1), wishDir: new Vector3(0, 0, 1), delta: 0.0167,
            moveSpeed: 6f, accel: 30f, decel: 45f);

        Assert.Equal(first, second);
    }
}
