using Godot;
using Hooper.Ball;

namespace Hooper.Ball.Tests;

/// <summary>
/// Integration tests for the ShotArc → RimBackboard → Make pipeline (ADR-0004,
/// issue #46). These tests exercise the two pure classes together — the same path
/// BallController.TickInFlight follows — to confirm that realistic shots aimed at
/// the rim reach ContactResult.Make before any other contact.
///
/// ── Why these tests exist ────────────────────────────────────────────────────
/// The scoring bug reported in M6b ("shooting doesn't increment score") was
/// diagnosed as a GAME-RULE behaviour (the take-it-back / IsCleared gate in
/// BallController.ResolveServerMake), NOT a defect in the underlying shot
/// physics. These tests lock down the physics layer to prevent a regression
/// there from being misdiagnosed as a possession-rule bug in the future.
///
/// ── What is NOT tested here ──────────────────────────────────────────────────
/// BallController itself (a Node3D) cannot be instantiated headlessly, so the
/// IsCleared gate, AwardPossession, and the full GameManager integration are
/// exercised only in the Godot editor (see EDITOR_TASKS.md). The pure physics
/// tested here and the rules tested in ScoreboardTests / ClearLineTests are the
/// two headless pieces that bracket the Node layer.
///
/// ── Test naming convention ────────────────────────────────────────────────
/// [Subject]_[Scenario]_[ExpectedOutcome]
/// </summary>
public class ShotScoreIntegrationTests
{
    // ── Shared geometry (matches BallController defaults and Main.tscn hoop) ──

    private static readonly Vector3 RimCenter    = new(0f, 3.05f, 0f);
    private const float             RimRadius    = 0.23f;
    private const float             BallRadius   = 0.12f;
    private const float             Restitution  = 0.65f;
    private static readonly Vector3 BoardCenter  = new(0f, 3.5f,  0.3f);
    private static readonly Vector3 BoardNormal  = new(0f, 0f,   -1f);
    private const float             BoardHalfW   = 0.46f;
    private const float             BoardHalfH   = 0.30f;
    private const float             ApexHeight   = 4.0f;
    private const float             Gravity      = 9.8f;
    private const float             FixedDt      = 1f / 60f;   // 60 Hz physics tick
    private const int               MaxTicks     = 360;        // 6 s — any real shot lands

    private static RimBackboard DefaultBasket() =>
        new RimBackboard(RimCenter, RimRadius, BallRadius, Restitution,
                         BoardCenter, BoardNormal, BoardHalfW, BoardHalfH, Restitution);

