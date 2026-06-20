using Godot;
using Hooper.Ball;

namespace Hooper.Ball.Tests;

/// <summary>
/// Unit tests for ClearLine — the take-it-back / "clear" geometry (ADR-0008,
/// issue #50). Headless (ADR-0004): pure floor-plane distance, no engine.
///
/// ── Test naming convention ───────────────────────────────────────────────
/// [MethodUnderTest]_[Scenario]_[ExpectedOutcome]
/// One logical assertion per test.
/// </summary>
public class ClearLineTests
{
    private static readonly Vector3 Hoop = new(0f, 3.05f, 0f);
    private const float ClearDistance = 5.8f;

    [Fact]
    public void IsBehindClearLine_AtHoop_NotCleared()
    {
        bool cleared = ClearLine.IsBehindClearLine(new Vector3(0f, 0f, 0f), Hoop, ClearDistance);
        Assert.False(cleared);
    }

    [Fact]
    public void IsBehindClearLine_JustInsideLine_NotCleared()
    {
        bool cleared = ClearLine.IsBehindClearLine(new Vector3(0f, 0f, 5.79f), Hoop, ClearDistance);
        Assert.False(cleared);
    }

    [Fact]
    public void IsBehindClearLine_WellBehindLine_Cleared()
    {
        bool cleared = ClearLine.IsBehindClearLine(new Vector3(0f, 0f, 8f), Hoop, ClearDistance);
        Assert.True(cleared);
    }

    [Fact]
    public void IsBehindClearLine_ExactlyOnLine_Cleared()
    {
        // Boundary: distance exactly equal to the clear distance clears (>=).
        bool cleared = ClearLine.IsBehindClearLine(new Vector3(0f, 0f, 5.8f), Hoop, ClearDistance);
        Assert.True(cleared);
    }

    [Fact]
    public void IsBehindClearLine_BehindOnAnyAxis_IsRadialNotPlanar()
    {
        // The line is a radius, not a plane: being 5.8 m out along X clears just
        // as well as along Z — orientation-agnostic, as documented.
        bool cleared = ClearLine.IsBehindClearLine(new Vector3(6f, 0f, 0f), Hoop, ClearDistance);
        Assert.True(cleared);
    }

    [Fact]
    public void IsBehindClearLine_DiagonalDistance_UsesCombinedXZ()
    {
        // (4.2, 4.2) is ~5.94 m from the hoop on the floor plane (> 5.8), so it
        // clears even though neither axis alone reaches the line.
        bool cleared = ClearLine.IsBehindClearLine(new Vector3(4.2f, 0f, 4.2f), Hoop, ClearDistance);
        Assert.True(cleared);
    }

    [Fact]
    public void IsBehindClearLine_HighAboveHoop_IgnoresHeight_NotCleared()
    {
        // Directly above the hoop and 10 m up — height must not count toward the
        // clear, so this is still not behind the line.
        bool cleared = ClearLine.IsBehindClearLine(new Vector3(0f, 10f, 0f), Hoop, ClearDistance);
        Assert.False(cleared);
    }

    [Fact]
    public void IsBehindClearLine_RespectsHoopOffset()
    {
        // Clear distance is measured from the hoop, not the world origin: a hoop
        // shifted to X=10 moves the line with it.
        var offsetHoop = new Vector3(10f, 3.05f, 0f);
        bool cleared = ClearLine.IsBehindClearLine(new Vector3(0f, 0f, 0f), offsetHoop, ClearDistance);
        Assert.True(cleared); // 10 m from the hoop > 5.8
    }
}
