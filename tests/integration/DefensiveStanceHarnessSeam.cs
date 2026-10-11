using Godot;
using Hooper.Moves;
namespace Hooper.Player;
public partial class PlayerController
{
    internal void StanceRequestForHarness(bool held, Vector2 input, bool sprint = false)
    { _pendingProtectRequest = held; _pendingSprintRequest = sprint; _pendingInput = _pendingRawStick = input; }
    internal void StanceTickAuthorityForHarness(double delta) => TickServerRemotePlayer(delta);
    internal bool StanceBeginForHarness(CommittedMove move) => BeginCommittedMove(move);
    internal MovePhase StancePhaseForHarness => _machine.Phase;
    internal int StanceFrameForHarness => _machine.FrameInPhase;
    internal float StanceOwedYawForHarness => _pivot.LatchedYaw;
}