    /// <summary>
    /// Steps the arc through up to MaxTicks, returning the first non-None
    /// ContactResult. Returns None if the ball never contacts anything.
    /// </summary>
    private static ContactResult FirstContact(ShotArc arc, RimBackboard basket)
    {
        for (int i = 0; i < MaxTicks; i++)
        {
            arc.Step(FixedDt);
            ContactResult r = basket.Resolve(arc);
            if (r != ContactResult.None)
                return r;
        }
        return ContactResult.None;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Straight-up shot from beneath the hoop
    // (player default spawn position: XZ = (0, 0), directly under the basket)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Tracer bullet: a shot aimed at the rim from directly below scores a Make.
    /// Confirms the fundamental "shot → Make" pipe works end-to-end.
    /// </summary>
    [Fact]
    public void ShotArc_AimedAtRim_FromDirectlyBelow_ReturnsMake()
    {
        // Player capsule centre at Y≈1.5; DribbleHandHeight = 1.0 → release Y = 2.5.
        var release = new Vector3(0f, 2.5f, 0f);
        var arc     = new ShotArc(release, RimCenter, ApexHeight, Gravity);
        var basket  = DefaultBasket();

        ContactResult result = FirstContact(arc, basket);

        Assert.Equal(ContactResult.Make, result);
    }

    /// <summary>
    /// A "swish" shot from beneath produces no rim Bounce before the Make.
    /// Guards against the contact-priority order (Make is checked before rim ring
    /// in RimBackboard.Resolve) being accidentally reversed.
    /// </summary>
    [Fact]
    public void ShotArc_AimedAtRim_FromDirectlyBelow_NoBounceBeforeMake()
    {
        var arc    = new ShotArc(new Vector3(0f, 2.5f, 0f), RimCenter, ApexHeight, Gravity);
        var basket = DefaultBasket();

        // The first contact result must be Make, not Bounce.
        ContactResult first = FirstContact(arc, basket);

        Assert.NotEqual(ContactResult.Bounce, first);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Standard mid-range shot from a cleared position (6 m from hoop, XZ plane)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A shot from a cleared position (6 m away, inside clear-line ≥ 5.8 m) aimed
    /// at the rim scores a Make — confirming the arc solver produces a valid
    /// trajectory from the standard shooting distance.
    /// </summary>
    [Fact]
    public void ShotArc_AimedAtRim_FromClearedPosition_ReturnsMake()
    {
        // Player at XZ (6, 0), capsule centre Y≈1.5, hand height 1.0 → release at (6, 2.5, 0).
        var release = new Vector3(6f, 2.5f, 0f);
        var arc     = new ShotArc(release, RimCenter, ApexHeight, Gravity);
        var basket  = DefaultBasket();

        ContactResult result = FirstContact(arc, basket);

        Assert.Equal(ContactResult.Make, result);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Make geometry — boundary conditions for the inner-opening window
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A ball aimed at the centre of the inner opening (horizDist = 0) that
    /// falls through at rim height detects Make — the core swish geometry.
    /// </summary>
    [Fact]
    public void Resolve_CentredDescendingBall_AtRimHeight_ReturnsMake()
    {
        // Pre-placed directly above the rim centre, falling at game-realistic speed.
        var arc = new ShotArc(
            releasePoint: new Vector3(0f, 10f, 0f),
            targetPoint:  RimCenter,
            apexHeight:   10f,
            gravity:      Gravity);
        // Override to a position inside the Y window, descending.
        arc.Position = RimCenter;                      // exactly at rim height
        arc.Velocity = new Vector3(0f, -5f, 0f);      // descending

        var basket = DefaultBasket();
        Assert.Equal(ContactResult.Make, basket.Resolve(arc));
    }

    /// <summary>
    /// A ball at the inner-opening boundary (horizDist exactly equal to
    /// RimRadius − BallRadius) does not score. The make check's boundary is
    /// exclusive (&lt;, not ≤), matching the geometry that "ball must fit inside
    /// the ring" — but at exactly this distance the ball is *also* exactly
    /// BallRadius from the ring itself (RimRadius − innerRadius == BallRadius
    /// along the same radial line), so the ring-contact check's boundary is
    /// exclusive too. Neither check fires: the true result is None, not Bounce.
    /// This pins both exclusive boundaries against a future "&lt;" → "&lt;=" slip.
    /// </summary>
    [Fact]
    public void Resolve_BallAtInnerOpeningBoundary_Descending_DoesNotMake()
    {
        // Place ball exactly on the inner-opening boundary (not inside).
        float innerRadius = RimRadius - BallRadius;   // 0.23 - 0.12 = 0.11 m
        var arc = new ShotArc(
            releasePoint: new Vector3(0f, 10f, 0f),
            targetPoint:  RimCenter,
            apexHeight:   10f,
            gravity:      Gravity);
        arc.Position = new Vector3(RimCenter.X + innerRadius, RimCenter.Y, RimCenter.Z);
        arc.Velocity = new Vector3(0f, -5f, 0f);

        var basket = DefaultBasket();
        // horizDist == innerRadius → NOT < innerRadius → Make fails.
        // distToRing == RimRadius - innerRadius == BallRadius → NOT < BallRadius → ring bounce fails too.
        Assert.Equal(ContactResult.None, basket.Resolve(arc));
    }

    /// <summary>
    /// A ball just past the inner-opening boundary (closer to the rim centre
    /// than the make threshold, but still outside the ring itself) genuinely
    /// clips the front of the rim — a real grazing miss, distinct from the
    /// exact-boundary case above where neither check fires.
    /// </summary>
    [Fact]
    public void Resolve_BallJustOutsideOpening_Descending_ReturnsBounce()
    {
        float innerRadius = RimRadius - BallRadius;        // 0.11 m — make threshold
        float justOutside = innerRadius + 0.01f;            // 0.12 m — clips the ring
        var arc = new ShotArc(
            releasePoint: new Vector3(0f, 10f, 0f),
            targetPoint:  RimCenter,
            apexHeight:   10f,
            gravity:      Gravity);
        arc.Position = new Vector3(RimCenter.X + justOutside, RimCenter.Y, RimCenter.Z);
        arc.Velocity = new Vector3(0f, -5f, 0f);

        var basket = DefaultBasket();
        // horizDist (0.12) > innerRadius (0.11) → Make fails.
        // distToRing == RimRadius - justOutside == 0.11 < BallRadius (0.12) → ring contact.
        Assert.Equal(ContactResult.Bounce, basket.Resolve(arc));
    }
}
