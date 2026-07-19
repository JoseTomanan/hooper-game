using Godot;
using Hooper.Ball;

namespace Hooper.Player;

/// <summary>
/// Pure C# decision: should THIS jump-shot release be classified
/// fadeaway/off-balance for LEGIBILITY purposes (issue #243, parent #185)?
///
/// ── Why this exists ───────────────────────────────────────────────────────
/// A shot released mid-pivot is ALREADY inaccurate by the existing model —
/// <see cref="ShotFacing.Multiplier"/> scales scatter continuously by the
/// same heading-vs-rim angle this resolver reads (ADR-0009 §Amendment #81).
/// No accuracy change is needed here. What's missing is the visual cue: a
/// distinct fadeaway/off-balance shot animation so BOTH players can see the
/// attempt was low-percentage (ADR-0003 legibility). This resolver is the
/// pure boolean gate the AnimationTree integration (PlayerController.
/// ApplyAnimation / MoveAnimResolver) switches on.
///
/// ── Why it reuses ShotFacing, not a second constant ──────────────────────
/// Per the duplicated-constant tripwire (hooper-config-and-flags), the
/// "how far off-rim" angle is computed ONCE (<see cref="ShotFacing.
/// AngleFromTarget"/>) and the classification cutoff is
/// <see cref="ShotFacing.MateriallyDivergentAngleRadians"/> — ADR-0009's own
/// documented 90° "side-on" pivot point in its facing-factor table, not an
/// independently invented threshold.
///
/// ── Why pure / why here ───────────────────────────────────────────────────
/// No Godot Node inheritance, no engine singletons (ADR-0004 headless-seam
/// discipline) — mirrors MoveAnimResolver/JumpShotReleaseResolver's pattern
/// so the classification is unit-testable without a running Godot instance.
/// The caller (PlayerController, at the shooter's own JustEnteredActive tick
/// for a JumpShot — see TickCommittedMoveBehavior) reads the
/// server-authoritative <c>Heading</c> (ADR-0010) and the ball's <c>RimCenter</c>
/// and passes them in.
/// </summary>
public static class FadeawayTriggerResolver
{
    /// <returns>
    /// True when the shooter's heading diverges from the rim direction by at
    /// least <see cref="ShotFacing.MateriallyDivergentAngleRadians"/> (90°) —
    /// released while still mid-pivot, back-of-body toward the rim. False for
    /// a squared-up release, including the degenerate on-rim case (which
    /// <see cref="ShotFacing.AngleFromTarget"/> reports as 0 divergence).
    /// </returns>
    public static bool IsFadeaway(float headingYaw, Vector3 shooterPos, Vector3 rimCenter)
        => ShotFacing.AngleFromTarget(headingYaw, shooterPos, rimCenter)
           >= ShotFacing.MateriallyDivergentAngleRadians;
}
