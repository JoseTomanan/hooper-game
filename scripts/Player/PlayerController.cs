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
	/// Shared with BehindTheBack — deadzone is a hardware-feel constant, not
	/// part of either move's tuning profile.
	/// </summary>
	[Export] public float ExitDeadzone { get; set; } = 0.15f;

	// ── Behind-the-back tuning (issue #194) ───────────────────────────────────
	// BehindTheBack shares CrossoverBurstMath/CrossoverBallSweep with Crossover
	// (composition, not a subclass of it — see BehindTheBack's doc) but is a
	// deliberately SAFER move: smaller burst, heavier gather bleed, narrower
	// exit cone. Recovery/Startup/Active frame counts differ too, but those
	// live on BehindTheBack.DefaultFrameData, not here.

	/// <summary>
	/// Lateral speed of BehindTheBack's Active-phase burst (m/s). Lower than
	/// Crossover's BurstSpeed (9) — "less explosive" per the spec. Bare feel
	/// default (33% below Crossover's), deferred to the per-milestone human
	/// pass like every other burst-family tunable (ADR-0015/CLAUDE.md).
	/// </summary>
	[Export] public float BehindTheBackBurstSpeed { get; set; } = 6.0f;

	/// <summary>
	/// Forward speed of BehindTheBack's Active-phase burst (m/s). Matches
	/// BehindTheBackBurstSpeed by default for the same symmetric-feel
	/// reasoning as Crossover's ForwardBurstScale. Bare feel default.
	/// </summary>
	[Export] public float BehindTheBackForwardBurstScale { get; set; } = 6.0f;

	/// <summary>
	/// Hard-decel rate (m/s²) BehindTheBack's Startup plant bleeds momentum
	/// at — the SAME hybrid-gather model #198 introduced for Crossover
	/// (ADR-0003 amendment), just tuned STEEPER: "heavier gather bleed" per
	/// the spec. At MoveSpeed's top speed (6 m/s) over 6 Startup ticks
	/// (0.1s), 55 leaves ~0.5 m/s surviving — noticeably less than
	/// Crossover's GatherDecel=40 (~2 m/s survives) while still not an
	/// instant zero (which would collapse back to the pre-#198 model this
	/// move deliberately keeps). Bare feel default, deferred sign-off.
	/// </summary>
	[Export] public float BehindTheBackGatherDecel { get; set; } = 55.0f;

	/// <summary>
	/// BehindTheBack's exit cone half-angle (degrees) — the left-stick exit
	/// vector is clamped to within this many degrees of the player's current
	/// heading before CrossoverBurstMath composes the burst (see its
	/// maxExitAngleRadians doc). "Fewer follow-up options" is modelled
	/// ONLY as this narrower cone (docs/handoffs/M9-move-taxonomy.md) —
	/// explicitly not a recovery penalty or a chain cooldown. 50 degrees
	/// (vs. Crossover's effectively unclamped ~180) still allows a genuine
	/// diagonal exit but rules out the "classic side-to-side shuffle"
	/// Crossover's pure-lateral row produces — you can't snap a sharp cut
	/// like a front cross. Bare feel default, deferred sign-off.
	/// </summary>
	[Export] public float BehindTheBackExitConeDegrees { get; set; } = 50.0f;

	// ── Step-back / retreat-dribble tuning (issue #197) ───────────────────────
	// The vertical-gesture counterpart to the crossover/hesitation horizontal
	// pair (#85/#86); frame data lives on RetreatDribble/StepBack's own
	// DefaultFrameData, not here.

	/// <summary>
	/// Backward speed of the retreat dribble's Active-phase hop (m/s).
	/// Deliberately modest — "a light, hesi-shaped move" per the spec, not a
	/// separation burst on the order of Crossover's BurstSpeed. Bare feel
	/// default, deferred to the per-milestone human pass (ADR-0015/CLAUDE.md).
	/// </summary>
	[Export] public float RetreatDribbleBurstSpeed { get; set; } = 4.0f;

	/// <summary>
	/// Backward speed of step-back's Active-phase burst (m/s) — shared for
	/// both the straight-back and back-lateral components
	/// (StepBackBurstMath passes this as both burstSpeed and
	/// forwardBurstScale). Higher than Crossover's BurstSpeed (9): the spec
	/// calls step-back "the biggest separation of any move in the
	/// taxonomy." Bare feel default, deferred sign-off.
	/// </summary>
	[Export] public float StepBackBurstSpeed { get; set; } = 10.0f;

	/// <summary>
	/// Step-back's exit-cone half-angle (degrees) — the left-stick exit
	/// vector is clamped to within this many degrees of TRUE BACKWARD (not
	/// forward, unlike every other exit-cone tunable in this file) before
	/// StepBackBurstMath composes the burst. "Back / back-left / back-right
	/// side-steps only" per the spec — comparable to BehindTheBack's 50°
	/// narrow cone, not Crossover's effectively unclamped one, since a
	/// step-back that could exit sideways-only or forward would no longer
	/// read as a retreat. Bare feel default, deferred sign-off.
	/// </summary>
	[Export] public float StepBackExitConeDegrees { get; set; } = 60.0f;

	// ── Between-the-legs tuning (issue #199) ──────────────────────────────────
	// "The balanced midpoint of the crossover family" — every tunable below is
	// a genuine midpoint between Crossover's and BehindTheBack's own value,
	// applying the identity literally rather than merely "somewhere in
	// between." Frame data lives on BetweenTheLegs.DefaultFrameData, not here.

	/// <summary>
	/// Lateral speed of BetweenTheLegs's Active-phase burst (m/s). Midpoint
	/// of Crossover's BurstSpeed (9) and BehindTheBack's
	/// BehindTheBackBurstSpeed (6) — "between Crossover (highest) and
	/// behind-the-back (lowest)" per the spec. Bare feel default, deferred to
	/// the per-milestone human pass like every other burst-family tunable
	/// (ADR-0015/CLAUDE.md).
	/// </summary>
	[Export] public float BetweenTheLegsBurstSpeed { get; set; } = 7.5f;

	/// <summary>
	/// Forward speed of BetweenTheLegs's Active-phase burst (m/s). Matches
	/// BetweenTheLegsBurstSpeed by default for the same symmetric-feel
	/// reasoning as Crossover's/BehindTheBack's own forward scales.
	/// </summary>
	[Export] public float BetweenTheLegsForwardBurstScale { get; set; } = 7.5f;

	/// <summary>
	/// Hard-decel rate (m/s²) BetweenTheLegs's Startup plant bleeds momentum
	/// at — the SAME hybrid-gather model #198 introduced for Crossover
	/// (ADR-0003 amendment). Midpoint of Crossover's GatherDecel (40) and
	/// BehindTheBack's BehindTheBackGatherDecel (55). Bare feel default,
	/// deferred sign-off.
	/// </summary>
	[Export] public float BetweenTheLegsGatherDecel { get; set; } = 47.5f;

	/// <summary>
	/// BetweenTheLegs's exit cone half-angle (degrees). Midpoint of
	/// BehindTheBack's narrowest cone (BehindTheBackExitConeDegrees, 50) and
	/// Crossover's effectively unclamped ~180 — "middle width" per the spec.
	/// Bare feel default, deferred sign-off.
	/// </summary>
	[Export] public float BetweenTheLegsExitConeDegrees { get; set; } = 115.0f;

	// ── In-and-out tuning (issue #202) ────────────────────────────────────────
	// The crossover's twin — same telegraph, no hand swap. Shares
	// CrossoverBurstMath.ComposeActiveVelocity with Crossover/BehindTheBack/
	// BetweenTheLegs (composition, not inheritance — see InAndOut's own class
	// doc), but at a REDUCED burst magnitude: the design call from the #202
	// grilling session is that an in-and-out is "fast, safe, small" (shorter
	// Startup, no transit-steal exposure, no hand-swap side-read invalidation)
	// against the crossover's "slow, risky, big" — the smaller burst is what
	// pays for the combined speed/safety advantage. Frame data lives on
	// InAndOut.DefaultFrameData, not here.

	/// <summary>
	/// Lateral speed of InAndOut's Active-phase burst (m/s). ~0.6x Crossover's
	/// BurstSpeed (9.0) per the #202 design session's locked relative-direction
	/// call ("in-and-out bursts LESS than crossover") — the EXACT scalar is
	/// feel, deferred to the consolidated human pass #173 (ADR-0021); do not
	/// tune this default without that pass. Only the relative comparison
	/// (InAndOutBurstSpeed &lt; BurstSpeed) is a locked, harness-assertable
	/// design call, not this specific magnitude.
	/// </summary>
	[Export] public float InAndOutBurstSpeed { get; set; } = 5.4f;

	/// <summary>
	/// Forward speed of InAndOut's Active-phase burst (m/s). Matches
	/// InAndOutBurstSpeed by default for the same symmetric-feel reasoning as
	/// Crossover's ForwardBurstScale. Bare feel default, deferred sign-off.
	/// </summary>
	[Export] public float InAndOutForwardBurstScale { get; set; } = 5.4f;

	// No separate InAndOutGatherDecel: InAndOut's Startup plant reuses
	// Crossover's own GatherDecel value directly (TickCommittedMoveBehavior's
	// Startup switch) — its Startup is shorter (4 ticks vs. Crossover's 6)
	// but otherwise an ordinary plant, not a heavier/lighter one by design, so
	// it doesn't need a fourth near-duplicate [Export] with nothing
	// distinguishing its value from GatherDecel/BehindTheBackGatherDecel/
	// BetweenTheLegsGatherDecel.

	// ── Spin tuning (issue #201) ───────────────────────────────────────────────
	// The family's last leaf: a full-body rotation, hand swap at the END of
	// Active (not JustEnteredActive — see Spin's own class doc). Shares
	// CrossoverBurstMath.ComposeActiveVelocity with the rest of the family
	// (composition, not inheritance), composed against the ENTRY heading/exit-
	// vector captured once at JustEnteredActive — see
	// TickCommittedMoveBehavior's Spin branch. Frame data lives on
	// Spin.DefaultFrameData, not here. The heading arc itself has NO tunable
	// magnitude here (it is always a full ~180°, scripted — see
	// SpinHeadingMath) — only the exit burst and the Startup plant are feel
	// knobs.

	/// <summary>
	/// Lateral speed of Spin's exit burst (m/s), applied once on the LAST
	/// Active tick (not JustEnteredActive). Defaults equal to Crossover's own
	/// BurstSpeed (9.0): a spin is a genuine separation move with the SAME
	/// explosive payoff as a crossover — it is not a safer/lesser variant the
	/// way BehindTheBack/BetweenTheLegs/InAndOut are (their reduced burst pays
	/// for a real safety/speed advantage a spin does not get: the largest
	/// pre-move exposure in the taxonomy, per the issue spec). Bare feel
	/// default, deferred to the consolidated human pass #173 (ADR-0021).
	/// </summary>
	[Export] public float SpinBurstSpeed { get; set; } = 9.0f;

	/// <summary>
	/// Forward speed of Spin's exit burst (m/s). Matches SpinBurstSpeed by
	/// default for the same symmetric-feel reasoning as the rest of the
	/// family's ForwardBurstScale twins. Bare feel default, deferred sign-off.
	/// </summary>
	[Export] public float SpinForwardBurstScale { get; set; } = 9.0f;

	/// <summary>
	/// Hard-decel rate (m/s²) Spin's Startup plant bleeds pre-existing
	/// momentum at — the SAME hybrid-gather model #198 introduced for
	/// Crossover. Own tunable (not shared with GatherDecel/
	/// BehindTheBackGatherDecel/BetweenTheLegsGatherDecel), following the
	/// established "own [Export] per opted-in move" pattern — Spin's 8-tick
	/// Startup (longer than Crossover's 6, per its own class doc's "bigger
	/// commitment" reasoning) has more time to bleed, so it is not simply a
	/// copy of an existing value. Bare feel default, deferred sign-off.
	/// </summary>
	[Export] public float SpinGatherDecel { get; set; } = 40.0f;

	/// <summary>
	/// Hard-decel rate (m/s²) DriveGather's Startup plant bleeds pre-existing
	/// (lateral) momentum at — the SAME hybrid-gather model #198 introduced
	/// for Crossover, per ADR-0022's explicit "reuse the existing model, do
	/// not invent a second one" instruction. Own tunable, not a shared
	/// constant, following the same "own [Export] per opted-in move" pattern
	/// as GatherDecel/BehindTheBackGatherDecel/BetweenTheLegsGatherDecel.
	/// Default sits within that established 40-55 range: a drive-gather's
	/// plant is a comparably fast commit, not a slower or harder one. Bare
	/// feel default, deferred to the per-milestone human pass like every
	/// other burst-family tunable (ADR-0015/CLAUDE.md).
	/// </summary>
	[Export] public float DriveGatherDecel { get; set; } = 45.0f;

	/// <summary>
	/// Forward drive-line burst magnitude (m/s), applied once on
	/// JustEnteredActive via DriveGatherMath.ComposeActiveVelocity. Matches
	/// Crossover's own BurstSpeed/ForwardBurstScale (9.0) — the fastest
	/// existing burst-family tunable — because a drive toward the rim is an
	/// explosive attacking commitment, not a shallower separation move. Bare
	/// feel default, deferred sign-off.
	/// </summary>
	[Export] public float DriveGatherBurstSpeed { get; set; } = 9.0f;

	/// <summary>
	/// Forward-toward-rim burst magnitude (m/s) for the euro-step (#231,
	/// ADR-0022), applied once on JustEnteredActive via
	/// EuroStepMath.ComposeActiveVelocity. Its OWN [Export] rather than sharing
	/// DriveGatherBurstSpeed — same "own tunable per move" pattern as
	/// BehindTheBack/BetweenTheLegs vs Crossover — so #238 can dial the
	/// euro-step's rim attack independently of the straight drive-gather's.
	/// Defaults equal to DriveGatherBurstSpeed (9.0): the euro-step's forward
	/// beat IS a drive, so the same explosive commitment is the natural starting
	/// point. Candidate, not final — catalogued in #238's consolidated pass.
	/// </summary>
	[Export] public float EuroStepForwardDriveSpeed { get; set; } = 9.0f;

	/// <summary>
	/// Fixed lateral hop magnitude (m/s) for the euro-step's evade (#231,
	/// ADR-0022), applied once on JustEnteredActive alongside the forward drive.
	/// A FIXED magnitude (the left stick chooses only the step's direction, not
	/// its size), mirroring RetreatDribbleBurstSpeed's fixed-hop precedent — a
	/// known displacement the defender can anticipate keeps the euro-step "a
	/// read, not a free dodge" (ADR-0003). Deliberately smaller than the forward
	/// drive: the step slides PAST the defender, it does not out-sprint them
	/// sideways. Candidate, not final — #238 owns the magnitude (and whether it
	/// ever becomes a stick-scaled cone instead).
	/// </summary>
	[Export] public float EuroStepLateralHopSpeed { get; set; } = 5.0f;

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
	/// (#102) Optional placeholder marker mesh (Player.tscn's "BeatenIndicator"
	/// child) toggled Visible while <see cref="DisplayBeaten"/> is true.
	/// Null-guarded — a scene without it (every code-built harness tree, and
	/// any older scene not yet re-saved) simply displays no cue, same
	/// degrade-gracefully posture as <see cref="_mesh"/> and <see cref="_animTree"/>.
	/// </summary>
	private Node3D _beatenIndicator;

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

	/// <summary>
	/// Harness observability (issue #242, ADR-0016): the display anim state
	/// ApplyAnimation last Traveled the state machine to. Not read by any
	/// gameplay or netcode path — cosmetic-only, same as <see cref="_currentAnimState"/>
	/// it mirrors.
	///
	/// NOTE this is the RESOLVER'S decision, not proof the AnimationTree
	/// actually entered that state — <c>Travel()</c> to a missing/misnamed
	/// state only logs a Godot engine error, it never throws or rolls this
	/// field back. A harness asserting against this alone would pass even if
	/// the .tscn's Pivot state/transitions were completely broken (found in
	/// #257's code review). Use <see cref="ActiveAnimNodeForHarness"/> to
	/// assert what the state machine actually did.
	/// </summary>
	internal MoveAnimState CurrentAnimStateForHarness => _currentAnimState;

	/// <summary>
	/// Harness observability (issue #242 code review, ADR-0016): the state
	/// machine's ACTUAL current node name, read live from
	/// <c>AnimationNodeStateMachinePlayback.GetCurrentNode()</c>. Unlike
	/// <see cref="CurrentAnimStateForHarness"/> — which only reflects what
	/// ApplyAnimation asked for — this reflects what the .tscn-authored
	/// state machine really did with that request, so a harness reading THIS
	/// is a genuine end-to-end proof of the AnimationTree wiring rather than
	/// a re-statement of the resolver's own decision. Empty string if the
	/// AnimationTree/playback never resolved (cosmetic degrade path).
	/// </summary>
	internal string ActiveAnimNodeForHarness => _animPlayback?.GetCurrentNode() ?? "";

	// ── Network state ─────────────────────────────────────────────────────────

	/// <summary>
	/// The seq of the last input the server applied for this player node.
	/// On the server: updated in SubmitInput, echoed via ReceiveState.
	/// On the client (own player): updated in ReceiveState, fed to
	/// _buffer.Acknowledge() to prune the prediction buffer.
	/// </summary>
	private int _serverAckedSeq;

	/// <summary>
	/// #224 fix: <see cref="_serverAckedSeq"/> value stamped the instant THIS
	/// player is awarded possession (rebound, turnover, make-it-take-it,
	/// tipoff — anything that fires <see cref="BallController.PossessionChanged"/>
	/// with this node's <see cref="OwnPeerId"/> as the new holder). Consulted
	/// ONLY by <see cref="TickServerRemotePlayer"/>'s freshness gate — see that
	/// method's doc for why this field must never be read from
	/// TickServerOwnPlayer or TickClientOwnPlayer.
	///
	/// ── Why a signal, not a parameter threaded through AwardPossession ──────
	/// BallController already diffs holder/cleared every tick and emits
	/// PossessionChanged exactly once per change (EmitPossessionIfChanged) —
	/// reusing that existing plumbing (the same one PossessionHud/CourtVisuals
	/// already subscribe to) needs no new cross-object contract. Subscribed
	/// lazily wherever <see cref="GetBall"/> first resolves a non-null ball,
	/// mirroring _ball's own lazy-group-lookup doc (a player's _Ready can race
	/// ahead of the ball's in the scene tree).
	///
	/// ── Why same-tick ordering is safe, not a race ──────────────────────────
	/// scenes/Main.tscn ticks the "Players" subtree before the sibling "Ball"
	/// node every physics frame (verified: Main.tscn declares Players at
	/// unique_id=1470573220 before Ball at unique_id=783251786). An award
	/// (AwardPossession/TryAssignTipoffHolder) always runs INSIDE BallController's
	/// own _PhysicsProcess, i.e. strictly after this tick's Players phase has
	/// already run — so the tick that awards possession cannot itself have
	/// already read the stale gate with a stamp that lags the award; the
	/// EARLIEST tick IsBallHolder can read true for the new holder is the tick
	/// AFTER the award, by which time this field (updated synchronously by the
	/// signal, during the award's own tick) is already fresh. No ordering
	/// dependency between the award and the stamp remains to race.
	/// </summary>
	private int _awardStampSeq;

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
	/// intentionally-gated _pendingInput.
	///
	/// (#210 — DEMOTED to fallback-only.) This field is streamed continuously
	/// over UnreliableOrdered SubmitInput, so under jitter/packet loss the
	/// freshest value cached here at the server's OWN JustEnteredActive tick
	/// can be several ticks stale relative to what the client actually read
	/// at ITS OWN JustEnteredActive tick — a genuine 5-30 degree burst-angle
	/// divergence between client and server with no self-heal (issue #210).
	/// <see cref="_authoritativeExitVector"/> (the discrete
	/// <see cref="RequestExitVector"/> RPC) is now the PRIMARY source read by
	/// <see cref="TickServerRemotePlayer"/>; this field is consulted only as
	/// the fallback for the residual case where that Reliable RPC has not yet
	/// been processed by the time it's needed — see
	/// <see cref="_authoritativeExitVector"/>'s doc for why that residual
	/// can't be proven to never happen. Still read ONLY inside
	/// TickCommittedMoveBehavior's burst-family branches; never substituted
	/// into the Move() call the way _pendingInput is.
	/// </summary>
	private Vector2 _pendingRawStick;

	/// <summary>
	/// The exit vector delivered by the client's discrete, one-shot
	/// <see cref="RequestExitVector"/> RPC for the CURRENTLY active committed
	/// move — issue #210's fix for the divergence <see cref="_pendingRawStick"/>'s
	/// doc describes. <c>null</c> means "not yet received for this move" (the
	/// server falls back to <see cref="_pendingRawStick"/> in that case — see
	/// <see cref="TickServerRemotePlayer"/>).
	///
	/// ── Why a discrete RPC succeeds where the streamed cache didn't ────────
	/// <see cref="_pendingRawStick"/>'s OWN doc already explains why the exit
	/// vector couldn't ride the EXISTING moveId/moveParam one-shot RPC
	/// (RequestBeginMove/BurstDirection): that RPC fires at Begin()
	/// (Startup-entry), 6 frames before Active begins for a default Crossover
	/// — the stick's future value is unknowable that early. This is a
	/// DIFFERENT, NEW one-shot RPC, fired at the CLIENT's own
	/// <c>JustEnteredActive</c> tick instead (see <see cref="TickClientOwnPlayer"/>)
	/// — exactly when the value is both known and needed — so it does not
	/// have that problem. It still needs to be Reliable (a dropped exit
	/// vector would be a correctness bug, not a smoothing concern — see
	/// <see cref="RequestExitVector"/>'s own doc), which is precisely what an
	/// UnreliableOrdered stream cannot promise.
	///
	/// ── Reset discipline (doubt-driven-development, #210) ──────────────────
	/// Cleared to <c>null</c> at the very TOP of <see cref="BeginCommittedMove"/>
	/// — unconditionally, on EVERY attempt, not only a successful one — so a
	/// stray value from a move the SERVER rejected (e.g. the dead-Held gate)
	/// can never survive to pollute a LATER, unrelated move. Combined with the
	/// phase gate inside <see cref="RequestExitVector"/> itself (only stores
	/// while the server's OWN machine is genuinely Startup/Active for a
	/// qualifying move), this does not rely SOLELY on ENet's same-channel
	/// reliable-ordering guarantee between RequestBeginMove and
	/// RequestExitVector (verified against Godot's own docs — see that RPC's
	/// class doc — but treated as defense-in-depth, not the only guard, per
	/// the doubt cycle finding that the "different RPC methods" granularity
	/// isn't explicitly spelled out there).
	/// </summary>
	private Vector2? _authoritativeExitVector;

	/// <summary>
	/// Spin's heading-arc anchor (issue #201) — the authoritative Heading at
	/// the instant this role's OWN local machine entered Active, captured
	/// ONCE (see TickCommittedMoveBehavior's Spin branch, gated on
	/// JustEnteredActive) and reused every subsequent Active tick as
	/// SpinHeadingMath.ArcHeading's anchor. Never re-read from the live
	/// Heading field mid-arc — by the second Active tick, Heading itself has
	/// already been overwritten by the previous tick's arc value, so re-
	/// reading it would compound rounding drift and — far more importantly —
	/// would reintroduce exactly the live-state dependency SpinHeadingMath's
	/// class doc explains this move must NOT have for cross-role determinism.
	///
	/// Bounded, documented, ACCEPTED trade-off (doubt-driven-development pass,
	/// #201): on the CLIENT'S OWN predicted player only, ReconcileFromServer
	/// unconditionally snaps `Heading = authHeading` (a ~1-RTT-stale value)
	/// every tick a broadcast lands, BEFORE this field's JustEnteredActive
	/// capture runs later that same tick. If a broadcast happens to land on
	/// the exact tick the client's own local prediction enters Active, this
	/// field captures that stale snap rather than the client's own smoothly-
	/// evolved pre-move heading — a bounded, self-correcting, SINGLE-INSTANCE
	/// visual artifact on the predicting client's OWN screen of its OWN spin.
	/// This can never desync the AUTHORITATIVE outcome or the REMOTE
	/// opponent's view: the server's own tick (whether for its own player or
	/// its copy of a remote player) never runs ReconcileFromServer at all, and
	/// TickClientRemotePlayer (the opponent's copy on this client) never runs
	/// this arc locally — it just displays the server's broadcast Heading
	/// directly every tick. Same class of accepted, bounded trade-off as the
	/// pre-M4 positional-smoothing artifact and the burst's own #210
	/// exitVectorSample residual (narrowed, not eliminated, by the
	/// RequestExitVector RPC fix — see <see cref="_authoritativeExitVector"/>'s
	/// doc) — documented on the record here rather than engineered around,
	/// per this issue's doubt-driven pass.
	/// </summary>
	private float _spinEntryHeading;

	/// <summary>
	/// Spin's exit-vector anchor (issue #201) — the left-stick reading
	/// captured ONCE at JustEnteredActive, alongside <see cref="_spinEntryHeading"/>,
	/// and reused for the exit-burst composition on the LAST Active tick.
	///
	/// Doubt-driven-development finding (#201): an earlier draft read the
	/// live exitVectorSample parameter directly on the final Active tick
	/// instead of capturing it here. That would have let a player keep
	/// steering the ENTIRE multi-tick rotation and have the LAST-instant
	/// stick position decide the exit direction — reading the defender's
	/// real-time reaction throughout the whole spin before committing, unlike
	/// every sibling move (Crossover/BehindTheBack/BetweenTheLegs/InAndOut),
	/// which all lock their burst direction in at Active-ENTRY and are immune
	/// to post-commit stick movement (ADR-0003: a committed move's parameters
	/// lock at commitment, not at whatever is convenient once the outcome is
	/// visible). Capturing here, once, at the SAME tick and from the SAME
	/// per-role source (ReadInput()/_pendingRawStick/rawStick) every sibling
	/// move already snapshots from, closes that hole AND makes Spin's per-role
	/// snapshot divergence properties identical to Crossover's own (inherits
	/// the SAME #210 residual — narrowed by the RequestExitVector RPC fix
	/// exactly like every other burst-family move's, not a wider one).
	/// </summary>
	private Vector2 _spinEntryExitVector;

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

	/// <summary>
	/// (#102) Authoritative "beaten" (whiff-punish blow-by, issue #100) flag
	/// from the latest broadcast — display-only, mirroring _serverEndedActiveEarly's
	/// staging pattern. See <see cref="DisplayBeaten"/> for why every role but
	/// the server itself must read this instead of the local <c>_beaten</c>
	/// field: only the server ever judges a whiff (BallController.
	/// ResolveBeatenWindowTriggers is IsServer-gated), so a client can't
	/// locally know it was just ruled beaten — not even for its OWN player.
	/// Level-triggered like _serverEndedActiveEarly: the sender recomputes
	/// IsBeaten fresh every tick and resends it (even a redundant "still
	/// true"/"still false"), so a single dropped UnreliableOrdered packet
	/// can't erase the client's only chance to observe the window.
	/// </summary>
	private bool _serverIsBeaten;

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
		{
			_ball = GetTree().GetFirstNodeInGroup("ball") as BallController;
			// #224 fix: subscribe exactly once, the first tick the ball actually
			// resolves (mirrors PossessionHud's PossessionChanged subscription
			// pattern). See _awardStampSeq's doc for why this signal, not a
			// parameter threaded through AwardPossession, is the fix's seam.
			if (_ball != null)
				_ball.PossessionChanged += OnPossessionChangedForAwardStamp;
		}
		return _ball;
	}

	/// <summary>
	/// Resolves a steal attempt's target <see cref="HandSide"/> from the
	/// defender's raw aim SIGN, transformed into the ball-holder's
	/// body-relative frame via the relative heading between the two players
	/// (issue #254 fix; ADR-0010's server-authoritative <see cref="Heading"/>
	/// feeds both sides — never the cosmetic <c>FacingResolver</c>).
	///
	/// ── Root cause this replaces ────────────────────────────────────────
	/// The old code compared the defender's raw aim.X sign directly against
	/// the holder's body-relative HandSide with no transform at all
	/// (<c>aim.X > 0 ? Right : Left</c>). That silently inverted whenever the
	/// two players faced each other — a defender's own body-right is not the
	/// same world side as the holder's body-right once the holder has turned
	/// to face the defender.
	///
	/// ── Why this is called from BOTH ends, not just once ───────────────
	/// SampleMoveInput calls this for the LOCAL prediction (best-available
	/// data: own live Heading, holder's latest broadcast Heading).
	/// ApplyRequestedMove's "steal" branch calls it AGAIN, server-side, using
	/// the SERVER's own authoritative Heading for both players — so the value
	/// that actually reaches DefensiveResolution.StealSucceeds is always
	/// computed from truth, never trusted verbatim from the client (ADR-0002).
	/// Only the RAW aim sign rides the wire (mirrors the crossover family's
	/// BurstDirection convention), not a pre-resolved enum — see
	/// SampleMoveInput's steal block and RequestBeginMove's "steal" case.
	///
	/// The actual geometry (continuous in the relative heading, not a blanket
	/// flip) lives in HandStateResolver.TargetHandFromAim — see that method's
	/// doc for the derivation.
	///
	/// Falls back to the OLD naive mapping (no transform) when the ball has no
	/// live holder to be relative to (e.g. a Loose ball, or pre-tipoff) —
	/// there is no meaningful "holder facing" to transform against in that
	/// case, and StealMove's own resolution already no-ops against a
	/// non-Dribbling ball.
	///
	/// ── Accepted residual: local prediction can briefly disagree with the
	///    server's resolved TargetHand (doubt-cycle finding, #254) ──────────
	/// Because SampleMoveInput's call reads the HOLDER's ~1-RTT-stale
	/// broadcast Heading while ApplyRequestedMove's call reads the server's
	/// live authoritative Heading for both players, the two ends can pick a
	/// DIFFERENT HandSide near the boundary (e.g. a holder actively spinning
	/// through ~90° relative heading during the round trip). This is the
	/// same class of accepted, self-correcting staleness every other
	/// predicted-then-reconciled field in this file already has (§4c/§11 of
	/// the hooper-netcode-reference skill) — not a new divergence class —
	/// and it is harmless to correctness: StealMove's outcome is never
	/// client-predicted at all (only its Startup/Active timing is), so the
	/// only visible effect of a locally mispredicted target is a brief
	/// "reaching toward the other hand" animation cosmetic, self-correcting
	/// on the next ReceiveState broadcast exactly like a mispredicted
	/// Crossover already does today.
	/// </summary>
	private HandSide ResolveStealTargetHand(float aimSign, BallController ball)
	{
		PlayerController holder = ball?.Players?.GetNodeOrNull<PlayerController>(
			ball.StateMachine.HolderPeerId.ToString());
		if (holder == null)
			return aimSign > 0f ? HandSide.Right : HandSide.Left;

		return HandStateResolver.TargetHandFromAim(aimSign, Heading, holder.Heading);
	}

	/// <summary>
	/// Tracks the last holderPeerId THIS handler observed, across every
	/// PossessionChanged emission (not just ones addressed to THIS player) —
	/// see OnPossessionChangedForAwardStamp's doc for why this is required.
	/// -1 means "nothing observed yet" (0 is a legitimate "no holder" value
	/// BallController._lastEmittedHolder's own doc already reserves -1 for
	/// the analogous reason).
	/// </summary>
	private int _lastSeenHolderPeerIdForAwardStamp = -1;

	/// <summary>
	/// #224 fix: stamps <see cref="_awardStampSeq"/> the instant possession is
	/// awarded to THIS player. See that field's doc for the full rationale.
	/// Filtered to this node's own peer — every PlayerController instance on
	/// a given peer subscribes to the SAME ball singleton, so every award
	/// (regardless of which player it goes to) fires this handler on every
	/// player node; only the node whose OwnPeerId matches the new holder
	/// should update its stamp.
	///
	/// (Code-review fix) BallController.EmitPossessionIfChanged fires
	/// PossessionChanged whenever EITHER the holder OR the cleared flag
	/// differs from the last emit — not only on a genuine new award. A
	/// player who is ALREADY the holder can trigger a cleared-only emission
	/// (e.g. carrying the ball across the clear line, ADR-0008's take-it-
	/// back rule, UpdateClearStatus) with no possession change at all. A
	/// naive "holderPeerId == OwnPeerId" filter would re-stamp on THAT event
	/// too, needlessly re-closing the freshness gate for ~1 tick on a drive
	/// that was already legitimately in progress. Guarding on
	/// _lastSeenHolderPeerIdForAwardStamp actually CHANGING restricts the
	/// re-stamp to genuine award transitions only, matching this method's
	/// own doc ("the instant possession is awarded").
	/// </summary>
	private void OnPossessionChangedForAwardStamp(int holderPeerId, bool cleared)
	{
		bool holderChanged = holderPeerId != _lastSeenHolderPeerIdForAwardStamp;
		_lastSeenHolderPeerIdForAwardStamp = holderPeerId;
		if (!holderChanged) return; // same holder as last emission -- a cleared-only toggle, not a new award

		if (holderPeerId != OwnPeerId) return;
		_awardStampSeq = _serverAckedSeq;
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
	/// Generic Active-phase move read, consolidating what used to be two
	/// separate copies — ActiveStealTargetHand and BlockMoveActiveInterval —
	/// of the exact same shape: "is this player's machine Active on move type
	/// TMove right now?" (issue #216 original body row 2; a future contest
	/// move, #99, would otherwise have grown a third copy). Returns null on
	/// any tick this player's machine is not Active, or is Active on a
	/// DIFFERENT move type; otherwise the live move instance (typed as TMove,
	/// so a caller can read whatever move-specific payload it needs —
	/// StealMove.TargetHand, BlockMove's FrameData.ActiveFrames, a future
	/// ContestMove's own fields) plus the current FrameInPhase (needed for
	/// absolute-tick interval arithmetic, e.g.
	/// BallController.ResolveBlockAttempts).
	///
	/// BallController's steal/block resolvers read this every physics tick
	/// (server-only). The null return is the fast path — most ticks nobody is
	/// mid-defensive-move — so callers do a simple null-guard with zero
	/// overhead on inactive defenders.
	///
	/// Why the WHOLE Active phase, not just its entry tick (JustEnteredActive):
	/// ADR-0018 defines both steal and block success as the Active window
	/// OVERLAPPING another window (the exposed dribble band, or the shot's
	/// vulnerable window) — an interval relationship. Sampling only the
	/// single entry tick collapses that interval to a point and makes the
	/// Active window's width inert, so a defender who enters Active a tick
	/// early (before the other window opens) but whose window fully covers it
	/// would wrongly whiff. Reporting the move on every Active tick lets the
	/// resolver re-check the live opposing state each tick and succeed on the
	/// first overlapping tick — the interval model, evaluated against
	/// ground-truth phase with no projection. (Note: IsActive is the WRONG
	/// check — it is true for Startup/Recovery too.)
	/// </summary>
	public (TMove Move, int FrameInPhase)? ActiveMove<TMove>() where TMove : CommittedMove
	{
		if (_machine.Phase != MovePhase.Active) return null;
		return _machine.CurrentMove is TMove move ? (move, _machine.FrameInPhase) : ((TMove, int)?)null;
	}

	/// <summary>
	/// Server-only: ends this defender's defensive-move Active phase the instant
	/// BallController resolves it as a success (ResolveStealAttempts /
	/// ResolveBlockAttempts), paying Recovery immediately instead of riding out
	/// the remaining ActiveFrames.
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
	/// defensive move can produce at most one turnover, matching every other
	/// committed move's "spent once, then Recovery" contract. The block (#98)
	/// shares the contract for the same reason — its remaining Active ticks
	/// must not linger over the scramble it just created (and the successful
	/// defender is freed to contest that scramble instead of standing planted).
	/// </summary>
	public bool EndResolvedDefensiveMove() => _machine.EndActiveEarly();

	// ── Beaten window / blow-by lane (issue #100, ADR-0018 Amendment 2026-07-16) ──

	/// <summary>
	/// Server-authoritative: this defender's current beaten window, if any.
	/// See <see cref="BeatenWindow"/>'s class doc for the full mechanic. Only
	/// ever mutated by <see cref="TriggerBeatenWindow"/> (never ticked down
	/// itself — <see cref="IsBeaten"/> is a pure comparison against a tick
	/// the caller already knows, so there is nothing to advance per-frame).
	/// </summary>
	private BeatenWindow _beaten = BeatenWindow.None;

	/// <summary>
	/// Puts this defender into a beaten window through
	/// <paramref name="currentTick"/> + <paramref name="windowTicks"/>
	/// (exclusive) — the whiff-punish blow-by lane. While active, this
	/// defender's contest (both the committed <c>ContestMove</c> factor and
	/// the passive proximity scatter factor, ADR-0009) is suppressed against
	/// whichever handler is shooting (BallController.ApplyShootLocally reads
	/// <see cref="IsBeaten"/> for the defending peer).
	///
	/// Deliberately generic — this is the ONE choke point any caller uses to
	/// grant the blow-by, not a private detail of the steal-whiff path. A
	/// failed steal (this issue) is the first caller;
	/// <c>ResolveStealAttempts</c>'s whiff detection calls this exactly like
	/// a future failed crossover-transit steal (#196) is expected to.
	/// </summary>
	/// <param name="currentTick">The current physics tick (caller's single source of truth — see BeatenWindow's doc on why this struct never reads engine time itself).</param>
	/// <param name="windowTicks">Window length in ticks. Feel-only tunable; see BallController.BlowByWindowTicks.</param>
	public void TriggerBeatenWindow(int currentTick, int windowTicks) =>
		_beaten = BeatenWindow.Trigger(currentTick, windowTicks);

	/// <summary>
	/// True while this defender is inside a beaten window at
	/// <paramref name="currentTick"/>. See <see cref="TriggerBeatenWindow"/>.
	/// </summary>
	public bool IsBeaten(int currentTick) => _beaten.IsActive(currentTick);

	/// <summary>
	/// Test-only observability hook (issue #100): the raw tick the current
	/// beaten window expires on (exclusive), or <c>int.MinValue</c> if none
	/// is active. Exposed as the raw boundary rather than a bool so the
	/// harness (and later #102's telegraph remote sync) can assert the
	/// window's exact length against the live BlowByWindowTicks export,
	/// instead of only its presence/absence.
	/// </summary>
	internal int BeatenUntilTickForHarness => _beaten.UntilTick;

	/// <summary>
	/// True for exactly the one tick this defender's committed move of type
	/// <typeparamref name="TMove"/> naturally expired from Active into
	/// Recovery WITHOUT ever resolving early via
	/// <see cref="EndResolvedDefensiveMove"/> — i.e. a genuine whiff, not a
	/// per-tick miss that the move might still recover from on a later
	/// Active tick (see StealMove's own per-Active-tick re-check, ADR-0018
	/// Amendment 2026-07-01).
	///
	/// Built from the SAME <c>WasRecoveryEnteredEarly</c> level-triggered bit
	/// reconciliation already relies on (issue #175) — a successful
	/// resolution calls <c>EndActiveEarly()</c>, which sets that bit true, so
	/// "Recovery entered AND NOT early" is exactly "the whole Active window
	/// ran out with no success." One-shot like <c>JustEnteredActive</c>: true
	/// only on the tick FrameInPhase resets to 0 entering Recovery, false on
	/// every later Recovery tick.
	///
	/// Generic over <typeparamref name="TMove"/> so a future caller (#196's
	/// transit-steal whiff, or any other defensive move that wants to grant
	/// the same punish) can reuse this detector for its own move type without
	/// duplicating the CommittedMoveMachine field-reading logic.
	/// </summary>
	public bool JustWhiffedDefensiveMove<TMove>() where TMove : CommittedMove =>
		_machine.Phase == MovePhase.Recovery
		&& _machine.FrameInPhase == 0
		&& !_machine.WasRecoveryEnteredEarly
		&& _machine.CurrentMove is TMove;

	// ── Role helpers ──────────────────────────────────────────────────────────

	private bool IsServer      => Multiplayer.IsServer();
	private bool IsLocalPlayer => Name == Multiplayer.GetUniqueId().ToString();

	/// <summary>
	/// The current physics tick, same definition and same underlying engine
	/// counter as <c>BallController.PhysicsTick</c> (both read
	/// <c>Engine.GetPhysicsFrames()</c> directly rather than hand-rolling a
	/// counter). Reading it from either node during the SAME physics tick on
	/// the SAME process returns the identical value — the engine increments
	/// this counter once per tick, not per node — which is what makes it safe
	/// to pass to <see cref="IsBeaten"/> here even though the window itself
	/// was stamped by <c>BallController.ResolveBeatenWindowTriggers</c> via
	/// its own <c>PhysicsTick</c> property, not this one.
	/// </summary>
	private int PhysicsTick => (int)Engine.GetPhysicsFrames();

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

		// (#102) Optional placeholder cue for the whiff-punish "beaten" window
		// (issue #100). Bespoke clips are out of scope (à la #70) — a simple
		// visibility toggle on an unshaded marker mesh is the minimum readable
		// cue, matching this codebase's existing "placeholder is fine" bar
		// (e.g. the made-shot green flash, BallController.OnScoreChanged).
		// Optional/null-guarded: harness-built trees (StealTurnoverTest et al.)
		// never instance Player.tscn, so they simply have no indicator to toggle.
		_beatenIndicator = GetNodeOrNull<Node3D>("BeatenIndicator");
	}

	public override void _ExitTree()
	{
		// #224 fix: drop the PossessionChanged subscription so Godot doesn't
		// hold a dangling reference to a freed player node — same lifecycle
		// hygiene as PossessionHud's own PossessionChanged unsubscribe.
		if (_ball != null)
			_ball.PossessionChanged -= OnPossessionChangedForAwardStamp;
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
		ApplyBeatenCue();
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
		// (#102) isBeaten appended LAST of all, as its own trailing bool —
		// same reasoning as endedActiveEarly: a lone bool at the very end
		// can't be transposed with any neighboring same-typed param. Computed
		// fresh here (IsBeaten against THIS tick), not read from a cache, so
		// it always reflects this broadcast's own instant.
		Rpc(MethodName.ReceiveState, 0, GlobalPosition, Velocity,
			(int)_machine.Phase, _machine.FrameInPhase, MoveIdOf(_machine.CurrentMove), MoveParamOf(_machine.CurrentMove),
			Heading, (int)HandSide, _machine.WasRecoveryEnteredEarly, _pivot.HasLatch, _pivot.LatchedYaw,
			IsBeaten(PhysicsTick));
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
			// #210 fix: PREFER the exit vector delivered by the client's
			// discrete RequestExitVector RPC (fired at the CLIENT's own
			// JustEnteredActive, the exact tick the value is needed) over the
			// old continuously-streamed _pendingRawStick cache, which under
			// jitter/loss could be several ticks stale relative to what the
			// client actually read. Falls back to _pendingRawStick ONLY if
			// the Reliable RPC has not yet been processed by this tick — see
			// RequestExitVector's doc for why that can't be proven to NEVER
			// happen (a timing race under jitter, not a guarantee either
			// way); bounded to no worse than the pre-#210 status quo in that
			// residual case.
			TickCommittedMoveBehavior(delta, _authoritativeExitVector ?? _pendingRawStick);
		else
		{
			Move(_pendingInput, delta);

			// #224 fix: only honor drive intent from _pendingInput once a
			// genuinely FRESH (post-award) SubmitInput has actually arrived —
			// see _awardStampSeq's doc for the full race and why this gate is
			// scoped to THIS call site only (TickServerOwnPlayer/
			// TickClientOwnPlayer read same-tick hardware input and are already
			// immune; gating them here too would permanently freeze the
			// host's own drive, since the host never calls SubmitInput on
			// itself and its _serverAckedSeq never advances past 0).
			//
			// Repo law: never force-match a ~1RTT-stale broadcast; only force
			// discrete identity (project_networked_discrete_state_staleness).
			// Applied here in the mirror direction — never TRUST a ~1RTT-stale
			// input to drive a discrete transition either. A held drive keeps
			// producing rising-seq packets, so this gate opens within ~1 tick
			// of the award for a genuine drive; a released stick's stale cache
			// carries no seq newer than the award and stays gated shut for the
			// rest of this possession (until CheckAutoStartDribble's own
			// IsBallHolder/HasDribbled guards make it moot anyway).
			if (_serverAckedSeq > _awardStampSeq)
				CheckAutoStartDribble(_pendingInput);
		}

		// Echo _serverAckedSeq so the client prunes its pending buffer, plus
		// the committed-move state and heading piggybacked on the same broadcast
		// (see ReceiveState below for the payload rationale). (#175) Same
		// trailing WasRecoveryEnteredEarly bool as TickServerOwnPlayer's broadcast.
		// (#172) Same trailing pivotHasLatch/pivotLatchedYaw pair too.
		// (#102) Same trailing isBeaten bool too — see TickServerOwnPlayer's
		// broadcast comment. Both server-side broadcast call sites must stay
		// in sync on this trailing arg by hand; there is no shared factory
		// (matching this file's existing two-call-site convention for every
		// prior trailing-param addition — #175, #172).
		Rpc(MethodName.ReceiveState, _serverAckedSeq, GlobalPosition, Velocity,
			(int)_machine.Phase, _machine.FrameInPhase, MoveIdOf(_machine.CurrentMove), MoveParamOf(_machine.CurrentMove),
			Heading, (int)HandSide, _machine.WasRecoveryEnteredEarly, _pivot.HasLatch, _pivot.LatchedYaw,
			IsBeaten(PhysicsTick));
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
		{
			// #210 fix: tell the server the EXACT exit vector THIS role's own
			// local prediction is about to compose its burst from — fired
			// ONCE, at THIS role's own JustEnteredActive tick, never at
			// Begin() (see _authoritativeExitVector's doc for why Begin-time
			// is 6 frames too early to know the stick's future value). Only
			// the six moves that actually read exitVectorSample need this
			// (see TickCommittedMoveBehavior's param doc); every other
			// committed move (JumpShot, StealMove, ...) skips the call
			// entirely — RequestExitVector's own phase gate would discard it
			// anyway, but there's no reason to send it at all.
			if (_machine.JustEnteredActive && _machine.CurrentMove is Crossover or BehindTheBack
				or BetweenTheLegs or InAndOut or StepBack or Spin)
				RpcId(1, MethodName.RequestExitVector, rawStick.X, rawStick.Y);

			// Local zero-lag prediction: rawStick, not moveInput (moveInput is
			// deliberately zeroed above for Move()/replay purposes — see the
			// comment on that line — but the exit-vector snapshot needs the
			// TRUE stick, #198).
			TickCommittedMoveBehavior(delta, rawStick);
		}
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
	/// Called BY THE CLIENT on the SERVER's copy of this node — issue #210's
	/// fix. Fired ONCE, from <see cref="TickClientOwnPlayer"/>, at the
	/// CLIENT's own local <c>JustEnteredActive</c> tick for one of the six
	/// burst-family moves that read an exit vector (Crossover, BehindTheBack,
	/// BetweenTheLegs, InAndOut, StepBack, Spin — see
	/// <see cref="TickCommittedMoveBehavior"/>'s <c>exitVectorSample</c> param
	/// doc for the full list; RetreatDribble/DriveGather/EuroStep never read
	/// the stick, so they never send this and are unaffected by #210).
	///
	/// Transfer mode: Reliable — same one-time-discrete-event reasoning as
	/// <see cref="RequestBeginMove"/> (see that RPC's doc): a dropped exit
	/// vector is a correctness bug (the server permanently falls back to the
	/// stale <see cref="_pendingRawStick"/> cache for this move), not a
	/// smoothing concern, so it must retransmit rather than silently vanish
	/// the way an UnreliableOrdered <see cref="SubmitInput"/> drop is allowed
	/// to.
	///
	/// No explicit channel override, matching RequestBeginMove/RequestFeint —
	/// Godot's high-level multiplayer API gives each TRANSFER MODE its own
	/// underlying ENet channel regardless of which RPC method sent it ("the
	/// default channel with index 0 is actually three different channels —
	/// one for each transfer mode," per Godot's own networking docs), and
	/// ENet's reliable channels deliver packets in the exact order sent. Since
	/// the client's own local phase machine can only send RequestBeginMove(B)
	/// for a NEW move after RequestExitVector(A) for whatever move A preceded
	/// it (a new move cannot begin locally until the prior one fully cycles
	/// through Active → Recovery → Inactive), this ordering means A's exit
	/// vector is always PROCESSED before B's Begin() request arrives — so a
	/// stale echo of A can never overwrite a freshly-reset slot for B by
	/// arriving late.
	///
	/// That said (doubt-driven-development finding, #210): Godot's own docs
	/// don't spell out the ordering guarantee at the "two different RPC
	/// method names" granularity, only "same channel ⇒ same transfer mode."
	/// This method therefore does NOT rely on that ordering ALONE — see the
	/// phase gate below, which is defense-in-depth regardless of whether the
	/// ordering claim above holds in every Godot/ENet version.
	///
	/// The phase gate: even a stray/rejected exit vector (the client's OWN
	/// local prediction reached JustEnteredActive for a move the SERVER
	/// rejected — e.g. the dead-Held gate in <see cref="BeginCommittedMove"/>)
	/// is only STORED if the server's own machine is CURRENTLY Startup or
	/// Active for one of the six qualifying move types — i.e. genuinely
	/// mid-move on the server's own authoritative copy. A stray value
	/// arriving while nothing legitimate is running is discarded rather than
	/// left to pollute <see cref="_authoritativeExitVector"/> until some later,
	/// unrelated Begin() happens to reset it. Combined with
	/// <see cref="BeginCommittedMove"/> unconditionally clearing that field at
	/// the TOP of every attempt (successful or not), a stale value from move A
	/// cannot be applied to a later move B.
	///
	/// Security: same sender check as SubmitInput/RequestBeginMove.
	/// </summary>
	[Rpc(MultiplayerApi.RpcMode.AnyPeer,
		 CallLocal = false,
		 TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void RequestExitVector(float x, float y)
	{
		int senderId = Multiplayer.GetRemoteSenderId();
		if (senderId.ToString() != Name)
		{
			GD.PrintErr($"[PlayerController] Unauthorized RequestExitVector from peer {senderId} for node '{Name}'");
			return;
		}

		bool inQualifyingPhase = _machine.Phase is MovePhase.Startup or MovePhase.Active;
		bool isQualifyingMove = _machine.CurrentMove is Crossover or BehindTheBack or BetweenTheLegs
			or InAndOut or StepBack or Spin;
		if (!inQualifyingPhase || !isQualifyingMove) return;

		_authoritativeExitVector = new Vector2(x, y);
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
	private bool BeginCommittedMove(CommittedMove move, bool clientWasAlreadyDribbling = false)
	{
		// #210 doubt-driven-development fix: unconditionally clear any
		// previously-received exit vector at the START of every attempt, not
		// only a successful one — see _authoritativeExitVector's field doc.
		// A move the SERVER goes on to REJECT (e.g. the dead-Held gate a few
		// lines down) must not leave a stale value sitting in that field for
		// whatever move comes next to inherit.
		_authoritativeExitVector = null;

		// #193 code-review fix: a Crossover/Hesitation IS a dribble move in
		// real ball — it cannot legally begin while this player HOLDS the
		// ball in Held state, dead OR live. Without this gate, JumpShot was
		// the only move #193 special-cased, so a dead-Held player (post-
		// cradle, or post a canceled pump-fake) could still Begin a
		// crossover: the burst fires and the JustEnteredActive HandSide flip
		// (TickCommittedMoveBehavior) authoritatively moves the HELD ball to
		// the other hand — a sweep transit since #195, no longer a teleport —
		// escaping the dead-dribble rule's whole point —
		// #193's own "stranded in dead Held" cost became avoidable. From a
		// LIVE Held possession the fix is the same: the player must push the
		// stick to start dribbling first (CheckAutoStartDribble ->
		// BallController.TryStartDribble), matching real ball's "you can't
		// crossover a ball you haven't started bouncing yet."
		//
		// Gated on IsBallHolder specifically — a player WITHOUT the ball
		// (defense) is never affected; their crossover/hesitation attempts
		// are unrelated to possession state.
		//
		// BehindTheBack (#194) is gated the same way — it is ALSO a dribble
		// move in real ball (the hand-swap only makes sense off a live
		// dribble), so the dead-dribble rule must apply to it identically.
		// BetweenTheLegs (#199) is a dribble move for the exact same reason
		// (the hand-swap AND the through-the-legs bounce both need a live
		// dribble to swap/bounce from) — joins the same gate.
		//
		// RetreatDribble (#197) joins the same gate for the same reason:
		// "the ball stays Dribbling" only makes sense starting FROM a live
		// dribble — you cannot retreat-dribble a ball you haven't started
		// bouncing. StepBack is deliberately NOT in this list: like
		// JumpShot, it may begin from a live OR dead Held possession (its
		// own Active-entry gather call safely no-ops if the ball isn't
		// Dribbling — see BallController.CradleForShotStartup's doc), so
		// gating its Begin() on ball state would only add a spurious
		// restriction, not close a real bypass.
		//
		// DriveGather (#230, ADR-0022) joins this gate too — code-review fix,
		// NOT the same reasoning as StepBack's exemption above. Real ball
		// (ADR-0014 tier 1): "the gather" IS the act of picking up a live
		// dribble to commit toward the rim — once you've already gathered
		// (dead Held), there is no dribble left to pick up, so a second
		// drive-gather is not a real basketball action, it is free movement.
		// Without this gate, CradleForShotStartup's no-op-if-not-Dribbling
		// guard would silently let a dead-Held holder spam move_drive for a
		// repeatable free DriveGatherBurstSpeed burst toward the rim — larger
		// than any other burst-family move's impulse, with no travel rule
		// (#206, known gap) to catch it. Unlike StepBack, DriveGather's own
		// Active-entry effect does NOT no-op from a dead Held possession (the
		// burst fires regardless of ball state), so gating Begin() here is
		// what actually closes the bypass.
		// EuroStep (#231, ADR-0022) joins for the SAME reason as DriveGather: it
		// is a lateral variant of the gather, so its identity is likewise "pick
		// up a live dribble to commit toward the rim" — illegal from a dead Held
		// possession, and (like DriveGather, unlike StepBack) its Active-entry
		// burst fires regardless of ball state, so gating Begin() here is what
		// closes the free-movement bypass.
		//
		// InAndOut (#202) joins for the SAME reason as Crossover/BehindTheBack:
		// it is ALSO a dribble move in real ball (the fake-out only makes sense
		// off a live dribble cadence — see InAndOut's own class doc), so the
		// dead-dribble rule must apply to it identically. This is the single
		// most likely defect the #202 brief calls out: an InAndOut omitted from
		// this hardcoded type list would silently escape the dead-dribble rule
		// (the #193 bug class) even though its Active-entry burst fires
		// regardless of ball state, exactly like Crossover's does.
		//
		// Spin (#201) joins for the SAME reason as the rest of the family: a
		// spin shields a LIVE dribble with the body — there is no dribble to
		// shield once the ball is dead-Held, and (like Crossover/BehindTheBack/
		// BetweenTheLegs/InAndOut) its Active-phase hand swap and exit burst
		// fire regardless of ball state, so omitting it here would let a
		// dead-Held holder spin the ball to the other hand for free — the
		// same #193 bug class this whole gate exists to close.
		if ((move is Crossover || move is Hesitation || move is BehindTheBack || move is BetweenTheLegs
			 || move is RetreatDribble || move is DriveGather || move is EuroStep || move is InAndOut
			 || move is Spin)
			&& IsBallHolder && GetBall()?.State == BallState.Held)
			return false;

		// JabStep (#200) is the INVERSE gate: triple threat's own stance bait
		// must be legal FROM Held (dead or live) and illegal FROM Dribbling —
		// see JabStepLegalityResolver's class doc for why this is not a
		// contradiction of the gate immediately above, but a taxonomy (a jab
		// is thrown from a stationary stance; Hesitation/hand-fake, #86,
		// already covers the equivalent bait off a live dribble).
		if (move is JabStep && IsBallHolder && !JabStepLegalityResolver.IsLegal(GetBall()?.State))
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
		//
		// Layup (#229, ADR-0022) joins the same gate: it is a second "shot"
		// whose gather is exactly as inherent to its finishing motion as
		// JumpShot's is to the set shot's — the same real-ball fact, applied
		// to a second committed move.
		//
		// DriveGather (#230, ADR-0022) joins here too, but for a slightly
		// different reason: unlike StepBack (which cradles at ACTIVE-entry,
		// because its separation burst IS the gather motion), the
		// drive-gather's entire identity IS "the gather" — ADR-0022's own
		// taxonomy entry frames it as "the moment a driving ball-handler
		// picks up their dribble," which real ball places at the INSTANT the
		// move begins, not after a later burst. CradleForShotStartup's
		// no-op-if-not-Dribbling guard makes this call safe from a dead-Held
		// possession exactly as it already is for JumpShot/Layup.
		// EuroStep (#231, ADR-0022) cradles at Startup-begin exactly like
		// DriveGather: beat 1 of the two-beat euro-step IS the gather, and the
		// gather is inherent to the move beginning, not a later burst.
		// #225 fix: clientWasAlreadyDribbling is forwarded here (false for
		// every OTHER caller — a client's own local prediction of its own
		// move, and every non-cradle-family Begin) — see
		// CradleForShotStartup's doc for how the server's dispatch of a
		// REMOTE client's request is the only caller that ever supplies a
		// meaningful value.
		if ((move is JumpShot || move is Layup || move is DriveGather || move is EuroStep) && OwnPeerId != 0)
			GetBall()?.CradleForShotStartup(OwnPeerId, clientWasAlreadyDribbling);

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
	/// moveId/param is the minimal payload to reconstruct a move. A handful of
	/// concrete moves exist now ("crossover", "behindtheback", and
	/// "betweenthelegs", all carrying BurstDirection as param; "jumpshot", carrying none; "steal",
	/// carrying the defender's RAW aim sign, NOT a pre-resolved TargetHand —
	/// issue #254 fix; see ApplyRequestedMove's "steal" branch and
	/// ResolveStealTargetHand's doc for why the facing transform is re-derived
	/// server-side from this raw sign rather than trusted from the client) so
	/// a small if-chain is still correct and
	/// proportionate — see CommittedMove.Id's doc comment, which already
	/// anticipated this exact use. Revisit only if a future move arrives with
	/// materially different reconstruction needs than a simple id dispatch;
	/// do not build a registry/factory pre-emptively.
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
	///
	/// <paramref name="clientWasAlreadyDribbling"/> (#225 fix): true if the
	/// CLIENT's OWN local ball copy was ALREADY Dribbling the instant it
	/// fired THIS request — read from the client's own zero-latency
	/// GetBall()?.State, BEFORE its own local BeginCommittedMove call (whose
	/// cradle side effect would otherwise already have flipped it back to
	/// Held by the time it's read). NOT a replay/idempotency guard (the note
	/// above still holds), but a one-off, move-specific payload consulted
	/// ONLY by the four cradle-family moves (jumpshot/layup/drivegather/
	/// eurostep) inside ApplyRequestedMove, to resolve the
	/// Begin-races-SubmitInput cradle race (see
	/// BallController.CradleForShotStartup's doc for the full mechanism —
	/// including why this is a client-supplied boolean resolved immediately
	/// at Begin-time, rather than a seq comparison deferred to a later
	/// packet that an earlier design draft could not actually rely on
	/// arriving). Every other moveId ignores it; the client always sends
	/// SOME value (false where irrelevant) because Godot's RPC
	/// deserialization requires the wire payload to match this method's
	/// fixed arity exactly — there is no "omit an unused trailing arg" over
	/// an RPC boundary the way a plain C# optional parameter would allow for
	/// a direct call. Bundled into this SAME RPC (rather than a second,
	/// separate one-shot RPC fired just before it) specifically so delivery
	/// is atomic — no cross-RPC-ordering assumption needed at all, unlike a
	/// two-RPC design would require (a doubt-driven-development review of
	/// this fix's first draft flagged that #210's own RequestExitVector
	/// precedent explicitly declined to rely solely on
	/// same-channel-same-mode ordering between two DIFFERENT RPC methods,
	/// adding a defense-in-depth phase gate instead — bundling into ONE RPC
	/// removes the need for an equivalent gate here by removing the
	/// cross-RPC race entirely).
	/// </summary>
	[Rpc(MultiplayerApi.RpcMode.AnyPeer,
		 CallLocal = false,
		 TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
	private void RequestBeginMove(string moveId, float param, bool clientWasAlreadyDribbling)
	{
		int senderId = Multiplayer.GetRemoteSenderId();
		if (senderId.ToString() != Name)
		{
			GD.PrintErr($"[PlayerController] Unauthorized RequestBeginMove from peer {senderId} for node '{Name}'");
			return;
		}

		ApplyRequestedMove(moveId, param, clientWasAlreadyDribbling);
	}

	/// <summary>
	/// The authoritative move-dispatch body of <see cref="RequestBeginMove"/>,
	/// split out from its sender-id authorization check (issue #236) so the two
	/// concerns are separable: this method answers "given that peer N legitimately
	/// asked for moveId, what does the server begin?", and knows nothing about
	/// WHO asked. Authorization stays entirely in the RPC entry point above.
	///
	/// Why the split exists: the server-side gates in here (the layup's range
	/// check, block/contest's "not your own shot") are real authority, but they
	/// were unreachable by the headless harness (ADR-0016) — RequestBeginMove is
	/// [Rpc(AnyPeer, CallLocal = false)] and sender-gated, so a single offline
	/// instance can neither deliver it remotely nor pass its authorization
	/// check. The alternative was for a harness to re-implement the gate, which
	/// would prove a copy rather than the shipped path — precisely the failure
	/// DefensiveMoveHarnessSeam's own doc documents as a cautionary tale. This
	/// keeps the seam (LayupRangeHarnessSeam) pointed at the REAL dispatch,
	/// bypassing only the RPC/authorization layer that no gate under test uses.
	///
	/// Every branch here still routes through BeginCommittedMove — the one
	/// production choke point (architecture-contract invariant #11).
	///
	/// <paramref name="clientWasAlreadyDribbling"/> (#225 fix): forwarded
	/// ONLY by the four cradle-family branches (jumpshot/layup/drivegather/
	/// eurostep) into BeginCommittedMove's own optional parameter — see
	/// RequestBeginMove's doc for where this value comes from and
	/// CradleForShotStartup's doc for how it resolves the cross-channel
	/// cradle race. Every other branch ignores it.
	/// </summary>
	private void ApplyRequestedMove(string moveId, float param, bool clientWasAlreadyDribbling)
	{
		if (moveId == "crossover")
			// param is the body-relative flick sign (M9, #85) — the world burst
			// direction is derived from Heading when the move reaches Active.
			BeginCommittedMove(new Crossover(burstDirection: param));
		else if (moveId == "behindtheback")
			// Same reconstruction payload as "crossover" (issue #194) — param
			// is the body-relative flick sign.
			BeginCommittedMove(new BehindTheBack(burstDirection: param));
		else if (moveId == "betweenthelegs")
			// Same reconstruction payload as "crossover"/"behindtheback"
			// (issue #199) — param is the body-relative flick sign.
			BeginCommittedMove(new BetweenTheLegs(burstDirection: param));
		else if (moveId == "spin")
			// Spin (#201): same reconstruction-payload convention as
			// "crossover"/"behindtheback"/"betweenthelegs" — param is the
			// body-relative flick sign, reused here as the rotation direction
			// (Spin.SpinDirection). The world-space rotation itself is
			// resolved at Active-entry from the ALREADY-authoritative Heading
			// (SpinHeadingMath.ArcHeading), not from anything re-derived here.
			BeginCommittedMove(new Spin(spinDirection: param));
		else if (moveId == "hesitation")
			BeginCommittedMove(new Hesitation());
		else if (moveId == "inandout")
			// In-and-out (#202): same reconstruction payload as "crossover" —
			// param is the body-relative flick sign (toward the empty hand).
			// The world burst direction and the negated-sign fallback are both
			// derived at Active-entry (TickCommittedMoveBehavior), not here.
			BeginCommittedMove(new InAndOut(burstDirection: param));
		else if (moveId == "stepback")
			// param = 0f — step-back carries no payload; its exit direction
			// is read from the left stick at Active-entry, not the RPC.
			BeginCommittedMove(new StepBack());
		else if (moveId == "retreatdribble")
			// param = 0f — retreat dribble carries no payload (issue #197).
			BeginCommittedMove(new RetreatDribble());
		else if (moveId == "drivegather")
			// param = 0f — drive-gather carries no payload (issue #230): its
			// drive line is resolved server-side from live GlobalPosition/
			// RimCenter during Startup (TickCommittedMoveBehavior), not from
			// anything the client sends.
			{
				BeginCommittedMove(new DriveGather(), clientWasAlreadyDribbling); // #225: cradle-family move
			}
		else if (moveId == "eurostep")
			// Euro-step (#231, ADR-0022): param is the body-relative lateral read
			// sign (+1 = step right, -1 = left), the SAME reconstruction-payload
			// convention "crossover" uses — the world step direction is re-derived
			// from Heading when the move reaches Active (EuroStepMath). The move
			// itself carries NO range gate: it is a displacement, not a shot. The
			// finish is a SEPARATE "layup" request begun from the displaced
			// position, which crosses the ADR-0023 range gate above verbatim at
			// that displaced GlobalPosition (the chain — see EuroStep's class doc).
			BeginCommittedMove(new EuroStep(lateralDirection: param), clientWasAlreadyDribbling); // #225: cradle-family move
		else if (moveId == "jab")
			// Jab step (#200): param = 0f — no payload, no burst, purely
			// informational (the telegraph itself is the whole effect).
			BeginCommittedMove(new JabStep());
		else if (moveId == "jumpshot")
		{
			// Capture the server-authoritative pre-plant speed at begin for the
			// movement scatter penalty (#137) — see ShotInitiationSpeed.
			if (BeginCommittedMove(new JumpShot(), clientWasAlreadyDribbling)) // #225: cradle-family move
				CaptureShotInitiationSpeed();
		}
		else if (moveId == "layup")
		{
			// Layup / rim-finish (issue #229, ADR-0022). param = 0f — carries
			// no payload, same convention as jumpshot.
			//
			// Server-authoritative range gate (ADR-0002): the client-side
			// LayupRangeResolver check in SampleMoveInput is UX only — a
			// tampered client could send moveId="layup" from anywhere on the
			// court to get the layup's shorter frame data with none of its
			// range restriction (nothing else here would otherwise stop it,
			// since ApplyShootLocally's distance-scatter is computed from
			// GlobalPosition regardless of which move triggered the release).
			// Re-assert the SAME distance check here using the server's own
			// authoritative GlobalPosition/RimCenter — mirrors block/contest's
			// "not your own shot" dual-gate pattern (client UX + server
			// authority) below.
			//
			// ── Why the threshold is widened, and why a reject is still a
			//    reject (issue #236, ADR-0023) ────────────────────────────────
			// The client pressed at its PREDICTED position; this RPC only
			// arrived one-way-latency later, by which time our authoritative
			// copy of a driving player has moved on. Around the boundary the
			// two positions disagree, and gating on the bare LayupRange
			// rejected honest layups — the press was eaten at the rim.
			// LayupRangeNetTolerance is this side's allowance for that
			// uncertainty (see its doc for the derivation of 0.5m).
			//
			// What we deliberately DO NOT do is fall back to a JumpShot when
			// out of range, which is the intuitive fix and the one #236
			// originally prescribed. Beginning a move the client did not
			// request breaks the invariant documented in ReconcileFromServer
			// (~line 1800): the server only ever leaves Inactive by echoing the
			// client's own moveId back. Break it and NEITHER correction gate
			// can repair the split — ShouldForceInactive fires only on
			// serverPhase == Inactive (we'd be mid-JumpShot), and
			// ShouldForceRecovery requires matching moveIds ("layup" is not
			// "jumpshot"). The client would run its 8-tick Layup to completion
			// against our 18-tick JumpShot, with nothing to reconcile them.
			//
			// Rejecting is SAFE precisely because it leaves us Inactive, which
			// ShouldForceInactive already reconciles — the client's predicted
			// layup reverts within ~1 RTT. A tamperer at 8m therefore still
			// gets nothing, and the anti-tamper property above survives intact.
			// A null ball is dropped deliberately, not incidentally (issue #236):
			// with no ball there is no shot to gate, and dropping leaves us
			// Inactive — the same SAFE outcome as an out-of-range reject above,
			// which ShouldForceInactive reconciles. Falling back to any move here
			// would be the very moveId-invariant break ADR-0023 rejects.
			BallController layupBall = GetBall();
			if (layupBall != null)
			{
				float distanceToRim = Mathf.Sqrt(DefensiveResolution.DistanceXZSquared(
					GlobalPosition, layupBall.RimCenter));
				float serverGate = layupBall.LayupRange + layupBall.LayupRangeNetTolerance;
				if (LayupRangeResolver.IsLayupRange(distanceToRim, serverGate)
					&& BeginCommittedMove(new Layup(), clientWasAlreadyDribbling)) // #225: cradle-family move
					CaptureShotInitiationSpeed();
			}
		}
		else if (moveId == "steal")
		{
			// param = the defender's RAW aim sign (issue #254 fix), not a
			// pre-resolved HandSide — mirrors the crossover family's
			// BurstDirection convention (param is body-relative read, the
			// world mapping is re-derived here). The SERVER redoes the SAME
			// facing transform ResolveStealTargetHand's doc describes, but
			// using ITS OWN authoritative Heading for both this player
			// (this.Heading — the defender, since ApplyRequestedMove runs on
			// the AUTHORITY's copy of the RPC sender's node) and the ball's
			// holder — never trusting a client-computed TargetHand verbatim
			// (ADR-0002). This is what actually feeds
			// DefensiveResolution.StealSucceeds; ResolveStealTargetHand's own
			// doc explains why it is called from both ends.
			// The CommittedMoveMachine enforces the usual phase guards (Inactive-
			// only Begin), so a client that sends "steal" while still in Recovery
			// gets a silent no-op — Begin() returns false and nothing happens.
			HandSide target = ResolveStealTargetHand(param, GetBall());
			BeginCommittedMove(new StealMove(target));
		}
		else if (moveId == "block")
		{
			// param = 0f (block carries no payload — it is a one-axis timing read,
			// no hand-side). Same phase guards apply: Begin() no-ops while mid-Recovery.
			//
			// "You cannot block your own shot" must be enforced where authority
			// actually lives (ADR-0002) — the local !IsBallHolder check at the
			// def_block input site is client-side UX (keeps a ball-holder from
			// even attempting the input), not authority; a tampered client could
			// send "block" while holding the ball. ResolveBlockAttempts' own
			// _lastShooterPeerId exclusion only covers the POST-release window
			// (a shooter can't block their own already-released shot); this gate
			// covers the PRE-release Begin() itself.
			if (!IsBallHolder)
				BeginCommittedMove(new BlockMove());
		}
		else if (moveId == "contest")
		{
			// param = 0f (contest carries no payload, same convention as block —
			// it's a committed pressure move, not a targeted swipe). Same
			// "not your own shot" server-side re-assertion as block, for the
			// same reason (ADR-0002: the client-side !IsBallHolder gate at the
			// def_contest input site is UX, not authority).
			if (!IsBallHolder)
				BeginCommittedMove(new ContestMove());
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
	///
	/// (#102) isBeaten is the server's own live IsBeaten() read at broadcast
	/// time (issue #100's whiff-punish window) — display-only, feeding
	/// DisplayBeaten(). Appended LAST, as its own trailing bool, same
	/// transposition-safety reasoning as endedActiveEarly. Unlike every other
	/// field in this payload, this one is NOT read-with-a-fallback-to-local
	/// for ANY role but the server: see BeatenDisplayResolver's class doc for
	/// why the beaten window's truth is knowable by only one role, not three.
	/// </summary>
	[Rpc(MultiplayerApi.RpcMode.Authority,
		 CallLocal = false,
		 TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
	private void ReceiveState(int ackSeq, Vector3 pos, Vector3 vel,
		int movePhase, int frameInPhase, string moveId, float moveParam, float heading, int handSide,
		bool endedActiveEarly, bool pivotHasLatch, float pivotLatchedYaw, bool isBeaten)
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
		// (#102) See _serverIsBeaten's field doc — same level-triggered
		// overwrite-every-broadcast reasoning.
		_serverIsBeaten  = isBeaten;
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
		// Multiple move types exist now (crossover, behindtheback,
		// betweenthelegs, hesitation, stepback, retreatdribble, drivegather,
		// jumpshot, layup, steal, block, contest), but a
		// "different move Id while both sides are active" case is still
		// structurally unreachable: the
		// server only ever moves out of Inactive by echoing back the EXACT
		// moveId the client's own RPC requested (RequestBeginMove's Id
		// parameter round-trips unchanged through the broadcast). There is no
		// path where the server is Active/Startup/Recovery on a move whose Id
		// differs from what this client itself asked to begin — so the two
		// sides' move Ids can never disagree while both are non-Inactive,
		// regardless of how many move types the game has.
		//
		// This invariant is LOAD-BEARING, not incidental — it is why the two
		// gates below are the only correction machinery this system needs.
		// Issue #236 proposed having the server substitute a JumpShot for an
		// out-of-range "layup" request, which would have made it the first code
		// to violate this, producing a split neither gate can repair
		// (ShouldForceInactive needs serverPhase == Inactive;
		// ShouldForceRecovery needs matching moveIds). ADR-0023 rejected that
		// in favour of widening the gate's threshold instead. Any future
		// server-side "begin a different move than requested" idea lands here
		// first: read ADR-0023 before writing it.
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
		// (#243) isFadeaway only ever changes MoveAnimResolver's answer during
		// Active (a squared-up JumpShot, every other move, and every non-Active
		// phase ignore it) — see MoveAnimResolver.Resolve's own doc.
		//
		// IsPivotingInPlace is already correct for EITHER role without going
		// through DisplayMove(): the own player's Move() sets _pivot locally
		// every tick, and TickClientRemotePlayer adopts the broadcast latch
		// directly for the opponent's copy (see that method's #172 comment) —
		// exactly the same "already-resolved for display" property DisplayMove
		// itself exists to provide for MovePhase (issue #242). The two flags
		// never both matter for the same call: isFadeaway only bites on
		// Active, isPivotingInPlace only bites on Inactive.
		MoveAnimState target = MoveAnimResolver.Resolve(displayPhase, DisplayFadeaway(), IsPivotingInPlace);
		if (target != _currentAnimState)
		{
			_animPlayback.Travel(target.ToString());
			_currentAnimState = target;
		}
	}

	/// <summary>
	/// (#102) Toggles the placeholder "beaten" marker's visibility to match
	/// <see cref="DisplayBeaten"/>. Runs for EVERY role, every tick — same
	/// "cosmetics apply everywhere" posture as ApplyCosmetics/ApplyAnimation,
	/// so the cue renders on the client's copy of the opponent too, not just
	/// locally. No-op if the scene has no BeatenIndicator child.
	/// </summary>
	private void ApplyBeatenCue()
	{
		if (_beatenIndicator == null) return;
		_beatenIndicator.Visible = DisplayBeaten();
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
			return (_machine.Phase,
				(_machine.CurrentMove as Crossover)?.BurstDirection
				?? (_machine.CurrentMove as BehindTheBack)?.BurstDirection
				?? (_machine.CurrentMove as BetweenTheLegs)?.BurstDirection
				?? (_machine.CurrentMove as InAndOut)?.BurstDirection
				?? 0f);

		float burstDir = _serverMoveId is "crossover" or "behindtheback" or "betweenthelegs" or "inandout" ? _serverMoveParam : 0f;
		return (_serverMovePhase, burstDir);
	}

	/// <summary>
	/// The committed-move Id this node should DISPLAY this frame — same
	/// per-role resolution as <see cref="DisplayMove"/> (M7b, #69), but
	/// returning the identity string instead of phase/burst. Added for
	/// BehindTheBack (#194): BallController.AdvanceHandSweep needs to tell a
	/// Crossover-driven hand flip apart from a BehindTheBack-driven one (the
	/// in-front sweep vs. the shielded behind-body path) for EVERY role,
	/// including the remote client's copy of the opponent — which, like
	/// DisplayMove, has no live local _machine to read (that peer's _machine
	/// never advances for the opponent's node), so it must fall back to the
	/// broadcast _serverMoveId exactly like DisplayMove's burstDir already does.
	/// </summary>
	public string DisplayMoveId() =>
		DisplayPhaseResolver.LocalMachineDrivesDisplay(IsServer, IsLocalPlayer)
			? MoveIdOf(_machine.CurrentMove)
			: _serverMoveId;

	/// <summary>
	/// (Issue #243) Whether this node should DISPLAY the fadeaway/off-balance
	/// shot clip this frame — same per-role resolution as
	/// <see cref="DisplayMove"/>/<see cref="DisplayMoveId"/> (M7b, #69): the
	/// role that locally simulates this machine (server for either holder,
	/// client for its own player) reads the live <c>JumpShot.IsFadeaway</c>
	/// flag off <c>CurrentMove</c>; the client's copy of a REMOTE opponent has
	/// no live local machine to read (that peer's machine never advances for
	/// the opponent's node), so it falls back to the broadcast
	/// <c>_serverMoveId</c>/<c>_serverMoveParam</c> pair, exactly like
	/// DisplayMove's burstDir reconstruction — <see cref="MoveParamOf"/>
	/// repurposes that same payload slot to carry IsFadeaway (1f/0f) for a
	/// JumpShot specifically.
	/// </summary>
	public bool DisplayFadeaway() =>
		DisplayPhaseResolver.LocalMachineDrivesDisplay(IsServer, IsLocalPlayer)
			? _machine.CurrentMove is JumpShot jumpShot && jumpShot.IsFadeaway
			: _serverMoveId == "jumpshot" && _serverMoveParam != 0f;

	/// <summary>
	/// Whether this node should DISPLAY the whiff-punish "beaten" cue
	/// (issue #100's blow-by lane, made visible for #102) this frame.
	///
	/// Deliberately NOT gated by <see cref="DisplayPhaseResolver"/>'s
	/// <c>LocalMachineDrivesDisplay</c> predicate — that predicate answers
	/// "does this peer simulate the committed-move machine," which is true
	/// for the client's own player too. The beaten window is different: only
	/// the SERVER ever judges a whiff (<c>BallController.
	/// ResolveBeatenWindowTriggers</c> runs solely inside its own
	/// <c>if (IsServer)</c> block), so a client cannot locally know it was
	/// just ruled beaten — not even for its own predicted player. See
	/// <see cref="BeatenDisplayResolver"/>'s class doc for the full
	/// role-by-role reasoning. Concretely: every role but the server itself
	/// reads the broadcast <c>_serverIsBeaten</c>, never the local
	/// <see cref="IsBeaten"/> read.
	/// </summary>
	public bool DisplayBeaten() =>
		BeatenDisplayResolver.Resolve(IsServer, IsBeaten(PhysicsTick), _serverIsBeaten);

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
	///
	/// (#225 fix: unchanged by that fix — see BallController.CradleForShotStartup's
	/// doc for why the cradle race is resolved entirely at Begin-time via a
	/// client-supplied boolean instead of a seq comparison here.)
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
	/// gets the same crossover/hesitation/in-and-out semantics as the stick.
	/// Feint modifier (E key / L1): pump-fakes a JumpShot during its startup
	/// window (#202 closed the crossover-family recall model — see the
	/// ADR-0003 amendment — so this key's ONLY remaining live effect is the
	/// JumpShot pump-fake; Crossover/BehindTheBack are now structurally
	/// unfeintable, feintWindowFrames: 0).
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
				//
				// BehindTheBack (#194) reuses this SAME flick-toward-the-empty-
				// hand gesture — it is a size-up VARIANT of the crossover read,
				// not a separate gesture grammar (real ball has no dedicated
				// analog input for "which crossover variant"; a player decides
				// by choice, not by a different stick motion). The "move_size_up"
				// modifier held DURING the flick selects it, mirroring 2K's
				// modifier-gated advanced-dribble convention (ADR-0014 tier 3 —
				// neither real ball nor Undisputed 3 specify a literal control
				// mapping here, so the lowest-ranked but still valid reference
				// resolves it). Held, not a separate discrete press: the flick
				// itself is still what commits the move; the modifier only
				// selects WHICH move the flick becomes.
				//
				// BetweenTheLegs (#199) is a THIRD size-up variant of the same
				// crossover read, selected by a second modifier ("move_finesse")
				// rather than overloading move_size_up — real ball has no
				// dedicated analog input for "which crossover variant" either,
				// so which key selects which variant is itself an ADR-0014
				// tier-3 self-resolved call (neither real ball nor
				// Undisputed 3 specify a literal control mapping here). Checked
				// AFTER move_size_up so a player holding both defaults to the
				// existing BehindTheBack behavior rather than silently
				// reassigning it.
				//
				// Spin (#201) is a FOURTH variant of the same crossover read,
				// selected by holding BOTH existing modifiers together
				// ("move_size_up" + "move_finesse") rather than adding a new
				// input action or a new gesture-recognition primitive. This is
				// a deliberate circuit-breaker call: a real spin is a
				// rotational input (2K models it as a right-stick roll/circle
				// gesture), but the right-stick vocabulary here is already
				// full (Crossover/QuickReturn/StepBack/RetreatDribble), and
				// building a new stick-rotation recognizer to match 2K's
				// literal control feel would be a large, netcode-sensitive
				// addition out of scope for this issue. Combining the two
				// modifiers ALREADY used to select BehindTheBack/BetweenTheLegs
				// is the lowest-complexity trigger that still fits the
				// existing input model: same ADR-0014 tier-3 self-resolution
				// those two variants already use (neither real ball nor
				// Undisputed 3 specify a literal control mapping for "which
				// crossover variant" — 2K's modifier-gated advanced-dribble
				// convention is the lowest-ranked but still valid reference
				// that resolves it), extended to a THIRD modifier combination
				// rather than a new input primitive. Checked BEFORE the
				// single-modifier checks so a player holding both gets Spin,
				// not BehindTheBack (move_size_up alone still resolves to
				// BehindTheBack exactly as before).
				if (Input.IsActionPressed("move_size_up") && Input.IsActionPressed("move_finesse"))
				{
					if (BeginCommittedMove(new Spin(spinDirection: flickSign)) && !isServer)
						RpcId(1, MethodName.RequestBeginMove, "spin", flickSign, false); // #225: not a cradle-family move
				}
				else if (Input.IsActionPressed("move_size_up"))
				{
					if (BeginCommittedMove(new BehindTheBack(flickSign)) && !isServer)
						RpcId(1, MethodName.RequestBeginMove, "behindtheback", flickSign, false); // #225: not a cradle-family move
				}
				else if (Input.IsActionPressed("move_finesse"))
				{
					if (BeginCommittedMove(new BetweenTheLegs(flickSign)) && !isServer)
						RpcId(1, MethodName.RequestBeginMove, "betweenthelegs", flickSign, false); // #225: not a cradle-family move
				}
				else if (BeginCommittedMove(new Crossover(flickSign)) && !isServer)
				{
					RpcId(1, MethodName.RequestBeginMove, "crossover", flickSign, false); // #225: not a cradle-family move
				}
			}
			else if (BeginCommittedMove(new Hesitation()) && !isServer)
			{
				RpcId(1, MethodName.RequestBeginMove, "hesitation", 0f, false); // #225: not a cradle-family move
			}
		}
		else if (gesture.Kind == GestureKind.QuickReturn)
		{
			// In-and-out (M9, issue #202) — the quick-return retarget. The
			// SAME hand-state disambiguation as the held Crossover branch
			// above: flick toward the empty hand -> in-and-out (the fast,
			// safe, small twin of the crossover — same telegraph, no hand
			// swap); flick toward the ball hand -> hesitation, IDENTICAL to
			// the held gesture (AC-8) — the flick DIRECTION picks the move
			// family, hold-vs-quick-return only disambiguates WITHIN the
			// empty-hand column. This gesture used to unconditionally feint
			// whatever move was in Startup (GestureKind was named "Feint");
			// #202 retargets it to BEGIN a move instead — see GestureKind's
			// own doc for the rename, and the ADR-0003 amendment (docs/adr/
			// 0003-input-model-hybrid.md) for why the old recall model is
			// closed rather than kept alongside this.
			int flickSign = System.Math.Sign(gesture.Direction);
			if (HandStateResolver.IsCrossover(HandSide, flickSign))
			{
				if (BeginCommittedMove(new InAndOut(flickSign)) && !isServer)
					RpcId(1, MethodName.RequestBeginMove, "inandout", flickSign, false); // #225: not a cradle-family move
			}
			else if (BeginCommittedMove(new Hesitation()) && !isServer)
			{
				RpcId(1, MethodName.RequestBeginMove, "hesitation", 0f, false); // #225: not a cradle-family move
			}
		}
		else if (gesture.Kind == GestureKind.StepBack)
		{
			// Step-back (M9, issue #197): the "hold" half of the new vertical
			// gesture pair. Gated to the ball holder (an off-ball defender
			// retreating has no ball to gather); BeginCommittedMove itself
			// imposes NO Held-vs-Dribbling gate for StepBack (unlike
			// Crossover/Hesitation/BehindTheBack/RetreatDribble) — see that
			// method's comment for why the move's own Active-entry gather
			// call safely no-ops from either starting state.
			if (IsBallHolder && BeginCommittedMove(new StepBack()) && !isServer)
				RpcId(1, MethodName.RequestBeginMove, "stepback", 0f, false); // #225: cradles at Active-entry via StepBack's OWN call site, not this one — see CradleForShotStartup's doc
		}
		else if (gesture.Kind == GestureKind.RetreatDribble)
		{
			// Retreat dribble (M9, issue #197): the "quick" half of the
			// vertical pair. The gesture's own quick-return timing IS the
			// feint (RetreatDribble.FeintWindowFrames=0 — see its class doc
			// for why a SECOND free-abort would make this a zero-cost bait
			// tool). Requires an actual live dribble (BeginCommittedMove's
			// dead-dribble gate, extended for this move, refuses it from
			// Held exactly like Crossover/Hesitation/BehindTheBack).
			if (IsBallHolder && BeginCommittedMove(new RetreatDribble()) && !isServer)
				RpcId(1, MethodName.RequestBeginMove, "retreatdribble", 0f, false); // #225: not a cradle-family move
		}

		// Drive-gather (M9, issue #230, ADR-0022): a discrete button, not a
		// right-stick gesture — the gather commits toward the rim, so it
		// needs its own dedicated input rather than overloading the
		// gesture-driven crossover/step-back grammar. Gated to the ball
		// holder (an off-ball defender has no dribble to gather from), same
		// discipline StepBack/RetreatDribble already use. BeginCommittedMove's
		// dead-dribble gate DOES include DriveGather (code-review fix) — the
		// gather IS the act of picking up a live dribble (ADR-0014 tier 1
		// real-ball fact), so it cannot legally begin from a dead Held
		// possession; see that method's comment for the full reasoning.
		//
		// Euro-step (#231, ADR-0022) shares this SAME button: the left stick's
		// lateral tilt at press decides which move begins. A lateral push
		// promotes the drive into a euro-step and picks the step side
		// (EuroStepReadResolver, decomposing the movement stick against the
		// body-right axis exactly as the crossover family reads its exit
		// vector); a forward/neutral push stays a straight drive-gather. The read
		// sign rides the RequestBeginMove RPC param the same way
		// Crossover.BurstDirection does — the server reconstructs the move from
		// the payload (ApplyRequestedMove), it does not re-derive the read. Both
		// branches funnel through the SAME dead-dribble gate in BeginCommittedMove
		// (EuroStep joins it alongside DriveGather).
		if (Input.IsActionJustPressed("move_drive") && IsBallHolder)
		{
			int lateralSign = EuroStepReadResolver.ResolveLateralSign(
				ReadInput(), HandStateResolver.BurstWorldDir(Heading, +1), ExitDeadzone);
			bool wasAlreadyDribblingForGather = GetBall()?.State == BallState.Dribbling;
			if (lateralSign != 0)
			{
				if (BeginCommittedMove(new EuroStep(lateralDirection: lateralSign)) && !isServer)
					RpcId(1, MethodName.RequestBeginMove, "eurostep", (float)lateralSign, wasAlreadyDribblingForGather); // #225: cradle-family move
			}
			else if (BeginCommittedMove(new DriveGather()) && !isServer)
				RpcId(1, MethodName.RequestBeginMove, "drivegather", 0f, wasAlreadyDribblingForGather); // #225: cradle-family move
		}

		// Jab step (M9, issue #200): triple threat's stance bait. Its own
		// dedicated button, not a gesture — the jab has no directional
		// payload and no exit-vector to read, so overloading the crossover
		// gesture grammar would add nothing. Gated to the ball holder, same
		// discipline every other Held/Dribbling-sensitive move already uses;
		// BeginCommittedMove's JabStepLegalityResolver gate is the real
		// authority (legal from Held, dead or live; illegal from Dribbling).
		if (Input.IsActionJustPressed("move_jab") && IsBallHolder)
		{
			if (BeginCommittedMove(new JabStep()) && !isServer)
				RpcId(1, MethodName.RequestBeginMove, "jab", 0f, false); // #225: not a cradle-family move
		}

		// Shoot: begin a JumpShot or a Layup (M7b #74; layup added #229,
		// ADR-0022) — this REPLACES the old instant "ball leaves hand on
		// press" trigger that used to live in BallController.TryShoot.
		// Holder-gated the same way that old trigger was (IsBallHolder mirrors
		// its IsLocalHolder check); Begin() itself enforces Inactive-only
		// legality, so a shot attempt mid-crossover (or mid-shot) silently
		// no-ops exactly like a second crossover attempt would. The actual
		// ball release is NOT requested here — it fires several ticks later,
		// on this machine's own JustEnteredActive, which BallController.
		// CheckJumpShotRelease reads via JustReleasedJumpShot.
		//
		// Which move begins is decided HERE, at press time, by
		// LayupRangeResolver reading this player's live distance to the rim
		// (#229): reusing the SAME shoot input rather than adding a second
		// button or a drive mechanic (explicitly out of #229's scope; #230's
		// job). The chosen move's id rides the RequestBeginMove RPC exactly
		// like every other move already does — the server does not
		// re-derive the choice, but DOES re-assert the range gate for
		// "layup" specifically (see RequestBeginMove's "layup" branch) since
		// a client's choice of WHICH move to request is not itself
		// authoritative.
		BallController ball = GetBall();
		if (ball != null && Input.IsActionJustPressed(ball.ShootAction) && IsBallHolder)
		{
			// Shares DefensiveResolution.DistanceXZSquared with the server's
			// re-assertion in RequestBeginMove (issue #236) so the two ends of
			// the same gate cannot drift apart in their arithmetic. NOT shared
			// with ApplyShootLocally's shot-distance calc, which measures a
			// different pair of points entirely — see LayupRangeResolver's doc.
			float distanceToRim = Mathf.Sqrt(DefensiveResolution.DistanceXZSquared(
				GlobalPosition, ball.RimCenter));
			bool isLayup = LayupRangeResolver.IsLayupRange(distanceToRim, ball.LayupRange);

			CommittedMove shot = isLayup ? new Layup() : new JumpShot();
			// #225 fix: captured BEFORE BeginCommittedMove, whose own LOCAL
			// cradle side effect (CradleForShotStartup, for our own
			// zero-latency prediction) would otherwise already flip
			// Dribbling -> Held by the time it's read below. See
			// RequestBeginMove's doc for how the SERVER uses this value to
			// resolve the cradle race for a REMOTE client's copy of this
			// same request.
			bool wasAlreadyDribblingForShot = ball.State == BallState.Dribbling;
			if (BeginCommittedMove(shot))
			{
				// Capture the pre-plant locomotion speed NOW — Startup zeroes
				// Velocity, so the movement scatter penalty must read speed at
				// shot initiation, not at release (#137). Only the server's
				// value feeds the outcome. Applies identically to both shot
				// types (ADR-0022: the layup feeds the same accuracy model).
				CaptureShotInitiationSpeed();
				if (!isServer)
					RpcId(1, MethodName.RequestBeginMove, isLayup ? "layup" : "jumpshot", 0f, wasAlreadyDribblingForShot); // #225: cradle-family move
			}
		}

		// Steal: defensive committed move (M10, issue #96, ADR-0018).
		//
		// Gated on NOT holding the ball — you cannot steal from yourself.
		// The "side" axis of the two-axis steal read (ADR-0018 §2) is chosen here
		// from the AIM stick X component: right aim (aimX > 0) → the defender's
		// own body-right, left/neutral → body-left. This reads the AIM
		// direction, NOT the movement stick, so the defender can separately
		// move and aim their reach.
		//
		// ADR-0014 / real-ball rationale: in half-court 1v1 the most common steal
		// attempt is a swipe at the dribble hand; the AIM axis lets the defender
		// commit the read explicitly rather than guessing a default.
		//
		// (issue #254 fix) The wire payload is now the RAW aim SIGN, not a
		// pre-resolved HandSide — see ResolveStealTargetHand's doc for why the
		// facing transform must be re-derived, not trusted verbatim, and
		// RequestBeginMove's "steal" branch for the server-side half of this
		// same fix (mirrors the crossover family's BurstDirection convention:
		// param is the defender's own body-relative read, the WORLD mapping is
		// resolved from the authoritative Heading(s) at the point of use).
		if (Input.IsActionJustPressed("def_steal") && !IsBallHolder)
		{
			float aimSign = aim.X > 0f ? 1f : -1f;
			HandSide target = ResolveStealTargetHand(aimSign, GetBall());
			if (BeginCommittedMove(new StealMove(target)) && !isServer)
				RpcId(1, MethodName.RequestBeginMove, "steal", aimSign, false); // #225: not a cradle-family move
		}

		// Block: defensive committed move (M10, issue #98, ADR-0018 §2).
		//
		// Gated on NOT holding the ball — you cannot block your own shot. This
		// local check is client-side UX only (stops a ball-holder from even
		// attempting the input); RequestBeginMove's "block" case re-asserts it
		// server-side, since a tampered client could send the RPC regardless
		// of this gate (ADR-0002).
		// Unlike the steal (two-axis: when + which hand), the block is a
		// ONE-AXIS read: only timing matters. The ball is airborne when it
		// can be blocked, so there is no hand-side to target.
		//
		// The vulnerable window is [InFlight start, InFlight start + blockGraceTicks)
		// — the same tick as [JumpShot.Active start, ...) since release fires on
		// Active entry (see BallController.ResolveBlockAttempts' doc for the
		// equivalence). BallController.ResolveBlockAttempts evaluates the full
		// interval overlap (not a point-in-time check like steal) — a defender
		// who entered Active before or after the release still blocks if their
		// window overlaps the grace.
		// param = 0f: block carries no payload.
		if (Input.IsActionJustPressed("def_block") && !IsBallHolder)
		{
			if (BeginCommittedMove(new BlockMove()) && !isServer)
				RpcId(1, MethodName.RequestBeginMove, "block", 0f, false); // #225: not a cradle-family move
		}

		// Contest: defensive committed move (M10, issue #99, ADR-0018 §2).
		//
		// Gated on NOT holding the ball — you cannot contest your own shot.
		// Unlike steal/block, contest never resolves a binary succeed/fail —
		// it composes an ADDITIONAL accuracy factor on top of the existing
		// passive proximity scatter (ADR-0009 / #65) when its Active window
		// overlaps the shot's release tick (BallController.ApplyShootLocally's
		// contest composition; see DefensiveResolution.ContestAppliesAt).
		// param = 0f: contest carries no payload, same as block.
		if (Input.IsActionJustPressed("def_contest") && !IsBallHolder)
		{
			if (BeginCommittedMove(new ContestMove()) && !isServer)
				RpcId(1, MethodName.RequestBeginMove, "contest", 0f, false); // #225: not a cradle-family move
		}

		// Feint modifier (#202 closed the ambiguous-gesture path): the ONLY
		// input that can still call CommittedMoveMachine.Feint() is the
		// explicit discrete "move_feint" key (E / L1) — a direct pump-fake
		// request for whatever move is running. The machine enforces the
		// feint-window guard itself (Startup only, within FeintWindowFrames);
		// a false return is silent.
		//
		// Before #202 this ALSO fired from the right-stick quick-return
		// gesture (then GestureKind.Feint), routed through FeintGateResolver
		// to withhold it from an in-progress JumpShot (bug fix, /diagnose
		// 2026-07-03: an incidental aim-stick flick was silently eating the
		// shot's release, since Feint() never sets JustEnteredActive while the
		// windup animation plays regardless). #202 retargets that SAME
		// gesture kind (renamed QuickReturn) to BEGIN a move instead (see the
		// branch above) — a gesture reaching CommittedMoveMachine.Begin() while
		// a JumpShot is Startup-ing is already a silent no-op (Begin() only
		// succeeds from Inactive), so FeintGateResolver's whole reason to
		// exist — withholding an AMBIGUOUS gesture-sourced Feint() call from
		// JumpShot — is now structurally moot: there is no other gesture-
		// sourced Feint() call left to withhold it from. FeintGateResolver
		// was removed (doubt-driven pass, #202) rather than kept as dead code.
		if (Input.IsActionJustPressed("move_feint") && _machine.Feint() && !isServer)
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
				// #198's hybrid gather is scoped to the burst-family moves that
				// explicitly opt into it (ADR-0003 amendment) — every other
				// committed move (Hesitation, StealMove, JumpShot, …) keeps the
				// original instant-zero plant. A blanket bleed here would have
				// silently let a driving player slide through a Hesitation's
				// "stand still and sell the fake" or a defender's StealMove
				// Active window on residual momentum GatherDecel's budget
				// doesn't fully cover for their (shorter) Startup lengths —
				// exactly the un-bounded relaxation the ADR amendment explicitly
				// rules out. BehindTheBack (#194) opts in with its OWN, steeper
				// rate (BehindTheBackGatherDecel — "heavier gather bleed" per
				// the spec), not Crossover's GatherDecel. BetweenTheLegs (#199)
				// opts in with a rate midway between the two, per its own
				// "balanced midpoint" identity.
				Velocity = _machine.CurrentMove switch
				{
					Crossover      => Velocity.MoveToward(Vector3.Zero, GatherDecel * (float)delta),
					BehindTheBack  => Velocity.MoveToward(Vector3.Zero, BehindTheBackGatherDecel * (float)delta),
					BetweenTheLegs => Velocity.MoveToward(Vector3.Zero, BetweenTheLegsGatherDecel * (float)delta),
					DriveGather    => Velocity.MoveToward(Vector3.Zero, DriveGatherDecel * (float)delta),
					// EuroStep (#231) reuses DriveGather's gather bleed — beat 1 of
					// the euro-step is the same gather motion, so it shares the
					// same decel budget rather than inventing a second rate.
					EuroStep       => Velocity.MoveToward(Vector3.Zero, DriveGatherDecel * (float)delta),
					// InAndOut (#202) reuses Crossover's OWN GatherDecel (not a
					// separate tunable — see PlayerController's field doc): it
					// is an ordinary plant, just a shorter one (4 ticks vs. 6).
					InAndOut       => Velocity.MoveToward(Vector3.Zero, GatherDecel * (float)delta),
					// Spin (#201) opts into the SAME hybrid-gather model with
					// its own rate — see SpinGatherDecel's doc. Without this,
					// Spin would fall into the default instant-zero plant
					// below and NEVER carry any surviving momentum into
					// Active, making its exit burst's "continues the original
					// drive line" identity vacuous (doubt-cycle finding, #201).
					Spin           => Velocity.MoveToward(Vector3.Zero, SpinGatherDecel * (float)delta),
					_              => Vector3.Zero,
				};

				// Drive-gather (#230, ADR-0022) also resolves its drive line
				// during Startup, via the SAME bounded turn-rate heading path
				// Move() itself uses (HeadingMath.RotateToward, ADR-0010) —
				// NOT an instant snap to the rim. Every OTHER committed move
				// freezes Heading for its whole duration (this method
				// replaces Move() entirely while a move IsActive); DriveGather
				// is the first exception, because "commit your body toward
				// the rim" (ADR-0022's own taxonomy entry) is itself a
				// gradual plant, not an instant re-aim — an instant snap-turn
				// would be exactly the arcade decoupling ADR-0003 rules out.
				// Confined to Startup only: by Active-entry the drive line is
				// locked in and read once (below), matching every other
				// burst move's "SET once on JustEnteredActive, never
				// re-derived mid-Active" rule.
				//
				// EuroStep (#231) bends toward the rim during Startup identically:
				// beat 1 (the gather) commits the body toward the rim exactly as
				// DriveGather's does — the euro-step's lateral read is a beat-2
				// (Active) concern, not a beat-1 re-aim. Sharing this branch keeps
				// the rim-bent Heading that EuroStepMath's Active composition then
				// reads as its forward/right basis.
				if (_machine.CurrentMove is DriveGather or EuroStep)
				{
					BallController driveBall = GetBall();
					if (driveBall != null)
					{
						Vector2 fromXZ = new(GlobalPosition.X, GlobalPosition.Z);
						Vector2 targetXZ = new(driveBall.RimCenter.X, driveBall.RimCenter.Z);
						Vector2 wishDir = DriveGatherMath.WishDirToward(fromXZ, targetXZ);
						Heading = HeadingMath.RotateToward(Heading, wishDir, delta, MaxTurnRateDeg, BackTurnSlowFactor);
					}
				}

				MoveAndSlide();
				break;

			case MovePhase.Active:
				// Set the burst velocity on the first Active tick; on subsequent
				// Active ticks the same velocity is maintained (no else-zero here).
				// MoveAndSlide() applies it each tick, producing sustained separation.
				// A JumpShot (M7b, #74) has no horizontal effect here — Velocity is
				// already bled toward zero by Startup's gather above and nothing
				// sets it for a non-burst-family move, so the shooter stays
				// (near-)planted through the release. BallController reads
				// JustReleasedJumpShot to fire the actual ball transition on this
				// same tick — that is a SEPARATE node's read of this machine's
				// state, not a side effect this switch needs to produce.
				//
				// (#243) Classify a JumpShot's release as fadeaway/off-balance
				// exactly on the tick it enters Active — the same tick the ball
				// releases (JustReleasedJumpShot/CheckJumpShotRelease) and the
				// same tick ShotFacing.Multiplier reads Heading server-side for
				// the accuracy penalty. Must happen HERE, not at Begin(): the
				// shooter can still be turning through the whole of Startup, so
				// the classification is only meaningful at the release instant.
				// Cosmetic-only (ADR-0004): this never feeds back into
				// accuracy — BallController.ApplyShootLocally computes its own
				// facingFactor independently from the same Heading/RimCenter
				// inputs. GetBall() can be null in a scene with no ball wired;
				// IsFadeaway simply stays false (squared-up default) then.
				if (_machine.JustEnteredActive && _machine.CurrentMove is JumpShot jumpShotMove)
				{
					BallController ball = GetBall();
					if (ball != null)
						jumpShotMove.IsFadeaway =
							FadeawayTriggerResolver.IsFadeaway(Heading, GlobalPosition, ball.RimCenter);
				}

				// Crossover, BehindTheBack (#194), BetweenTheLegs (#199), and
				// InAndOut (#202) share the SAME CrossoverBurstMath composition
				// (composition, not inheritance — see BehindTheBack's doc) with
				// different tunables: BehindTheBack passes its own (smaller)
				// burst speeds and a NARROWER exit cone (BehindTheBackExitConeDegrees,
				// "fewer follow-ups" per the M9 taxonomy handoff — never a
				// recovery/cooldown penalty); BetweenTheLegs passes its own
				// MIDPOINT tunables between Crossover's and BehindTheBack's;
				// InAndOut passes its own REDUCED tunables (~0.6x Crossover's,
				// #202's "fast, safe, small" design call).
				if (_machine.JustEnteredActive && _machine.CurrentMove is Crossover or BehindTheBack or BetweenTheLegs or InAndOut)
				{
					// #198: CrossoverBurstMath composes the surviving Startup
					// momentum with the exit-vector-driven burst impulse — see
					// its doc for the full emergent-move table.
					//
					// (#210, fixed) STILL NOT jointly deterministic across
					// client/server the way Heading is (ADR-0010) — each role
					// composes from its OWN snapshot of exitVectorSample — but
					// the SERVER's snapshot (TickServerRemotePlayer's call
					// site) now PREFERS the value delivered by the client's
					// discrete RequestExitVector RPC, fired at the client's
					// own JustEnteredActive tick, over the old continuously-
					// streamed _pendingRawStick cache that used to be the
					// sole source. Any tick the RPC has already been
					// processed by the time the server needs it, the two
					// composed bursts are IDENTICAL, not just coincidentally
					// close — the RPC IS the client's own value, not a second
					// independent read of it. The residual: the RPC can only
					// be proven to win a same-frame-or-earlier arrival, not a
					// strict "always beats the old cache" guarantee under
					// adversarial jitter (see RequestExitVector's doc for the
					// timing analysis) — in that rare residual case this falls
					// back to _pendingRawStick, i.e. no worse than the
					// pre-#210 status quo, never a NEW divergence class.
					// BehindTheBack, BetweenTheLegs, and InAndOut all ride the
					// SAME composition path (and the SAME RequestExitVector
					// send site, gated on CurrentMove type in
					// TickClientOwnPlayer), so this fix covers all of them,
					// and Spin (below), for free.
					float burstSign = _machine.CurrentMove switch
					{
						Crossover c      => c.BurstDirection,
						BehindTheBack b  => b.BurstDirection,
						BetweenTheLegs g => g.BurstDirection,
						InAndOut i       => i.BurstDirection,
						_                => 0f,
					};
					int sign = System.Math.Sign(burstSign);

					// InAndOut (#202) passes the NEGATED sign into the shared
					// composition. ComposeActiveVelocity's own flickSign
					// parameter is used ONLY as (a) the stationary+neutral-exit
					// fallback lateral direction and (b) the exact-backward-pole
					// tiebreak — for Crossover/BehindTheBack/BetweenTheLegs both
					// must point toward the EMPTY hand (where the ball is
					// headed), which is exactly what BurstDirection already
					// encodes. An in-and-out's ball never crosses, so those same
					// two fallback paths must instead point toward the BALL
					// hand — the direction of the sell, not the empty hand — see
					// InAndOut's class doc "Negated flick sign" section and
					// AC-4's control (a Crossover under identical conditions
					// bursts toward the empty hand).
					int composeSign = _machine.CurrentMove is InAndOut ? -sign : sign;

					// Per-move tunable triple, resolved with a switch so this
					// stays a single ComposeActiveVelocity call site rather than
					// a growing nested ternary as move #3 joins the family
					// (#199 code-review self-check). Crossover's own 180° falls
					// back to CrossoverBurstMath's own FullExitCone default via
					// an explicit 180 here — Mathf.DegToRad(180) is exactly PI,
					// bit-identical to omitting the parameter. InAndOut is not
					// given a narrower cone (#202's brief only locks the burst
					// MAGNITUDE as reduced, not the exit cone).
					(float burstSpeed, float forwardBurstScale, float exitConeDegrees) = _machine.CurrentMove switch
					{
						BehindTheBack  => (BehindTheBackBurstSpeed, BehindTheBackForwardBurstScale, BehindTheBackExitConeDegrees),
						BetweenTheLegs => (BetweenTheLegsBurstSpeed, BetweenTheLegsForwardBurstScale, BetweenTheLegsExitConeDegrees),
						InAndOut       => (InAndOutBurstSpeed, InAndOutForwardBurstScale, 180f),
						_              => (BurstSpeed, ForwardBurstScale, 180f),
					};

					Velocity = CrossoverBurstMath.ComposeActiveVelocity(
						Velocity, Heading, composeSign, exitVectorSample,
						burstSpeed, forwardBurstScale, ExitDeadzone,
						Mathf.DegToRad(exitConeDegrees));

					// Crossover/BehindTheBack/BetweenTheLegs swap the ball to
					// the (now near) empty hand — the authoritative hand-state
					// change (M9, #83). InAndOut does NOT (AC-2): the ball
					// never crosses, mirroring Hesitation's identical
					// no-swap rule — it rides this same predicted + reconciled
					// Active-entry event for its BURST only, never the hand.
					if (_machine.CurrentMove is not InAndOut)
						HandSide = HandStateResolver.Opposite(HandSide);
				}
				else if (_machine.JustEnteredActive && _machine.CurrentMove is RetreatDribble)
				{
					// Retreat dribble (#197): a modest, fixed hop straight back
					// along Heading — no left-stick exit shaping (unlike
					// StepBack below), no hand swap, no gather. Ball stays
					// Dribbling throughout; nothing here touches BallController.
					Vector2 backward = -HeadingMath.Forward(Heading);
					Velocity = new Vector3(backward.X, 0f, backward.Y) * RetreatDribbleBurstSpeed;
				}
				else if (_machine.JustEnteredActive && _machine.CurrentMove is StepBack)
				{
					// Step-back (#197): full backward burst, shaped by the
					// left-stick exit vector within a backward-only cone —
					// composed via #198's shared burst math (see
					// StepBackBurstMath's doc for why "flip the heading" is a
					// safe reuse rather than a second hand-rolled cone-clamp).
					Velocity = StepBackBurstMath.ComposeActiveVelocity(
						Heading, exitVectorSample, StepBackBurstSpeed, ExitDeadzone,
						Mathf.DegToRad(StepBackExitConeDegrees));

					// The gather: cradles a live dribble exactly like a
					// JumpShot's pump-fake does (#193's "dead Held" pattern),
					// but at ACTIVE-entry rather than Startup-entry — the
					// separation burst IS the gather motion here, per the
					// issue spec. CradleForShotStartup's guard logic (no-ops
					// unless the ball is Dribbling AND this player is the
					// holder) is generic enough to reuse verbatim despite its
					// shot-specific name; see its own doc for the exact
					// no-op conditions this relies on.
					GetBall()?.CradleForShotStartup(OwnPeerId);
				}
				else if (_machine.JustEnteredActive && _machine.CurrentMove is DriveGather)
				{
					// Drive-gather (#230, ADR-0022): plant onto the forward
					// drive line resolved during Startup (Heading has already
					// bent toward the rim, bounded by turn rate — see the
					// Startup case above). SET once here, never re-derived on
					// later Active ticks — the same "SET not +=" rule every
					// burst-family move follows (see CrossoverBurstMath's
					// class doc: additive velocity would overshoot on
					// reconcile replay). Carries whatever momentum survived
					// Startup's gather bleed (DriveGatherMath.ComposeActiveVelocity
					// adds, never re-zeroes it) rather than a second momentum
					// scheme — no exit-vector/exit-cone steering here by
					// design: this is a straight-line attack, the euro-step's
					// (#231) job is the lateral evasive variant. The gather
					// itself already happened at Startup-begin (see
					// BeginCommittedMove's cradle gate) — nothing to cradle
					// again here.
					Velocity = DriveGatherMath.ComposeActiveVelocity(Velocity, Heading, DriveGatherBurstSpeed);
				}
				else if (_machine.JustEnteredActive && _machine.CurrentMove is EuroStep euroStep)
				{
					// Euro-step (#231, ADR-0022) — beat 2: plant off the drive
					// line (Heading already rim-bent during Startup, same as
					// DriveGather) with a forward drive PLUS a fixed lateral hop in
					// the read direction. SET once here, never re-derived — the
					// same "SET not +=" rule (EuroStepMath adds to the surviving
					// momentum, never re-zeroes it; additive velocity would
					// overshoot on reconcile replay). The lateral sign is the
					// body-relative read carried on the move (LateralDirection);
					// EuroStepMath re-derives its world direction from the rim-bent
					// Heading. This move does NOT release the ball — the finish is
					// a separate Layup begun from this displaced position (the
					// chain; see EuroStep's class doc). The gather already fired at
					// Startup-begin (BeginCommittedMove's cradle gate), so nothing
					// to cradle again here.
					int lateralSign = System.Math.Sign(euroStep.LateralDirection);
					Velocity = EuroStepMath.ComposeActiveVelocity(
						Velocity, Heading, lateralSign,
						EuroStepForwardDriveSpeed, EuroStepLateralHopSpeed);
				}
				else if (_machine.CurrentMove is Spin spin)
				{
					// Spin (#201) is the ONLY branch in this switch NOT gated
					// on JustEnteredActive — its heading arc must advance
					// EVERY Active tick (contrast every sibling move, which
					// fires a one-shot effect once and never touches Velocity/
					// Heading again for the rest of Active). Mutually
					// exclusive with every other branch above by construction
					// (_machine.CurrentMove is exactly one concrete type), so
					// this is safe as the ladder's final, unconditional arm.
					if (_machine.JustEnteredActive)
					{
						// Captured ONCE, at the SAME tick and from the SAME
						// per-role source every sibling move already locks its
						// own burst read from — see _spinEntryHeading's and
						// _spinEntryExitVector's field docs for the full
						// doubt-driven-development reasoning (both the
						// bounded, accepted ReconcileFromServer-timing trade-
						// off and the fixed live-input-steering hole).
						_spinEntryHeading    = Heading;
						_spinEntryExitVector = exitVectorSample;
					}

					int spinSign = System.Math.Sign(spin.SpinDirection);
					MoveFrameData spinFd = spin.FrameData;

					// ── ADR-0010 SANCTIONED EXCEPTION ───────────────────────
					// Directly overwrites Heading, bypassing HeadingMath.
					// RotateToward's bounded non-linear turn-rate cap, for
					// THIS move's Active phase only — see the ADR-0010
					// amendment (docs/adr/0010-authoritative-heading.md, same
					// commit as this branch) for the full record. SpinHeadingMath.
					// ArcHeading is a PURE function of (entry heading,
					// direction, tick index, total Active ticks) — it reads
					// NO live per-tick input — so it reproduces bit-
					// identically on every role that runs this method: the
					// server's own tick, the server's copy of a remote
					// player, and the predicting client's own tick. See
					// SpinHeadingMath's class doc for why this is safe where
					// the burst's per-role exitVectorSample (below) is not.
					Heading = SpinHeadingMath.ArcHeading(
						_spinEntryHeading, spinSign, _machine.FrameInPhase, spinFd.ActiveFrames);

					// Hand swap + exit burst fire on the LAST Active tick —
					// "at the END of the rotation" (contrast Crossover/
					// BehindTheBack/BetweenTheLegs, which swap on
					// JustEnteredActive, the FIRST tick — see Spin's own class
					// doc for why this move's identity requires the opposite
					// timing). SpinHeadingMath.ArcHeading reaches EXACTLY the
					// full ~180° arc on this same tick (frameInPhase ==
					// activeFrames - 1 ⇒ progress == 1.0), so "the rotation
					// completes" and "the hand swaps" are structurally the
					// same event, never two that could drift apart.
					if (_machine.FrameInPhase == spinFd.ActiveFrames - 1)
					{
						// Composed against the ENTRY heading/exit-vector, NOT
						// the just-rotated Heading above — the translational
						// exit line continues the ORIGINAL drive direction;
						// only the player's authoritative FACING has spun
						// around. Using the rotated Heading here would clamp
						// away the forward contribution entirely (Compose
						// ActiveVelocity's "never backward" rule), since the
						// original drive direction is now ~180° BEHIND the
						// new facing — see Spin's class doc "Exit composition"
						// section.
						Velocity = CrossoverBurstMath.ComposeActiveVelocity(
							Velocity, _spinEntryHeading, spinSign, _spinEntryExitVector,
							SpinBurstSpeed, SpinForwardBurstScale, ExitDeadzone);

						HandSide = HandStateResolver.Opposite(HandSide);
					}
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
	/// Each concrete move's Id is unique and stable (see RequestBeginMove's doc
	/// comment for why a small if-chain, not a registry, is appropriate here).
	/// </summary>
	private static string MoveIdOf(CommittedMove move) => move?.Id ?? "";

	/// <summary>
	/// The move's reconstruction payload to broadcast — Crossover's and
	/// BehindTheBack's shared BurstDirection shape, StealMove's TargetHand, or
	/// (#243) JumpShot's IsFadeaway classification (1f/0f). 0 when there is no
	/// current move or the move type carries no extra payload.
	///
	/// (#21 doubt cycle 1, finding #2) _serverMoveId/_serverMoveParam are stored
	/// from every broadcast — satisfying "active move included in the server
	/// tick broadcast" — but Step 0 of ReconcileFromServer deliberately does
	/// not reconstruct a CommittedMove from them today; only _serverMovePhase
	/// is consulted (see that method's comment). They are reserved for a
	/// richer reconciliation once a second move type makes "client and server
	/// agree a move is active, but disagree on WHICH one" a reachable case.
	///
	/// (#243) Unlike the burst-family payloads (fixed at construction),
	/// JumpShot.IsFadeaway can change mid-life (false through Startup, set
	/// once at Active-entry) — harmless here, since ReceiveState broadcasts
	/// this every tick regardless (see the two Rpc(MethodName.ReceiveState...)
	/// call sites), so the remote-viewing client's DisplayFadeaway() picks up
	/// the flip within one broadcast of the release tick, same latency as
	/// every other DisplayMove-family cosmetic.
	/// </summary>
	private static float MoveParamOf(CommittedMove move) =>
		move is Crossover crossover         ? crossover.BurstDirection :
		move is BehindTheBack behindTheBack ? behindTheBack.BurstDirection :
		move is BetweenTheLegs betweenLegs  ? betweenLegs.BurstDirection :
		move is InAndOut inAndOut           ? inAndOut.BurstDirection :
		move is StealMove steal             ? (float)(int)steal.TargetHand :
		move is JumpShot jumpShot           ? (jumpShot.IsFadeaway ? 1f : 0f) :
		// BlockMove, Hesitation, and all future no-payload moves → 0f.
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
