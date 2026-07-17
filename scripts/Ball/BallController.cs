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

	/// <summary>
	/// Duration (seconds) of the crossover's authoritative cross-body ball
	/// transit (#195) — how long the ball takes to sweep from the old hand's
	/// lateral offset to the new one instead of teleporting there in one
	/// tick. ~0.12s default (feel-tuned; hitl sign-off deferred to the
	/// per-milestone human pass, NOT this issue — see CrossoverBallSweep).
	/// </summary>
	[Export] public float CrossoverSweepDuration { get; set; } = 0.12f;

	/// <summary>
	/// Depth (metres) of the single-arch low dip the ball takes at the
	/// midpoint of a crossover sweep (#195) — a real cross-body dribble dips
	/// low and protective through the middle of the transit rather than
	/// sliding flat across the body. ~0.15m default (feel-tuned; hitl
	/// sign-off deferred, NOT this issue — see CrossoverBallSweep).
	/// </summary>
	[Export] public float CrossoverSweepDipDepth { get; set; } = 0.15f;

	/// <summary>
	/// How far (metres) BEHIND the holder's DribbleForwardOffset baseline the
	/// ball is pulled at the midpoint of a BehindTheBack sweep (#194) — the
	/// "shielded" transit: the ball travels behind the body, away from a
	/// front-facing defender, instead of Crossover's in-front sweep. Larger
	/// than DribbleForwardOffset (0.5) by default so the ball's forward
	/// offset actually goes NEGATIVE at the peak (i.e. genuinely behind the
	/// holder's centerline, not just less-in-front). Reuses the SAME
	/// single-arch curve CrossoverBallSweep.Offset already computes for the
	/// vertical dip (0 at both ends, peak at t=0.5) — one pure curve, two
	/// consumers, per the "composition over hierarchy" call (#194). Feel-
	/// tuned default, hitl sign-off deferred to the per-milestone human pass
	/// like every other sweep tunable.
	/// </summary>
	[Export] public float BehindTheBackSweepDepth { get; set; } = 0.7f;

	/// <summary>
	/// Depth (metres) of the vertical dip a BetweenTheLegs sweep (#199) takes
	/// at its midpoint — DEEPER than Crossover's CrossoverSweepDipDepth
	/// (0.15), because a real between-the-legs move has to bring the ball
	/// down to bounce it off the floor between the legs, not merely dip it
	/// low in front of the body. Reuses the SAME single-arch curve
	/// (CrossoverBallSweep.Offset's verticalDip, 0 at both ends, peak at
	/// t=0.5) as Crossover/BehindTheBack — only the depth constant differs,
	/// composition over a new curve (#194's precedent). At the peak,
	/// DribbleHandHeight (1.0) - 0.85 = 0.15m — close to the floor
	/// (BallRadius 0.12) without clipping through it, representing the
	/// bounce point the issue's "ball exposure" identity describes. Feel-
	/// tuned default, hitl sign-off deferred to the per-milestone human pass
	/// like every other sweep tunable.
	/// </summary>
	[Export] public float BetweenTheLegsDipDepth { get; set; } = 0.85f;

	/// <summary>Full down-and-up dribble cycle duration (seconds).</summary>
	[Export] public float DribblePeriod { get; set; } = 0.6f;

	// ── Shot tunables ─────────────────────────────────────────────────────

	/// <summary>Peak world-space Y the shot arc reaches (metres).</summary>
	[Export] public float ShotApexHeight { get; set; } = 4.0f;

	/// <summary>Downward acceleration applied to the shot + loose ball (m/s²).</summary>
	[Export] public float Gravity { get; set; } = 9.8f;

	// ── Basket geometry (must match the hoop node's placement) ────────────

	/// <summary>
	/// Default RimCenter, hoisted to a static field (issue #216 finding 2) so
	/// BoardCenter's default can derive from it below instead of duplicating
	/// the literal. Not itself referenceable from an [Export] property
	/// initializer if it were an instance member — Godot's source generator
	/// and C# both require export defaults to be static-evaluable (CS0236) —
	/// which is exactly why this is `static readonly` rather than inlined on
	/// RimCenter's own initializer.
	/// </summary>
	private static readonly Vector3 DefaultRimCenter = new(0f, 3.05f, 0f);

	/// <summary>World-space centre of the rim ring. Used for collision geometry only.</summary>
	[Export] public Vector3 RimCenter { get; set; } = DefaultRimCenter;

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

	/// <summary>
	/// World-space centre of the backboard face. Derived from DefaultRimCenter
	/// + RimBackboard.DefaultRimToBoardOffset (issue #216 finding 2) instead of
	/// a hand-copied literal, so a code-built tree with no .tscn override
	/// (headless harnesses, unit-adjacent tests) behaves like production
	/// instead of intercepting every clean make-arc with a board that sits in
	/// FRONT of the rim. See RimBackboard.IsBoardBehindRim, which pins this
	/// invariant (issue #217; the old default (0, 3.5, 0.3) was 0.3 m in
	/// front of RimCenter (0, 3.05, 0)). Deriving both defaults from the same
	/// two static sources — instead of two independent literals that only
	/// agreed by hand — is what makes this class of regression structurally
	/// impossible instead of merely fixed.
	/// </summary>
	[Export] public Vector3 BoardCenter { get; set; } = DefaultRimCenter + RimBackboard.DefaultRimToBoardOffset;

	/// <summary>
	/// Unit normal along the ball's approach axis toward the board — AWAY
	/// from the court, from the rim toward the board (issue #216 finding 1;
	/// see RimBackboard.BoardNormal's doc for why this sign, not "toward the
	/// court", is the load-bearing convention).
	/// </summary>
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
	/// Grounded in the NBA inflation spec (issue #79, ADR-0014 top reference): a
	/// ball dropped from 72 in (measured to the BOTTOM of the ball) must rebound
	/// to 49–54 in (measured to the TOP of the ball).  Because rebound height
	/// scales as COR², that band pins COR ∈ [0.741, 0.787] — the diameter offset
	/// between the two measurement points is ~9.4 in and must NOT be ignored (the
	/// earlier √(1.22/1.8) ≈ 0.82 derivation did, landing the ball above legal at
	/// ~58 in).  0.76 sits mid-band (rebound top ≈ 51 in) and matches the widely
	/// cited empirical basketball-on-hardwood COR (~0.75–0.78).  Proven by
	/// FloorBounceTests.RegulationDrop_ReboundTop_LandsInNbaLegalBand.  Still higher
	/// than RimRestitution (0.65) because an inflated ball returns more energy off
	/// the floor than off the rigid rim edge.
	/// </summary>
	[Export] public float FloorRestitution { get; set; } = 0.76f;

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

	/// <summary>
	/// Floor-plane (XZ) distance (metres) from RimCenter within which pressing
	/// ShootAction begins a Layup instead of a JumpShot (issue #229, ADR-0022;
	/// see LayupRangeResolver, the pure decision PlayerController.
	/// SampleMoveInput reads this into). 4.0m matches this issue's own
	/// acceptance text ("&lt;4m ≈ automatic") and sits between ADR-0009's
	/// ≤3m≈100%-open and 5m≈67%-open anchors — the same real-ball fact the
	/// layup's frame data is grounded in, not an independently guessed number.
	/// Editor-tunable balance surface, not an architectural constant.
	/// </summary>
	[Export] public float LayupRange { get; set; } = 4.0f;

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
	///
	/// Defaults to <see cref="CourtBounds.DefaultMin"/> — the single source of
	/// truth every test fixture also derives from, so this export, the white
	/// outline CourtVisuals draws from it, and the test suite can never drift
	/// apart on what "the court rectangle" is. Change the width/depth THERE,
	/// not here, unless you're deliberately overriding a specific instance in
	/// the Inspector.
	/// </summary>
	[Export] public Vector2 CourtMin { get; set; } = CourtBounds.DefaultMin;

	/// <summary>
	/// Floor-plane (XZ) upper bound of the playable court rectangle: X = right
	/// edge, Y = far edge (largest Z). See CourtMin for layout notes and the
	/// <see cref="CourtBounds.DefaultMax"/> single-source-of-truth rationale.
	/// </summary>
	[Export] public Vector2 CourtMax { get; set; } = CourtBounds.DefaultMax;

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

	// ── Steal tunables (M10, issue #96, ADR-0018 §2) ─────────────────────

	/// <summary>
	/// Low bound of the dribble phase considered "exposed" for a steal attempt
	/// [0, 1). The ball is stealable while
	/// <see cref="DribbleCycle.Phase"/> ∈ [StealLoExposed, StealHiExposed],
	/// straddling phase ≈ 0.5 (floor contact — the ball is lowest and furthest
	/// from the handler's hand, the natural steal window, ADR-0018 §2).
	///
	/// Default 0.35 = ball enters the window ~30 % through the downward arc.
	/// Tuning deferred to #104 + the per-milestone feel pass (ADR-0015).
	/// </summary>
	[Export] public float StealLoExposed { get; set; } = 0.35f;

	/// <summary>
	/// High bound of the dribble phase considered "exposed" for a steal
	/// attempt [0, 1). Symmetric with <see cref="StealLoExposed"/> around 0.5.
	///
	/// Default 0.65 = window closes when the ball has risen ~15 % past the
	/// floor contact point on the upward arc. Tuning deferred to #104.
	/// </summary>
	[Export] public float StealHiExposed { get; set; } = 0.65f;

	/// <summary>
	/// Horizontal speed (m/s) of the provisional "knock" velocity a successful
	/// steal imparts on the now-Loose ball, directed from the holder toward the
	/// stealing defender (ADR-0014 — real 1v1 ball: a stolen ball is knocked
	/// toward whoever poked it away, not left inert). Purely provisional —
	/// precise feel tuning is deferred to #104 + the per-milestone feel pass
	/// (ADR-0015); this value only needs to be non-degenerate so ResolveStealAttempts
	/// can seed a real ShotArc (see that method's doc for why an unseeded _arc
	/// crashes TickLoose).
	/// </summary>
	[Export] public float StealKnockSpeed { get; set; } = 1.5f;

	/// <summary>
	/// Vertical rise (m/s) of the same provisional knock velocity — a small pop
	/// so the ball briefly leaves the floor plane instead of sliding, matching
	/// the visual read of a real deflection. Provisional; tuned in #104.
	/// </summary>
	[Export] public float StealKnockRiseSpeed { get; set; } = 1.0f;

	// ── Block tunables (M10, issue #98, ADR-0018 §2) ─────────────────────

	/// <summary>
	/// Number of ticks after the ball enters InFlight during which a block
	/// attempt can still connect — the "grace" tail of the shot's vulnerable
	/// window. The vulnerable interval is [InFlight start, InFlight start + BlockGraceTicks)
	/// on the deterministic physics-tick clock (ADR-0018 §2).
	///
	/// Mechanically: after the ball leaves the hand it continues to rise near
	/// the shooter. A defender jumping to block connects if their Active window
	/// overlaps this window. Once the grace period expires the ball is past
	/// the defender and the block opportunity is gone.
	///
	/// Default 10 ticks ≈ 0.17 s at 60 Hz — provisional; deferred to #104
	/// and the per-milestone feel pass (ADR-0015). Must be ≥ BlockMove.ActiveFrames
	/// (currently 8) per ADR-0018 §3 so a perfectly-timed block can always connect.
	/// Derives from BlockMove.DefaultBlockGraceTicks (issue #216 original body
	/// row 7) rather than an independently hand-copied literal, so this and
	/// the xUnit mirror in BlockMoveTests can't drift.
	/// </summary>
	[Export] public int BlockGraceTicks { get; set; } = BlockMove.DefaultBlockGraceTicks;

	/// <summary>
	/// Horizontal speed (m/s) of the provisional "swat" velocity a successful
	/// block imparts on the now-Loose ball, directed from the rim toward the
	/// ball — i.e. AWAY from the basket (ADR-0014 — real 1v1 ball: a blocked
	/// shot is swatted back toward the court, it does not continue its arc to
	/// the rim; ADR-0008 Amendment 2026-06-30: "the in-flight arc terminates").
	/// Mirrors StealKnockSpeed's role for the steal. Provisional — feel tuning
	/// deferred to #104 + the per-milestone feel pass (ADR-0015).
	/// </summary>
	[Export] public float BlockSwatSpeed { get; set; } = 2.0f;

	/// <summary>
	/// Downward speed (m/s) of the same swat velocity — a block knocks the
	/// ball DOWN out of its rising arc (contrast StealKnockRiseSpeed: a steal
	/// pokes a low ball up off the floor plane; a block swats a high ball down
	/// toward it). TickLoose's FloorBounce then gives the natural
	/// hit-the-hardwood-and-bounce read. Provisional; tuned in #104.
	/// </summary>
	[Export] public float BlockSwatDropSpeed { get; set; } = 1.0f;

	/// <summary>
	/// Maximum XZ distance (metres) between the defender and the ball at
	/// block-resolution time for a timing-correct block to still connect
	/// (issue #214). ResolveBlockAttempts composes this with the existing
	/// timing-only Succeeds predicate — BOTH must hold; this does not replace
	/// the timing check, it gates it spatially.
	///
	/// Without this gate, a defender anywhere on the court could "block" a
	/// shot purely on timing, deleting the spacing axis from the shot/block
	/// duel (CLAUDE.md §1 — "the duel is the space between two players").
	///
	/// Default 2.2 m reuses <see cref="ContestRange"/>'s own already-cited
	/// ADR-0014 real-ball anchor ("roughly an arm's-length closeout", issue
	/// #65) rather than inventing a new number for the same physical concept
	/// applied to a different defensive move. Provisional — feel tuning
	/// deferred to #104 + the per-milestone feel pass (ADR-0015); the number
	/// is a citable starting point, not a locked balance value.
	/// </summary>
	[Export] public float BlockReachRadius { get; set; } = 2.2f;

	// ── Contest tunables (M10, issue #99, ADR-0018 §2) ────────────────────

	/// <summary>
	/// Strength of the ADDITIONAL accuracy factor a committed on-ball contest
	/// applies on top of the existing passive proximity scatter (ADR-0009 /
	/// #65's <see cref="ContestScatterK"/>) when the contest's Active window
	/// overlaps the shot's release tick (see
	/// <see cref="DefensiveResolution.ContestAppliesAt"/>). Composed as
	/// <c>1 + ContestMoveScatterK</c> multiplied into the existing
	/// <c>accuracyMultiplier</c> chain in <c>ApplyShootLocally</c> — it never
	/// replaces the passive <see cref="ContestScatterK"/> term, only stacks
	/// on top of it (ADR-0018 §2's explicit composition rule).
	///
	/// ADR-0014 citation (real half-court ball, tier 2): a defender who
	/// actively closes out and times their pressure to the release is
	/// strictly harder to shoot over than one who merely stands nearby — the
	/// active commitment costs a Recovery window the passive proximity term
	/// never spends, so it earns a strictly larger penalty. 0.5 (giving an
	/// extra 1.5× on top of whatever the passive term already contributed) is
	/// a citable starting point, not a locked balance value — provisional,
	/// tuning deferred to #104 + the per-milestone feel pass (ADR-0015).
	/// </summary>
	[Export(PropertyHint.Range, "0,3,0.05")] public float ContestMoveScatterK { get; set; } = 0.5f;

	// ── Blow-by lane / whiff-punish tunables (issue #100, ADR-0018 Amendment 2026-07-16) ──

	/// <summary>
	/// Length, in physics ticks, of the beaten window a whiffed defensive
	/// committed move (a failed steal today; #196's failed transit steal is
	/// the next planned caller) grants the offense — see
	/// <see cref="PlayerController.TriggerBeatenWindow"/>. While active, the
	/// beaten defender's contest is suppressed against the handler's shot:
	/// both the committed <c>ContestMove</c> factor (<see
	/// cref="ContestMoveScatterK"/>) and the passive proximity scatter factor
	/// (<see cref="ContestScatterK"/>) are forced to 1.0 in
	/// <c>ApplyShootLocally</c>.
	///
	/// ADR-0014 citation (tier-1 identity, ADR-0018 §3): the default is
	/// <c>StealMove.DefaultFrameData.RecoveryFrames</c> (20 — the SAME cost
	/// the whiffing defender already pays themselves, keeping the reward
	/// commensurate with the miss rather than an arbitrary separate number)
	/// PLUS <c>ContestMove.DefaultFrameData.StartupFrames + ActiveFrames</c>
	/// (6 + 8 = 14) of margin, for 34 total. The margin is load-bearing, not
	/// decoration: the defender's OWN Recovery from the whiffed steal already
	/// occupies the first 20 ticks of this window (Begin() is illegal until
	/// Recovery elapses — CommittedMoveMachine's phase graph), so a window
	/// exactly equal to RecoveryFrames would close at the SAME tick Recovery
	/// releases the defender, leaving zero ticks in which a freshly-begun
	/// ContestMove's Active window could ever land — making the committed-
	/// ContestMove half of this mechanic (as opposed to the passive proximity
	/// half, which needs no new move) structurally unreachable in real play,
	/// only demonstrable by directly forcing the window in a test. The +14
	/// margin guarantees a ContestMove begun the INSTANT Recovery elapses
	/// still has its entire Active window fall inside the beaten window.
	/// Provisional — tuning deferred to #104 + the per-milestone feel pass
	/// (ADR-0015), same as every other defensive magnitude in this file.
	/// </summary>
	[Export] public int BlowByWindowTicks { get; set; } =
		StealMove.DefaultFrameData.RecoveryFrames
		+ ContestMove.DefaultFrameData.StartupFrames
		+ ContestMove.DefaultFrameData.ActiveFrames;

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
	/// Per-possession dead-dribble flag (#193, ADR-0008 dead-dribble amendment).
	/// False means the current possession's dribble is still LIVE; true means
	/// it has been cradled (a JumpShot/pump-fake Startup — see
	/// CradleForShotStartup) and StartDribble() is refused for the rest of
	/// this possession (DeadDribbleRule).
	///
	/// Reset to false on every possession change (AwardPossession) and on the
	/// tipoff (TryAssignTipoffHolder) — never independently on "score", because
	/// a make-it-take-it reset IS a possession change and already routes
	/// through AwardPossession.
	///
	/// Broadcast alongside IsCleared (same ReceiveState payload, same
	/// unconditional force-correct in ReconcileFromServer) — NOT the
	/// "predicted-everywhere, never corrected" treatment an earlier draft of
	/// this issue tried. Reasoning (doubt-driven review, #193): every OTHER
	/// discrete identity write in this class (StateMachine.Current/HolderPeerId)
	/// gets force-corrected on a client/server disagreement via ForceState —
	/// but that correction can re-point HolderPeerId at a DIFFERENT peer (e.g.
	/// the client mispredicted which player won a loose-ball scramble) while
	/// leaving THIS flag holding whatever value it last had for a completely
	/// different possession. Unlike IsCleared's already-accepted <=1-RTT
	/// cosmetic window, an uncorrected stale HasDribbled=true would wrongly
	/// refuse a legitimate drive attempt for the REST of that possession, not
	/// just a sub-frame — broadcasting it closes that gap the same way
	/// IsCleared already closes its own.
	/// </summary>
	public bool HasDribbled { get; private set; }

	/// <summary>
	/// Server-only: has the current holder been INSIDE the clear line at some point
	/// during this possession? Crossing-detection for the take-it-back rule (#135):
	/// a possession clears only on a genuine take-back (inside → behind), not by
	/// merely standing behind the line on recovery (an offensive rebound from behind
	/// the arc). Reset to false on every change of possession (AwardPossession),
	/// advanced by UpdateClearStatus via ClearLine.Advance. Never broadcast — only
	/// the resulting IsCleared flag is (clients never compute the flip; see IsCleared).
	/// </summary>
	private bool _holderHasBeenInsideClearLine;

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
	/// edge-detector that fires the once-per-possession reset (AdvanceHandSweep,
	/// formerly UpdateHandSide — renamed when #195 folded the crossover ball
	/// sweep trigger into the same method).
	/// </summary>
	private int _handSideHolderId;

	/// <summary>
	/// The holder's HandSide as of the last AdvanceHandSweep call (#195) — the
	/// OTHER edge-detector alongside _handSideHolderId. Comparing this against
	/// the current tick's holder.HandSide (itself already broadcast +
	/// reconciled, same as HandSign reads) is how a same-holder crossover flip
	/// is told apart from a possession-change reset: _handSideHolderId changing
	/// means "new holder, snap" (rule 2); this changing while
	/// _handSideHolderId stays put means "same holder crossed over, sweep"
	/// (rule 1). Null before any holder has ever been observed.
	/// </summary>
	private HandSide? _lastObservedHandSide;

	/// <summary>True while a crossover ball sweep (#195) is interpolating the lateral hand offset.</summary>
	private bool _sweepActive;

	/// <summary>Ticks elapsed since the current sweep started (0 on the trigger tick itself).</summary>
	private int _sweepTicks;

	/// <summary>
	/// Sweep length in ticks, derived from CrossoverSweepDuration at the
	/// moment the sweep (re)started — computed via the physics tick rate
	/// (Engine.PhysicsTicksPerSecond) so the sweep is a deterministic tick
	/// count (ADR-0004), identical on every peer, rather than a wall-clock
	/// timer that could drift between machines.
	/// </summary>
	private int _sweepDurationTicks;

	/// <summary>Lateral factor the current sweep started from (an old HandSign, or an in-progress sweep's position on a re-cross — rule 3).</summary>
	private float _sweepFromLateral;

	/// <summary>Lateral factor the current sweep is travelling toward (the new HandSign).</summary>
	private float _sweepToLateral;

	/// <summary>
	/// Which transit path the CURRENT sweep is playing — Crossover's in-front,
	/// BehindTheBack's behind-body/shielded (#194), or BetweenTheLegs's
	/// through-the-legs (#199). Captured ONCE, at the tick the flip is
	/// detected (see AdvanceHandSweep rule 1), from the holder's
	/// DisplayMoveId() — never re-read mid-sweep, so a move finishing its
	/// Active phase partway through the (shorter) sweep animation cannot flip
	/// which path this in-flight sweep renders as. Was a bare bool
	/// (_sweepIsBehindBody) before #199 added the third path.
	/// </summary>
	private BallSweepPath _sweepPath = BallSweepPath.InFront;

	/// <summary>
	/// The in-flight (or loose) trajectory. Non-null only while InFlight or
	/// Loose — it carries the position+velocity the integrator advances. Reused
	/// for the loose fall so a bounced ball keeps moving under gravity.
	/// </summary>
	private ShotArc _arc;

	// ── Block tracking (M10, issue #98) ───────────────────────────────────

	/// <summary>
	/// Monotonically-increasing physics tick number, used for block
	/// resolution's absolute-tick interval arithmetic (issue #216 finding —
	/// original body row 1). Reads the ENGINE's own physics-frame counter
	/// (<see cref="Engine.GetPhysicsFrames"/>) instead of a hand-rolled
	/// per-node field: Godot already increments this exactly once per physics
	/// tick, in lockstep with every node's own _PhysicsProcess call, so a
	/// second, independently-incremented counter was pure duplication (and a
	/// latent two-clocks-different-epochs desync risk — see the doubt-cycle
	/// note below).
	///
	/// Still purely LOCAL in the sense that matters here: each peer's process
	/// has its own engine instance and its own physics-frame count, and
	/// nothing synchronizes that VALUE across peers. That's fine because its
	/// only consumer, ResolveBlockAttempts, is gated `IsServer`-only: every
	/// absolute-tick interval it computes (the defender's Active window, the
	/// shot's vulnerable window) is compared entirely within the server's own
	/// reading of this same property, so no cross-peer property is ever
	/// needed.
	///
	/// (Doubt cycle, issue #216 row 1) The one real risk this substitution
	/// introduces: Engine.GetPhysicsFrames() returns ulong, narrowed to int
	/// here to match every existing downstream consumer (_inFlightStartTick,
	/// DefensiveResolution.Succeeds' int parameters, PlayerController's int
	/// FrameInPhase). This is NOT a new overflow risk — the OLD hand-rolled
	/// `_physicsTick++` was already an unbounded int counter with the exact
	/// same eventual wraparound behaviour (~1.13 years of continuous ticks at
	/// 60 Hz) — and it is NOT an epoch-mismatch risk either: every use site
	/// below is a DIFFERENCE or an interval-overlap comparison between two
	/// readings of THIS SAME property, never a comparison against a literal
	/// or another clock, so shifting the epoch from "0 at this node's first
	/// tick" to "whatever the engine's count already was" changes no
	/// arithmetic outcome. Grep-verified: no test or harness reads this tick
	/// number directly or assumes it starts near 0.
	///
	/// Why the underlying counter exists at all: the block uses
	/// DefensiveResolution.Succeeds with two REAL intervals (the defender's
	/// Active window and the shot's vulnerable window). The steal reduces to
	/// a point-in-time test, so it doesn't need an absolute clock. The block
	/// check runs every InFlight tick, so we need the defender's entry tick
	/// (derived from FrameInPhase + currentTick) and the shot's InFlight
	/// start tick. This property supplies those values.
	/// </summary>
	private int PhysicsTick => (int)Engine.GetPhysicsFrames();

	/// <summary>
	/// The physics tick on which the ball last transitioned to InFlight (a shot
	/// was released). Set in ApplyShootLocally (called by CheckJumpShotRelease)
	/// right after StateMachine.Shoot() succeeds.
	///
	/// It is reset to -1 in exactly one place: ResolveBlockAttempts' success
	/// branch, when a block turns the shot into a Loose ball. There is no reset
	/// on a miss/make or any other InFlight exit — the value is simply
	/// overwritten the next time a shot is released, and in the meantime it sits
	/// inert behind the `StateMachine.Current != BallState.InFlight` guard at
	/// the top of ResolveBlockAttempts (State isn't InFlight, so the stale value
	/// is never read as a live window). -1 means "no shot has ever been
	/// released yet" and also causes ResolveBlockAttempts to return immediately.
	///
	/// Any FUTURE path that transitions the ball into InFlight without going
	/// through ApplyShootLocally would inherit whatever stale value this field
	/// last held — worth flagging at that call site if one is ever added.
	///
	/// The block vulnerable window is [_inFlightStartTick, _inFlightStartTick + BlockGraceTicks).
	/// </summary>
	private int _inFlightStartTick = -1;

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
	private bool _serverHasDribbled;

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
	/// The peer id of the last player to POSSESS (touch) the ball — updated on
	/// EVERY possession change: the tipoff (TryAssignTipoffHolder) and every
	/// AwardPossession (rebound, make-it-take-it, OOB award, carry turnover).
	/// Distinct from <see cref="_lastShooterPeerId"/>, which moves only on a shot.
	///
	/// Drives the loose-ball OOB turnover (TickLoose → OobResolution.ResolveRecipient,
	/// issue #118, ADR-0008 §Amendment 2026-06-30): the streetball "last-toucher-out
	/// → other ball" rule. Keying the award off the toucher (not the shooter) stops
	/// a rebounder who fumbles the ball OOB from being handed it straight back —
	/// the last-shooter field never updated on a rebound, so it would.
	///
	/// Server-authoritative in effect: only the server issues an OOB Award
	/// (OobResolution gates Award on isServer), so only the server's value drives
	/// a real turnover. It needs no broadcast — like _lastShooterPeerId, the
	/// RESULT (a possession change) is what ReceiveState carries. 0 until the
	/// first possession (pre-tipoff), which ResolveRecipient treats as "no
	/// turnover basis" (clamp, not an arbitrary award).
	///
	/// (#177 audit R1, amended) NOT set on every peer: the steal-turnover write
	/// in ResolveStealAttempts (below) is a server-ONLY path into this field —
	/// GoLoose() bypasses AwardPossession entirely (a live loose-ball scramble,
	/// not a discrete Catch/Turnover edge), so the write happens once, on the
	/// server's own copy, with no client-prediction counterpart. This is
	/// currently safe DESPITE the exception: the sole consumer,
	/// OobResolution.Award, is itself gated on isServer, and TickLoose's
	/// client-side read of BallState never branches on this field. It would
	/// stop being safe the day a client-side consumer is added — that consumer
	/// would need its own reconciliation, not an assumption that this field is
	/// already in sync (do NOT add a broadcast preemptively; the two known
	/// current writers already have all the server-authority guarantees they need).
	/// </summary>
	private int _lastToucherPeerId;

	/// <summary>
	/// Test-only: exposes <see cref="_lastToucherPeerId"/> for the headless
	/// integration harness (ADR-0016). The field itself must stay private —
	/// production code never reads it from outside this class — but the steal
	/// turnover harness (issue #96 remediation) needs to prove the defender,
	/// not the offensive holder, is charged as the last toucher after a steal,
	/// since that value is otherwise only observable indirectly (an actual OOB
	/// roll, which the harness does not simulate).
	/// </summary>
	internal int LastToucherPeerIdForHarness => _lastToucherPeerId;

	/// <summary>
	/// Test-only: exposes <see cref="_dribble"/>'s current Phase for the
	/// headless integration harness (ADR-0016, issue #176). Proves the live
	/// engine — not just the pure DribbleCycle unit tests — actually resets
	/// the phase at the moment AwardPossession fires after a scramble
	/// recovery, which is the exact mechanism that closes the #176 re-steal
	/// exploit (a frozen in-band phase resuming from where it froze instead
	/// of restarting at 0).
	/// </summary>
	internal float DribblePhaseForHarness => _dribble.Phase;

	/// <summary>
	/// Test-only: exposes whether a crossover ball sweep (#195) is currently
	/// interpolating. The harness needs this as a direct proof that a
	/// possession-change hand reset produces NO sweep (rule 2) — position
	/// alone can't distinguish "reset straight to the default hand" from "a
	/// sweep that happens to already be at its endpoint", since both look
	/// identical from outside once settled.
	/// </summary>
	internal bool SweepActiveForHarness => _sweepActive;

	/// <summary>
	/// Test-only: exposes whether the CURRENT sweep is a BehindTheBack
	/// (behind-body) transit rather than a Crossover (in-front) one (#194) —
	/// the harness discriminator between the two ball-transit paths. Kept
	/// (rather than removed when #199 added a third path) since
	/// BehindTheBackTest.cs already depends on this exact bool contract;
	/// SweepPathForHarness below is the 3-way version new harness code should
	/// use.
	/// </summary>
	internal bool SweepIsBehindBodyForHarness => _sweepPath == BallSweepPath.BehindBody;

	/// <summary>
	/// Test-only: exposes which of the three transit paths the CURRENT sweep
	/// is playing (#199) — Crossover's in-front, BehindTheBack's behind-body,
	/// or BetweenTheLegs's through-the-legs. Added alongside
	/// SweepIsBehindBodyForHarness (kept for BehindTheBackTest.cs's existing
	/// bool contract) so new harness code can discriminate all three paths
	/// without an "isBehindBody but somehow also not InFront" ambiguity a
	/// second bool would have needed.
	/// </summary>
	internal BallSweepPath SweepPathForHarness => _sweepPath;

	/// <summary>
	/// Test-only: exposes the current ball velocity (issue #98) — the block
	/// harness needs to prove a successful block's swat velocity (away from
	/// the rim, downward) actually overwrote the shot's original toward-the-rim
	/// arc velocity, which is otherwise only observable indirectly through the
	/// resulting trajectory over several ticks. Delegates to the existing
	/// private CurrentVelocity() (used by the ReceiveState broadcast) so the
	/// harness reads the exact same value every peer already reconciles from.
	/// </summary>
	internal Vector3 VelocityForHarness => CurrentVelocity();

	/// <summary>
	/// Test-only: the ADDITIONAL committed-contest accuracy factor computed
	/// for the most recently resolved shot's scatter (issue #99). 1.0 (no
	/// effect) whenever no committed contest was Active at release — the same
	/// value the shot's own accuracyMultiplier composed <c>contestFactor</c>
	/// (passive proximity, #65) with. Otherwise-observable only indirectly
	/// through the RNG-influenced landing spot, which is why this exposes the
	/// live-computed factor directly — the same *ForHarness pattern as
	/// <see cref="LastToucherPeerIdForHarness"/>/<see cref="VelocityForHarness"/>.
	/// Set once per shot inside ApplyShootLocally's <c>IsServer &amp;&amp;
	/// ShotScatterEnabled</c> block; stays at its constructed default (1f) for
	/// any shot resolved before that block ever runs (e.g. ShotScatterEnabled
	/// == false, or before the first shot of a harness run).
	/// </summary>
	internal float LastContestMoveFactorForHarness { get; private set; } = 1f;

	/// <summary>
	/// Test-only: the PASSIVE proximity contest factor (ADR-0009 / #65,
	/// exported as <see cref="ContestScatterK"/>/<see cref="ContestRange"/>)
	/// computed for the most recently resolved shot — the sibling to
	/// <see cref="LastContestMoveFactorForHarness"/>, added for issue #100 so
	/// the harness can prove a beaten window suppresses BOTH the passive
	/// scatter term AND the committed ContestMove term, not just one of the
	/// two the issue is explicit about. 1.0 whenever no defender was in
	/// range (or a beaten window forced it), exactly like the sibling.
	/// </summary>
	internal float LastContestFactorForHarness { get; private set; } = 1f;

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
		// The state machine's own constructor already starts Held (#193: every
		// possession — the tipoff included — now starts Held-not-Dribbling, so
		// there is no separate "begin dribbling" step to run here). With
		// holder 0 TickHeld tracks the world origin until the server assigns a
		// holder (a <=1-tick window once a player exists) — the SAME pre-holder
		// behaviour the listen-server already had between scene load and the
		// Host button press.
		StateMachine = new BallStateMachine(initialHolderPeerId: 0);

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

		// PhysicsTick (issue #216 row 1) reads Engine.GetPhysicsFrames()
		// directly — the engine already increments it once per physics tick,
		// so there is nothing to increment here anymore (see PhysicsTick's
		// own doc for why removing the hand-rolled counter is safe).

		// Fixed timestep — NOT the variable wall-clock delta — so the arc is
		// deterministic and reproducible identically on server and every client.
		float dt = 1.0f / Engine.PhysicsTicksPerSecond;

		// Server-only: resolve block attempts BEFORE the per-state tick so that
		// a successful block (GoLoose from InFlight) prevents TickInFlight from
		// running on the same tick. This ordering is CRITICAL for the
		// "blocked shot cannot score" correctness requirement (issue #98):
		//
		//   Without pre-switch block: TickInFlight runs first → rim contact can
		//   trigger RegisterBasket → THEN block GoLoose → basket already counted.
		//
		//   With pre-switch block: if block succeeds, State transitions to Loose
		//   BEFORE the switch → switch falls into TickLoose instead of TickInFlight
		//   → RegisterBasket is never reached (ADR-0008 Amendment 2026-06-30).
		//
		// Contrast with steal (ResolveStealAttempts after the switch): steal targets
		// Dribbling state which has no make-detection code, so ordering doesn't matter
		// for steal. Block specifically interrupts InFlight's scoring path.
		if (IsServer)
			ResolveBlockAttempts();

		switch (State)
		{
			case BallState.Held:      TickHeld();        break;
			case BallState.Dribbling: TickDribbling(dt); break;
			case BallState.InFlight:  TickInFlight(dt);  break;
			case BallState.Loose:     TickLoose(dt);     break;
		}

		// (issue #216 original body row 5) The release-tick top-up call that
		// used to live here was moved into ApplyShootLocally itself — see that
		// method's own comment for why. Nothing replaces it at this call
		// site: the pre-switch call above already covers every OTHER InFlight
		// tick, and the release tick is now resolved from inside the switch,
		// the moment the ball actually becomes InFlight.

		// Server-only: assign the tipoff holder once a player node exists but the
		// ball has none yet (ADR-0007). The broadcast below propagates the holder
		// to every client; clients never compute it themselves.
		if (IsServer
			&& StateMachine.HolderPeerId == 0
			&& (State == BallState.Held || State == BallState.Dribbling))
		{
			TryAssignTipoffHolder();
		}

		// Everything below is server-only authoritative resolution, run under a
		// single guard (the order matters and is documented per step):
		if (IsServer)
		{
			// Turn the ball over if the BALLHANDLER has crossed the court line
			// (player-OOB rule). Runs before UpdateClearStatus so the clear check
			// evaluates the NEW holder, not the one being dispossessed.
			ResolvePlayerOutOfBounds();

			// Resolve defensive steal attempts (M10, issue #96, ADR-0018). Runs
			// after the main state-switch so the dribble phase is already advanced
			// this tick. A successful steal transitions Dribbling→Loose; the
			// existing TickLoose scramble awards possession next tick (ADR-0008
			// §Amendment 2026-06-30).
			ResolveStealAttempts();

			// Resolve the whiff-punish blow-by lane (issue #100, ADR-0018
			// Amendment 2026-07-16). Deliberately its OWN call, not folded into
			// ResolveStealAttempts: that method early-returns unless the ball is
			// currently Dribbling, but a defender's committed move can whiff on
			// ANY tick regardless of what the ball is doing right now (e.g. the
			// holder released a shot mid-attempt) — the beaten window is a
			// property of the DEFENDER's own machine, not the ball's state, so
			// its detection must not inherit ResolveStealAttempts's ball-state
			// guard. Runs every server tick, independent of ball state.
			ResolveBeatenWindowTriggers();

			// Clear the possession once the handler carries the ball back behind
			// the clear line (#50). Clients receive the flag in the broadcast
			// below, never compute it.
			UpdateClearStatus();

			// Broadcast authoritative truth. Every peer (server included) already
			// predicted its own copy above; this broadcast is what every OTHER
			// peer reconciles against. IsCleared rides the same per-tick snapshot
			// as the holder it belongs to — continuously resent, so a dropped
			// packet self-heals on the next tick.
			Rpc(MethodName.ReceiveState,
				(int)StateMachine.Current, GlobalPosition, CurrentVelocity(), StateMachine.HolderPeerId, IsCleared, HasDribbled);
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

				// First touch of the match: the tipoff holder is now the last
				// toucher, so a loose ball that goes OOB before any shot is
				// awarded opposite them (#118, last-toucher-out rule) rather
				// than clamped for lack of possession history.
				_lastToucherPeerId = peerId;

				// The opening possession is pre-cleared (ADR-0008): the
				// take-it-back rule applies "on every change of possession and
				// after every made basket" — the tipoff is neither, so the
				// first basket must be allowed to count without a take-back.
				IsCleared = true;

				// The tipoff is also a fresh possession start (#193): its
				// dribble is live. ForceState above kept whatever State this
				// ball already was — Held by the state machine's own
				// constructor default — so this only needs to reset the flag,
				// not the discrete ball state.
				HasDribbled = false;

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
	/// actually carried the ball back behind the clear line (#50, ADR-0008). Only
	/// checked while a player holds the ball (Held/Dribbling) and only until it
	/// flips — once cleared, the possession stays cleared until the next change of
	/// possession resets it (AwardPossession). One-way within a possession, so
	/// stepping back inside the line after clearing does not un-clear it.
	///
	/// Uses crossing-detection (#135), not a static position test: a holder must
	/// have been inside the line this possession and then carry the ball back
	/// behind it. Recovering a loose ball while already behind the line (an
	/// offensive rebound from behind the arc) is NOT a take-back and does not clear
	/// — the rule decision lives in the pure, headless-tested ClearLine.Advance.
	/// </summary>
	private void UpdateClearStatus()
	{
		if (IsCleared) return;
		if (State != BallState.Held && State != BallState.Dribbling) return;
		if (StateMachine.HolderPeerId == 0) return; // no holder to measure (pre-tipoff)

		bool cleared;
		(cleared, _holderHasBeenInsideClearLine) = ClearLine.Advance(
			IsCleared, _holderHasBeenInsideClearLine, HolderPosition(), RimCenter, ClearLineDistance);
		IsCleared = cleared;
	}

	/// <summary>
	/// Server-only: turn the ball over when the BALLHANDLER crosses the court
	/// line — the half-court 1v1 analogue of stepping out of bounds with the
	/// ball. Mirrors the loose-ball OOB rule (TickLoose + OobResolution) but keyed
	/// on the HOLDER's position instead of the ball's: a dead-ball turnover awarded
	/// directly to the opponent, uncleared (they must take it back before they can
	/// score — ADR-0008 §Amendment 2026-06-21).
	///
	/// ── Why the court line and not the walls ──────────────────────────────
	/// The scene walls sit well OUTSIDE this line (≈ X ±10 vs the court's X ±7.62),
	/// so they act only as a far backstop; the turnover fires at the court line
	/// long before a player could reach a wall. The deterministic ball ignores the
	/// walls entirely (ADR-0004) — this rule, not a collider, is what makes leaving
	/// the court matter.
	///
	/// ── Why server-authoritative ──────────────────────────────────────────
	/// Same authority boundary as UpdateClearStatus and the loose-ball OOB award:
	/// only the server holds truthful player positions, and gating the award on the
	/// server removes prediction-flip risk (two peers briefly disagreeing on the new
	/// holder) for zero gameplay cost — a dead-ball ruling has no 50/50 proximity
	/// contest to predict, unlike a rebound. A client keeps its predicted possession
	/// for up to one RTT until the broadcast corrects it; any shot it fires in that
	/// window is reconciled away — the same accepted cost as the live-rebound
	/// contest (ADR-0002).
	///
	/// The decision table is OobResolution.Resolve, reused verbatim from the
	/// loose-ball path: OOB + server + opponent-present → Award; otherwise NoOp /
	/// ClampFallback. ClampFallback here means "no eligible recipient" (a solo
	/// editor test, or the opponent is unreachable — see the recipient gate
	/// below): we never clamp a PLAYER (only the ball is clamped), so a lone holder
	/// may roam past the line with no turnover — there is nobody to award it to.
	///
	/// ── Why the recipient must be present AND in-bounds ───────────────────
	/// The award only fires if the opponent's node exists and is itself inside the
	/// court. Two reasons, both load-bearing:
	///   1. Both players OOB: awarding to an also-OOB opponent would turn the ball
	///      straight back the very next tick — a 60 Hz possession strobe. Gating on
	///      the recipient being in-bounds breaks the ping-pong: nobody is awarded
	///      until one player steps back inside.
	///   2. Disconnected/ghost opponent: a peer whose PlayerController has been
	///      freed must not be handed the ball (it would park at the origin forever).
	///      Requiring a live Node3D fails safe — no turnover rather than a lost ball.
	/// When the recipient is ineligible we pass recipient = 0, which OobResolution
	/// maps to ClampFallback → no award this tick.
	/// </summary>
	private void ResolvePlayerOutOfBounds()
	{
		if (State != BallState.Held && State != BallState.Dribbling) return;
		if (StateMachine.HolderPeerId == 0) return; // no holder yet (pre-tipoff)

		int opponent = OtherPlayerPeerId(StateMachine.HolderPeerId);

		// Recipient eligibility: the opponent must be a live node AND in-bounds to
		// receive the turnover (see doc "Why the recipient must be present AND
		// in-bounds"). Ineligible → recipient 0 → OobResolution yields no award.
		bool recipientCanReceive =
			opponent != 0
			&& Players?.GetNodeOrNull(opponent.ToString()) is Node3D opp
			&& !CourtBounds.IsOutOfBounds(opp.GlobalPosition, CourtMin, CourtMax);

		OobResolution.Result oob = OobResolution.Resolve(
			CourtBounds.IsOutOfBounds(HolderPosition(), CourtMin, CourtMax),
			IsServer,
			recipientCanReceive ? opponent : 0);

		if (oob.Action == OobResolution.Action.Award)
			AwardPossession(oob.RecipientPeerId); // uncleared turnover
	}

	/// <summary>
	/// Server-only: resolve any steal attempts by defenders this physics tick.
	///
	/// Called after the main state-switch (so the dribble phase is already
	/// advanced for this tick), before the OOB and clear checks.
	///
	/// A steal succeeds iff, on ANY tick the defender's committed-move machine is
	/// in the Active phase of a StealMove, both steal axes pass (ADR-0018 §2):
	///   Axis 1 — dribble phase in the exposed band [StealLoExposed, StealHiExposed]
	///   Axis 2 — defender's TargetHand matches the holder's authoritative
	///             HandSide (ADR-0012 — read from PlayerController, never from
	///             the cosmetic FacingResolver)
	///
	/// This is checked EVERY Active tick, not just the entry tick: ADR-0018
	/// defines success as the Active window OVERLAPPING the exposed band (an
	/// interval), so sampling only the entry tick (the merged #96 bug) collapsed
	/// that to a point and left the Active window's width inert. Re-reading the
	/// live _dribble.Phase each Active tick evaluates the interval model against
	/// ground-truth phase — no projection, no duplicated dribble math — and
	/// steals on the first in-band, matching-hand tick.
	///
	/// On success: GoLoose() once — the ball transitions Dribbling → Loose,
	/// seeded with a provisional knock velocity (see below) — plus the
	/// defender's StealMove Active phase is ended early via EndResolvedDefensiveMove()
	/// so this StealMove cannot resolve a second time (issue #96 remediation's
	/// multi-fire guard — see the comment at the success branch). The existing
	/// TickLoose proximity scramble then awards possession to whichever player
	/// reaches the ball first (ADR-0008 §Decision-2, §Amendment 2026-06-30:
	/// steal is a defense-induced Dribbling→Loose turnover; the new holder is
	/// chosen by the scramble, not awarded immediately).
	///
	/// On a whiff: no action. The defender's CommittedMoveMachine continues
	/// through Recovery naturally — the punish window is implicit, not wired here.
	/// </summary>
	private void ResolveStealAttempts()
	{
		if (StateMachine.Current != BallState.Dribbling) return;
		if (Players == null) return;

		var holder = Players.GetNodeOrNull(StateMachine.HolderPeerId.ToString()) as PlayerController;
		if (holder == null) return;

		foreach (Node child in Players.GetChildren())
		{
			if (child is not PlayerController defender) continue;
			if (defender == holder) continue; // can't steal from yourself

			// ActiveMove<StealMove>() returns the live move on EVERY tick the
			// defender's machine is in the Active phase of a StealMove; null on
			// all other ticks (fast path — no allocation). Reading it each Active
			// tick is what turns the point sample into the interval overlap the
			// spec requires (see PlayerController.ActiveMove's doc / issue #96).
			var stealActive = defender.ActiveMove<StealMove>();
			if (stealActive == null) continue;
			HandSide targetHand = stealActive.Value.Move.TargetHand;

			// Two-axis steal check (ADR-0018 §2, issue #96), re-evaluated against
			// the live dribble phase THIS tick: DefensiveResolution.StealSucceeds
			// checks side first (fast exit if wrong hand), then the phase band.
			// Pure method, no engine calls — same result on server AND any
			// predicting client (ADR-0004).
			bool success = DefensiveResolution.StealSucceeds(
				_dribble.Phase,
				StealLoExposed, StealHiExposed,
				targetHand, holder.HandSide);

			if (success)
			{
				// GoLoose exactly once — the ball is now a live loose-ball contest.
				// Dribbling → Loose routes into the existing TickLoose scramble
				// (ADR-0008 §Amendment 2026-06-30). The new holder is awarded by
				// the scramble's proximity check next tick, not here. The
				// Dribbling guard at the top of this method blocks any re-resolution
				// on later Active ticks once the ball has already gone Loose.
				StateMachine.GoLoose();

				// Seed _arc: GoLoose() only flips the discrete BallState. Every
				// OTHER path into Loose arrives FROM InFlight, where Shoot()
				// already constructed _arc; Dribbling never touches _arc at all.
				// This steal is the first Dribbling→Loose path, and TickLoose's
				// very first line dereferences _arc unconditionally — an
				// unseeded _arc here crashes the server with a
				// NullReferenceException on the very next physics tick
				// (confirmed via the headless harness, issue #96 remediation).
				//
				// The knock is a provisional straight-line "poked toward the
				// defender, with a small rise" velocity, not a real deflection
				// model (ADR-0014 — real 1v1 ball: a stolen ball is knocked away
				// from the handler, not left inert; precise feel tuning deferred
				// to #104). ShotArc's constructor solves a velocity aimed at a
				// target point, which has no meaning for a knock, so it is built
				// with a degenerate release==target pair purely to obtain a
				// valid Gravity-carrying instance (this yields Velocity = Zero,
				// verified: equal release/target collapses every term in
				// SolveInitialVelocity to zero, no divide-by-zero), then
				// Velocity is overwritten directly — the same seed-then-overwrite
				// pattern ReconcileFromServer already uses for a client's null _arc.
				// DefensiveKnockDirection.SafeHorizontal (issue #216 row 4) —
				// shared with the block swat direction math below. Degenerate
				// (holder/defender coincident) falls back to Zero — rise only.
				Vector3 knockDir = DefensiveKnockDirection.SafeHorizontal(defender.GlobalPosition - GlobalPosition);
				_arc = new ShotArc(GlobalPosition, GlobalPosition, GlobalPosition.Y, Gravity);
				_arc.Velocity = new Vector3(
					knockDir.X * StealKnockSpeed,
					StealKnockRiseSpeed,
					knockDir.Z * StealKnockSpeed);

				// The defender is now the last toucher (#118 rule, mirrors the
				// comment in AwardPossession): this steal bypasses AwardPossession
				// entirely (GoLoose keeps the ball in a live loose-ball contest,
				// not a discrete Catch/Turnover edge), so without this line
				// _lastToucherPeerId stays pinned to the offensive holder. A
				// knocked ball that sails OOB before the scramble recovers it
				// would then charge the OOB turnover to the offense again
				// instead of the defender who just touched it — backwards from
				// the "last-toucher-out" rule this steal itself just triggered.
				// (#177 audit R4) `&& defenderPeerId != 0` matches every other
				// name-parse site in this class (peer 0 is the "no possession
				// history" sentinel ResolveRecipient clamps on) — unreachable in
				// practice since NetworkManager.SpawnPlayer can never name a node
				// "0", but this site was the one inconsistent outlier of four.
				if (int.TryParse(defender.Name, out int defenderPeerId) && defenderPeerId != 0)
					_lastToucherPeerId = defenderPeerId;

				// End the defender's Active phase NOW instead of letting it ride
				// out the remaining ActiveFrames (issue #96 multi-fire bug):
				// DribbleCycle.Phase only advances in TickDribbling, so it is
				// frozen at the in-band value read above for as long as the ball
				// stays Loose. If TickLoose's proximity scramble re-awards the
				// ball to this SAME holder before the Active window would have
				// naturally expired, this method re-enters next tick and would
				// see Dribbling + the same frozen in-band phase + matching hand
				// again — firing GoLoose() a second time for one committed move.
				// EndResolvedDefensiveMove caps it at exactly one turnover per
				// StealMove, then the defender pays Recovery like any other
				// spent move.
				defender.EndResolvedDefensiveMove();

				GD.Print($"[BallController] Steal success: defender {defender.Name}, " +
						 $"phase {_dribble.Phase:F2}, hand {holder.HandSide}");
				return; // only one steal resolves per tick
			}
			// Whiff this tick: keep checking subsequent Active ticks (the loop
			// re-enters next physics tick). Recovery is the natural punishment for
			// a fully-missed window; not wired here.
		}
	}

	/// <summary>
	/// Server-only: grants the whiff-punish blow-by lane (issue #100,
	/// ADR-0018 Amendment 2026-07-16) — a bounded contest-free window for the
	/// offense — the tick a defender's committed StealMove naturally expires
	/// from Active into Recovery without ever having succeeded.
	///
	/// This is intentionally the ONLY caller of
	/// <see cref="PlayerController.TriggerBeatenWindow"/> today, but the API
	/// itself is generic (see that method's doc) — a future caller (#196's
	/// failed crossover-transit steal) is expected to call the SAME method
	/// from its own resolution site, not invent a parallel mechanism. This
	/// method's job is narrowly "detect a natural StealMove whiff and trigger
	/// the reusable lane," not "own the lane."
	///
	/// Runs over every player every tick regardless of ball state — see the
	/// call site's comment for why this cannot share ResolveStealAttempts's
	/// Dribbling-only guard.
	/// </summary>
	private void ResolveBeatenWindowTriggers()
	{
		if (Players == null) return;

		foreach (Node child in Players.GetChildren())
		{
			if (child is not PlayerController defender) continue;

			if (defender.JustWhiffedDefensiveMove<StealMove>())
			{
				defender.TriggerBeatenWindow(PhysicsTick, BlowByWindowTicks);
				GD.Print($"[BallController] Blow-by lane granted: defender {defender.Name} whiffed a steal, " +
						 $"beaten through tick {PhysicsTick + BlowByWindowTicks}.");
			}
		}
	}

	/// <summary>
	/// Server-only: resolve block attempts by defenders every InFlight tick.
	///
	/// Two call sites cover every InFlight tick between them (issue #216
	/// original body row 5): the pre-switch call in _PhysicsProcess (every
	/// tick State is ALREADY InFlight going in), and a release-tick-only call
	/// inside ApplyShootLocally itself (the one tick State BECOMES InFlight,
	/// which the pre-switch call — running while State was still
	/// Held/Dribbling — could not observe). Neither call site can double-
	/// resolve the same tick: on any given tick exactly one of them sees
	/// State == InFlight.
	///
	/// Called BEFORE the per-state switch in _PhysicsProcess so that a successful
	/// block (GoLoose from InFlight) prevents TickInFlight from running this tick —
	/// which is the only way to guarantee a blocked shot cannot score. If block
	/// resolution ran AFTER TickInFlight, rim contact could trigger RegisterBasket
	/// on the same tick the block connected. (ADR-0008 Amendment 2026-06-30.)
	///
	/// Success condition (ADR-0018 §2 — full interval form):
	///   DefensiveResolution.Succeeds(blockActiveStart, blockActiveEnd,
	///                                inFlightStartTick, inFlightStartTick + BlockGraceTicks)
	///
	/// where blockActiveStart = PhysicsTick − FrameInPhase (the absolute tick the
	/// defender entered Active) and blockActiveEnd = blockActiveStart + ActiveFrames.
	///
	/// This is the INTERVAL FORM (not the point-in-band form used by steal):
	/// the defender's entire Active window is checked against the shot's vulnerable
	/// window, so a defender who entered Active slightly before or after the release
	/// can still block if their window overlaps the grace period.
	///
	/// Phrasing note: ADR-0018 §2 writes the vulnerable window as
	/// [JumpShot.Active start, InFlight start + BlockGraceTicks), while this
	/// method uses [_inFlightStartTick, _inFlightStartTick + BlockGraceTicks).
	/// These are the SAME tick by construction, not two different quantities —
	/// the ball releases on JumpShot's JustEnteredActive (its first Active
	/// tick; see JumpShotReleaseResolver / PlayerController.
	/// JustReleasedJumpShot), so JumpShot.Active start == InFlight start. This
	/// equivalence holds only as long as release continues to fire on Active
	/// entry; if a future change decouples them, the two phrasings would need
	/// to be reconciled explicitly.
	///
	/// On success: GoLoose() once — InFlight → Loose (ADR-0008 Amendment 2026-06-30).
	/// The existing TickLoose proximity scramble then awards possession next tick.
	///
	/// On a whiff: no action. The defender pays Recovery naturally.
	///
	/// Spatial gate (issue #214, ADR-0014): timing alone (#98/ADR-0018 §2) let
	/// a defender anywhere on the court "block" a shot, deleting the spacing
	/// axis from the shot/block duel. Success now ALSO requires
	/// DefensiveResolution.WithinBlockReach(defender.GlobalPosition,
	/// GlobalPosition, BlockReachRadius) — a defender's Active window
	/// overlapping the vulnerable window is necessary but no longer
	/// sufficient; they must also be within BlockReachRadius (default 2.2 m,
	/// reusing ContestRange's arm's-length anchor) of the ball at resolution
	/// time. The sibling positional gap on steal is tracked separately (#196).
	///
	/// Not client-predicted: this runs only here, IsServer-gated, so the
	/// blocking client sees its own swat ~1 RTT late via ReceiveState — the
	/// same accepted gap as the steal. ADR-0018 §4's prediction sentence
	/// stays aspirational until the remote-phase/display work (#69/#102
	/// lineage) lands.
	/// </summary>
	private void ResolveBlockAttempts()
	{
		if (StateMachine.Current != BallState.InFlight) return;
		if (_inFlightStartTick < 0) return; // defensive: no shot recorded yet
		if (Players == null) return;

		foreach (Node child in Players.GetChildren())
		{
			if (child is not PlayerController defender) continue;

			// A non-peer node (unparsable Name) is never a blocker — fail
			// SAFE by excluding it, matching ResolveLooseBallRecovery's own
			// convention. The inverted form matters: `TryParse(...) &&
			// defenderId == _lastShooterPeerId` would let a parse FAILURE
			// (defenderId left at 0) fall through as a live candidate instead
			// of being excluded, silently disabling the self-block guard for
			// exactly the nodes it can't identify.
			if (!int.TryParse(defender.Name, out int defenderId)) continue;

			// You cannot block your own shot. The holder is 0 while InFlight,
			// so the "defender == holder" exclusion the steal uses has no
			// meaning here — the shooter is identified by _lastShooterPeerId
			// instead. Server-side because authority rules must not live only
			// in client input code (ADR-0002): the client-side !IsBallHolder
			// input gate is UX, this is the rule.
			if (defenderId == _lastShooterPeerId) continue;

			// ActiveMove<BlockMove>() is non-null only when the defender is in
			// Active phase on a BlockMove. Most ticks this is null (fast path).
			var blockActive = defender.ActiveMove<BlockMove>();
			if (blockActive == null) continue;

			var (move, frameInPhase) = blockActive.Value;
			int activeFrames = move.FrameData.ActiveFrames;

			// Compute the defender's Active window as absolute physics ticks.
			// frameInPhase = 0 means they JUST entered Active this very tick;
			// frameInPhase = N means they entered Active N ticks ago.
			//
			// Hidden assumption: this reads defActiveStart as of THIS Ball tick,
			// which is only correct because the Players node precedes the Ball
			// node in Main.tscn — player CommittedMoveMachines tick before the
			// ball reads their phase/frame here. Reordering those nodes would
			// shift every block window by one tick without changing a single
			// line in this file (see the repo's recorded parent-precedes-child
			// tick-observation gotcha). The headless harness pins the resulting
			// end-to-end timing, so a reorder regression would show up there.
			int defActiveStart = PhysicsTick - frameInPhase;
			int defActiveEnd   = defActiveStart + activeFrames;

			// Vulnerable window: [inFlightStart, inFlightStart + BlockGraceTicks).
			// Full interval form of the shared predicate (ADR-0018 §2):
			//   defActiveStart < vulnEnd  AND  vulnStart < defActiveEnd
			bool timingSucceeds = DefensiveResolution.Succeeds(
				defActiveStart, defActiveEnd,
				_inFlightStartTick, _inFlightStartTick + BlockGraceTicks);

			// Spatial gate (issue #214): timing alone let a defender anywhere
			// on the court "block" a shot, deleting the spacing axis from the
			// shot/block duel (CLAUDE.md §1). Composed with — not a
			// replacement for — the timing check: BOTH must hold. GlobalPosition
			// here is the ball's position as of the end of the PREVIOUS
			// TickInFlight (this method runs before the state switch, see the
			// class doc's per-tick ordering note), which is exactly where the
			// existing swat-direction code below already reads it from — close
			// enough to "the release point" that a short-lived, ~10-tick grace
			// window cannot have moved the ball far from it.
			bool withinReach = DefensiveResolution.WithinBlockReach(
				defender.GlobalPosition, GlobalPosition, BlockReachRadius);

			bool success = timingSucceeds && withinReach;

			if (success)
			{
				// InFlight → Loose: the shot arc terminates mid-flight.
				// (ADR-0008 Amendment 2026-06-30: block is a defense-induced
				// InFlight→Loose turnover; the loose-ball scramble awards next tick.)
				StateMachine.GoLoose();

				// Terminate the arc's shot velocity with a provisional "swat":
				// horizontal away from the rim, vertical down (ADR-0014 — a real
				// blocked shot is knocked back toward the court and down, it does
				// NOT keep sailing toward the basket; without this the ball flies
				// its unaltered make-trajectory as a Loose ball, passing through
				// the rim geometry, and the block reads as a phantom miss). _arc
				// is already a valid instance here (Shoot() built it), so unlike
				// the steal's Dribbling→Loose path no seed-then-overwrite is
				// needed — overwriting Velocity in place is enough.
				// DefensiveKnockDirection.SafeHorizontal (issue #216 row 4) —
				// shared with the steal knock direction math above.
				Vector3 fromRim = DefensiveKnockDirection.SafeHorizontal(GlobalPosition - RimCenter);
				Vector3 swatDir;
				if (fromRim != Vector3.Zero)
					swatDir = fromRim;
				else
					// Degenerate case: the ball's XZ coincides with RimCenter's —
					// release from directly under the rim, the most common block
					// range. A pure "away from rim" direction is undefined there,
					// so fall back to "away from the defender's hand" instead: the
					// swat physically comes off whoever's hand touched the ball,
					// not a vertical drop through the rim geometry. Also Zero
					// (straight-down drop) if THIS is degenerate too.
					swatDir = DefensiveKnockDirection.SafeHorizontal(GlobalPosition - defender.GlobalPosition);
				_arc.Velocity = new Vector3(
					swatDir.X * BlockSwatSpeed,
					-BlockSwatDropSpeed,
					swatDir.Z * BlockSwatSpeed);

				// The blocker is now the last toucher (#118 last-toucher-out rule,
				// same reasoning as the steal's success branch): a swatted ball
				// that sails OOB before the scramble recovers it must charge the
				// turnover to the DEFENDER who touched it — offense retains —
				// not re-key off the shooter, which would hand the blocker the
				// ball for blocking it out of bounds.
				if (defenderId != 0)
					_lastToucherPeerId = defenderId;

				// One committed move = at most one turnover ("spent once, then
				// Recovery" — the steal remediation's contract). Today the frame
				// data makes a second resolution unreachable (the ball cannot be
				// caught and re-shot inside the ≤8 remaining Active ticks:
				// JumpShot Startup alone is 18), but nothing should rely on
				// tunables staying that way — #104 retunes them. Ending Active
				// now pins the invariant AND frees the blocker to contest the
				// very scramble their block just created instead of standing
				// planted through the leftover Active ticks.
				defender.EndResolvedDefensiveMove();

				GD.Print($"[BallController] Block success: defender {defender.Name}, " +
				         $"defActive [{defActiveStart},{defActiveEnd}), " +
				         $"vulnWindow [{_inFlightStartTick},{_inFlightStartTick + BlockGraceTicks})");

				// No shot is in flight any more. ApplyShootLocally re-arms this
				// on the next release; clearing it here keeps the field's
				// "-1 means no shot recorded" reading truthful between shots.
				_inFlightStartTick = -1;
				return; // only one block resolves per tick
			}
			// Whiff: Recovery is the natural punishment; nothing to do here.
		}
	}

	// ── Per-state behaviour ───────────────────────────────────────────────

	/// <summary>Ball cradled at hand height in front of the holder. Shoot to release.</summary>
	private void TickHeld()
	{
		var holderBody = Players?.GetNodeOrNull(StateMachine.HolderPeerId.ToString()) as PlayerController;
		Vector3 holderPos = holderBody?.GlobalPosition ?? Vector3.Zero;
		Vector3 forward   = HolderForward(holderBody);
		Vector3 right     = HandRight(forward);
		(float lateralSign, float verticalDip, BallSweepPath sweepPath) = AdvanceHandSweep(holderBody);
		float forwardOffset = SweepForwardOffset(verticalDip, sweepPath);

		// World-space Y = DribbleHandHeight, consistent with DribbleCycle's
		// world-Y convention, minus the current sweep's mid-transit dip
		// (#195's shared curve; 0 outside an active sweep) at whichever
		// depth this sweep's path uses (SweepDipDepth).
		GlobalPosition = new Vector3(
			holderPos.X + forward.X * forwardOffset + right.X * HandOffset * lateralSign,
			DribbleHandHeight - verticalDip * SweepDipDepth(sweepPath),
			holderPos.Z + forward.Z * forwardOffset + right.Z * HandOffset * lateralSign
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
		(float lateralSign, float verticalDip, BallSweepPath sweepPath) = AdvanceHandSweep(holderBody);
		float forwardOffset = SweepForwardOffset(verticalDip, sweepPath);

		// Pass XZ-offset position; GetBallPosition discards the Y and uses
		// HeightAtPhase instead — so the crossover sweep's dip (#195) is
		// applied as a separate Y subtraction afterward rather than folded
		// into the position passed in here.
		GlobalPosition = _dribble.GetBallPosition(new Vector3(
			holderPos.X + forward.X * forwardOffset + right.X * HandOffset * lateralSign,
			holderPos.Y,
			holderPos.Z + forward.Z * forwardOffset + right.Z * HandOffset * lateralSign
		));
		GlobalPosition -= new Vector3(0f, verticalDip * SweepDipDepth(sweepPath), 0f);
		// GetBallPosition's Y is 0 at the bounce's floor-contact phase, and the
		// dip above can subtract up to SweepDipDepth(sweepPath) more —
		// uncorrelated with the dribble phase, so the two CAN coincide and
		// drive the ball center under the floor. This is EXACTLY why
		// BetweenTheLegs's deeper BetweenTheLegsDipDepth is safe to tune
		// aggressively (close to the floor, per its own doc): the dip is
		// cosmetic transit flavor; it must never win over the "stay above the
		// floor" invariant the rest of this class enforces (see the
		// depenetration guard further down).
		GlobalPosition = GlobalPosition with { Y = Mathf.Max(GlobalPosition.Y, BallRadius) };
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
	/// The in-hand forward offset (metres) for this tick. Thin wrapper over
	/// CrossoverBallSweep.ForwardOffset (#211 code-review fix) — the formula
	/// itself is a pure static there so xUnit can pin it directly; this
	/// method just supplies BallController's own tunables.
	/// </summary>
	private float SweepForwardOffset(float verticalDip, BallSweepPath sweepPath) =>
		CrossoverBallSweep.ForwardOffset(DribbleForwardOffset, verticalDip, BehindTheBackSweepDepth, sweepPath);

	/// <summary>
	/// The vertical dip depth (metres) to apply for the CURRENT sweep's path
	/// (#199) — BetweenTheLegsDipDepth for a through-the-legs transit,
	/// CrossoverSweepDipDepth (shared by Crossover's and BehindTheBack's
	/// shallower dips) otherwise.
	/// </summary>
	private float SweepDipDepth(BallSweepPath sweepPath) =>
		sweepPath == BallSweepPath.ThroughLegs ? BetweenTheLegsDipDepth : CrossoverSweepDipDepth;

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
	/// Drives both the once-per-possession hand reset (M9, #83/ADR-0012, the
	/// former UpdateHandSide) and the crossover ball sweep (#195), and returns
	/// this tick's (lateralFactor, verticalDip) for TickHeld/TickDribbling to
	/// fold into the in-hand position. lateralFactor replaces the old bare
	/// HandSign() read; verticalDip is 0 whenever no sweep is running.
	///
	/// ── Trigger rules (#195) ──────────────────────────────────────────────
	/// 1. Same-holder HandSide flip -> (re)start the sweep.
	/// 2. Possession change (HolderPeerId changed) -> snap, NO sweep. Edge-
	///    detected by _handSideHolderId, exactly as UpdateHandSide always did
	///    — a new holder has no carried-over hand OR sweep state.
	/// 3. A flip while a sweep is already running (a re-cross) -> restart
	///    from the ball's CURRENT interpolated lateral position, never
	///    jumping back to the old side.
	/// Rules 2 and 1 are told apart by caching BOTH _handSideHolderId (the
	/// possession edge) and _lastObservedHandSide (the hand edge) — rule 2 is
	/// checked first and returns early, so a possession change can never also
	/// register as a same-holder flip on the same tick.
	///
	/// ── Doubt-driven: why this is safe to trigger off broadcast state alone ──
	/// HandSide is server-authoritative, predicted, and reconciled per
	/// PlayerController (ReceiveState) — never a local-only signal like
	/// CurrentMove, which the opponent's remote copy doesn't have (the M7b
	/// #69 remote-display gap class this issue's spec explicitly calls out).
	/// TickHeld/TickDribbling run once per fixed physics tick on EVERY role —
	/// server, the predicting holder-client, and the remote client alike (see
	/// this class's doc) — and Players ticks before Ball in the scene tree,
	/// so by the time this runs, holder.HandSide already holds THIS tick's
	/// authoritative value everywhere: the server/holder set it inline
	/// (TickCommittedMoveBehavior's Active-entry), the remote copy adopted
	/// the broadcast value (TickClientRemotePlayer). Comparing it against the
	/// PREVIOUS tick's locally-cached value is therefore comparing two
	/// already-reconciled samples on each machine independently — the sweep
	/// derives identically everywhere with no new RPC, exactly like
	/// DribbleCycle's local phase advance already does for the dribble bounce.
	/// (A sweep started by one machine slightly before another, due to
	/// network latency in HandSide's arrival, is the same acceptable
	/// per-machine timing skew every other reconciled field already has —
	/// not a new class of desync.)
	///
	/// ── Known, accepted residual gaps (doubt cycle, #195) ─────────────────
	/// Two edge cases can make a peer's sweep count diverge from another's;
	/// both are pre-existing properties of the underlying HandSide field
	/// itself (unchanged by this method), not new netcode gaps this sweep
	/// introduces, so they are documented rather than "fixed" with more
	/// machinery:
	///   1. A rare mispredicted Crossover: the predicting holder-client's
	///      ReconcileFromServer can force HandSide back to _serverHandSide
	///      when the server rejects the move — a SECOND HandSide change this
	///      method cannot tell apart from a genuine flip, so it plays an
	///      (extra, self-correcting) reverse sweep the server never plays.
	///      This rides the exact same accepted "residual staleness" gap
	///      ReconcileFromServer's own doc already calls out for the raw
	///      HandSide snap (ADR-0012) — this method just makes that already-
	///      accepted transient divergence visible as a cosmetic ripple.
	///   2. Packet loss on the remote-opponent-client: HandSide there only
	///      updates on ticks a fresh ReceiveState broadcast lands
	///      (TickClientRemotePlayer's _hasNewState gate). A rapid re-cross
	///      whose intermediate broadcast is dropped can vanish entirely on
	///      that peer (no sweep at all), while the server/holder-client play
	///      one — inherent to HandSide's existing "unreliable, latest-value-
	///      wins" broadcast, not something reliable delivery would be worth
	///      adding new netcode for here.
	///   3. (Doubt cycle, #194) The SAME mispredicted-move revert from gap 1
	///      can additionally pick the WRONG sweep PATH for its self-
	///      correcting reverse sweep: at the exact revert tick,
	///      _machine.CurrentMove has typically already gone null/Inactive
	///      (ReconcileFromServer's force-Inactive branch), so
	///      DisplayMoveId() reads "" rather than "behindtheback"/
	///      "betweenthelegs" — the extra reverse sweep plays as a front
	///      (Crossover-style) transit even if the reverted move was a
	///      BehindTheBack or BetweenTheLegs. Cosmetic-only, self-correcting
	///      within one sweep duration, and strictly a narrower instance of
	///      gap 1 (a wrong PATH, not a wrong COUNT of sweeps) — not worth
	///      caching a second piece of pre-revert state to close.
	/// </summary>
	private (float lateralFactor, float verticalDip, BallSweepPath sweepPath) AdvanceHandSweep(PlayerController holder)
	{
		if (StateMachine.HolderPeerId != _handSideHolderId)
		{
			_handSideHolderId = StateMachine.HolderPeerId;
			holder?.ResetHandSide();
			_sweepActive = false;
			_lastObservedHandSide = holder?.HandSide;
			return (HandSign(holder), 0f, BallSweepPath.InFront);
		}

		// (Doubt cycle, #195, finding #3) Only overwrite the cached baseline
		// when we actually observed a real HandSide this tick. A transient
		// null holder (e.g. a momentary NodePath lookup miss) must NOT
		// clobber _lastObservedHandSide with null — doing so would silently
		// drop the next tick's flip detection (comparing the real value
		// against null instead of against the true last-known side) instead
		// of merely skipping one tick's read, which is all a transient miss
		// should cost.
		HandSide? current = holder?.HandSide;
		if (current.HasValue && _lastObservedHandSide.HasValue && current.Value != _lastObservedHandSide.Value)
		{
			// Rule 3 (re-cross restart): live-but-dormant under default tuning —
			// the sweep (~7 ticks at CrossoverSweepDuration=0.12s) completes long
			// before Crossover's own Startup+Recovery lets a second crossover
			// legally Begin (~tick 21), so _sweepActive is never still true when
			// this runs today. It becomes reachable (and would then need real
			// coverage, not just this unit-tested branch) if a future tuning pass
			// shortens Recovery or lengthens CrossoverSweepDuration past ~15 ticks.
			float fromLateral = _sweepActive
				? CrossoverBallSweep.Offset(CurrentSweepT(), _sweepFromLateral, _sweepToLateral).LateralFactor
				: SignOf(_lastObservedHandSide.Value);

			_sweepFromLateral    = fromLateral;
			_sweepToLateral      = SignOf(current.Value);
			_sweepTicks          = 0;
			_sweepDurationTicks  = Mathf.Max(1, Mathf.RoundToInt(CrossoverSweepDuration * Engine.PhysicsTicksPerSecond));
			_sweepActive         = true;
			// #194/#199: sampled ONCE, at the moment the flip is detected — see
			// _sweepPath's doc for why this is not re-read per tick.
			// DisplayMoveId() is the same per-role broadcast-aware resolver
			// DisplayMove() already uses (M7b #69's gap class): the holder's
			// OWN simulated role reads its live _machine, but the remote
			// client's copy of the opponent has no live _machine to read and
			// must fall back to the broadcast _serverMoveId — exactly the
			// same role split this class's own doc above already establishes
			// is safe for HandSide itself.
			_sweepPath = holder?.DisplayMoveId() switch
			{
				"behindtheback"  => BallSweepPath.BehindBody,
				"betweenthelegs" => BallSweepPath.ThroughLegs,
				_                => BallSweepPath.InFront,
			};
		}
		if (current.HasValue)
			_lastObservedHandSide = current;

		if (!_sweepActive)
			return (HandSign(holder), 0f, BallSweepPath.InFront);

		// (Doubt cycle, #195, finding #2) Sample t up to AND INCLUDING 1.0
		// before deactivating — advancing _sweepTicks only when t hasn't yet
		// reached the end — so the final active sample is the curve's true
		// t=1 endpoint (lateral = toSign exactly, dip = 0 exactly) and the
		// following tick's early-return fallback (HandSign = toSign, dip
		// hardcoded 0) is a continuation of that same value, not a pop. The
		// previous version advanced the tick counter first, so the sweep's
		// LAST active sample landed at t=(duration-1)/duration — short of
		// the endpoint — and the tick after that hard-snapped, producing a
		// one-tick discontinuity in the dip (visible; the lateral discrepancy
		// was small enough to be imperceptible, but the dip curve peaks
		// nowhere near 0 at that point).
		float t = CurrentSweepT();
		(float lateral, float dip) = CrossoverBallSweep.Offset(t, _sweepFromLateral, _sweepToLateral);
		if (t >= 1f)
			_sweepActive = false;
		else
			_sweepTicks++;

		return (lateral, dip, _sweepPath);
	}

	/// <summary>Normalised progress [0, 1] through the current sweep, for CrossoverBallSweep.Offset.</summary>
	private float CurrentSweepT() =>
		_sweepDurationTicks <= 0 ? 1f : (float)_sweepTicks / _sweepDurationTicks;

	/// <summary>+1 for HandSide.Right, -1 for HandSide.Left — the same convention HandSign reads off a holder node.</summary>
	private static float SignOf(HandSide side) => side == HandSide.Right ? 1f : -1f;

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

			case ContactResult.None:
				// No rim or backboard contact this tick. A clean miss (air ball),
				// a shot scattered wide of the rim, or a long pass must STILL end
				// its flight — otherwise the arc integrates forever and the ball
				// sinks through the floor (Y → −∞) or sails through the walls. The
				// deterministic mini-physics ball never consults Godot's collision
				// system (ADR-0004), so the scene walls cannot contain it; its only
				// containment lives in TickLoose, which a never-terminating flight
				// never reaches. FlightTermination ends the flight on floor-contact
				// or OOB (pure helper, headless-seam). After GoLoose, TickLoose
				// takes over: FloorBounce + rebound contest in bounds, or
				// OobResolution's turnover award when out of bounds.
				//
				// Runs on EVERY peer as deterministic prediction, exactly like the
				// Bounce/Make branches — ShouldGoLoose is a pure function of the
				// arc position and the CourtMin/Max exports, identical everywhere.
				if (FlightTermination.ShouldGoLoose(_arc.Position, BallRadius, CourtMin, CourtMax))
					StateMachine.GoLoose();
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

		// Out-of-bounds detection (issue #63, ADR-0008 §Amendment 2026-06-28):
		// replaces the old unconditional clamp.  When a loose ball crosses the
		// play-court line, the play is dead — server awards possession to the player
		// OPPOSITE the last shooter ("last-toucher-out → other ball" rule).
		//
		// The three-branch decision table lives in OobResolution.Resolve (pure
		// helper, ADR-0004 headless-seam discipline) so it can be unit-tested
		// without a Godot runtime.  Engine lookups (IsServer, OtherPlayerPeerId)
		// are resolved here before the call and passed in as plain values, keeping
		// the pure helper engine-free.
		//
		// Ordering: runs BEFORE ResolveLooseBallRecovery so a ball that crossed the
		// line is not also rebounded the same tick.  An Award returns immediately,
		// skipping the rebound step.  ClampFallback falls through to the clamp below.
		{
			// OOB turnover recipient = opposite the last TOUCHER (#118 part 1,
			// ADR-0008 §Amendment 2026-06-30): the streetball "last-toucher-out →
			// other ball" rule. _lastToucherPeerId advances on every possession
			// change (tipoff, rebound, catch, make-it-take-it, carry turnover), so
			// a rebounder who fumbles OOB is awarded AGAINST — not handed the ball
			// back, which the old last-SHOOTER key did (it never moved on a rebound).
			//
			// Pre-touch short-circuit (#118 part 2) lives entirely in
			// ResolveRecipient: when _lastToucherPeerId is still 0 (pre-tipoff,
			// nobody has touched the ball) it returns 0 regardless of the opponent
			// arg, and OobResolution maps recipient 0 to ClampFallback — the ball
			// clamps and stays in play instead of teleporting to a spawn-order-
			// arbitrary player. OtherPlayerPeerId(0) is a harmless read (it would
			// return the first player, but ResolveRecipient discards it), so no
			// call-site guard is needed; the helper is the single owner of the rule.
			int resolvedRecipient = OobResolution.ResolveRecipient(
				_lastToucherPeerId,
				OtherPlayerPeerId(_lastToucherPeerId));

			OobResolution.Result oob = OobResolution.Resolve(
				CourtBounds.IsOutOfBounds(_arc.Position, CourtMin, CourtMax),
				IsServer,
				resolvedRecipient);

			if (oob.Action == OobResolution.Action.Award)
			{
				// Dead ball: award possession to the non-shooting player, uncleared
				// (they must take it back before scoring — ADR-0008 §Amendment 2026-06-21).
				AwardPossession(oob.RecipientPeerId);
				GlobalPosition = _arc.Position;
				return; // skip rebound step — possession is already resolved
			}
			// NoOp: ball is in bounds, nothing to do.
			// ClampFallback: no opponent present (solo test) or non-server client —
			// fall through to CourtBounds.Clamp below so the ball stays in play.
			// The server's ReceiveState broadcast corrects any divergence on clients.
		}

		// Half-court bound clamp: keeps XZ within the play-court rectangle.
		// Runs on every peer (deterministic: same CourtMin/Max exports → no drift).
		// This is now the fallback path for the client and the no-opponent solo case;
		// the server's authoritative OOB turnover above has already returned when an
		// opponent exists.  Y is preserved — the floor-contact check above already
		// owns vertical containment.
		//
		// Velocity zeroing: when the clamp fires on an axis, zero the matching
		// velocity component so the integrator does not keep pushing the ball
		// through the wall every tick.
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
	/// Awards possession of a loose / in-flight ball to a player — the shared
	/// handoff path used by a live rebound (#48), the make-it-take-it reset
	/// (#49), and every OOB/steal/block turnover. Lands the new holder in a
	/// fresh, LIVE Held possession (#193, ADR-0008): unlike the pre-#193
	/// behaviour, this no longer auto-chains into Dribbling — the new holder
	/// gets the "triple threat" beat (they may shoot immediately, or drive by
	/// pushing the stick past deadzone, see PlayerController.
	/// CheckAutoStartDribble) rather than being dropped straight into a
	/// dribble they never asked to start.
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
		// Pick the legal edge by the ball's current state:
		//   • Loose / InFlight  → Catch    : a live recovery (rebound, made-shot
		//     reset, OOB award after a loose ball crossed the line). The original
		//     and only pre-OOB-turnover callers always reach here while Loose.
		//   • Held / Dribbling  → Turnover : a dead-ball handoff while a player
		//     still controls the ball — the player-OOB turnover (the ballhandler
		//     crossed the court line). Catch is illegal from these states, so a
		//     single AwardPossession serves both award kinds without the caller
		//     having to know which edge applies.
		bool awarded = (State == BallState.Held || State == BallState.Dribbling)
			? StateMachine.Turnover(peerId)
			: StateMachine.Catch(peerId);

		if (!awarded)
		{
			// Defensive backstop. Today all four ball states map to a legal edge
			// (Loose/InFlight→Catch, Held/Dribbling→Turnover), so this branch is
			// unreachable in normal play. It is deliberately kept — NOT dead code —
			// because generalizing the edge selection removed the old "Catch fails
			// loudly when called in Held/Dribbling" guard: should a future state be
			// added (or an edge guard tightened) and leave a hole, this surfaces it
			// loudly per CLAUDE.md's loud-failure rule instead of silently dropping
			// the award. Reconciliation is unaffected: ReconcileFromServer runs at
			// the TOP of _PhysicsProcess, before the state switch, so the
			// dispatching tick state is authoritative.
			GD.PrintErr($"[BallController] AwardPossession({peerId}) rejected in state {State}; possession unchanged.");
			return;
		}

		// Possession changed hands → this player is now the last toucher (#118).
		// Set on every peer that calls AwardPossession itself (this path runs as
		// prediction on clients too), but only the server's value drives the OOB
		// award (OobResolution gates Award on isServer). Updating here — not only
		// on a shot — is the whole fix: a rebounder who later fumbles the ball OOB
		// is no longer handed it straight back, because the toucher has advanced
		// to them. (#177 audit R1) This is the general AwardPossession path — the
		// steal-turnover write in ResolveStealAttempts is the one EXCEPTION that
		// bypasses AwardPossession and this per-peer symmetry; see
		// _lastToucherPeerId's field doc for why that server-only write is
		// currently safe.
		_lastToucherPeerId = peerId;

		// #193: this possession's dribble is LIVE — reset the dead-dribble flag
		// unconditionally. This is the ONE reset point for every possession
		// change (rebound, turnover, make-it-take-it); the tipoff resets it
		// separately in TryAssignTipoffHolder, which never calls AwardPossession.
		// No StartDribble() call follows here any more (pre-#193 behaviour):
		// the new holder starts Held, not Dribbling — see this method's doc.
		HasDribbled = false;

		// Every possession change starts a fresh dribble (#176, ADR-0014 call
		// recorded on the issue): real half-court 1v1 rules end a dribble the
		// instant the ball leaves the previous holder's control, so a rebound,
		// steal/block recovery, OOB turnover, and make-it-take-it award all
		// restart _dribble.Phase at 0 — the same value a brand-new DribbleCycle
		// starts at. Without this, Phase stayed frozen at whatever value existed
		// when the ball last went Loose (DribbleCycle.Phase only advances in
		// TickDribbling), which let a defender who forced a scramble and then
		// recovered the SAME ball re-attempt a steal against a phase that could
		// already sit inside the steal-exposed band — no genuine timing read
		// required. Called unconditionally for every AwardPossession path
		// (there is no "live-recovery only" special case) because the same
		// stale-phase hazard is reachable from every one of them, not just the
		// steal path that first exposed it.
		_dribble.Reset();

		// `cleared` is false for rebounds/turnovers (every peer sets this
		// deterministically; clients predict it immediately for HUD responsiveness),
		// and true for make-it-take-it resets (server-only call site in
		// ResolveServerMake; clients start with false and are corrected by the next
		// ReceiveState broadcast within 1 RTT).  The 1-RTT window where a client
		// briefly shows "take it back" after a counting make is an accepted cosmetic
		// artefact — sub-frame at LAN latency, inherent to client prediction.
		IsCleared = cleared;

		// New possession → restart the take-back crossing latch (#135). Server-only
		// state, harmless on clients (they never run UpdateClearStatus). For a
		// pre-cleared award (make-it-take-it) the latch is moot — UpdateClearStatus
		// early-returns on IsCleared — but resetting unconditionally keeps the
		// invariant simple: every possession begins with the take-back not yet done.
		_holderHasBeenInsideClearLine = false;
	}

	// ── Triple threat: dead-dribble rule (#193, ADR-0008 amendment) ─────────

	/// <summary>
	/// Cradles a live dribble as the side effect of the holder BEGINNING a
	/// JumpShot — covers the pump-fake too, since a feint is a Startup-phase
	/// abort of the SAME Begin(), not a separate one (#193's spec). Ends the
	/// dribble (StopDribble → Held) and marks <see cref="HasDribbled"/>
	/// immediately, even though the shot itself may still be feinted away:
	/// you can't un-pick-up your dribble by faking a shot. Matches real ball
	/// / 2K — the gather is inherent to the shooting motion, not a separate
	/// discrete action or CommittedMove.
	///
	/// Called from PlayerController.BeginCommittedMove — the ONE shared choke
	/// point every Begin(JumpShot) call already funnels through (the server,
	/// for every player node it runs authoritatively; a client, only for its
	/// own predicted player) — so this runs under exactly the same authority
	/// set CheckJumpShotRelease already restricts ball-state writes to, with
	/// no separate IsServer/IsLocalHolder guard needed here.
	///
	/// No-ops if the ball isn't currently Dribbling, or if the caller isn't
	/// the ball's holder — so a stray/mistimed call can never desync ball
	/// state. In particular, shooting straight out of a fresh live Held
	/// possession never touches StopDribble at all: there is no dribble to
	/// end, and HasDribbled is already false for that possession.
	/// </summary>
	public void CradleForShotStartup(int peerId)
	{
		// KNOWN ACCEPTED RACE (code review on PR #204, ~1-tick window, NOT
		// fixed here): PlayerController.RequestBeginMove travels Reliable
		// while SubmitInput (the channel that carries the drive input
		// CheckAutoStartDribble reads) travels UnreliableOrdered — separate
		// channels with no cross-ordering guarantee. A client that drives and
		// then pump-fakes within roughly one tick can have the SERVER process
		// Begin(JumpShot) BEFORE that drive's SubmitInput arrives: the server
		// still sees Held here (this guard no-ops), so HasDribbled stays
		// false server-side while the client's own prediction already set it
		// true — the client is then force-corrected back to false by the next
		// ReceiveState broadcast (HasDribbled IS broadcast; see the
		// ReconcileFromServer doc), a silent, narrow dead-dribble bypass. A
		// robust fix is cross-channel input/RPC ordering, which is a separate
		// piece of work, not a #193 fix — see the cradle-race follow-up issue
		// linked from PR #204's conversation.
		if (StateMachine.Current != BallState.Dribbling) return;
		if (StateMachine.HolderPeerId != peerId) return;

		if (StateMachine.StopDribble())
			HasDribbled = true;
	}

	/// <summary>
	/// Attempts to resume a live dribble out of a fresh Held possession — the
	/// "drive" exit from the triple-threat stance (#193): the holder pushing
	/// the left stick past deadzone. Refused once <see cref="HasDribbled"/> is
	/// set for this possession (DeadDribbleRule.CanStartDribble) — the dead-
	/// dribble rule means a feinted pump-fake strands the holder in dead Held
	/// until the next possession change; they can still walk (Move() is
	/// unaffected), they just can't resume bouncing the ball.
	///
	/// Called from PlayerController.CheckAutoStartDribble wherever a tick's
	/// REAL movement input is available for this player — see that method's
	/// doc for exactly which roles that is (the same authority set every
	/// other ball-state write in this class already restricts to).
	///
	/// No-ops if the ball isn't Held, or if the caller isn't the ball's
	/// holder — so an off-ball player's own stick input can never touch
	/// someone else's possession.
	/// </summary>
	public void TryStartDribble(int peerId)
	{
		if (StateMachine.Current != BallState.Held) return;
		if (StateMachine.HolderPeerId != peerId) return;
		if (!DeadDribbleRule.CanStartDribble(HasDribbled)) return;

		StateMachine.StartDribble();
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
		// (#120) A holder who is out of bounds at the moment of release has
		// already turned the ball over — in real half-court 1v1 the ball is
		// dead the instant you step on/over the line (ADR-0008, ADR-0014
		// real-ball authority), so the shot must NOT count. Without this guard,
		// Shoot() transitions to InFlight here, INSIDE the TickHeld/TickDribbling
		// switch, which runs BEFORE _PhysicsProcess's ResolvePlayerOutOfBounds()
		// (line ~720); that later check then sees InFlight and no-ops, letting a
		// make from an OOB release score.
		//
		// We deliberately reuse ResolvePlayerOutOfBounds() — the SAME rule the
		// carry-OOB turnover uses — rather than inventing a release-specific OOB
		// test: one OOB definition, one recipient-eligibility path, one ADR-0008
		// award (and the same clamp fallback when no eligible recipient exists).
		// State is still Held/Dribbling at this call site, so that method resolves
		// the turnover correctly. Server-authoritative: possession is never
		// client-computed (clients only reconcile), so the predicting client's
		// shot is corrected to the turnover by the next ReceiveState broadcast —
		// the same mispredict-then-reconcile path a scattered miss already uses.
		if (IsServer
			&& CourtBounds.IsOutOfBounds(HolderPosition(), CourtMin, CourtMax))
		{
			ResolvePlayerOutOfBounds();
			return false;
		}

		int holderAtShootTime = StateMachine.HolderPeerId;
		if (!StateMachine.Shoot()) return false;

		// Record the tick the ball entered InFlight so block resolution can
		// compute the shot's vulnerable window [_inFlightStartTick, _inFlightStartTick +
		// BlockGraceTicks) each tick while InFlight (ADR-0018 §2, issue #98).
		// PhysicsTick reads the engine's own physics-frame counter, stable for
		// this whole tick regardless of when within _PhysicsProcess it's read
		// (see its doc) — this value is the CURRENT tick (the release tick
		// itself).
		_inFlightStartTick = PhysicsTick;

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
			// Reads ShotInitiationSpeed (XZ speed captured when the JumpShot
			// BEGAN), NOT the release-time Velocity: the committed shot plants the
			// feet (Velocity≈0) by the time it releases, so reading Velocity here
			// made the penalty inert — a sprint pull-up scattered like a set shot
			// (#137). ShotInitiationSpeed is server-authoritative for this calc.
			float speedRatio      = holder.MoveSpeed > 0f
				? Math.Clamp(holder.ShotInitiationSpeed / holder.MoveSpeed, 0f, 1f)
				: 0f;
			float movementFactor  = 1f + MovementScatterK * speedRatio;

			// #65 — contest penalty: defender within ContestRange → larger factor.
			// proximity is in [0,1]: 0 = at ContestRange edge, 1 = on top of shooter.
			// #99 — committed contest: an ADDITIONAL factor on top of the above
			// (ADR-0018 §2), applied when the defender's committed ContestMove is
			// Active on this exact release tick. Both share the same
			// defenderController lookup and the shared XZ-distance helper (issue
			// #99 folded-forward cleanup from PR #220 review — one implementation
			// of "XZ distance between two positions" instead of a second
			// independent dx/dz/sqrt copy; see DefensiveResolution.DistanceXZSquared).
			float contestFactor     = 1f;
			float contestMoveFactor = 1f;
			int   shooterPeerId     = holderAtShootTime; // captured before Shoot() cleared it
			int   defenderPeerId    = OtherPlayerPeerId(shooterPeerId);
			if (defenderPeerId != 0 && Players != null)
			{
				var defenderController = Players.GetNodeOrNull<PlayerController>(defenderPeerId.ToString());
				if (defenderController != null)
				{
					// #100 — whiff-punish blow-by lane (ADR-0018 Amendment
					// 2026-07-16): a defender inside a beaten window (granted by
					// ResolveBeatenWindowTriggers on a whiffed StealMove) contests
					// this shot with NEITHER of its two terms — both the passive
					// proximity scatter below AND the committed ContestMove factor
					// stay forced to their neutral 1.0. This is checked BEFORE
					// either term is computed (not after, then overridden) so a
					// beaten defender genuinely contributes nothing, matching the
					// issue's "suppresses BOTH" requirement exactly rather than
					// computing then discarding.
					bool defenderBeaten = defenderController.IsBeaten(PhysicsTick);

					if (!defenderBeaten && ContestRange > 0f)
					{
						float defDistSq = DefensiveResolution.DistanceXZSquared(
							defenderController.GlobalPosition, holder.GlobalPosition);
						float defDist   = MathF.Sqrt(defDistSq);
						float proximity = Math.Clamp(1f - defDist / ContestRange, 0f, 1f);
						contestFactor   = 1f + ContestScatterK * proximity;
					}

					// #99 — is the defender's ContestMove Active on this exact
					// release tick? DefensiveResolution.ContestAppliesAt's own doc
					// explains why the shot's "release window" collapses to a
					// single tick for contest, unlike block's multi-tick grace.
					var contestActive = defenderController.ActiveMove<ContestMove>();
					if (!defenderBeaten && contestActive != null)
					{
						var (contestMove, frameInPhase) = contestActive.Value;
						int contestActiveStart = PhysicsTick - frameInPhase;
						int contestActiveEnd   = contestActiveStart + contestMove.FrameData.ActiveFrames;
						bool contestAppliesAtRelease = DefensiveResolution.ContestAppliesAt(
							contestActiveStart, contestActiveEnd, _inFlightStartTick);
						contestMoveFactor = DefensiveResolution.ContestMoveFactor(
							contestAppliesAtRelease, ContestMoveScatterK);
					}
				}
			}
			LastContestMoveFactorForHarness = contestMoveFactor;
			LastContestFactorForHarness = contestFactor;

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

			// #99 — contestMoveFactor composes multiplicatively alongside the
			// existing three factors (ADR-0018 §2: additional, not a
			// replacement for contestFactor's passive term).
			float accuracyMultiplier =
				movementFactor * contestFactor * contestMoveFactor * facingFactor;

			aimTarget = ShotScatter.Scatter(
				ShotTarget, distance, angle01, radius01,
				ShotScatterPerMeter, MaxShotScatter, accuracyMultiplier);
		}

		_arc = new ShotArc(GlobalPosition, aimTarget, ShotApexHeight, Gravity);

		// Server-only: release-tick block top-up (issue #216 original body
		// row 5 — moved here from a separate conditional in _PhysicsProcess
		// so the tick-ordering knowledge lives in the one place the release
		// itself happens, ahead of whiff-punish #100 needing the same
		// knowledge). _PhysicsProcess's PRE-switch ResolveBlockAttempts()
		// call ran this same tick while State was still Held/Dribbling — the
		// shot only becomes InFlight here, INSIDE the TickHeld/TickDribbling
		// switch that call precedes — so the release tick itself was never
		// evaluated by that call: a defender whose Active window's last
		// frame is exactly this tick should connect per the half-open
		// interval semantics (ADR-0018 §2), but without this call the first
		// evaluation would be next tick, by which time that defender has
		// moved into Recovery.
		//
		// MUST run AFTER _arc is constructed above, not merely after
		// _inFlightStartTick is set: on success ResolveBlockAttempts
		// overwrites _arc.Velocity directly (the swat) — calling it any
		// earlier in this method would mutate a stale or (on the very first
		// shot) null arc instead of this shot's own.
		//
		// Cannot double-resolve the same shot: StateMachine.Current is now
		// InFlight (Shoot() already succeeded above), so this call is the
		// ONLY resolution attempt for this tick — the pre-switch call that
		// already ran this tick saw Held/Dribbling and no-op'd via its own
		// `StateMachine.Current != BallState.InFlight` guard.
		if (IsServer) ResolveBlockAttempts();

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
	/// hasDribbled appended LAST, after cleared (#193, doubt-driven review):
	/// kept as its own trailing bool rather than reusing an existing slot, the
	/// same transposition-safety convention PlayerController.ReceiveState uses
	/// for WasRecoveryEnteredEarly — two same-typed trailing params next to
	/// unrelated ones is exactly the positional-arg fragility this doc already
	/// warns about elsewhere in this file.
	[Rpc(MultiplayerApi.RpcMode.Authority,
		 CallLocal = false,
		 TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
	private void ReceiveState(int state, Vector3 pos, Vector3 vel, int holderPeerId, bool cleared, bool hasDribbled)
	{
		_serverState         = (BallState)state;
		_serverPos           = pos;
		_serverVel           = vel;
		_serverHolderPeerId  = holderPeerId;
		_serverCleared       = cleared;
		_serverHasDribbled   = hasDribbled;
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

		// HasDribbled (#193, doubt-driven review): same unconditional force-
		// correct as IsCleared, for the same reason. Without this, a client
		// whose local rebound/turnover prediction disagreed with the server's
		// actual recipient (ForceState above just repointed HolderPeerId at a
		// DIFFERENT peer than the client predicted) would keep whatever stale
		// HasDribbled value it last held — potentially TRUE for what the
		// corrected identity is really a brand-new possession — wrongly
		// refusing that player's next legitimate drive attempt until some
		// LATER real possession change happened to reset it. Unlike IsCleared's
		// already-accepted <=1-RTT cosmetic window, that staleness had no
		// natural expiry, so it needed the same broadcast treatment.
		HasDribbled = _serverHasDribbled;

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
