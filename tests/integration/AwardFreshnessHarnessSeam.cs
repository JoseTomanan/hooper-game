using Godot;

namespace Hooper.Player;

// Harness-only seam for the #224 stale-input-auto-dribble-on-award
// integration test (AwardFreshnessTest.cs). Same pattern and rationale as
// MovingCrossoverHarnessSeam.cs: a `partial` of PlayerController declared
// under tests/integration/ (compiled into the game assembly), so it can
// reach the private _pendingInput/_serverAckedSeq fields without polluting
// the production class with a test-only setter.
//
// WHY a seam is unavoidable: production code only ever writes _pendingInput/
// _serverAckedSeq together from inside SubmitInput — an
// [Rpc(AnyPeer, CallLocal=false)] method that is sender-id gated via
// Multiplayer.GetRemoteSenderId(), valid only during a live RPC call from an
// actual remote peer. A single offline headless instance
// (OfflineMultiplayerPeer) has no second peer to manufacture that call from.
// This seam sets the SAME two fields SubmitInput would, so the test still
// exercises the real TickServerRemotePlayer -> CheckAutoStartDribble ->
// BallController.TryStartDribble chain the #224 fix actually gates, not a
// reimplementation of it.
public partial class PlayerController
{
    /// <summary>
    /// Test-only: sets _pendingInput and _serverAckedSeq directly, bypassing
    /// the SubmitInput RPC layer headless cannot drive from a single offline
    /// instance (see class doc). Mirrors exactly what SubmitInput's body
    /// assigns (see that method) — no seq-staleness guard replicated here,
    /// since the harness fully controls call order and never needs it.
    /// internal, not public — only needs to be visible to other files
    /// compiled into the same game assembly.
    /// </summary>
    internal void SetPendingInputForHarness(int seq, Vector2 input)
    {
        _serverAckedSeq = seq;
        _pendingInput = input;
    }
}
