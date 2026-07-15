using Godot;
using Hooper.Ball;

namespace Hooper.Ball.Tests;

/// <summary>
/// Unit tests for RimBackboard — the deterministic hand-authored collision
/// resolver for rim and backboard geometry (ADR-0004, issue #11).
///
/// All tests run without a live Godot instance. RimBackboard is pure C# with
/// no Node inheritance, no engine singletons, and no Random/DateTime.
///
/// ── Geometry model ──────────────────────────────────────────────────────────
/// Rim  : horizontal ring — a circle of RimRadius centred at RimCenter.
///        The ring has no thickness; contact occurs when the ball centre
///        is within ballRadius of the nearest point on the ring circumference.
///        A "make" is when the ball passes DOWN through the inner opening
///        (horizontal distance from RimCenter < RimRadius - ballRadius,
///         and velocity Y < 0).
///
/// Board: bounded vertical plane — a rectangle at a given position with a
///        unit normal.  Contact occurs when the ball crosses the plane within
///        the board's width/height extents.
///
/// ── Result type ─────────────────────────────────────────────────────────────
/// Resolve() returns ContactResult:
///   None   — no contact; caller does nothing.
///   Bounce — rim or backboard hit; caller should call GoLoose() on the state
///            machine and write the corrected pos/vel back to the arc.
///   Make   — clean swish through the hoop; caller handles scoring; ball does
///            NOT go Loose from the make transition itself (caller decides).
///
/// ── Reflection math (closed-form, deterministic) ─────────────────────────
///   v' = v − (1 + e)(v · n)n     where e = restitution [0..1]
///   Simplified: reflect the normal component and scale it by restitution,
///   keep the tangential component intact.
///
/// ── Test naming convention ───────────────────────────────────────────────
/// [Subject]_[Scenario]_[ExpectedOutcome]
///
/// One logical assertion per test (multiple Assert lines only when they form
/// one indivisible logical check — e.g., result AND corrected velocity).
/// </summary>
public class RimBackboardTests
{
    // ── Shared constants ──────────────────────────────────────────────────

    private const float FineEpsilon   = 0.0001f;
    private const float CoarseEpsilon = 0.01f;

    // Realistic defaults mirroring ADR-0004 geometry note.
    private static readonly Vector3 DefaultRimCenter    = new(0f, 3.05f, 0f);
    private const float             DefaultRimRadius    = 0.23f;
    private const float             DefaultBallRadius   = 0.12f;
    private const float             DefaultRestitution  = 0.65f;

    // Backboard sits behind/above the rim (positive Z is "behind" the hoop
    // in these tests). Normal points toward the court (negative Z).
    private static readonly Vector3 DefaultBoardCenter  = new(0f, 3.35f,  0.30f);
    private static readonly Vector3 DefaultBoardNormal  = new(0f, 0f,    -1f);
    private const float             DefaultBoardHalfW   = 0.46f;  // half of ~0.91 m board width
    private const float             DefaultBoardHalfH   = 0.30f;  // half of ~0.61 m board height

    // ── Factory helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Builds a RimBackboard with realistic defaults.  Individual tests may
    /// construct their own instance with simplified numbers for clarity.
    /// </summary>
    private static RimBackboard DefaultGeometry() =>
        new RimBackboard(
            rimCenter:        DefaultRimCenter,
            rimRadius:        DefaultRimRadius,
            ballRadius:       DefaultBallRadius,
            rimRestitution:   DefaultRestitution,
            boardCenter:      DefaultBoardCenter,
            boardNormal:      DefaultBoardNormal,
            boardHalfWidth:   DefaultBoardHalfW,
            boardHalfHeight:  DefaultBoardHalfH,
            boardRestitution: DefaultRestitution);

