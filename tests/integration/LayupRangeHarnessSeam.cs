namespace Hooper.Player;

// Harness seam for the SERVER-SIDE layup range gate (issue #236, ADR-0023).
//
// Same `partial class PlayerController` pattern as DefensiveMoveHarnessSeam /
// PlayerHarnessSeam: compiled into the game assembly (the game .csproj compiles
// tests/integration/**/*.cs, since those files are engine code referenced by
// .tscn scenes), so it reaches private members without production
// scripts/Player/PlayerController.cs carrying a test hook.
//
// ── Why BeginMoveForHarness is NOT sufficient here ────────────────────────
// Every existing move harness begins its move via BeginMoveForHarness, which
// calls BeginCommittedMove directly. That is exactly right when the thing under
// test is what a move DOES once begun (BlockTurnoverTest, StealTurnoverTest,
// LayupTest's own three scenarios). It is exactly wrong here: the thing under
// test is the gate that decides WHETHER a Layup begins at all, and that gate
// lives upstream of BeginCommittedMove, inside RequestBeginMove's dispatch.
// Calling BeginMoveForHarness(new Layup()) would sail straight past the code
// #236 is about and pass vacuously.
//
// ── Why a seam is unavoidable ─────────────────────────────────────────────
// The same reason DefensiveMoveHarnessSeam's doc gives: RequestBeginMove is
// [Rpc(AnyPeer, CallLocal = false)] and sender-id gated, so a single offline
// headless instance can neither have it delivered remotely nor pass its
// Multiplayer.GetRemoteSenderId() check (offline returns 0; the node is named
// "1"). This seam bypasses ONLY that RPC/authorization layer — which no gate
// under test consults — and lands on the real ApplyRequestedMove dispatch,
// which is why ApplyRequestedMove was split out of RequestBeginMove in the
// first place (see its doc).
//
// Hard rule (architecture-contract invariant #11) is preserved transitively:
// ApplyRequestedMove's layup branch still reaches BeginCommittedMove, the one
// production choke point. Nothing here re-implements the gate.
public partial class PlayerController
{
    /// <summary>
    /// Test-only: drives the SERVER's authoritative move dispatch exactly as a
    /// legitimately-authorized client RPC would, skipping only the sender-id
    /// authorization check (see this file's doc for why that is both necessary
    /// and safe).
    ///
    /// Deliberately takes the raw wire payload — a moveId string and a float
    /// param — rather than a CommittedMove instance, because reconstructing the
    /// move from that payload IS the behavior under test.
    ///
    /// internal, not public: only needs to be visible to the harnesses compiled
    /// into the same game assembly.
    ///
    /// clientWasAlreadyDribbling defaults to false (harmless — see
    /// ApplyRequestedMove's doc: only the four cradle-family moveIds consult
    /// it at all, and this scenario is not about #225's cradle race).
    /// </summary>
    internal void RequestMoveForHarness(string moveId, float param = 0f, bool clientWasAlreadyDribbling = false)
        => ApplyRequestedMove(moveId, param, clientWasAlreadyDribbling);
}
