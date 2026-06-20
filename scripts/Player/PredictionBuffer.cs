using Godot;
using System.Collections.Generic;

namespace Hooper.Player;

/// <summary>
/// Pure C# client-side prediction buffer — no Godot Node inheritance, no
/// engine singletons, no _PhysicsProcess, no RPCs.
///
/// Extracted from PlayerController's _seq/_pending fields (issue #55, the
/// PredictionBuffer sibling of #37's MovementMath extraction) so the seq/ack
/// contract behind client-side prediction (ADR-0002) is unit-testable. That
/// contract used to be smeared across three methods: TickClientOwnPlayer
/// (Record), SubmitInput/ReceiveState (the server-role ack — unrelated to
/// this buffer, left untouched), and ReconcileFromServer (Acknowledge +
/// Replay). This class owns exactly the client-side bookkeeping: what gets
/// recorded, what gets evicted under sustained packet loss, what gets pruned
/// once the server confirms it, and what survives to be replayed.
///
/// What this class deliberately does NOT own: the replay itself. Replaying a
/// buffered input means calling PlayerController.Move(), which calls
/// MoveAndSlide() — Godot's collision solver. That step is irreducibly
/// engine-bound and stays in PlayerController; this class only hands back
/// the ordered inputs to replay.
/// </summary>
public sealed class PredictionBuffer
{
	/// <summary>
	/// Maximum buffered (unacknowledged) inputs. At 60 Hz this is ~2 seconds;
	/// the same value PlayerController used inline as PendingCap.
	/// </summary>
	public int Capacity { get; }

	/// <summary>Number of inputs currently buffered (recorded but not yet acknowledged).</summary>
	public int Count => _pending.Count;

	/// <summary>
	/// The most recently assigned sequence number. 0 before the first Record()
	/// call — sequence numbers start at 1, matching the original `_seq++` (a
	/// pre-increment, so the first recorded input was always seq 1).
	///
	/// int wraps at 2^31 (~414 days at 60 Hz) — fine for a single match's
	/// lifetime; revisit only if a session ever needs to persist that long.
	/// </summary>
	public int LastSequence { get; private set; }

	private readonly Queue<(int seq, Vector2 input)> _pending = new();

	/// <param name="capacity">Maximum buffered inputs before the oldest is evicted. Defaults to 120 (~2 s at 60 Hz), matching PlayerController's original PendingCap.</param>
	public PredictionBuffer(int capacity = 120)
	{
		Capacity = capacity;
	}

	/// <summary>
	/// Records one tick's input: assigns the next sequence number, evicts the
	/// oldest buffered entry if already at capacity (sustained server silence
	/// — see PlayerController's original PendingCap doc), then enqueues.
	///
	/// Eviction happens BEFORE enqueue, matching the original
	/// `if (_pending.Count >= PendingCap) _pending.Dequeue();` ordering — at
	/// exactly Capacity entries, the oldest is dropped to make room for the
	/// new one rather than growing past Capacity first.
	/// </summary>
	/// <param name="input">This tick's movement intent vector.</param>
	/// <returns>The sequence number assigned to this input — send this to the server alongside the input.</returns>
	public int Record(Vector2 input)
	{
		LastSequence++;

		if (_pending.Count >= Capacity)
			_pending.Dequeue(); // oldest evicted; 2-s silence required to hit this

		_pending.Enqueue((LastSequence, input));
		return LastSequence;
	}

	/// <summary>
	/// Discards every buffered input the server has confirmed it applied.
	/// Call this BEFORE Replay() during reconciliation so Replay() only
	/// yields inputs the server has not yet seen.
	/// </summary>
	/// <param name="ackSeq">The server's last-applied sequence number for this player, from ReceiveState.</param>
	public void Acknowledge(int ackSeq)
	{
		while (_pending.Count > 0 && _pending.Peek().seq <= ackSeq)
			_pending.Dequeue();
	}

	/// <summary>
	/// The inputs still awaiting server confirmation, oldest first — the
	/// exact order PlayerController.ReconcileFromServer replays them through
	/// Move() to re-predict "now" from the freshly-snapped authoritative state.
	/// </summary>
	public IEnumerable<Vector2> Replay()
	{
		foreach ((_, Vector2 input) in _pending)
			yield return input;
	}
}
