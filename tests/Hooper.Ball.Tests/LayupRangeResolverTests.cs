using Hooper.Moves;

namespace Hooper.Ball.Tests;

/// <summary>
/// Unit tests for LayupRangeResolver — the pure "does pressing shoot begin a
/// Layup or a JumpShot" decision (issue #229), extracted so it is verified
/// without a running Godot instance.
///
/// ── Test naming ──────────────────────────────────────────────────────────────
/// [MethodUnderTest]_[Scenario]_[ExpectedOutcome]
/// </summary>
public class LayupRangeResolverTests
{
    [Fact]
    public void IsLayupRange_StrictlyInsideRange_True()
    {
        Assert.True(LayupRangeResolver.IsLayupRange(distanceToRimXZ: 2f, layupRange: 4f));
    }

    [Fact]
    public void IsLayupRange_StrictlyOutsideRange_False()
    {
        Assert.False(LayupRangeResolver.IsLayupRange(distanceToRimXZ: 6f, layupRange: 4f));
    }

    [Fact]
    public void IsLayupRange_ExactlyAtBoundary_False()
    {
        // Strict comparison — a shot from exactly the boundary distance is a
        // jump shot, not a layup (matches CourtBounds.IsOutOfBounds' strict
        // boundary convention).
        Assert.False(LayupRangeResolver.IsLayupRange(distanceToRimXZ: 4f, layupRange: 4f));
    }

    [Fact]
    public void IsLayupRange_ZeroDistance_True()
    {
        Assert.True(LayupRangeResolver.IsLayupRange(distanceToRimXZ: 0f, layupRange: 4f));
    }
}
