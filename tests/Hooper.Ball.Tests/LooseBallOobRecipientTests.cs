using Godot;
using Hooper.Ball;

namespace Hooper.Ball.Tests;

/// <summary>
/// Issue #118 — who a loose ball out of bounds is awarded to.
///
/// BallController.TickLoose resolves the OOB turnover recipient as
///
///     int recipient = OobResolution.ResolveRecipient(
///         _lastToucherPeerId,
///         _lastToucherPeerId == 0 ? 0 : OtherPlayerPeerId(_lastToucherPeerId));
///     OobResolution.Resolve(oob, IsServer, recipient);
///
/// Two design calls are pinned here (both confirmed against ADR-0014 real-ball
/// authority, recorded in ADR-0008 §Amendment 2026-06-30):
///
///   • Part 1 — the recipient derives from the last TOUCHER (the player who most
///     recently possessed the ball), not the last SHOOTER. _lastToucherPeerId
///     advances on every possession change, so a rebounder who fumbles the ball
///     OOB is awarded AGAINST, never handed it straight back. The old last-shooter
///     key never moved on a rebound, so it would have. See
///     PostReboundFumbleOob_AwardsOppositeTheRebounder_NotBackToThem.
///
///   • Part 2 — before anyone has touched the ball (pre-tipoff, toucher 0), there
///     is no possession history, so ResolveRecipient short-circuits to 0 and the
///     ball clamps rather than teleporting to a spawn-order-arbitrary player.
///
/// These call the real production seam OobResolution.ResolveRecipient directly
/// (no local mirror), so they cannot silently drift from the call site. The
/// engine-facing half — that _lastToucherPeerId is actually written on every
/// possession change (tipoff + AwardPossession) — lives in BallController, which
/// extends a Godot Node (ADR-0004) and so is verified by review of those
/// write-sites, not unit-instantiable here.
/// </summary>
public class LooseBallOobRecipientTests
{
    [Fact]
    public void ResolveRecipient_AfterATouch_AwardsTheOpponent()
    {
        // A player has touched the ball (toucher 1); the opponent the caller
        // resolved (OtherPlayerPeerId(1) == 2) is who gets a subsequent OOB ball.
        Assert.Equal(2, OobResolution.ResolveRecipient(lastToucherPeerId: 1, opponentOfToucher: 2));
    }

    [Fact]
    public void ResolveRecipient_BeforeAnyTouch_ShortCircuitsToZero()
    {
        // Pre-tipoff: nobody has touched the ball. Even though the caller passes
        // a non-zero resolved opponent (what OtherPlayerPeerId(0) would arbitrarily
        // return), there is no possession history to award opposite of → 0,
        // which Resolve maps to ClampFallback (ball stays in play, #118 part 2).
        Assert.Equal(0, OobResolution.ResolveRecipient(lastToucherPeerId: 0, opponentOfToucher: 2));
    }

    [Fact]
    public void PostReboundFumbleOob_AwardsOppositeTheRebounder_NotBackToThem()
    {
        // The #118 part-1 bug, pinned. Player 1 shoots and misses; player 2
        // rebounds — so the LAST TOUCHER is now 2 (not the shooter 1). Player 2
        // then fumbles the ball out of bounds.
        //
        //   Correct (last-toucher rule): award opposite the rebounder 2 → 1.
        //   Old bug (last-shooter):      OtherPlayerPeerId(1) → 2, i.e. handed
        //                                straight back to the player who put it out.
        //
        // OtherPlayerPeerId(2) == 1 in a 1v1; the caller passes that in.
        int recipient = OobResolution.ResolveRecipient(lastToucherPeerId: 2, opponentOfToucher: 1);
        Assert.Equal(1, recipient);

        OobResolution.Result oob = OobResolution.Resolve(
            isOutOfBounds: true, isServer: true, resolvedRecipient: recipient);

        Assert.Equal(OobResolution.Action.Award, oob.Action);
        Assert.Equal(1, oob.RecipientPeerId); // the original shooter, never the fumbling rebounder
    }

    [Fact]
    public void PreTouchOob_Server_DoesNotAwardArbitrarily_ClampsInstead()
    {
        // End-to-end of part 2: toucher 0 → recipient 0 → ClampFallback (not
        // Award). The ball stays in play; nobody is handed it.
        int recipient = OobResolution.ResolveRecipient(lastToucherPeerId: 0, opponentOfToucher: 2);

        OobResolution.Result oob = OobResolution.Resolve(
            isOutOfBounds: true, isServer: true, resolvedRecipient: recipient);

        Assert.Equal(OobResolution.Action.ClampFallback, oob.Action);
    }

    [Fact]
    public void RegressionWithoutGuard_WouldHaveAwardedArbitrarily()
    {
        // Documents the part-2 bug: had the call site passed a non-zero recipient
        // through with no possession history (≈ OtherPlayerPeerId(0)'s spawn pick),
        // the server would have issued a real Award with no game context. The
        // toucher==0 short-circuit in ResolveRecipient is what prevents this.
        OobResolution.Result buggy = OobResolution.Resolve(
            isOutOfBounds: true, isServer: true, resolvedRecipient: 2);
        Assert.Equal(OobResolution.Action.Award, buggy.Action);
    }

    [Fact]
    public void PreTouchInBounds_NoOp()
    {
        // Sanity: in-bounds is unaffected regardless of possession context.
        int recipient = OobResolution.ResolveRecipient(lastToucherPeerId: 0, opponentOfToucher: 2);
        OobResolution.Result oob = OobResolution.Resolve(
            isOutOfBounds: false, isServer: true, resolvedRecipient: recipient);
        Assert.Equal(OobResolution.Action.NoOp, oob.Action);
    }
}
