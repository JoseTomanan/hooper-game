using Hooper.Moves;

namespace Hooper.Player;

// Harness-only seam for BetweenTheLegs integration tests (issue #199,
// ADR-0016). Same pattern as BehindTheBackHarnessSeam.cs's
// BeginBehindTheBackForHarness: a `partial` of PlayerController declared
// under tests/integration/, compiled into the game assembly, reaching the
// private BeginCommittedMove so the harness exercises the REAL dead-dribble
// gate (#193) and Begin() legality check production code uses, not a
// reimplementation of them. Headless has no "move_finesse" modifier hardware
// to hold during a flick, so this is also the only way to drive
// BetweenTheLegs's begin path at all in the harness.
public partial class PlayerController
{
    /// <summary>
    /// Test-only: begins a BetweenTheLegs through the SAME BeginCommittedMove
    /// path production code uses (SampleMoveInput's move_finesse-modified
    /// flick branch, RequestBeginMove's "betweenthelegs" branch). Returns
    /// BeginCommittedMove's result — false if refused (Held-holder dead-
    /// dribble gate, or the ordinary Inactive-only Begin() guard).
    /// </summary>
    internal bool BeginBetweenTheLegsForHarness(float flickSign) => BeginCommittedMove(new BetweenTheLegs(flickSign));
}
