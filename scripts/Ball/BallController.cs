using System.Collections.Generic;
using Godot;
using Hooper.Systems;

namespace Hooper.Ball;

/// <summary>
/// The ball node — the ONLY part of the ball that touches Godot. It drives the
/// pure, deterministic mini-physics each physics tick and moves this node's
/// transform to match. All the actual math lives in unit-tested pure classes
/// (per ADR-0004); this node is the thin glue between them and the scene tree.
///
/// ── Why the node holds no math ────────────────────────────────────────────
/// ADR-0004 requires every ball moment to be bit-identical on server and
/// clients and unit-testable without a running engine. A Node3D can't be
/// instantiated headlessly, so the logic is delegated:
///   BallStateMachine — which moment we're in (Held/Dribbling/InFlight/Loose)
///   DribbleCycle     — ball position while dribbling
///   ShotArc          — parabolic flight while InFlight
///   RimBackboard     — rim/backboard contact + clean-make detection
/// This class only sequences those per tick and copies the result into
/// GlobalPosition. Keep it that way: new ball behaviour goes in a pure class.
///
/// ── M4 scope: networking the ball (ADR-0002, issue #20) ───────────────────
/// Unlike PlayerController, the Ball is a SINGLE shared node identical in
/// every peer's copy of Main.tscn — there is no "remote copy of someone
/// else's object" the way other players' capsules are. The role split is
/// keyed on "is this machine the server" and "is the local peer the current
/// holder", not on node identity:
///
///   EVERY peer predicts the ball locally, every tick, via the same Tick*
///   methods used here for M2 (TickHeld/TickDribbling/TickInFlight/TickLoose).
///   The ball's motion is a pure function of elapsed time and fixed inputs
///   (dribble phase, arc integration, rim/backboard math) — not of per-peer
///   hardware — so there is nothing to "interpolate" the way a remote
///   player's capsule needs lerping. The one exception is the shoot trigger,
///   which has real input-authority rules (see TryShoot below).
///
///   The SERVER additionally broadcasts an authoritative snapshot
///   (state, position, velocity, holder) every tick by calling the
///   ReceiveState RPC on all peers. Every other peer reconciles to that
///   snapshot: discrete state is forced
///   to match (no partial-correction is meaningful for an enum); continuous
///   position uses the same mesh-offset smooth-correction trick
///   PlayerController uses for capsules, so a divergence drifts into place
///   rather than snapping.
///
/// ── Holder resolution ──────────────────────────────────────────────────
/// The ball has no fixed holder node — possession changes at runtime as
/// StateMachine.HolderPeerId changes. Players are an editor-wired Players export
/// (the same spawn-root NodePath pattern NetworkManager uses) and the actual
/// holder node is looked up by peer ID string each tick via GetNodeOrNull.
/// This replaces the old static Holder export, which pointed at the spawn
/// root itself (Main.tscn wired it to "../Players", not an actual player
/// node — there was never a static node to drag in M2 because Players is
/// populated by runtime spawning, not authored in the editor).
/// </summary>
public partial class BallController : Node3D
{
	// ── Holder / aim wiring (set in the editor) ───────────────────────────

	/// <summary>
	/// Spawn root whose children are player nodes, named by peer ID — the
	/// same identity contract NetworkManager.Players relies on. The ball
	/// resolves its current holder node from here each tick via
	/// StateMachine.HolderPeerId, because possession changes at runtime and
	/// there is no single static node to wire in the editor.
	/// </summary>
	[Export] public Node Players { get; set; }

	// ── Dribble tunables ──────────────────────────────────────────────────

	/// <summary>Hand height the dribble bounces up to (metres).</summary>
	[Export] public float DribbleHandHeight { get; set; } = 1.0f;

	/// <summary>How far (metres) in front of the holder the ball is positioned while Held or Dribbling.</summary>
	[Export] public float DribbleForwardOffset { get; set; } = 0.5f;

	/// <summary>Full down-and-up dribble cycle duration (seconds).</summary>
	[Export] public float DribblePeriod { get; set; } = 0.6f;

	// ── Shot tunables ─────────────────────────────────────────────────────

	/// <summary>Peak world-space Y the shot arc reaches (metres).</summary>
	[Export] public float ShotApexHeight { get; set; } = 4.0f;

	/// <summary>Downward acceleration applied to the shot + loose ball (m/s²).</summary>
	[Export] public float Gravity { get; set; } = 9.8f;

	// ── Basket geometry (must match the hoop node's placement) ────────────

	/// <summary>World-space centre of the rim ring. Used for collision geometry only.</summary>
	[Export] public Vector3 RimCenter { get; set; } = new(0f, 3.05f, 0f);

	/// <summary>
	/// World-space point the shot arc aims for. Defaults to RimCenter (a clean make).
	/// Offset this independently to aim the shot off-centre and test rim bounces.
	/// In M3+, the game will compute this from shot quality/timing; for M2 it is
	/// an editor export so both outcomes can be verified manually.
	/// </summary>
	[Export] public Vector3 ShotTarget { get; set; } = new(0f, 3.05f, 0f);

	/// <summary>Rim ring radius (metres). Regulation ≈ 0.23.</summary>
	[Export] public float RimRadius { get; set; } = 0.23f;

	/// <summary>Ball radius (metres). Regulation ≈ 0.12. Also the rest height on the floor.</summary>
	[Export] public float BallRadius { get; set; } = 0.12f;

	/// <summary>Restitution for rim contact [0..1].</summary>
	[Export] public float RimRestitution { get; set; } = 0.65f;

	/// <summary>World-space centre of the backboard face.</summary>
	[Export] public Vector3 BoardCenter { get; set; } = new(0f, 3.5f, 0.3f);

	/// <summary>Backboard outward normal, pointing toward the court (unit).</summary>
	[Export] public Vector3 BoardNormal { get; set; } = new(0f, 0f, -1f);

	/// <summary>Half-width of the backboard rectangle (metres).</summary>
	[Export] public float BoardHalfWidth { get; set; } = 0.46f;

	/// <summary>Half-height of the backboard rectangle (metres).</summary>
	[Export] public float BoardHalfHeight { get; set; } = 0.30f;

	/// <summary>Restitution for backboard contact [0..1].</summary>
	[Export] public float BoardRestitution { get; set; } = 0.65f;

	/// <summary>
	/// Input action that fires a shot while Held / Dribbling. The human adds
	/// this action in Project Settings → Input Map (EDITOR_TASKS).
	/// </summary>
	[Export] public string ShootAction { get; set; } = "ball_shoot";

	// ── Possession tunables (M6b, ADR-0008) ───────────────────────────────

	/// <summary>
	/// Floor-plane (XZ) distance (metres) within which a player can recover a
	/// loose ball (issue #48). Editor-tunable balance surface, not an
	/// architectural constant — see ADR-0008. The Y component of both ball and
	/// player positions is ignored (the fixed height gap between a resting ball
	/// and a capsule centre would otherwise shrink the real reach silently).
	/// When both players are in reach the nearer wins (ReboundContest).
	/// </summary>
	[Export] public float PickupRadius { get; set; } = 1.0f;

