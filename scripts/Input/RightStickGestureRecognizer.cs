#nullable enable

using Godot;

namespace Hooper.Moves;

/// <summary>
/// Recognizes discrete right-stick gestures (Pro Stick style, ADR-0003) and
/// maps them to GestureResult values.
///
/// Fed one Vector2 sample per physics tick (right-stick axis values, range
/// [-1, 1]). Recognizes two horizontal gestures:
///
///   Crossover — stick pushed past FlickThreshold and held for more than
///               FeintWindowTicks consecutive ticks.
///   Feint     — stick pushed past FlickThreshold then returned to the deadzone
///               within FeintWindowTicks ticks (a quick fake-out motion).
///
/// In both cases the recognizer fires exactly once per gesture and then waits
/// for the stick to return to the deadzone before it can fire again (debounce).
///
/// ── Why pure? ────────────────────────────────────────────────────────────────
/// The recognizer takes no Godot singleton dependency (no Input.GetVector
/// calls). It is fed samples by the node (PlayerController) so it can be
/// instantiated and tested headlessly. The node is responsible for reading
/// the hardware; this class is responsible for pattern recognition only.
///
/// ── Timing model ─────────────────────────────────────────────────────────────
/// On the first tick the stick crosses FlickThreshold we start counting.
/// The gesture kind is decided when:
///   (a) the stick returns to the deadzone within FeintWindowTicks ticks → Feint.
///   (b) the stick stays above threshold past FeintWindowTicks ticks → Crossover.
/// The caller (#17 integration) maps the GestureResult to a CommittedMove
/// or a Feint() call on CommittedMoveMachine.
/// </summary>
public sealed class RightStickGestureRecognizer
{
    // ── Configuration ─────────────────────────────────────────────────────────

    /// <summary>
    /// Horizontal magnitude the stick must exceed to register as a gesture
    /// start. Ignores vertical component — crossover is a pure lateral gesture.
    /// Default 0.6 leaves headroom above the usual 0.2 movement deadzone.
    /// </summary>
    public float FlickThreshold { get; }

    /// <summary>
    /// Magnitude below which the stick is considered "returned to centre"
    /// after a flick, re-arming the recognizer for the next gesture.
    /// </summary>
    public float DeadzoneRadius { get; }

    /// <summary>
    /// Number of ticks the stick may stay past the threshold before the
    /// recognizer commits the gesture as a Crossover rather than a Feint.
    ///
    /// At FeintWindowTicks the window is still open; it closes at
    /// FeintWindowTicks+1 (i.e. the crossover fires when
    /// ticksAboveThreshold > FeintWindowTicks). Default 4.
    /// </summary>
    public int FeintWindowTicks { get; }

    // ── State ─────────────────────────────────────────────────────────────────

    private bool  _gesturePending;      // tracking in-flight timing (not yet committed)
    private bool  _gestureFired;        // waiting for stick to return to deadzone after fire
    private int   _ticksAboveThreshold; // how many consecutive ticks past FlickThreshold
    private float _flickDir;            // direction captured on first above-threshold tick

    // ── Constructor ───────────────────────────────────────────────────────────

    /// <param name="flickThreshold">Horizontal magnitude to register a flick. Default 0.6.</param>
    /// <param name="deadzoneRadius">Return-to-centre radius. Default 0.2.</param>
    /// <param name="feintWindowTicks">Ticks within which a quick-return is a feint. Default 4.</param>
    public RightStickGestureRecognizer(
        float flickThreshold   = 0.6f,
        float deadzoneRadius   = 0.2f,
        int   feintWindowTicks = 4)
    {
        FlickThreshold   = flickThreshold;
        DeadzoneRadius   = deadzoneRadius;
        FeintWindowTicks = feintWindowTicks;
    }

    // ── Per-tick sample ───────────────────────────────────────────────────────

    /// <summary>
    /// Feed one right-stick sample. Returns a GestureResult describing what
    /// (if anything) completed this tick.
    ///
    /// Call once per physics tick immediately before checking the result.
    /// Each tick either fires a gesture or returns GestureResult.None.
    /// </summary>
    /// <param name="stick">Right-stick axes, range [-1, 1] per axis.</param>
    public GestureResult Sample(Vector2 stick)
    {
        float horizontal     = stick.X;
        bool  aboveThreshold = System.MathF.Abs(horizontal) >= FlickThreshold;
        bool  inDeadzone     = stick.Length() < DeadzoneRadius;

        // ── Gesture already fired: wait for deadzone re-arm ───────────────────
        if (_gestureFired)
        {
            if (inDeadzone)
            {
                _gestureFired        = false;
                _gesturePending      = false;
                _ticksAboveThreshold = 0;
            }
            return GestureResult.None;
        }

        // ── No pending gesture: look for a new flick start ────────────────────
        if (!_gesturePending)
        {
            if (!aboveThreshold) return GestureResult.None;

            // First tick above threshold: capture direction and start timing.
            _flickDir             = horizontal > 0f ? 1f : -1f;
            _gesturePending       = true;
            _ticksAboveThreshold  = 1;
            return GestureResult.None; // not committed yet
        }

        // ── Gesture is pending (timing in progress) ───────────────────────────

        if (aboveThreshold)
        {
            _ticksAboveThreshold++;

            // Window closed: commit as crossover.
            if (_ticksAboveThreshold > FeintWindowTicks)
            {
                _gestureFired = true;
                return new GestureResult(GestureKind.Crossover, _flickDir);
            }

            // Still inside window — not committed yet.
            return GestureResult.None;
        }

        // Stick left the threshold area.
        if (inDeadzone && _ticksAboveThreshold <= FeintWindowTicks)
        {
            // Returned to deadzone within the window: it's a feint.
            float dir         = _flickDir;
            _gestureFired     = true;
            return new GestureResult(GestureKind.Feint, dir);
        }

        // Stick in mid-zone (between deadzone and threshold) — keep waiting.
        // Also handles the unlikely case where window is closed and stick left
        // threshold without reaching deadzone: reset silently.
        if (!inDeadzone && !aboveThreshold)
        {
            // Mid-zone: wait for stick to either re-cross threshold or reach deadzone.
            return GestureResult.None;
        }

        // Fell through (stick reached deadzone but window was already closed).
        _gesturePending      = false;
        _ticksAboveThreshold = 0;
        return GestureResult.None;
    }
}
