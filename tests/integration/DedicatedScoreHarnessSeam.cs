namespace Hooper.Systems
{
	public partial class GameManager
	{
		/// <summary>
		/// Harness-only score-channel mutation. It defaults false, is internal to
		/// the game assembly, and has no production input/config surface. The #375
		/// journey arms it only after both clients prove a healthy score baseline.
		/// </summary>
		internal bool SuppressScoreRpcForHarness { get; set; }
	}
}
