#nullable enable

using Godot;

namespace Hooper.Moves;

/// <summary>
/// Recognizes discrete right-stick gestures (Pro Stick style, ADR-0003) and
/// maps them to GestureResult values.
///
/// Fed one Vector2 sample per physics tick (right-stick axis values, range
/// [-1, 1]). Recognizes two axis-pairs, each sharing the SAME quick-vs-hold
/// grammar:
///
///   Horizontal — Crossover (held past FeintWindowTicks) / Feint (quick
///                flick-and-return).
///   Vertical, downward only (issue #197) — StepBack (held past
///                FeintWindowTicks) / RetreatDribble (quick flick-and-return).
///
/// In all four cases the recognizer fires exactly once per gesture and then
/// waits for the stick to return to the deadzone before it can fire again
/// (debounce).
///
/// ── Why pure? ────────────────────────────────────────────────────────────────
/// The recognizer takes no Godot singleton dependency (no Input.GetVector
/// calls). It is fed samples by the node (PlayerController) so it can be
/// instantiated and tested headlessly. The node is responsible for reading
/// the hardware; this class is responsible for pattern recognition only.
///
/// ── Timing model ─────────────────────────────────────────────────────────────
/// On the first tick the stick crosses FlickThreshold on EITHER axis we start
/// counting, having first picked which axis this gesture belongs to (see
/// "Axis disambiguation" below). The gesture kind is then decided when:
///   (a) the stick returns to the deadzone within FeintWindowTicks ticks →
///       Feint (horizontal) / RetreatDribble (vertical).
///   (b) the stick stays above threshold past FeintWindowTicks ticks →
///       Crossover (horizontal) / StepBack (vertical).
/// The caller (#17 integration, #197 for the vertical pair) maps the
/// GestureResult to a CommittedMove or a Feint() call on CommittedMoveMachine.
///
/// ── Axis disambiguation (issue #197) ─────────────────────────────────────────
/// A diagonal flick is ambiguous between the horizontal and vertical pairs.
/// The axis is decided ONCE, on the tick the gesture STARTS (first tick either
/// axis clears FlickThreshold), and stays locked for that gesture's whole
/// lifetime — later ticks only re-check whichever axis was picked, never
/// re-arbitrate mid-gesture. This must be deterministic and reproducible
/// server-side and client-side from the same input sequence (same concern
/// class as #198's exit-vector snapshot), so the rule is dominant-axis-wins
/// with a small hysteresis band (<see cref="AxisHysteresis"/>) rather than a
/// bare magnitude comparison: when both axes clear the threshold within the
/// hysteresis band of each other (a near-45° flick), the tie is broken in
/// favor of Horizontal — an arbitrary but FIXED choice, so the same input
/// always resolves the same way on every machine that runs this code, rather
/// than depending on any floating-point tie that could vary by build/CPU.
/// Only DOWNWARD vertical motion is recognized (stick.Y > 0, this project's
/// GetVector convention — see aim_down's InputMap binding); upward aim-stick
/// motion never starts a vertical gesture.
/// </summary>
public sealed class RightStickGestureRecognizer
{
    // ── Configuration ─────────────────────────────────────────────────────────

    /// <summary>
    /// Magnitude either axis must exceed to register as a gesture start —
    /// shared by both the horizontal (crossover/feint) and vertical
    /// (step-back/retreat-dribble, #197) pairs, so neither axis is
    /// systematically easier to trigger than the other. Default 0.6 leaves
    /// headroom above the usual 0.2 movement deadzone.
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

    /// <summary>
    /// Hysteresis band (issue #197) applied when BOTH axes clear
    /// FlickThreshold on the same tick a gesture starts: the axis whose
    /// magnitude exceeds the other's by more than this band wins outright; a
    /// closer call (a near-45° flick) is a fixed, deterministic tie-break to
    /// Horizontal rather than a per-tick floating-point coin flip. See the
    /// class doc's "Axis disambiguation" section.
    /// </summary>
    public float AxisHysteresis { get; }

    // ── State ─────────────────────────────────────────────────────────────────

    private enum GestureAxis { Horizontal, Vertical }

    private bool        _gesturePending;      // tracking in-flight timing (not yet committed)
    private bool        _gestureFired;        // waiting for stick to return to deadzone after fire
    private int         _ticksAboveThreshold; // how many consecutive ticks past FlickThreshold
    private float       _flickDir;            // horizontal direction captured on first above-threshold tick (0 for a vertical gesture)
    private GestureAxis _pendingAxis;         // axis locked in for the CURRENT gesture (decided once, at gesture start)

