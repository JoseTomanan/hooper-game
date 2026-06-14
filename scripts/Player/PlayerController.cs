using Godot;

namespace Hooper.Player;

/// <summary>
/// Drives a single player capsule with analog left-stick movement.
///
/// Milestone 1a: local, single-player, immediate feel. There is intentionally
/// NO networking here yet. The one forward-looking decision baked in now is the
/// split between *reading* input and *applying* movement: M1b's network layer
/// must be able to drive the exact same movement step from a server tick using a
/// replicated input vector, without this script ever touching the local gamepad.
/// Keeping that seam clean now is far cheaper than retrofitting it later.
/// </summary>
public partial class PlayerController : CharacterBody3D
{
	/// <summary>
	/// Top ground speed in metres/second. Exported so it can be tuned live in the
	/// Godot Inspector without recompiling.
	/// </summary>
	[Export] public float MoveSpeed { get; set; } = 6.0f;

	/// <summary>
	/// Ground acceleration in m/s² — how fast the capsule builds toward top speed
	/// while the stick is pushed. Lower = weightier, more perceptible ramp-up.
	/// </summary>
	[Export] public float Accel { get; set; } = 30.0f;

	/// <summary>
	/// Ground deceleration in m/s² — how fast speed bleeds off when the stick is
	/// released or reversed. Kept above <see cref="Accel"/> so plants/stops feel
	/// crisp; that asymmetry is the "change of pace" of ADR-0003's neutral game.
	/// </summary>
	[Export] public float Decel { get; set; } = 45.0f;

	public override void _PhysicsProcess(double delta)
	{
		// This is the ONLY place that reads the hardware. Everything downstream is
		// pure motion that M1b's server tick can replay from a replicated vector.
		Vector2 inputDir = ReadInput();
		Move(inputDir, delta);
	}

	/// <summary>
	/// Samples the left stick / WASD as a 2D intent vector. X = strafe (left/right),
	/// Y = forward/back, each already deadzone-filtered and clamped to magnitude ≤ 1
	/// by <c>Input.GetVector</c>. Kept separate so the network layer can bypass it
	/// and feed a replicated vector straight into <see cref="Move"/>.
	/// </summary>
	private static Vector2 ReadInput()
	{
		// Reads the named input-map actions the human wires in Project Settings
		// (issue #3). Do NOT normalize the result — that would discard analog
		// partial-tilt and make the stick feel binary.
		return Input.GetVector("move_left", "move_right", "move_forward", "move_backward");
	}

	/// <summary>
	/// Applies one physics step of movement from a 2D intent vector. Public and
	/// input-source-agnostic by design: M1b's server tick calls this with the
	/// client's replicated input so prediction and the authoritative sim run the
	/// identical motion code.
	/// </summary>
	public void Move(Vector2 inputDir, double delta)
	{
		// Map 2D intent onto the ground plane. In Godot, -Z is "forward", so a
		// forward press (inputDir.Y = -1) must travel along -Z.
		//
		// World-space, NOT camera-relative: the input vector means the same thing
		// on every machine, so M1b's server can replay it during prediction
		// without knowing each client's camera (ADR-0002). This holds only while
		// the broadcast camera has no yaw relative to world axes; if the camera
		// ever rotates, revisit this and rotate wishDir by the camera basis.
		Vector3 wishDir = new Vector3(inputDir.X, 0.0f, inputDir.Y);

		Velocity = ComputeVelocity(Velocity, wishDir, delta);
		MoveAndSlide();
	}

	/// <summary>
	/// Turns desired direction into the next velocity. This is the "feel" knob of
	/// the neutral game (ADR-0003: the left stick is spacing + change of pace).
	///
	/// Ramps toward the target speed instead of snapping: accelerate while the
	/// stick is pushed, decelerate when it's released. The two rates are separate
	/// on purpose — that asymmetry is where "change of pace" lives.
	/// </summary>
	private Vector3 ComputeVelocity(Vector3 current, Vector3 wishDir, double delta)
	{
		Vector3 target = wishDir * MoveSpeed;
		// No stick input => glide to a stop at Decel; otherwise build speed at Accel.
		float rate = wishDir == Vector3.Zero ? Decel : Accel;
		return current.MoveToward(target, rate * (float)delta);
	}
}
