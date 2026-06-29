using Godot;
using Hooper.Ball;

namespace Hooper.Ball.Tests;

/// <summary>
/// Unit tests for CourtBounds — the half-court XZ clamp for loose balls
/// (issue #46). Headless (ADR-0004): pure rectangle clamp, no engine.
///
/// ── Test naming convention ───────────────────────────────────────────────
/// [MethodUnderTest]_[Scenario]_[ExpectedOutcome]
/// One logical assertion per test.
/// </summary>
public class CourtBoundsTests
{
    // Representative court matching BallController's default exports:
    //   floor X ∈ [−5, 5], floor Z ∈ [−1, 12], inset by a ball-radius (0.12).
    private static readonly Vector2 Min = new(-5f, -1f);
    private static readonly Vector2 Max = new(5f, 12f);

    [Fact]
    public void Clamp_InsideCourt_Unchanged()
    {
        var pos = new Vector3(2f, 0.12f, 5f);
        Vector3 result = CourtBounds.Clamp(pos, Min, Max);
        Assert.Equal(pos, result);
    }

    [Fact]
    public void Clamp_BeyondRightEdge_ClampsX()
    {
        var pos = new Vector3(20f, 0.12f, 5f);
        Vector3 result = CourtBounds.Clamp(pos, Min, Max);
        Assert.Equal(Max.X, result.X);
    }

    [Fact]
    public void Clamp_BeyondLeftEdge_ClampsX()
    {
        var pos = new Vector3(-20f, 0.12f, 5f);
        Vector3 result = CourtBounds.Clamp(pos, Min, Max);
        Assert.Equal(Min.X, result.X);
    }

    [Fact]
    public void Clamp_BeyondFarEdge_ClampsZ()
    {
        var pos = new Vector3(0f, 0.12f, 99f);
        Vector3 result = CourtBounds.Clamp(pos, Min, Max);
        Assert.Equal(Max.Y, result.Z);
    }

    [Fact]
    public void Clamp_BeyondNearEdge_ClampsZ()
    {
        var pos = new Vector3(0f, 0.12f, -99f);
        Vector3 result = CourtBounds.Clamp(pos, Min, Max);
        Assert.Equal(Min.Y, result.Z);
    }

    [Fact]
    public void Clamp_YPreservedWhenOutsideXZ()
    {
        // Y is the ball's height above the floor — court bounds must never touch it.
        float originalY = 3.5f;
        var pos = new Vector3(20f, originalY, 99f);
        Vector3 result = CourtBounds.Clamp(pos, Min, Max);
        Assert.Equal(originalY, result.Y);
    }

    [Fact]
    public void Clamp_ExactlyOnEdge_StaysOnEdge()
    {
        // The boundary itself is inside the court (Clamp is inclusive).
        var pos = new Vector3(Max.X, 0.12f, Max.Y);
        Vector3 result = CourtBounds.Clamp(pos, Min, Max);
        Assert.Equal(pos, result);
    }

    [Fact]
    public void Clamp_BothAxesOutside_ClampsBoth()
    {
        // Corner shot: ball escapes both X and Z bounds simultaneously.
        var pos = new Vector3(99f, 0.12f, -99f);
        Vector3 result = CourtBounds.Clamp(pos, Min, Max);
        Assert.Equal(Max.X, result.X);
        Assert.Equal(Min.Y, result.Z);
    }

    // ── IsOutOfBounds tests ──────────────────────────────────────────────────
    // Covering: inside→false, boundary edges→false (inclusive), beyond each of
    // the 4 edges→true, both-axes-out→true, Y-far-above-but-XZ-inside→false.

    [Fact]
    public void IsOutOfBounds_InsideCourt_ReturnsFalse()
    {
        var pos = new Vector3(0f, 0.12f, 5f);
        Assert.False(CourtBounds.IsOutOfBounds(pos, Min, Max));
    }

    [Fact]
    public void IsOutOfBounds_ExactlyOnRightEdge_ReturnsFalse()
    {
        var pos = new Vector3(Max.X, 0.12f, 5f);
        Assert.False(CourtBounds.IsOutOfBounds(pos, Min, Max));
    }

    [Fact]
    public void IsOutOfBounds_ExactlyOnLeftEdge_ReturnsFalse()
    {
        var pos = new Vector3(Min.X, 0.12f, 5f);
        Assert.False(CourtBounds.IsOutOfBounds(pos, Min, Max));
    }

    [Fact]
    public void IsOutOfBounds_ExactlyOnFarEdge_ReturnsFalse()
    {
        var pos = new Vector3(0f, 0.12f, Max.Y);
        Assert.False(CourtBounds.IsOutOfBounds(pos, Min, Max));
    }

    [Fact]
    public void IsOutOfBounds_ExactlyOnNearEdge_ReturnsFalse()
    {
        var pos = new Vector3(0f, 0.12f, Min.Y);
        Assert.False(CourtBounds.IsOutOfBounds(pos, Min, Max));
    }

    [Fact]
    public void IsOutOfBounds_BeyondRightEdge_ReturnsTrue()
    {
        var pos = new Vector3(Max.X + 0.01f, 0.12f, 5f);
        Assert.True(CourtBounds.IsOutOfBounds(pos, Min, Max));
    }

    [Fact]
    public void IsOutOfBounds_BeyondLeftEdge_ReturnsTrue()
    {
        var pos = new Vector3(Min.X - 0.01f, 0.12f, 5f);
        Assert.True(CourtBounds.IsOutOfBounds(pos, Min, Max));
    }

    [Fact]
    public void IsOutOfBounds_BeyondFarEdge_ReturnsTrue()
    {
        var pos = new Vector3(0f, 0.12f, Max.Y + 0.01f);
        Assert.True(CourtBounds.IsOutOfBounds(pos, Min, Max));
    }

    [Fact]
    public void IsOutOfBounds_BeyondNearEdge_ReturnsTrue()
    {
        var pos = new Vector3(0f, 0.12f, Min.Y - 0.01f);
        Assert.True(CourtBounds.IsOutOfBounds(pos, Min, Max));
    }

    [Fact]
    public void IsOutOfBounds_BothAxesOut_ReturnsTrue()
    {
        var pos = new Vector3(Max.X + 1f, 0.12f, Min.Y - 1f);
        Assert.True(CourtBounds.IsOutOfBounds(pos, Min, Max));
    }

    [Fact]
    public void IsOutOfBounds_YFarAboveButXZInside_ReturnsFalse()
    {
        // Y is the height above the floor — completely irrelevant to OOB detection.
        var pos = new Vector3(0f, 999f, 5f);
        Assert.False(CourtBounds.IsOutOfBounds(pos, Min, Max));
    }
}
