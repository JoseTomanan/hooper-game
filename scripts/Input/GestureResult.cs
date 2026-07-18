#nullable enable

namespace Hooper.Moves;

/// <summary>
/// Discriminates the outcome of a single RightStickGestureRecognizer.Sample() call.
///
/// StepBack/RetreatDribble (issue #197) are the VERTICAL (downward-only)
/// counterpart to the Crossover/QuickReturn horizontal pair — same "quick
/// flick-and-release vs. flick-and-hold" grammar, disambiguated from the
/// horizontal pair by dominant-axis-wins (see RightStickGestureRecognizer's
/// doc). There is deliberately no vertical "QuickReturn" kind: each vertical
/// gesture IS its own complete committed move (RetreatDribble = quick,
/// StepBack = held) rather than one move plus a separate disambiguation.
///
/// ── Rename history (issue #202) ──────────────────────────────────────────
/// This kind was originally named "Feint": every quick flick-and-return used
/// to abort whatever move was Startup-ing (CommittedMoveMachine.Feint()).
/// #202 retargets it — the quick-return horizontal gesture now BEGINS a move
/// of its own (InAndOut when the flick is toward the empty hand, Hesitation
/// when toward the ball hand — see HandStateResolver.IsCrossover, which the
/// caller now consults for THIS kind exactly as it already does for the held
/// Crossover kind) rather than cancelling one. "QuickReturn" names what the
/// STICK did, leaving "which move begins" to the caller's hand-state read —
/// the same split the "Crossover" kind name already models (a completed hold
/// gesture, disambiguated into Crossover-or-Hesitation by the caller).
/// </summary>
public enum GestureKind { None, Crossover, QuickReturn, StepBack, RetreatDribble }

/// <summary>
/// Carries the result of one gesture-recognizer tick.
///
/// Kind == None          → no gesture completed this tick.
/// Kind == Crossover      → a completed HOLD gesture (direction ±1) — the
///                          caller disambiguates Crossover vs Hesitation via
///                          HandStateResolver.IsCrossover.
/// Kind == QuickReturn    → the player pushed past the threshold and pulled
///                          back quickly (direction ±1 = direction of the
///                          initial flick) — the caller disambiguates
///                          InAndOut vs Hesitation via the SAME
///                          HandStateResolver.IsCrossover call (issue #202).
/// Kind == StepBack       → a vertical (downward) flick was held past the feint
///                          window — the step-back's "hold" commitment (#197).
/// Kind == RetreatDribble → a vertical (downward) flick quick-returned to the
///                          deadzone within the window — the retreat dribble's
///                          "quick" commitment (#197).
///
/// Direction is 0 when Kind == None, StepBack, or RetreatDribble — the
/// vertical pair carries no left/right payload (there is only one downward
/// direction to recognize); otherwise +1 (right) or -1 (left) for the
/// horizontal pair.
/// The caller is responsible for mapping this to a CommittedMove — the
/// recognizer is pattern-only.
/// </summary>
public readonly struct GestureResult
{
    public GestureKind Kind      { get; }
    public float       Direction { get; } // +1 right, -1 left; 0 if Kind == None, StepBack, or RetreatDribble

    /// <summary>Sentinel for "nothing happened this tick."</summary>
    public static readonly GestureResult None = new(GestureKind.None, 0f);

    public GestureResult(GestureKind kind, float direction)
    {
        Kind      = kind;
        Direction = direction;
    }
}
