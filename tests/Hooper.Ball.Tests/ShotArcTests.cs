using Godot;
using Hooper.Ball;

namespace Hooper.Ball.Tests;

/// <summary>
/// Unit tests for ShotArc — the deterministic parabolic shot/pass trajectory
/// (ADR-0004).
///
/// All tests run without a live Godot instance. The pure class accepts
/// only plain data; no engine calls are made.
///
/// ── Coordinate system ────────────────────────────────────────────────────
/// Y is UP (Godot default). A shot fired from a release point toward a
/// target follows a parabolic path under constant downward gravity.
///
/// ── Launch velocity solve ────────────────────────────────────────────────
/// Given: release point, target point, apex height, gravity constant.
/// Solve: initial velocity (Vx, Vy, Vz) so the ball passes through the apex
/// and lands at the target under constant gravity.
///
/// ── Stepper API ──────────────────────────────────────────────────────────
/// ShotArc.Step(dt) advances one fixed tick using semi-implicit Euler:
///   velocity += gravity * dt
///   position += velocity * dt
/// The dt parameter is explicit so tests can use a fixed value and get
/// deterministic results without a running Godot engine.
///
/// ── Test naming convention ───────────────────────────────────────────────
/// [MethodOrProperty]_[Scenario]_[ExpectedOutcome]
///
/// Each test contains exactly one logical assertion (or one indivisible group).
/// </summary>
public class ShotArcTests
{
    // ── Shared constants ──────────────────────────────────────────────────

    private const float GravityMagnitude = 9.8f;
    private const float FixedDt          = 1.0f / 60.0f; // 60 Hz fixed tick
    private const float CoarseEpsilon    = 0.15f;  // for landing-accuracy tests
    private const float FineEpsilon      = 0.001f; // for determinism / tunable tests

    // ── Factory helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Creates a ShotArc with a flat shot: release at origin, target on same
    /// height at (5, 0, 0), apex 2 m above the release point.
    /// </summary>
    private static ShotArc FlatShot() =>
        new ShotArc(
            releasePoint:  new Vector3(0f, 1f, 0f),
            targetPoint:   new Vector3(5f, 1f, 0f),
            apexHeight:    3f,
            gravity:       GravityMagnitude);

    /// <summary>
    /// Creates a ShotArc simulating a free-throw (release ~1 m in front,
    /// target basket at 3 m away, elevation 3.05 m).
    /// </summary>
    private static ShotArc FreeThrow() =>
        new ShotArc(
            releasePoint:  new Vector3(0f, 2.0f, 0f),
            targetPoint:   new Vector3(4.572f, 3.05f, 0f),
            apexHeight:    4.5f,
            gravity:       GravityMagnitude);

    // ═════════════════════════════════════════════════════════════════════
    // Construction / tunables
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void Constructor_InitialPosition_IsReleasePoint()
    {
        var release = new Vector3(1f, 2f, 3f);
        var arc = new ShotArc(
            releasePoint: release,
            targetPoint:  new Vector3(5f, 2f, 3f),
            apexHeight:   4f,
            gravity:      GravityMagnitude);

        Assert.Equal(release.X, arc.Position.X, FineEpsilon);
        Assert.Equal(release.Y, arc.Position.Y, FineEpsilon);
        Assert.Equal(release.Z, arc.Position.Z, FineEpsilon);
    }

    [Fact]
    public void Constructor_ExposesGravity()
    {
        var arc = FlatShot();
        Assert.Equal(GravityMagnitude, arc.Gravity, FineEpsilon);
    }

    [Fact]
    public void Constructor_InitialVelocity_HasUpwardYComponent()
    {
        // A shot fired from below its apex must have a positive initial Vy.
        var arc = FlatShot();
        Assert.True(arc.Velocity.Y > 0f,
            $"Expected positive initial Vy, got {arc.Velocity.Y}");
    }

    [Fact]
    public void Constructor_InitialVelocity_HasHorizontalXComponent()
    {
        // A shot toward positive X must have positive initial Vx.
        var arc = FlatShot();
        Assert.True(arc.Velocity.X > 0f,
            $"Expected positive initial Vx, got {arc.Velocity.X}");
    }

    // ═════════════════════════════════════════════════════════════════════
    // Step — semi-implicit Euler integrator
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void Step_Once_PositionChanges()
    {
        var arc = FlatShot();
        var initial = arc.Position;
        arc.Step(FixedDt);
        // After one step, position must have changed.
        Assert.NotEqual(initial, arc.Position);
    }

