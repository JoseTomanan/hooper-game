using Hooper.Moves;

namespace Hooper.Player;

// General-purpose observability accessors shared across multiple headless
// integration harnesses (originally added alongside StealHarnessSeam for
// issue #175, since folded out of that per-move file during issue #216's
// harness-seam consolidation — these three are not steal-specific, so they
// get their own file rather than living inside DefensiveMoveHarnessSeam.cs).
// Same `partial class PlayerController` pattern as every other seam file
// under tests/integration/: compiled into the game assembly, reaching
// private CommittedMoveMachine state without polluting the production class.
public partial class PlayerController
{
    /// <summary>
    /// Test-only (issue #175): exposes the player's real committed-move Phase
    /// so a harness can observe a SERVER-side phase transition (e.g.
    /// EndActiveEarly()) without a client process to read a ReceiveState
    /// broadcast from.
    /// </summary>
    internal MovePhase PhaseForHarness => _machine.Phase;

    /// <summary>
    /// Test-only (issue #175): exposes CommittedMoveMachine.WasRecoveryEnteredEarly
    /// — the level-triggered signal that gets piggybacked on ReceiveState and
    /// drives ShouldForceRecovery client-side. Proving THIS flag flips true at
    /// the real moment a defensive move resolves (BallController.
    /// ResolveStealAttempts / ResolveBlockAttempts calling
    /// EndResolvedDefensiveMove) is the server-side half of the #175 fix;
    /// StealTurnoverTest's shadow-client block proves the client-side half
    /// (ShouldForceRecovery + ForceState) against these real values.
    /// </summary>
    internal bool WasRecoveryEnteredEarlyForHarness => _machine.WasRecoveryEnteredEarly;

    /// <summary>Test-only (issue #175): the real machine's CurrentMove.Id, or "" if none.</summary>
    internal string CurrentMoveIdForHarness => _machine.CurrentMove?.Id ?? "";
}
