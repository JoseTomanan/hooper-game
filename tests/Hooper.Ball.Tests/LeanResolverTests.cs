using Hooper.Moves;
using Hooper.Player;

namespace Hooper.Ball.Tests;

/// <summary>
/// Unit tests for LeanResolver.ResolveTilt — the pure tilt resolver extracted
/// from PlayerController (M7a, issues #38/#39) so that burst-lean logic can
/// be verified without a running Godot instance.
///
/// The function returns a signed tilt in radians (≈12°) only during
/// MovePhase.Active; all other phases return exactly 0f. The design rationale
/// per phase is encoded in the comments of each relevant test below and in
/// LeanResolver.cs itself.
///
/// Cosmetic-only: the return value only drives the visual mesh transform;
/// collision shapes, Velocity, and all authoritative/replicated state are
/// completely unaffected (ADR-0004).
///
/// ── Test naming ──────────────────────────────────────────────────────────────
/// [MethodUnderTest]_[Scenario]_[ExpectedOutcome]
/// Each test contains exactly one logical assertion.
/// </summary>
public class LeanResolverTests
{
    // ── Non-Active phases return zero ─────────────────────────────────────────

    [Fact]
    public void ResolveTilt_Inactive_ReturnsZero()
    {
        float result = LeanResolver.ResolveTilt(MovePhase.Inactive, burstDirection: 1f);

        Assert.Equal(0f, result);
    }

    [Fact]
    public void ResolveTilt_Startup_ReturnsZero()
    {
        // The Startup frame is the telegraph window: the opponent reads *which
        // direction* the move is going from the player's body being still and
        // wound up. A pre-lean here would reveal the direction too early and
        // collapse the commitment mind-game that is central to ADR-0003.
        float result = LeanResolver.ResolveTilt(MovePhase.Startup, burstDirection: 1f);

        Assert.Equal(0f, result);
    }

    [Fact]
    public void ResolveTilt_Recovery_ReturnsZero()
    {
        // The Recovery frame is the punish window (ADR-0003): an upright,
        // slowing stop reads as vulnerability and deliberate commitment. Leaning
        // during recovery would read as comedy jank rather than the weight-
        // bearing stop that ADR-0003 requires. Staying upright here is
        // observable by the opponent and constitutes the design's risk-reward.
        float result = LeanResolver.ResolveTilt(MovePhase.Recovery, burstDirection: 1f);

        Assert.Equal(0f, result);
    }

    // ── Active phase: signed lean ─────────────────────────────────────────────

    [Fact]
    public void ResolveTilt_ActiveWithPositiveBurstDirection_ReturnsPositiveValue()
    {
        float result = LeanResolver.ResolveTilt(MovePhase.Active, burstDirection: 1f);

        Assert.True(result > 0f);
    }

    [Fact]
    public void ResolveTilt_ActiveWithNegativeBurstDirection_ReturnsNegativeValue()
    {
        float result = LeanResolver.ResolveTilt(MovePhase.Active, burstDirection: -1f);

        Assert.True(result < 0f);
    }

    [Fact]
    public void ResolveTilt_ActivePositiveVsNegativeBurstDirection_ResultsHaveOppositeSigns()
    {
        // Symmetry check: a burst left and a burst right must lean in opposite
        // directions by the same magnitude. Asymmetry here would mean the mesh
        // leans the wrong way on one side of the court.
        float leanRight = LeanResolver.ResolveTilt(MovePhase.Active, burstDirection: 1f);
        float leanLeft  = LeanResolver.ResolveTilt(MovePhase.Active, burstDirection: -1f);

        Assert.Equal(Math.Sign(leanRight), -Math.Sign(leanLeft));
    }

    [Fact]
    public void ResolveTilt_ActiveWithZeroBurstDirection_ReturnsZero()
    {
        // burstDirection == 0 means no burst is committed; Math.Sign(0) == 0,
        // so the mesh stays upright even though the phase is Active.
        float result = LeanResolver.ResolveTilt(MovePhase.Active, burstDirection: 0f);

        Assert.Equal(0f, result);
    }
}
