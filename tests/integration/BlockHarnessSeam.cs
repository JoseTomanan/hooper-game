using Hooper.Moves;

namespace Hooper.Player;

// Harness-only seam for the block-turnover integration test (issue #98,
// ADR-0016). Same pattern as StealHarnessSeam.cs's BeginStealForHarness: a
// `partial` of PlayerController declared under tests/integration/ (compiled
// into the game assembly alongside every other engine script, per the game
// csproj's tests/integration/**/*.cs Compile Include), reaching the private
// _machine so the harness can drive a defender's BlockMove Active window
// directly instead of through the input/RPC layer headless cannot exercise
// (RequestBeginMove is [Rpc(AnyPeer, CallLocal=false)] and sender-id gated;
// there is no remote peer in a single offline instance to deliver it, and
// SampleMoveInput reads hardware input headless does not provide).
//
// Calls _machine.Begin(new BlockMove()) directly rather than the private
// BeginCommittedMove wrapper (contrast TripleThreatHarnessSeam.BeginJumpShotForHarness,
// which deliberately goes through BeginCommittedMove because #193's fix IS the
// side effect that method fires). BlockMove has no such side effect —
// BeginCommittedMove's only special-cased moves are Crossover/Hesitation/
// BehindTheBack (dead-dribble gate) and JumpShot (dribble cradle); a BlockMove
// falls through to the plain `_machine.Begin(move)` call either way — so
// reaching _machine.Begin() directly here is equivalent and mirrors
// StealHarnessSeam's own precedent for a defensive move with no begin-time
// side effect.
public partial class PlayerController
{
    /// <summary>
    /// Test-only: begins a BlockMove on this player's committed-move machine,
    /// bypassing the input/RPC layer the fix does not touch. Returns Begin()'s
    /// result (false if a move is already running — same guard as production).
    /// </summary>
    internal bool BeginBlockForHarness() => _machine.Begin(new BlockMove());
}
