using Godot;

namespace Hooper.Player;

/// <summary>
/// Pure C# movement math for the player capsule — no Godot Node inheritance,
/// no engine singletons, no _PhysicsProcess, no RPCs.
///
/// Extracted from PlayerController.ComputeVelocity (issue #37) so the M1
/// accel/decel asymmetry can be unit-tested without a running Godot instance.
/// CharacterBody3D cannot be instantiated headlessly, so this logic lives here
/// as a plain static class that tests can call directly; PlayerController.Move
/// calls ComputeVelocity exactly as it did when the method was private.
///
/// Behavior-preserving: every parameter that used to be read from instance
/// state (Velocity, MoveSpeed, Accel, Decel) is now passed in explicitly.
/// No role checks, no network calls, no side effects — this must stay pure,
/// since it runs identically on the shared motion step (PlayerController.Move)
/// for server authority, client prediction, AND reconciliation replay.
/// </summary>
public static class MovementMath
{
	/// <summary>
	/// Turns desired direction into the next velocity. Asymmetric rates
	/// (decel &gt; accel) are where "change of pace" lives (ADR-0003): a
	/// player decelerates faster than they accelerate, which is what makes
	/// a sudden stop read as a deliberate change of pace rather than drift.
	/// </summary>
	/// <param name="current">This tick's starting velocity.</param>
	/// <param name="wishDir">Desired movement direction on the ground plane (world-space, not camera-relative — see PlayerController.Move).</param>
	/// <param name="delta">Physics step duration in seconds.</param>
	/// <param name="moveSpeed">Top ground speed in metres/second.</param>
	/// <param name="accel">Ground acceleration in m/s² — used while wishDir is non-zero.</param>
	/// <param name="decel">Ground deceleration in m/s² — used while wishDir is zero. Intentionally higher than accel.</param>
	/// <returns>The velocity for this tick, moved toward the target at the chosen rate.</returns>
	public static Vector3 ComputeVelocity(Vector3 current, Vector3 wishDir, double delta, float moveSpeed, float accel, float decel)
	{
		Vector3 target = wishDir * moveSpeed;
		float rate = wishDir == Vector3.Zero ? decel : accel;
		return current.MoveToward(target, rate * (float)delta);
	}
}
