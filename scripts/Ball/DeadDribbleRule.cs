namespace Hooper.Ball;

/// <summary>
/// Pure decision for the dead-dribble rule (#193, ADR-0008 dead-dribble
/// amendment): once a possession's live dribble has been cradled (picked up
/// into a shot or pump-fake gather), the ball may not be dribbled again for
/// the REST of that possession. This is the real half-court "you already
/// picked it up" rule, deliberately WITHOUT the violations that would
/// normally accompany it — travel, 5-second closely-guarded — which are out
/// of scope for #193 (bare-minimum realism; don't build enforcement nobody
/// asked for yet).
///
/// Extracted as its own pure static method — rather than an inline `if` at
/// the BallController call site — purely so this one rule has a unit-testable
/// seam independent of BallController's Node-based state, mirroring
/// OobResolution/ClearLine's role as the tiny pure decision behind a stateful
/// glue method (ADR-0004's headless-test seam, generalized beyond the ball's
/// physics to its rules layer).
/// </summary>
public static class DeadDribbleRule
{
    /// <param name="hasDribbled">
    /// True if the CURRENT possession's dribble has already been cradled
    /// (BallController.HasDribbled) — reset on every possession change and
    /// on score (ADR-0008's possession events), set true the instant the
    /// holder begins a JumpShot (which covers the pump-fake too — a feint is
    /// a Startup-phase abort of the SAME Begin(), not a separate one).
    /// </param>
    /// <returns>True if StartDribble() may legally be attempted.</returns>
    public static bool CanStartDribble(bool hasDribbled) => !hasDribbled;
}
