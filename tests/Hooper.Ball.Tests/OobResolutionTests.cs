using Hooper.Ball;

namespace Hooper.Ball.Tests;

/// <summary>
/// Unit tests for OobResolution — the pure OOB award decision helper
/// (issue #63, ADR-0008 §Amendment 2026-06-28).  Headless (ADR-0004):
/// no Godot Node, no engine singletons.
///
/// ── Test naming convention ───────────────────────────────────────────────
/// [MethodUnderTest]_[Scenario]_[ExpectedOutcome]
/// One logical assertion per test.
///
/// ── Decision table under test ────────────────────────────────────────────
///   isOob  isServer  recipient  → Action
///   ──────────────────────────────────────────────────────────
///   false  *         *          → NoOp
///   true   true      != 0       → Award (RecipientPeerId = recipient)
///   true   true      == 0       → ClampFallback
///   true   false     *          → ClampFallback
/// </summary>
public class OobResolutionTests
{
    // Representative peer ids: any non-zero int is a valid peer id in Godot.
    private const int PlayerOne = 1;
    private const int PlayerTwo = 2;

    // ── In-bounds branch ─────────────────────────────────────────────────

    [Fact]
    public void Resolve_InBounds_Server_WithRecipient_ReturnsNoOp()
    {
        // Ball inside the play court — OOB rule does not fire regardless of
        // server role or opponent presence.
        var result = OobResolution.Resolve(isOutOfBounds: false, isServer: true, resolvedRecipient: PlayerTwo);
        Assert.Equal(OobResolution.Action.NoOp, result.Action);
    }

    [Fact]
    public void Resolve_InBounds_Client_NoRecipient_ReturnsNoOp()
    {
        // Non-server, no opponent, but ball is in bounds — still NoOp.
        var result = OobResolution.Resolve(isOutOfBounds: false, isServer: false, resolvedRecipient: 0);
        Assert.Equal(OobResolution.Action.NoOp, result.Action);
    }

    // ── Award branch ─────────────────────────────────────────────────────

    [Fact]
    public void Resolve_OutOfBounds_Server_WithRecipient_ReturnsAward()
    {
        // Server + OOB + opponent present → authoritative dead-ball award.
        // This is the normal 2-player OOB turnover (ADR-0008 §Amendment 2026-06-28).
        var result = OobResolution.Resolve(isOutOfBounds: true, isServer: true, resolvedRecipient: PlayerTwo);
        Assert.Equal(OobResolution.Action.Award, result.Action);
    }

    [Fact]
    public void Resolve_OutOfBounds_Server_WithRecipient_RecipientPeerIdIsPassedThrough()
    {
        // The recipient peer id must reach the caller unchanged so
        // BallController.TickLoose can call AwardPossession(RecipientPeerId).
        var result = OobResolution.Resolve(isOutOfBounds: true, isServer: true, resolvedRecipient: PlayerTwo);
        Assert.Equal(PlayerTwo, result.RecipientPeerId);
    }

    [Fact]
    public void Resolve_OutOfBounds_Server_WithRecipient_DifferentId_RecipientPeerIdCorrect()
    {
        // Verify the peer id is forwarded generically, not hard-coded to a
        // specific constant — test with a different id to confirm.
        var result = OobResolution.Resolve(isOutOfBounds: true, isServer: true, resolvedRecipient: PlayerOne);
        Assert.Equal(PlayerOne, result.RecipientPeerId);
    }

    // ── ClampFallback branch — no opponent ───────────────────────────────

    [Fact]
    public void Resolve_OutOfBounds_Server_NoRecipient_ReturnsClampFallback()
    {
        // Server + OOB but recipient == 0 (solo editor test with no opponent).
        // Nobody to award the ball to — fall back to CourtBounds.Clamp so
        // the ball stays in play (ADR-0008 §Amendment 2026-06-28 "no opponent present").
        var result = OobResolution.Resolve(isOutOfBounds: true, isServer: true, resolvedRecipient: 0);
        Assert.Equal(OobResolution.Action.ClampFallback, result.Action);
    }

    // ── ClampFallback branch — non-server client ─────────────────────────

    [Fact]
    public void Resolve_OutOfBounds_Client_WithRecipient_ReturnsClampFallback()
    {
        // Non-server peer (client) + OOB + opponent present.
        // Clients keep the clamp; the server's ReceiveState broadcast reconciles
        // divergence — identical to how all other possession changes propagate
        // (ADR-0002, ADR-0008 §Amendment 2026-06-28 "Non-server peers").
        var result = OobResolution.Resolve(isOutOfBounds: true, isServer: false, resolvedRecipient: PlayerTwo);
        Assert.Equal(OobResolution.Action.ClampFallback, result.Action);
    }

    [Fact]
    public void Resolve_OutOfBounds_Client_NoRecipient_ReturnsClampFallback()
    {
        // Non-server + OOB + no opponent: still ClampFallback (not NoOp).
        // This tests the client path with the solo-editor recipient value.
        var result = OobResolution.Resolve(isOutOfBounds: true, isServer: false, resolvedRecipient: 0);
        Assert.Equal(OobResolution.Action.ClampFallback, result.Action);
    }
}
