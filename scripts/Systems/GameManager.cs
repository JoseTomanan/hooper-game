using Godot;

namespace Hooper.Systems;

/// <summary>
/// Glue node that owns the live Scoreboard on the SERVER and a broadcast
/// mirror on every peer (ADR-0002, issues #24/#25). This is the ONLY node
/// allowed to mutate Scoreboard — BallController calls RegisterBasket() on
/// a clean make; it never touches Scoreboard directly.
///
/// ── Why score is NOT predicted (the crux of this file) ───────────────────
/// Every other piece of game state in this project IS predicted on every
/// peer: PlayerController predicts movement, BallController predicts ball
/// position/state. Score is deliberately the odd one out — there is no
/// reconciliation channel for a mispredicted score the way there is for
/// position. A divergent position is a vector that ReconcileFromServer can
/// blend or snap; a divergent SCORE is an integer with no "blend" operation
/// that makes sense, and worse, a phantom point a client briefly believed
/// in (e.g. it predicted a make the server later rules a rim-out) has no
/// way to be quietly subtracted back out without either flickering the HUD
/// or leaving a permanent ghost point if the correction is ever missed.
/// So: only the server ever calls Scoreboard.RegisterBasket. Every other
/// peer's GameManager holds nothing but a passive mirror of the server's
/// last broadcast, exactly the same "discrete identity is forced, never
/// blended" treatment BallController already gives BallState (see its
/// ReconcileFromServer comment) — score is just discrete identity with no
/// continuous component at all, so there is nothing left to predict.
///
/// ── Discovery ──────────────────────────────────────────────────────────
/// Found via the "game_manager" group rather than an editor NodePath export,
/// because BallController/PlayerController are not the ones authoring the
/// scene tree relationship to GameManager — GameManager is a sibling system
/// node, not something wired per-player. Group lookup avoids needing a new
/// export wired N times (once per player node) for something there is only
/// ever one instance of.
/// </summary>
public partial class GameManager : Node
{
	/// <summary>Score a player must reach or exceed to win. Server-only meaning (see Scoreboard).</summary>
	[Export] public int TargetScore { get; set; } = 11;

	/// <summary>Emitted on every peer when score or game-over state changes from a broadcast (or, on the server, immediately after a local mutation).</summary>
	[Signal] public delegate void ScoreChangedEventHandler();

	/// <summary>Emitted on every peer the instant game-over state is broadcast/observed. winnerPeerId is never 0 when this fires.</summary>
	[Signal] public delegate void GameOverEventHandler(int winnerPeerId);

	// ── Server-only ground truth ──────────────────────────────────────────

	/// <summary>
	/// The single live Scoreboard instance. Non-null ONLY on the server —
	/// clients never construct one (see class doc for why). Guard every use
	/// with IsServer; a client accidentally reading this would see null, not
	/// a stale/empty Scoreboard, which is the loud-failure behaviour we want
	/// rather than a silently-wrong score of 0 for everyone.
	/// </summary>
	private Scoreboard _scoreboard;

	// ── Client mirror (also populated on the server's own broadcast loop-back is NOT needed — see RegisterBasket) ──

	/// <summary>
	/// Mirror of the two peers' scores as last broadcast by the server.
	/// 1v1 cap (NetworkManager.MaxClients = 2 + host = 2 total), but modelled
	/// as explicit fields rather than a Dictionary because the broadcast RPC
	/// itself is fixed-arity (see ReceiveScoreState) — there is no variable-
	/// length collection at any point in this class, so a Dictionary would
	/// add indirection without adding flexibility.
	/// </summary>
	private int _peerAId, _peerAScore;
	private int _peerBId, _peerBScore;

	private bool _mirrorIsGameOver;
	private int _mirrorWinnerPeerId;

	private bool IsServer => Multiplayer.IsServer();

	// ── Lifecycle ──────────────────────────────────────────────────────────

	public override void _Ready()
	{
		// Group membership, not a NodePath export: see class doc "Discovery".
		AddToGroup("game_manager");

		// (#24 doubt cycle 1, finding #1) Constructing the Scoreboard
		// unconditionally on every peer was the first draft — wrong, because
		// it would let a client's GameManager silently hold a second, fake
		// "ground truth" that nothing ever mutates correctly (only the server
		// is meant to call RegisterBasket, but a stray future bug calling it
		// on a client would corrupt a real Scoreboard instead of being a
		// visible no-op). Restricting construction to the server makes that
		// entire bug class impossible: a client has no Scoreboard object to
		// even accidentally mutate.
		if (IsServer)
			_scoreboard = new Scoreboard(TargetScore);
	}