	/// <summary>
	/// Floor-plane distance (metres) from the hoop the handler must reach to
	/// clear a possession — the take-it-back line near the top of the key
	/// (issue #50, ADR-0008). Editor-tunable balance surface. Defaults to ~the
	/// NBA top-of-key radius (5.8 m).
	/// </summary>
	[Export] public float ClearLineDistance { get; set; } = 5.8f;

	// ── Court bounds (issue #46, half-court containment) ────────────────────

	/// <summary>
	/// Floor-plane (XZ) lower bound of the playable court rectangle, in world
	/// space: X = left edge, Y = near edge (smallest Z). Used to clamp a loose
	/// ball in TickLoose (CourtBounds.Clamp) so it cannot roll off the floor
	/// edge. Must match the StaticBody3D walls placed in the editor for players
	/// (EDITOR_TASKS.md — court-bound step, issue #46). Inset from the raw
	/// floor geometry by ~BallRadius to prevent the ball from resting half-
	/// outside the floor mesh.
	/// </summary>
	[Export] public Vector2 CourtMin { get; set; } = new(-4.88f, -1.0f);

	/// <summary>
	/// Floor-plane (XZ) upper bound of the playable court rectangle: X = right
	/// edge, Y = far edge (largest Z). See CourtMin for layout notes.
	/// </summary>
	[Export] public Vector2 CourtMax { get; set; } = new(4.88f, 11.88f);

	// ── Reconciliation tuning (mirrors PlayerController's tunables) ───────

	/// <summary>
	/// Fraction of the visual snap distance corrected per physics frame.
	/// Same role as PlayerController.ReconcileLerpRate.
	/// </summary>
	[Export] public float ReconcileLerpRate { get; set; } = 0.3f;

	/// <summary>
	/// Divergence smaller than this (metres) is accepted silently.
	/// Same role as PlayerController.ReconcileSnapThreshold.
	/// </summary>
	[Export] public float ReconcileSnapThreshold { get; set; } = 0.001f;

	// ── Composed pure logic ───────────────────────────────────────────────

	/// <summary>The state machine that tracks which ball moment we're in.</summary>
	public BallStateMachine StateMachine { get; private set; }

	/// <summary>Convenience accessor for the current state.</summary>
	public BallState State => StateMachine.Current;

	/// <summary>
	/// Whether the current possession has been "cleared" — the handler has
	/// carried the ball back behind the clear line, so a basket may now count
	/// (take-it-back rule, ADR-0008, issue #50).
	///
	/// Server-authoritative and NEVER predicted, exactly the discrete-forced
	/// treatment GameManager documents for score: only the server flips it
	/// (false on every change of possession, true once the holder crosses the
	/// clear line — see UpdateClearStatus / AwardPossession), and clients take
	/// the value verbatim from the ReceiveState broadcast in
	/// ReconcileFromServer. It lives here, with the holder it belongs to, rather
	/// than in GameManager, because "cleared" is a property of THIS possession:
	/// a possession change resets it, and the only check that sets it is pure
	/// geometry on the holder position this node already computes. Held next to
	/// HolderPeerId so the two travel together in one broadcast and the HUD
	/// (#51) reads possession from a single source.
	/// </summary>
	public bool IsCleared { get; private set; }

	/// <summary>
	/// Emitted on every peer whenever the holder or the cleared flag changes —
	/// the push-driven cue the possession HUD (#51) refreshes on, mirroring how
	/// GameManager.ScoreChanged drives ScoreHud. Fires from the same per-tick
	/// change-check on every peer, so it is correct whether the change came from
	/// local gameplay (server) or from a reconcile (client).
	/// </summary>
	[Signal] public delegate void PossessionChangedEventHandler(int holderPeerId, bool cleared);

	private DribbleCycle _dribble;
	private RimBackboard _basket;

	/// <summary>
	/// Last known horizontal facing direction of the holder. Persisted so the
	/// dribble offset stays stable while the player is stationary. Initialised
	/// to -Z (facing up the court). Reset effectively on each possession change
	/// because the new holder's velocity drives it immediately on the first
	/// moving tick; the server's ReceiveState broadcast corrects any 1-tick
	/// directional divergence in the meantime.
	/// </summary>
	private Vector3 _lastHolderForward = new Vector3(0f, 0f, -1f);

	/// <summary>
	/// The in-flight (or loose) trajectory. Non-null only while InFlight or
	/// Loose — it carries the position+velocity the integrator advances. Reused
	/// for the loose fall so a bounced ball keeps moving under gravity.
	/// </summary>
	private ShotArc _arc;

	// ── Visual-correction mesh reference ───────────────────────────────────

	/// <summary>
	/// The MeshInstance3D child whose local Position is offset during smooth
	/// correction — same trick PlayerController uses. This node (the root
	/// Node3D) snaps immediately to the authoritative position on reconcile
	/// so any future position-based logic never reads a stale value; only the
	/// mesh drifts visually.
	/// </summary>
	private Node3D _mesh;

	/// <summary>
	/// Visual-only offset applied to the MeshInstance3D child. SET to the
	/// divergence when reconciliation finds a mismatch; lerped to zero each
	/// frame. SET, not accumulated — mirrors PlayerController._smoothOffset.
	/// </summary>
	private Vector3 _smoothOffset;

	// ── Made-shot green flash (issue #46) ─────────────────────────────────

	/// <summary>
	/// Duration (seconds) the ball stays green after a counting basket.
	/// Editor-tunable via the export below; 1 s is the default.
	/// </summary>
	[Export] public float MadeFlashDuration { get; set; } = 1.0f;

	/// <summary>
	/// Private per-instance material override for the ball mesh — duplicated
	/// from the scene's shared sub-resource in _Ready so we can tint it without
	/// affecting other instances (or the asset on disk). Null if the mesh node
	/// was not found.
	/// </summary>
	private StandardMaterial3D _ballMaterial;

	/// <summary>Original orange albedo, cached at startup for restoration after the flash.</summary>
	private Color _originalAlbedo;

	/// <summary>Countdown to the end of the current flash; &lt;= 0 means no active flash.</summary>
	private float _flashTimer;

	// ── Authoritative snapshot staging (client + server's own broadcast) ──

	/// <summary>Latest broadcast received from the server, staged for reconcile.</summary>
	private BallState _serverState;
	private Vector3 _serverPos;
	private Vector3 _serverVel;
	private int _serverHolderPeerId;
	private bool _serverCleared;

	/// <summary>True once ReceiveState has arrived since the last _PhysicsProcess.</summary>
	private bool _hasNewState;

	// ── Scoring (#24/#25) ───────────────────────────────────────────────────

	/// <summary>
	/// The peer id holding the ball at the moment Shoot() is called, captured
	/// BEFORE StateMachine.Shoot() clears HolderPeerId to 0. Needed because by
	/// the time TickInFlight's contact resolution detects a Make, HolderPeerId
	/// is already 0 (see class doc's "Holder resolution") — there is no other
	/// record of who released the shot. Half-court 1v1 means shooter == scorer
	/// (both players shoot the same hoop), so no separate "which hoop" logic
	/// is needed here.
	/// </summary>
	private int _lastShooterPeerId;