    // ── Constructor ───────────────────────────────────────────────────────────

    /// <param name="flickThreshold">Magnitude either axis must exceed to register a flick. Default 0.6.</param>
    /// <param name="deadzoneRadius">Return-to-centre radius. Default 0.2.</param>
    /// <param name="feintWindowTicks">Ticks within which a quick-return commits the "quick" gesture (Feint / RetreatDribble). Default 4.</param>
    /// <param name="axisHysteresis">Tie-break band for simultaneous-axis flicks (#197). Default 0.1.</param>
    public RightStickGestureRecognizer(
        float flickThreshold   = 0.6f,
        float deadzoneRadius   = 0.2f,
        int   feintWindowTicks = 4,
        float axisHysteresis   = 0.1f)
    {
        FlickThreshold   = flickThreshold;
        DeadzoneRadius   = deadzoneRadius;
        FeintWindowTicks = feintWindowTicks;
        AxisHysteresis   = axisHysteresis;
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
        float horizontal   = stick.X;
        float horizontalMag = System.MathF.Abs(horizontal);
        // Downward only (this project's GetVector convention: aim_down is the
        // POSITIVE Y binding — see the InputMap and the class doc's axis note).
        // An upward push never contributes to gesture recognition.
        float verticalMag  = System.MathF.Max(stick.Y, 0f);
        bool  inDeadzone   = stick.Length() < DeadzoneRadius;

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
            bool horizontalAbove = horizontalMag >= FlickThreshold;
            bool verticalAbove   = verticalMag   >= FlickThreshold;
            if (!horizontalAbove && !verticalAbove) return GestureResult.None;

            // Axis disambiguation (#197): decided ONCE, here, for the whole
            // gesture — see class doc's "Axis disambiguation" section for why
            // this is dominant-axis-wins with a fixed hysteresis-band tie-break
            // rather than a per-tick re-arbitration.
            GestureAxis axis;
            if (horizontalAbove && verticalAbove)
            {
                if (horizontalMag > verticalMag + AxisHysteresis)      axis = GestureAxis.Horizontal;
                else if (verticalMag > horizontalMag + AxisHysteresis) axis = GestureAxis.Vertical;
                else                                                  axis = GestureAxis.Horizontal; // fixed tie-break
            }
            else
            {
                axis = horizontalAbove ? GestureAxis.Horizontal : GestureAxis.Vertical;
            }

            _pendingAxis = axis;
            // Only the horizontal pair carries a direction payload (#197 —
            // the vertical pair has only one recognized direction: down).
            _flickDir            = axis == GestureAxis.Horizontal ? (horizontal > 0f ? 1f : -1f) : 0f;
            _gesturePending      = true;
            _ticksAboveThreshold = 1;
            return GestureResult.None; // not committed yet
        }

        // ── Gesture is pending (timing in progress) ───────────────────────────
        // Re-check ONLY the axis locked in at gesture start — a gesture never
        // switches axis mid-flight even if the other axis also clears
        // threshold later (see class doc).
        bool stillAboveThreshold = _pendingAxis == GestureAxis.Horizontal
            ? horizontalMag >= FlickThreshold
            : verticalMag   >= FlickThreshold;

        if (stillAboveThreshold)
        {
            _ticksAboveThreshold++;

            // Window closed: commit as the "hold" gesture for this axis.
            if (_ticksAboveThreshold > FeintWindowTicks)
            {
                _gestureFired = true;
                return _pendingAxis == GestureAxis.Horizontal
                    ? new GestureResult(GestureKind.Crossover, _flickDir)
                    : new GestureResult(GestureKind.StepBack, 0f);
            }

            // Still inside window — not committed yet.
            return GestureResult.None;
        }

        // Stick left the threshold area (on the locked axis).
        if (inDeadzone && _ticksAboveThreshold <= FeintWindowTicks)
        {
            // Returned to deadzone within the window: it's the "quick" gesture.
            float dir     = _flickDir;
            _gestureFired = true;
            return _pendingAxis == GestureAxis.Horizontal
                ? new GestureResult(GestureKind.Feint, dir)
                : new GestureResult(GestureKind.RetreatDribble, 0f);
        }

        // Stick in mid-zone (between deadzone and threshold) — keep waiting.
        // Also handles the unlikely case where window is closed and stick left
        // threshold without reaching deadzone: reset silently.
        if (!inDeadzone && !stillAboveThreshold)
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
