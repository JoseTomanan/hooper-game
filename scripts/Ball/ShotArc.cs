using System;
using Godot;

namespace Hooper.Ball;

/// <summary>
/// Deterministic parabolic shot/pass arc integrator for the InFlight ball state.
///
/// ── Why hand-authored math and not a physics engine? ─────────────────────
/// ADR-0004: all ball motion must be bit-identical on server and every client.
/// Godot Physics / Jolt is not guaranteed deterministic across platforms.
/// This class uses a closed-form launch velocity solve + a fixed-timestep
/// semi-implicit Euler integrator, producing the same trajectory everywhere
/// given the same inputs and fixed dt.
///
/// ── Launch velocity solve ─────────────────────────────────────────────────
/// Inputs: release point, target point, apex height, gravity constant.
///
/// From the classic projectile equations under constant downward gravity g:
///
///   1. Vy_launch = sqrt(2 * g * (apexHeight - releaseY))
///      — the upward velocity needed to coast from releaseY to apexHeight.
///
///   2. t_up   = Vy_launch / g
///      — time from release to apex (Vy becomes zero at apex).
///
///   3. t_down = sqrt(2 * (apexHeight - targetY) / g)
///      — time to fall from apex down to targetY (free fall from rest).
///
///   4. t_total = t_up + t_down
///      — total flight time.
///
///   5. Vx = (targetX - releaseX) / t_total
///      Vz = (targetZ - releaseZ) / t_total
///      — constant horizontal velocities (no air resistance).
///
/// Assumption: apexHeight > max(releaseY, targetY). If the caller violates
/// this, the square root is taken of a negative number. The implementation
/// clamps to a floor of zero inside the sqrt for graceful degradation, but
/// well-formed inputs are the caller's responsibility.
///
/// ── Fixed-timestep integrator ─────────────────────────────────────────────
/// Trapezoidal (average-velocity) integration — exact for constant
/// acceleration, unlike semi-implicit Euler:
///
///   newVelocity.Y = velocity.Y - gravity * dt   // gravity updates velocity
///   position     += 0.5 * (velocity + newVelocity) * dt  // average drives position
///
/// Semi-implicit Euler (position driven by the NEW velocity only) has a
/// systematic undershoot of 0.5*gravity*t*dt that grows with elapsed flight
/// time — at ~1s of flight (a routine mid-range shot) this is several
/// centimetres, enough to make a dead-centre-aimed shot clang the front of the
/// rim instead of swishing (issue #46). Averaging old and new velocity for the
/// position update reproduces the exact closed-form parabola
/// position(t) = release + v0*t - 0.5*gravity*t^2 at every tick boundary — this
/// is the standard trapezoidal rule, which integrates a quadratic exactly.
///
/// Applied at a fixed dt matching Engine.PhysicsTicksPerSecond (60 Hz).
/// The dt is passed as a parameter so tests can drive it with a known fixed
/// value without a running Godot engine.
///
/// ── API contract for issue #11 (rim/backboard) ────────────────────────────
/// Each tick:
///   arc.Step(dt);                // advance one physics tick
///   Vector3 pos = arc.Position;  // current ball centre
///   Vector3 vel = arc.Velocity;  // current velocity (for reflection math)
///
/// Rim/backboard intersection code should read Position after each Step and
/// compare against the geometry of the rim circle and backboard plane. On
/// intersection, it may set Position and Velocity directly (via the mutable
/// properties exposed for #11) to implement a bounce response, then call
/// Step() again for the next tick.
///
/// ── Tunables ─────────────────────────────────────────────────────────────
///   releasePoint — world-space ball position at the moment of release.
///   targetPoint  — intended landing point (e.g. basket centre at rim height).
///   apexHeight   — peak Y the ball should reach (metres, world-space).
///   gravity      — downward acceleration constant (m/s²). Default 9.8.
///
/// ── No engine I/O, no DateTime, no Random ────────────────────────────────
/// Trajectory is a pure function of (initial conditions, accumulated Step()
/// calls with a fixed dt). Same inputs always produce the same outputs.
/// </summary>
public sealed class ShotArc
{
    // ── Tunables (read-only) ──────────────────────────────────────────────

