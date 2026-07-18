#nullable enable

namespace Hooper.Moves;

/// <summary>
/// The spin move — a full-body rotation that shields the ball from the
/// defender, with a hand swap at the END of the rotation (issue #201,
/// M9/#75). The last leaf of the dribble-move family; unlike every sibling
/// (Crossover/BehindTheBack/BetweenTheLegs/InAndOut), the ball does not
/// transit hand-to-hand mid-Active — the body rotates around it, and the
/// swap happens once the rotation completes.
///
/// ── Why "swap at the END", not Active-entry (contrast the whole family) ────
/// Every other family move fires its one-shot Active effect (burst + hand
/// swap where applicable) on <c>JustEnteredActive</c> — the FIRST Active
/// tick — because their ball motion (a lateral cross, a behind-the-back
/// pull, a through-the-legs bounce) is a single instantaneous transit that
/// completes in one conceptual beat. A spin's identity is the OPPOSITE: the
/// body physically rotates for the whole Active window, and only once that
/// rotation is complete does the ball end up controlled by the other hand —
/// swapping earlier would show the ball changing hands before the body has
/// finished turning, which is not what a real spin move looks like (ADR-0014
/// tier-1 real-ball fact). See PlayerController.TickCommittedMoveBehavior's
/// Spin branch for exactly which tick fires the swap
/// (<c>FrameInPhase == ActiveFrames - 1</c>, the LAST Active tick).
///
/// ── The scripted heading arc (ADR-0010 sanctioned exception) ───────────────
/// A spin needs the body to rotate ~180° over the Active window — faster,
/// and structurally different, than ADR-0010's bounded non-linear turn rate
/// (which governs ordinary stick-driven turning via HeadingMath.RotateToward
/// inside Move()). This is a SANCTIONED, on-the-record exception, not a
/// silent bypass — see the ADR-0010 amendment this issue's heading-arc commit
/// carries, and <see cref="Hooper.Player.SpinHeadingMath"/> for the pure,
/// deterministic function that computes it every tick.
///
/// ── Ball path: body-shield sweep (a #195 sweep-geometry variant) ───────────
/// A fourth <see cref="Hooper.Ball.BallSweepPath"/> option alongside
/// Crossover's in-front, BehindTheBack's behind-body, and BetweenTheLegs'
/// through-the-legs paths — see <see cref="Hooper.Ball.CrossoverBallSweep"/>'s
/// <c>BodyShield</c> case.
///
/// ── Exit composition reuses #198's CrossoverBurstMath (post-#209) ──────────
/// Composed against the ENTRY heading/exit-vector captured ONCE at
/// JustEnteredActive — NOT the heading the Active branch is actively
/// rotating — so the exit burst continues the ORIGINAL drive line, only the
/// player's authoritative facing has spun around. See
/// PlayerController.TickCommittedMoveBehavior's Spin branch doc for the full
/// reasoning (including the doubt-driven-development finding that fixed an
/// earlier draft which read the exit vector live on the LAST tick instead of
/// locking it in at Active-entry like every sibling move).
///
/// ── Largest pre-move exposure in the taxonomy (per the issue spec) ─────────
/// Near-zero steal window DURING the spin itself (the ball is shielded by the
/// rotating body), but the ball is extended out on the drive BEFORE the spin
/// even starts — that pre-move exposure, not the spin itself, is what a
/// defender should be punishing. This is a design/feel property of the whole
/// system (Startup telegraph + pre-existing steal windows), not a mechanism
/// this move needs to implement itself.
///
/// Default frame data (placeholder, tunable at construction if needed):
///   Startup:  8 ticks  — plant + shoulder-dip tell. Longer than Crossover's
///             6: a full-body rotation is a bigger commitment than a lateral
///             hand swap, so its telegraph is correspondingly longer
///             (ADR-0014 tier-2: a real spin's wind-up is visibly larger than
///             a crossover's).
///   Active:   6 ticks  — the rotation itself. Longer than Crossover's 3:
///             a ~180° body turn takes measurably longer than a lateral
///             hand-to-hand transit.
///   Recovery: 10 ticks — matches BehindTheBack's Recovery; the spin is a
///             genuine separation move, not a safer/lesser variant, so its
///             punish window is not inflated beyond the family's existing
///             range.
///   Feint:    0 ticks  — a design constant, not a placeholder: mirrors the
///             whole dribble-move family's #202 closure (a fake of a dribble
///             move is not a real basketball action; see Crossover's own
///             class doc for the full ADR-0003 amendment reasoning this
///             move inherits rather than re-litigates).
/// </summary>
public sealed class Spin : CommittedMove
{
    /// <summary>Default spin frame data. Tunable per instance if needed.</summary>
    public static readonly MoveFrameData DefaultFrameData =
        new(startupFrames: 8, activeFrames: 6, recoveryFrames: 10, feintWindowFrames: 0);

    /// <summary>
    /// Direction of the rotation: +1 = spin toward the player's right
    /// (clockwise viewed from above), -1 = toward the left (counter-
    /// clockwise). Same body-relative sign convention as Crossover.
    /// BurstDirection, so RequestBeginMove's wire payload and the family's
    /// existing dispatch plumbing stay uniform — but here it drives a
    /// rotation direction, not a lateral push, so it is named distinctly.
    /// </summary>
    public float SpinDirection { get; }

    /// <param name="spinDirection">+1 = spin right (clockwise), -1 = spin left (counter-clockwise).</param>
    /// <param name="frameData">Override frame data; null uses DefaultFrameData.</param>
    public Spin(float spinDirection, MoveFrameData? frameData = null)
        : base(id: "spin", displayName: "Spin", frameData: frameData ?? DefaultFrameData)
    {
        SpinDirection = spinDirection;
    }
}
