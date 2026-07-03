using Godot;
using Hooper.Ball;

namespace Hooper.Ball.Tests;

/// <summary>
/// Issue #120 — voiding a shot released while the holder is out of bounds.
///
/// In real half-court 1v1 the ball is dead the instant the handler steps on or
/// over the line, so a shot released from out of bounds must NOT count — it is a
/// turnover (ADR-0008; ADR-0014 ranks real-ball authority over arcade feel).
///
/// BallController.ApplyShootLocally extends a Godot Node and cannot run headless
/// (ADR-0004), so — exactly as FlightTerminationIntegrationTests and
/// ShotScatterCurveCharacterizationTests do — we replicate the decision over the
/// SAME pure collaborators the real guard composes:
///
///     bool oob       = CourtBounds.IsOutOfBounds(holderPos, min, max);
///     int  recipient = recipientEligible ? opponent : 0;
///     var  result    = OobResolution.Resolve(oob, isServer, recipient);
///     bool shotVoided = isServer &amp;&amp; oob;   // the new ApplyShootLocally guard
///
/// The point of #120 is that this turnover decision is the SAME rule the
/// carry-OOB path already uses (ResolvePlayerOutOfBounds) — one OOB definition,
/// one recipient-eligibility path, one ADR-0008 award — just consulted at the
/// shot-release seam instead of only on the carry tick.
/// </summary>
public class OobShotReleaseTests
{
    // Court rectangle matching BallController's default exports (and the values
    // FlightTerminationIntegrationTests pins) — both derive from
    // CourtBounds.Default{Min,Max} (single source of truth).
    private static readonly Vector2 CourtMin = CourtBounds.DefaultMin;
    private static readonly Vector2 CourtMax = CourtBounds.DefaultMax;

    private const int Shooter  = 1;
    private const int Opponent = 2;

    /// <summary>
    /// Mirrors the ApplyShootLocally guard exactly: on the server, a holder who
    /// is out of bounds at release voids the shot; the turnover recipient is
    /// resolved by the same OobResolution table the carry rule uses.
    /// </summary>
    private static (bool shotVoided, OobResolution.Result turnover) DecideRelease(
        Vector3 holderPos, bool isServer, bool recipientEligible)
    {
        bool oob = CourtBounds.IsOutOfBounds(holderPos, CourtMin, CourtMax);
        int recipient = recipientEligible ? Opponent : 0;
        OobResolution.Result turnover = OobResolution.Resolve(oob, isServer, recipient);
        bool shotVoided = isServer && oob;
        return (shotVoided, turnover);
    }

    [Fact]
    public void InBoundsRelease_ShotProceeds_NoTurnover()
    {
        // Holder comfortably inside the court releases a shot — normal scoring path.
        var (voided, turnover) = DecideRelease(new Vector3(0f, 1f, 5f), isServer: true, recipientEligible: true);

        Assert.False(voided);
        Assert.Equal(OobResolution.Action.NoOp, turnover.Action);
    }

    [Fact]
    public void OnSidelineRelease_CountsAsInBounds_ShotProceeds()
    {
        // The boundary is in-bounds (CourtBounds uses strict </>), so a release
        // with a toe exactly on the line still scores — #120's "toe-on-line"
        // edge resolved by reusing the one existing OOB definition, not a new one.
        var (voided, turnover) = DecideRelease(new Vector3(CourtMax.X, 1f, 5f), isServer: true, recipientEligible: true);

        Assert.False(voided);
        Assert.Equal(OobResolution.Action.NoOp, turnover.Action);
    }

    [Fact]
    public void OobRelease_Server_WithEligibleOpponent_VoidsShotAndAwardsTurnover()
    {
        // Holder past the sideline (X > CourtMax.X) at release: dead ball.
        var (voided, turnover) = DecideRelease(new Vector3(6f, 1f, 5f), isServer: true, recipientEligible: true);

        Assert.True(voided);                                    // shot does not count
        Assert.Equal(OobResolution.Action.Award, turnover.Action);
        Assert.Equal(Opponent, turnover.RecipientPeerId);       // possession to the opponent
    }

    [Fact]
    public void OobRelease_PastBaseline_AlsoVoids()
    {
        // Z beyond the far baseline is equally out — proves the rule is not
        // sideline-only (CourtBounds tests both X and Z).
        var (voided, turnover) = DecideRelease(new Vector3(0f, 1f, 12.5f), isServer: true, recipientEligible: true);

        Assert.True(voided);
        Assert.Equal(OobResolution.Action.Award, turnover.Action);
    }

    [Fact]
    public void OobRelease_Server_NoEligibleRecipient_StillVoidsShot_ClampFallback()
    {
        // Opponent absent or also out of bounds: there is nobody to award to, so
        // OobResolution yields ClampFallback (no authoritative award) — but the
        // shot is STILL voided on the server, because being OOB at release is a
        // dead ball regardless of who can receive it. This matches the carry-OOB
        // path, which likewise issues no award (and no clamp for a held ball)
        // when no recipient is eligible; the holder simply keeps the dead ball
        // until the next resolution tick.
        var (voided, turnover) = DecideRelease(new Vector3(6f, 1f, 5f), isServer: true, recipientEligible: false);

        Assert.True(voided);
        Assert.Equal(OobResolution.Action.ClampFallback, turnover.Action);
    }

    [Fact]
    public void OobRelease_OnClient_DoesNotVoidLocally_ServerReconciles()
    {
        // A non-server peer never computes possession (server-authoritative): the
        // predicting client shoots locally and is corrected by the next
        // ReceiveState broadcast. So the guard does NOT fire on a client even when
        // its own holder view is out of bounds.
        var (voided, turnover) = DecideRelease(new Vector3(6f, 1f, 5f), isServer: false, recipientEligible: true);

        Assert.False(voided);
        Assert.Equal(OobResolution.Action.ClampFallback, turnover.Action); // non-server → no award
    }
}
