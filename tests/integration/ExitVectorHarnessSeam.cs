using Godot;

namespace Hooper.Player;

// Harness-only seam for issue #210's fix — same `partial class PlayerController`
// pattern as every other seam file under tests/integration/ (compiled into the
// game assembly, reaching a private field without adding a test hook to the
// production class).
//
// WHY this seam is unavoidable: _authoritativeExitVector is written ONLY from
// inside the private RequestExitVector RPC handler, itself only reachable over
// the wire from a genuinely REMOTE client's own JustEnteredActive tick (see
// NetExitVectorRpcTest.cs's dual-instance proof of THAT path). A single
// offline instance driving a move on a "2"-named node (the TickServerRemotePlayer
// role — see MovingCrossoverHarnessSeam.cs's identical reasoning for
// SetPendingRawStickForHarness) has no second peer to send that RPC from. This
// seam sets the SAME field the RPC handler would, so a single-instance harness
// can still exercise the REAL TickServerRemotePlayer -> TickCommittedMoveBehavior
// (_authoritativeExitVector ?? _pendingRawStick) composition every burst-family
// move (including Spin) actually reads, without needing a second Godot process
// just to prove the SHARED seam reaches a move OTHER than Crossover.
public partial class PlayerController
{
    /// <summary>
    /// Test-only: sets _authoritativeExitVector directly, bypassing the
    /// RequestExitVector RPC layer a single offline instance cannot drive from
    /// a real remote peer (see class doc). Must be called AFTER
    /// BeginCommittedMove/BeginMoveForHarness — that method unconditionally
    /// resets this field to null at the START of every attempt (issue #210's
    /// stale-echo guard), so setting it first would just be immediately wiped.
    /// </summary>
    internal void SetAuthoritativeExitVectorForHarness(Vector2 v) => _authoritativeExitVector = v;
}
