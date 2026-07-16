#nullable enable

namespace Hooper.Moves;

/// <summary>
/// Discriminates the outcome of a single RightStickGestureRecognizer.Sample() call.
///
/// StepBack/RetreatDribble (issue #197) are the VERTICAL (downward-only)
/// counterpart to the Crossover/Feint horizontal pair — same "quick
/// flick-and-release vs. flick-and-hold" grammar, disambiguated from the
/// horizontal pair by dominant-axis-wins (see RightStickGestureRecognizer's
/// doc). There is deliberately no vertical "Feint" kind: each vertical
/// gesture IS its own complete committed move (RetreatDribble = quick,
/// StepBack = held) rather than one move plus a separate abort signal.
/// </summary>
public enum GestureKind { None, Crossover, Feint, StepBack, RetreatDribble }

/// <summary>
/// Carries the result of one gesture-recognizer tick.
///
/// Kind == None          → no gesture completed this tick.
/// Kind == Crossover      → a committed crossover flick was confirmed (direction ±1).
/// Kind == Feint          → the player pushed past the threshold and pulled back quickly,
///                          signalling a fake-out (direction ±1 = direction of the initial flick).
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
/// The caller is responsible for mapping this to a CommittedMove or a Feint() call
/// on CommittedMoveMachine — the recognizer is pattern-only.
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