	/// <summary>
	/// Cached GameManager reference, looked up via the "game_manager" group
	/// (see GameManager's class doc "Discovery"). Null-guarded loudly in
	/// _Ready, with a lazy re-lookup fallback in case this node's _Ready runs
	/// before GameManager's (Main.tscn authors GameManager first, so this
	/// should not normally happen, but player/ball nodes can spawn at
	/// runtime via MultiplayerSpawner while GameManager is static scene
	/// content — defensive, not load-bearing).
	/// </summary>
	private GameManager _gameManager;

	/// <summary>
	/// Resolves _gameManager, re-querying the group if the cached reference
	/// is still null (see field doc). Returns null (with a loud PrintErr,
	/// already emitted once in _Ready) if GameManager truly isn't in the
	/// scene — callers must null-check.
	/// </summary>
	private GameManager GetGameManager()
	{
		if (_gameManager == null)
			_gameManager = GetTree().GetFirstNodeInGroup("game_manager") as GameManager;
		return _gameManager;
	}

	// ── Role helpers ──────────────────────────────────────────────────────

	private bool IsServer => Multiplayer.IsServer();

	/// <summary>
	/// True when the LOCAL peer is the ball's current holder. Drives shoot-
	/// input authority (see TryShoot) — only the holder's machine may
	/// legally trigger a shot, mirroring PlayerController's IsLocalPlayer
	/// check but keyed on ball possession rather than node identity (the
	/// Ball has no per-peer node to compare Name against).
	/// </summary>
	private bool IsLocalHolder =>
		Multiplayer.GetUniqueId().ToString() == StateMachine.HolderPeerId.ToString();

	// ── Lifecycle ───────────────────────────────────────────────────────────

	public override void _Ready()
	{
		// Discoverable by the possession HUD via group lookup (#51), the same
		// pattern ScoreHud uses to find GameManager — there is exactly one ball.
		AddToGroup("ball");

		_dribble = new DribbleCycle(DribbleHandHeight, DribblePeriod);
		_basket  = new RimBackboard(
			RimCenter, RimRadius, BallRadius, RimRestitution,
			BoardCenter, BoardNormal, BoardHalfWidth, BoardHalfHeight, BoardRestitution);

		// Start with NO holder (peer 0). The server self-assigns the tipoff to the
		// first player node that exists (see TryAssignTipoffHolder), which is
		// correct for BOTH topologies (ADR-0007): on a listen-server the first
		// present node is the host (peer 1) — identical to the old hardcoded
		// default — and on a headless dedicated server, where peer 1 has no player
		// node, it is the first connecting client. Hardcoding peer 1 here parked
		// the ball at the world origin forever on a dedicated host.
		//
		// StartDribble still runs so the ball begins Dribbling; with holder 0 it
		// tracks the world origin until the server assigns a holder (a <=1-tick
		// window once a player exists). This is the SAME pre-holder behaviour the
		// listen-server already had between scene load and the Host button press.
		StateMachine = new BallStateMachine(initialHolderPeerId: 0);
		StateMachine.StartDribble();

		_mesh = GetNodeOrNull<Node3D>("MeshInstance3D");
		if (_mesh == null)
			GD.PrintErr("[BallController] MeshInstance3D child not found; smooth correction disabled.");

		// Set up the made-shot green flash (issue #46).  Duplicate the sphere's
		// shared material into a per-instance override so tinting does not affect
		// other instances or the sub-resource on disk.
		if (_mesh is MeshInstance3D meshInst)
		{
			// The mesh's embedded material is on the SphereMesh, not the instance
			// (Ball.tscn wires it via SphereMesh.material). GetActiveMaterial(0)
			// resolves the mesh's own material when no override exists yet, giving
			// us the orange color to duplicate and cache.
			var baseMat = meshInst.GetActiveMaterial(0) as StandardMaterial3D;
			if (baseMat != null)
			{
				_ballMaterial  = (StandardMaterial3D)baseMat.Duplicate();
				_originalAlbedo = _ballMaterial.AlbedoColor;
				meshInst.SetSurfaceOverrideMaterial(0, _ballMaterial);
			}
			else
			{
				GD.PrintErr("[BallController] Ball mesh has no StandardMaterial3D at surface 0; made-shot flash disabled.");
			}
		}

		// (Doubt cycle 1, finding #6/#9) Players is unassigned → HolderPosition()
		// would silently fall back to world origin every tick with no diagnostic,
		// which is exactly the kind of failure CLAUDE.md asks us to surface loudly
		// rather than let a non-game-dev human chase a silently teleporting ball.
		// Must be wired to the SAME spawn-root node as NetworkManager.Players —
		// the peer-ID-as-name identity contract only holds if both point at it.
		if (Players == null)
			GD.PrintErr("[BallController] Players is not assigned. Wire it in the Inspector to the same spawn root as NetworkManager.Players.");

		// CourtMin/Max must be correctly ordered (Min.X < Max.X, Min.Y < Max.Y) for
		// CourtBounds.Clamp to behave correctly.  Mathf.Clamp throws when min > max
		// in debug builds, so catch the misconfiguration loud and early (issue #46).
		if (CourtMin.X >= CourtMax.X || CourtMin.Y >= CourtMax.Y)
			GD.PrintErr($"[BallController] CourtMin ({CourtMin}) must be strictly less than CourtMax ({CourtMax}) on each axis. Court bounds will not work until corrected in the Inspector.");

		// Deferred so GameManager._Ready() (a later sibling) has run and joined
		// the group before we check — avoids a false-positive error on scene load.
		Callable.From(() =>
		{
			_gameManager = GetTree().GetFirstNodeInGroup("game_manager") as GameManager;
			if (_gameManager == null)
			{
				GD.PrintErr("[BallController] No node in group 'game_manager' found. Scoring and game-over freeze will not work until GameManager is added to the scene (issue #27).");
				return;
			}

			// Trigger the made-shot green flash on every counting basket (issue #46).
			// ScoreChanged fires on EVERY peer (server via BroadcastAndEmit; clients
			// via ReceiveScoreState RPC), so the flash is truthful everywhere: it only
			// lights up when the server actually registered a point — never on an
			// uncleared make that turned over.
			if (_ballMaterial != null)
				_gameManager.ScoreChanged += OnScoreChanged;
		}).CallDeferred();
	}

	public override void _ExitTree()
	{
		// Drop the delegate so GameManager doesn't hold a dangling reference.
		// Same lifecycle hygiene as PossessionHud._ExitTree.
		if (_gameManager != null && _ballMaterial != null)
			_gameManager.ScoreChanged -= OnScoreChanged;
	}

	// ── Tick loop ─────────────────────────────────────────────────────────

