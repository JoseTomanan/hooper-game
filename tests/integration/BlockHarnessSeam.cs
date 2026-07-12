using Hooper.Moves;

namespace Hooper.Player;

// Harness-only seam for the block-turnover integration test (issue #98,
// ADR-0016). Same pattern as StealHarnessSeam.cs's BeginStealForHarness: a
// `partial` of PlayerController declared under tests/integration/ (compiled
// into the game assembly alongside every other engine script, per the game
// csproj's tests/integration/**/*.cs Compile Include), reaching the private
// BeginCommittedMove so the harness can drive a defender's BlockMove Active
// window directly instead of through the input/RPC layer headless cannot
// exercise (RequestBeginMove is [Rpc(AnyPeer, CallLocal=false)] and sender-id
// gated; there is no remote peer in a single offline instance to deliver it,
// and SampleMoveInput reads hardware input headless does not provide).
//
// Calls BeginCommittedMove(new BlockMove()) — the SAME production choke
// point RequestBeginMove's "block" branch calls — rather than reaching
// _machine.Begin() directly. A prior version of this seam called
// _machine.Begin() directly on the theory that BlockMove has no
// BeginCommittedMove-gated side effect (unlike JumpShot's dribble cradle or
// Crossover/Hesitation/BehindTheBack's dead-dribble gate) and so the two were
// equivalent — that reasoning MISSED that BeginCommittedMove unconditionally
// clears the in-place-pivot latch (`_pivot = HeadingMath.PivotState.None`)
// on every successful Begin(), regardless of move type (#172). Skipping
// BeginCommittedMove meant this harness was not actually exercising that
// production begin path for BlockMove, contrary to what its doc claimed.
// Calling BeginCommittedMove here directly makes the equivalence true by
// construction instead of by (incomplete) argument — mirroring
// StealHarnessSeam's own precedent of reaching production code through the
// real choke point rather than re-deriving its effects.
public partial class PlayerController
{
    /// <summary>
    /// Test-only: begins a BlockMove on this player's committed-move machine
    /// via the same BeginCommittedMove path production input reaches,
    /// bypassing only the input/RPC layer the fix does not touch. Returns
    /// BeginCommittedMove's result (false if a move is already running —
    /// same phase guard as production; BeginCommittedMove's dead-dribble gate
    /// is scoped to Crossover/Hesitation/BehindTheBack only, so it never
    /// applies to a BlockMove).
    /// </summary>
    internal bool BeginBlockForHarness() => BeginCommittedMove(new BlockMove());
}
