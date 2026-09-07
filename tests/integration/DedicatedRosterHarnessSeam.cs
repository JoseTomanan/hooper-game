namespace Hooper.Networking
{
	public partial class DiscoveryListener
	{
		/// <summary>Harness-only observation of the real UDP-listener lifecycle state.</summary>
		internal bool IsListeningForHarness => _listening;
	}
}

namespace Hooper.Systems
{
	public partial class ScoreHud
	{
		/// <summary>Harness-only call through the HUD's real opponent resolver.</summary>
		internal int OpponentPeerIdForHarness() => OpponentPeerId();
	}
}
