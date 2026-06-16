#nullable enable

namespace Hooper.Moves;

/// <summary>
/// Discriminates the outcome of a single RightStickGestureRecognizer.Sample() call.
/// </summary>
public enum GestureKind { None, Crossover, Feint }

/// <summary>
/// Carries the result of one gesture-recognizer tick.
///
/// Kind == None    → no gesture completed this tick.
/// Kind == Crossover → a committed crossover flick was confirmed (direction ±1).
/// Kind == Feint   → the player pushed past the threshold and pulled back quickly,
///                   signalling a fake-out (direction ±1 = direction of the initial flick).
///
/// Direction is 0 when Kind == None; otherwise +1 (right) or -1 (left).
/// The caller is responsible for mapping this to a CommittedMove or a Feint() call
/// on CommittedMoveMachine — the recognizer is pattern-only.
/// </summary>
public readonly struct GestureResult
{
    public GestureKind Kind      { get; }
    public float       Direction { get; } // +1 right, -1 left; 0 if Kind == None

    /// <summary>Sentinel for "nothing happened this tick."</summary>
    public static readonly GestureResult None = new(GestureKind.None, 0f);

    public GestureResult(GestureKind kind, float direction)
    {
        Kind      = kind;
        Direction = direction;
    }
}
