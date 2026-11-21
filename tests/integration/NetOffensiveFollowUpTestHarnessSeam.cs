using Hooper.Moves;

namespace Hooper.Player;

public partial class PlayerController
{
    internal (CommittedMove Move, MovePhase Phase, int Frame, int Request) FollowUpSimulationForHarness =>
        (_machine.CurrentMove, _machine.Phase, _machine.FrameInPhase, CurrentOffensiveRequest);

    internal int DeferredOffensiveBeginsForHarness => _deferredOffensiveBegins.Count;

    internal (string Move, MovePhase Phase) LastAuthoritativeMoveForFollowUpHarness =>
        (_serverMoveId, _serverMovePhase);
}
