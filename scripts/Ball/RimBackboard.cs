using Godot;

namespace Hooper.Ball;

/// <summary>
/// Pure deterministic collision resolver for the basketball rim and backboard
/// (ADR-0004, issue #11).
///
/// ── Why hand-authored geometry and not Godot Physics / Jolt? ─────────────
/// ADR-0004 mandates bit-identical ball motion on server and every client.
/// Godot Physics and Jolt are not guaranteed deterministic across platforms or
/// engine versions. All collision math here is closed-form — pure functions of
/// inputs with no engine state, no Random, no DateTime.
///
/// ── Geometry models ──────────────────────────────────────────────────────
///
/// Rim — a horizontal ring (circle in the XZ plane) at RimCenter, radius
/// RimRadius.  The ring has no thickness.  A ball of radius BallRadius
/// contacts the rim when the distance from the ball centre to the nearest
/// point on the ring circumference is less than BallRadius.
///
///   Nearest point on ring to ball centre P:
///     1. Project P onto the rim plane:  Pxz = (P.X, RimCenter.Y, P.Z)
///     2. Direction from rim centre to Pxz: d = normalize(Pxz − RimCenter)
///     3. Nearest ring point: Q = RimCenter + d * RimRadius
///     4. Distance: |P − Q|
///
/// Make (clean swish) — the ball is inside the inner opening and moving
/// downward.  Condition:
///   horizDist(P, RimCenter) < RimRadius − BallRadius   AND   vel.Y < 0
/// In this case NO reflection occurs; the ball passes through the hoop.
///
/// Backboard — a bounded axis-aligned rectangle in world space, described by:
///   BoardCenter, BoardNormal (unit), BoardHalfWidth, BoardHalfHeight.
/// Contact when:
///   1. Signed distance from ball centre to the board plane < BallRadius, AND
///   2. The projection of the ball centre onto the board plane lies within the
///      (BoardHalfWidth × BoardHalfHeight) rectangle centred on BoardCenter.
///
/// ── Reflection math (semi-implicit, closed-form) ──────────────────────────
/// For a contact normal n̂ and restitution e ∈ [0, 1]:
///
///   v_normal    = (v · n̂) n̂          // normal component of velocity
///   v_tangent   = v − v_normal        // tangential component (preserved)
///   v'          = v_tangent − e * v_normal
///
/// Equivalent to v' = v − (1 + e)(v · n̂)n̂, the standard coefficient-of-
/// restitution formula. Tangential component is perfectly preserved (no
/// friction model at this milestone).
///
/// ── Depenetration ────────────────────────────────────────────────────────
/// After computing the corrected velocity, the position is pushed to the
/// exact contact surface (BallRadius from the geometry) along the contact
/// normal. This prevents the ball from tunnelling deeper next tick.
///
/// ── Tunables ─────────────────────────────────────────────────────────────
/// All geometry parameters are supplied at construction and stored as public
/// read-only properties.  No magic constants are baked in. Realistic defaults
/// for a regulation NBA basket:
///   RimCenter       — world-space centre of the rim ring, e.g. (0, 3.05, 0)
///   RimRadius       — 0.23 m (≈ 9 inches)
///   BallRadius      — 0.12 m (≈ 4.7 inches, men's regulation ball)
///   RimRestitution  — 0.65 (empirical; rim is rigid steel, some energy lost)
///   BoardCenter     — world-space centre of the backboard face
///   BoardNormal     — unit normal pointing toward the court (away from board)
///   BoardHalfWidth  — 0.46 m (half of ≈ 0.91 m board width)
///   BoardHalfHeight — 0.30 m (half of ≈ 0.61 m board height)
///   BoardRestitution— 0.65 (fibreglass/tempered glass; similar to rim)
///
/// ── API contract ─────────────────────────────────────────────────────────
/// Each physics tick after ShotArc.Step(dt):
///
///   ContactResult result = geometry.Resolve(arc);
///
///   switch (result)
///   {
///       case ContactResult.Bounce:
///           stateMachine.GoLoose();  // caller drives state transition
///           break;
///       case ContactResult.Make:
///           // scorer.RegisterBasket(); — caller handles separately
///           break;
///       case ContactResult.None:
///           break;
///   }
///
/// Resolve() writes the corrected Position and Velocity directly into the
/// ShotArc when contact is detected, so the arc is ready to Step() again
/// next tick with the post-bounce state.
///
/// ── No engine I/O, no DateTime, no Random ────────────────────────────────
/// Pure function of inputs. Same RimBackboard + same ShotArc state =
/// identical ContactResult and identical corrected arc every time.
/// </summary>
public sealed class RimBackboard
{
    // ── Rim tunables ──────────────────────────────────────────────────────

