using Godot;
using Hooper.Ball;

namespace Hooper.Systems;

/// <summary>
/// On-screen possession indicator (issue #51): who holds the ball and whether
/// the possession has been cleared (take-it-back rule, ADR-0008). A sibling of
/// ScoreHud — same discipline, different source: ScoreHud renders GameManager's
/// score; this renders BallController's possession. Splitting them keeps each
/// HUD a single-responsibility view of one authoritative node.
///
/// ── Why this exists ──────────────────────────────────────────────────────
/// The clear rule gates scoring: a make while "not cleared" does not count
/// (ADR-0008). That is invisible — and so confusing — unless the player can see
/// the cleared state, which is exactly why ADR-0008 lists the HUD as part of
/// the rule rather than optional polish. It also makes the #52 verification
/// observable (you can watch possession flip and the clear state change).
///
/// ── Display-only ──────────────────────────────────────────────────────────
/// Reads the broadcast holder and the server-authoritative cleared flag from
/// BallController; never sets state, never talks to the network. Identical
/// behaviour on both peers, framed from the local player's point of view.
///
/// ── Push, not poll ──────────────────────────────────────────────────────
/// Refreshes only on BallController.PossessionChanged — one update per
/// possession/clear event, not 60 per second (same pattern as ScoreHud +
/// GameManager.ScoreChanged).
///
/// Scene wiring is human editor work (issue #51, hitl): add a Label to
/// Main.tscn's HUD layer and attach this script. See EDITOR_TASKS.md.
/// </summary>
public partial class PossessionHud : Label
{
	/// <summary>Cached BallController, found via the "ball" group (there is exactly one ball).</summary>
	private BallController _ball;

	public override void _Ready()
	{
		_ball = GetTree().GetFirstNodeInGroup("ball") as BallController;
		if (_ball == null)
		{
			GD.PrintErr("[PossessionHud] No node in group 'ball' found. Possession will not display until BallController is in the scene.");
			Text = "(no ball)";
			return;
		}

		// Push-driven: refresh only when possession or the cleared flag changes.
		_ball.PossessionChanged += Refresh;

		// Render the current possession before the first change fires.
		Refresh(_ball.StateMachine.HolderPeerId, _ball.IsCleared);
	}

	public override void _ExitTree()
	{
		// Drop the delegate so Godot doesn't hold a dangling reference to a freed
		// HUD (same lifecycle hygiene as ScoreHud).
		if (_ball == null) return;
		_ball.PossessionChanged -= Refresh;
	}

	/// <summary>
	/// Renders the possession line from the local player's point of view.
	/// holderPeerId 0 means no holder (the ball is loose or in flight), in which
	/// case the cleared flag is not meaningful and is omitted.
	/// </summary>
	private void Refresh(int holderPeerId, bool cleared)
	{
		int localId = Multiplayer.GetUniqueId();

		string who = holderPeerId == 0   ? "Loose ball"
		           : holderPeerId == localId ? "You have the ball"
		           : "Opponent has the ball";

		// Clear state only matters while someone holds the ball.
		string clear = holderPeerId == 0 ? ""
		             : cleared ? "   •   Cleared"
		             : "   •   Take it back";

		Text = who + clear;
	}
}
