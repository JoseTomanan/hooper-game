using Godot;
using System.Collections.Generic;
using Hooper.Moves;

namespace Hooper.Player;

/// <summary>
/// Drives a single player capsule — both the local movement feel AND the
/// server-authoritative network tick (Milestone 1b, issues #5 + #6).
///
/// ── Design ──────────────────────────────────────────────────────────────
/// Listen-server topology (ADR-0002): one machine is simultaneously server
/// + player 1; the other is a client + player 2.
///
/// Each physics tick this node runs in one of three roles:
///
///   HOST's own player (IsServer + IsLocalPlayer):
///     Read hardware → apply Movement immediately (authoritative, no lag).
///     Broadcast ReceiveState with ackSeq=0 (no client seq to ack).
///
///   SERVER's copy of a REMOTE PLAYER (IsServer + !IsLocalPlayer):
///     Consume the latest input received from the client via SubmitInput.
///     Apply Movement. Broadcast ReceiveState with the client's ackSeq.
///
///   CLIENT's own player (IsLocalPlayer + !IsServer) — prediction path:
///     seq++; push input to _pending; apply Movement locally NOW (zero lag).
///     Send SubmitInput(seq, input) to the server.
///     On receiving ReceiveState → reconcile (see ReconcileFromServer).
///
///   CLIENT's copy of REMOTE PLAYER (!IsLocalPlayer + !IsServer) — interpolation:
///     Lerp GlobalPosition toward the latest server broadcast each tick.
///     No MoveAndSlide — remote players are display-only on the client.
///
/// ── Identity contract ────────────────────────────────────────────────────
/// This node's Name is set to the controlling peer's ID (string) by
/// NetworkManager.SpawnPlayer before AddChild, so Name is set before
/// _Ready or _PhysicsProcess ever runs. IsLocalPlayer checks
/// Name == GetUniqueId(), which is valid once ConnectedToServer fires
/// (and spawning happens after that, so the check is always safe).
///
/// All nodes have default authority = server (peer 1). We never call
/// SetMultiplayerAuthority — the server owns all transform truth.
///
/// ── Shared motion code ───────────────────────────────────────────────────
/// Move() and ComputeVelocity() are called identically on the server
/// (authority), during client prediction, and during reconciliation replay.
/// They must stay pure: no role branches, no network calls, no side effects.
///
/// ── Smooth correction ────────────────────────────────────────────────────
/// When reconciliation detects divergence, _smoothOffset is SET to the
/// divergence vector (NOT accumulated — drift accumulation found in
/// doubt-cycle review). The physics body (CharacterBody3D) snaps to the
/// authoritative replayed position; the MeshInstance3D child is offset by
/// _smoothOffset and lerps back to local zero each frame, hiding the snap.
///
/// ── Committed-move integration (M3, local-only) ──────────────────────────────
/// From M3, local players drive a CommittedMoveMachine + RightStickGestureRecognizer
/// alongside the existing prediction loop. The machine and recognizer live directly
/// here so they share Velocity ownership without a second node fighting for it.
///
/// M3 limitation (Doubt cycle 2): committed moves are not networked. The server
/// always runs Move() — during a committed move the client sends Vector2.Zero as
/// movement input, so the server decelerates while the client runs startup/burst/
/// recovery behavior. The remaining divergence (especially during the Active burst)
/// is corrected by the existing reconcile smooth-correction mechanism. Full server
/// authority over committed moves is M4 work (#21).
///
/// Source: https://docs.godotengine.org/en/stable/tutorials/networking/high_level_multiplayer.html
/// </summary>
public partial class PlayerController : CharacterBody3D
{
	// ── Movement tuning (unchanged from M1a) ─────────────────────────────────

	/// <summary>Top ground speed in metres/second.</summary>
	[Export] public float MoveSpeed { get; set; } = 6.0f;

	/// <summary>Ground acceleration in m/s².</summary>
	[Export] public float Accel { get; set; } = 30.0f;

	/// <summary>
	/// Ground deceleration in m/s². Higher than Accel intentionally —
	/// that asymmetry is where "change of pace" lives (ADR-0003).
	/// </summary>
	[Export] public float Decel { get; set; } = 45.0f;

	// ── Reconciliation tuning ─────────────────────────────────────────────────