    /// <summary>World-space centre of the rim ring at rim height.</summary>
    public Vector3 RimCenter { get; }

    /// <summary>Radius of the rim ring in metres (≈ 0.23 m regulation).</summary>
    public float RimRadius { get; }

    /// <summary>Radius of the basketball in metres (≈ 0.12 m regulation).</summary>
    public float BallRadius { get; }

    /// <summary>
    /// Coefficient of restitution for rim contact [0..1].
    /// 0 = perfectly inelastic (dead stop). 1 = perfectly elastic (no energy loss).
    /// </summary>
    public float RimRestitution { get; }

    // ── Backboard tunables ────────────────────────────────────────────────

    /// <summary>World-space centre of the backboard face.</summary>
    public Vector3 BoardCenter { get; }

    /// <summary>
    /// Unit normal of the backboard plane, pointing toward the court
    /// (i.e., in the direction a ball would bounce off the board toward the basket).
    /// </summary>
    public Vector3 BoardNormal { get; }

    /// <summary>Half-width of the board rectangle along the axis perpendicular
    /// to BoardNormal and the world-up axis (≈ 0.46 m for a regulation board).</summary>
    public float BoardHalfWidth { get; }

    /// <summary>Half-height of the board rectangle along the world-up axis (≈ 0.30 m).</summary>
    public float BoardHalfHeight { get; }

    /// <summary>
    /// Coefficient of restitution for backboard contact [0..1].
    /// Fibreglass/tempered glass boards have similar restitution to the steel rim.
    /// </summary>
    public float BoardRestitution { get; }

    // ── Constructor ───────────────────────────────────────────────────────

    /// <summary>
    /// Creates a RimBackboard resolver with the given geometry tunables.
    /// All parameters are stored as-is; no normalisation is applied.
    /// Callers must supply a normalised BoardNormal.
    /// </summary>
    public RimBackboard(
        Vector3 rimCenter,
        float   rimRadius,
        float   ballRadius,
        float   rimRestitution,
        Vector3 boardCenter,
        Vector3 boardNormal,
        float   boardHalfWidth,
        float   boardHalfHeight,
        float   boardRestitution)
    {
        RimCenter        = rimCenter;
        RimRadius        = rimRadius;
        BallRadius       = ballRadius;
        RimRestitution   = rimRestitution;
        BoardCenter      = boardCenter;
        BoardNormal      = boardNormal;
        BoardHalfWidth   = boardHalfWidth;
        BoardHalfHeight  = boardHalfHeight;
        BoardRestitution = boardRestitution;
    }

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>
    /// Tests the ball's current position and velocity against rim and backboard
    /// geometry, applies reflection and depenetration to the arc when contact
    /// is detected, and returns the contact result so the caller can drive the
    /// correct state-machine transition.
    ///
    /// Priority order: the make condition is checked first — a centred,
    /// descending ball through the inner opening is a clean swish even if it
    /// grazes the ring, so it must not be misclassified as a rim bounce. If
    /// not a make, the rim ring is checked, then the backboard.
    ///
    /// Writes corrected Position and Velocity into <paramref name="arc"/>
    /// when result is Bounce. Does NOT modify arc on Make or None.
    /// </summary>
    /// <param name="arc">
    ///   The ShotArc to test. Position and Velocity are read and, on Bounce,
    ///   written back with the post-bounce state.
    /// </param>
    /// <returns>
    ///   ContactResult.Bounce — caller should call stateMachine.GoLoose().
    ///   ContactResult.Make   — caller should register a score; no GoLoose.
    ///   ContactResult.None   — no contact; caller does nothing.
    /// </returns>
    public ContactResult Resolve(ShotArc arc)
    {
        Vector3 pos = arc.Position;
        Vector3 vel = arc.Velocity;

        // ── 1. Check rim contact first ────────────────────────────────────
        // Compute horizontal distance from ball centre to rim centre.
        float dx = pos.X - RimCenter.X;
        float dz = pos.Z - RimCenter.Z;
        float horizDist = MathF.Sqrt(dx * dx + dz * dz);

        // ── 2. Make (clean swish) — ball inside inner opening, moving down ─
        // The inner opening radius is (RimRadius - BallRadius): the ball
        // clears the rim when its entire cross-section fits inside the ring.
        //
        // Y proximity guard: the make can only trigger when the ball centre
        // is within BallRadius of the rim height. Without this guard a ball
        // travelling straight down at high altitude with horizDist ≈ 0 would
        // falsely register as a make long before reaching the hoop.
        float innerRadius = RimRadius - BallRadius;
        float yDistToRim  = MathF.Abs(pos.Y - RimCenter.Y);
        if (horizDist < innerRadius && vel.Y < 0f && yDistToRim <= BallRadius * 2f)
        {
            // Ball is on-target, at rim height, and descending: clean make.
            // Velocity is not reflected — the ball passes through cleanly.
            return ContactResult.Make;
        }

        // ── 3. Rim ring contact ───────────────────────────────────────────
        // Distance from ball centre to nearest point on the rim ring.
        float distToRing = RimRingDistance(pos, horizDist);
        if (distToRing < BallRadius)
        {
            ApplyRimBounce(arc, pos, vel, horizDist);
            return ContactResult.Bounce;
        }

        // ── 4. Backboard contact ──────────────────────────────────────────
        if (BackboardContact(pos))
        {
            ApplyBackboardBounce(arc, pos, vel);
            return ContactResult.Bounce;
        }

        return ContactResult.None;
    }

