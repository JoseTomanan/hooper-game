using Hooper.Moves;

namespace Hooper.Player;

// Harness-only seam for the triple-threat / dead-dribble integration test
// (issue #193, ADR-0016). Same pattern and rationale as StealHarnessSeam.cs:
// a `partial` of PlayerController declared under tests/integration/ (compiled
// into the game assembly alongside every other engine script), so it can
// reach the private _machine and the private BeginCommittedMove without
// polluting the production class with test-only members.
//
// WHY BeginJumpShotForHarness calls BeginCommittedMove (not _machine.Begin()
// directly, the way StealHarnessSeam's BeginStealForHarness does): #193's
// whole point is the SIDE EFFECT BeginCommittedMove fires on a successful
// JumpShot begin (cradling a live dribble via BallController.
// CradleForShotStartup — see PlayerController.BeginCommittedMove). Calling
// _machine.Begin() directly would start the move but skip that side effect
// entirely, testing nothing this issue actually changed. Going through the
// real private method is what makes this an integration proof rather than a
// reimplementation of the fix under test.
public partial class PlayerController
{
    /// <summary>
    /// Test-only: begins a JumpShot through the SAME BeginCommittedMove path
    /// production code uses (SampleMoveInput's shoot branch, RequestBeginMove's
    /// "jumpshot" branch), bypassing only the input/RPC layer headless cannot
    /// drive. Returns BeginCommittedMove's result (false if a move is already
    /// running).
    /// </summary>
    internal bool BeginJumpShotForHarness() => BeginCommittedMove(new JumpShot());

    /// <summary>
    /// Test-only: attempts a feint (pump-fake) on this player's committed-move
    /// machine, identical to the E-key/L1 feint-modifier path SampleMoveInput
    /// would otherwise trigger. Exposed here because headless has no hardware
    /// to press that modifier with.
    /// </summary>
    internal bool FeintForHarness() => _machine.Feint();
}
