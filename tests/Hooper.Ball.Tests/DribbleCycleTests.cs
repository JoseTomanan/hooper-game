using Godot;
using Hooper.Ball;

namespace Hooper.Ball.Tests;

/// <summary>
/// Unit tests for DribbleCycle — the deterministic hand-authored dribble
/// oscillation (ADR-0004).
///
/// All tests run without a live Godot instance. The pure class accepts
/// only plain data; no engine calls are made.
///
/// ── Test naming convention ───────────────────────────────────────────────
/// [MethodOrProperty]_[Scenario]_[ExpectedOutcome]
///
/// Each test contains exactly one logical assertion (or one indivisible
/// group, e.g. X and Y of the same position).
/// </summary>
public class DribbleCycleTests
{
    // ── Tunables shared across tests ──────────────────────────────────────

    /// <summary>
    /// Height of the holder's hand above the ground (release / catch point).
    /// The ball should reach this height at the top of the cycle.
    /// </summary>
    private const float HandHeight  = 1.0f;

    /// <summary>
    /// The ball is considered "at the floor" when its Y is ≤ this threshold.
    /// 0 = exact floor contact.
    /// </summary>
    private const float FloorHeight = 0.0f;

    /// <summary>
    /// Dribble period in seconds — full down-and-back-up cycle.
    /// </summary>
    private const float Period = 0.6f;

    private const float Epsilon = 0.001f;

    // ── Factory helpers ───────────────────────────────────────────────────

    private static DribbleCycle NewCycle(
        float handHeight = HandHeight,
        float period     = Period)
        => new DribbleCycle(handHeight: handHeight, period: period);

    // ═════════════════════════════════════════════════════════════════════
    // Construction / tunables
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void Constructor_ExposesHandHeight()
    {
        var cycle = NewCycle(handHeight: 1.2f);
        Assert.Equal(1.2f, cycle.HandHeight, precision: 4);
    }

    [Fact]
    public void Constructor_ExposesPeriod()
    {
        var cycle = NewCycle(period: 0.8f);
        Assert.Equal(0.8f, cycle.Period, precision: 4);
    }

    // ═════════════════════════════════════════════════════════════════════
    // HeightAtPhase — parametric height curve
    //
    // Phase is normalised [0, 1]:
    //   0.0 = top of cycle (hand — ball just released/caught)
    //   0.5 = bottom of cycle (floor — ball at bounce point)
    //   1.0 = wraps to 0.0 (top again)
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void HeightAtPhase_PhaseZero_ReturnsHandHeight()
    {
        var cycle = NewCycle();
        float height = cycle.HeightAtPhase(phase: 0.0f);
        Assert.Equal(HandHeight, height, Epsilon);
    }

    [Fact]
    public void HeightAtPhase_PhaseHalf_ReturnsFloorHeight()
    {
        var cycle = NewCycle();
        float height = cycle.HeightAtPhase(phase: 0.5f);
        Assert.Equal(FloorHeight, height, Epsilon);
    }

    [Fact]
    public void HeightAtPhase_PhaseOne_WrapsToHandHeight()
    {
        // Phase = 1.0 is equivalent to phase = 0.0 (full cycle complete).
        var cycle = NewCycle();
        float height = cycle.HeightAtPhase(phase: 1.0f);
        Assert.Equal(HandHeight, height, Epsilon);
    }

    [Fact]
    public void HeightAtPhase_PhaseQuarter_IsBetweenFloorAndHand()
    {
        // At 1/4 through the cycle the ball is on its way down —
        // height is between hand and floor.
        var cycle = NewCycle();
        float height = cycle.HeightAtPhase(phase: 0.25f);
        Assert.True(height > FloorHeight && height < HandHeight,
            $"Expected height between {FloorHeight} and {HandHeight}, got {height}");
    }

