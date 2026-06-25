using Godot;
using Hooper.Moves;
using Hooper.Systems;

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
///     _buffer.Record(input) (assigns seq); apply Movement locally NOW (zero lag).
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
/// Move() is called identically on the server (authority), during client
/// prediction, and during reconciliation replay. It must stay pure: no role
/// branches, no network calls, no side effects. The Accel/Decel asymmetry it
/// delegates to lives in MovementMath (issue #37) — pure and unit-tested,
/// since any divergence there is a netcode bug, not just a feel tweak.
///
/// ── Smooth correction ────────────────────────────────────────────────────
/// When reconciliation detects divergence, _smoothOffset is SET to the
/// divergence vector (NOT accumulated — drift accumulation found in
/// doubt-cycle review). The physics body (CharacterBody3D) snaps to the
/// authoritative replayed position; the MeshInstance3D child is offset by
/// _smoothOffset and lerps back to local zero each frame, hiding the snap.
///
/// ── Committed-move integration (M3 local-only → M4 server-authoritative) ─────
/// From M3, local players drive a CommittedMoveMachine + RightStickGestureRecognizer
/// alongside the existing prediction loop. The machine and recognizer live directly
/// here so they share Velocity ownership without a second node fighting for it.
///
/// M4 (#21): the server now runs an authoritative CommittedMoveMachine for EVERY
/// player node, not just its own. A remote player's machine cannot be driven by
/// SampleMoveInput() — that reads local hardware (Input.GetVector / IsActionJustPressed),
/// which on the server would read the SERVER's own gamepad, not the remote client's.
/// Instead the remote client reports discrete move-start/feint events over RPC
/// (RequestBeginMove / RequestFeint); the server applies them to its own _machine
/// copy for that node, which enforces the legal phase graph for free — in
/// particular, Begin() returning false while the server's copy is still mid-Recovery
/// IS the server-authoritative punish window: a client cannot self-report being out
/// of recovery, because the server's own frame count is what Begin()/Feint() check.
/// _machine.Tick() is called once per physics tick for every player node regardless
/// of role (it no-ops while Inactive), separately from SampleMoveInput() which is
/// only ever called for the locally-controlled player.
///
/// The client still predicts its own _machine locally for zero perceived lag
/// (Begin()/Feint() succeed immediately client-side), then asks the server via the
/// same RPCs. If the server's copy is still mid-Recovery when the RPC arrives (e.g.
/// high latency), the server's Begin() fails and the server stays Inactive/Recovery
/// while the client briefly believes it started a new move — ReconcileFromServer's
/// Step 0 repairs exactly this case (server confirms Inactive once the client has
/// progressed past Startup) via CommittedMoveMachine.ForceState(). It deliberately
/// does NOT force-match FrameInPhase every tick the way BallController forces
/// BallState (#20) — ReceiveState is ~1-RTT stale, so a strict-equality force would
/// rewind the predicted phase continuously under any nonzero latency. See Step 0's
/// comment in ReconcileFromServer for the full reasoning (#21 doubt cycle 1, finding #2).
///
/// The Vector2.Zero-during-a-move behavior in TickClientOwnPlayer (below) is kept
/// as-is for the regular Move() path. Phase/punish-window correctness no longer
/// depends on it (the server independently drives _machine/TickCommittedMoveBehavior
/// for remote players now), but it is still the input the reconciliation replay
/// (Step 3) reproduces for any buffered tick during Active — that replay calls
/// Move() only, never TickCommittedMoveBehavior, so the burst itself still isn't
/// reconstructed on replay. That residual position-only divergence is the same
/// accepted, _smoothOffset-covered trade-off this file already had before M4.
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

	/// <summary>
	/// The visual-root node that is offset during smooth correction and rotated
	/// for cosmetic facing + lean. Set this in the editor to the node holding
	/// the player mesh (humanoid root after M7a's mesh swap, or left unset to
	/// fall back to the MeshInstance3D child lookup below).
	/// </summary>
	[Export] public NodePath VisualRoot { get; set; }

	/// <summary>
	/// The AnimationTree that drives the rigged humanoid (M7b, issues #68/#41/#69).
	/// Set this in the editor to the AnimationTree node added under the humanoid
	/// model. Left unset, all animation is silently skipped — movement, collision,
	/// and netcode are completely unaffected (this is cosmetic-only, ADR-0002/0004),
	/// so a scene without the AnimationTree wired still plays correctly, just
	/// without locomotion/committed-move animation.
	///
	/// The tree's root must be an AnimationNodeStateMachine whose state names match
	/// MoveAnimState exactly (Locomotion / Startup / Active / Recovery), with the
	/// Locomotion state a BlendSpace1D blending idle→run by horizontal speed. See
	/// EDITOR_TASKS.md "Milestone 7b" for the exact node/parameter contract this
	/// code binds to.
	/// </summary>
	[Export] public NodePath AnimationTreePath { get; set; }

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

	/// <summary>
	/// The visual node's authored local position, captured once in _Ready.
	/// Player.tscn seats the humanoid mesh below the CharacterBody3D origin
	/// (M7a mesh swap) via CharacterModel's local Position — smooth correction
	/// below must apply ON TOP of that seat offset, not overwrite it. Before
	/// this field existed, ApplySmoothCorrection() reset _mesh.Position to
	/// Vector3.Zero whenever no correction was active, clobbering the seat
	/// offset and leaving the model floating above the floor every frame.
	/// </summary>
	private Vector3 _meshRestPosition;

	/// <summary>
	/// Last computed yaw for the visual mesh (radians). Persists between frames
	/// so the mesh holds its facing when the player is stationary.
	/// </summary>
	private float _visualYaw;

	// ── Rigged-animation runtime (M7b, issues #68/#41/#69) ───────────────────

	/// <summary>
	/// The AnimationTree resolved from <see cref="AnimationTreePath"/> in _Ready,
	/// or null if unset/unresolved. Null-guarded everywhere — animation is purely
	/// cosmetic, so a null tree disables animation without affecting gameplay.
	/// </summary>
	private AnimationTree _animTree;

	/// <summary>
	/// The state-machine playback handle pulled from the AnimationTree's
	/// "parameters/playback" once in _Ready. Travel() switches committed-move
	/// states; cached so we don't re-fetch the Variant every tick.
	/// </summary>
	private AnimationNodeStateMachinePlayback _animPlayback;

	/// <summary>
	/// The committed-move animation state currently traveled to. Tracked so
	/// ApplyAnimation only calls Travel() on an actual state change, not every
	/// tick — repeated Travel() to the current state would restart the clip.
	/// </summary>
	private MoveAnimState _currentAnimState = MoveAnimState.Locomotion;

	// ── Network state ─────────────────────────────────────────────────────────

	/// <summary>
	/// The seq of the last input the server applied for this player node.
	/// On the server: updated in SubmitInput, echoed via ReceiveState.
	/// On the client (own player): updated in ReceiveState, fed to
	/// _buffer.Acknowledge() to prune the prediction buffer.
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
	/// Records the client's own predicted inputs and drains them once the
	/// server confirms them. Extracted from inline _seq/_pending fields
	/// (issue #55, sibling of #37's MovementMath extraction) so the seq/ack
	/// bookkeeping behind client-side prediction (ADR-0002) is unit-testable.
	/// Capacity 120 (~2 s at 60 Hz, the original PendingCap default): if the
	/// server goes silent for 2 s the oldest inputs are evicted; a reconcile
	/// gap may follow, but this requires effective server death — acceptable.
	/// </summary>
	private readonly PredictionBuffer _buffer = new();

	/// <summary>Authoritative state received from the server, staged for reconcile.</summary>
	private Vector3 _serverPos;
	private Vector3 _serverVel;

	/// <summary>
	/// Authoritative committed-move phase received from the server, staged for
	/// reconcile (M4, #21) AND for display of a remote player's commitment (M7b,
	/// #69). ReconcileFromServer's Step 0 consults it only for the own-player
	/// force-Inactive correction (FrameInPhase deliberately not compared — see
	/// that comment); ApplyCosmetics/ApplyAnimation read it as the DISPLAY phase
	/// for the client's copy of the opponent (DisplayPhaseResolver decides which
	/// roles use it). The two consumers never conflict: reconciliation runs only
	/// on the own player, display-from-broadcast only on the remote copy.
	/// </summary>
	private MovePhase _serverMovePhase;

	/// <summary>
	/// Authoritative moveId / move payload from the latest broadcast (M7b, #69).
	/// Before #69 these wire fields were received but discarded (only the phase
	/// fed reconciliation). Now they drive the DISPLAY of a remote player's burst
	/// lean direction: the client's copy of the opponent has no live local
	/// CurrentMove to read BurstDirection from, so it reconstructs the lean's
	/// sign from the broadcast payload instead. Display-only — never feeds
	/// reconciliation, prediction, or any authoritative state.
	/// </summary>
	private string _serverMoveId = "";
	private float _serverMoveParam;

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

	// ── Game-over freeze (#25) ──────────────────────────────────────────────────

	/// <summary>
	/// Cached GameManager reference, discovered via the "game_manager" group
	/// (see GameManager's class doc "Discovery" — same pattern BallController
	/// uses). Null-guarded loudly in _Ready; GetGameManager() below adds a
	/// lazy re-lookup fallback for the (expected-rare) case a player node's
	/// _Ready races ahead of GameManager's in the scene tree.
	/// </summary>
	private GameManager _gameManager;

	/// <summary>Resolves _gameManager, re-querying the group if still null. See field doc.</summary>
	private GameManager GetGameManager()
	{
		if (_gameManager == null)
			_gameManager = GetTree().GetFirstNodeInGroup("game_manager") as GameManager;
		return _gameManager;
	}

	// ── Role helpers ──────────────────────────────────────────────────────────

	private bool IsServer      => Multiplayer.IsServer();
	private bool IsLocalPlayer => Name == Multiplayer.GetUniqueId().ToString();

	// ── Lifecycle ─────────────────────────────────────────────────────────────

	public override void _Ready()
	{
		// Resolve the visual-root node for smooth-correction offset and cosmetic
		// facing/lean. VisualRoot (set in the Inspector) is preferred — it
		// survives the humanoid mesh swap (M7a) without a code change. Falls back
		// to the hardcoded MeshInstance3D child lookup for scenes not yet updated.
		if (VisualRoot != null && !VisualRoot.IsEmpty)
		{
			_mesh = GetNodeOrNull<Node3D>(VisualRoot);
			if (_mesh == null)
				GD.PrintErr($"[PlayerController] VisualRoot '{VisualRoot}' is set but could not be resolved — check for a renamed or deleted node. Smooth correction disabled.");
		}
		else
		{
			_mesh = GetNodeOrNull<Node3D>("MeshInstance3D");
			if (_mesh == null)
				GD.PrintErr("[PlayerController] Visual root not found; smooth correction disabled. Set VisualRoot in the Inspector or ensure a MeshInstance3D child exists.");
		}

		// Capture the authored seat offset (see _meshRestPosition's doc) so
		// ApplySmoothCorrection() can apply correction relative to it instead
		// of clobbering it with Vector3.Zero.
		if (_mesh != null)
			_meshRestPosition = _mesh.Position;

		// Resolve the rigged-animation AnimationTree (M7b). Optional and fully
		// null-guarded: a scene without it wired plays exactly as before, just
		// without animation (cosmetic-only, ADR-0002/0004). Active is forced on
		// so the tree drives the skeleton; the playback handle is cached once
		// rather than re-fetched from the Variant dictionary every tick.
		if (AnimationTreePath != null && !AnimationTreePath.IsEmpty)
		{
			_animTree = GetNodeOrNull<AnimationTree>(AnimationTreePath);
			if (_animTree != null)
			{
				_animTree.Active = true;
				_animPlayback = _animTree.Get("parameters/playback")
					.As<AnimationNodeStateMachinePlayback>();
				if (_animPlayback == null)
					GD.PrintErr("[PlayerController] AnimationTree resolved but 'parameters/playback' is null — its root must be an AnimationNodeStateMachine. Committed-move animation disabled.");
			}
			else
			{
				GD.PrintErr($"[PlayerController] AnimationTreePath '{AnimationTreePath}' is set but could not be resolved — check for a renamed or deleted node. Animation disabled.");
			}
		}

		_gameManager = GetTree().GetFirstNodeInGroup("game_manager") as GameManager;
		if (_gameManager == null)
			GD.PrintErr("[PlayerController] No node in group 'game_manager' found. Game-over freeze will not work until GameManager is added to the scene (issue #27).");
	}

	// ── Tick loop ─────────────────────────────────────────────────────────────

	public override void _PhysicsProcess(double delta)
	{
		// (#25) Game-over freeze: skip movement entirely once the match has
		// ended, on EVERY role (server's own player, server's remote-player
		// copy, client's own player, client's remote-player copy) — a frozen
		// match must look frozen for both players regardless of which
		// machine is asking.
		//
		// Read off GameManager.IsGameOver, which itself reads the server's
		// live Scoreboard on the server and the broadcast mirror everywhere
		// else (see GameManager). This is deliberately NOT predicted: a
		// player could not legally "predict" game-over locally without first
		// predicting the score, and score is never predicted (see
		// GameManager's class doc and BallController's matching freeze
		// guard). The alternative — freezing based on a locally-predicted
		// score threshold — would let a client freeze itself (or, worse,
		// the OTHER player's remote copy) before the server agrees the game
		// actually ended, which is a far worse user-facing bug than the
		// ~1-RTT delay below.
		//
		// (#25 doubt cycle 1, finding #1) Doubt-checked the ~1-RTT delay
		// before a client observes game-over: during that window the losing/
		// winning client keeps moving for up to one round trip after the
		// server has already decided the match. Is that acceptable, or
		// should movement be predicted-frozen too? Predicting the freeze
		// would require the client to independently know the score crossed
		// TargetScore — which requires predicting the score — which is
		// exactly the asymmetry GameManager's class doc rules out. A
		// mispredicted freeze (player freezes, then a moment later un-freezes
		// because the server says the game ISN'T over — e.g. the client
		// misjudged its own score) is more jarring and harder to reason
		// about than a brief continued-movement window before the broadcast
		// arrives. ~1 RTT (tens of ms on a LAN, the target topology per
		// ADR-0005) is not perceptible as "the game kept going" the way a
		// freeze/unfreeze flicker would be. Confirmed: broadcast-driven
		// freeze is the only correct choice here, not just the simplest one.
		if (GetGameManager()?.IsGameOver == true) return;

		if      (IsServer && IsLocalPlayer)  TickServerOwnPlayer(delta);
		else if (IsServer && !IsLocalPlayer) TickServerRemotePlayer(delta);
		else if (!IsServer && IsLocalPlayer) TickClientOwnPlayer(delta);
		else                                 TickClientRemotePlayer();

		ApplySmoothCorrection();
		ApplyCosmetics();
		ApplyAnimation();
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
		SampleMoveInput(isServer: true); // reads gesture + feint, advances machine one tick

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
		Rpc(MethodName.ReceiveState, 0, GlobalPosition, Velocity,
			(int)_machine.Phase, _machine.FrameInPhase, MoveIdOf(_machine.CurrentMove), MoveParamOf(_machine.CurrentMove));
	}

	/// <summary>
	/// SERVER's copy of a REMOTE PLAYER. Apply the latest input received via
	/// SubmitInput, then broadcast authoritative state back to the client.
	///
	/// M4 (#21): mirrors TickServerOwnPlayer's structure — the server's own
	/// _machine copy for this remote player is now authoritative, so Move()
	/// is skipped while a committed move is active, exactly like the own-player
	/// path. _machine.Tick() advances every tick regardless of role (it no-ops
	/// while Inactive); the events that drive Begin()/Feint() arrive via
	/// RequestBeginMove/RequestFeint, NOT SampleMoveInput() — there is no local
	/// hardware to sample for someone else's player on this machine.
	/// </summary>
	private void TickServerRemotePlayer(double delta)
	{
		_machine.Tick();

		if (_machine.IsActive)
			TickCommittedMoveBehavior(delta);
		else
			Move(_pendingInput, delta);

		// Echo _serverAckedSeq so the client prunes its pending buffer, plus
		// the committed-move state piggybacked on the same broadcast (see
		// ReceiveState below for the payload rationale).
		Rpc(MethodName.ReceiveState, _serverAckedSeq, GlobalPosition, Velocity,
			(int)_machine.Phase, _machine.FrameInPhase, MoveIdOf(_machine.CurrentMove), MoveParamOf(_machine.CurrentMove));
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
		SampleMoveInput(isServer: false); // reads gesture + feint, advances machine one tick, RPCs server

		// During a committed move, send Vector2.Zero to the server as this
		// tick's regular movement input (independent of the RequestBeginMove/
		// RequestFeint RPCs that now carry the move itself, #21). This still
		// matters for one specific path post-M4: Step 3 of ReconcileFromServer
		// replays buffered pending inputs through Move() only — it does not
		// replay TickCommittedMoveBehavior — so a replayed tick during Active
		// reproduces a zero-input decel, not the burst. The PHASE/punish-window
		// divergence this once stood in for is now fully resolved server-side
		// (#21); this residual is purely positional and was already an
		// accepted, _smoothOffset-covered trade-off before M4 (originally
		// "Doubt cycle 2" on this file) — M4 narrows what it's compensating
		// for but doesn't need to eliminate it.
		Vector2 moveInput = _machine.IsActive ? Vector2.Zero : ReadInput();

		// Record() assigns the seq and handles capacity eviction (see
		// PredictionBuffer doc) — behavior-identical to the inline
		// _seq++ / cap-check / enqueue this replaced.
		int seq = _buffer.Record(moveInput);

		if (_machine.IsActive)
			TickCommittedMoveBehavior(delta);
		else
			Move(moveInput, delta);

		// Send input to server. UnreliableOrdered: dropped packets are gone,
		// but arriving packets are always in seq order (no regress to stale input).
		// Source: RpcId(peerId, MethodName.X) — peerId 1 = server.
		RpcId(1, MethodName.SubmitInput, seq, moveInput.X, moveInput.Y);
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

	/// <summary>
	/// Called BY THE CLIENT on the SERVER's copy of this node, requesting the
	/// authoritative start of a committed move.
	///
	/// Transfer mode: Reliable — a deliberate deviation from SubmitInput's
	/// UnreliableOrdered, mirroring BallController.RequestShoot (#20). This is a
	/// ONE-TIME discrete event with no redundancy: a dropped packet means the
	/// player pressed crossover and nothing happened, a correctness bug, not a
	/// smoothing concern. Reliable's head-of-line-blocking risk (the reason
	/// ReceiveState/SubmitInput avoid it) doesn't apply because this fires
	/// rarely — once per committed move attempt, not every physics tick — so
	/// there is no continuous stream for a retransmit to stall.
	///
	/// moveId/param is the minimal payload to reconstruct a move. Only one
	/// concrete move exists today ("crossover", carrying BurstDirection as
	/// param) so a small if-chain is correct and proportionate — see
	/// CommittedMove.Id's doc comment, which already anticipated this exact use.
	/// Do not generalize into a move registry/factory until a second move exists.
	///
	/// Security: same sender check as SubmitInput — PlayerController has a
	/// per-peer node identity (Name == peer ID), unlike the Ball, which had to
	/// validate against HolderPeerId instead.
	///
	/// _machine.Begin() enforces the legal phase graph using the SERVER's own
	/// frame count: if the server's copy is still mid-Recovery from a prior
	/// move, Begin() returns false here and the request is silently dropped.
	/// THIS is what makes the punish window server-authoritative — a client
	/// cannot self-report being out of recovery, because the server never
	/// takes the client's word for its own phase.
	///
	/// (#21 doubt cycle 1, finding #1) Exactly which physics tick this RPC is
	/// processed on relative to this node's own _machine.Tick() that same
	/// frame is not something Godot's MultiplayerApi guarantees — the accept/
	/// reject boundary can land on either side of a single tick depending on
	/// packet arrival timing. This is a ±1-tick (≈16ms) nondeterminism inherent
	/// to ANY RPC-plus-fixed-tick-loop design (it equally affects SubmitInput
	/// and BallController.RequestShoot); resolving it would require lockstep
	/// simulation, well beyond M4's scope. Begin()'s own legality check is
	/// still the sole authority regardless of which side of that boundary the
	/// packet lands on.
	///
	/// (#21 doubt cycle 1, finding #5) No sequence/idempotency guard, unlike
	/// SubmitInput's seq check — none is needed: Begin() is already idempotent
	/// under duplicate or out-of-order delivery (a second call while already
	/// active just no-ops and returns false), so there is no stale-input-regression
	/// risk the way continuous movement input has.
	/// </summary>
	[Rpc(MultiplayerApi.RpcMode.AnyPeer,
		 CallLocal = false,
		 TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void RequestBeginMove(string moveId, float param)
	{
		int senderId = Multiplayer.GetRemoteSenderId();
		if (senderId.ToString() != Name)
		{
			GD.PrintErr($"[PlayerController] Unauthorized RequestBeginMove from peer {senderId} for node '{Name}'");
			return;
		}

		if (moveId == "crossover")
			_machine.Begin(new Crossover(burstDirection: param));
		// Unrecognized moveId: silently ignored. No other move type exists yet;
		// a malformed/forged moveId from a tampered client simply does nothing.
	}

	/// <summary>
	/// Called BY THE CLIENT on the SERVER's copy of this node, requesting the
	/// authoritative feint abort. Same transfer-mode rationale as
	/// RequestBeginMove above — a one-shot discrete event, Reliable is correct.
	///
	/// _machine.Feint() enforces the feint window using the SERVER's own frame
	/// count (FrameInPhase), not anything the client reports — so a client
	/// attempting to feint outside the legal window simply gets a no-op here,
	/// exactly like a local Feint() call would.
	/// </summary>
	[Rpc(MultiplayerApi.RpcMode.AnyPeer,
		 CallLocal = false,
		 TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void RequestFeint()
	{
		int senderId = Multiplayer.GetRemoteSenderId();
		if (senderId.ToString() != Name)
		{
			GD.PrintErr($"[PlayerController] Unauthorized RequestFeint from peer {senderId} for node '{Name}'");
			return;
		}

		_machine.Feint();
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
	///
	/// M4 (#21): committed-move state piggybacks on this EXISTING broadcast
	/// rather than a parallel channel — it already fires every server tick with
	/// the same staleness/redundancy properties committed-move state needs.
	/// movePhase is sent as int, not the MovePhase enum directly — same
	/// Variant-safety reasoning BallController.ReceiveState already uses for
	/// BallState (#20): Godot's [Rpc] Variant marshaling is not guaranteed to
	/// box a raw C# enum cleanly. moveId is empty string for Inactive/no move;
	/// moveParam is the move's reconstruction payload (today: Crossover's
	/// BurstDirection), 0 when not applicable.
	///
	/// (#21 doubt cycle 1, finding #2) frameInPhase remains received-but-unstored —
	/// only movePhase feeds reconciliation (see ReconcileFromServer's Step 0, which
	/// explains why FrameInPhase must not be compared against this stale snapshot).
	///
	/// (M7b, #69) moveId/moveParam, formerly discarded for the same "nothing reads
	/// them" reason, are now stored: they drive the DISPLAY of a remote player's
	/// burst-lean direction (ApplyCosmetics), since the client's copy of the
	/// opponent has no live local CurrentMove to read BurstDirection from. This is
	/// a display-only read; reconciliation is unchanged.
	/// </summary>
	[Rpc(MultiplayerApi.RpcMode.Authority,
		 CallLocal = false,
		 TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
	private void ReceiveState(int ackSeq, Vector3 pos, Vector3 vel,
		int movePhase, int frameInPhase, string moveId, float moveParam)
	{
		_serverPos       = pos;
		_serverVel       = vel;
		_serverAckedSeq  = ackSeq;
		_serverMovePhase = (MovePhase)movePhase;
		_serverMoveId    = moveId;
		_serverMoveParam = moveParam;
		_hasNewState     = true;
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
	///
	/// Step 0 (M4, #21): committed-move correction is narrower than a naive
	/// "force to match the broadcast" — see the long comment on Step 0 below
	/// for why FrameInPhase is deliberately NOT part of the trigger condition
	/// (#21 doubt cycle 1, finding #2: comparing it for equality against a
	/// structurally ~1-RTT-stale broadcast force-rewinds every active move
	/// under any nonzero latency, since the client's local FrameInPhase is
	/// *always* further along than the last snapshot once a move is running).
	/// </summary>
	private void ReconcileFromServer(Vector3 authPos, Vector3 authVel, int ackSeq, double delta)
	{
		// Step 0: only correct the ONE divergence that actually matters for
		// the contract — the server confirms the move the client predicted
		// never took hold (rejected because the server's own copy was still
		// mid-Recovery, i.e. a real punish-window violation the client
		// mispredicted past). We deliberately do NOT compare FrameInPhase, and
		// we do NOT correct while the client is still in Startup:
		//
		//   - FrameInPhase: ReceiveState is ~1 RTT stale, same as position.
		//     Once Phase/move identity agree, Tick() is a pure function of
		//     elapsed ticks with no further network dependency — exactly like
		//     the Ball's InFlight arc, which is never force-rewound to a stale
		//     frame count, only its position is smoothed. Forcing FrameInPhase
		//     to the stale broadcast value every tick would yank the predicted
		//     phase backward continuously for the entire duration of any move,
		//     visibly mangling the Active burst's timing — the opposite of
		//     "client predicts committed-move phase locally."
		//
		//   - Startup grace: a transient one-RTT delay before the server's
		//     confirming broadcast arrives is expected on EVERY legitimate
		//     move attempt (RequestBeginMove takes one-way latency to reach
		//     the server, then another to come back). Nothing externally
		//     visible has happened yet during Startup — Velocity is zero
		//     either way — so reverting before the confirmation has had a
		//     fair chance to arrive would falsely flicker every single
		//     committed move, not just mispredicted ones. By the time the
		//     client's local Active phase begins, StartupFrames worth of
		//     one-way trip time has already elapsed, which covers all but
		//     pathological RTTs — consistent with the latency tolerance the
		//     rest of this prediction system already assumes.
		//
		// Only one move type exists today, so a "different move Id while both
		// sides are active" case is unreachable; revisit this gate when a
		// second move type makes that distinguishable from this check.
		//
		// (#21 doubt cycle 2, finding #1) Known bounded gap: if the client begins
		// a SECOND move right as its own local Recovery from a FIRST move
		// ends, while the server's copy of the first move is still finishing
		// Recovery (not yet Inactive — the server's Recovery end is itself up
		// to ~1 RTT behind the client's belief that it ended), the server
		// rejects the second Begin() and stays in the (truthful) Recovery
		// phase, not Inactive — so this gate, which only fires on
		// serverSaysInactive, does not fire immediately. The client runs a
		// fully local "phantom" second move (including a one-tick burst
		// glimpse) until its own local copy ticks past Startup while the
		// server is STILL reporting (the real) Inactive for that phantom
		// move — at which point this gate correctly reverts it, typically
		// within one tick of the burst applying. This is a bounded, self-
		// correcting, LOCAL-ONLY visual artifact: the server's truth (what
		// every other player and the actual game outcome depend on) was never
		// wrong, and the phantom move's local duration roughly matches the
		// real wait the player would have needed anyway, so there is no
		// recovery-frame skip to exploit. Handling this perfectly would
		// require tracking "is there an unconfirmed Begin in flight" the way
		// the seq/ack system does for movement — more machinery than this
		// milestone calls for; documented here as an accepted trade-off.
		if (CommittedMoveMachine.ShouldForceInactive(_machine.Phase, _machine.IsActive, _serverMovePhase))
			_machine.ForceState(MovePhase.Inactive, frameInPhase: 0, move: null);

		// Step 1: prune confirmed inputs.
		_buffer.Acknowledge(ackSeq);

		// Step 2: remember current rendered position for divergence calculation.
		Vector3 renderedPos = GlobalPosition;

		// Snap physics to authoritative state.
		GlobalPosition = authPos;
		Velocity       = authVel;

		// Step 3: replay unacknowledged inputs using the fixed physics timestep.
		// The server simulated each of these at the same fixed rate, so using
		// Engine.PhysicsTicksPerSecond here is the correct matching timestep.
		// Move() (the engine-bound replay step, MoveAndSlide included) stays
		// here — only the buffer bookkeeping moved to PredictionBuffer (#55).
		double fixedDelta = 1.0 / Engine.PhysicsTicksPerSecond;
		foreach (Vector2 input in _buffer.Replay())
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
	///
	/// Offsets are applied RELATIVE to _meshRestPosition, not Vector3.Zero —
	/// Player.tscn authors a seat offset on the visual node (M7a humanoid mesh
	/// swap) and this channel must not clobber it (the "player floats above
	/// the floor" bug: this used to reset _mesh.Position straight to zero).
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
			_mesh.Position = _meshRestPosition;
			return;
		}

		_smoothOffset  = _smoothOffset.Lerp(Vector3.Zero, ReconcileLerpRate);
		_mesh.Position = _meshRestPosition + _smoothOffset;
	}

	/// <summary>
	/// Applies cosmetic facing + burst lean to the visual mesh each frame.
	/// Cosmetic-only: touches only _mesh.Rotation, never Velocity or any
	/// authoritative/replicated state. Position (smooth-correction offset) and
	/// Rotation (facing/lean) are independent transform channels on the same
	/// node — setting one does not affect the other in Godot.
	///
	/// Exact tilt axis and sign are hitl visual sign-off (human verifies in-editor
	/// that the mesh faces the run direction and leans into the crossover burst).
	///
	/// M7b (#69): the lean now reads the DISPLAY phase/burst from DisplayMove(),
	/// not the raw local _machine. This revives the burst lean for the CLIENT's
	/// copy of the REMOTE player — previously always 0 because that role never
	/// advances its local _machine (the accepted #39 "non-networked" limitation).
	/// The opponent's commitment now leans on your screen, driven by the same
	/// broadcast phase already on the wire. Facing is unchanged — it derives from
	/// Velocity, which ReceiveState already sets for the remote copy.
	/// </summary>
	private void ApplyCosmetics()
	{
		if (_mesh == null) return;

		_visualYaw = FacingResolver.ResolveYaw(Velocity, _visualYaw);
		(MovePhase displayPhase, float burstDir) = DisplayMove();
		float tilt = LeanResolver.ResolveTilt(displayPhase, burstDir);

		// Y = yaw (face run direction), Z = lean (lateral tilt into burst).
		// Exact sign/axis is hitl sign-off.
		_mesh.Rotation = new Vector3(0f, _visualYaw, tilt);
	}

	/// <summary>
	/// Drives the rigged AnimationTree each frame (M7b): the idle↔run locomotion
	/// blend from horizontal speed (#68), and the committed-move state machine
	/// (Locomotion/Startup/Active/Recovery) from the DISPLAY phase (#41 + #69).
	///
	/// Cosmetic-only and fully null-guarded — if the AnimationTree isn't wired,
	/// this no-ops and gameplay is unaffected (ADR-0002/0004). Like ApplyCosmetics
	/// it runs for EVERY role, so the opponent's committed-move animation plays on
	/// your screen via the broadcast phase (DisplayMove), closing the ADR-0003 gap
	/// that the lean alone left open.
	///
	/// The blend position is set from |horizontal Velocity| in m/s; the editor
	/// authors the BlendSpace1D so 0 = idle and MoveSpeed = full run (see
	/// EDITOR_TASKS.md M7b). Travel() fires only on a state CHANGE — re-traveling
	/// to the current state would restart the placeholder clip every tick.
	/// </summary>
	private void ApplyAnimation()
	{
		if (_animTree == null) return;

		// Locomotion blend (#68): feed horizontal speed; the Y component is
		// vertical and irrelevant to a ground idle/run blend.
		float horizontalSpeed = new Vector2(Velocity.X, Velocity.Z).Length();
		_animTree.Set("parameters/Locomotion/blend_position", horizontalSpeed);

		// Committed-move state (#41/#69): map the DISPLAY phase to an anim state
		// and Travel() only when it changes. Enum names match the AnimationTree's
		// state names by contract (EDITOR_TASKS.md M7b), so ToString() is the
		// state id — Locomotion/Startup/Active/Recovery.
		if (_animPlayback == null) return;
		(MovePhase displayPhase, _) = DisplayMove();
		MoveAnimState target = MoveAnimResolver.Resolve(displayPhase);
		if (target != _currentAnimState)
		{
			_animPlayback.Travel(target.ToString());
			_currentAnimState = target;
		}
	}

	/// <summary>
	/// The committed-move phase and burst direction this node should DISPLAY this
	/// frame, resolved by role (M7b, #69). For every role this peer simulates
	/// (host own, server's remote copy, client own) it reads the live local
	/// _machine; for the client's copy of the opponent — the one role that never
	/// advances its local _machine — it reads the broadcast phase/payload instead.
	/// DisplayPhaseResolver owns that decision (pure + unit-tested); this method is
	/// the thin node-side glue that reads the right fields per branch.
	///
	/// Burst direction: the own/simulated path reads it live off CurrentMove; the
	/// broadcast path reconstructs it from _serverMoveId/_serverMoveParam (today,
	/// "crossover" carrying BurstDirection — the same minimal payload RequestBeginMove
	/// and MoveParamOf already speak).
	/// </summary>
	private (MovePhase phase, float burstDir) DisplayMove()
	{
		if (DisplayPhaseResolver.LocalMachineDrivesDisplay(IsServer, IsLocalPlayer))
			return (_machine.Phase, (_machine.CurrentMove as Crossover)?.BurstDirection ?? 0f);

		float burstDir = _serverMoveId == "crossover" ? _serverMoveParam : 0f;
		return (_serverMovePhase, burstDir);
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

	// ── Committed-move input and behavior (M3 local-only → M4 networked) ─────

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
	///
	/// M4 (#21): after a local Begin()/Feint() succeeds, also tell the server —
	/// UNLESS this machine IS the server (TickServerOwnPlayer), in which case the
	/// local _machine call above already happened on the authoritative copy and
	/// no RPC is needed. This mirrors BallController.TryShoot's split: the
	/// server-own-player path applies directly; only a client predicting ahead
	/// of the server needs to ask for the authoritative transition.
	/// </summary>
	private void SampleMoveInput(bool isServer)
	{
		Vector2 aim = Input.GetVector("aim_left", "aim_right", "aim_up", "aim_down");
		GestureResult gesture = _recognizer.Sample(aim);

		// Keyboard Q = immediate crossover (right burst). Takes precedence over
		// the recognizer so the keyboard and gamepad paths don't double-Begin().
		float crossoverDir = float.NaN;
		if (Input.IsActionJustPressed("move_crossover"))
			crossoverDir = +1f;
		else if (gesture.Kind == GestureKind.Crossover)
			crossoverDir = gesture.Direction;

		if (!float.IsNaN(crossoverDir) && _machine.Begin(new Crossover(crossoverDir)) && !isServer)
			RpcId(1, MethodName.RequestBeginMove, "crossover", crossoverDir);

		// Feint modifier: abort during the startup window.
		// The machine enforces the feint-window guard; false return is silent.
		if (Input.IsActionJustPressed("move_feint") && _machine.Feint() && !isServer)
			RpcId(1, MethodName.RequestFeint);

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

	// ── Committed-move network serialization helpers (M4, #21) ───────────────

	/// <summary>
	/// The moveId to broadcast for the current move, or "" if none — the
	/// ReceiveState payload's sentinel for "Inactive / no move running".
	/// Only one concrete move type exists today (see RequestBeginMove's doc
	/// comment for why a small if-chain, not a registry, is appropriate here).
	/// </summary>
	private static string MoveIdOf(CommittedMove move) => move?.Id ?? "";

	/// <summary>
	/// The move's reconstruction payload to broadcast — today, Crossover's
	/// BurstDirection. 0 when there is no current move or the move type
	/// carries no extra payload.
	///
	/// (#21 doubt cycle 1, finding #2) _serverMoveId/_serverMoveParam are stored
	/// from every broadcast — satisfying "active move included in the server
	/// tick broadcast" — but Step 0 of ReconcileFromServer deliberately does
	/// not reconstruct a CommittedMove from them today; only _serverMovePhase
	/// is consulted (see that method's comment). They are reserved for a
	/// richer reconciliation once a second move type makes "client and server
	/// agree a move is active, but disagree on WHICH one" a reachable case.
	/// </summary>
	private static float MoveParamOf(CommittedMove move) =>
		move is Crossover crossover ? crossover.BurstDirection : 0f;

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
		// The Accel/Decel asymmetry ("change of pace", ADR-0003) lives in
		// MovementMath now (issue #37) so it's unit-testable without a
		// running Godot instance — this call is behavior-identical to the
		// inline ComputeVelocity it replaced.
		Velocity = MovementMath.ComputeVelocity(Velocity, wishDir, delta, MoveSpeed, Accel, Decel);
		MoveAndSlide();
	}
}
