namespace Hooper.Ball;

// Included only with the integration harness. The optional partial call is
// erased from game-only builds, leaving no observer state in production.
public partial class BallController
{
	internal ulong SnapshotReceiptCountForHarness { get; private set; }
	internal BallState AuthoritativeStateForHarness { get; private set; }
	internal int AuthoritativeHolderForHarness { get; private set; }
	internal bool AuthoritativeClearedForHarness { get; private set; }
	internal bool AuthoritativeHasDribbledForHarness { get; private set; }
	internal bool SawAuthoritativeFlightForHarness { get; private set; }

	partial void ObserveSnapshotReceiptForHarness()
	{
		SnapshotReceiptCountForHarness++;
		AuthoritativeStateForHarness = _serverState;
		AuthoritativeHolderForHarness = _serverHolderPeerId;
		AuthoritativeClearedForHarness = _serverCleared;
		AuthoritativeHasDribbledForHarness = _serverHasDribbled;
		SawAuthoritativeFlightForHarness |= _serverState == BallState.InFlight;
	}
}