    /// <summary>
    /// Downward acceleration constant (m/s²). Applied each Step() as
    /// velocity.Y -= Gravity * dt.
    /// </summary>
    public float Gravity { get; }

    // ── Mutable state ─────────────────────────────────────────────────────

    /// <summary>
    /// Current world-space ball position (metres).
    /// Updated each Step() call.
    ///
    /// Exposed mutable so issue #11 (rim/backboard collision) can apply a
    /// position correction when the ball intersects geometry, then continue
    /// stepping. Do not write this outside of collision response code.
    /// </summary>
    public Vector3 Position { get; set; }

    /// <summary>
    /// Current velocity vector (m/s).
    /// Updated each Step() call.
    ///
    /// Exposed mutable so issue #11 can reflect velocity on rim/backboard
    /// contact. The X and Z components are constant under gravity alone;
    /// only Y is modified by Step(). Collision response may modify any
    /// component to implement a bounce.
    /// </summary>
    public Vector3 Velocity { get; set; }

    // ── Constructor ───────────────────────────────────────────────────────

    /// <summary>
    /// Creates a ShotArc and solves the launch velocity from the given
    /// release point, target, apex height, and gravity constant.
    ///
    /// After construction, call Step(dt) each physics tick to advance the
    /// ball, and read Position / Velocity after each step.
    /// </summary>
    /// <param name="releasePoint">World-space ball position at release.</param>
    /// <param name="targetPoint">
    ///   Intended landing point (e.g. basket rim centre at its Y height).
    /// </param>
    /// <param name="apexHeight">
    ///   Peak Y the ball should reach (world-space metres).
    ///   Must be greater than both releasePoint.Y and targetPoint.Y.
    /// </param>
    /// <param name="gravity">
    ///   Downward acceleration (m/s²). Positive value; applied as downward.
    ///   Default 9.8 m/s² (Earth gravity).
    /// </param>
    public ShotArc(
        Vector3 releasePoint,
        Vector3 targetPoint,
        float   apexHeight,
        float   gravity = 9.8f)
    {
        Gravity  = gravity;
        Position = releasePoint;
        Velocity = SolveInitialVelocity(releasePoint, targetPoint, apexHeight, gravity);
    }

    // ── Integrator ────────────────────────────────────────────────────────

    /// <summary>
    /// Advances the ball by one fixed physics tick using trapezoidal
    /// (average-velocity) integration:
    ///
    ///   newVelocity.Y = Velocity.Y - Gravity * dt   (gravity updates velocity)
    ///   Position     += 0.5 * (Velocity + newVelocity) * dt  (average drives position)
    ///
    /// Averaging the pre- and post-gravity velocity for the position update is
    /// exact for constant acceleration (it reproduces the closed-form parabola
    /// at every tick boundary — see the class docstring), unlike semi-implicit
    /// Euler (position driven by the new velocity alone), which undershoots by
    /// a growing amount the longer the ball is in flight.
    ///
    /// The caller (BallController._PhysicsProcess) should pass
    /// (float)(1.0 / Engine.PhysicsTicksPerSecond) as dt at runtime.
    /// Tests pass a known constant so results are deterministic without the
    /// Godot engine running.
    ///
    /// Issue #11 note: call this once per tick, then read Position to check
    /// against rim/backboard geometry. If a collision is detected, modify
    /// Position (depenetrate) and Velocity (reflect) before the next Step().
    /// </summary>
    /// <param name="dt">Elapsed time this tick (seconds).</param>
    public void Step(float dt)
    {
        // Gravity acts only on Y (downward); X and Z are inertial.
        Vector3 oldVel = Velocity;
        Vector3 newVel = oldVel;
        newVel.Y -= Gravity * dt;
        Velocity  = newVel;

        // Trapezoidal position update: average of old and new velocity.
        Position += 0.5f * (oldVel + newVel) * dt;
    }

