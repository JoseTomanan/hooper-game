using Godot;
using Hooper.Player;

namespace Hooper.Ball.Tests;

// Issue #230 — the drive-gather's steering target and Active-phase burst
// composition.
public class DriveGatherMathTests
{
    // ── WishDirToward ─────────────────────────────────────────────────────────

    [Fact]
    public void WishDirToward_TargetDirectlyAhead_ReturnsForwardUnitVector()
    {
        Vector2 result = DriveGatherMath.WishDirToward(
            fromXZ: new Vector2(0f, -6f), targetXZ: new Vector2(0f, 0f));

        Assert.Equal(0f, result.X, precision: 4);
        Assert.Equal(1f, result.Y, precision: 4);
    }

    [Fact]
    public void WishDirToward_TargetToTheRight_ReturnsRightUnitVector()
    {
        Vector2 result = DriveGatherMath.WishDirToward(
            fromXZ: new Vector2(0f, -6f), targetXZ: new Vector2(6f, -6f));

        Assert.Equal(1f, result.X, precision: 4);
        Assert.Equal(0f, result.Y, precision: 4);
    }

    [Fact]
    public void WishDirToward_ResultIsAlwaysUnitLength()
    {
        Vector2 result = DriveGatherMath.WishDirToward(
            fromXZ: new Vector2(1f, 2f), targetXZ: new Vector2(9f, -14f));

        Assert.Equal(1f, result.Length(), precision: 4);
    }

    [Fact]
    public void WishDirToward_SamePosition_ReturnsZeroNotNaN()
    {
        Vector2 result = DriveGatherMath.WishDirToward(
            fromXZ: new Vector2(3f, 3f), targetXZ: new Vector2(3f, 3f));

        Assert.Equal(Vector2.Zero, result);
    }

    // ── ComposeActiveVelocity ─────────────────────────────────────────────────

    [Fact]
    public void ComposeActiveVelocity_NoSurvivingMomentum_IsPureForwardBurst()
    {
        // Heading 0 -> forward is (0,1) per HeadingMath.Forward's convention
        // (matches CrossoverBurstMathTests/StepBackBurstMathTests).
        Vector3 result = DriveGatherMath.ComposeActiveVelocity(
            survivingVelocity: Vector3.Zero, heading: 0f, driveSpeed: 9f);

        Assert.Equal(0f, result.X, precision: 3);
        Assert.Equal(9f, result.Z, precision: 3);
    }

    [Fact]
    public void ComposeActiveVelocity_SurvivingMomentum_IsAddedNotDiscarded()
    {
        // ADR-0022: "opt-in to the SAME hybrid-gather model" — surviving
        // momentum from Startup's bleed carries forward, it is never
        // re-zeroed by the Active-entry burst.
        Vector3 result = DriveGatherMath.ComposeActiveVelocity(
            survivingVelocity: new Vector3(2f, 0f, 0f), heading: 0f, driveSpeed: 9f);

        Assert.Equal(2f, result.X, precision: 3);
        Assert.Equal(9f, result.Z, precision: 3);
    }

    [Fact]
    public void ComposeActiveVelocity_HeadingRotated90_BurstFollowsTheDriveLine()
    {
        // Heading pi/2 -> forward is (1,0): the burst tracks WHEREVER the
        // drive line resolved to during Startup, not a fixed world axis.
        Vector3 result = DriveGatherMath.ComposeActiveVelocity(
            survivingVelocity: Vector3.Zero, heading: Mathf.Pi / 2f, driveSpeed: 9f);

        Assert.Equal(9f, result.X, precision: 3);
        Assert.Equal(0f, result.Z, precision: 3);
    }

    [Fact]
    public void ComposeActiveVelocity_YAlwaysZero()
    {
        Vector3 result = DriveGatherMath.ComposeActiveVelocity(
            survivingVelocity: new Vector3(0f, 5f, 0f), heading: 0.3f, driveSpeed: 9f);

        Assert.Equal(0f, result.Y);
    }
}