    /// <summary>
    /// Builds a ShotArc with the given start position and velocity, gravity
    /// of 9.8 m/s².  We use a target far away so the arc solver doesn't
    /// interfere with our hand-placed initial conditions.  Tests then
    /// override Position/Velocity directly to exercise specific scenarios.
    /// </summary>
    private static ShotArc ArcAt(Vector3 position, Vector3 velocity)
    {
        // Solve a generic arc; we override the mutable state immediately.
        var arc = new ShotArc(
            releasePoint: new Vector3(0f, 0f, -10f),
            targetPoint:  new Vector3(0f, 0f,  10f),
            apexHeight:   5f,
            gravity:      9.8f);
        arc.Position = position;
        arc.Velocity = velocity;
        return arc;
    }

    // ═════════════════════════════════════════════════════════════════════
    // Construction — tunables are stored and readable
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void Constructor_RimCenter_IsStored()
    {
        var geo = DefaultGeometry();
        Assert.Equal(DefaultRimCenter.X, geo.RimCenter.X, FineEpsilon);
        Assert.Equal(DefaultRimCenter.Y, geo.RimCenter.Y, FineEpsilon);
        Assert.Equal(DefaultRimCenter.Z, geo.RimCenter.Z, FineEpsilon);
    }

    [Fact]
    public void Constructor_RimRadius_IsStored()
    {
        var geo = DefaultGeometry();
        Assert.Equal(DefaultRimRadius, geo.RimRadius, FineEpsilon);
    }