	public override void _PhysicsProcess(double delta)
	{
		// (#25 doubt cycle 2, finding #1 — fixed from an earlier draft that
		// early-returned the ENTIRE ball tick, including the broadcast, the
		// instant GameManager.IsGameOver went true) An early "freeze the
		// ball" guard here looked symmetric with PlayerController's freeze,
		// but is actually wrong for the Ball specifically: RegisterBasket
		// (called from TickInFlight below) sets IsGameOver=true SYNCHRONOUSLY
		// on the SAME tick the winning Make happens, and GoLoose() only
		// transitions the ball OUT of InFlight into Loose — it does not
		// finish the fall. TickLoose needs several MORE ticks to integrate
		// gravity down to the floor (see TickLoose). A guard here would have
		// suppressed every one of those remaining ticks' Rpc(ReceiveState,...)
		// broadcasts (IsGameOver is already true by the next tick), freezing
		// every client's view of the ball mid-air, never settling, on every
		// game-winning shot. There is no good place to insert "but let it
		// finish falling first" without re-deriving exactly what TickLoose
		// already computes, so instead: the ball is simply never frozen by
		// game-over. This is safe, not just convenient — TickHeld/
		// TickDribbling only re-centre the ball on whatever the holder's
		// (now player-frozen, see PlayerController) position already is, and
		// Loose settles to the floor and then reports zero velocity forever
		// (see TickLoose) — none of these states do anything surprising once
		// both players have stopped moving. Freezing the PLAYERS is what
		// actually stops the match from progressing; the ball coasting to
		// its natural rest (or idly re-centring on a stationary holder) is
		// inert, not a bug.
		//
		// Reconcile against the freshest server snapshot BEFORE predicting
		// this tick, same ordering rationale as PlayerController.TickClientOwnPlayer:
		// the correction baseline should be the latest authoritative data we have.
		//
		// (Doubt cycle 1, finding #5) The server itself never reaches this
		// branch: ReceiveState has CallLocal = false, so _hasNewState stays
		// false on the server. The server's StateMachine IS the ground truth
		// it just broadcast — there is nothing for it to reconcile against,
		// unlike every other peer. This mirrors PlayerController exactly (the
		// host's own player never reconciles against its own broadcast either).
		if (_hasNewState)
		{
			ReconcileFromServer();
			_hasNewState = false;
		}

		// Fixed timestep — NOT the variable wall-clock delta — so the arc is
		// deterministic and reproducible identically on server and every client.
		float dt = 1.0f / Engine.PhysicsTicksPerSecond;

		switch (State)
		{
			case BallState.Held:      TickHeld();        break;
			case BallState.Dribbling: TickDribbling(dt); break;
			case BallState.InFlight:  TickInFlight(dt);  break;
			case BallState.Loose:     TickLoose(dt);     break;
		}

		// Server-only: assign the tipoff holder once a player node exists but the
		// ball has none yet (ADR-0007). The broadcast below propagates the holder
		// to every client; clients never compute it themselves.
		if (IsServer
			&& StateMachine.HolderPeerId == 0
			&& (State == BallState.Held || State == BallState.Dribbling))
		{
			TryAssignTipoffHolder();
		}

		// Server-only: clear the possession once the handler carries the ball
		// back behind the clear line (#50). Server-authoritative; clients
		// receive the flag in the broadcast below, never compute it.
		if (IsServer)
			UpdateClearStatus();

		// Only the server broadcasts authoritative truth. Every peer (server
		// included) already predicted its own copy above; the server's
		// broadcast is what every OTHER peer reconciles against. IsCleared rides
		// the same per-tick snapshot as the holder it belongs to — continuously
		// resent, so a dropped packet self-heals on the next tick.
		if (IsServer)
		{
			Rpc(MethodName.ReceiveState,
				(int)StateMachine.Current, GlobalPosition, CurrentVelocity(), StateMachine.HolderPeerId, IsCleared);
		}

		ApplySmoothCorrection();

		// Push the possession HUD only when something actually changed (#51) —
		// one refresh per possession/clear event, not 60 per second. Runs on
		// every peer after reconcile, so a client emits when the broadcast moves
		// the holder/cleared, and the server when gameplay does.
		EmitPossessionIfChanged();

		// Tick the made-shot green flash countdown; restore orange when it expires.
		TickMadeFlash((float)delta);
	}

	// ── Possession HUD push (#51) ──────────────────────────────────────────

	/// <summary>Last (holder, cleared) pair emitted; -1 holder means "nothing emitted yet" (0 is a valid no-holder).</summary>
	private int _lastEmittedHolder = -1;
	private bool _lastEmittedCleared;

	/// <summary>Emits PossessionChanged when the holder or cleared flag differs from the last emit.</summary>
	private void EmitPossessionIfChanged()
	{
		if (StateMachine.HolderPeerId == _lastEmittedHolder && IsCleared == _lastEmittedCleared)
			return;

		_lastEmittedHolder = StateMachine.HolderPeerId;
		_lastEmittedCleared = IsCleared;
		EmitSignal(SignalName.PossessionChanged, _lastEmittedHolder, _lastEmittedCleared);
	}

	// ── Made-shot green flash (issue #46) ─────────────────────────────────

	/// <summary>
	/// Called on every peer when the score changes (GameManager.ScoreChanged)
	/// — i.e. when a counting basket is registered. Starts the green flash.
	/// Fires only for genuine points (cleared makes), never for uncleared makes
	/// that turn over — ScoreChanged is the authority boundary.
	/// </summary>
	private void OnScoreChanged()
	{
		if (_ballMaterial == null) return;
		_flashTimer = MadeFlashDuration;
		_ballMaterial.AlbedoColor = Colors.Green;
	}

	/// <summary>
	/// Ticks the flash countdown and restores the original orange albedo when
	/// the timer expires.  No-op when no flash is active (_flashTimer &lt;= 0).
	/// </summary>
	private void TickMadeFlash(float delta)
	{
		if (_flashTimer <= 0f || _ballMaterial == null) return;

		_flashTimer -= delta;
		if (_flashTimer <= 0f)
		{
			_flashTimer = 0f;
			_ballMaterial.AlbedoColor = _originalAlbedo;
		}
	}

	/// <summary>
	/// Velocity to report in the broadcast. Held/Dribbling have no ShotArc
	/// (_arc is only constructed on Shoot), so report Zero rather than a
	/// stale value from a previous flight.
	/// </summary>
	private Vector3 CurrentVelocity() => _arc?.Velocity ?? Vector3.Zero;