    // ── Private geometry helpers ──────────────────────────────────────────

    /// <summary>
    /// Computes the distance from the ball centre P to the nearest point on
    /// the rim ring.
    ///
    /// The rim ring is a circle of radius RimRadius centred at RimCenter,
    /// lying in the horizontal plane (Y = RimCenter.Y).
    ///
    /// Nearest point Q on the ring:
    ///   1. Project P horizontally: Pxz.
    ///   2. Direction from RimCenter to Pxz (unit): d̂ = (Pxz − RimCenterXZ) / horizDist.
    ///      Special case: if horizDist ≈ 0, any direction on the ring is equally
    ///      near; we pick +X arbitrarily (gives consistent depenetration direction).
    ///   3. Q = RimCenter + d̂ * RimRadius   (on the ring, at rim Y)
    ///   4. distance = |P − Q|   (3-D)
    ///
    /// <paramref name="horizDist"/> is pre-computed by the caller to avoid
    /// a redundant sqrt.
    /// </summary>
    private float RimRingDistance(Vector3 pos, float horizDist)
    {
        // Nearest point on ring in 3-D.
        Vector3 nearestOnRing = NearestRingPoint(pos, horizDist);

        float ex = pos.X - nearestOnRing.X;
        float ey = pos.Y - nearestOnRing.Y;
        float ez = pos.Z - nearestOnRing.Z;
        return MathF.Sqrt(ex * ex + ey * ey + ez * ez);
    }

    /// <summary>
    /// Returns the 3-D nearest point on the rim ring to the ball centre P.
    /// The ring point is always at Y = RimCenter.Y (the ring is horizontal).
    /// </summary>
    private Vector3 NearestRingPoint(Vector3 pos, float horizDist)
    {
        float dirX, dirZ;

        if (horizDist > 1e-6f)
        {
            dirX = (pos.X - RimCenter.X) / horizDist;
            dirZ = (pos.Z - RimCenter.Z) / horizDist;
        }
        else
        {
            // Ball centre directly above/below rim centre — pick +X arbitrarily.
            dirX = 1f;
            dirZ = 0f;
        }

        return new Vector3(
            RimCenter.X + dirX * RimRadius,
            RimCenter.Y,              // ring lies at rim height
            RimCenter.Z + dirZ * RimRadius);
    }

    /// <summary>
    /// Applies rim bounce: reflects velocity off the contact normal
    /// (direction from nearest ring point to ball centre), scales the normal
    /// component by RimRestitution, then depenetrates the ball so its surface
    /// just touches the ring.
    /// </summary>
    private void ApplyRimBounce(ShotArc arc, Vector3 pos, Vector3 vel, float horizDist)
    {
        Vector3 nearestOnRing = NearestRingPoint(pos, horizDist);

        // Contact normal: from the ring surface toward the ball centre (outward).
        float nx = pos.X - nearestOnRing.X;
        float ny = pos.Y - nearestOnRing.Y;
        float nz = pos.Z - nearestOnRing.Z;
        float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);

        if (len < 1e-8f)
        {
            // Degenerate: ball centre exactly on the nearest ring point —
            // the contact normal is ambiguous from the 3-D vector alone.
            // Physically the ball is sitting on top of the rim: push it
            // outward in the horizontal radial direction (away from the
            // rim axis), which is the direction the rim itself would deflect
            // a ball landing on it. We use the horizontal direction from
            // the rim centre to the ring point (already computed).
            float rdx = nearestOnRing.X - RimCenter.X;
            float rdz = nearestOnRing.Z - RimCenter.Z;
            float rlen = MathF.Sqrt(rdx * rdx + rdz * rdz);
            if (rlen > 1e-8f)
            {
                nx = rdx / rlen; ny = 0f; nz = rdz / rlen;
            }
            else
            {
                // Absolute degenerate (ball exactly above rim centre, on ring):
                // push along +X arbitrarily — any horizontal direction is valid.
                nx = 1f; ny = 0f; nz = 0f;
            }
            len = 1f; // already normalised above
        }

