#nullable enable

namespace Hooper.Moves;

/// <summary>
/// Pure decision for "should pressing shoot begin a Layup or a JumpShot"
/// (issue #229) — no Godot Node inheritance, mirroring JumpShotReleaseResolver
/// / FeintGateResolver's pattern: the decision itself is pure and unit-tested;
/// PlayerController.SampleMoveInput is the thin node-side glue that reads
/// GlobalPosition/RimCenter into this function's parameters.
///
/// ── Why proximity-to-rim selects the move (real ball, ADR-0014 tier 2)
/// ────────────────────────────────────────────────────────────────────────
/// #229 explicitly excludes any drive mechanic (that is #230's job) — there
/// is no committed "gather toward the rim" move yet to hang the layup off
/// of. Reusing the EXISTING shoot input, but branching on distance to the
/// rim at press time, is the minimal-scope way to make the layup reachable
/// at all without building ahead of #230: in real half-court ball, a player
/// close enough to the rim finishes with a layup motion; farther out, they
/// shoot a jumper. The threshold itself is not an arbitrary tuning knob —
/// it deliberately reuses the SAME "&lt;4m" boundary #229's own acceptance
/// criteria and ADR-0009's ≤3m≈100%-open anchor already describe as
/// "automatic when open," so the layup's range and its make-rate floor are
/// the same real-ball fact, not two independently guessed numbers.
///
/// Distance is XZ-plane only for the SAME reason ADR-0009 gives for the shot
/// scatter's own distance (scatter grows with court distance, not arc length —
/// a tall and short player at the same floor position get the same treatment).
///
/// ── What this distance is NOT (issue #236 review finding) ────────────────
/// An earlier version of this comment claimed the gate's distance "matches
/// BallController.ApplyShootLocally's own shot-distance calculation." It does
/// not, and the claim was load-bearing enough that #236 proposed deduplicating
/// the two into one helper on the strength of it. They measure different
/// things:
///
///   this gate            PLAYER GlobalPosition -> BallController.RimCenter
///   ApplyShootLocally    BALL   GlobalPosition -> BallController.ShotTarget
///
/// Both operands differ. RimCenter and ShotTarget are independent [Export]s
/// that merely happen to be equal today (scenes/Main.tscn sets both to
/// (0, 3.05, 0.3)) — ShotTarget's own doc explicitly invites offsetting it
/// away from the rim to test rim bounces, at which point folding these two
/// call sites together would silently re-anchor ADR-0009's scatter magnitude.
/// They share a FORMULA (XZ euclidean distance, via
/// DefensiveResolution.DistanceXZSquared), not a MEASUREMENT. Keep them
/// separate.
/// </summary>
public static class LayupRangeResolver
{
    /// <returns>
    /// True when <paramref name="distanceToRimXZ"/> is strictly less than
    /// <paramref name="layupRange"/> — a shot from exactly the boundary
    /// distance is a jump shot, not a layup (matches the codebase's existing
    /// convention of strict comparisons at boundaries, e.g.
    /// CourtBounds.IsOutOfBounds).
    /// </returns>
    public static bool IsLayupRange(float distanceToRimXZ, float layupRange)
        => distanceToRimXZ < layupRange;
}
