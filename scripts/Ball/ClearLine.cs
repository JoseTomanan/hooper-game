using Godot;

namespace Hooper.Ball;

/// <summary>
/// Pure geometry for the take-it-back / "clear" rule (ADR-0008, issue #50): has
/// the handler carried the ball back behind the clear line? No Godot Node, no
/// engine singletons — the same headless test seam as ReboundContest / ShotArc
/// (ADR-0004).
///
/// ── Why a radial line ────────────────────────────────────────────────────
/// "The clear line near the top of the key" is modelled as a horizontal radius
/// around the hoop rather than an axis-aligned plane: the half-court key arc is
/// itself a radius from the basket, and a radial test is orientation-agnostic
/// (it does not assume which way "out toward half-court" points in world
/// space), so the same export works regardless of how the court is laid out in
/// the scene. Height is ignored — only the floor-plane distance from the hoop
/// matters, so a shot's arc height never accidentally satisfies the clear.
/// </summary>
public static class ClearLine
{
    /// <summary>
    /// True once the handler is at least <paramref name="clearLineDistance"/>
    /// metres (measured on the floor plane, ignoring height) from the hoop —
    /// i.e. they have taken the ball back behind the clear line and a basket
    /// may now count.
    /// </summary>
    /// <param name="handlerPosition">Current world position of the ball handler.</param>
    /// <param name="hoopCenter">World-space centre of the rim.</param>
    /// <param name="clearLineDistance">Floor-plane radius from the hoop that defines the clear line.</param>
    public static bool IsBehindClearLine(Vector3 handlerPosition, Vector3 hoopCenter, float clearLineDistance)
    {
        // Floor-plane (X/Z) separation only — height is irrelevant to clearing.
        // Compare squared distances to avoid a sqrt; identical math on every
        // peer, though only the server ever evaluates this (clients receive the
        // resulting cleared flag, never compute it — see BallController).
        float dx = handlerPosition.X - hoopCenter.X;
        float dz = handlerPosition.Z - hoopCenter.Z;
        float horizontalDistSq = dx * dx + dz * dz;

        return horizontalDistSq >= clearLineDistance * clearLineDistance;
    }
}
