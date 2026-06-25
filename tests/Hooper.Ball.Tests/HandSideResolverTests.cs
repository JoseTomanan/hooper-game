using Hooper.Moves;
using Hooper.Player;

namespace Hooper.Ball.Tests;

/// <summary>
/// Unit tests for HandSideResolver — the pure ball-on-hand decision extracted
/// for M7b (issue #73) so the "which hand should the ball render in" logic is
/// verified without a running Godot instance.
///
/// ── Test naming ──────────────────────────────────────────────────────────────
/// [MethodUnderTest]_[Scenario]_[ExpectedOutcome]
/// </summary>
public class HandSideResolverTests
{
    [Fact]
    public void Resolve_EnteringActiveWithPositiveBurst_ReturnsRight()
    {
        HandSide result = HandSideResolver.Resolve(
            current: HandSide.Left, previousPhase: MovePhase.Startup, currentPhase: MovePhase.Active, burstDirection: 1f);

        Assert.Equal(HandSide.Right, result);
    }

    [Fact]
    public void Resolve_EnteringActiveWithNegativeBurst_ReturnsLeft()
    {
        HandSide result = HandSideResolver.Resolve(
            current: HandSide.Right, previousPhase: MovePhase.Startup, currentPhase: MovePhase.Active, burstDirection: -1f);

        Assert.Equal(HandSide.Left, result);
    }

    [Fact]
    public void Resolve_EnteringActiveWithZeroBurst_ReturnsCurrentUnchanged()
    {
        // A JumpShot (no directional payload) entering Active must not flip
        // the hand — only a directional move (crossover) does.
        HandSide result = HandSideResolver.Resolve(
            current: HandSide.Left, previousPhase: MovePhase.Startup, currentPhase: MovePhase.Active, burstDirection: 0f);

        Assert.Equal(HandSide.Left, result);
    }

    [Fact]
    public void Resolve_AlreadyActiveNoFreshTransition_ReturnsCurrentUnchanged()
    {
        // Both previous and current phase are Active — not the entry tick —
        // so even a nonzero burst must not re-flip the hand every tick.
        HandSide result = HandSideResolver.Resolve(
            current: HandSide.Right, previousPhase: MovePhase.Active, currentPhase: MovePhase.Active, burstDirection: -1f);

        Assert.Equal(HandSide.Right, result);
    }

    [Fact]
    public void Resolve_StillInStartup_ReturnsCurrentUnchanged()
    {
        HandSide result = HandSideResolver.Resolve(
            current: HandSide.Left, previousPhase: MovePhase.Startup, currentPhase: MovePhase.Startup, burstDirection: 1f);

        Assert.Equal(HandSide.Left, result);
    }

    [Fact]
    public void Resolve_EnteringRecoveryFromActive_ReturnsCurrentUnchanged()
    {
        // Leaving Active is not an entry transition — the hand the move
        // landed on during Active should persist through Recovery.
        HandSide result = HandSideResolver.Resolve(
            current: HandSide.Right, previousPhase: MovePhase.Active, currentPhase: MovePhase.Recovery, burstDirection: 1f);

        Assert.Equal(HandSide.Right, result);
    }

    [Fact]
    public void Resolve_InactiveToInactive_ReturnsCurrentUnchanged()
    {
        HandSide result = HandSideResolver.Resolve(
            current: HandSide.Right, previousPhase: MovePhase.Inactive, currentPhase: MovePhase.Inactive, burstDirection: 0f);

        Assert.Equal(HandSide.Right, result);
    }
}
