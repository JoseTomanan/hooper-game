using System;
using Godot;

namespace Hooper.Player;

/// <summary>
/// Pure C# resolver for the M9 crossover/hesi mind-game disambiguation —
/// no Godot Node inheritance, no engine singletons, no _PhysicsProcess, no RPCs.
///
/// One right-stick flick produces two possible moves, disambiguated by which
/// hand currently holds the ball and which way the flick travels:
///
///   - Flick TOWARD the EMPTY hand → crossover (ball swaps to that hand +
///     a lateral burst in that direction).
///   - Flick TOWARD the BALL hand  → hesitation (freeze/stutter feint;
///     no swap, no burst — the body commits to looking like a crossover
///     but withholds the weight transfer).
///
/// Hand-side is BODY-RELATIVE throughout — the player's left hand is
/// their left hand regardless of which way they face the camera. Flick
/// signs from RightStickGestureRecognizer are likewise body-relative
/// (ADR-0003: no free-aim, committed moves are relative to the body).
///
/// World-space burst direction is derived from the player's authoritative
/// heading (ADR-0010) so the burst follows the body as it turns, not a
/// fixed screen axis.
/// </summary>
public static class HandStateResolver
{
    /// <summary>
    /// Returns the OPPOSITE hand — the hand the ball moves TO in a crossover.
    ///
    /// This is the pure ball-swap: Left→Right means the ball was in the left
    /// hand and is now carried in the right. Used by the crossover integration
    /// layer to update the authoritative ball-hand state.
    /// </summary>
    /// <param name="hand">The hand currently holding the ball.</param>
    /// <returns>The other hand.</returns>
    public static HandSide Opposite(HandSide hand) => hand == HandSide.Left ? HandSide.Right : HandSide.Left;

    /// <summary>
    /// Returns the body-relative sign of the player's EMPTY hand.
    ///
    /// Sign convention matches RightStickGestureRecognizer's flick sign:
    ///   +1 = the player's RIGHT side
    ///   -1 = the player's LEFT side
    ///
    /// If the ball is in the LEFT hand, the empty hand is on the RIGHT → +1.
    /// If the ball is in the RIGHT hand, the empty hand is on the LEFT → -1.
    ///
    /// This is the reference sign used by <see cref="IsCrossover"/> to
    /// decide whether the flick is moving TOWARD the empty hand.
    /// </summary>
    /// <param name="hand">The hand currently holding the ball.</param>
    /// <returns>+1 if the empty hand is on the right, -1 if on the left.</returns>
    public static int EmptyHandSign(HandSide hand) => hand == HandSide.Left ? +1 : -1;

    /// <summary>
    /// Classifies a right-stick flick as a crossover (true) or a hesitation (false).
    ///
    /// A crossover is triggered when the flick travels TOWARD the empty hand —
    /// i.e. the sign of the flick matches the body-relative sign of the empty
    /// hand. A hesitation is triggered when the flick travels TOWARD the ball
    /// hand — the player loads weight as if crossing over but withholds the swap.
    ///
    /// Truth table (body-relative):
    ///   (Left,  +1) = true   [flick right, empty hand right → crossover]
    ///   (Left,  -1) = false  [flick left,  ball hand left   → hesitation]
    ///   (Left,   0) = false  [no gesture]
    ///   (Right, -1) = true   [flick left,  empty hand left  → crossover]
    ///   (Right, +1) = false  [flick right, ball hand right  → hesitation]
    ///   (Right,  0) = false  [no gesture]
    ///
    /// Any nonzero magnitude flick is accepted — <see cref="Math.Sign"/> is
    /// used so a raw stick reading of +5 or −3 behaves the same as ±1.
    /// </summary>
    /// <param name="hand">The hand currently holding the ball.</param>
    /// <param name="flickSign">
    /// Body-relative flick direction from the gesture recognizer.
    /// Positive = player's right, negative = player's left, zero = no gesture.
    /// Any magnitude is accepted; only the sign is compared.
    /// </param>
    /// <returns>
    /// True if the flick is a crossover; false if hesitation or no gesture.
    /// </returns>
    public static bool IsCrossover(HandSide hand, int flickSign) =>
        flickSign != 0 && Math.Sign(flickSign) == EmptyHandSign(hand);

    /// <summary>
    /// Maps a body-relative flick sign and the player's authoritative heading
    /// to a unit world-space burst direction on the XZ plane.
    ///
    /// The player's right-hand direction in world space at heading <c>h</c> is
    /// <c>(cos h, −sin h)</c> (XZ components). A +1 flick (body-relative right)
    /// therefore bursts along that vector; a −1 flick bursts along its negation.
    /// Formula: <c>flickSign * (cos h, −sin h)</c>.
    ///
    /// Rationale: the player's forward at heading h is <c>(sin h, cos h)</c>
    /// on XZ (Atan2(x,z) convention, yaw 0 faces +Z — the same convention as
    /// HeadingMath and FacingResolver). The right vector is the 90°-clockwise
    /// orthogonal: <c>(cos h, −sin h)</c>. Multiplying by the flick sign means
    /// the burst follows the body as it turns — left and right are always
    /// body-relative, not screen-relative (ADR-0003 no-free-aim).
    ///
    /// NOTE: the exact sign of the world-space output is hitl visual sign-off —
    /// the human must verify in-editor that a +1 flick (right stick pushed to
    /// the player's right) produces a burst that reads as "rightward" given the
    /// game camera angle. The formula is internally consistent; the axis
    /// orientation is a tuning judgment.
    /// </summary>
    /// <param name="heading">
    /// Player's authoritative heading in radians, Y-rotation, Godot convention.
    /// Yaw 0 faces +Z; Atan2(x, z) convention matches HeadingMath/FacingResolver.
    /// </param>
    /// <param name="flickSign">
    /// Body-relative flick direction: +1 = player's right, −1 = player's left.
    /// A zero flick returns the zero vector (caller should guard against this).
    /// </param>
    /// <returns>
    /// A unit <see cref="Vector2"/> (worldX, worldZ) representing the burst
    /// direction in world space, or <see cref="Vector2.Zero"/> when
    /// <paramref name="flickSign"/> is 0.
    /// </returns>
    public static Vector2 BurstWorldDir(float heading, int flickSign) =>
        flickSign * new Vector2(MathF.Cos(heading), -MathF.Sin(heading));
}
