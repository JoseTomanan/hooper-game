#nullable enable

namespace Hooper.Moves;

/// <summary>
/// The hesitation — the counter-move to the crossover in the M9 right-stick
/// mind-game (ADR-0003, issue #86).
///
/// In basketball, a hesitation dribble (also called a stutter step) is a
/// freeze move: the ball-handler pauses their dribble rhythm to bait the
/// defender into a reactive lean or a premature weight shift, then exploits
/// the gap once the defender has committed. It is the honest feint — unlike
/// the crossover's feint-window abort (which is a recalled crossover), the
/// hesitation is a first-class committed move that runs to completion; it
/// simply has no directional payload.
///
/// Key differences from <see cref="Crossover"/>:
///   - No burst direction: a hesitation applies NO lateral velocity impulse.
///     The "go" is the left-stick drive that the player chooses AFTER the
///     move resolves — the integration layer, not this class, handles that.
///   - No ball swap: the ball stays in the same hand throughout.
///   - feintWindowFrames: 0 — a hesitation CANNOT be feinted. It is its own
///     honest commitment; there is no recall. This is deliberate — see ADR-0003:
///     the absence of a cancel IS the mind game, and a recall-able hesitation
///     would collapse into a free-aim tool (the primary anti-goal).
///
/// Default frame data (placeholder — the human tunes the freeze feel in-editor):
///   Startup:  4 ticks — visible wind-up; the body loads but hasn't stutter-stepped yet
///   Active:   8 ticks — the visible freeze/stutter window; this is where the
///                       defender reads a crossover and commits
///   Recovery: 6 ticks — returning to dribble rhythm; punish window if the defender
///                       read the hesi correctly and doesn't bite
///   Feint:    0 ticks — no feint; the commitment is total
/// </summary>
public sealed class Hesitation : CommittedMove
{
    /// <summary>
    /// Default hesitation frame data.
    ///
    /// These values are intentionally placeholder — the exact freeze duration
    /// that makes the stutter readable without being abusable is a feel judgment
    /// the human must calibrate in-editor (see EDITOR_TASKS.md). The
    /// FeintWindowFrames = 0 is NOT a placeholder; that is a design constant
    /// (see class doc above).
    /// </summary>
    public static readonly MoveFrameData DefaultFrameData =
        new(startupFrames: 4, activeFrames: 8, recoveryFrames: 6, feintWindowFrames: 0);

    /// <param name="frameData">
    /// Override frame data for tuning. Null uses <see cref="DefaultFrameData"/>.
    /// </param>
    public Hesitation(MoveFrameData? frameData = null)
        : base(id: "hesitation", displayName: "Hesitation", frameData: frameData ?? DefaultFrameData)
    {
    }
}
