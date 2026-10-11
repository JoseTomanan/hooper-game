using Godot;
using Hooper.Moves;

namespace Hooper.Player;

public partial class PlayerController
{
    // Presentation fixtures only. Real request/transport progression is covered by
    // PostTest/NetPostTest; this seam holds the display inputs still while the rig runs.
    internal void PostSetPresentationForHarness(bool posting, bool established, HandSide hand,
        MovePhase phase = MovePhase.Inactive, bool pivot = false, bool protect = false)
    {
        _requestedPost = posting;
        _effectivePost = posting;
        _postEstablished = established;
        _requestedProtect = protect;
        HandSide = hand;
        _pivot = pivot ? new HeadingMath.PivotState(true, Heading + 1f) : HeadingMath.PivotState.None;
        _machine.ForceState(phase, 0, phase == MovePhase.Inactive ? null : new TurnaroundShot(Heading));
    }

    internal void PostSetRemotePresentationForHarness(bool posting, HandSide hand,
        MovePhase phase = MovePhase.Inactive, bool pivot = false)
    {
        _serverPost = posting;
        _serverPostEstablished = posting;
        _serverMovePhase = phase;
        _serverMoveId = phase == MovePhase.Inactive ? "" : "turnaround";
        HandSide = hand;
        _pivot = pivot ? new HeadingMath.PivotState(true, Heading + 1f) : HeadingMath.PivotState.None;
    }
}
