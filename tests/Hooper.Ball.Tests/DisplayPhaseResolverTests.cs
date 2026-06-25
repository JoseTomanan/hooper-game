using Hooper.Moves;
using Hooper.Player;

namespace Hooper.Ball.Tests;

/// <summary>
/// Unit tests for DisplayPhaseResolver — the pure role decision extracted for
/// M7b (issue #69) so the "which phase drives this node's display" logic is
/// verified without a running Godot instance.
///
/// The whole decision is the four-role matrix in DisplayPhaseResolver's doc:
/// the local CommittedMoveMachine is the display source whenever this peer
/// simulates the node (host own, server-side remote copy, client own), and the
/// broadcast phase is the source for the one role that never advances its local
/// machine — the client's copy of the opponent.
///
/// ── Test naming ──────────────────────────────────────────────────────────────
/// [MethodUnderTest]_[Scenario]_[ExpectedOutcome]
/// </summary>
public class DisplayPhaseResolverTests
{
    // ── The role predicate: true in 3 of 4 roles, false only for client-remote ──

    [Fact]
    public void LocalMachineDrivesDisplay_HostOwnPlayer_True()
    {
        Assert.True(DisplayPhaseResolver.LocalMachineDrivesDisplay(isServer: true, isLocalPlayer: true));
    }

    [Fact]
    public void LocalMachineDrivesDisplay_ServerCopyOfRemote_True()
    {
        // The server ticks EVERY player's machine, so even its copy of a remote
        // player has an authoritative local phase to display.
        Assert.True(DisplayPhaseResolver.LocalMachineDrivesDisplay(isServer: true, isLocalPlayer: false));
    }

    [Fact]
    public void LocalMachineDrivesDisplay_ClientOwnPlayer_True()
    {
        // The client predicts its own machine locally, so its own phase is live.
        Assert.True(DisplayPhaseResolver.LocalMachineDrivesDisplay(isServer: false, isLocalPlayer: true));
    }

    [Fact]
    public void LocalMachineDrivesDisplay_ClientCopyOfRemote_False()
    {
        // THE bug case: this role never advances its local machine, so reading it
        // would always show Inactive. This single false is the entire fix.
        Assert.False(DisplayPhaseResolver.LocalMachineDrivesDisplay(isServer: false, isLocalPlayer: false));
    }

    // ── Resolve selects the right phase per role ──────────────────────────────

    [Fact]
    public void Resolve_ClientCopyOfRemote_ReturnsServerPhase()
    {
        // The opponent on your screen: local machine is stuck Inactive, so the
        // broadcast's Active is what must render.
        MovePhase result = DisplayPhaseResolver.Resolve(
            isServer: false, isLocalPlayer: false,
            localPhase: MovePhase.Inactive, serverPhase: MovePhase.Active);

        Assert.Equal(MovePhase.Active, result);
    }

    [Fact]
    public void Resolve_ClientOwnPlayer_ReturnsLocalPhase()
    {
        // Your own predicted player: the live local phase wins over the ~1-RTT
        // stale broadcast (same staleness reasoning as ReconcileFromServer Step 0).
        MovePhase result = DisplayPhaseResolver.Resolve(
            isServer: false, isLocalPlayer: true,
            localPhase: MovePhase.Startup, serverPhase: MovePhase.Inactive);

        Assert.Equal(MovePhase.Startup, result);
    }

    [Fact]
    public void Resolve_ServerCopyOfRemote_ReturnsLocalPhase()
    {
        MovePhase result = DisplayPhaseResolver.Resolve(
            isServer: true, isLocalPlayer: false,
            localPhase: MovePhase.Recovery, serverPhase: MovePhase.Inactive);

        Assert.Equal(MovePhase.Recovery, result);
    }
}
