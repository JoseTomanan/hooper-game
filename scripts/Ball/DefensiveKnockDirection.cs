using Godot;

namespace Hooper.Ball;

/// <summary>
/// Pure degenerate-safe horizontal (XZ) direction helper, shared by the steal
/// knock and block swat provisional-velocity math (issue #216 original body
/// row 4). BallController.ResolveStealAttempts and ResolveBlockAttempts each
/// independently built the identical "normalize this horizontal delta, or
/// fall back to Vector3.Zero if it's too close to degenerate to normalize
/// safely" shape, with the same 0.0001f threshold — contest (#99) would have
/// grown a third copy.
///
/// ── Why pure / why here ───────────────────────────────────────────────────
/// Mirrors DefensiveResolution's seam discipline (ADR-0004): no Godot Node,
/// no engine singletons. Vector3 is a plain value struct, not an engine
/// dependency — the same precedent ShotArc/RimBackboard already establish
/// for "pure" classes in this codebase.
/// </summary>
public static class DefensiveKnockDirection
{
    private const float DegenerateThresholdSquared = 0.0001f;

    /// <summary>
    /// Projects <paramref name="delta"/> onto the horizontal (XZ) plane and
    /// normalizes it, or returns Vector3.Zero if the projection's squared
    /// length is at or below the degenerate threshold (e.g. the ball's XZ
    /// coincides with the reference point's — holder/defender standing on
    /// top of each other, or the ball directly under the rim). The returned
    /// vector's Y is always exactly 0, regardless of <paramref name="delta"/>'s
    /// own Y — callers apply their own vertical component (rise/drop speed)
    /// separately.
    /// </summary>
    public static Vector3 SafeHorizontal(Vector3 delta)
    {
        delta.Y = 0f;
        return delta.LengthSquared() > DegenerateThresholdSquared ? delta.Normalized() : Vector3.Zero;
    }
}
