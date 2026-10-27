using Hooper.Moves;

namespace Hooper.Player;

public partial class PlayerController
{
    // Bypass only input/RPC delivery; retain every production begin side effect.
    internal bool BeginFourMoveEntryForHarness(CommittedMove move) => BeginCommittedMove(move);
    internal bool FourMoveJustEnteredActiveForHarness => _machine.JustEnteredActive;
    internal CommittedMove FourMoveCurrentMoveForHarness => _machine.CurrentMove;
}
