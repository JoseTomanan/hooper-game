namespace Hooper.Ball
{
    public partial class BallController
    {
        internal void ProtectionResolveHeldForHarness() => ResolveHeldStealAttempts();
    }
}

namespace Hooper.Player
{
    public partial class PlayerController
    {
        internal void ProtectionApplyAnimationForHarness() => ApplyAnimation();
        internal void ProtectionPivotForHarness(bool active) => _pivot = active
            ? new HeadingMath.PivotState(true, Heading + 1f) : HeadingMath.PivotState.None;
        internal bool ProtectionReceivedForHarness => _hasContactSnapshot && _serverProtect;
        internal void ProtectionTickRemoteForHarness() => TickClientRemotePlayer();
    }
}