    [Fact]
    public void Constructor_BallRadius_IsStored()
    {
        var geo = DefaultGeometry();
        Assert.Equal(DefaultBallRadius, geo.BallRadius, FineEpsilon);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Resolve — no contact (ball well away from all geometry)
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_BallFarFromAll_ReturnsNone()
    {
        // Ball 3 m above the rim — nowhere near rim ring or backboard.
        var geo = DefaultGeometry();
        var arc = ArcAt(
            position: new Vector3(0f, 6f, 0f),
            velocity: new Vector3(0f, -3f, 0f));

        ContactResult result = geo.Resolve(arc);

        Assert.Equal(ContactResult.None, result);
    }

    [Fact]
    public void Resolve_BallFarFromAll_PositionUnchanged()
    {
        var geo = DefaultGeometry();
        var startPos = new Vector3(0f, 6f, 0f);
        var arc = ArcAt(position: startPos, velocity: new Vector3(0f, -3f, 0f));

        geo.Resolve(arc);

        Assert.Equal(startPos.X, arc.Position.X, FineEpsilon);
        Assert.Equal(startPos.Y, arc.Position.Y, FineEpsilon);
        Assert.Equal(startPos.Z, arc.Position.Z, FineEpsilon);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Resolve — Make (clean swish through the hoop opening)
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_BallDescendingThroughInnerOpening_ReturnsMake()
    {
        // Ball centred directly above rim centre, moving straight down,
        // horizontal distance from rim centre = 0 < (RimRadius - BallRadius).
        // Use simplified round numbers: rim at y=3, radius=0.5, ballR=0.1.
        var geo = new RimBackboard(
            rimCenter:        new Vector3(0f, 3f, 0f),
            rimRadius:        0.5f,
            ballRadius:       0.1f,
            rimRestitution:   0.6f,
            boardCenter:      new Vector3(0f, 4f, 2f),   // board far away
            boardNormal:      new Vector3(0f, 0f, -1f),
            boardHalfWidth:   0.1f,
            boardHalfHeight:  0.1f,
            boardRestitution: 0.6f);

        // Ball is at rim height, exactly centred, moving downward.
        var arc = ArcAt(
            position: new Vector3(0f, 3f, 0f),
            velocity: new Vector3(0f, -5f, 0f));

        ContactResult result = geo.Resolve(arc);

        Assert.Equal(ContactResult.Make, result);
    }

    [Fact]
    public void Resolve_MakeShot_VelocityIsNotReflected()
    {
        // On a make the velocity must pass through unchanged — no bounce.
        var geo = new RimBackboard(
            rimCenter:        new Vector3(0f, 3f, 0f),
            rimRadius:        0.5f,
            ballRadius:       0.1f,
            rimRestitution:   0.6f,
            boardCenter:      new Vector3(0f, 4f, 2f),
            boardNormal:      new Vector3(0f, 0f, -1f),
            boardHalfWidth:   0.1f,
            boardHalfHeight:  0.1f,
            boardRestitution: 0.6f);

        var downVel = new Vector3(0f, -5f, 0f);
        var arc = ArcAt(position: new Vector3(0f, 3f, 0f), velocity: downVel);

        geo.Resolve(arc);

        // Velocity must remain unchanged (no reflection on a make).
        Assert.Equal(downVel.X, arc.Velocity.X, FineEpsilon);
        Assert.Equal(downVel.Y, arc.Velocity.Y, FineEpsilon);
        Assert.Equal(downVel.Z, arc.Velocity.Z, FineEpsilon);
    }

    [Fact]
    public void Resolve_BallDescendingThroughOpening_ButMovingUpward_IsNotMake()
    {
        // Ball is inside the opening horizontally, but moving UP — not a make
        // (make requires downward motion through the hoop).
        var geo = new RimBackboard(
            rimCenter:        new Vector3(0f, 3f, 0f),
            rimRadius:        0.5f,
            ballRadius:       0.1f,
            rimRestitution:   0.6f,
            boardCenter:      new Vector3(0f, 4f, 2f),
            boardNormal:      new Vector3(0f, 0f, -1f),
            boardHalfWidth:   0.1f,
            boardHalfHeight:  0.1f,
            boardRestitution: 0.6f);

        var arc = ArcAt(
            position: new Vector3(0f, 3f, 0f),
            velocity: new Vector3(0f, +5f, 0f));  // moving UP

        ContactResult result = geo.Resolve(arc);

        Assert.NotEqual(ContactResult.Make, result);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Resolve — Rim bounce
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_BallContactsRimRing_ReturnsBounce()
    {
        // Place the ball so its centre is exactly (RimRadius) from the rim
        // centre in the horizontal plane — i.e., on the rim ring itself —
        // and within ballRadius of it (ball centred on the rim torus surface).
        // Use simplified numbers: rim at y=3, radius=0.5, ballRadius=0.1.
        // Ball centre at x=(0.5), y=3 → distance from ring = 0, which is < 0.1.
        var geo = new RimBackboard(
            rimCenter:        new Vector3(0f, 3f, 0f),
            rimRadius:        0.5f,
            ballRadius:       0.1f,
            rimRestitution:   0.6f,
            boardCenter:      new Vector3(0f, 4f, 2f),
            boardNormal:      new Vector3(0f, 0f, -1f),
            boardHalfWidth:   0.1f,
            boardHalfHeight:  0.1f,
            boardRestitution: 0.6f);

        // Ball centre at (0.5, 3, 0) — nearest point on ring is (0.5, 3, 0)
        // so distance = 0 < ballRadius 0.1 → contact.
        var arc = ArcAt(
            position: new Vector3(0.5f, 3f, 0f),
            velocity: new Vector3(1f, -3f, 0f));

        ContactResult result = geo.Resolve(arc);

        Assert.Equal(ContactResult.Bounce, result);
    }

    [Fact]
    public void Resolve_RimBounce_VelocityNormalComponentIsReversed()
    {
        // Simplified geometry: rim at origin height 0, radius 1.0, ball radius 0.1.
        // Ball at (1.0, 0, 0) — on the ring. Velocity pointing inward: (-2, 0, 0).
        // Contact normal = outward radial from ring to ball = (1, 0, 0) (ball is
        // at exactly the ring point, so normal is directly outward from rim centre).
        // After reflection: Vx' = -Vx * restitution → positive.
        var geo = new RimBackboard(
            rimCenter:        new Vector3(0f, 0f, 0f),
            rimRadius:        1.0f,
            ballRadius:       0.1f,
            rimRestitution:   1.0f,   // e=1 → elastic, easy math: v'=(+2,0,0)
            boardCenter:      new Vector3(0f, 10f, 10f),
            boardNormal:      new Vector3(0f, 0f, -1f),
            boardHalfWidth:   0.01f,
            boardHalfHeight:  0.01f,
            boardRestitution: 1.0f);

        // Ball centre at (1.0, 0, 0): nearest ring point is also (1.0, 0, 0).
        // Outward normal from ring point to ball = (1, 0, 0).
        var arc = ArcAt(
            position: new Vector3(1.0f, 0f, 0f),
            velocity: new Vector3(-2f, 0f, 0f));

        geo.Resolve(arc);

        // After elastic reflection off normal (1,0,0): Vx flips sign.
        Assert.True(arc.Velocity.X > 0f,
            $"Expected Vx > 0 after rim bounce, got {arc.Velocity.X}");
    }

    [Fact]
    public void Resolve_RimBounce_TangentialVelocityPreserved()
    {
        // Vz is perpendicular to the contact normal (1,0,0) — it must be unchanged.
        var geo = new RimBackboard(
            rimCenter:        new Vector3(0f, 0f, 0f),
            rimRadius:        1.0f,
            ballRadius:       0.1f,
            rimRestitution:   0.7f,
            boardCenter:      new Vector3(0f, 10f, 10f),
            boardNormal:      new Vector3(0f, 0f, -1f),
            boardHalfWidth:   0.01f,
            boardHalfHeight:  0.01f,
            boardRestitution: 1.0f);

        var arc = ArcAt(
            position: new Vector3(1.0f, 0f, 0f),
            velocity: new Vector3(-2f, 0f, 1.5f));

        geo.Resolve(arc);

        // Vz (tangential to normal (1,0,0)) must be unchanged.
        Assert.Equal(1.5f, arc.Velocity.Z, FineEpsilon);
    }

    [Fact]
    public void Resolve_RimBounce_PositionDepenetratedOutsideRing()
    {
        // After a rim bounce the ball centre must be at least ballRadius away
        // from the nearest point on the rim ring (no interpenetration).
        var geo = new RimBackboard(
            rimCenter:        new Vector3(0f, 0f, 0f),
            rimRadius:        1.0f,
            ballRadius:       0.1f,
            rimRestitution:   0.6f,
            boardCenter:      new Vector3(0f, 10f, 10f),
            boardNormal:      new Vector3(0f, 0f, -1f),
            boardHalfWidth:   0.01f,
            boardHalfHeight:  0.01f,
            boardRestitution: 1.0f);

        // Ball deeply interpenetrating: centre at (1.0, 0, 0), ballRadius=0.1,
        // distance to ring = 0 (less than ballRadius).
        var arc = ArcAt(
            position: new Vector3(1.0f, 0f, 0f),
            velocity: new Vector3(-2f, 0f, 0f));

        geo.Resolve(arc);

        // Compute distance from corrected position to nearest ring point.
        float correctedHorizDist = MathF.Sqrt(
            arc.Position.X * arc.Position.X + arc.Position.Z * arc.Position.Z);
        float distToRing = MathF.Abs(correctedHorizDist - 1.0f);  // ring radius = 1.0

        Assert.True(distToRing >= 0.1f - CoarseEpsilon,
            $"Ball still interpenetrating ring after depenetration. distToRing={distToRing}");
    }

    [Fact]
    public void Resolve_RimRestitution_SpeedIsReducedAfterBounce()
    {
        // With restitution < 1 the normal-component speed must decrease.
        var geo = new RimBackboard(
            rimCenter:        new Vector3(0f, 0f, 0f),
            rimRadius:        1.0f,
            ballRadius:       0.1f,
            rimRestitution:   0.6f,    // 40% energy loss in normal direction
            boardCenter:      new Vector3(0f, 10f, 10f),
            boardNormal:      new Vector3(0f, 0f, -1f),
            boardHalfWidth:   0.01f,
            boardHalfHeight:  0.01f,
            boardRestitution: 1.0f);

        // Pure normal impact: ball at (1,0,0), velocity inward (-3,0,0).
        var arc = ArcAt(
            position: new Vector3(1.0f, 0f, 0f),
            velocity: new Vector3(-3f, 0f, 0f));

        float speedBefore = 3.0f;
        geo.Resolve(arc);
        float speedAfter = arc.Velocity.Length();

        Assert.True(speedAfter < speedBefore,
            $"Expected speed reduction after bounce (restitution=0.6). Before={speedBefore}, After={speedAfter}");
    }

    [Fact]
    public void Resolve_BallOutsideRimRing_ByMoreThanBallRadius_ReturnsNone()
    {
        // Ball well outside the rim ring — no contact.
        // Rim radius=0.5, ball radius=0.1. Ball at horizontal dist=1.0 from
        // rim centre → distance to ring = 1.0 - 0.5 = 0.5, which is > 0.1.
        var geo = new RimBackboard(
            rimCenter:        new Vector3(0f, 3f, 0f),
            rimRadius:        0.5f,
            ballRadius:       0.1f,
            rimRestitution:   0.6f,
            boardCenter:      new Vector3(0f, 4f, 2f),
            boardNormal:      new Vector3(0f, 0f, -1f),
            boardHalfWidth:   0.01f,
            boardHalfHeight:  0.01f,
            boardRestitution: 0.6f);

        var arc = ArcAt(
            position: new Vector3(1.0f, 3f, 0f),
            velocity: new Vector3(-3f, 0f, 0f));

        ContactResult result = geo.Resolve(arc);

        Assert.Equal(ContactResult.None, result);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Resolve — Backboard bounce
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_BallHitsBackboardWithinBounds_ReturnsBounce()
    {
        // Board: plane at z=1, normal=(0,0,-1) pointing toward the court (−Z side).
        // A ball coming from the court side is at z < 1 (on the +signedDist side
        // of the board). Contact when (ballPos - boardCenter)·normal > 0
        // and < BallRadius.
        //
        // Ball at z=0.95: signedDist = (0.95-1)*(-1) = 0.05 < BallRadius(0.1) → contact.
        var geo = new RimBackboard(
            rimCenter:        new Vector3(0f, -10f, 0f),  // rim far away
            rimRadius:        0.01f,
            ballRadius:       0.1f,
            rimRestitution:   0.6f,
            boardCenter:      new Vector3(0f, 0f, 1f),
            boardNormal:      new Vector3(0f, 0f, -1f),
            boardHalfWidth:   1.0f,
            boardHalfHeight:  1.0f,
            boardRestitution: 0.7f);

        // Ball on the court side (z<1), moving toward the board (+Z direction).
        var arc = ArcAt(
            position: new Vector3(0f, 0f, 0.95f),
            velocity: new Vector3(0f, 0f, 3f));

        ContactResult result = geo.Resolve(arc);

        Assert.Equal(ContactResult.Bounce, result);
    }

    [Fact]
    public void Resolve_BackboardBounce_NormalComponentIsReversed()
    {
        // Board at z=1, normal=(0,0,-1). Ball at z=0.95 moving toward board (+Z).
        // After elastic reflection Vz must flip to negative (toward court).
        var geo = new RimBackboard(
            rimCenter:        new Vector3(0f, -10f, 0f),
            rimRadius:        0.01f,
            ballRadius:       0.1f,
            rimRestitution:   0.6f,
            boardCenter:      new Vector3(0f, 0f, 1f),
            boardNormal:      new Vector3(0f, 0f, -1f),
            boardHalfWidth:   1.0f,
            boardHalfHeight:  1.0f,
            boardRestitution: 1.0f);   // elastic for simple math

        var arc = ArcAt(
            position: new Vector3(0f, 0f, 0.95f),
            velocity: new Vector3(0f, 0f, 3f));

        geo.Resolve(arc);

        Assert.True(arc.Velocity.Z < 0f,
            $"Expected Vz < 0 after backboard bounce, got {arc.Velocity.Z}");
    }

    [Fact]
    public void Resolve_BackboardBounce_TangentialVelocityPreserved()
    {
        // Vx and Vy are tangential to normal (0,0,-1) — must be unchanged.
        var geo = new RimBackboard(
            rimCenter:        new Vector3(0f, -10f, 0f),
            rimRadius:        0.01f,
            ballRadius:       0.1f,
            rimRestitution:   0.6f,
            boardCenter:      new Vector3(0f, 0f, 1f),
            boardNormal:      new Vector3(0f, 0f, -1f),
            boardHalfWidth:   1.0f,
            boardHalfHeight:  1.0f,
            boardRestitution: 0.75f);

        var arc = ArcAt(
            position: new Vector3(0f, 0f, 0.95f),
            velocity: new Vector3(1.5f, -2f, 3f));

        geo.Resolve(arc);

        Assert.Equal(1.5f, arc.Velocity.X, FineEpsilon);
        Assert.Equal(-2f,  arc.Velocity.Y, FineEpsilon);
    }

    [Fact]
    public void Resolve_BackboardBounce_PositionDepenetrated()
    {
        // After a backboard bounce the ball's surface must not overlap the
        // board plane.  With boardNormal=(0,0,-1) and boardCenter.Z=1, the
        // court-side surface of the board is at z=1.  Ball surface = ballCentre.Z
        // + BallRadius in the +Z direction.  After depenetration ballCentre.Z
        // must be <= boardCenter.Z - BallRadius  (= 0.9) so the ball is fully
        // on the court side.
        var geo = new RimBackboard(
            rimCenter:        new Vector3(0f, -10f, 0f),
            rimRadius:        0.01f,
            ballRadius:       0.1f,
            rimRestitution:   0.6f,
            boardCenter:      new Vector3(0f, 0f, 1f),
            boardNormal:      new Vector3(0f, 0f, -1f),
            boardHalfWidth:   1.0f,
            boardHalfHeight:  1.0f,
            boardRestitution: 0.7f);

        // Ball partially inside the board (z=0.92, signedDist=(0.92-1)*(-1)=0.08 < 0.1).
        var arc = ArcAt(
            position: new Vector3(0f, 0f, 0.92f),
            velocity: new Vector3(0f, 0f, 3f));

        geo.Resolve(arc);

        // After depenetration the ball centre must be at z ≤ 0.9
        // (so the ball surface at z+0.1 just touches z=1).
        Assert.True(arc.Position.Z <= 1f - 0.1f + CoarseEpsilon,
            $"Ball still inside board after depenetration. Z={arc.Position.Z}");
    }

    [Fact]
    public void Resolve_BackboardBounce_RestitutionReducesNormalSpeed()
    {
        // Board at z=1, normal=(0,0,-1), restitution=0.5.
        // Ball at z=0.95, Vz=+4 (toward board). After bounce Vz should be ≈ -2
        // (reflected and halved by restitution).
        var geo = new RimBackboard(
            rimCenter:        new Vector3(0f, -10f, 0f),
            rimRadius:        0.01f,
            ballRadius:       0.1f,
            rimRestitution:   0.6f,
            boardCenter:      new Vector3(0f, 0f, 1f),
            boardNormal:      new Vector3(0f, 0f, -1f),
            boardHalfWidth:   1.0f,
            boardHalfHeight:  1.0f,
            boardRestitution: 0.5f);

        var arc = ArcAt(
            position: new Vector3(0f, 0f, 0.95f),
            velocity: new Vector3(0f, 0f, 4f));

        geo.Resolve(arc);

        // Vz was +4, reflected and scaled by 0.5 → should be ≈ -2.
        Assert.Equal(-2f, arc.Velocity.Z, CoarseEpsilon);
    }

    [Fact]
    public void Resolve_BallOutsideBackboardBounds_DoesNotBounce()
    {
        // Ball is in front of the board plane but outside its rectangular extents
        // (half-width = 0.5 m, ball at X=2.0 → outside).
        var geo = new RimBackboard(
            rimCenter:        new Vector3(0f, -10f, 0f),
            rimRadius:        0.01f,
            ballRadius:       0.1f,
            rimRestitution:   0.6f,
            boardCenter:      new Vector3(0f, 0f, 1f),
            boardNormal:      new Vector3(0f, 0f, -1f),
            boardHalfWidth:   0.5f,
            boardHalfHeight:  1.0f,
            boardRestitution: 0.7f);

        // Ball at X=2.0 > halfWidth=0.5, on the court side (z=0.95), moving +Z.
        var arc = ArcAt(
            position: new Vector3(2.0f, 0f, 0.95f),
            velocity: new Vector3(0f, 0f, 3f));

        ContactResult result = geo.Resolve(arc);

        Assert.Equal(ContactResult.None, result);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Determinism
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_Determinism_IdenticalInputsProduceIdenticalBounce()
    {
        // Two independent arcs in the same position/velocity must produce
        // bit-identical corrected position and velocity after Resolve().
        var geoA = DefaultGeometry();
        var geoB = DefaultGeometry();

        var pos = new Vector3(DefaultRimRadius, DefaultRimCenter.Y, 0f);
        var vel = new Vector3(-2f, -1f, 0f);

        var arcA = ArcAt(pos, vel);
        var arcB = ArcAt(pos, vel);

        geoA.Resolve(arcA);
        geoB.Resolve(arcB);

        Assert.Equal(arcA.Position.X, arcB.Position.X, FineEpsilon);
        Assert.Equal(arcA.Position.Y, arcB.Position.Y, FineEpsilon);
        Assert.Equal(arcA.Position.Z, arcB.Position.Z, FineEpsilon);
        Assert.Equal(arcA.Velocity.X, arcB.Velocity.X, FineEpsilon);
        Assert.Equal(arcA.Velocity.Y, arcB.Velocity.Y, FineEpsilon);
        Assert.Equal(arcA.Velocity.Z, arcB.Velocity.Z, FineEpsilon);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Consumer contract — result type drives GoLoose
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_RimBounce_StateTransitionIsCallerResponsibility()
    {
        // The geometry class must NOT know about BallStateMachine.  This test
        // verifies the caller pattern: Bounce result → caller calls GoLoose.
        var geo = new RimBackboard(
            rimCenter:        new Vector3(0f, 0f, 0f),
            rimRadius:        1.0f,
            ballRadius:       0.1f,
            rimRestitution:   0.6f,
            boardCenter:      new Vector3(0f, 10f, 10f),
            boardNormal:      new Vector3(0f, 0f, -1f),
            boardHalfWidth:   0.01f,
            boardHalfHeight:  0.01f,
            boardRestitution: 1.0f);

        var sm = new BallStateMachine(initialHolderPeerId: 1);
        sm.Shoot();  // Held → InFlight

        var arc = ArcAt(
            position: new Vector3(1.0f, 0f, 0f),
            velocity: new Vector3(-2f, 0f, 0f));

        ContactResult result = geo.Resolve(arc);

        // Caller pattern: if Bounce → GoLoose.
        if (result == ContactResult.Bounce) sm.GoLoose();

        Assert.Equal(BallState.Loose, sm.Current);
    }

    [Fact]
    public void Resolve_MakeShot_StateMachineRemainsInFlight()
    {
        // A Make does NOT call GoLoose — the ball is still in flight (caller
        // handles scoring separately and decides what to do with the ball).
        var geo = new RimBackboard(
            rimCenter:        new Vector3(0f, 3f, 0f),
            rimRadius:        0.5f,
            ballRadius:       0.1f,
            rimRestitution:   0.6f,
            boardCenter:      new Vector3(0f, 4f, 2f),
            boardNormal:      new Vector3(0f, 0f, -1f),
            boardHalfWidth:   0.1f,
            boardHalfHeight:  0.1f,
            boardRestitution: 0.6f);

        var sm = new BallStateMachine(initialHolderPeerId: 1);
        sm.Shoot();  // Held → InFlight

        var arc = ArcAt(
            position: new Vector3(0f, 3f, 0f),
            velocity: new Vector3(0f, -5f, 0f));

        ContactResult result = geo.Resolve(arc);

        // Make does NOT trigger GoLoose — state stays InFlight.
        if (result == ContactResult.Bounce) sm.GoLoose();

        Assert.Equal(BallState.InFlight, sm.Current);
    }

    // ═════════════════════════════════════════════════════════════════════
    // IsBoardBehindRim — placement invariant (issue #217)
    //
    // BallController's exported RimCenter/BoardCenter/BoardNormal ARE the
    // defaults RimBackboard's own DefaultXxx fields intentionally do NOT
    // mirror (see the comment atop the DefaultBoardCenter field above — this
    // file's fixture uses its own self-consistent local geometry, unrelated
    // to production values). BallController is a Node-derived class and
    // cannot be constructed in this plain-xUnit project (no live Godot
    // instance — hooper-verification-and-qa §1/§2), so the mirrored consts
    // below are this suite's only way to pin the exported defaults' geometry
    // as a pure test. If BallController's RimCenter/BoardCenter/BoardNormal
    // defaults are ever retuned, update these three consts in the same
    // commit (the ShotScatterCurveCharacterizationTests.cs precedent for
    // this "must mirror" duplication, hooper-config-and-flags §4).
    // ═════════════════════════════════════════════════════════════════════

    private static readonly Vector3 LiveRimCenter   = new(0f, 3.05f, 0f);
    private static readonly Vector3 LiveBoardCenter = new(0f, 3.205f, -0.27f);
    private static readonly Vector3 LiveBoardNormal = new(0f, 0f, -1f);

    [Fact]
    public void IsBoardBehindRim_BallControllerDefaults_IsTrue()
    {
        // Pins the fix: a code-built BallController tree (headless harnesses,
        // future tests — no .tscn override in play) must get a board that
        // sits behind the rim along the approach axis, matching production
        // Main.tscn's RELATIVE placement (rim (0,3.05,0.3), board
        // (0,3.205,0.03) — board 0.27 m behind rim). Before the fix,
        // BoardCenter (0, 3.5, 0.3) was 0.3 m IN FRONT of RimCenter
        // (0, 3.05, 0) and this assertion failed.
        Assert.True(RimBackboard.IsBoardBehindRim(LiveRimCenter, LiveBoardCenter, LiveBoardNormal));
    }

    [Fact]
    public void IsBoardBehindRim_BoardInFrontOfRim_IsFalse()
    {
        // The pre-fix BallController defaults, verbatim — pins the OLD
        // (broken) geometry as a genuine negative case so this invariant
        // check is proven to actually discriminate, not just always return
        // true for any input.
        var brokenRimCenter   = new Vector3(0f, 3.05f, 0f);
        var brokenBoardCenter = new Vector3(0f, 3.5f, 0.3f);
        var boardNormal       = new Vector3(0f, 0f, -1f);

        Assert.False(RimBackboard.IsBoardBehindRim(brokenRimCenter, brokenBoardCenter, boardNormal));
    }

    [Fact]
    public void IsBoardBehindRim_BoardExactlyOnRimPlane_IsFalse()
    {
        // Zero along the approach axis is the boundary — not strictly
        // behind, so it must not count as satisfying the invariant.
        var rimCenter   = new Vector3(0f, 3.05f, 0f);
        var boardCenter = new Vector3(0f, 4f, 0f); // same Z as rim
        var boardNormal = new Vector3(0f, 0f, -1f);

        Assert.False(RimBackboard.IsBoardBehindRim(rimCenter, boardCenter, boardNormal));
    }
}
