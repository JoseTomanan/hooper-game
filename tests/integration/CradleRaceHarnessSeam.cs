namespace Hooper.Player;

// Harness-only seam for the #225 cradle-race integration test
// (CradleRaceTest.cs) — Reliable Begin(JumpShot) overtaking UnreliableOrdered
// dribble input, bypassing dead-dribble (Race 2 of #207). Same pattern and
// rationale as LayupRangeHarnessSeam.cs.
//
// ── Why a seam is unavoidable ─────────────────────────────────────────────
// RequestBeginMove is [Rpc(AnyPeer, CallLocal = false)] and sender-id gated,
// so a single offline headless instance can neither have it delivered
// remotely nor pass its Multiplayer.GetRemoteSenderId() check. This seam
// bypasses ONLY that RPC/authorization layer — which no gate under test
// consults — and lands on the real ApplyRequestedMove dispatch (the same
// production choke point RequestBeginMove itself calls), which is exactly
// what lets this test directly supply the clientWasAlreadyDribbling boolean
// a REAL remote client would have computed from its own local ball state
// (see RequestBeginMove's doc) — the disambiguator the #225 fix resolves the
// race with.
//
// Hard rule (architecture-contract invariant #11) is preserved transitively:
// ApplyRequestedMove's cradle-family branches still reach BeginCommittedMove,
// the one production choke point. Nothing here re-implements the fix.
public partial class PlayerController
{
    /// <summary>
    /// Test-only: drives the SERVER's authoritative move dispatch exactly as
    /// a legitimately-authorized client RPC would, skipping only the
    /// sender-id authorization check (see this file's doc). Deliberately
    /// takes the raw wire payload (moveId, param, clientWasAlreadyDribbling)
    /// rather than a CommittedMove instance, because reconstructing the move
    /// AND resolving the #225 cradle race from that payload IS the behavior
    /// under test. internal, not public — only needs to be visible to
    /// harnesses compiled into the same game assembly.
    /// </summary>
    internal void ApplyRequestedMoveForHarness(string moveId, float param, bool clientWasAlreadyDribbling)
        => ApplyRequestedMove(moveId, param, clientWasAlreadyDribbling);
}
