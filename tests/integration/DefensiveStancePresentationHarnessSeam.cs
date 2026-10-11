using Godot;
using Hooper.Moves;

namespace Hooper.Player;

public partial class PlayerController
{
    // Exercise the production snapshot receiver and remote presentation path;
    // no direct cosmetic field writes. Transport is proven in NetDefensiveStanceTest.
    internal void StanceReceivePresentationForHarness(bool stance, MovePhase phase = MovePhase.Inactive,
        string moveId = "", bool pivot = false)
    {
        ReceiveState(0, GlobalPosition, Vector3.Zero, (int)phase, 0, moveId, 0,
            Heading, (int)HandSide, false, pivot, Heading + 1f, false, false, stance);
        TickClientRemotePlayer();
    }
}
