namespace Hooper.Ball;

public partial class BallController
{
    // Diagnostic only: owning simulation must ignore unrelated remote-history resets.
    internal void ResetPivotPresentationForHarness() => ResetRemotePresentation();
}
