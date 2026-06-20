using Godot;

namespace Hooper.Systems;

/// <summary>
/// Minimal on-screen score display (issue #26). No polish — a single Label
/// that shows both players' scores and, on game-over, who won.
///
/// ── Why this node holds no logic ─────────────────────────────────────────
/// Same discipline as the rest of the project (ADR-0004's spirit, applied to
/// UI): all scoring truth lives in the pure Scoreboard, owned by the server's
/// GameManager and mirrored to clients by broadcast. This node only RENDERS
/// what GameManager already knows — it never counts, never decides a winner,
/// never talks to the network. It reads through GameManager.ScoreOf, which
/// transparently sources from the server's live Scoreboard on the host and
/// the broadcast mirror on a client, so this exact script behaves identically
/// on both peers (satisfying #26's "visible in both networked instances").
///
/// ── Push, not poll ────────────────────────────────────────────────────────
/// The HUD does not check the score every frame. It refreshes only when
/// GameManager emits ScoreChanged / GameOver — which fire on every peer the
/// instant a basket's broadcast lands (see GameManager.ReceiveScoreState).
/// One refresh per basket, not 60 per second.
///
/// ── Identity for labelling ───────────────────────────────────────────────
/// "You" is always the local peer (Multiplayer.GetUniqueId()); the opponent
/// is the only other player in a 1v1. On the host (local id 1) the opponent
/// is the single remote peer; on a client the opponent is always the host
/// (peer 1, by NetworkManager's listen-server convention). 0 means "no
/// opponent connected yet" and renders as a dash.
///
/// Scene wiring is human editor work (issue #27, hitl): add a Label to
/// Main.tscn's HUD layer and attach this script. See EDITOR_TASKS.md.
/// </summary>
public partial class ScoreHud : Label
{
	/// <summary>
	/// Cached GameManager, found via the "game_manager" group — the same
	/// discovery pattern BallController / PlayerController use, for the same
	/// reason (there is exactly one GameManager and it is a sibling system
	/// node, not something wired per-HUD). Null-guarded loudly so a missing
	/// editor step (#27) surfaces in the console rather than as a silently
	/// blank scoreboard.
	/// </summary>
	private GameManager _gameManager;

	public override void _Ready()
	{
		// Deferred so GameManager._Ready() (a sibling) has run and joined the
		// group before we look it up — avoids a false-positive error and a
		// missed signal subscription if ScoreHud is listed first in the scene.
		Callable.From(() =>
		{
			_gameManager = GetTree().GetFirstNodeInGroup("game_manager") as GameManager;
			if (_gameManager == null)
			{
				GD.PrintErr("[ScoreHud] No node in group 'game_manager' found. The score will not display until GameManager is added to the scene (issue #27).");
				Text = "(no GameManager)";
				return;
			}

			// Push-driven: refresh only when score state actually changes. Both
			// signals fire on every peer when a basket broadcast lands (the server
			// emits them locally too — see GameManager.BroadcastAndEmit).
			_gameManager.ScoreChanged += RefreshScore;
			_gameManager.GameOver     += ShowWinner;

			// Render the opening 0–0 before any basket has happened.
			RefreshScore();
		}).CallDeferred();
	}

	public override void _ExitTree()
	{
		// Unsubscribe so Godot doesn't hold a dangling delegate to a freed HUD
		// (same lifecycle hygiene NetworkManager applies to its signal handlers).
		if (_gameManager == null) return;
		_gameManager.ScoreChanged -= RefreshScore;
		_gameManager.GameOver     -= ShowWinner;
	}

	/// <summary>Resolves the opponent's peer id for a 1v1 (see class doc).</summary>
	private int OpponentPeerId()
	{
		int localId = Multiplayer.GetUniqueId();

		// A client's only opponent is the host (peer 1). The host's opponent
		// is the single connected remote peer, or 0 if none has joined yet.
		if (localId != 1)
			return 1;

		int[] peers = Multiplayer.GetPeers();
		return peers.Length > 0 ? peers[0] : 0;
	}

	/// <summary>Re-renders the running score line. Called on each ScoreChanged.</summary>
	private void RefreshScore()
	{
		int localId    = Multiplayer.GetUniqueId();
		int opponentId = OpponentPeerId();

		int you      = _gameManager.ScoreOf(localId);
		string them  = opponentId == 0 ? "—" : _gameManager.ScoreOf(opponentId).ToString();

		Text = $"You: {you}   Opponent: {them}";
	}

	/// <summary>
	/// Appends the result line when the match ends. winnerPeerId is the
	/// authoritative winner from the broadcast (never 0 when this fires).
	/// </summary>
	private void ShowWinner(int winnerPeerId)
	{
		// Re-render the final score first, then state the outcome from the
		// local player's point of view.
		RefreshScore();
		bool localWon = winnerPeerId == Multiplayer.GetUniqueId();
		Text += localWon ? "\nYou win!" : "\nYou lose.";
	}
}
