using Hooper.Moves;

namespace Hooper.Player;

// Harness-only seam for step-back / retreat-dribble integration tests (issue
// #197, ADR-0016). Same pattern as BehindTheBackHarnessSeam.cs: a `partial`
// of PlayerController declared under tests/integration/, compiled into the
// game assembly, reaching the private BeginCommittedMove so the harness
// exercises the REAL dead-dribble gate (#193) and Begin() legality check
// production code uses, not a reimplementation of them.
public partial class PlayerController
{
    /// <summary>
    /// Test-only: begins a StepBack through the SAME BeginCommittedMove path
    /// production code uses (SampleMoveInput's vertical-gesture branch,
    /// RequestBeginMove's "stepback" branch). Returns BeginCommittedMove's
    /// result — false if refused (the ordinary Inactive-only Begin() guard;
    /// StepBack has no Held-holder gate — see BeginCommittedMove's comment).
    /// </summary>
    internal bool BeginStepBackForHarness() => BeginCommittedMove(new StepBack());

    /// <summary>
    /// Test-only: begins a RetreatDribble through the SAME BeginCommittedMove
    /// path production code uses. Returns BeginCommittedMove's result — false
    /// if refused (the Held-holder dead-dribble gate this move shares with
    /// Crossover/Hesitation/BehindTheBack, or the ordinary Inactive-only
    /// Begin() guard).
    /// </summary>
    internal bool BeginRetreatDribbleForHarness() => BeginCommittedMove(new RetreatDribble());
}
