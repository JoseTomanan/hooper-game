using Hooper.Moves;

namespace Hooper.Player;

public partial class PlayerController
{
    internal int CapturedFeintCount { get; private set; }
    internal (ulong PhysicsFrame, MovePhase Phase, int Frame, bool Accepted) CapturedFeint { get; private set; }

    partial void ObserveShotFeintForHarness(MovePhase phase, int frame, bool accepted)
    {
        CapturedFeintCount++;
        CapturedFeint = (Godot.Engine.GetPhysicsFrames(), phase, frame, accepted);
    }
}