	// ── Unified read surface (works regardless of role) ──────────────────

	/// <summary>True once the game has ended, from whichever source is authoritative for this peer (Scoreboard on the server, the broadcast mirror on a client).</summary>
	public bool IsGameOver => IsServer ? _scoreboard.IsGameOver : _mirrorIsGameOver;

	/// <summary>Winning peer id, or 0 before game over.</summary>
	public int WinnerPeerId => IsServer ? _scoreboard.WinnerPeerId : _mirrorWinnerPeerId;

	/// <summary>Current score of the given peer.</summary>
	public int ScoreOf(int peerId)
	{
		if (IsServer)
			return _scoreboard.ScoreOf(peerId);

		if (peerId == _peerAId) return _peerAScore;
		if (peerId == _peerBId) return _peerBScore;
		return 0; // never broadcast for this peer yet — matches Scoreboard.ScoreOf's own "0 if never scored"
	}

	// ── Server-only mutation entry point ──────────────────────────────────

	/// <summary>
	/// Called by BallController on a clean make. SERVER-ONLY: every other
	/// peer's call is a guarded no-op, because every other peer's
	/// BallController also runs this exact tick (the make is predicted on
	/// every peer — see BallController.TickInFlight) but only the server's
	/// copy is allowed to turn that prediction into a real point.
	///
	/// (#24 doubt cycle 1, finding #2) Doubt-checked: does a client that
	/// locally predicted a make ever get a stuck/incorrect HUD because its
	/// own call here was a no-op? No — the client never reads its own
	/// no-op'd call for anything; it reads ScoreOf/IsGameOver, both of which
	/// come from the broadcast mirror on a client (see the property
	/// implementations above), not from any local mutation attempt. The
	/// guard below is purely defensive; nothing on a client currently
	/// depends on its return value or side effects.
	/// </summary>
	public void RegisterBasket(int scorerPeerId)
	{
		if (!IsServer) return;

		_scoreboard.RegisterBasket(scorerPeerId);
		BroadcastAndEmit();
	}

	/// <summary>
	/// Builds the fixed-arity broadcast payload from the server's Scoreboard
	/// and both fires the RPC to every other peer AND updates/emits locally
	/// — the server's own HUD needs ScoreChanged/GameOver too, and the
	/// broadcast RPC has CallLocal = false (see ReceiveScoreState doc), so
	/// the server must apply its own "broadcast" by hand exactly once here.
	/// </summary>
	private void BroadcastAndEmit()
	{
		(int peerAId, int peerAScore, int peerBId, int peerBScore) = SnapshotPeers();

		Rpc(MethodName.ReceiveScoreState,
			peerAId, peerAScore, peerBId, peerBScore,
			_scoreboard.WinnerPeerId, _scoreboard.IsGameOver);

		// Server applies the same values to its own mirror fields so
		// ScoreOf/IsGameOver read consistently via IsServer ? scoreboard : mirror
		// — though IsServer always reads _scoreboard directly, keeping the
		// mirror fields updated here too costs nothing and avoids a latent
		// trap if a future edit changes the property getters to always read
		// the mirror.
		ApplyMirror(peerAId, peerAScore, peerBId, peerBScore, _scoreboard.WinnerPeerId, _scoreboard.IsGameOver);

		EmitSignal(SignalName.ScoreChanged);
		if (_scoreboard.IsGameOver)
			EmitSignal(SignalName.GameOver, _scoreboard.WinnerPeerId);
	}

