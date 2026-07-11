using Hooper.Moves;

namespace Hooper.Player;

// Harness-only seam for the crossover-ball-sweep integration test (issue #195,
// code-review fix #4). Same pattern/rationale as StealHarnessSeam.cs and
// TripleThreatHarnessSeam.cs: a `partial` of PlayerController declared under
// tests/integration/ (compiled into the game assembly alongside every other
// engine script), reaching the private ReceiveState/TickClientRemotePlayer
// without adding a test hook to the production class.
//
// WHY this seam is unavoidable: CrossoverSweepTest's "crossover-sweep" scenario
// only exercises the LOCALLY-driven HandSide flip (the server/holder-client
// role, where HandSide is set inline by TickCommittedMoveBehavior). The other
// role BallController.AdvanceHandSweep must handle identically — the REMOTE
// client's copy of the opponent, where HandSide only updates when a
// ReceiveState broadcast lands (TickClientRemotePlayer's _hasNewState gate) —
// is exactly the class of gap issue #195's own spec cites as motivation (the
// M7b #69 remote-display gap). A true second client can't run inside this
// offline single-instance harness (OfflineMultiplayerPeer, no MultiplayerPeer
// assigned — see CrossoverSweepTest's class doc), so this seam drives the
// production ReceiveState/TickClientRemotePlayer code path directly with a
// hand-built payload whose HandSide differs from the node's current value —
// mimicking a landed broadcast — instead of reimplementing the flip.
public partial class PlayerController
{
    /// <summary>
    /// Test-only: calls the REAL, private ReceiveState RPC handler directly
    /// (a same-assembly partial can reach private members of the type it
    /// extends — Godot's [Rpc] attribute only gates dispatch over the wire,
    /// not a direct in-process C# call, so this runs the identical body
    /// production network delivery would). Mirrors the harness's own current
    /// pos/vel/heading for every field except handSide, so only the ONE
    /// broadcast dimension under test changes.
    /// </summary>
    internal void ReceiveStateForHarness(HandSide handSide) =>
        ReceiveState(
            ackSeq: 0,
            pos: GlobalPosition,
            vel: Velocity,
            movePhase: (int)MovePhase.Inactive,
            frameInPhase: 0,
            moveId: "",
            moveParam: 0f,
            heading: Heading,
            handSide: (int)handSide,
            endedActiveEarly: false,
            pivotHasLatch: false,
            pivotLatchedYaw: 0f);

    /// <summary>
    /// Test-only: runs the REAL, private TickClientRemotePlayer — the exact
    /// method that adopts _serverHandSide (set by ReceiveStateForHarness above)
    /// onto the public HandSide property on every real remote-client tick.
    /// Calling it directly bypasses only the IsServer/IsLocalPlayer role
    /// branch in _PhysicsProcess (this harness runs everything as the server,
    /// so that branch would never route here on its own) — the state-apply
    /// logic itself is unmodified production code.
    /// </summary>
    internal void ApplyRemoteStateForHarness() => TickClientRemotePlayer();
}