	/// <summary>
	/// Server-only tipoff assignment (ADR-0007): give the ball to the first
	/// present player node. Called each tick while the ball is still awaiting
	/// its first holder (Held/Dribbling with HolderPeerId == 0) — a state that
	/// uniquely identifies the pre-tipoff window, since Catch() always sets a
	/// holder and a post-shot holder-0 is InFlight/Loose (excluded by the
	/// caller's guard), so this never steals possession mid-game.
	///
	/// ForceState is the right tool: assigning the tipoff is an authoritative
	/// server decision, the same category as reconciliation — a direct snap of
	/// holder identity, keeping the current ball state, not a gameplay edge to
	/// validate. The player nodes are named by peer ID (the identity contract
	/// NetworkManager.SpawnPlayer establishes), so the child's Name parses to
	/// the holder peer ID. Peer 0 is impossible as a real peer, so the int != 0
	/// guard is belt-and-suspenders against an unexpectedly-named node.
	/// </summary>
	private void TryAssignTipoffHolder()
	{
		if (Players == null) return;

		foreach (Node child in Players.GetChildren())
		{
			if (int.TryParse(child.Name, out int peerId) && peerId != 0)
			{
				StateMachine.ForceState(State, peerId);

				// The opening possession is pre-cleared (ADR-0008): the
				// take-it-back rule applies "on every change of possession and
				// after every made basket" — the tipoff is neither, so the
				// first basket must be allowed to count without a take-back.
				IsCleared = true;

				GD.Print("[BallController] Tipoff holder assigned to peer ", peerId);
				return;
			}
		}
	}

	/// <summary>Resolves the current holder's world position, or the origin if none.</summary>
	private Vector3 HolderPosition()
	{
		Node holder = Players?.GetNodeOrNull(StateMachine.HolderPeerId.ToString());
		return (holder as Node3D)?.GlobalPosition ?? Vector3.Zero;
	}

	/// <summary>
	/// Returns the holder's current horizontal facing direction as a normalised XZ vector.
	/// Falls back to <see cref="_lastHolderForward"/> when the holder is absent or stationary
	/// (speed below 0.1 m/s — just above the deceleration floor so the direction locks in
	/// before the player fully stops rather than flickering at the last frame of movement).
	/// Writes <see cref="_lastHolderForward"/> as a side-effect; the server broadcasts
	/// position every tick so any client/server divergence in this field is absorbed by
	/// <see cref="ReconcileFromServer"/> within one tick.
	/// </summary>
	private Vector3 ComputeHolderForward(CharacterBody3D body)
	{
		if (body == null) return _lastHolderForward;
		Vector3 vel = body.Velocity;
		float horizontalSpeed = new Vector2(vel.X, vel.Z).Length();
		if (horizontalSpeed < 0.1f) return _lastHolderForward;
		_lastHolderForward = new Vector3(vel.X, 0f, vel.Z).Normalized();
		return _lastHolderForward;
	}

	/// <summary>
	/// Server-only: flip the current possession to cleared once the handler has
	/// carried the ball back behind the clear line (#50, ADR-0008). Only checked
	/// while a player actually holds the ball (Held/Dribbling) and only until it
	/// flips — once cleared, the possession stays cleared until the next change
	/// of possession resets it (AwardPossession). One-way within a possession, so
	/// stepping back inside the line after clearing does not un-clear it.
	/// </summary>
	private void UpdateClearStatus()
	{
		if (IsCleared) return;
		if (State != BallState.Held && State != BallState.Dribbling) return;
		if (StateMachine.HolderPeerId == 0) return; // no holder to measure (pre-tipoff)

		if (ClearLine.IsBehindClearLine(HolderPosition(), RimCenter, ClearLineDistance))
			IsCleared = true;
	}

	// ── Per-state behaviour ───────────────────────────────────────────────

	/// <summary>Ball cradled at hand height in front of the holder. Shoot to release.</summary>
	private void TickHeld()
	{
		var holderBody = Players?.GetNodeOrNull(StateMachine.HolderPeerId.ToString()) as CharacterBody3D;
		Vector3 holderPos = holderBody?.GlobalPosition ?? Vector3.Zero;
		Vector3 forward   = ComputeHolderForward(holderBody);
		// World-space Y = DribbleHandHeight, consistent with DribbleCycle's world-Y convention.
		GlobalPosition = new Vector3(
			holderPos.X + forward.X * DribbleForwardOffset,
			DribbleHandHeight,
			holderPos.Z + forward.Z * DribbleForwardOffset
		);
		TryShoot();
	}

	/// <summary>Ball bouncing in front of the holder. Shoot to release.</summary>
	private void TickDribbling(float dt)
	{
		_dribble.Advance(dt);
		var holderBody = Players?.GetNodeOrNull(StateMachine.HolderPeerId.ToString()) as CharacterBody3D;
		Vector3 holderPos = holderBody?.GlobalPosition ?? Vector3.Zero;
		Vector3 forward   = ComputeHolderForward(holderBody);
		// Pass XZ-offset position; GetBallPosition discards the Y and uses HeightAtPhase instead.
		GlobalPosition = _dribble.GetBallPosition(
			new Vector3(holderPos.X + forward.X * DribbleForwardOffset, holderPos.Y, holderPos.Z + forward.Z * DribbleForwardOffset)
		);
		TryShoot();
	}

	/// <summary>
	/// Ball in flight: advance the arc, resolve against the basket, and move
	/// this node. A rim/backboard contact knocks the ball Loose; a clean make
	/// passes through (scoring is wired in M5).
	/// </summary>
	private void TickInFlight(float dt)
	{
		_arc.Step(dt);
		ContactResult contact = _basket.Resolve(_arc); // mutates _arc on a bounce
		GlobalPosition = _arc.Position;

		switch (contact)
		{
			case ContactResult.Bounce:
				StateMachine.GoLoose();
				break;
			case ContactResult.Make:
				// (#24 doubt cycle 1, finding #1) M2 left the ball in
				// InFlight after a Make, with no state transition. Since
				// this method runs every physics tick while InFlight,
				// RimBackboard.Resolve would keep reporting Make on EVERY
				// subsequent tick the ball remains near the rim (the ball
				// doesn't move away on its own) — at 60 Hz that's ~60
				// "scores" per second for a single shot. GoLoose() fixes
				// this the same way a Bounce already does: it transitions
				// the state machine OUT of InFlight, so this case can
				// never fire again for the same shot (Resolve is only
				// called while State == InFlight). GoLoose runs on EVERY
				// peer identically (it's pure ball-physics prediction,
				// exactly like the Bounce branch above) — only the
				// RegisterBasket call below is server-gated.
				StateMachine.GoLoose();

				// (#24 doubt cycle 1, finding #2 — THE crux) Only the
				// SERVER may turn this prediction into a real point. Every
				// peer (including the server) reaches this line on the same
				// tick because Make detection is itself predicted
				// identically everywhere (RimBackboard.Resolve is a pure
				// function of _arc, which every peer integrates identically)
				// — but RegisterBasket no-ops on a client
				// (GameManager.RegisterBasket guards on !IsServer), so a
				// client never mutates score, only ever displays the
				// server's later broadcast. See GameManager's class doc for
				// why score has no reconciliation channel the way position
				// does.
				//
				// (#24 doubt cycle 1, finding #3) Doubt-checked the
				// asymmetric case: can a CLIENT predict a Make the SERVER
				// later rules a Bounce/miss (or vice versa)? The arc and rim
				// geometry are deterministic pure functions (ADR-0004) fed
				// the same RimCenter/RimRadius/BallRadius exports and the
				// same _arc state — kept in sync every tick via
				// ReceiveState/ReconcileFromServer — so the two sides agree
				// tick-for-tick in the common case. If they ever diverged
				// (e.g. a missed reconcile pass left a client's _arc stale),
				// the client's local GoLoose() already ran and its
				// RegisterBasket call already no-op'd — there is nothing to
				// undo, because the client never had authority to register
				// anything. The next ReceiveState broadcast force-corrects
				// the client's StateMachine/position exactly like any other
				// misprediction (discrete state is forced, per
				// ReconcileFromServer below). A client that predicts a make
				// the server rims out simply never scores, and its ball
				// state gets force-corrected on the next broadcast — confirmed
				// consistent, no permanent divergence possible.
				// The scoring DECISION is server-only — gated on IsCleared,
				// which is server-authoritative (clients only ever display the
				// broadcast value; see IsCleared's doc). A client reaching this
				// branch already ran GoLoose() above as prediction and reconciles
				// to whatever the server broadcasts next; it never scores or
				// decides a turnover. (This is the same authority boundary the
				// old code relied on via RegisterBasket's internal IsServer
				// guard; making it an explicit server block here just lets the
				// clear-gate and the take-it-back turnover live in one place.)
				if (IsServer)
					ResolveServerMake();
				break;
		}
	}