	/// <summary>
	/// Fraction of the visual snap distance corrected per physics frame.
	/// At 0.3 a 1-metre divergence closes in ~8 frames (~0.13 s at 60 Hz).
	/// </summary>
	[Export] public float ReconcileLerpRate { get; set; } = 0.3f;

	/// <summary>
	/// Divergence smaller than this (metres) is accepted silently — sub-pixel
	/// on a typical display, so no lerp is needed.
	/// </summary>
	[Export] public float ReconcileSnapThreshold { get; set; } = 0.001f;

	// ── Committed-move tuning (M3) ────────────────────────────────────────────

	/// <summary>
	/// Lateral speed of the crossover's Active-phase burst (m/s).
	/// Applied for the full ActiveFrames duration. Intentionally higher than
	/// MoveSpeed to create visible separation — that is the point of the move.
	/// </summary>
	[Export] public float BurstSpeed { get; set; } = 12.0f;

	// ── Committed-move state (M3, local-only) ─────────────────────────────────

	/// <summary>
	/// Sequences the startup / active / recovery phases for local committed moves.
	/// One instance per player node; starts Inactive (machine.IsActive = false).
	/// </summary>
	private readonly CommittedMoveMachine _machine = new();

	/// <summary>
	/// Recognizes right-stick gestures (horizontal flick) for the local player.
	/// Pure and engine-free — fed Vector2 samples from hardware each tick.
	/// </summary>
	private readonly RightStickGestureRecognizer _recognizer = new();

	// ── Visual-correction mesh reference ─────────────────────────────────────

	/// <summary>
	/// The MeshInstance3D child whose local Position is offset during smooth
	/// correction. The physics body (this node) stays at the authoritative
	/// replayed position; only the mesh drifts visually.
	///
	/// Null-guarded everywhere — if the editor wiring is wrong the physics
	/// still works, it just snaps visibly.
	/// </summary>
	private Node3D _mesh;

	// ── Network state ─────────────────────────────────────────────────────────

	/// <summary>
	/// Monotonic sequence counter. Incremented by the client each tick and
	/// stamped on outgoing inputs. The server echoes the last-applied seq back
	/// as ackSeq so the client can prune the pending buffer.
	///
	/// int wraps at 2^31 (~414 days at 60 Hz). Fine for M1b; revisit in M4
	/// if we add session-persistence across restarts.
	/// </summary>
	private int _seq;

	/// <summary>
	/// The seq of the last input the server applied for this player node.
	/// On the server: updated in SubmitInput, echoed via ReceiveState.
	/// On the client (own player): updated in ReceiveState, used to prune _pending.
	/// </summary>
	private int _serverAckedSeq;

	/// <summary>
	/// The latest input received from the client this tick (server-side,
	/// remote player only). A single value — we consume only the latest per
	/// tick; earlier values are superseded by newer SubmitInput calls.
	///
	/// Trade-off: using UnreliableOrdered for SubmitInput means packets
	/// arrive in order or are dropped; we never regress to a stale input.
	/// Out-of-order delivery is prevented at the transport level.
	/// (Doubt cycle 1, finding #4.)
	/// </summary>
	private Vector2 _pendingInput;

	/// <summary>
	/// Inputs the client has applied locally but not yet confirmed by the
	/// server. Each entry is (seq, inputDir). Drained by ReconcileFromServer.
	///
	/// Cap: 120 entries (~2 s at 60 Hz). If the server goes silent for 2 s
	/// the oldest inputs are evicted; a reconcile gap may follow, but this
	/// requires effective server death — acceptable for M1b.
	/// </summary>
	private readonly Queue<(int seq, Vector2 input)> _pending = new();
	private const int PendingCap = 120;

	/// <summary>Authoritative state received from the server, staged for reconcile.</summary>
	private Vector3 _serverPos;
	private Vector3 _serverVel;

	/// <summary>True once ReceiveState has arrived since the last _PhysicsProcess.</summary>
	private bool _hasNewState;

	/// <summary>
	/// Visual-only offset applied to the MeshInstance3D child.
	/// SET to the divergence when reconciliation finds a mismatch; lerped to
	/// zero each frame so the mesh drifts rather than snaps.
	///
	/// Always SET (=), never accumulated (+=) — a fresh reconcile supersedes
	/// the previous correction rather than stacking on top of it.
	/// (Doubt cycle 1, findings #2 + #9.)
	/// </summary>
	private Vector3 _smoothOffset;

