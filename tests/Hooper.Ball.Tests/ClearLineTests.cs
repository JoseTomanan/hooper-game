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

    // ── Advance: crossing-detection take-it-back (#135) ──────────────────────
    // A possession clears only on a genuine take-back — the handler must have
    // been inside the clear line during this possession and then carried the
    // ball back behind it. Standing behind the line without that round-trip
    // (e.g. rebounding your own miss from behind the arc) must NOT clear.

    [Fact]
    public void Advance_BehindLineAfterHavingBeenInside_Clears()
    {
        // The genuine take-back: hasBeenInside already latched, now behind the
        // line → the possession clears.
        var (cleared, _) = ClearLine.Advance(
            cleared: false, hasBeenInside: true,
            handlerPosition: new Vector3(0f, 0f, 8f), hoopCenter: Hoop, clearLineDistance: ClearDistance);
        Assert.True(cleared);
    }

    [Fact]
    public void Advance_BehindLineWithoutHavingBeenInside_DoesNotClear()
    {
        // The #135 fix: a rebound recovered while already behind the line (the
        // offensive board from behind the arc) has done no take-back, so it must
        // NOT clear — no instant put-back three.
        var (cleared, hasBeenInside) = ClearLine.Advance(
            cleared: false, hasBeenInside: false,
            handlerPosition: new Vector3(0f, 0f, 8f), hoopCenter: Hoop, clearLineDistance: ClearDistance);
        Assert.False(cleared);
        Assert.False(hasBeenInside); // standing behind the line is not "being inside"
    }

    [Fact]
    public void Advance_InsideLine_LatchesHasBeenInside_NotCleared()
    {
        // Inside the clear line: not cleared, but the take-back round-trip is now
        // underway — latch hasBeenInside so a later step behind the line can clear.
        var (cleared, hasBeenInside) = ClearLine.Advance(
            cleared: false, hasBeenInside: false,
            handlerPosition: new Vector3(0f, 0f, 0f), hoopCenter: Hoop, clearLineDistance: ClearDistance);
        Assert.False(cleared);
        Assert.True(hasBeenInside);
    }

    [Fact]
    public void Advance_AlreadyCleared_StaysClearedRegardlessOfPosition()
    {
        // Once cleared, the possession stays cleared even back inside the line —
        // the existing one-way-within-a-possession guarantee (BallController early
        // returns on IsCleared; Advance preserves it defensively).
        var (cleared, _) = ClearLine.Advance(
            cleared: true, hasBeenInside: true,
            handlerPosition: new Vector3(0f, 0f, 0f), hoopCenter: Hoop, clearLineDistance: ClearDistance);
        Assert.True(cleared);
    }

    [Fact]
    public void Advance_ReboundBehindArc_RequiresRoundTrip_ToClear()
    {
        // End-to-end take-back for the #135 case: rebound your own miss behind the
        // arc (behind the line, never been inside), then drive inside and carry it
        // back out. Only the final step — behind AFTER the inside latch — clears.
        bool cleared = false, inside = false;

        // 1. Recover the ball behind the line: no take-back yet.
        (cleared, inside) = ClearLine.Advance(cleared, inside, new Vector3(0f, 0f, 8f), Hoop, ClearDistance);
        Assert.False(cleared);

        // 2. Drive inside the line: latches the round-trip, still not cleared.
        (cleared, inside) = ClearLine.Advance(cleared, inside, new Vector3(0f, 0f, 1f), Hoop, ClearDistance);
        Assert.False(cleared);
        Assert.True(inside);

        // 3. Carry it back behind the line: the take-back is complete → cleared.
        (cleared, inside) = ClearLine.Advance(cleared, inside, new Vector3(0f, 0f, 8f), Hoop, ClearDistance);
        Assert.True(cleared);
    }
}