	/// <summary>
	/// Server-only resolution of a made shot under the take-it-back rule (#50,
	/// ADR-0008). A make counts only if the possession was cleared:
	///   - Cleared: register the point, then — unless it won the game —
	///     hand the ball back to the scorer (make-it-take-it, #49). The new
	///     possession starts CLEARED (ADR-0008 amended 2026-06-21): the scorer
	///     already took the ball back to earn this make; forcing them to do it
	///     again on the very next possession is double-punishment with no
	///     defensive purpose. Rebounds and turnovers still start uncleared.
	///   - Not cleared: the basket does NOT count; the ball turns over to the
	///     defender (a take-it-back violation), who starts uncleared. With
	///     no opponent present (e.g. a solo editor test) the ball is left loose
	///     for the rebound contest to resolve.
	/// </summary>
	private void ResolveServerMake()
	{
		if (IsCleared)
		{
			GetGameManager()?.RegisterBasket(_lastShooterPeerId);

			// Make-it-take-it, unless the basket ended the game — then the
			// game-over freeze stands (see _PhysicsProcess's no-freeze note).
			// cleared: true — the scorer already earned their trip (see doc).
			if (!(GetGameManager()?.IsGameOver ?? false))
				AwardPossession(_lastShooterPeerId, cleared: true);
			return;
		}

		// Uncleared make: no points. Turn the ball over to the defender, who
		// must take it back behind the clear line before THEY can score.
		int defender = OtherPlayerPeerId(_lastShooterPeerId);
		if (defender != 0)
			AwardPossession(defender);
		// else: no opponent present — leave the ball loose (GoLoose already
		// ran) for the rebound contest to award. IsCleared is already false in
		// this branch, so the possession correctly stays uncleared either way.
	}

	/// <summary>
	/// Loose ball: fall under gravity until it settles on the floor, AND each
	/// tick run the live-rebound contest (#48, ADR-0008) so the ball can be
	/// recovered and the possession loop continues — the keystone that turns
	/// the old "one shot per match" ceiling into a real game.
	///
	/// The contest runs on EVERY peer as prediction, exactly like the Make
	/// detection in TickInFlight: the recovery is a deterministic function of
	/// the ball's and players' positions (ReboundContest), so each peer
	/// computes the same winner from the data it has, the recoverer regains
	/// control with zero input lag, and the server's next ReceiveState
	/// broadcast reconciles away any divergence (e.g. a client predicting off a
	/// slightly stale remote-player position). No separate server-gated side
	/// effect is needed the way RegisterBasket is for scoring: the holder change
	/// IS the broadcast state, forced to match on clients via
	/// ReconcileFromServer like any other possession change.
	///
	/// Accepted cost: in the rare genuinely-contested case (both players within
	/// PickupRadius AND their positions desynced enough to flip "who is nearer"),
	/// a client may predict ITSELF recovering when the server awards the
	/// opponent. Its predicted possession — and any shot it fired that frame —
	/// is then reconciled away. That is the inherent price of predicting
	/// possession (issue #48 requires the prediction), bounded to a one-RTT
	/// window on a 50/50 scramble; nothing diverges permanently.
	/// </summary>
	private void TickLoose(float dt)
	{
		_arc.Step(dt);
		Vector3 p = _arc.Position;

		// Floor is the ground plane; the ball centre rests one radius above it.
		if (p.Y <= BallRadius)
		{
			p.Y = BallRadius;
			_arc.Position = p;
			_arc.Velocity = Vector3.Zero; // settled; no bounce model on the floor yet
		}

		// Half-court bound: clamp XZ so the loose ball cannot roll off the floor
		// edge (issue #46).  Y is preserved — the floor-contact check above already
		// owns vertical containment.  Exported CourtMin/Max are the single source of
		// truth for the bounds; the StaticBody3D walls in the editor must match them.
		// Deterministic: same exported values on every peer → no reconciliation drift.
		//
		// Ordering note: this must run AFTER the floor check above (which may have
		// already written _arc.Position via the local `p` copy) so the final
		// _arc.Position reflects both corrections before it is read into GlobalPosition.
		//
		// Velocity zeroing: when the clamp fires on an axis, zero the matching
		// velocity component so the integrator does not keep pushing the ball
		// through the wall every tick (analogous to the floor-contact Velocity=Zero
		// at line 712).
		Vector3 preclamp = _arc.Position;
		_arc.Position = CourtBounds.Clamp(_arc.Position, CourtMin, CourtMax);

		Vector3 arcVel = _arc.Velocity;
		if (_arc.Position.X != preclamp.X) arcVel.X = 0f;
		if (_arc.Position.Z != preclamp.Z) arcVel.Z = 0f;
		_arc.Velocity = arcVel;

		GlobalPosition = _arc.Position;

		int recoverer = ResolveLooseBallRecovery();
		if (recoverer != 0)
			AwardPossession(recoverer);
	}

	/// <summary>
	/// Gathers the loose-ball candidates from the Players spawn root (each
	/// child is named by peer id — the identity contract NetworkManager.
	/// SpawnPlayer establishes) and asks ReboundContest who recovers the ball.
	/// Returns 0 (nobody in reach) when Players is unwired or no player is
	/// within PickupRadius.
	/// </summary>
	private int ResolveLooseBallRecovery()
	{
		if (Players == null) return 0;

		var candidates = new List<ReboundContest.Candidate>();
		foreach (Node child in Players.GetChildren())
		{
			if (child is Node3D player
				&& int.TryParse(child.Name, out int peerId)
				&& peerId != 0)
			{
				candidates.Add(new ReboundContest.Candidate(peerId, player.GlobalPosition));
			}
		}

		return ReboundContest.Resolve(GlobalPosition, candidates, PickupRadius);
	}

