namespace Hooper.Networking
{
	public partial class DiscoveryListener
	{
		/// <summary>Harness observability for the positive half of a no-row mutation.</summary>
		internal bool IsListeningForHarness => _listening;
	}

	public partial class ServerBrowser
	{
		/// <summary>
		/// Reports whether production discovery observed the expected game port;
		/// avoids treating an empty UI caused by a failed listener bind as evidence.
		/// </summary>
		internal bool DiscoveredExpectedPortForHarness(int gamePort)
		{
			foreach (ServerListEntry entry in Discovery.DiscoveredServers)
				if (entry.GamePort == gamePort)
					return true;
			return false;
		}
	}
}
