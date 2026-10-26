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
    [Fact]
    public void ComputeTurnVelocity_Reversal_BrakesFiniteMomentumBeforeBuildingReverseSpeed()
    {
        Vector3 result = MovementMath.ComputeTurnVelocity(new Vector3(0, 0, 6),
            new Vector3(0, 0, -1), 1.0 / 60, 6, 45, 70, MathF.PI);
        Assert.InRange(result.Z, 4.83332f, 4.83335f);
        Assert.True(result.Z > 0, "A reverse request must retain incoming momentum on its first tick.");
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(6f)]
    [InlineData(9f)]
    public void ComputeTurnVelocity_Released_StopsWithinDecelBudgetWithoutReversing(float speed)
    {
        Vector3 velocity = new(0, 0, speed);
        int ticks = (int)MathF.Ceiling(speed / 70 * 60);
        for (int tick = 0; tick < ticks; tick++)
        {
            float previous = velocity.Z;
            velocity = MovementMath.ComputeTurnVelocity(velocity, Vector3.Zero,
                1.0 / 60, speed, 45, 70, MathF.PI);
            Assert.InRange(velocity.Z, 0, previous);
            Assert.InRange(previous - velocity.Z, 0, 70f / 60 + 1e-5f);
        }
        Assert.Equal(Vector3.Zero, velocity);
    }

    [Theory]
    [InlineData(1f, 0f, .65f)]
    [InlineData(6f, 180f, .35f)]
    [InlineData(9f, 90f, .5f)]
    [InlineData(6f, 360f, .35f)]
    public void ComputeTurnVelocity_LargeStep_ClampsAtAngleScaledModeTarget(float speed, float degrees, float fraction)
    {
        Vector3 velocity = MovementMath.ComputeTurnVelocity(Vector3.Zero, Vector3.Right,
            10, speed, 45, 70, degrees * MathF.PI / 180);
        Assert.InRange(velocity.X, speed * fraction - 1e-5f, speed * fraction + 1e-5f);
        Assert.Equal(0, velocity.Z);
    }

    [Fact]
    public void ComputeTurnVelocity_FromRest_BuildsSpeedAtFiniteAcceleration()
    {
        Vector3 velocity = MovementMath.ComputeTurnVelocity(Vector3.Zero, Vector3.Right,
            1.0 / 60, 6, 45, 70, MathF.PI);
        Assert.InRange(velocity.X, .74999f, .75001f);
    }

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
        // deliberate change of pace rather than drift (AGENTS.md §1, ADR-0003).
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