	/// <summary>
	/// Returns the peer id of the OTHER player under the Players spawn root —
	/// the one whose node name is not <paramref name="peerId"/> — or 0 if there
	/// is no other player present (e.g. a solo test). Used to award a take-it-back
	/// turnover to the defender (#50). 1v1, so there is at most one other player.
	/// </summary>
	private int OtherPlayerPeerId(int peerId)
	{
		if (Players == null) return 0;

		foreach (Node child in Players.GetChildren())
		{
			if (int.TryParse(child.Name, out int other) && other != 0 && other != peerId)
				return other;
		}

		return 0;
	}

	/// <summary>
	/// Awards possession of a loose / in-flight ball to a player and resumes a
	/// live dribble — the shared handoff path used by a live rebound (#48) and
	/// the make-it-take-it reset (#49). Mirrors the tipoff sequence
	/// (Catch → StartDribble): Catch is legal only from InFlight/Loose (it
	/// returns false otherwise, leaving state untouched), and StartDribble then
	/// puts the new holder into the same dribbling state the game opens in, so
	/// they can immediately dribble and shoot.
	///
	/// <paramref name="cleared"/> controls the clear state of the new possession
	/// (ADR-0008 §Decision-3, amended 2026-06-21):
	///   - false (default): a rebound or turnover — the new holder must carry
	///     the ball back behind the clear line before a basket counts. Set on
	///     EVERY peer (prediction runs on TickLoose), so the HUD shows "take it
	///     back" immediately without waiting for the server broadcast.
	///   - true: a make-it-take-it possession (#49) — the scorer already earned
	///     their trip, so the new possession starts cleared. Server-only call
	///     site (ResolveServerMake), broadcast via ReceiveState.
	///
	/// Only the TRUE clear flip (UpdateClearStatus, crossing the line) is server-
	/// authoritative; clients receive that via the ReceiveState broadcast in
	/// ReconcileFromServer and never compute it themselves.
	/// </summary>
	private void AwardPossession(int peerId, bool cleared = false)
	{
		if (!StateMachine.Catch(peerId))
		{
			// Both callers reach here while the ball is Loose, so Catch (Loose→Held)
			// is legal and this branch is unreachable in normal play.  "Unreachable"
			// holds even for the client prediction path: ReconcileFromServer runs at
			// the TOP of _PhysicsProcess, before the state switch, so TickLoose (and
			// thus this method) only ever dispatches when State == Loose.  Surface
			// loudly per CLAUDE.md loud-failure rule so a future regression is not
			// silent.
			GD.PrintErr($"[BallController] AwardPossession({peerId}) called in state {State}; Catch rejected, possession unchanged.");
			return;
		}

		// StartDribble is legal from Held (the state Catch just transitioned to).
		// Guard the return value so a future internal-state divergence (e.g. a
		// ForceState landing between Catch and StartDribble) fails loudly rather
		// than leaving the ball Held with no dribble cycle.
		if (!StateMachine.StartDribble())
		{
			GD.PrintErr($"[BallController] AwardPossession({peerId}): StartDribble rejected after Catch in state {State}; possession partially awarded, ball may behave unexpectedly.");
		}

		// `cleared` is false for rebounds/turnovers (every peer sets this
		// deterministically; clients predict it immediately for HUD responsiveness),
		// and true for make-it-take-it resets (server-only call site in
		// ResolveServerMake; clients start with false and are corrected by the next
		// ReceiveState broadcast within 1 RTT).  The 1-RTT window where a client
		// briefly shows "take it back" after a counting make is an accepted cosmetic
		// artefact — sub-frame at LAN latency, inherent to client prediction.
		IsCleared = cleared;
	}

	// ── Shot trigger / input authority (M4) ─────────────────────────────────

	/// <summary>
	/// Releases a shot toward the rim if the local shoot input fires this tick
	/// AND the local peer holds shoot authority.
	///
	/// Authority rules (mirrors PlayerController's server-vs-client split,
	/// but keyed on ball possession rather than node identity):
	///   - If this machine is the SERVER and also the current holder: apply
	///     the shot directly. The server IS the input source for its own
	///     player, exactly like TickServerOwnPlayer reads hardware directly —
	///     no RPC needed because there is no authority to defer to.
	///   - If this machine is a CLIENT and the LOCAL peer is the current
	///     holder: predict the shot immediately against the local _arc/
	///     StateMachine copy (zero perceived lag), AND send RequestShoot to
	///     the server so it can apply the authoritative transition. The
	///     server's later broadcast reconciles away any divergence.
	///   - Otherwise (this machine doesn't hold the ball): no local action.
	///     A remote client's shot reaches us only via the server's broadcast.
	///
	/// Shoot() clears HolderPeerId to 0, and no peer's unique ID is ever 0, so
	/// IsLocalHolder is false after a shot until possession is re-awarded. As of
	/// M6b that re-award is wired (ADR-0008): TickLoose runs the live-rebound
	/// contest (#48) and a made basket hands the ball back to the scorer (#49),
	/// both via AwardPossession → Catch, which restores a holder and makes
	/// IsLocalHolder true again for the recoverer. The old M4 "one shot per
	/// match" ceiling — when nothing ever called Catch() — is no longer in force.
	/// </summary>
	private void TryShoot()
	{
		if (!Input.IsActionJustPressed(ShootAction)) return;
		if (!IsLocalHolder) return;

		if (!ApplyShootLocally()) return;

		// Clients additionally ask the server to make it official. The server
		// (when it IS the holder) needed no RPC — it just applied the shot above.
		if (!IsServer)
			RpcId(1, MethodName.RequestShoot);
	}

	/// <summary>
	/// Shared shot-application step: transitions the state machine and builds
	/// the ShotArc. Used both by the predicting holder (TryShoot) and by the
	/// server when fulfilling a RequestShoot from a remote holder.
	/// </summary>
	/// <returns>True if the shot was legal (Held/Dribbling) and applied.</returns>
	///
	/// (Doubt cycle 1, finding #3) The release point used here is whichever
	/// GlobalPosition this machine currently has for the ball — the client's
	/// own predicted position when the client is the holder, or the server's
	/// (possibly up-to-1-RTT-different) view of that same holder's position
	/// when the server applies a remote RequestShoot. These two release
	/// points can briefly differ; this is expected, not a new failure mode —
	/// the standard ReconcileFromServer pass on the next broadcast absorbs it
	/// exactly like any other position divergence.
	///
	/// (#24 doubt cycle 1, finding #4 — scorer attribution) HolderPeerId must
	/// be captured into _lastShooterPeerId BEFORE StateMachine.Shoot() runs,
	/// because Shoot() clears HolderPeerId to 0 (see class doc's "Holder
	/// resolution" / TryShoot's M4 limitation note) — by the time TickInFlight
	/// later detects a Make, HolderPeerId is already gone. Capturing it here
	/// (the one place both shoot paths funnel through — the server's own
	/// TryShoot→ApplyShootLocally call AND the server's RequestShoot→
	/// ApplyShootLocally call for a remote holder) means _lastShooterPeerId is
	/// correct in both cases: when the server itself is the holder, and when a
	/// remote client is. A CLIENT also runs this method (for its own
	/// prediction) and so also sets its own _lastShooterPeerId — harmless,
	/// since RegisterBasket no-ops on a client regardless of which id it's
	/// called with.
	private bool ApplyShootLocally()
	{
		int holderAtShootTime = StateMachine.HolderPeerId;
		if (!StateMachine.Shoot()) return false;

		_lastShooterPeerId = holderAtShootTime;
		_arc = new ShotArc(GlobalPosition, ShotTarget, ShotApexHeight, Gravity);
		return true;
	}