	// ── Role helpers ──────────────────────────────────────────────────────────

	private bool IsServer      => Multiplayer.IsServer();
	private bool IsLocalPlayer => Name == Multiplayer.GetUniqueId().ToString();

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		// Cache the mesh child for smooth-correction offset. Null if the scene
		// is wired differently — physics still works, correction just snaps.
		_mesh = GetNodeOrNull<Node3D>("MeshInstance3D");
		if (_mesh == null)
			GD.PrintErr("[PlayerController] MeshInstance3D child not found; smooth correction disabled.");
	}

	// ── Tick loop ─────────────────────────────────────────────────────────────

	public override void _PhysicsProcess(double delta)
	{
		if      (IsServer && IsLocalPlayer)  TickServerOwnPlayer(delta);
		else if (IsServer && !IsLocalPlayer) TickServerRemotePlayer(delta);
		else if (!IsServer && IsLocalPlayer) TickClientOwnPlayer(delta);
		else                                 TickClientRemotePlayer();

		ApplySmoothCorrection();
	}

	// ── Tick roles ────────────────────────────────────────────────────────────

	/// <summary>
	/// HOST's own player. Authoritative: read hardware and move directly.
	/// No prediction buffer — this machine IS the authority.
	///
	/// Note: ReadInput() accesses the Input singleton (local keyboard/gamepad).
	/// This is correct for a listen-server where the host IS the local machine.
	/// When we add a headless dedicated server (M6) this path will not exist
	/// for that process — the dedicated server has no local player.
	/// </summary>
	private void TickServerOwnPlayer(double delta)
	{
		SampleMoveInput(); // reads gesture + feint, advances machine one tick

		if (_machine.IsActive)
			TickCommittedMoveBehavior(delta);
		else
		{
			Vector2 input = ReadInput();
			Move(input, delta);
		}

		// Broadcast authoritative state to all clients.
		// ackSeq = 0 because the host has no client-input queue to acknowledge.
		// Source: Rpc(MethodName.X) broadcasts to all peers.
		Rpc(MethodName.ReceiveState, 0, GlobalPosition, Velocity);
	}

	/// <summary>
	/// SERVER's copy of a REMOTE PLAYER. Apply the latest input received via
	/// SubmitInput, then broadcast authoritative state back to the client.
	/// </summary>
	private void TickServerRemotePlayer(double delta)
	{
		Move(_pendingInput, delta);
		// Echo _serverAckedSeq so the client prunes its pending buffer.
		Rpc(MethodName.ReceiveState, _serverAckedSeq, GlobalPosition, Velocity);
	}

	/// <summary>
	/// CLIENT's own player. Prediction path: move locally NOW (zero input lag),
	/// buffer the input, and send it to the server.
	/// Reconcile first against the freshest server state we have.
	/// </summary>
	private void TickClientOwnPlayer(double delta)
	{
		// Reconcile BEFORE predicting this tick's input so the correction
		// baseline is the freshest authoritative state we have.
		if (_hasNewState)
		{
			ReconcileFromServer(_serverPos, _serverVel, _serverAckedSeq, delta);
			_hasNewState = false;
		}

		// Prediction step.
		_seq++;
		SampleMoveInput(); // reads gesture + feint, advances machine one tick

		// During a committed move, send Vector2.Zero to the server so it
		// runs Move(zero) → deceleration, which approximates the locked/recovering
		// state and bounds server/client divergence in M3. The Active burst still
		// diverges; reconciliation smooths it via _smoothOffset.
		// (Doubt cycle 2, finding: Valid trade-off, documented above.)
		Vector2 moveInput = _machine.IsActive ? Vector2.Zero : ReadInput();

		if (_pending.Count >= PendingCap)
			_pending.Dequeue(); // oldest evicted; 2-s silence required to hit this

		_pending.Enqueue((_seq, moveInput));

		if (_machine.IsActive)
			TickCommittedMoveBehavior(delta);
		else
			Move(moveInput, delta);

		// Send input to server. UnreliableOrdered: dropped packets are gone,
		// but arriving packets are always in seq order (no regress to stale input).
		// Source: RpcId(peerId, MethodName.X) — peerId 1 = server.
		RpcId(1, MethodName.SubmitInput, _seq, moveInput.X, moveInput.Y);
	}

	/// <summary>
	/// CLIENT's copy of REMOTE PLAYER. No local physics — this is a display-only
	/// view. Lerp GlobalPosition toward the latest server broadcast.
	///
	/// We do NOT call MoveAndSlide here: doing so would apply Velocity * delta
	/// as additional movement on top of the position lerp, causing the remote
	/// capsule to overshoot its target on every tick.
	/// (Doubt cycle 1, finding #10.)
	/// </summary>
	private void TickClientRemotePlayer()
	{
		if (!_hasNewState) return;
		_hasNewState = false;

		GlobalPosition = GlobalPosition.Lerp(_serverPos, ReconcileLerpRate);
		// Keep Velocity in sync with server so any future collision queries
		// against this node return plausible values (not stale local data).
		Velocity = _serverVel;
	}

	// ── Server RPC: receive client input ─────────────────────────────────────

	/// <summary>
	/// Called BY THE CLIENT on the SERVER's copy of this node.
	/// Stores the latest input for use in the next TickServerRemotePlayer.
	///
	/// Transfer mode: UnreliableOrdered (was Unreliable in first draft).
	/// UnreliableOrdered ensures packets that arrive are always in seq order —
	/// no risk of reverting to an older input if UDP delivers out of order.
	/// Dropped packets are acceptable: the server repeats the last known input.
	/// (Doubt cycle 1, finding #4.)
	///
	/// Security: GetRemoteSenderId() is valid only inside an active RPC handler.
	/// We verify the sender's peer ID matches this node's Name to block one
	/// client from injecting inputs for another player's node.
	/// Source: https://docs.godotengine.org/en/stable/classes/class_multiplayerapi.html#class-multiplayerapi-method-get-remote-sender-id
	///
	/// inputX/Y sent as two floats rather than one Vector2 because Godot 4's
	/// Variant RPC does not support direct Vector2 without boxing overhead.
	/// </summary>
	[Rpc(MultiplayerApi.RpcMode.AnyPeer,
		 CallLocal = false,
		 TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
	private void SubmitInput(int seq, float inputX, float inputY)
	{
		// Validate sender (read synchronously at top — GetRemoteSenderId() is
		// only valid during the RPC call, never after).
		int senderId = Multiplayer.GetRemoteSenderId();
		if (senderId.ToString() != Name)
		{
			GD.PrintErr($"[PlayerController] Unauthorized SubmitInput from peer {senderId} for node '{Name}'");
			return;
		}

		// Reject out-of-order deliveries (UnreliableOrdered should prevent this
		// at transport level, but guard anyway for safety).
		if (seq <= _serverAckedSeq) return;

		_serverAckedSeq = seq;
		_pendingInput   = new Vector2(inputX, inputY);
	}

	// ── Client RPC: receive server state ─────────────────────────────────────

	/// <summary>
	/// Called BY THE SERVER on all peers, broadcasting the authoritative
	/// position + velocity of this player node.
	///
	/// Transfer mode: UnreliableOrdered — NOT Reliable (first draft was wrong).
	/// ReceiveState is a snapshot: only the LATEST value is useful. Using
	/// Reliable at 60 Hz causes head-of-line blocking: a single dropped packet
	/// stalls all subsequent state updates until retransmission, producing
	/// exactly the rubber-banding we're trying to eliminate.
	/// UnreliableOrdered drops lost packets (fine — next broadcast will be
	/// fresher) but never delivers an older snapshot after a newer one.
	/// (Doubt cycle 1, finding #7 — most impactful single fix.)
	///
	/// CallLocal = false: the host's own player is already authoritative,
	/// so the host must not reconcile against its own broadcasts.
	/// Source: https://docs.godotengine.org/en/stable/tutorials/networking/high_level_multiplayer.html
	/// </summary>
	[Rpc(MultiplayerApi.RpcMode.Authority,
		 CallLocal = false,
		 TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
	private void ReceiveState(int ackSeq, Vector3 pos, Vector3 vel)
	{
		_serverPos      = pos;
		_serverVel      = vel;
		_serverAckedSeq = ackSeq;
		_hasNewState    = true;
	}

	// ── Reconciliation ────────────────────────────────────────────────────────

	/// <summary>
	/// Client-side reconciliation on the own player node.
	///
	/// Algorithm:
	///   1. Discard pending inputs with seq ≤ ackSeq (server confirmed these).
	///   2. Snap physics body to authoritative pos/vel.
	///   3. Replay remaining pending inputs through Move() to repredict "now".
	///      Each replay call mirrors one past server tick (hence one MoveAndSlide
	///      per input — multiple calls in one frame is intentional and correct).
	///   4. If the replay ends at a different position than we were rendering,
	///      SET _smoothOffset to the divergence so the mesh drifts rather than
	///      snapping. SET, not +=: a fresh divergence supersedes the prior one.
	///
	/// Why replay? The server state is ~1 RTT old when it arrives. Snapping to
	/// it without replay shows the player jumping backward by 1 RTT of movement.
	/// Replay brings the prediction forward to "now".
	/// </summary>
	private void ReconcileFromServer(Vector3 authPos, Vector3 authVel, int ackSeq, double delta)
	{
		// Step 1: prune confirmed inputs.
		while (_pending.Count > 0 && _pending.Peek().seq <= ackSeq)
			_pending.Dequeue();

		// Step 2: remember current rendered position for divergence calculation.
		Vector3 renderedPos = GlobalPosition;

		// Snap physics to authoritative state.
		GlobalPosition = authPos;
		Velocity       = authVel;

		// Step 3: replay unacknowledged inputs using the fixed physics timestep.
		// The server simulated each of these at the same fixed rate, so using
		// Engine.PhysicsTicksPerSecond here is the correct matching timestep.
		double fixedDelta = 1.0 / Engine.PhysicsTicksPerSecond;
		foreach ((_, Vector2 input) in _pending)
			Move(input, fixedDelta);

		// Step 4: measure divergence and start a visual smooth correction if needed.
		Vector3 divergence = renderedPos - GlobalPosition;
		if (divergence.Length() > ReconcileSnapThreshold)
		{
			// SET — not +=. A new reconcile supersedes the previous correction;
			// stacking offsets causes overshoot and drift.
			// (Doubt cycle 1, finding #9.)
			_smoothOffset = divergence;
		}
	}

	/// <summary>
	/// Lerps the visual-only MeshInstance3D offset toward zero each frame,
	/// giving the appearance of smooth drift rather than a physics snap.
	///
	/// The physics body (CharacterBody3D) is already at the correct position.
	/// Only the mesh child's LOCAL position is offset, so collisions, raycasts,
	/// and other physics queries see the authoritative position.
	///
	/// (Doubt cycle 1, findings #2 + #9 — fixed from GlobalPosition manipulation
	/// which caused 2.33× overshoot, to mesh-child local offset instead.)
	/// </summary>
	private void ApplySmoothCorrection()
	{
		if (_mesh == null)
		{
			_smoothOffset = Vector3.Zero;
			return;
		}

		if (_smoothOffset.LengthSquared() < ReconcileSnapThreshold * ReconcileSnapThreshold)
		{
			_smoothOffset  = Vector3.Zero;
			_mesh.Position = Vector3.Zero;
			return;
		}

		_smoothOffset  = _smoothOffset.Lerp(Vector3.Zero, ReconcileLerpRate);
		_mesh.Position = _smoothOffset;
	}

	// ── Input (unchanged from M1a) ────────────────────────────────────────────

	/// <summary>
	/// Samples the left stick / WASD as a 2D intent vector.
	/// Called ONLY on the machine that locally controls this player —
	/// the host running TickServerOwnPlayer, or the client running
	/// TickClientOwnPlayer. Remote copies are driven by SubmitInput.
	/// </summary>
	private static Vector2 ReadInput()
	{
		return Input.GetVector("move_left", "move_right", "move_forward", "move_backward");
	}

	// ── Committed-move input and behavior (M3) ───────────────────────────────

	/// <summary>
	/// Samples gesture input, feeds the recognizer, and advances the committed-
	/// move machine one tick. Called at the top of every local-player tick so
	/// JustEnteredActive is correct when TickCommittedMoveBehavior reads it.
	///
	/// Right-stick gestures (gamepad): routed through RightStickGestureRecognizer.
	/// Keyboard fallback (Q): triggers a crossover (right burst) directly,
	/// bypassing the recognizer — simpler timing without a gamepad.
	/// Feint modifier (E key / L1): aborts a crossover during its startup window.
	///
	/// Note: Input.GetVector / IsActionJustPressed read the local machine's hardware.
	/// This is correct for listen-server (the host IS the local player). If a
	/// dedicated server (M6) runs this path, it would need refactoring — but that
	/// scenario is excluded by the IsLocalPlayer guard in _PhysicsProcess.
	/// </summary>
	private void SampleMoveInput()
	{
		Vector2 aim = Input.GetVector("aim_left", "aim_right", "aim_up", "aim_down");
		GestureResult gesture = _recognizer.Sample(aim);

		// Keyboard Q = immediate crossover (right burst). Takes precedence over
		// the recognizer so the keyboard and gamepad paths don't double-Begin().
		if (Input.IsActionJustPressed("move_crossover"))
			_machine.Begin(new Crossover(burstDirection: +1f));
		else if (gesture.Kind == GestureKind.Crossover)
			_machine.Begin(new Crossover(gesture.Direction));

		// Feint modifier: abort during the startup window.
		// The machine enforces the feint-window guard; false return is silent.
		if (Input.IsActionJustPressed("move_feint"))
			_machine.Feint();

		_machine.Tick(); // advance one frame — always called, including Inactive (no-op)
	}

	/// <summary>
	/// Applies the committed-move velocity effect for the current phase and calls
	/// MoveAndSlide(). Called instead of Move() while _machine.IsActive.
	///
	/// Startup  — Velocity zeroed: movement locked so the telegraph is readable.
	///            Do NOT smooth or blend — clunky startup is intentional (ADR-0003).
	/// Active   — Burst velocity SET on JustEnteredActive; maintained through all
	///            Active ticks so the lateral separation spans the full duration.
	///            SET not += : additive velocity would overshoot on reconcile replay.
	///            (Doubt cycle 2, finding #4 — actionable: sustain burst.)
	/// Recovery — Decelerate toward zero: the punish window (ADR-0003). Player
	///            cannot re-input; wrong reads are paid for here.
	///
	/// Note: MoveAndSlide() may clip Velocity on wall contact (finding #9, valid
	/// trade-off). Recovery decelerates from whatever clipped value results —
	/// acceptable for M3.
	/// </summary>
	private void TickCommittedMoveBehavior(double delta)
	{
		switch (_machine.Phase)
		{
			case MovePhase.Startup:
				Velocity = Vector3.Zero;
				MoveAndSlide();
				break;

			case MovePhase.Active:
				// Set the burst velocity on the first Active tick; on subsequent
				// Active ticks the same velocity is maintained (no else-zero here).
				// MoveAndSlide() applies it each tick, producing sustained separation.
				if (_machine.JustEnteredActive && _machine.CurrentMove is Crossover crossover)
					Velocity = new Vector3(crossover.BurstDirection * BurstSpeed, 0f, 0f);
				MoveAndSlide();
				break;

			case MovePhase.Recovery:
				Velocity = Velocity.MoveToward(Vector3.Zero, Decel * (float)delta);
				MoveAndSlide();
				break;
		}
	}

	// ── Shared motion step ────────────────────────────────────────────────────

	/// <summary>
	/// Applies one physics step of movement from a 2D intent vector.
	///
	/// THIS IS THE SHARED MOTION STEP. Runs identically for:
	///   - Server authoritative simulation (one call per tick)
	///   - Client prediction (one call per tick, current input)
	///   - Client reconciliation replay (one call per unacknowledged pending input)
	///
	/// Keep it pure: no role checks, no network calls, no side effects.
	/// Any divergence between server and client is a bug in this function.
	/// </summary>
	public void Move(Vector2 inputDir, double delta)
	{
		// Map 2D intent onto the ground plane. -Z is "forward" in Godot.
		// World-space, not camera-relative — so the server can replay it
		// without knowing each client's camera orientation (ADR-0002).
		Vector3 wishDir = new Vector3(inputDir.X, 0.0f, inputDir.Y);
		Velocity = ComputeVelocity(Velocity, wishDir, delta);
		MoveAndSlide();
	}

	/// <summary>
	/// Turns desired direction into the next velocity. Asymmetric rates
	/// (Decel > Accel) are where "change of pace" lives (ADR-0003).
	/// </summary>
	private Vector3 ComputeVelocity(Vector3 current, Vector3 wishDir, double delta)
	{
		Vector3 target = wishDir * MoveSpeed;
		float rate = wishDir == Vector3.Zero ? Decel : Accel;
		return current.MoveToward(target, rate * (float)delta);
	}
}
