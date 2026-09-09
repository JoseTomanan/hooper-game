namespace Hooper.Networking
{
	public partial class DiscoveryListener
	{
		private static int _startListeningCallCountForHarness;

		/// <summary>Harness-only observation of the real UDP-listener lifecycle state.</summary>
		internal bool IsListeningForHarness => _listening;

		internal static int StartListeningCallCountForHarness => _startListeningCallCountForHarness;

		internal static void ResetStartListeningCallCountForHarness() =>
			_startListeningCallCountForHarness = 0;

		partial void OnStartListeningForHarness() => _startListeningCallCountForHarness++;
	}
}
