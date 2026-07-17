#nullable enable

namespace Hooper.Moves;

/// <summary>
/// The layup / rim-finish — the first leaf of the M9 rim-finishing vertical
/// (issue #229, ADR-0022, epic #203). A distinct committed move from
/// <see cref="JumpShot"/>: its own Startup/Active/Recovery frame data and its
/// own release point (the tick it enters Active), but it feeds the SAME
/// distance/scatter make model (ADR-0009, <c>ShotScatter</c>) and is
/// contestable by the SAME block timing window (#98/#214,
/// <c>DefensiveResolution.Succeeds</c>) — no parallel accuracy formula, no
/// new defensive primitive, per ADR-0022's Decision.
///
/// The ball's actual release is NOT this class's responsibility, exactly like
/// JumpShot: it fires on the holder's JustEnteredActive tick, read by
/// BallController.CheckJumpShotRelease via PlayerController.
/// JustReleasedJumpShot (JumpShotReleaseResolver, extended by this issue to
/// recognize a Layup alongside a JumpShot) — this class only carries identity
/// + timing.
///
/// ── Why the layup has different numbers than JumpShot (real ball, ADR-0014
///    tier 2) ──────────────────────────────────────────────────────────────
/// A close-range finish is a quick one-or-two-step gather into the rim, not a
/// set shot's full wind-up — real half-court ball's own close-range shot
/// takes noticeably less time to get off than a jumper. The landing is also
/// lower-effort (the finish happens near the floor, not off a full jump
/// shot's descent), so the Recovery punish window is shorter too — but still
/// nonzero, preserving the punish window ADR-0003 requires.
///
/// Default frame data (tunable at construction if needed):
///   Startup:   8 ticks — quick gather/release wind-up (~0.13s at 60Hz),
///              deliberately shorter than JumpShot's 18: a layup is a
///              lower-commitment finish than a set shot, but still nonzero
///              so the telegraph exists (ADR-0003's anti-arcade-decoupling
///              floor — a zero-frame Startup would be invisible to the
///              defender).
///   Active:     4 ticks — the release window, same one-shot convention
///              JumpShot's burst already uses: the ball leaves the hand on
///              the FIRST of these ticks (JustEnteredActive).
///   Recovery:  14 ticks — shorter than JumpShot's 20-tick landing recovery
///              (a layup's finish is close to the floor, less descent to
///              recover from), but still a real punish window — a whiffed
///              drive-and-miss still costs something (#100's blow-by lane
///              applies here exactly as it does to a missed jump shot).
///   Feint: NONE (feintWindowFrames = 0 — structurally unfeintable, a design
///              constant like Hesitation's, not a placeholder). A pump-fake
///              variant of the layup is real-ball plausible but is explicitly
///              out of this issue's scope (#229: "no floater/dunk/
///              contact-finish variants" — a single tracer-bullet finish is
///              the deliverable; a feintable layup is a future decision, not
///              implied by this one, ADR-0014 tracer-bullet scope).
/// </summary>
public sealed class Layup : CommittedMove
{
    /// <summary>Default layup frame data. Tunable per instance if needed.</summary>
    public static readonly MoveFrameData DefaultFrameData =
        new(startupFrames: 8, activeFrames: 4, recoveryFrames: 14, feintWindowFrames: 0);

    /// <param name="frameData">Override frame data; null uses DefaultFrameData.</param>
    public Layup(MoveFrameData? frameData = null)
        : base(id: "layup", displayName: "Layup", frameData: frameData ?? DefaultFrameData)
    {
    }
}
