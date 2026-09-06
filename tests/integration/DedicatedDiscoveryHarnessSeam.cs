namespace Hooper.Networking
{
	public partial class ServerBrowser
	{
		/// <summary>
		/// Finds the exact production browser row backing an endpoint. Reading
		/// _rows, rather than Discovery.DiscoveredServers, proves RefreshRows has
		/// made that endpoint activatable through the real ItemList signal.
		/// </summary>
		internal int FindRowForHarness(string ip, int gamePort)
		{
			for (int i = 0; i < _rows.Count; i++)
				if (_rows[i].Ip == ip && _rows[i].GamePort == gamePort)
					return i;
			return -1;
		}
	}
}
