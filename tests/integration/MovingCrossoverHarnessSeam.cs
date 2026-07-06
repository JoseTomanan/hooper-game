using Godot;

namespace Hooper.Player;

// Harness-only seam for the moving-crossover REMOTE-PATH integration
// scenario (issue #198 code-review fix round, ADR-0016). Same pattern and
// rationale as StealHarnessSeam.cs / TripleThreatHarnessSeam.cs: a `partial`
// of PlayerController declared under tests/integration/ (compiled into the
// game assembly alongside every other engine script), so it can reach the
// private _pendingRawStick field without polluting the production class
// with a test-only setter.
//
// WHY a seam is unavoidable: MovingCrossoverTest's original scenarios only
// drove TickServerOwnPlayer, whose exit-vector source is the real Input
// singleton (ReadInput()) — headless CAN drive that via Input.ActionPress.
// The server's copy of a REMOTE player instead reads _pendingRawStick, which
// production code only ever writes from inside SubmitInput — an
// [Rpc(AnyPeer, CallLocal=false)] method that is sender-id gated via
// Multiplayer.GetRemoteSenderId(), valid only during a live RPC call from an
// actual remote peer. A single offline headless instance (OfflineMultiplayerPeer)
// has no second peer to manufacture that call from. This seam sets the SAME
// field SubmitInput would, so the test still exercises the real
// TickServerRemotePlayer -> TickCommittedMoveBehavior(delta, _pendingRawStick)
// path the #198 fix actually shipped, not a reimplementation of it.
public partial class PlayerController
{
    /// <summary>
    /// Test-only: sets _pendingRawStick directly, bypassing the SubmitInput
    /// RPC layer headless cannot drive from a single offline instance (see
    /// class doc). internal, not public — only needs to be visible to other
    /// files compiled into the same game assembly
    /// (tests/integration/MovingCrossoverTest.cs, via the .csproj's explicit
    /// re-include, same as every other *HarnessSeam.cs file here).
    /// </summary>
    internal void SetPendingRawStickForHarness(Vector2 stick) => _pendingRawStick = stick;
}
