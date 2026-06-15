using Godot;

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
/// ── M2 scope (local, single-player, no networking) ────────────────────────
/// For Milestone 2 there is no possession system or netcode yet. The ball
/// starts attached to an exported Holder and immediately begins dribbling, so
/// the human can see the dribble cycle. Pressing the "ball_shoot" input fires
/// a shot toward the rim; on a rim/backboard contact the ball goes Loose and
/// settles on the floor; an on-target shot swishes through cleanly.
/// Possession hand-off and the shot/dribble inputs proper move to M3/M4.
///
/// ── Determinism note ──────────────────────────────────────────────────────
/// _PhysicsProcess delta is variable; the pure steppers take dt as a parameter.
/// We pass the FIXED tick (1/PhysicsTicksPerSecond) — not the wall-clock delta —
/// so the trajectory is reproducible and will match a server replay in M4.
/// </summary>
public partial class BallController : Node3D
{
	// ── Holder / aim wiring (set in the editor) ───────────────────────────

	/// <summary>
	/// The player node the ball attaches to while Held / Dribbling. The dribble
	/// cycle tracks this node's XZ each tick. Null-guarded: if unset the ball
	/// simply dribbles in place at the origin.
	/// </summary>
	[Export] public Node3D Holder { get; set; }

	// ── Dribble tunables ──────────────────────────────────────────────────

	/// <summary>Hand height the dribble bounces up to (metres).</summary>
	[Export] public float DribbleHandHeight { get; set; } = 1.0f;

	/// <summary>Full down-and-up dribble cycle duration (seconds).</summary>
	[Export] public float DribblePeriod { get; set; } = 0.6f;

	// ── Shot tunables ─────────────────────────────────────────────────────

	/// <summary>Peak world-space Y the shot arc reaches (metres).</summary>
	[Export] public float ShotApexHeight { get; set; } = 4.0f;

	/// <summary>Downward acceleration applied to the shot + loose ball (m/s²).</summary>
	[Export] public float Gravity { get; set; } = 9.8f;

	// ── Basket geometry (must match the hoop node's placement) ────────────

	/// <summary>World-space centre of the rim ring; also the shot's aim point.</summary>
	[Export] public Vector3 RimCenter { get; set; } = new(0f, 3.05f, 0f);

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

	// ── Composed pure logic ───────────────────────────────────────────────

	/// <summary>The state machine that tracks which ball moment we're in.</summary>
	public BallStateMachine StateMachine { get; private set; }

	/// <summary>Convenience accessor for the current state.</summary>
	public BallState State => StateMachine.Current;

	private DribbleCycle _dribble;
	private RimBackboard _basket;

	/// <summary>
	/// The in-flight (or loose) trajectory. Non-null only while InFlight or
	/// Loose — it carries the position+velocity the integrator advances. Reused
	/// for the loose fall so a bounced ball keeps moving under gravity.
	/// </summary>
	private ShotArc _arc;

	// ── Lifecycle ─────────────────────────────────────────────────────────

	public override void _Ready()
	{
		_dribble = new DribbleCycle(DribbleHandHeight, DribblePeriod);
		_basket  = new RimBackboard(
			RimCenter, RimRadius, BallRadius, RimRestitution,
			BoardCenter, BoardNormal, BoardHalfWidth, BoardHalfHeight, BoardRestitution);

		// M2: ball starts in the holder's possession and immediately dribbles,
		// so the dribble cycle is visible the moment the scene runs. (Possession
		// hand-off becomes the network layer's job in M4.)
		StateMachine = new BallStateMachine(initialHolderPeerId: 0);
		StateMachine.StartDribble();
	}

	// ── Tick loop ─────────────────────────────────────────────────────────

	public override void _PhysicsProcess(double delta)
	{
		// Fixed timestep — NOT the variable wall-clock delta — so the arc is
		// deterministic and reproducible by a future server replay (M4).
		float dt = 1.0f / Engine.PhysicsTicksPerSecond;

		switch (State)
		{
			case BallState.Held:      TickHeld();        break;
			case BallState.Dribbling: TickDribbling(dt); break;
			case BallState.InFlight:  TickInFlight(dt);  break;
			case BallState.Loose:     TickLoose(dt);     break;
		}
	}

	// ── Per-state behaviour ───────────────────────────────────────────────

	/// <summary>Ball cradled at hand height above the holder. Shoot to release.</summary>
	private void TickHeld()
	{
		Vector3 origin = Holder?.GlobalPosition ?? Vector3.Zero;
		GlobalPosition = origin + Vector3.Up * DribbleHandHeight;
		TryShoot();
	}

	/// <summary>Ball bouncing in the dribble cycle, tracking the holder. Shoot to release.</summary>
	private void TickDribbling(float dt)
	{
		_dribble.Advance(dt);
		Vector3 origin = Holder?.GlobalPosition ?? Vector3.Zero;
		GlobalPosition = _dribble.GetBallPosition(origin);
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
				// M2: just announce it; M5 (#24/#25) turns this into a score.
				GD.Print("[Ball] Clean make.");
				break;
		}
	}

	/// <summary>
	/// Loose ball: keep falling under gravity until it settles on the floor.
	/// This makes the "missed shot → loose → rests on the court" outcome
	/// visible. A real loose-ball contest is an M4+ concern.
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

		GlobalPosition = _arc.Position;
	}

	// ── Shot trigger ──────────────────────────────────────────────────────

	/// <summary>
	/// Releases a shot toward the rim if the shoot input is pressed this tick.
	/// Legal only from Held / Dribbling; Shoot() enforces that and returns false
	/// otherwise, in which case we leave the arc untouched.
	/// </summary>
	private void TryShoot()
	{
		if (!Input.IsActionJustPressed(ShootAction)) return;
		if (!StateMachine.Shoot()) return;

		_arc = new ShotArc(GlobalPosition, RimCenter, ShotApexHeight, Gravity);
	}
}
