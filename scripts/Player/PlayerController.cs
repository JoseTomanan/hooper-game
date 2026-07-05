using Godot;
using Hooper.Ball;
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
	// ── Movement tuning (issue #183 retune of the M1a values) ────────────────

	/// <summary>Top ground speed in metres/second.</summary>
	[Export] public float MoveSpeed { get; set; } = 6.0f;

	// ── Heading tuning (issue #80) ────────────────────────────────────────────

	/// <summary>
	/// Nominal maximum turn speed in degrees/second, applied at small angular
	/// differences (micro-corrections). Scaled down toward BackTurnSlowFactor
	/// as the angle widens to 180° (see HeadingMath.RotateToward).
	///
	/// 900 °/s at the shipped BackTurnSlowFactor of 0.95 (issue #172 follow-up
	/// feel pass): a full 180° back-turn takes ≈ 0.20 s — this is the integrated
	/// time of the non-linear rate schedule (rate accelerates as the diff closes),
	/// NOT the constant-rate 180/(rate×f) estimate, which overestimates the time.
	/// A 20° micro-correction takes ≈ 0.02 s — effectively instant to the player, so the reversal is now
	/// only mildly slower than a micro-turn (the plant-then-pivot gate at
	/// <see cref="PivotThresholdDeg"/> carries the "commitment" read instead — see
	/// HeadingMath.Step's doc). Raising this scales EVERY turn proportionally; it
	/// is the knob to reach for when "turning feels too slow" overall. (To instead
	/// change only the reversal's relative slowdown, adjust BackTurnSlowFactor.)
	/// </summary>
	[Export] public float MaxTurnRateDeg { get; set; } = 900f;

	/// <summary>
	/// Fraction of MaxTurnRateDeg applied at exactly 180° (back-turn).
	/// The effective rate lerps continuously from MaxTurnRateDeg at diff=0°
	/// to MaxTurnRateDeg × BackTurnSlowFactor at diff=180° — no sharp gear-change.
	///
	/// 0.95 (issue #172 retune 0.35 → 0.90, then a #172 follow-up 0.90 → 0.95): a
	/// back-turn is now only mildly slower than a small correction — the raw
	/// rate no longer has to carry the "back-turn is a commitment" read on
	/// its own, because <see cref="PivotThresholdDeg"/>'s plant-then-pivot
	/// gate now carries that commitment instead (see HeadingMath.Step's doc).
	/// Combined with MaxTurnRateDeg 900, a full 180° reversal comes down to
	/// ≈0.20 s (from the pre-#172 ≈0.55 s).
	/// Values closer to 1.0 approach linear (no slowdown); values closer to
	/// 0 approach a frozen back-turn.
	/// </summary>
	[Export] public float BackTurnSlowFactor { get; set; } = 0.95f;

	/// <summary>
	/// Facing difference, in degrees, above which a turn demands the player
	/// plant their feet and pivot in place before moving, rather than
	/// resolving as an ordinary same-tick rotation (issue #172,
	/// HeadingMath.Step). 90° default: anything up to a quarter-turn is
	/// "forward-ish" and never gates movement; a flick past that — including
	/// a full reverse — is a committed read the opponent can see and punish
	/// (ADR-0003), not a free instant snap-turn.
	/// </summary>
	[Export] public float PivotThresholdDeg { get; set; } = 90f;

	/// <summary>
	/// Ground acceleration in m/s². 45 (issue #183 retune, up from the M1a
	/// default of 30): 0 → top speed in ≈ 0.13 s instead of 0.20 s — the
	/// NBA-2K-style snappier start the human picked for the arcade-relaxed
	/// feel pass (same relaxation as #172). Human feel sign-off pending.
	/// </summary>
	[Export] public float Accel { get; set; } = 45.0f;

	/// <summary>
	/// Ground deceleration in m/s². Higher than Accel intentionally —
	/// that asymmetry is where "change of pace" lives (ADR-0003) and the
	/// #183 retune (45 → 70; full stop in ≈ 0.086 s) deliberately preserves
	/// the ratio so a sudden stop still reads as a change of pace.
	/// </summary>
	[Export] public float Decel { get; set; } = 70.0f;

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
	[Export] public float BurstSpeed { get; set; } = 9.0f;

	/// <summary>
	/// Forward speed of the crossover's Active-phase burst (m/s) — the exit
	/// vector's forward-aligned component (#198). Matches BurstSpeed by
	/// default for a symmetric feel; both are bare feel defaults deferred to
	/// the per-milestone human pass (ADR-0015/CLAUDE.md), not signed off here.
	/// </summary>
	[Export] public float ForwardBurstScale { get; set; } = 9.0f;

	/// <summary>
	/// Hard-decel rate (m/s²) a crossover's Startup plant bleeds momentum at
	/// (#198's hybrid-gather model — see the ADR-0003 amendment this issue's
	/// PR carries). Deliberately LOWER than Decel (70): at MoveSpeed's top
	/// speed (6 m/s) over the crossover's 6-tick/0.1s Startup, 70 would fully
	/// zero it (70×0.1=7 > 6) — indistinguishable from the pre-#198 instant
	/// zero this issue exists to replace. 40 leaves ~2 m/s surviving at top
	/// speed (a genuinely bled, not-zeroed, plant) while still fully clamping
	/// a slow jog to a clean stop. Bare feel default, deferred sign-off.
	/// </summary>
	[Export] public float GatherDecel { get; set; } = 40.0f;

	/// <summary>
	/// Left-stick exit-vector magnitude at/below which Active-entry treats the
	/// stick as neutral (no steering input) — see CrossoverBurstMath's doc.
	/// </summary>
	[Export] public float ExitDeadzone { get; set; } = 0.15f;

	// ── Authoritative heading (issue #80) ────────────────────────────────────

	/// <summary>
	/// Server-authoritative heading in radians (Y-rotation, Godot convention).
	/// Updated every tick inside Move() via HeadingMath.Step (issue #172's
	/// flick-to-latch pivot wrapper around the RotateToward rate schedule) —
	/// the same shared step used for prediction, server authority, and
	/// reconciliation replay (ADR-0002). Broadcast in ReceiveState alongside
	/// pos/vel; the client replays it during reconciliation exactly as pos/vel.
	///
	/// This replaces the cosmetic-only FacingResolver.ResolveYaw(Velocity, …)
	/// as the source of the display yaw (ApplyCosmetics). Elevating it to
	/// server-authoritative state also unblocks issue #81 (facing-based shot
	/// accuracy), which needs to read the server's opinion of where the player
	/// is actually pointing when the shot releases.
	/// </summary>
	public float Heading { get; private set; }

	/// <summary>
	/// Server-authoritative in-place-pivot latch (issue #172), predicted +
	/// reconciled with EXACTLY the same treatment as <see cref="Heading"/>:
	/// updated every tick inside Move() via HeadingMath.Step, broadcast on
	/// ReceiveState, and snapped to the authoritative value before the
	/// reconciliation replay loop so the replay re-evolves it forward
	/// deterministically rather than force-matching a stale broadcast every
	/// tick (see ReconcileFromServer, and the NETCODE LAW note there).
	/// </summary>
	private HeadingMath.PivotState _pivot = HeadingMath.PivotState.None;

	/// <summary>
	/// True while the player is planted mid-pivot and Move() must not advance
	/// position (a flick or held turn past <see cref="PivotThresholdDeg"/>
	/// that hasn't yet reached its latched facing). Exposed read-only for the
	/// deferred animation layer (#184) to drive a plant/pivot pose; movement
	/// and netcode do not read it back — <see cref="_pivot"/> alone is what
	/// Move() consults.
	///
	/// Deliberately a COMPUTED property, not its own stored+set field: per
	/// HeadingMath.Step's contract every one of its return branches sets
	/// IsPivotingInPlace exactly equal to Pivot.HasLatch, so deriving it here
	/// keeps the two impossible to desync — a separate stored bool would need
	/// to be re-set by hand at every place _pivot is snapped from the network
	/// (ReconcileFromServer's pre-replay snap, TickClientRemotePlayer's
	/// display adoption), and missing one of those would leave a stale value
	/// exactly on the tick that matters most (an empty-replay reconcile).
	/// </summary>
	public bool IsPivotingInPlace => _pivot.HasLatch;

	// ── Authoritative ball-hand (M9, issue #83, ADR-0012) ─────────────────────

	/// <summary>
	/// Server-authoritative hand the ball-handler holds the ball in. Promoted
	/// from M7b's cosmetic-only value (issue #73) because the M9 crossover/hesi
	/// model reads it to disambiguate a right-stick flick (toward the empty hand
	/// = crossover + swap; toward the ball hand = hesitation). Since that drives
	/// move resolution it must be server-owned and predicted + reconciled
	/// (ADR-0002, ADR-0012), the same treatment as Heading and the move machine.
	///
	/// Mutated ONLY by the authoritative simulation: it flips to the opposite
	/// hand on the tick a Crossover enters Active (the swap), inside
	/// TickCommittedMoveBehavior — so the change rides the same predicted +
	/// reconciled event the move itself does. A Hesitation does not change it.
	/// Reset to the default (Left) when the player gains possession
	/// (BallController.UpdateHandSide). Broadcast on ReceiveState like Heading;
	/// the ball mesh's left/right offset READS it (BallController.HandSign).
	/// </summary>
	public HandSide HandSide { get; private set; } = HandSide.Left;

	/// <summary>
	/// Authoritative ball-hand from the latest server broadcast. Used for the
	/// remote display copy (TickClientRemotePlayer adopts it, like _serverHeading)
	/// and to restore the own player's predicted hand when a mispredicted move is
	/// reverted on reconcile (ReconcileFromServer's force-Inactive branch). It is
	/// deliberately NOT snapped every tick — see that branch's comment.
	/// </summary>
	private HandSide _serverHandSide = HandSide.Left;

	/// <summary>
	/// Resets the authoritative hand to the default (Left). Called by
	/// BallController on a possession change (the new holder has no carried-over
	/// hand state). Runs on every simulating role — server authoritative, client
	/// predicted — so all machines agree; the client's remote copy instead adopts
	/// the broadcast value. Left is simply *a* deterministic default every peer
	/// agrees on (issue #73); nothing downstream depends on which side it is.
	/// </summary>
	public void ResetHandSide() => HandSide = HandSide.Left;

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
	/// The latest RAW left-stick reading received from the client this tick
	/// (server-side, remote player only) — separate from <see cref="_pendingInput"/>
	/// on purpose (#198). _pendingInput is deliberately zeroed by the CLIENT
	/// itself while a committed move IsActive (TickClientOwnPlayer's own
	/// comment explains why: it protects the brief Inactive-on-server/
	/// predicted-Active-on-client window before RequestBeginMove arrives, so
	/// the server does not move the player on stale intent during that gap).
	/// The moving crossover's exit vector needs the OPPOSITE guarantee — the
	/// TRUE stick value, continuously, even mid-move — so it rides its own
	/// always-on field fed by two extra SubmitInput floats, never the
	/// intentionally-gated _pendingInput. Read ONLY inside
	/// TickCommittedMoveBehavior's Crossover branch; never substituted into
	/// the Move() call the way _pendingInput is.
	///
	/// This deliberately does NOT ride the moveId/moveParam one-shot RPC the
	/// way Crossover.BurstDirection does: that RPC fires once at Begin()
	/// (Startup-entry), 6 frames before Active begins, so the stick's future
	/// position is unknowable at send time. SubmitInput already streams the
	/// true stick every tick — exactly the "not a local-only read, predicted
	/// + reconciled" channel the exit vector needs at the LATER Active-entry
	/// tick, without racing Active's short (3-tick default) window the way a
	/// new one-shot RPC fired at Active-entry would (doubt cycle, #198).
	/// </summary>
	private Vector2 _pendingRawStick;

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
	/// Authoritative heading received from the server, staged for reconcile
	/// (own player) and for display (client's remote copy). Snapped to
	/// Heading before the replay loop in ReconcileFromServer so the heading
	/// is replayed forward from the correct authoritative base — identical
	/// treatment to _serverPos/_serverVel (ADR-0002).
	/// </summary>
	private float _serverHeading;

	/// <summary>
	/// Authoritative pivot-latch state from the latest server broadcast
	/// (issue #172) — the exact same staging role _serverHeading plays.
	/// Consumed by ReconcileFromServer's pre-replay snap (own player) and by
	/// TickClientRemotePlayer's display adoption (remote copy).
	/// </summary>
	private bool _serverPivotHasLatch;
	private float _serverPivotLatchedYaw;

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

	/// <summary>
	/// (#175) Authoritative CommittedMoveMachine.WasRecoveryEnteredEarly from
	/// the latest broadcast — level-triggered (true for the server's WHOLE
	/// Recovery duration when it resulted from an EndActiveEarly() early end,
	/// e.g. a resolved steal), not a single-tick edge, so a dropped
	/// UnreliableOrdered broadcast doesn't lose the signal for the remainder of
	/// that Recovery window. Feeds ReconcileFromServer's Step 0.5
	/// (ShouldForceRecovery) — the fix for the OWN client's Active prediction
	/// otherwise never learning the server ended its move early.
	/// </summary>
	private bool _serverEndedActiveEarly;

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

	/// <summary>
	/// Cached BallController reference, discovered via the "ball" group
	/// (BallController.AddToGroup("ball") in its own _Ready) — the same
	/// lazy-group-lookup pattern as _gameManager/GetGameManager, needed for
	/// the same reason: a player node's _Ready can race ahead of the ball's
	/// in the scene tree. Used by IsBallHolder and the shoot-input read in
	/// SampleMoveInput (M7b, issue #74).
	/// </summary>
	private BallController _ball;

	/// <summary>Resolves _ball, re-querying the group if still null. See field doc.</summary>
	private BallController GetBall()
	{
		if (_ball == null)
			_ball = GetTree().GetFirstNodeInGroup("ball") as BallController;
		return _ball;
	}

	/// <summary>
	/// True when this player node is the ball's current holder — gates the
	/// shoot input the same way the old BallController.TryShoot's IsLocalHolder
	/// check did (M7b, issue #74). Keyed on Name == HolderPeerId, the same
	/// peer-ID-as-node-name identity contract IsLocalPlayer already uses.
	/// </summary>
	private bool IsBallHolder
	{
		get
		{
			BallController ball = GetBall();
			return ball != null && OwnPeerId != 0 && ball.StateMachine.HolderPeerId == OwnPeerId;
		}
	}

	private int? _cachedOwnPeerId;

	/// <summary>
	/// This node's own peer id, parsed from Name once and cached (#204
	/// code-review cleanup). Name is the peer-ID-as-node-name identity
	/// contract every Name-parse site in this class already assumes
	/// (NetworkManager.SpawnPlayer names nodes by peer id; it never changes
	/// for the node's lifetime), so caching removes a redundant
	/// int.TryParse from the 60Hz tick path. 0 (never a real peer id) is
	/// returned if Name fails to parse, matching every other Name-parse
	/// site's fail-safe default.
	/// </summary>
	private int OwnPeerId
	{
		get
		{
			_cachedOwnPeerId ??= int.TryParse(Name, out int id) ? id : 0;
			return _cachedOwnPeerId.Value;
		}
	}

	/// <summary>
	/// True for exactly one tick — the tick this node's committed-move machine
	/// enters Active on a JumpShot (M7b, issue #74). BallController.
	/// CheckJumpShotRelease reads this on the holder's node to fire the
	/// actual ball-state transition; this property never touches ball state
	/// itself, only exposes the timing. JumpShotReleaseResolver is the pure,
	/// unit-tested decision — this is the thin node-side glue reading it off
	/// the live local _machine (correct for every role that calls this: the
	/// server's authoritative copy of either holder role, and the client's
	/// own prediction — see CheckJumpShotRelease's doc for why the client's
	/// copy of a REMOTE holder is never consulted here).
	/// </summary>
	public bool JustReleasedJumpShot =>
		JumpShotReleaseResolver.ShouldRelease(_machine.JustEnteredActive, _machine.CurrentMove);

	/// <summary>
	/// While this player's committed-move machine is in the Active phase of a
	/// StealMove, returns the targeted hand side; null on every other tick.
	///
	/// BallController.ResolveStealAttempts reads this every physics tick (server-
	/// only) to resolve steal attempts (ADR-0018 §1–2, issue #96). The null return
	/// is the fast path — most ticks nobody is stealing — so the caller can do a
	/// simple null-guard with zero overhead on inactive defenders.
	///
	/// Why the WHOLE Active phase, not just its entry tick (JustEnteredActive):
	/// ADR-0018 defines a steal's success as its Active window OVERLAPPING the
	/// exposed dribble band — an interval relationship. Sampling only the single
	/// entry tick collapses that interval to a point and makes the Active window's
	/// width inert, so a defender who enters Active a tick early (band not yet
	/// open) but whose window fully covers the band would wrongly whiff. Reporting
	/// the hand on every Active tick lets ResolveStealAttempts re-check the live
	/// dribble phase each tick and succeed on the first in-band tick — the
	/// interval model, evaluated against ground-truth phase with no projection.
	/// (Note: IsActive is the WRONG check — it is true for Startup/Recovery too.)
	/// </summary>
	public HandSide? ActiveStealTargetHand
	{
		get
		{
			if (_machine.Phase != MovePhase.Active) return null;
			return _machine.CurrentMove is StealMove steal ? steal.TargetHand : (HandSide?)null;
		}
	}

	/// <summary>
	/// Server-only: ends this defender's StealMove Active phase the instant
	/// BallController.ResolveStealAttempts resolves it as a success, paying
	/// Recovery immediately instead of riding out the remaining ActiveFrames.
	///
	/// Why this must exist (issue #96 remediation, multi-fire bug): a
	/// resolved steal calls BallState.GoLoose(), and TickLoose's proximity
	/// scramble (ResolveLooseBallRecovery) can re-award the ball to the SAME
	/// still-in-place holder within the very next tick — but DribbleCycle.Phase
	/// only advances in TickDribbling, so it is frozen in-band for as long as
	/// the ball is Loose. Without this call, ActiveStealTargetHand keeps
	/// returning this defender's TargetHand for every remaining Active tick,
	/// and ResolveStealAttempts would see Dribbling + in-band + matching-hand
	/// again and fire GoLoose() repeatedly — up to ActiveFrames times for one
	/// committed move. Ending Active the moment it resolves means one
	/// StealMove can produce at most one turnover, matching every other
	/// committed move's "spent once, then Recovery" contract.
	/// </summary>
	public bool EndResolvedSteal() => _machine.EndActiveEarly();

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
			// The host IS the local machine — ReadInput() is the TRUE, zero-
			// latency left stick, exactly what the exit-vector snapshot needs
			// at Active-entry (#198). No network involved for this role.
			TickCommittedMoveBehavior(delta, ReadInput());
		else
		{
			Vector2 input = ReadInput();
			Move(input, delta);
			CheckAutoStartDribble(input);
		}

		// Broadcast authoritative state to all clients.
		// ackSeq = 0 because the host has no client-input queue to acknowledge.
		// Heading is piggybacked on this existing broadcast — same staleness
		// and redundancy properties as pos/vel; no separate channel needed.
		// Source: Rpc(MethodName.X) broadcasts to all peers.
		// (#175) WasRecoveryEnteredEarly appended LAST, after handSide — kept
		// as its own trailing bool (not packed into an existing int slot) so a
		// future positional edit to this already enum-as-int-heavy signature
		// can't silently transpose it with handSide (see ReceiveState's doc).
		// (#172) pivotHasLatch/pivotLatchedYaw appended AFTER that, as their
		// own distinct bool+float pair (not reusing handSide's int slot or
		// heading's float slot) for the same transposition-safety reason —
		// two same-typed trailing params next to unrelated ones is exactly
		// the positional-arg fragility this file's ReceiveState doc already
		// warns about; keeping them last and together limits the blast radius
		// of a future edit to one contiguous pair.
		Rpc(MethodName.ReceiveState, 0, GlobalPosition, Velocity,
			(int)_machine.Phase, _machine.FrameInPhase, MoveIdOf(_machine.CurrentMove), MoveParamOf(_machine.CurrentMove),
			Heading, (int)HandSide, _machine.WasRecoveryEnteredEarly, _pivot.HasLatch, _pivot.LatchedYaw);
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
			// _pendingRawStick, NOT _pendingInput — see that field's doc for why
			// the exit-vector snapshot needs the always-on channel rather than
			// the one the client itself zeroes during a committed move (#198).
			TickCommittedMoveBehavior(delta, _pendingRawStick);
		else
		{
			Move(_pendingInput, delta);
			CheckAutoStartDribble(_pendingInput);
		}

		// Echo _serverAckedSeq so the client prunes its pending buffer, plus
		// the committed-move state and heading piggybacked on the same broadcast
		// (see ReceiveState below for the payload rationale). (#175) Same
		// trailing WasRecoveryEnteredEarly bool as TickServerOwnPlayer's broadcast.
		// (#172) Same trailing pivotHasLatch/pivotLatchedYaw pair too.
		Rpc(MethodName.ReceiveState, _serverAckedSeq, GlobalPosition, Velocity,
			(int)_machine.Phase, _machine.FrameInPhase, MoveIdOf(_machine.CurrentMove), MoveParamOf(_machine.CurrentMove),
			Heading, (int)HandSide, _machine.WasRecoveryEnteredEarly, _pivot.HasLatch, _pivot.LatchedYaw);
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
			ReconcileFromServer(_serverPos, _serverVel, _serverAckedSeq, _serverHeading, delta);
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
		Vector2 rawStick = ReadInput();
		Vector2 moveInput = _machine.IsActive ? Vector2.Zero : rawStick;

		// Record() assigns the seq and handles capacity eviction (see
		// PredictionBuffer doc) — behavior-identical to the inline
		// _seq++ / cap-check / enqueue this replaced.
		int seq = _buffer.Record(moveInput);

		if (_machine.IsActive)
			// Local zero-lag prediction: rawStick, not moveInput (moveInput is
			// deliberately zeroed above for Move()/replay purposes — see the
			// comment on that line — but the exit-vector snapshot needs the
			// TRUE stick, #198).
			TickCommittedMoveBehavior(delta, rawStick);
		else
		{
			Move(moveInput, delta);
			CheckAutoStartDribble(moveInput);
		}

		// Send input to server. UnreliableOrdered: dropped packets are gone,
		// but arriving packets are always in seq order (no regress to stale input).
		// Source: RpcId(peerId, MethodName.X) — peerId 1 = server.
		//
		// rawStick is sent as TWO EXTRA trailing floats, always the true stick
		// value — deliberately NOT gated by _machine.IsActive the way moveInput
		// is (#198). This is what lets the SERVER's copy of a REMOTE player's
		// crossover read a real, continuously-updated exit vector at its own
		// Active-entry tick (via _pendingRawStick) instead of the intentionally-
		// blanked moveInput/_pendingInput. See _pendingRawStick's doc for the
		// full doubt-driven rationale (why this rides SubmitInput's continuous
		// stream rather than the moveId/moveParam one-shot RPC).
		RpcId(1, MethodName.SubmitInput, seq, moveInput.X, moveInput.Y, rawStick.X, rawStick.Y);
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
		// Adopt the server's heading directly — the remote copy never runs
		// Move(), so it has no local HeadingMath step to advance Heading.
		// Setting it here ensures ApplyCosmetics displays the correct
		// authoritative facing on the opponent's model.
		Heading = _serverHeading;
		// Same for the pivot latch (#172): the remote copy never runs Move(),
		// so it has no local HeadingMath.Step to derive it from — adopting the
		// broadcast value directly is what lets IsPivotingInPlace (computed
		// from _pivot) drive a future opponent-plant animation (#184).
		_pivot = new HeadingMath.PivotState(_serverPivotHasLatch, _serverPivotLatchedYaw);
		// Same for the ball-hand (M9, #83): the remote copy never simulates the
		// swap, so it adopts the broadcast value — this is how the opponent's
		// crossover hand-switch renders on your screen (BallController.HandSign
		// reads HandSide on the holder's node, whichever role it is).
		HandSide = _serverHandSide;
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
	///
	/// rawStickX/Y (#198) are the SAME hardware read as inputX/Y EXCEPT they
	/// are never gated to zero during a committed move — see _pendingRawStick's
	/// doc for why the moving crossover's exit vector needs that guarantee and
	/// why it piggybacks this existing continuous RPC rather than a new one.
	/// </summary>
	[Rpc(MultiplayerApi.RpcMode.AnyPeer,
		 CallLocal = false,
		 TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
	private void SubmitInput(int seq, float inputX, float inputY, float rawStickX, float rawStickY)
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

		_serverAckedSeq  = seq;
		_pendingInput    = new Vector2(inputX, inputY);
		// Same "freshest wins" semantics as _pendingInput above (both are
		// behind the same stale-seq guard on this method). The distinction
		// from _pendingInput is NOT about seq ordering — it's that the SENDER
		// (TickClientOwnPlayer) never blanks rawStickX/Y to zero while a
		// committed move IsActive the way it blanks moveInput.X/Y, so this
		// field always reflects the true stick, not a role/move-state gate
		// applied on this end.
		_pendingRawStick = new Vector2(rawStickX, rawStickY);
	}

	/// <summary>
	/// Starts a committed move on <see cref="_machine"/> and — only if it
	/// actually started — cancels any in-progress in-place pivot (issue #172).
	///
	/// This is the ONE shared code path every _machine.Begin(...) call site in
	/// this file must go through instead of calling Begin() directly, so the
	/// pivot-cancel rule is enforced identically regardless of which role is
	/// starting the move: the server's own player and the owning client's
	/// local prediction both reach it via SampleMoveInput; the server's copy
	/// of a remote player reaches it via RequestBeginMove. Because it's the
	/// same code running on both server and client — not a value carried over
	/// the network — server and client agree on "a pivot was cancelled" on
	/// the same tick the move begins, with no RTT needed to converge.
	///
	/// Gated on Begin()'s return value (not called unconditionally before it)
	/// because Begin() silently no-ops when the move is illegal in the current
	/// phase (e.g. still mid-Recovery) — an illegal attempt must not cancel a
	/// legitimately in-progress pivot.
	///
	/// A committed move plants and freezes Heading for its whole duration
	/// (TickCommittedMoveBehavior skips Move() entirely), so there is nothing
	/// left for a stale pivot latch to resolve against once the move starts;
	/// clearing it here rather than leaving it to silently resume after
	/// Recovery is what makes the burst fire from the CURRENT Heading, not an
	/// unrelated facing debt inherited from before the move (HandStateResolver.
	/// BurstWorldDir already reads Heading directly — no change needed there).
	/// </summary>
	private bool BeginCommittedMove(CommittedMove move)
	{
		// #193 code-review fix: a Crossover/Hesitation IS a dribble move in
		// real ball — it cannot legally begin while this player HOLDS the
		// ball in Held state, dead OR live. Without this gate, JumpShot was
		// the only move #193 special-cased, so a dead-Held player (post-
		// cradle, or post a canceled pump-fake) could still Begin a
		// crossover: the burst fires and the JustEnteredActive HandSide flip
		// (TickCommittedMoveBehavior) authoritatively teleports the HELD ball
		// to the other hand, escaping the dead-dribble rule's whole point —
		// #193's own "stranded in dead Held" cost became avoidable. From a
		// LIVE Held possession the fix is the same: the player must push the
		// stick to start dribbling first (CheckAutoStartDribble ->
		// BallController.TryStartDribble), matching real ball's "you can't
		// crossover a ball you haven't started bouncing yet."
		//
		// Gated on IsBallHolder specifically — a player WITHOUT the ball
		// (defense) is never affected; their crossover/hesitation attempts
		// are unrelated to possession state.
		if ((move is Crossover || move is Hesitation) && IsBallHolder
			&& GetBall()?.State == BallState.Held)
			return false;

		if (!_machine.Begin(move)) return false;
		_pivot = HeadingMath.PivotState.None;

		// #193 (triple threat / dead-dribble rule): a JumpShot's gather is
		// inherent to the shooting motion, not a separate input — so cradling
		// a live dribble is a SIDE EFFECT of the shot's Startup beginning,
		// fired right here at the ONE choke point every Begin(JumpShot) call
		// already funnels through. This also covers the pump-fake: a feint is
		// a Startup-phase ABORT of this SAME move (CommittedMoveMachine.
		// Feint()), not a second Begin(), so there is no separate hook needed
		// for it — a canceled pump-fake still leaves HasDribbled set, which is
		// the intentional "you can't un-pick-up your dribble by faking a shot"
		// cost the issue calls for. BallController.CradleForShotStartup no-ops
		// on its own if the ball isn't Dribbling for this exact peer, so this
		// call is safe to fire unconditionally whenever a JumpShot begins.
		if (move is JumpShot && OwnPeerId != 0)
			GetBall()?.CradleForShotStartup(OwnPeerId);

		return true;
	}

	/// <summary>
	/// Called BY THE CLIENT on the SERVER's copy of this node, requesting the
	/// authoritative start of a committed move.
	///
	/// Transfer mode: Reliable — a deliberate deviation from SubmitInput's
	/// UnreliableOrdered, the same one-time-discrete-event reasoning the old
	/// BallController.RequestShoot (#20) used before M7b #74 replaced it with
	/// phase-derived release. This is a ONE-TIME discrete event with no
	/// redundancy: a dropped packet means the
	/// player pressed crossover and nothing happened, a correctness bug, not a
	/// smoothing concern. Reliable's head-of-line-blocking risk (the reason
	/// ReceiveState/SubmitInput avoid it) doesn't apply because this fires
	/// rarely — once per committed move attempt, not every physics tick — so
	/// there is no continuous stream for a retransmit to stall.
	///
	/// moveId/param is the minimal payload to reconstruct a move. Two concrete
	/// moves exist now ("crossover", carrying BurstDirection as param; and
	/// "jumpshot", M7b issue #74, carrying no param) so a small if-chain is
	/// still correct and proportionate — see CommittedMove.Id's doc comment,
	/// which already anticipated this exact use. Revisit only if a third move
	/// arrives with materially different reconstruction needs than a simple
	/// id dispatch; do not build a registry/factory pre-emptively.
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
	/// to ANY RPC-plus-fixed-tick-loop design (it equally affects SubmitInput);
	/// resolving it would require lockstep simulation, well beyond M4's scope.
	/// Begin()'s own legality check is
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
			// param is the body-relative flick sign (M9, #85) — the world burst
			// direction is derived from Heading when the move reaches Active.
			BeginCommittedMove(new Crossover(burstDirection: param));
		else if (moveId == "hesitation")
			BeginCommittedMove(new Hesitation());
		else if (moveId == "jumpshot")
		{
			// Capture the server-authoritative pre-plant speed at begin for the
			// movement scatter penalty (#137) — see ShotInitiationSpeed.
			if (BeginCommittedMove(new JumpShot()))
				CaptureShotInitiationSpeed();
		}
		else if (moveId == "steal")
		{
			// param = (float)(int)HandSide: 0 → Left, 1 → Right (written by
			// SampleMoveInput; see comment there for rationale).
			// The CommittedMoveMachine enforces the usual phase guards (Inactive-
			// only Begin), so a client that sends "steal" while still in Recovery
			// gets a silent no-op — Begin() returns false and nothing happens.
			HandSide target = param > 0.5f ? HandSide.Right : HandSide.Left;
			BeginCommittedMove(new StealMove(target));
		}
		// Unrecognized moveId: silently ignored. A malformed/forged moveId
		// from a tampered client simply does nothing.
	}

	/// <summary>
	/// XZ-plane speed of the shooter at the tick their JumpShot BEGAN — captured
	/// before the committed-move Startup plant zeroes Velocity. The shot's movement
	/// scatter penalty (#64/#137) reads this, NOT the (planted, ~0) release-time
	/// velocity: a committed jump shot always plants the feet for legibility
	/// (ADR-0003), so the velocity present at release no longer reflects whether the
	/// shot was a set shot or a sprinting pull-up. Server-authoritative for the
	/// make/miss (shot scatter is computed server-side only); each peer also sets
	/// its own value on Begin, but a client's is never read for the outcome.
	/// </summary>
	public float ShotInitiationSpeed { get; private set; }

	/// <summary>
	/// Records <see cref="ShotInitiationSpeed"/> from the current (pre-plant)
	/// Velocity. Called on the tick a JumpShot's Begin() succeeds, at every begin
	/// site (local prediction in SampleMoveInput and the authoritative
	/// RequestBeginMove handler).
	/// </summary>
	private void CaptureShotInitiationSpeed() =>
		ShotInitiationSpeed = new Vector2(Velocity.X, Velocity.Z).Length();

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
	///
	/// (#175) endedActiveEarly is CommittedMoveMachine.WasRecoveryEnteredEarly
	/// from the server's copy of THIS player's machine — appended last, as a
	/// plain bool (Godot's [Rpc] Variant marshaling handles bool natively, so
	/// unlike movePhase there is no enum-boxing reason to send it as an int).
	/// It feeds ReconcileFromServer's Step 0.5 (ShouldForceRecovery): the fix
	/// for the OWN client's Active prediction never learning that the server
	/// resolved this move's Active phase early (e.g. a successful steal).
	///
	/// (#172) pivotHasLatch/pivotLatchedYaw are the authoritative in-place-
	/// pivot latch, same UnreliableOrdered snapshot properties as heading:
	/// staged here, then snapped into _pivot pre-replay in ReconcileFromServer
	/// (own player) or adopted directly for display in TickClientRemotePlayer
	/// (remote copy) — never force-matched every tick, per this file's
	/// standing snap-then-replay reconciliation law (see that method's
	/// Heading-snap comment).
	/// </summary>
	[Rpc(MultiplayerApi.RpcMode.Authority,
		 CallLocal = false,
		 TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
	private void ReceiveState(int ackSeq, Vector3 pos, Vector3 vel,
		int movePhase, int frameInPhase, string moveId, float moveParam, float heading, int handSide,
		bool endedActiveEarly, bool pivotHasLatch, float pivotLatchedYaw)
	{
		_serverPos       = pos;
		_serverVel       = vel;
		_serverAckedSeq  = ackSeq;
		_serverMovePhase = (MovePhase)movePhase;
		_serverMoveId    = moveId;
		_serverMoveParam = moveParam;
		// Authoritative ball-hand (M9, #83/ADR-0012). Same UnreliableOrdered
		// snapshot properties as heading: the remote copy adopts it for display
		// (TickClientRemotePlayer), the own player consults it only to restore a
		// reverted predicted swap (ReconcileFromServer), never to snap every tick.
		_serverHandSide  = (HandSide)handSide;
		// Authoritative heading for reconcile (own player) and display
		// (client's remote copy). Same UnreliableOrdered staleness properties
		// as pos/vel — fine, since the replay loop catches the own player up
		// and the remote player displays the latest received value.
		_serverHeading   = heading;
		// (#172) See _serverPivotHasLatch/_serverPivotLatchedYaw's field doc.
		_serverPivotHasLatch   = pivotHasLatch;
		_serverPivotLatchedYaw = pivotLatchedYaw;
		// (#175) See _serverEndedActiveEarly's field doc — level-triggered,
		// so overwriting every broadcast (even a redundant "still true") is
		// correct and required for the packet-loss robustness that field relies on.
		_serverEndedActiveEarly = endedActiveEarly;
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
	private void ReconcileFromServer(Vector3 authPos, Vector3 authVel, int ackSeq, float authHeading, double delta)
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
		{
			_machine.ForceState(MovePhase.Inactive, frameInPhase: 0, move: null);
			// The reverted move may have been a crossover that already swapped the
			// PREDICTED hand (M9, #83/ADR-0012). Restore the authoritative hand in
			// this SAME branch — and only here, never every tick: an unconditional
			// per-tick snap would force-revert a CORRECTLY predicted swap for ~1 RTT
			// until the confirming broadcast arrives, flickering the ball between
			// hands on every legitimate crossover — the exact trap the FrameInPhase
			// reasoning above describes for move phase. The residual staleness of a
			// confirmed swap is the accepted, self-correcting gap (ADR-0012),
			// identical in spirit to the phantom-second-move artifact documented
			// in Step 0 above.
			HandSide = _serverHandSide;
		}
		// Step 0.5 (#175): the OWN client keeps predicting Active for the rest
		// of a move's window even after the server ends that move's Active
		// phase EARLY (EndActiveEarly() — today only a resolved steal). Unlike
		// Step 0 above, this is NOT "the server disagrees a move ran at all" —
		// it is "the server agrees a move ran, but it's further along than the
		// client's local prediction" — so it is deliberately a SEPARATE gate
		// (ShouldForceRecovery), not a broadened Step 0. Folding a
		// `serverPhase == Recovery` case into ShouldForceInactive's contract
		// would misfire on every ORDINARY Active→Recovery boundary a move
		// crosses under jitter, which is exactly the failure mode this issue's
		// remediation must not reintroduce (see ShouldForceRecovery's doc for
		// how the level-triggered WasRecoveryEnteredEarly bit — not serverPhase
		// alone — is what distinguishes "early" from "on schedule", and why a
		// moveId identity check bounds this to the same accepted staleness
		// trade-off Step 0 above already documents, rather than a wider one).
		//
		// Only reachable when Step 0 above did NOT already force Inactive
		// (mutually exclusive: Step 0 fires on serverSaysInactive, this one
		// requires serverPhase == Recovery).
		else if (CommittedMoveMachine.ShouldForceRecovery(
			_machine.Phase, _serverMovePhase, _serverEndedActiveEarly,
			MoveIdOf(_machine.CurrentMove), _serverMoveId))
		{
			// frameInPhase forced to 0, not the server's (possibly further-
			// advanced, ~1-RTT-stale) FrameInPhase — a discrete identity
			// correction (Phase), never a stale continuous value snap, per
			// this file's standing reconciliation law (see Step 0's comment).
			// recoveryWasEarly: true so a future consumer of
			// WasRecoveryEnteredEarly on THIS client sees the same truth the
			// server does (ForceState's own doc explains why leaving this
			// un-set would be a silent partial-overwrite bug).
			_machine.ForceState(MovePhase.Recovery, frameInPhase: 0, move: _machine.CurrentMove, recoveryWasEarly: true);
		}

		// Step 1: prune confirmed inputs.
		_buffer.Acknowledge(ackSeq);

		// Step 2: remember current rendered position for divergence calculation.
		Vector3 renderedPos = GlobalPosition;

		// Snap physics to authoritative state.
		GlobalPosition = authPos;
		Velocity       = authVel;

		// Snap heading to the authoritative value BEFORE the replay loop so
		// each replayed Move() step advances it forward from the correct base —
		// identical treatment to GlobalPosition/Velocity. Without this snap,
		// the replay would diverge from the server's path whenever the server's
		// heading differed from the local prediction's heading at the ack point
		// (e.g. after a large turn where the non-linear rate produces
		// per-tick differences that accumulate over unacknowledged ticks).
		Heading = authHeading;

		// (#172) Same pre-replay snap for the pivot latch as Heading immediately
		// above — IsPivotingInPlace is a computed readout of _pivot.HasLatch
		// (see its doc), so this single assignment also makes it correct for
		// this same tick even when the replay buffer below is EMPTY (i.e. every
		// input has already been acked): with no snap here, an idle/ack'd-up
		// client would otherwise keep IsPivotingInPlace pinned to whatever its
		// last locally-predicted Move() call left it at, silently drifting from
		// the server's opinion. Note this rides the same accepted trade-off
		// Heading already carries into the replay loop below: a buffered input
		// whose real-time tick had a committed move Active would have skipped
		// Move() entirely (TickCommittedMoveBehavior instead) but is replayed
		// through Move() here regardless (see TickClientOwnPlayer's "Step 3 of
		// ReconcileFromServer replays ... through Move() only" comment) — a
		// pre-existing, _smoothOffset-covered positional trade-off that _pivot
		// now inherits rather than introduces.
		_pivot = new HeadingMath.PivotState(_serverPivotHasLatch, _serverPivotLatchedYaw);

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

		// Display yaw now derives from the authoritative Heading (issue #80,
		// ADR-0010) instead of FacingResolver.ResolveYaw(Velocity, …).
		// Heading is server-authoritative and replayed on reconcile, so it
		// expresses the true bounded turn cost that both players observe —
		// not a purely cosmetic velocity-derived estimate. FacingResolver is
		// left in the codebase for historical reference; it is no longer
		// called on this code path. _visualYaw is preserved as the field name
		// for the lean computation below, but its value now comes from Heading.
		_visualYaw = Heading;
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
	/// and MoveParamOf already speak). A JumpShot has no burst payload, so both
	/// paths naturally return 0 for it — correct, since the jump shot has no
	/// lateral lean of its own (ApplyCosmetics/LeanResolver only react to a
	/// nonzero burst).
	///
	/// Public (M7b, issue #73): BallController.UpdateHandSide reads this on the
	/// holder's node so the ball-on-hand display rides the same per-role DISPLAY
	/// path #69 established, rather than re-deriving its own role logic.
	/// </summary>
	public (MovePhase phase, float burstDir) DisplayMove()
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

	/// <summary>
	/// Drives the ball out of a live Held possession the instant the holder
	/// pushes the stick past deadzone (#193, ADR-0008's dead-dribble rule): a
	/// fresh possession now starts Held-not-Dribbling (BallController.
	/// AwardPossession no longer auto-chains into a dribble), so ordinary
	/// movement intent is what resumes it — the "drive" read out of a triple-
	/// threat stance.
	///
	/// Called only where <paramref name="input"/> is REAL ground-truth for
	/// THIS player this tick — own-player local hardware on the server or a
	/// client (TickServerOwnPlayer/TickClientOwnPlayer), or a remote player's
	/// latest submitted input on the server (TickServerRemotePlayer) — never
	/// from TickClientRemotePlayer, which has no local input to read. This is
	/// exactly the authority set every other ball-state write in this file
	/// already restricts to (see BallController.CradleForShotStartup's doc).
	///
	/// Godot's Input.GetVector already clamps anything inside an action's
	/// configured deadzone down to an exact Vector2.Zero (project.godot's
	/// move_* actions carry a 0.2 deadzone), so a nonzero vector here already
	/// means "past deadzone" — no separate threshold constant needed.
	///
	/// BallController.TryStartDribble owns every actual gameplay guard (must
	/// be Held, dead-dribble refusal) — this call site pre-filters on
	/// IsBallHolder (#204 code-review cleanup) so a non-holder's movement
	/// input never even makes the cross-object call, which TryStartDribble's
	/// own HolderPeerId check would have no-opped on anyway.
	/// </summary>
	private void CheckAutoStartDribble(Vector2 input)
	{
		if (input == Vector2.Zero) return;
		if (!IsBallHolder) return;
		GetBall()?.TryStartDribble(OwnPeerId);
	}

	// ── Committed-move input and behavior (M3 local-only → M4 networked) ─────

	/// <summary>
	/// Samples gesture input, feeds the recognizer, and advances the committed-
	/// move machine one tick. Called at the top of every local-player tick so
	/// JustEnteredActive is correct when TickCommittedMoveBehavior reads it.
	///
	/// Right-stick gestures: routed through RightStickGestureRecognizer. Both
	/// input devices feed it via the aim_* actions — gamepad right stick, or
	/// the IJKL keys (#191); the recognizer is hardware-agnostic, so keyboard
	/// gets the same crossover/hesitation/feint semantics as the stick.
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

		// One right-stick flick, disambiguated by which hand holds the ball
		// (M9, ADR-0012): a flick TOWARD the empty hand is a crossover (ball
		// swaps to that hand + a lateral burst that way); a flick TOWARD the
		// ball hand is a hesitation (freeze/bait — no swap, no scripted burst;
		// the player drives the exit with the left stick after the move
		// resolves). Keyboard reaches this through the same recognizer via the
		// IJKL bindings on the aim_* actions (#191) — there is no separate
		// keyboard path, so the flick sign is always real and signed and
		// IsCrossover alternates correctly on either input device.
		if (gesture.Kind == GestureKind.Crossover)
		{
			int flickSign = System.Math.Sign(gesture.Direction);
			if (HandStateResolver.IsCrossover(HandSide, flickSign))
			{
				// Crossover carries the BODY-RELATIVE flick sign (M9, #85): the
				// world burst direction is derived from Heading at apply time
				// (TickCommittedMoveBehavior), so the burst follows the body's
				// facing rather than a fixed screen axis. The wire param is the
				// same single float RequestBeginMove already speaks.
				if (BeginCommittedMove(new Crossover(flickSign)) && !isServer)
					RpcId(1, MethodName.RequestBeginMove, "crossover", flickSign);
			}
			else if (BeginCommittedMove(new Hesitation()) && !isServer)
			{
				RpcId(1, MethodName.RequestBeginMove, "hesitation", 0f);
			}
		}

		// Shoot: begin a JumpShot (M7b, issue #74) — this REPLACES the old
		// instant "ball leaves hand on press" trigger that used to live in
		// BallController.TryShoot. Holder-gated the same way that old trigger
		// was (IsBallHolder mirrors its IsLocalHolder check); Begin() itself
		// enforces Inactive-only legality, so a shot attempt mid-crossover (or
		// mid-shot) silently no-ops exactly like a second crossover attempt
		// would. The actual ball release is NOT requested here — it fires
		// several ticks later, on this machine's own JustEnteredActive, which
		// BallController.CheckJumpShotRelease reads via JustReleasedJumpShot.
		BallController ball = GetBall();
		if (ball != null && Input.IsActionJustPressed(ball.ShootAction) && IsBallHolder
			&& BeginCommittedMove(new JumpShot()))
		{
			// Capture the pre-plant locomotion speed NOW — Startup zeroes Velocity,
			// so the movement scatter penalty must read speed at shot initiation,
			// not at release (#137). Only the server's value feeds the outcome.
			CaptureShotInitiationSpeed();
			if (!isServer)
				RpcId(1, MethodName.RequestBeginMove, "jumpshot", 0f);
		}

		// Steal: defensive committed move (M10, issue #96, ADR-0018).
		//
		// Gated on NOT holding the ball — you cannot steal from yourself.
		// The "side" axis of the two-axis steal read (ADR-0018 §2) is chosen here
		// from the AIM stick X component: right aim (aimX > 0) → HandSide.Right,
		// left/neutral → HandSide.Left.  This reads the AIM direction, NOT the
		// movement stick, so the defender can separately move and aim their reach.
		//
		// ADR-0014 / real-ball rationale: in half-court 1v1 the most common steal
		// attempt is a swipe at the dribble hand; the AIM axis lets the defender
		// commit the read explicitly rather than guessing a default.
		// Wire param = (float)(int)HandSide so RequestBeginMove can reconstruct
		// the TargetHand without widening the RPC signature (HandSide.Left=0,
		// HandSide.Right=1 — safe as float across the wire).
		if (Input.IsActionJustPressed("def_steal") && !IsBallHolder)
		{
			HandSide target = aim.X > 0f ? HandSide.Right : HandSide.Left;
			if (BeginCommittedMove(new StealMove(target)) && !isServer)
				RpcId(1, MethodName.RequestBeginMove, "steal", (float)(int)target);
		}

		// Feint modifier: abort during the startup window. Two input paths feed
		// the SAME Feint() call (#139): the discrete "move_feint" key, and the
		// right-stick quick-return gesture the recognizer reports as
		// GestureKind.Feint (its own doc says the caller must map it). A single
		// flick is exactly one GestureKind, so this never double-fires with the
		// crossover branch above. The machine enforces the feint-window guard;
		// false return is silent.
		//
		// Routed through FeintGateResolver (bug fix, /diagnose 2026-07-03): the
		// gesture-sourced Feint is ambiguous — it fires from ANY quick aim-stick
		// flick-and-return, unrelated to which move is running. For Crossover/
		// Hesitation that free abort is harmless, but a JumpShot's feint is a
		// pump-fake that SILENTLY consumes the ball release (Feint() never sets
		// JustEnteredActive), while the windup animation plays regardless
		// (MoveAnimResolver reads Phase alone) — so an incidental stick flick
		// while shooting read as "I pressed shoot and nothing happened." The
		// gate withholds the gesture (but never the explicit key) from an
		// in-progress JumpShot; see FeintGateResolver's doc for the full reasoning.
		bool shouldFeint = FeintGateResolver.ShouldFeint(
			Input.IsActionJustPressed("move_feint"), gesture.Kind, _machine.CurrentMove);
		if (shouldFeint && _machine.Feint() && !isServer)
			RpcId(1, MethodName.RequestFeint);

		_machine.Tick(); // advance one frame — always called, including Inactive (no-op)
	}

	/// <summary>
	/// Applies the committed-move velocity effect for the current phase and calls
	/// MoveAndSlide(). Called instead of Move() while _machine.IsActive.
	///
	/// Startup  — Hybrid gather (#198, ADR-0003 amendment): Velocity BLEEDS
	///            toward zero via GatherDecel, a hard decel that never
	///            instant-zeros. This replaces the pre-#198 "Velocity =
	///            Vector3.Zero every tick" — a real plant sheds speed fast but
	///            a genuine drive can still carry some momentum into Active.
	///            Still clunky/no smoothing by design (ADR-0003).
	/// Active   — Burst velocity SET on JustEnteredActive via CrossoverBurstMath,
	///            composing whatever momentum survived Startup with the
	///            left-stick exit vector snapshotted THIS tick (#198); maintained
	///            through all Active ticks so it spans the full duration.
	///            SET not += : additive velocity would overshoot on reconcile replay.
	///            (Doubt cycle 2, finding #4 — actionable: sustain burst.)
	/// Recovery — Decelerate toward zero: the punish window (ADR-0003). Player
	///            cannot re-input; wrong reads are paid for here.
	///
	/// Note: MoveAndSlide() may clip Velocity on wall contact (finding #9, valid
	/// trade-off). Recovery decelerates from whatever clipped value results —
	/// acceptable for M3.
	/// </summary>
	/// <param name="exitVectorSample">
	/// The left-stick reading to use IF this tick is a Crossover's
	/// JustEnteredActive tick (ignored otherwise). Passed in by the caller
	/// because which source is correct depends on ROLE — host's own/client's
	/// own predicted read real local hardware (ReadInput()); the server's copy
	/// of a remote player reads the networked _pendingRawStick. See
	/// _pendingRawStick's doc for why this is a distinct channel from the
	/// regular movement input.
	/// </param>
	private void TickCommittedMoveBehavior(double delta, Vector2 exitVectorSample)
	{
		switch (_machine.Phase)
		{
			case MovePhase.Startup:
				// #198's hybrid gather is scoped to the CROSSOVER's plant only
				// (ADR-0003 amendment) — every other committed move (Hesitation,
				// StealMove, JumpShot, …) keeps the original instant-zero plant.
				// A blanket bleed here would have silently let a driving player
				// slide through a Hesitation's "stand still and sell the fake"
				// or a defender's StealMove Active window on residual momentum
				// GatherDecel's budget doesn't fully cover for their (shorter)
				// Startup lengths — exactly the un-bounded relaxation the ADR
				// amendment explicitly rules out.
				Velocity = _machine.CurrentMove is Crossover
					? Velocity.MoveToward(Vector3.Zero, GatherDecel * (float)delta)
					: Vector3.Zero;
				MoveAndSlide();
				break;

			case MovePhase.Active:
				// Set the burst velocity on the first Active tick; on subsequent
				// Active ticks the same velocity is maintained (no else-zero here).
				// MoveAndSlide() applies it each tick, producing sustained separation.
				// A JumpShot (M7b, #74) has no horizontal effect here — Velocity is
				// already bled toward zero by Startup's gather above and nothing
				// sets it for a non-Crossover move, so the shooter stays
				// (near-)planted through the release. BallController reads
				// JustReleasedJumpShot to fire the actual ball transition on this
				// same tick — that is a SEPARATE node's read of this machine's
				// state, not a side effect this switch needs to produce.
				if (_machine.JustEnteredActive && _machine.CurrentMove is Crossover crossover)
				{
					// #198: CrossoverBurstMath composes the surviving Startup
					// momentum with the exit-vector-driven burst impulse — see
					// its doc for the full emergent-move table. Heading is
					// reconciled, so server and client derive the identical
					// world-space forward/right axes from it.
					int sign = System.Math.Sign(crossover.BurstDirection);
					Velocity = CrossoverBurstMath.ComposeActiveVelocity(
						Velocity, Heading, sign, exitVectorSample,
						BurstSpeed, ForwardBurstScale, ExitDeadzone);

					// The crossover swaps the ball to the (now near) empty hand —
					// the authoritative hand-state change (M9, #83). It rides this
					// same predicted + reconciled Active-entry event as the burst;
					// a Hesitation is not a Crossover, so it never reaches here and
					// the hand stays put.
					HandSide = HandStateResolver.Opposite(HandSide);
				}
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
		move is Crossover crossover ? crossover.BurstDirection :
		move is StealMove steal    ? (float)(int)steal.TargetHand :
		0f;

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
		// Advance the authoritative heading — and the in-place-pivot latch it
		// now carries (issue #172) — toward inputDir at a bounded non-linear
		// rate BEFORE velocity, because velocity below depends on whether this
		// step says the player is currently planted. HeadingMath.Step is pure,
		// so this introduces no role-checks or network calls in violation of
		// the "keep Move() pure" contract, and — placed here inside the shared
		// motion step — it replays identically on the server, the client
		// prediction, and the reconciliation replay loop without any extra
		// code paths.
		HeadingMath.HeadingStep step = HeadingMath.Step(
			Heading, _pivot, inputDir, delta, MaxTurnRateDeg, BackTurnSlowFactor, PivotThresholdDeg);
		Heading = step.NewYaw;
		_pivot  = step.Pivot;

		if (step.IsPivotingInPlace)
		{
			// Feet planted mid-pivot: zero displacement is the acceptance
			// criterion (issue #172) — a committed plant-and-turn must cost
			// real position, not just a facing delay. MovementMath.ComputeVelocity
			// is intentionally NOT called on this branch (it must not change).
			// Zeroing the whole Vector3 (not just X/Z) is safe here: this
			// controller applies no gravity and never sets Velocity.Y anywhere
			// (a top-down half-court capsule, not a jumping character).
			Velocity = Vector3.Zero;
		}
		else
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
		}

		MoveAndSlide();
	}
}