    [Fact]
    public void HeightAtPhase_PhaseThreeQuarters_IsBetweenFloorAndHand()
    {
        // At 3/4 through the cycle the ball is on its way up.
        var cycle = NewCycle();
        float height = cycle.HeightAtPhase(phase: 0.75f);
        Assert.True(height > FloorHeight && height < HandHeight,
            $"Expected height between {FloorHeight} and {HandHeight}, got {height}");
    }

    [Fact]
    public void HeightAtPhase_IsSymmetricAroundHalfCycle()
    {
        // The dribble should be symmetric: descending half mirrors ascending half.
        var cycle = NewCycle();
        float h1 = cycle.HeightAtPhase(phase: 0.25f);
        float h2 = cycle.HeightAtPhase(phase: 0.75f);
        Assert.Equal(h1, h2, Epsilon);
    }

    [Fact]
    public void HeightAtPhase_DifferentHandHeight_ScalesCorrectly()
    {
        float customHeight = 1.5f;
        var cycle = NewCycle(handHeight: customHeight);
        float height = cycle.HeightAtPhase(phase: 0.0f);
        Assert.Equal(customHeight, height, Epsilon);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Advance — phase progresses with time
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void Advance_OnePeriod_PhaseCyclesBackToZero()
    {
        var cycle = NewCycle(period: 1.0f);
        cycle.Advance(dt: 1.0f); // exactly one period
        Assert.Equal(0.0f, cycle.Phase, Epsilon);
    }

    [Fact]
    public void Advance_HalfPeriod_PhaseIsHalf()
    {
        var cycle = NewCycle(period: 1.0f);
        cycle.Advance(dt: 0.5f);
        Assert.Equal(0.5f, cycle.Phase, Epsilon);
    }

    [Fact]
    public void Advance_ZeroDt_PhaseUnchanged()
    {
        var cycle = NewCycle();
        float phaseBefore = cycle.Phase;
        cycle.Advance(dt: 0.0f);
        Assert.Equal(phaseBefore, cycle.Phase, Epsilon);
    }

    [Fact]
    public void Advance_MultiplePeriods_PhaseWrapsCorrectly()
    {
        // Advancing by 2.5 periods should land at phase 0.5.
        var cycle = NewCycle(period: 1.0f);
        cycle.Advance(dt: 2.5f);
        Assert.Equal(0.5f, cycle.Phase, Epsilon);
    }

    // ═════════════════════════════════════════════════════════════════════
    // GetBallPosition — tracks the holder horizontally, oscillates vertically
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void GetBallPosition_MatchesHolderXZ_AtAnyPhase()
    {
        var cycle = NewCycle(period: 1.0f);
        cycle.Advance(dt: 0.3f);

        var holderPos = new Vector3(5.0f, 0.0f, -3.0f);
        Vector3 ballPos = cycle.GetBallPosition(holderPos);

        // Ball tracks holder on the ground plane (X and Z identical).
        Assert.Equal(holderPos.X, ballPos.X, Epsilon);
        Assert.Equal(holderPos.Z, ballPos.Z, Epsilon);
    }

    [Fact]
    public void GetBallPosition_YIsHandHeight_WhenPhaseIsZero()
    {
        var cycle = NewCycle();
        // Fresh cycle starts at phase 0 (top of bounce).
        var holderPos = new Vector3(0f, 0f, 0f);
        Vector3 ballPos = cycle.GetBallPosition(holderPos);

        Assert.Equal(HandHeight, ballPos.Y, Epsilon);
    }

    [Fact]
    public void GetBallPosition_YIsFloor_WhenPhaseIsHalf()
    {
        var cycle = NewCycle(period: 1.0f);
        cycle.Advance(dt: 0.5f); // advance to bottom of cycle

        var holderPos = new Vector3(0f, 0f, 0f);
        Vector3 ballPos = cycle.GetBallPosition(holderPos);

        Assert.Equal(FloorHeight, ballPos.Y, Epsilon);
    }

    [Fact]
    public void GetBallPosition_HolderMovement_BallFollowsNewPosition()
    {
        var cycle = NewCycle();
        var oldPos = new Vector3(0f, 0f, 0f);
        var newPos = new Vector3(10f, 0f, 5f);

        Vector3 ballAtNew = cycle.GetBallPosition(newPos);

        Assert.Equal(newPos.X, ballAtNew.X, Epsilon);
        Assert.Equal(newPos.Z, ballAtNew.Z, Epsilon);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Determinism — same inputs → same outputs
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void Determinism_TwoIndependentCycles_ProduceIdenticalSequence()
    {
        // Two separate DribbleCycle instances with the same tunables and
        // the same sequence of Advance() calls must yield identical phases.
        var cycleA = NewCycle(handHeight: 1.0f, period: 0.6f);
        var cycleB = NewCycle(handHeight: 1.0f, period: 0.6f);

        float dt = 1.0f / 60.0f; // fixed tick
        for (int i = 0; i < 50; i++)
        {
            cycleA.Advance(dt);
            cycleB.Advance(dt);
        }

        Assert.Equal(cycleA.Phase, cycleB.Phase, Epsilon);
    }

    [Fact]
    public void Determinism_TwoIndependentCycles_ProduceIdenticalPositions()
    {
        var cycleA = NewCycle(handHeight: 1.0f, period: 0.6f);
        var cycleB = NewCycle(handHeight: 1.0f, period: 0.6f);

        float dt = 1.0f / 60.0f;
        var holderPos = new Vector3(3f, 0f, -2f);
        for (int i = 0; i < 37; i++)
        {
            cycleA.Advance(dt);
            cycleB.Advance(dt);
        }

        Vector3 posA = cycleA.GetBallPosition(holderPos);
        Vector3 posB = cycleB.GetBallPosition(holderPos);

        Assert.Equal(posA.X, posB.X, Epsilon);
        Assert.Equal(posA.Y, posB.Y, Epsilon);
        Assert.Equal(posA.Z, posB.Z, Epsilon);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Reset — issue #176: a possession change must start a fresh dribble
    //
    // Per real half-court 1v1 rules (ADR-0014 ranked references, resolved on
    // #176), a change of possession — rebound, steal recovery, tipoff,
    // make-it-take-it — always begins a brand-new dribble; there is no such
    // thing as a "continued" dribble surviving a loose-ball scramble. Reset()
    // is the pure mechanism BallController.AwardPossession calls so a frozen
    // mid-cycle Phase can never leak across a possession change (the #176
    // re-steal exploit: a frozen Phase inside the steal-exposed band let a
    // defender re-steal a scramble recovery with no genuine timing read).
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void Reset_AfterAdvancing_PhaseReturnsToZero()
    {
        var cycle = NewCycle(period: 1.0f);
        cycle.Advance(dt: 0.5f); // Phase is now 0.5 — inside the steal-exposed band.

        cycle.Reset();

        Assert.Equal(0.0f, cycle.Phase, Epsilon);
    }

    [Fact]
    public void Reset_TunablesUnaffected()
    {
        var cycle = NewCycle(handHeight: 1.2f, period: 0.8f);
        cycle.Advance(dt: 0.4f);

        cycle.Reset();

        Assert.Equal(1.2f, cycle.HandHeight, precision: 4);
        Assert.Equal(0.8f, cycle.Period, precision: 4);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Period tunable affects cycle speed
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void Period_Shorter_PhaseAdvancesFaster()
    {
        var fast = NewCycle(period: 0.3f);
        var slow = NewCycle(period: 1.0f);

        float dt = 0.1f;
        fast.Advance(dt);
        slow.Advance(dt);

        // Fast cycle should have advanced further through its phase.
        Assert.True(fast.Phase > slow.Phase,
            $"Fast phase {fast.Phase} should be > slow phase {slow.Phase}");
    }
}
