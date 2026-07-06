using Hooper.Moves;

namespace Hooper.Player;

// Harness-only seam for BehindTheBack integration tests (issue #194,
// ADR-0016). Same pattern as TripleThreatHarnessSeam.cs's
// BeginCrossoverForHarness: a `partial` of PlayerController declared under
// tests/integration/, compiled into the game assembly, reaching the private
// BeginCommittedMove so the harness exercises the REAL dead-dribble gate
// (#193) and Begin() legality check production code uses, not a
// reimplementation of them. Headless has no "move_size_up" modifier hardware
// to hold during a flick, so this is also the only way to drive
// BehindTheBack's begin path at all in the harness.
public partial class PlayerController
{
    /// <summary>
    /// Test-only: begins a BehindTheBack through the SAME BeginCommittedMove
    /// path production code uses (SampleMoveInput's move_size_up-modified
    /// flick branch, RequestBeginMove's "behindtheback" branch). Returns
    /// BeginCommittedMove's result — false if refused (Held-holder dead-
    /// dribble gate, or the ordinary Inactive-only Begin() guard).
    /// </summary>
    internal bool BeginBehindTheBackForHarness(float flickSign) => BeginCommittedMove(new BehindTheBack(flickSign));
}
