using System;
using System.Collections.Generic;
using Godot;
using Hooper.Moves;
using Hooper.Player;
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
///   which has real input-authority rules (see CheckJumpShotRelease below;
///   M7b #74 moved the actual button-press read to PlayerController, but the
///   ball-state transition itself is still gated here the same way).
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

	/// <summary>
	/// Lateral offset (metres) from the holder's centerline to the ball,
	/// displaying which hand it's currently in (M7b, issue #73). Stacks with
	/// DribbleForwardOffset — the ball sits forward-AND-to-the-side, not
	/// directly on the centerline. Exact value is hitl visual sign-off, the
	/// same as FacingResolver/LeanResolver's tilt — the human verifies
	/// in-editor that the offset reads as a believable hand position.
	/// </summary>
	[Export] public float HandOffset { get; set; } = 0.18f;

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

	// ── Floor-bounce tunables (issue #66, M8 realism pass) ────────────────

	/// <summary>
	/// Coefficient of restitution for floor contact [0..1].
	/// Grounded in measured data: a regulation NBA ball inflated to spec rebounds
	/// to ~1.22 m when dropped from 1.8 m on hardwood, i.e. COR = √(1.22/1.8) ≈
	/// 0.82.  The simulation harness (docs/handoffs note / scratchpad) confirms
	/// 0.82 produces a realistic decay — first rebound ~125 cm from a 1.8 m drop,
	/// settling over ~15 ever-smaller bounces — rather than the dead single-thud
	/// a lower value gives.  Higher than RimRestitution (0.65) because a properly
	/// inflated ball returns more energy off the floor than off the rigid rim edge.
	/// </summary>
	[Export] public float FloorRestitution { get; set; } = 0.82f;

	/// <summary>
	/// Fraction of horizontal (XZ) speed retained after each floor contact [0..1].
	/// Models rolling friction and spin-down per bounce. 1.0 = frictionless floor.
	/// Default 0.9 means 10 % of lateral speed is lost each contact — hardwood
	/// contact is brief, so lateral loss per bounce is small; the ball keeps
	/// rolling/drifting realistically rather than stopping dead.
	/// </summary>
	[Export] public float FloorHorizontalDecay { get; set; } = 0.9f;

	/// <summary>
	/// Post-bounce vertical speed threshold (m/s). When a bounce would produce
	/// a vertical rebound speed below this value, the ball settles immediately
	/// (velocity zeroed) instead of executing an imperceptible micro-bounce.
	/// Prevents infinite-bounce jitter and keeps the ball visibly at rest.
	/// 0.6 m/s ⇒ a ~1.8 cm rebound, below the threshold of visual perception at
	/// typical camera distances; it trims the long tail of tiny bounces without
	/// cutting any visible bounce short.  Tune lower for more "active" micro-bouncing.
	/// </summary>
	[Export] public float FloorSettleSpeed { get; set; } = 0.6f;

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

	// ── Shot scatter tunables (issue #62, ADR-0009) ──────────────────────

	/// <summary>
	/// Master switch for distance-based shot scatter (issue #62, ADR-0009).
	/// Enabled by default: the magnitudes below were tuned against a Monte-Carlo
	/// make-percentage sweep through the real ShotArc + RimBackboard physics (see
	/// ShotMakeCurveTests) so the resulting curve matches real basketball — open
	/// layups automatic, an open three ~41 % (NBA wide-open ≈ 38–40 %), long
	/// heaves falling off steeply.  Flip to <c>false</c> only to restore the old
	/// "every uncontested shot makes" behaviour for an isolated test.
	///
	/// Server-only: the scatter draw runs only when IsServer (client prediction
	/// keeps aiming dead-centre; ReconcileFromServer snaps the arc to the
	/// server's possibly-missed trajectory within ~1 RTT — no new netcode
	/// needed, see ADR-0009 and the ApplyShootLocally comment below).
	/// </summary>
	[Export] public bool ShotScatterEnabled { get; set; } = true;

	/// <summary>
	/// Base scatter radius per metre of shot distance, in metres per metre.
	/// The raw offset radius before capping is
	/// <c>ShotScatterPerMeter × horizontalDistance</c>.
	/// 0.026 m/m is tuned so the make curve tracks real FG% in the playable
	/// range: shots under ~4 m make ~100 % open, ~5 m ≈ 67 %, an open three
	/// (~6.75 m) ≈ 41 %.  Since a make requires the offset to land inside the
	/// inner-rim radius (0.11 m), make% ≈ (0.11 / (perMeter·distance))² — raise
	/// this to make shooting harder, lower it to make it more forgiving.
	/// </summary>
	[Export] public float ShotScatterPerMeter { get; set; } = 0.026f;

	/// <summary>
	/// Hard cap on the BASE scatter offset radius (metres), before accuracy
	/// penalties.  Prevents very long shots from producing absurd multi-metre
	/// misses.  0.45 m is roughly the rim radius × 2 — a clear miss that still
	/// looks like an honest attempt; it floors long open heaves at ~6–12 % and
	/// binds only on long, heavily-penalised shots (the penalty multiplier is
	/// applied after this cap — see ShotScatter.Scatter).
	/// </summary>
	[Export] public float MaxShotScatter { get; set; } = 0.45f;

	/// <summary>
	/// Seed for the server-side shot-scatter RNG (<c>_shotRng</c>).
	/// Changing this seed changes the miss pattern for a given sequence of
	/// shots but does not affect whether scatter is active (that is
	/// <see cref="ShotScatterEnabled"/>).  Exported so it can be varied in
	/// the editor to test different miss distributions without recompiling.
	/// </summary>
	[Export] public int ShotScatterSeed { get; set; } = 12345;

	/// <summary>
	/// Strength of the movement penalty applied to shot scatter (issue #64,
	/// ADR-0009).  When <see cref="ShotScatterEnabled"/> is true and the
	/// shooter is moving, the scatter radius is scaled by
	/// <c>1 + MovementScatterK × speedRatio</c>, where
	/// <c>speedRatio = clamp(velocity / MoveSpeed, 0, 1)</c>.  A full-sprint
	/// shot receives the maximum penalty; a stationary shot has factor 1 (no
	/// penalty beyond the base distance scatter).
	///
	/// Only active inside the <c>IsServer &amp;&amp; ShotScatterEnabled</c>
	/// block — client prediction keeps aiming dead-centre, unchanged.
	///
	/// 0.8 ⇒ a full-sprint shot scatters 1.8× as much as a stationary one; in the
	/// make sweep this turns an open 5 m shot (~67 %) into ~35 % when fired on the
	/// move, leaving close shots forgiving unless ALSO contested.
	///
	/// <b>Open design question (#64):</b> continuous speed-ratio (current)
	/// vs. a discrete planted/not-planted threshold.  A threshold may fit
	/// ADR-0003's hybrid committed-move model better.  Default continuous
	/// pending human review.
	/// </summary>
	[Export(PropertyHint.Range, "0,3,0.05")] public float MovementScatterK { get; set; } = 0.8f;

	/// <summary>
	/// Strength of the defender-contest penalty applied to shot scatter (issue
	/// #65, ADR-0009).  When <see cref="ShotScatterEnabled"/> is true and the
	/// other player is within <see cref="ContestRange"/> metres (XZ), the
	/// scatter radius is scaled by <c>1 + ContestScatterK × proximity</c>,
	/// where <c>proximity = clamp(1 - dist / ContestRange, 0, 1)</c>.  A
	/// defender exactly at the shooter's position gives factor
	/// <c>1 + ContestScatterK</c>; beyond <see cref="ContestRange"/> gives
	/// factor 1 (no penalty).  If no other player node is present (solo test),
	/// factor is 1.
	///
	/// Only active inside the <c>IsServer &amp;&amp; ShotScatterEnabled</c>
	/// block — client prediction keeps aiming dead-centre, unchanged.
	///
	/// <b>Open design question (#65):</b> proximity-alone (current) vs.
	/// requiring the defender to be facing/closing-out.  ADR-0003 earmarks the
	/// full contest/timing mechanic for the timing-window layer; this is the
	/// deliberately-minimal first slice.  Do not grow into block/steal logic
	/// here — that belongs in a later milestone.  Default proximity-only
	/// pending human review.
	/// </summary>
	[Export(PropertyHint.Range, "0,3,0.05")] public float ContestScatterK { get; set; } = 1.0f;

	/// <summary>
	/// XZ-plane distance (metres) within which the other player contests a
	/// shot (issue #65, ADR-0009).  Beyond this range the contest penalty
	/// factor is 1 (no effect).  Pairs with <see cref="ContestScatterK"/>.
	/// 2.2 m is roughly an arm's-length closeout: a defender ~1 m away yields a
	/// ~1.5× scatter factor, dropping an open 5 m shot from ~67 % to ~43 %; a
	/// defender right on top approaches the full <see cref="ContestScatterK"/>.
	/// </summary>
	[Export] public float ContestRange { get; set; } = 2.2f;

	/// <summary>
	/// Strength of the facing-direction penalty applied to shot scatter (issue
	/// #81, ADR-0009 amendment 2026-06-27).  When
	/// <see cref="ShotScatterEnabled"/> is true, the scatter radius is scaled
	/// by <c>1 + FacingScatterK × (angle / π)</c>, where <c>angle</c> is the
	/// shortest angular distance in [0, π] between the shooter's
	/// server-authoritative <c>Heading</c> (ADR-0010) and the direction to the
	/// rim.  A squared-up shot has factor 1 (no penalty); a full back-to-basket
	/// shot has factor <c>1 + FacingScatterK</c>.
	///
	/// Only active inside the <c>IsServer &amp;&amp; ShotScatterEnabled</c>
	/// block — client prediction keeps aiming dead-centre, unchanged.
	///
	/// 0.8 ⇒ a back-to-basket shot (180°) scatters 1.8× as much as a
	/// squared-up shot, placing the facing penalty slightly below the maximum
	/// ContestScatterK = 1.0 on-ball closeout (2.0×).  A 90° side-on shot
	/// scatters 1.4× — meaningful but still makeable on a clean look.  This
	/// keeps the facing factor clearly subordinate to a full defender closeout
	/// so the two penalties stack without producing absurd misses on moderate
	/// contest + slight mis-facing scenarios.
	///
	/// Uses <c>holder.Heading</c> (ADR-0010), NOT <c>FacingResolver</c> —
	/// FacingResolver is cosmetic-only and cannot feed an authoritative outcome
	/// (see ADR-0004 and ADR-0009 §Resolved 2026-06-27).
	/// </summary>
	[Export(PropertyHint.Range, "0,3,0.05")] public float FacingScatterK { get; set; } = 0.8f;

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

	// ── Ball-on-hand display (M7b, issue #73) ─────────────────────────────

	/// <summary>
	/// The holder peer id the ball-hand was last reset for — detects a possession
	/// change so the new holder's authoritative hand resets to the default
	/// (M9, #83/ADR-0012). Ball-hand is no longer a cosmetic value derived here;
	/// it lives on PlayerController.HandSide (server-authoritative, predicted +
	/// reconciled) and the ball mesh merely READS it (HandSign). This field is the
	/// edge-detector that fires the once-per-possession reset (UpdateHandSide).
	/// </summary>
	private int _handSideHolderId;

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

	// ── Shot scatter RNG (issue #62, ADR-0009) ─────────────────────────────

	/// <summary>
	/// Server-side seeded RNG for shot scatter (ADR-0009).  Seeded from
	/// <see cref="ShotScatterSeed"/> in _Ready (see initialisation note there).
	/// Only USED when <c>IsServer &amp;&amp; ShotScatterEnabled</c>; constructed
	/// on every peer anyway because _Ready runs everywhere, but a client never
	/// draws from it — the server's draw is the authoritative one, and the
	/// existing ReconcileFromServer broadcast snaps the client's predicted arc
	/// onto the server's (possibly scattered) trajectory within ~1 RTT.
	/// </summary>
	private Random _shotRng;

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
	/// input authority (see CheckJumpShotRelease) — only the holder's machine
	/// may legally trigger a shot, mirroring PlayerController's IsLocalPlayer
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

		// Seed the shot-scatter RNG from the editor-tunable ShotScatterSeed.
		// Constructed on every peer (client + server) because _Ready runs
		// everywhere, but only ever DRAWN from on the server (see _shotRng field
		// doc and ApplyShootLocally).  Constructing it unconditionally is
		// harmless — a System.Random allocation costs nothing — and keeps the
		// field non-null so there is no null-check at draw time if the
		// IsServer branch ever widens.
		_shotRng = new Random(ShotScatterSeed);

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
	/// The holder's forward direction on the XZ plane, derived from their
	/// server-authoritative <see cref="PlayerController.Heading"/> (ADR-0010) —
	/// NOT from velocity. This is what makes the ball orbit the holder as they
	/// turn, even while standing still: the old velocity-derived forward froze
	/// the instant the player stopped moving, so a pivot or a stationary
	/// crossover left the ball stranded on the pre-turn side. Heading is valid
	/// and identical across roles (own/server set it in Move(); the client's
	/// remote copy adopts the broadcast value in TickClientRemotePlayer), the
	/// same guarantee <see cref="HandSign"/> relies on. A null holder
	/// (pre-tipoff / loose) → heading 0 (+Z); the ball tracks the world origin
	/// in that case anyway, so the exact fallback direction is immaterial.
	/// </summary>
	private static Vector3 HolderForward(PlayerController holder)
	{
		Vector2 fwd = HeadingMath.Forward(holder?.Heading ?? 0f);
		return new Vector3(fwd.X, 0f, fwd.Y);
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
		var holderBody = Players?.GetNodeOrNull(StateMachine.HolderPeerId.ToString()) as PlayerController;
		Vector3 holderPos = holderBody?.GlobalPosition ?? Vector3.Zero;
		Vector3 forward   = HolderForward(holderBody);
		Vector3 right     = HandRight(forward);
		UpdateHandSide(holderBody);

		// World-space Y = DribbleHandHeight, consistent with DribbleCycle's world-Y convention.
		GlobalPosition = new Vector3(
			holderPos.X + forward.X * DribbleForwardOffset + right.X * HandOffset * HandSign(holderBody),
			DribbleHandHeight,
			holderPos.Z + forward.Z * DribbleForwardOffset + right.Z * HandOffset * HandSign(holderBody)
		);
		CheckJumpShotRelease(holderBody);
	}

	/// <summary>Ball bouncing in front of the holder. Shoot to release.</summary>
	private void TickDribbling(float dt)
	{
		_dribble.Advance(dt);
		var holderBody = Players?.GetNodeOrNull(StateMachine.HolderPeerId.ToString()) as PlayerController;
		Vector3 holderPos = holderBody?.GlobalPosition ?? Vector3.Zero;
		Vector3 forward   = HolderForward(holderBody);
		Vector3 right     = HandRight(forward);
		UpdateHandSide(holderBody);

		// Pass XZ-offset position; GetBallPosition discards the Y and uses HeightAtPhase instead.
		GlobalPosition = _dribble.GetBallPosition(new Vector3(
			holderPos.X + forward.X * DribbleForwardOffset + right.X * HandOffset * HandSign(holderBody),
			holderPos.Y,
			holderPos.Z + forward.Z * DribbleForwardOffset + right.Z * HandOffset * HandSign(holderBody)
		));
		CheckJumpShotRelease(holderBody);
	}

	/// <summary>
	/// The holder's world-space "right" direction given their forward vector
	/// (issue #73) — Cross(forward, Up), matching the Godot convention
	/// HolderForward's callers already assume (-Z is forward, +X is
	/// right). Used to place the ball to the side of the holder's centerline
	/// rather than only in front of it.
	/// </summary>
	private static Vector3 HandRight(Vector3 forward) => new(-forward.Z, 0f, forward.X);

	/// <summary>
	/// +1 when the ball renders in the holder's right hand, -1 when in the left.
	/// READS the holder's server-authoritative HandSide (M9, #83/ADR-0012) — the
	/// ball no longer derives its own hand from burst direction. Works for every
	/// role: the holder's node carries the authoritative value (own/server) or the
	/// broadcast value (the client's remote copy adopts it in TickClientRemotePlayer),
	/// so the opponent's crossover hand-switch renders on your screen for free.
	/// Null holder (pre-tipoff / loose) defaults to Left.
	/// </summary>
	private static float HandSign(PlayerController holder) =>
		(holder?.HandSide ?? HandSide.Left) == HandSide.Right ? 1f : -1f;

	/// <summary>
	/// Resets the new holder's authoritative ball-hand to the default on a
	/// possession change (M9, #83/ADR-0012): a new holder has no carried-over hand
	/// state. Fires at most once per possession via the _handSideHolderId edge.
	///
	/// Runs on every machine — the state switch (TickHeld/TickDribbling) ticks on
	/// server AND clients, and HolderPeerId is the broadcast authoritative holder
	/// — so the server resets authoritatively, the client's own player predicts the
	/// same reset, and the client's remote copy picks the reset up via the broadcast
	/// HandSide. The per-possession swaps themselves are NOT touched here (they are
	/// driven by the crossover's Active-entry in PlayerController); this only sets
	/// the clean starting hand when possession changes.
	/// </summary>
	private void UpdateHandSide(PlayerController holder)
	{
		if (StateMachine.HolderPeerId == _handSideHolderId) return;

		_handSideHolderId = StateMachine.HolderPeerId;
		holder?.ResetHandSide();
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
			// Resolve the GameManager ONCE: RegisterBasket can set IsGameOver
			// synchronously (and fires the GameOver signal whose handlers may
			// mutate the tree), so re-calling GetGameManager() for the IsGameOver
			// read could observe a different — or null-fallback — node and
			// wrongly award a post-game possession (#136). One reference, one tick.
			GameManager gm = GetGameManager();
			gm?.RegisterBasket(_lastShooterPeerId);

			// Make-it-take-it, unless the basket ended the game — then the
			// game-over freeze stands (see _PhysicsProcess's no-freeze note).
			// cleared: true — the scorer already earned their trip (see doc).
			if (!(gm?.IsGameOver ?? false))
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

		// Floor contact: bounce the ball with restitution instead of dead-stopping.
		// FloorBounce.Resolve is a pure helper (ADR-0004 headless-seam, issue #66):
		//   - Depenetrates: sets position.Y = BallRadius.
		//   - Reflects vY with FloorRestitution; decays vX/vZ by FloorHorizontalDecay.
		//   - Settles (velocity = 0) when the post-bounce vertical speed would fall
		//     below FloorSettleSpeed — preventing infinite micro-bounce jitter.
		// This replaces the old `Velocity = Vector3.Zero` dead-stop, so the ball now
		// bounces a few times before coming to rest, matching hardwood behaviour.
		// The call site already guards p.Y <= BallRadius so Resolve's internal guard
		// is a safety net, not the primary check — performance-neutral.
		if (p.Y <= BallRadius)
		{
			(Vector3 bouncedPos, Vector3 bouncedVel) = FloorBounce.Resolve(
				p, _arc.Velocity,
				BallRadius, FloorRestitution,
				FloorHorizontalDecay, FloorSettleSpeed);
			_arc.Position = bouncedPos;
			_arc.Velocity = bouncedVel;
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

	// ── Shot trigger / input authority (M4 → M7b #74) ───────────────────────

	/// <summary>
	/// Fires the actual ball-state transition for a jump shot, on the tick the
	/// holder's committed-move machine enters Active (M7b, issue #74) — this
	/// REPLACES the old instant "press shoot, ball leaves hand" trigger that
	/// used to live here as TryShoot(). The shoot BUTTON is no longer read by
	/// this class at all: pressing it now begins a JumpShot on the holder's
	/// PlayerController (PlayerController.SampleMoveInput), and the release
	/// several ticks later is this method's job, derived purely from that
	/// machine's state — there is no separate "release" RPC.
	///
	/// Authority rules (mirrors the old TryShoot/RequestShoot split, but
	/// derived from machine state instead of an input press):
	///   - SERVER: always checks, regardless of whether the holder is its own
	///     player or a remote client's — the server already runs an
	///     authoritative CommittedMoveMachine for EVERY player node (see
	///     PlayerController's class doc, M4 #21), so JustReleasedJumpShot is
	///     truthful here for either holder role with no RPC needed.
	///   - CLIENT, own holder: predicts the same release locally against its
	///     own _machine copy for zero perceived lag, exactly like the old
	///     TryShoot's client-prediction branch.
	///   - CLIENT, remote holder: never fires here — the client's copy of a
	///     REMOTE player never advances its local _machine (the #69 gap), so
	///     JustReleasedJumpShot is permanently false for it regardless. That
	///     client instead sees the shot via the ball's own ReceiveState
	///     broadcast/ReconcileFromServer, completely unchanged by this issue.
	///
	/// Shoot() clears HolderPeerId to 0, and no peer's unique ID is ever 0, so
	/// IsLocalHolder is false after a shot until possession is re-awarded. As of
	/// M6b that re-award is wired (ADR-0008): TickLoose runs the live-rebound
	/// contest (#48) and a made basket hands the ball back to the scorer (#49),
	/// both via AwardPossession → Catch, which restores a holder and makes
	/// IsLocalHolder true again for the recoverer.
	/// </summary>
	private void CheckJumpShotRelease(PlayerController holder)
	{
		if (!IsServer && !IsLocalHolder) return;
		if (holder == null) return;
		if (holder.JustReleasedJumpShot)
			ApplyShootLocally(holder);
	}

	/// <summary>
	/// Shared shot-application step: transitions the state machine and builds
	/// the ShotArc. Used both by the predicting holder (CheckJumpShotRelease's
	/// client branch) and by the server (CheckJumpShotRelease's server branch,
	/// for either holder role) — see CheckJumpShotRelease's doc for the full
	/// authority split (M7b, #74; extended in #64/#65 to accept the holder for
	/// accuracy-penalty computation).
	/// </summary>
	/// <param name="holder">
	/// The PlayerController currently holding the ball.  Used server-side only
	/// (inside the <c>IsServer &amp;&amp; ShotScatterEnabled</c> block) to read
	/// <c>Velocity</c> and <c>MoveSpeed</c> for the movement penalty (#64) and
	/// <c>GlobalPosition</c> + peer id for the contest-proximity lookup (#65).
	/// Never null at either call site (CheckJumpShotRelease guards this).
	/// </param>
	/// <returns>True if the shot was legal (Held/Dribbling) and applied.</returns>
	///
	/// (Doubt cycle 1, finding #3) The release point used here is whichever
	/// GlobalPosition this machine currently has for the ball — the client's
	/// own predicted position when the client is the holder, or the server's
	/// (possibly up-to-1-RTT-different) view of that same holder's position
	/// when the server applies the release for a remote holder. These two
	/// release points can briefly differ; this is expected, not a new failure
	/// mode — the standard ReconcileFromServer pass on the next broadcast
	/// absorbs it exactly like any other position divergence.
	///
	/// (#24 doubt cycle 1, finding #4 — scorer attribution) HolderPeerId must
	/// be captured into _lastShooterPeerId BEFORE StateMachine.Shoot() runs,
	/// because Shoot() clears HolderPeerId to 0 (see class doc's "Holder
	/// resolution") — by the time TickInFlight later detects a Make,
	/// HolderPeerId is already gone. Capturing it here (the one place both
	/// shoot paths funnel through — CheckJumpShotRelease's server branch for
	/// either holder role, and its client-prediction branch) means
	/// _lastShooterPeerId is correct in both cases: when the server itself is
	/// the holder, and when a remote client is. A CLIENT also runs this
	/// method (for its own prediction) and so also sets its own
	/// _lastShooterPeerId — harmless, since RegisterBasket no-ops on a client
	/// regardless of which id it's called with.
	private bool ApplyShootLocally(PlayerController holder)
	{
		int holderAtShootTime = StateMachine.HolderPeerId;
		if (!StateMachine.Shoot()) return false;

		_lastShooterPeerId = holderAtShootTime;

		// ── Shot scatter (issue #62, ADR-0009) ───────────────────────────────
		// Clients predict dead-centre: aimTarget == ShotTarget.  The server
		// draws from _shotRng to possibly offset the target into a miss; the
		// existing ReconcileFromServer broadcast (which runs every in-flight
		// tick) snaps the client's predicted arc onto the server's (possibly
		// scattered) trajectory within ~1 RTT — no new netcode needed.
		//
		// Distance is XZ-plane only: ignoring the Y difference (shooter height
		// vs. rim height) means a tall and a short player shooting from the
		// same floor position get identical scatter magnitude, which is the
		// intended mechanic (scatter grows with court distance, not arc length).
		Vector3 aimTarget = ShotTarget;
		if (IsServer && ShotScatterEnabled)
		{
			float dx       = ShotTarget.X - GlobalPosition.X;
			float dz       = ShotTarget.Z - GlobalPosition.Z;
			float distance = MathF.Sqrt(dx * dx + dz * dz);

			float angle01  = (float)_shotRng.NextDouble();
			float radius01 = (float)_shotRng.NextDouble();

			// ── Accuracy multiplier: movement (#64) × contest (#65) ──────────
			// Both penalties are pure server-side reads of already-authoritative
			// state (holder Velocity/MoveSpeed; player node GlobalPosition).
			// No new netcode: clients keep aiming dead-centre and are corrected
			// by the next ReconcileFromServer broadcast, exactly as with the
			// base #62 distance scatter.  The multiplier is ≥ 1; a stationary
			// uncontested shot has multiplier 1.0 and behaves identically to
			// the pre-#64/#65 path.

			// #64 — movement penalty: full-sprint shot scatters most.
			// speedRatio is in [0,1]: 0 = standing still, 1 = full MoveSpeed.
			float speedRatio      = holder.MoveSpeed > 0f
				? Math.Clamp(holder.Velocity.Length() / holder.MoveSpeed, 0f, 1f)
				: 0f;
			float movementFactor  = 1f + MovementScatterK * speedRatio;

			// #65 — contest penalty: defender within ContestRange → larger factor.
			// proximity is in [0,1]: 0 = at ContestRange edge, 1 = on top of shooter.
			float contestFactor   = 1f;
			int   shooterPeerId   = holderAtShootTime; // captured before Shoot() cleared it
			int   defenderPeerId  = OtherPlayerPeerId(shooterPeerId);
			if (defenderPeerId != 0 && Players != null)
			{
				var defenderNode = Players.GetNodeOrNull<Node3D>(defenderPeerId.ToString());
				if (defenderNode != null && ContestRange > 0f)
				{
					float ddx       = defenderNode.GlobalPosition.X - holder.GlobalPosition.X;
					float ddz       = defenderNode.GlobalPosition.Z - holder.GlobalPosition.Z;
					float defDist   = MathF.Sqrt(ddx * ddx + ddz * ddz);
					float proximity = Math.Clamp(1f - defDist / ContestRange, 0f, 1f);
					contestFactor   = 1f + ContestScatterK * proximity;
				}
			}

			// #81 — facing penalty: back-to-basket shots scatter more.
			// Reads holder.Heading (server-authoritative, ADR-0010), NOT
			// FacingResolver — using FacingResolver here would make an
			// authoritative make/miss depend on cosmetic state, the
			// "ADR-0004 trap" documented in ADR-0009 §Resolved 2026-06-27.
			float facingFactor = ShotFacing.Multiplier(
				holder.Heading,
				holder.GlobalPosition,
				ShotTarget,
				FacingScatterK);

			float accuracyMultiplier = movementFactor * contestFactor * facingFactor;

			aimTarget = ShotScatter.Scatter(
				ShotTarget, distance, angle01, radius01,
				ShotScatterPerMeter, MaxShotScatter, accuracyMultiplier);
		}

		_arc = new ShotArc(GlobalPosition, aimTarget, ShotApexHeight, Gravity);
		return true;
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
	/// than the server (e.g. it predicted a JumpShot release the server's
	/// authoritative _machine copy hasn't reached Active for yet) —
	/// ForceState repairs it unconditionally rather than silently keeping the
	/// stale local value.
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
			// InFlight before its own CheckJumpShotRelease ever fired) —
			// construct a matching arc rather than dereferencing null.
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
