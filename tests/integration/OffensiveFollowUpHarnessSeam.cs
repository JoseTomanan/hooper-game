using Hooper.Moves;

namespace Hooper.Player;

public partial class PlayerController
{
    internal (CommittedMove Move, MovePhase Phase, int Frame) OffensiveMoveForHarness
        => (_machine.CurrentMove, _machine.Phase, _machine.FrameInPhase);

    internal int PendingOffensiveInputsForHarness => _offensiveFollowUps.Count;
}
