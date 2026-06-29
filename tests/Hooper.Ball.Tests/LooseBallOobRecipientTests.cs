using Godot;
using Hooper.Ball;

namespace Hooper.Ball.Tests;

/// <summary>
/// Issue #118 part 2 — a loose ball going out of bounds BEFORE any shot has been
/// fired must not award possession to an arbitrary spawn-order player.
///
/// BallController.TickLoose resolves the OOB turnover recipient as
///
///     recipient = _lastShooterPeerId == 0 ? 0 : OtherPlayerPeerId(_lastShooterPeerId);
///     OobResolution.Resolve(oob, IsServer, recipient);
///
/// The bug this pins: <c>OtherPlayerPeerId(0)</c> returns the FIRST player child
/// (its parse loop skips id 0), so without the <c>_lastShooterPeerId == 0</c>
/// short-circuit a pre-shot OOB would award the ball to a spawn-order-arbitrary
/// player. TickLoose extends a Godot Node (ADR-0004), so — as
/// FlightTerminationIntegrationTests does — we replicate the recipient decision
/// over the pure collaborators. <c>simulatedOpponent</c> stands in for the value
/// <c>OtherPlayerPeerId</c> would return once a shooter exists.
/// </summary>
public class LooseBallOobRecipientTests
{
    private const int Opponent = 2; // what OtherPlayerPeerId would return post-shot

    /// <summary>Mirrors the TickLoose recipient short-circuit exactly.</summary>
    private static int ResolveRecipient(int lastShooterPeerId, int simulatedOpponent)
        => lastShooterPeerId == 0 ? 0 : simulatedOpponent;

    [Fact]
    public void PreShotOob_Server_DoesNotAwardArbitrarily_ClampsInstead()
    {
        // No shot fired yet: _lastShooterPeerId == 0.
        int recipient = ResolveRecipient(lastShooterPeerId: 0, simulatedOpponent: Opponent);
        Assert.Equal(0, recipient); // short-circuited, NOT OtherPlayerPeerId(0)'s spawn pick

        OobResolution.Result oob = OobResolution.Resolve(
            isOutOfBounds: true, isServer: true, resolvedRecipient: recipient);

        // ClampFallback, not Award — the ball stays in play; nobody is handed it.
        Assert.Equal(OobResolution.Action.ClampFallback, oob.Action);
    }

    [Fact]
    public void RegressionWithoutGuard_WouldHaveAwardedArbitrarily()
    {
        // Documents the bug: had the call site passed OtherPlayerPeerId(0) (≈ the
        // first player, here Opponent) straight through, the server would have
        // issued a real Award with no game context. This is what the guard prevents.
        OobResolution.Result buggy = OobResolution.Resolve(
            isOutOfBounds: true, isServer: true, resolvedRecipient: Opponent);
        Assert.Equal(OobResolution.Action.Award, buggy.Action);
    }

    [Fact]
    public void PostShotOob_Server_AwardsOppositeTheShooter()
    {
        // A shot HAS been fired: the short-circuit is bypassed and the resolved
        // opponent is awarded the dead ball, exactly as before this fix.
        int recipient = ResolveRecipient(lastShooterPeerId: 1, simulatedOpponent: Opponent);
        Assert.Equal(Opponent, recipient);

        OobResolution.Result oob = OobResolution.Resolve(
            isOutOfBounds: true, isServer: true, resolvedRecipient: recipient);

        Assert.Equal(OobResolution.Action.Award, oob.Action);
        Assert.Equal(Opponent, oob.RecipientPeerId);
    }

    [Fact]
    public void PreShotInBounds_NoOp()
    {
        // Sanity: in-bounds is unaffected regardless of shooter context.
        int recipient = ResolveRecipient(lastShooterPeerId: 0, simulatedOpponent: Opponent);
        OobResolution.Result oob = OobResolution.Resolve(
            isOutOfBounds: false, isServer: true, resolvedRecipient: recipient);
        Assert.Equal(OobResolution.Action.NoOp, oob.Action);
    }
}