        // Normalise.
        nx /= len; ny /= len; nz /= len;

        // Reflect: v' = v − (1 + e)(v·n)n
        float vDotN = vel.X * nx + vel.Y * ny + vel.Z * nz;
        float scale  = (1f + RimRestitution) * vDotN;

        arc.Velocity = new Vector3(
            vel.X - scale * nx,
            vel.Y - scale * ny,
            vel.Z - scale * nz);

        // Depenetrate: push ball centre to exactly BallRadius from the ring point.
        arc.Position = new Vector3(
            nearestOnRing.X + nx * BallRadius,
            nearestOnRing.Y + ny * BallRadius,
            nearestOnRing.Z + nz * BallRadius);
    }

    /// <summary>
    /// Returns true when the ball centre is within BallRadius of the board
    /// plane AND within the board's rectangular bounds.
    ///
    /// Signed distance from ball centre P to the board plane:
    ///   d = (P − BoardCenter) · BoardNormal
    /// Contact when |d| &lt; BallRadius (ball overlaps the plane surface).
    ///
    /// Bounds check: project P onto the two board axes (horizontal tangent and
    /// vertical tangent to BoardNormal), compare with half-extents.
    /// </summary>
    private bool BackboardContact(Vector3 pos)
    {
        // Signed distance to board plane.
        float dx = pos.X - BoardCenter.X;
        float dy = pos.Y - BoardCenter.Y;
        float dz = pos.Z - BoardCenter.Z;

        float signedDist = dx * BoardNormal.X + dy * BoardNormal.Y + dz * BoardNormal.Z;

        // Only contact if within BallRadius of the plane surface.
        if (MathF.Abs(signedDist) >= BallRadius) return false;

        // ── Bounds check on the board rectangle ───────────────────────────
        // We need two tangent axes. Since the board is typically vertical
        // (normal is horizontal), we derive:
        //   up axis    = world up (0,1,0)  [for height bounds]
        //   right axis = BoardNormal × worldUp  [for width bounds]
        //
        // If the board normal is parallel to world up (a horizontal board —
        // unusual but possible), fall back to world forward (0,0,1).
        var worldUp = new Vector3(0f, 1f, 0f);
        Vector3 right = BoardNormal.Cross(worldUp);
        float rightLen = right.Length();

        if (rightLen < 1e-6f)
        {
            // Board is horizontal — use world forward as up-on-board axis.
            right   = BoardNormal.Cross(new Vector3(0f, 0f, 1f));
            rightLen = right.Length();
        }

        right /= rightLen; // normalise

        // Local coordinates of ball on the board face.
        float localX = dx * right.X   + dy * right.Y   + dz * right.Z;
        float localY = dx * worldUp.X + dy * worldUp.Y + dz * worldUp.Z;

        return MathF.Abs(localX) <= BoardHalfWidth &&
               MathF.Abs(localY) <= BoardHalfHeight;
    }

    /// <summary>
    /// Applies backboard bounce: reflects the velocity's component along
    /// BoardNormal (scaled by BoardRestitution), preserves the tangential
    /// components, then depenetrates the ball to the board surface.
    /// </summary>
    private void ApplyBackboardBounce(ShotArc arc, Vector3 pos, Vector3 vel)
    {
        // Signed distance to plane (positive = same side as normal points to).
        float dx = pos.X - BoardCenter.X;
        float dy = pos.Y - BoardCenter.Y;
        float dz = pos.Z - BoardCenter.Z;
        float signedDist = dx * BoardNormal.X + dy * BoardNormal.Y + dz * BoardNormal.Z;

        // Contact normal toward the ball (from board plane outward to ball side).
        // If ball is on the normal side, normal points toward ball = BoardNormal.
        // If ball is on the back side, flip it.
        float sign = signedDist >= 0f ? 1f : -1f;
        float nx = sign * BoardNormal.X;
        float ny = sign * BoardNormal.Y;
        float nz = sign * BoardNormal.Z;

        // Reflect: v' = v − (1 + e)(v·n)n
        float vDotN = vel.X * nx + vel.Y * ny + vel.Z * nz;
        float scale  = (1f + BoardRestitution) * vDotN;

        arc.Velocity = new Vector3(
            vel.X - scale * nx,
            vel.Y - scale * ny,
            vel.Z - scale * nz);

        // Depenetrate: place ball surface exactly on the board plane.
        // Position = board contact point + n * BallRadius.
        // Contact point = pos projected back onto board plane.
        float penetration = BallRadius - MathF.Abs(signedDist);
        arc.Position = new Vector3(
            pos.X + nx * penetration,
            pos.Y + ny * penetration,
            pos.Z + nz * penetration);
    }
}
