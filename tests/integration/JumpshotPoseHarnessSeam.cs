using Hooper.Moves;

namespace Hooper.Player;

public partial class PlayerController
{
    // This fixture tests sampled presentation timelines, not gameplay or ENet.
    // Both real rigs receive the same explicit Recovery sample while only the
    // actor has a prior Startup sample which could contaminate its modifier.
    internal void SetJumpshotPoseDisplayForHarness(MovePhase phase, int frame, long sourceTick) =>
        AnimationDisplaySnapshot = new AnimationDisplayFrame(
            phase == MovePhase.Inactive ? "" : "jumpshot", phase, frame, HandSide.Right, false, sourceTick);
}

public partial class JumpshotPose
{
    // Runtime mutation: preserve the real previous phase/anchor, but make a
    // source-clock discontinuity appear adjacent to the production gap guard.
    internal void PretendAdjacentJumpshotSourceTickForHarness(long nextSourceTick) =>
        _clock = nextSourceTick - 1;
}
