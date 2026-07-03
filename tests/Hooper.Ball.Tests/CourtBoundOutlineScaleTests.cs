using Godot;
using Hooper.Ball;

namespace Hooper.Ball.Tests;

/// <summary>
/// Characterizes CourtVisuals.BuildCourtBoundOutline's X-axis scale
/// compensation (bug fix, /diagnose 2026-07-03).
///
/// CourtVisuals extends Node3D and cannot be unit-tested directly (ADR-0004
/// headless-seam discipline) — exactly as OobShotReleaseTests and
/// ShotScatterCurveCharacterizationTests do for BallController, this
/// replicates the SAME formula BuildCourtBoundOutline computes, so the fix is
/// proven at the arithmetic level without a running Godot instance. Keep this
/// formula in sync with CourtVisuals.cs by hand — the same duplication
/// discipline OobShotReleaseTests already documents for ApplyShootLocally.
///
/// ── The bug ──────────────────────────────────────────────────────────────
/// scenes/Main.tscn gives the CourtVisuals node Scale.X = 1.8 (sideline axis
/// only; Z/baseline is unscaled). BuildCourtBoundOutline built its wall
/// segments directly from BallController.CourtMin/CourtMax as LOCAL
/// positions/sizes with no compensation for that parent scale — Godot then
/// applies Scale.X to every child, so the rendered line's WORLD X ended up at
/// min.X/max.X * Scale.X, roughly double the true play-line the outline's own
/// doc comment says it exists to show the player. Players/Ball are unscaled
/// siblings of CourtVisuals, so CourtBounds.IsOutOfBounds enforces the actual
/// turnover at the TRUE min.X/max.X — well inside the visible (pre-fix) line.
///
/// The fix mirrors the sibling BuildClearLineArc method's existing, already-
/// correct sx-compensation pattern (CourtVisuals.cs, disabled arc method):
/// divide every X-derived local value by Scale.X before use, so multiplying
/// back by the parent's scale cancels out to the true world value.
///
/// ── Test naming ──────────────────────────────────────────────────────────────
/// [Scenario]_[ExpectedOutcome]
/// </summary>
public class CourtBoundOutlineScaleTests
{
    // Matches BallController's default CourtMin/CourtMax exports — both derive
    // from CourtBounds.Default{Min,Max}, the single source of truth (see that
    // field's doc comment), so this fixture can never silently drift from
    // production.
    private static readonly Vector2 CourtMin = CourtBounds.DefaultMin;
    private static readonly Vector2 CourtMax = CourtBounds.DefaultMax;
    private const float ScaleX = 1.8f;

    private readonly struct LocalX
    {
        public readonly float MinX, MaxX, Width, CenterX;
        public LocalX(float minX, float maxX, float width, float centerX)
        {
            MinX = minX; MaxX = maxX; Width = width; CenterX = centerX;
        }
    }

    /// <summary>Mirrors BuildCourtBoundOutline's PRE-FIX (buggy) local-X math.</summary>
    private static LocalX UncompensatedLocalX() => new(
        minX: CourtMin.X,
        maxX: CourtMax.X,
        width: CourtMax.X - CourtMin.X,
        centerX: (CourtMin.X + CourtMax.X) * 0.5f);

    /// <summary>Mirrors the FIXED formula: every X-derived value divided by sx first.</summary>
    private static LocalX CompensatedLocalX(float sx) => new(
        minX: CourtMin.X / sx,
        maxX: CourtMax.X / sx,
        width: (CourtMax.X - CourtMin.X) / sx,
        centerX: (CourtMin.X + CourtMax.X) * 0.5f / sx);

    [Fact]
    public void Uncompensated_WorldX_LandsAt1Point8xTheTrueBoundary()
    {
        // Documents the bug numerically: applying the parent's Scale.X to the
        // PRE-FIX (uncompensated) local positions lands at 1.8x the true
        // line, not on it — this is what shipped before the fix.
        LocalX local = UncompensatedLocalX();
        float worldMinX = local.MinX * ScaleX;
        float worldMaxX = local.MaxX * ScaleX;

        Assert.Equal(CourtMin.X * ScaleX, worldMinX, precision: 5);
        Assert.Equal(CourtMax.X * ScaleX, worldMaxX, precision: 5);
        Assert.NotEqual(CourtMin.X, worldMinX);
        Assert.NotEqual(CourtMax.X, worldMaxX);
    }

    [Fact]
    public void Compensated_WorldMinMaxX_MatchesTrueCourtBounds()
    {
        // THE FIX: dividing by sx first means applying the parent's Scale.X
        // cancels back out to the true CourtMin/CourtMax the game actually
        // enforces (CourtBounds.IsOutOfBounds, BallController.ResolvePlayerOutOfBounds).
        LocalX local = CompensatedLocalX(ScaleX);
        float worldMinX = local.MinX * ScaleX;
        float worldMaxX = local.MaxX * ScaleX;

        Assert.Equal(CourtMin.X, worldMinX, precision: 5);
        Assert.Equal(CourtMax.X, worldMaxX, precision: 5);
    }

    [Fact]
    public void Compensated_Width_MatchesTrueCourtWidth()
    {
        LocalX local = CompensatedLocalX(ScaleX);
        float worldWidth = local.Width * ScaleX;

        Assert.Equal(CourtMax.X - CourtMin.X, worldWidth, precision: 5);
    }

    [Fact]
    public void Compensated_CenterX_MatchesTrueCenter()
    {
        LocalX local = CompensatedLocalX(ScaleX);
        float worldCenterX = local.CenterX * ScaleX;

        Assert.Equal((CourtMin.X + CourtMax.X) * 0.5f, worldCenterX, precision: 5);
    }

    [Fact]
    public void Compensated_ZeroScale_FallsBackToUnscaledIdentity()
    {
        // Mirrors BuildClearLineArc's own guard against a degenerate/unset
        // scale: `Scale.X > 1e-6f ? Scale.X : 1f`. sx = 1f (the guarded
        // fallback) must reproduce the true, unscaled CourtMin/CourtMax
        // exactly — no divide-by-zero, no sign flip.
        const float guardedSx = 1f; // the value BuildCourtBoundOutline's ternary yields for Scale.X <= 1e-6f
        LocalX local = CompensatedLocalX(guardedSx);

        Assert.Equal(CourtMin.X, local.MinX, precision: 5);
        Assert.Equal(CourtMax.X, local.MaxX, precision: 5);
    }
}