	// ── Server RPC: receive shoot request from the holder ───────────────────

	/// <summary>
	/// Called BY A CLIENT (the current ball holder) on the SERVER, requesting
	/// the authoritative shot transition.
	///
	/// Transfer mode: Reliable — a deliberate deviation from SubmitInput's
	/// UnreliableOrdered. SubmitInput is continuous per-tick state: a dropped
	/// packet is harmless because the next tick's packet supersedes it. A
	/// shoot request is the opposite — a ONE-TIME discrete event with no
	/// redundancy. If it's dropped, the player pressed the shoot button and
	/// nothing happened, which is a correctness bug, not a smoothing concern.
	/// Reliable's head-of-line-blocking risk (the reason ReceiveState and
	/// SubmitInput avoid it) doesn't apply here because this RPC fires
	/// rarely — once per shot, not 60 times a second — so there is no
	/// continuous stream for a retransmit to stall.
	///
	/// Security: validate the sender against StateMachine.HolderPeerId, not
	/// against Name — the Ball node has no peer-ID name (it is one shared
	/// node, not one-per-peer like PlayerController), so HolderPeerId is the
	/// only available authority record.
	/// </summary>
	[Rpc(MultiplayerApi.RpcMode.AnyPeer,
		 CallLocal = false,
		 TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void RequestShoot()
	{
		int senderId = Multiplayer.GetRemoteSenderId();
		if (senderId != StateMachine.HolderPeerId)
		{
			GD.PrintErr($"[BallController] Unauthorized RequestShoot from peer {senderId} (holder is {StateMachine.HolderPeerId})");
			return;
		}

		// The server may already be ahead of this request if its own tick
		// got here first (e.g. extremely low latency) — ApplyShootLocally
		// returns false harmlessly via StateMachine.Shoot()'s legality guard
		// if the ball is no longer Held/Dribbling.
		ApplyShootLocally();
	}

	// ── Client RPC: receive server state ────────────────────────────────────

	/// <summary>
	/// Called BY THE SERVER on all peers, broadcasting the authoritative ball
	/// state. Mirrors PlayerController.ReceiveState's reasoning exactly:
	///
	/// Transfer mode: UnreliableOrdered — only the LATEST snapshot is useful,
	/// and at 60 Hz Reliable's head-of-line blocking would cause exactly the
	/// rubber-banding this broadcast exists to prevent. A dropped packet is
	/// fine; the next tick's broadcast is fresher anyway.
	///
	/// state is sent as int, not the BallState enum directly: Godot's Variant
	/// marshaling for [Rpc] parameters is not guaranteed to box a raw C# enum
	/// cleanly, so we cast out and back in, mirroring how SubmitInput avoided
	/// passing a raw Vector2 by splitting into floats.
	///
	/// CallLocal = false: the server already ran this exact tick's prediction
	/// above (every peer always predicts), so the server must not re-apply
	/// its own broadcast as if it were a correction — it is already
	/// authoritative and reconciling against itself would be a no-op at best
	/// and a redundant smoothing pass at worst.
	///
	/// (Doubt cycle 1, finding #4) The payload is trusted with no validation
	/// in ReconcileFromServer below — RpcMode.Authority is engine-enforced:
	/// only this node's multiplayer authority (the server, by default) can
	/// successfully invoke this RPC at all, so a non-server peer cannot forge
	/// a state/holder pair here. This is the same trust boundary
	/// PlayerController.ReceiveState already relies on for its own payload.
	/// </summary>
	[Rpc(MultiplayerApi.RpcMode.Authority,
		 CallLocal = false,
		 TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
	private void ReceiveState(int state, Vector3 pos, Vector3 vel, int holderPeerId, bool cleared)
	{
		_serverState         = (BallState)state;
		_serverPos           = pos;
		_serverVel           = vel;
		_serverHolderPeerId  = holderPeerId;
		_serverCleared       = cleared;
		_hasNewState         = true;
	}

	// ── Reconciliation ───────────────────────────────────────────────────

	/// <summary>
	/// Reconciles local prediction to the latest server snapshot.
	///
	/// Discrete state: forced to match exactly — there is no meaningful
	/// "partial correction" of an enum, unlike continuous position. A
	/// mismatch here means the local prediction took a different transition
	/// than the server (e.g. it predicted a shot the server hasn't applied
	/// yet, or missed a transition due to a dropped RequestShoot) — ForceState
	/// repairs it unconditionally rather than silently keeping the stale
	/// local value.
	///
	/// Continuous position: same mesh-offset smooth-correction trick
	/// PlayerController uses. This node (treated as the "physics body" the
	/// way CharacterBody3D is for PlayerController) snaps immediately to the
	/// authoritative position so nothing reads a stale GlobalPosition;
	/// the divergence is instead absorbed by the mesh child's local offset,
	/// which lerps back to zero in ApplySmoothCorrection.
	///
	/// Velocity: resynced directly into _arc.Velocity (when in flight) so the
	/// integrator's next Step() continues from the server's value rather than
	/// drifting further from a stale local one.
	/// </summary>
	private void ReconcileFromServer()
	{
		if (StateMachine.Current != _serverState || StateMachine.HolderPeerId != _serverHolderPeerId)
			StateMachine.ForceState(_serverState, _serverHolderPeerId);

		// Cleared is server-authoritative and never predicted: a client takes
		// the broadcast value verbatim (#50). This corrects the <=1-RTT window
		// after a client-predicted rebound where the local flag was left stale.
		IsCleared = _serverCleared;

		Vector3 renderedPos = GlobalPosition;
		GlobalPosition = _serverPos;

		if (_serverState == BallState.InFlight || _serverState == BallState.Loose)
		{
			// _arc may be null on a client that hasn't predicted its own
			// Shoot() yet (e.g. it just received the server's transition to
			// InFlight before its own TryShoot ever ran) — construct a
			// matching arc rather than dereferencing null.
			if (_arc == null)
				_arc = new ShotArc(_serverPos, ShotTarget, ShotApexHeight, Gravity);
			_arc.Position = _serverPos;
			_arc.Velocity = _serverVel;
		}

		Vector3 divergence = renderedPos - GlobalPosition;
		if (divergence.Length() > ReconcileSnapThreshold)
			_smoothOffset = divergence; // SET, not accumulated — see PlayerController.
	}

	/// <summary>
	/// Lerps the visual-only MeshInstance3D offset toward zero each frame.
	/// Identical mechanism to PlayerController.ApplySmoothCorrection.
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
}