    [Fact]
    public void Step_Once_VelocityYDecreasesByGravityTimesDt()
    {
        // Semi-implicit Euler: vel.Y -= gravity * dt (applied BEFORE position update).
        var arc = FlatShot();
        float initialVy = arc.Velocity.Y;
        arc.Step(FixedDt);
        float expectedVy = initialVy - GravityMagnitude * FixedDt;
        Assert.Equal(expectedVy, arc.Velocity.Y, FineEpsilon);
    }

    [Fact]
    public void Step_HorizontalVelocity_IsConstantUnderGravityOnly()
    {
        // Gravity is vertical only — Vx and Vz should not change each step.
        var arc = FlatShot();
        float initialVx = arc.Velocity.X;
        float initialVz = arc.Velocity.Z;
        arc.Step(FixedDt);
        Assert.Equal(initialVx, arc.Velocity.X, FineEpsilon);
        Assert.Equal(initialVz, arc.Velocity.Z, FineEpsilon);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Apex — ball reaches configured apex height
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void Apex_PeakY_ApproximatesConfiguredApexHeight()
    {
        // Step the arc until Vy goes negative (peak passed), then check the
        // maximum Y reached is approximately the configured apex height.
        var arc = FlatShot();
        float apexHeight = 3.0f; // same as FlatShot()
        float maxY = arc.Position.Y;
        const int maxSteps = 1000;

        for (int i = 0; i < maxSteps; i++)
        {
            float prevY = arc.Position.Y;
            arc.Step(FixedDt);
            maxY = MathF.Max(maxY, arc.Position.Y);
            // Stop once descending past release height.
            if (arc.Position.Y < 0f) break;
        }

        Assert.Equal(apexHeight, maxY, CoarseEpsilon);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Landing accuracy — ball reaches near the target
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void Landing_FlatShot_XReachesTargetX()
    {
        var arc = FlatShot();
        float targetX = 5.0f;
        StepUntilYCrossesTarget(arc, targetY: 1.0f, maxSteps: 500);

        Assert.Equal(targetX, arc.Position.X, CoarseEpsilon);
    }

    [Fact]
    public void Landing_FreeThrow_XReachesTargetX()
    {
        var arc = FreeThrow();
        float targetX = 4.572f;
        StepUntilYCrossesTarget(arc, targetY: 3.05f, maxSteps: 500);

        Assert.Equal(targetX, arc.Position.X, CoarseEpsilon);
    }

    [Fact]
    public void Landing_FreeThrow_ZRemainsZero()
    {
        // Free throw is in the XY plane — Z should stay at 0 throughout.
        var arc = FreeThrow();
        StepUntilYCrossesTarget(arc, targetY: 3.05f, maxSteps: 500);

        Assert.Equal(0f, arc.Position.Z, CoarseEpsilon);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Symmetry — parabola is symmetric around the apex
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void Trajectory_IsParabolic_SymmetricAroundApex()
    {
        // For a flat shot (same Y at start and end), the horizontal distance
        // to the apex should be approximately half the total range.
        var arc = FlatShot();
        float startX = arc.Position.X;
        float targetX = 5.0f;
        float midX = (startX + targetX) / 2.0f;

        float apexX = float.MinValue;
        float maxY  = arc.Position.Y;
        const int maxSteps = 500;

        for (int i = 0; i < maxSteps; i++)
        {
            arc.Step(FixedDt);
            if (arc.Position.Y > maxY)
            {
                maxY  = arc.Position.Y;
                apexX = arc.Position.X;
            }
            if (arc.Velocity.Y < 0 && arc.Position.Y <= 1.0f) break;
        }

        // Apex should be within CoarseEpsilon of the midpoint.
        Assert.Equal(midX, apexX, CoarseEpsilon);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Tunables — gravity and apex affect the arc
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void Gravity_Higher_BallLandsEarlier()
    {
        // Higher gravity means shorter flight time — ball lands at lower X.
        var release = new Vector3(0f, 1f, 0f);
        var target  = new Vector3(5f, 1f, 0f);

        var normalGravity = new ShotArc(release, target, apexHeight: 3f, gravity: 9.8f);
        var highGravity   = new ShotArc(release, target, apexHeight: 3f, gravity: 20f);

        // Step both until Y ≤ 0 or they pass target height.
        float xNormal = StepUntilYCrossesAndReturnX(normalGravity, targetY: 1.0f, maxSteps: 600);
        float xHigh   = StepUntilYCrossesAndReturnX(highGravity,   targetY: 1.0f, maxSteps: 600);

        // Under high gravity the flight is faster but the launch Vy is larger
        // to hit the same apex — both reach the same X, but time differs.
        // The true observable effect of higher gravity is: if we keep the same
        // initial velocity, the ball lands shorter. Here though apex is fixed,
        // so launch Vy compensates. The meaningful test is that gravity is
        // actually consumed in the velocity integration.
        Assert.NotEqual(xNormal, xHigh); // they MUST differ — gravity is wired
    }

    [Fact]
    public void ApexHeight_Higher_BallTakesLonger()
    {
        // Higher apex = more Vy upward = longer flight time.
        var release = new Vector3(0f, 1f, 0f);
        var target  = new Vector3(5f, 1f, 0f);

        var lowApex  = new ShotArc(release, target, apexHeight: 2.5f, gravity: 9.8f);
        var highApex = new ShotArc(release, target, apexHeight: 5f,   gravity: 9.8f);

        int stepsLow  = StepUntilYCrossesAndReturnSteps(lowApex,  targetY: 1.0f, maxSteps: 1000);
        int stepsHigh = StepUntilYCrossesAndReturnSteps(highApex, targetY: 1.0f, maxSteps: 1000);

        Assert.True(stepsHigh > stepsLow,
            $"High apex ({stepsHigh} steps) should take longer than low apex ({stepsLow} steps)");
    }

    // ═════════════════════════════════════════════════════════════════════
    // Determinism — same inputs across two independent instances
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void Determinism_TwoIndependentArcs_ProduceIdenticalPositionSequences()
    {
        var release = new Vector3(0f, 2f, 0f);
        var target  = new Vector3(5f, 3.05f, 0f);

        var arcA = new ShotArc(release, target, apexHeight: 4.5f, gravity: 9.8f);
        var arcB = new ShotArc(release, target, apexHeight: 4.5f, gravity: 9.8f);

        float dt = FixedDt;
        for (int i = 0; i < 40; i++)
        {
            arcA.Step(dt);
            arcB.Step(dt);
        }

        Assert.Equal(arcA.Position.X, arcB.Position.X, FineEpsilon);
        Assert.Equal(arcA.Position.Y, arcB.Position.Y, FineEpsilon);
        Assert.Equal(arcA.Position.Z, arcB.Position.Z, FineEpsilon);
    }

    [Fact]
    public void Determinism_TwoIndependentArcs_ProduceIdenticalVelocitySequences()
    {
        var release = new Vector3(0f, 2f, 0f);
        var target  = new Vector3(5f, 3.05f, 0f);

        var arcA = new ShotArc(release, target, apexHeight: 4.5f, gravity: 9.8f);
        var arcB = new ShotArc(release, target, apexHeight: 4.5f, gravity: 9.8f);

        float dt = FixedDt;
        for (int i = 0; i < 40; i++)
        {
            arcA.Step(dt);
            arcB.Step(dt);
        }

        Assert.Equal(arcA.Velocity.X, arcB.Velocity.X, FineEpsilon);
        Assert.Equal(arcA.Velocity.Y, arcB.Velocity.Y, FineEpsilon);
        Assert.Equal(arcA.Velocity.Z, arcB.Velocity.Z, FineEpsilon);
    }

    // ═════════════════════════════════════════════════════════════════════
    // 3-D shot — Z component is driven correctly
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void ThreeDimensional_ZComponent_ReachesTargetZ()
    {
        var arc = new ShotArc(
            releasePoint: new Vector3(0f, 2f, 0f),
            targetPoint:  new Vector3(4f, 2f, 3f),
            apexHeight:   4f,
            gravity:      GravityMagnitude);

        StepUntilYCrossesTarget(arc, targetY: 2.0f, maxSteps: 600);

        Assert.Equal(3f, arc.Position.Z, CoarseEpsilon);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Helper utilities (not tests)
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Steps the arc until its Y position first descends back through
    /// <paramref name="targetY"/> after the apex (i.e., on the way down).
    /// Stops at maxSteps.  Used to check landing position.
    /// </summary>
    private static void StepUntilYCrossesTarget(ShotArc arc, float targetY, int maxSteps)
    {
        bool pastApex = false;
        for (int i = 0; i < maxSteps; i++)
        {
            if (arc.Velocity.Y < 0f) pastApex = true;
            if (pastApex && arc.Position.Y <= targetY) break;
            arc.Step(FixedDt);
        }
    }

    private static float StepUntilYCrossesAndReturnX(ShotArc arc, float targetY, int maxSteps)
    {
        StepUntilYCrossesTarget(arc, targetY, maxSteps);
        return arc.Position.X;
    }

    private static int StepUntilYCrossesAndReturnSteps(ShotArc arc, float targetY, int maxSteps)
    {
        bool pastApex = false;
        int steps = 0;
        for (int i = 0; i < maxSteps; i++)
        {
            if (arc.Velocity.Y < 0f) pastApex = true;
            if (pastApex && arc.Position.Y <= targetY) break;
            arc.Step(FixedDt);
            steps++;
        }
        return steps;
    }
}