    // ── Launch velocity solver ────────────────────────────────────────────

    /// <summary>
    /// Solves the initial velocity vector that sends the ball from
    /// <paramref name="release"/> to <paramref name="target"/> while passing
    /// through <paramref name="apexHeight"/> under constant
    /// <paramref name="gravity"/>.
    ///
    /// Math derivation:
    ///
    ///   Vertical: ball decelerates at -g from release until Vy = 0 at apex.
    ///     Vy_launch = sqrt(2 * g * (apexHeight - release.Y))
    ///     t_up      = Vy_launch / g
    ///
    ///   From apex, ball falls freely to target.Y:
    ///     t_down    = sqrt(2 * max(apexHeight - target.Y, 0) / g)
    ///
    ///   Total flight time:
    ///     t_total   = t_up + t_down
    ///
    ///   Horizontal (constant, no drag):
    ///     Vx = (target.X - release.X) / t_total
    ///     Vz = (target.Z - release.Z) / t_total
    ///
    /// The clamp in t_down (max with 0) prevents a sqrt of a negative number
    /// when target.Y ≥ apexHeight (malformed input). Callers should ensure
    /// apexHeight > max(release.Y, target.Y) for a physically meaningful arc.
    /// </summary>
    private static Vector3 SolveInitialVelocity(
        Vector3 release,
        Vector3 target,
        float   apexHeight,
        float   gravity)
    {
        // Vertical component — velocity needed to reach apex from release point.
        float riseH   = MathF.Max(apexHeight - release.Y, 0.0f);
        float vyLaunch = MathF.Sqrt(2.0f * gravity * riseH);

        float tTotal = ComputeFlightTime(release.Y, target.Y, apexHeight, gravity);

        // Horizontal velocities (constant, no drag).
        // Guard against degenerate t_total = 0 (release == target, zero apex delta).
        float vx = tTotal > 0.0f ? (target.X - release.X) / tTotal : 0.0f;
        float vz = tTotal > 0.0f ? (target.Z - release.Z) / tTotal : 0.0f;

        return new Vector3(vx, vyLaunch, vz);
    }

    /// <summary>
    /// Closed-form total flight time (seconds) for an arc rising from
    /// <paramref name="releaseY"/> to <paramref name="apexHeight"/> and
    /// falling back down to <paramref name="targetY"/> under
    /// <paramref name="gravity"/> — the same t_up + t_down derivation
    /// SolveInitialVelocity uses internally to solve horizontal velocity.
    ///
    /// Exposed publicly (issue #216 original body row 6) for callers that
    /// only need DURATION, not a full velocity solve — e.g. a harness
    /// computing a generous upper bound on "how many ticks should this shot
    /// take to land" (BlockTurnoverTest.ComputeUnblockedMakeTicks used to
    /// re-derive this exact math by hand, which could silently drift from
    /// the real physics on a future ShotArc change).
    ///
    ///   t_up   = sqrt(2 * g * max(apexHeight - releaseY, 0)) / g
    ///   t_down = sqrt(2 * max(apexHeight - targetY, 0) / g)
    ///   t_total = t_up + t_down
    ///
    /// Same malformed-input caveat as SolveInitialVelocity: callers should
    /// ensure apexHeight > max(releaseY, targetY) for a physically
    /// meaningful arc (the max-with-0 clamps prevent a negative-sqrt crash,
    /// not a physically sensible result, if that's violated).
    /// </summary>
    public static float ComputeFlightTime(float releaseY, float targetY, float apexHeight, float gravity)
    {
        float riseH    = MathF.Max(apexHeight - releaseY, 0.0f);
        float vyLaunch = MathF.Sqrt(2.0f * gravity * riseH);
        float tUp      = vyLaunch / gravity;

        float fallH = MathF.Max(apexHeight - targetY, 0.0f);
        float tDown = MathF.Sqrt(2.0f * fallH / gravity);

        return tUp + tDown;
    }
}
