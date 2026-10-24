namespace Hooper.Ball;

public partial class BallController
{
    // Reuse the production connection lifecycle reset for the neutral-history control.
    // Kept in the harness compile surface; no production reset policy changes.
    internal void ResetReboundPresentationForHarness() => ResetRemotePresentation();
}