	/// <summary>
	/// Reads both peer ids straight from MultiplayerApi rather than tracking
	/// join order ourselves: peer 1 is always the server/host (NetworkManager
	/// convention), and Multiplayer.GetPeers() returns every OTHER connected
	/// peer id, which for this 1v1 cap is at most one id. If the second slot
	/// hasn't joined yet (e.g. game-managed in a lobby/solo test), peerBId/
	/// peerBScore are sent as 0/0 — 0 is never a valid peer id (Scoreboard's
	/// convention), so the HUD can treat a 0 id as "no second player yet."
	/// </summary>
	private (int peerAId, int peerAScore, int peerBId, int peerBScore) SnapshotPeers()
	{
		const int hostPeerId = 1;
		int peerAId = hostPeerId;
		int peerAScore = _scoreboard.ScoreOf(hostPeerId);

		int peerBId = 0;
		int peerBScore = 0;
		int[] others = Multiplayer.GetPeers();
		if (others.Length > 0)
		{
			// (#24 doubt cycle 2, finding #1) Cap is 2 total (NetworkManager.
			// MaxClients = 2, meaning 1 host + 1 remote slot), so others should
			// never have more than one entry — but if it ever does (e.g. a
			// future milestone raises the cap before this file is revisited),
			// silently picking [0] would misattribute a third peer's score to
			// "peer B" and drop the rest from the broadcast entirely. Surface
			// it loudly rather than let scores quietly go missing.
			if (others.Length > 1)
				GD.PrintErr($"[GameManager] Expected at most 1 remote peer (1v1 cap) but found {others.Length}; only the first is reported. Scoring for the rest will not broadcast correctly.");

			peerBId = others[0];
			peerBScore = _scoreboard.ScoreOf(peerBId);
		}

		return (peerAId, peerAScore, peerBId, peerBScore);
	}

	// ── Broadcast RPC ──────────────────────────────────────────────────────

	/// <summary>
	/// Called BY THE SERVER on all peers, broadcasting the post-basket score
	/// state. Mirrors BallController.RequestShoot's transfer-mode reasoning,
	/// NOT ReceiveState's:
	///
	/// Transfer mode: Reliable, deliberately NOT UnreliableOrdered like
	/// BallController.ReceiveState/PlayerController.ReceiveState. Those two
	/// broadcast EVERY physics tick (60 Hz) — a dropped packet there is
	/// harmless because the very next tick's broadcast supersedes it, and
	/// Reliable's head-of-line blocking at that rate would cause the
	/// rubber-banding those files exist to avoid. A basket is the opposite:
	/// a RARE, ONE-TIME discrete event with no follow-up packet that would
	/// ever resend the same information. If this drops, the client's score
	/// is permanently wrong (or the client never learns the game ended) —
	/// that is a correctness bug, not a smoothing concern, exactly the same
	/// reasoning BallController.RequestShoot already documents for its own
	/// Reliable choice. Reliable's HOL-blocking risk is a non-issue here
	/// because this fires at most a handful of times per match, never as a
	/// continuous per-tick stream.
	///
	/// RpcMode.Authority + CallLocal = false: only the server (this node's
	/// default multiplayer authority) can invoke this successfully — a
	/// non-server peer cannot forge a score update, the same trust boundary
	/// BallController.ReceiveState already relies on for its own payload.
	/// CallLocal = false because the server already holds the true values in
	/// its own Scoreboard and applies them to its own mirror by hand in
	/// BroadcastAndEmit — re-entering this handler locally would just redo
	/// that work from the (identical) wire values.
	///
	/// Fixed-arity int/int/int/int/int/bool parameters, not an int[] payload:
	/// the player cap is exactly 2, so there is no variable-length data to
	/// justify PackedInt32Array's complexity (confirmed via Godot docs that
	/// int[] IS Variant-marshallable for RPCs, but the existing codebase
	/// convention — see PlayerController.ReceiveState's float/float instead
	/// of Vector2 — already prefers flat primitive parameters over composite/
	/// array types wherever the arity is fixed and small).
	/// </summary>
	[Rpc(MultiplayerApi.RpcMode.Authority,
		 CallLocal = false,
		 TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void ReceiveScoreState(int peerAId, int peerAScore, int peerBId, int peerBScore,
		int winnerPeerId, bool isGameOver)
	{
		ApplyMirror(peerAId, peerAScore, peerBId, peerBScore, winnerPeerId, isGameOver);

		EmitSignal(SignalName.ScoreChanged);
		if (isGameOver)
			EmitSignal(SignalName.GameOver, winnerPeerId);
	}

	/// <summary>Shared write into the mirror fields, used by both the server's local apply and a client's RPC receipt.</summary>
	private void ApplyMirror(int peerAId, int peerAScore, int peerBId, int peerBScore, int winnerPeerId, bool isGameOver)
	{
		_peerAId = peerAId; _peerAScore = peerAScore;
		_peerBId = peerBId; _peerBScore = peerBScore;
		_mirrorWinnerPeerId = winnerPeerId;
		_mirrorIsGameOver = isGameOver;
	}
}
