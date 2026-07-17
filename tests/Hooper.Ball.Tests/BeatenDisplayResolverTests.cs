using Hooper.Player;

namespace Hooper.Ball.Tests;

/// <summary>
/// Unit tests for BeatenDisplayResolver — the pure role decision extracted for
/// issue #102 so "which peer's beaten-window value is trustworthy for
/// display" is verified without a running Godot instance.
///
/// Deliberately a NARROWER predicate than DisplayPhaseResolver's
/// LocalMachineDrivesDisplay: only the server ever judges a whiff
/// (BallController.ResolveBeatenWindowTriggers is IsServer-gated), so the
/// local _beaten field is trustworthy on exactly ONE role, not three.
///
/// ── Test naming ──────────────────────────────────────────────────────────────
/// [MethodUnderTest]_[Scenario]_[ExpectedOutcome]
/// </summary>
public class BeatenDisplayResolverTests
{
    // ── The role predicate: true ONLY for the server ────────────────────────

    [Fact]
    public void LocalStateIsAuthoritative_Server_True()
    {
        Assert.True(BeatenDisplayResolver.LocalStateIsAuthoritative(isServer: true));
    }

    [Fact]
    public void LocalStateIsAuthoritative_Client_False()
    {
        // Narrower than DisplayPhaseResolver: a client's OWN predicted player
        // still can't know it was just ruled beaten, because only the server
        // ever evaluates the whiff.
        Assert.False(BeatenDisplayResolver.LocalStateIsAuthoritative(isServer: false));
    }

    // ── Resolve selects the right value per role ────────────────────────────

    [Fact]
    public void Resolve_Server_ReturnsLocalValue()
    {
        bool result = BeatenDisplayResolver.Resolve(
            isServer: true, localIsBeaten: true, serverIsBeaten: false);

        Assert.True(result);
    }

    [Fact]
    public void Resolve_ClientOwnPlayer_IgnoresLocalValue_ReturnsBroadcast()
    {
        // THE narrowing case: even though this represents the client's OWN
        // player (where DisplayPhaseResolver would trust the local machine),
        // the beaten window is NOT locally knowable — the broadcast value
        // must win regardless of what the (always-false, in production)
        // local read happens to say.
        bool result = BeatenDisplayResolver.Resolve(
            isServer: false, localIsBeaten: true, serverIsBeaten: false);

        Assert.False(result);
    }

    [Fact]
    public void Resolve_ClientCopyOfRemote_ReturnsBroadcast()
    {
        bool result = BeatenDisplayResolver.Resolve(
            isServer: false, localIsBeaten: false, serverIsBeaten: true);

        Assert.True(result);
    }

    [Fact]
    public void Resolve_ServerCopyOfRemote_ReturnsLocalValue()
    {
        bool result = BeatenDisplayResolver.Resolve(
            isServer: true, localIsBeaten: false, serverIsBeaten: true);

        Assert.False(result);
    }
}
