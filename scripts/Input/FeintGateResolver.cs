#nullable enable

namespace Hooper.Moves;

/// <summary>
/// Pure "should this tick's feint input actually call
/// CommittedMoveMachine.Feint()" decision — extracted so the composition
/// PlayerController.SampleMoveInput already had (explicit "move_feint" press
/// OR RightStickGestureRecognizer reporting GestureKind.QuickReturn — renamed
/// from "GestureKind.Feint", issue #202) is unit-testable without a running
/// Godot instance, exactly like JumpShotReleaseResolver.
///
/// ── Bug fix (/diagnose, 2026-07-03) ──────────────────────────────────────────
/// The shared right-stick "Pro Stick" gesture recognizes a Feint from ANY
/// quick flick-and-return of the aim stick (RightStickGestureRecognizer),
/// regardless of which committed move is currently running or why the stick
/// moved. For Crossover/Hesitation that ambiguity is harmless — a feint just
/// aborts them for free (FeintRecoveryFrames == 0). For JumpShot it is not:
/// JumpShot is the one move whose feint (pump-fake, #77) SILENTLY consumes the
/// shot — CommittedMoveMachine.Feint() routes Startup -> Recovery without ever
/// setting JustEnteredActive, so JumpShotReleaseResolver correctly reports "no
/// release." The windup animation (driven by Phase alone — see
/// MoveAnimResolver) plays either way, so an incidental aim-stick flick while
/// shooting read to the player as "I pressed shoot and nothing happened" —
/// the shot animation played, but the shot did not fire.
///
/// The fix: a JumpShot may only be pump-faked by the EXPLICIT "move_feint"
/// action (E key / L1). The same gesture continues to feint Crossover /
/// Hesitation exactly as before — their free abort is intentional and
/// unaffected — but it can no longer reach into an in-progress JumpShot's
/// committed release.
/// </summary>
public static class FeintGateResolver
{
    /// <param name="explicitFeintPressed">
    /// Input.IsActionJustPressed("move_feint") this tick — always legal to
    /// feint whatever move is running (subject to the machine's own window).
    /// </param>
    /// <param name="gestureKind">This tick's RightStickGestureRecognizer.Sample().Kind.</param>
    /// <param name="currentMove">
    /// The committed-move machine's CurrentMove (null if Inactive). Only used
    /// to withhold the ambiguous gesture-sourced feint from a running JumpShot.
    /// </param>
    /// <returns>True if CommittedMoveMachine.Feint() should be called this tick.</returns>
    public static bool ShouldFeint(bool explicitFeintPressed, GestureKind gestureKind, CommittedMove? currentMove)
    {
        if (explicitFeintPressed) return true;
        if (gestureKind != GestureKind.QuickReturn) return false;

        // The ambiguous gesture-based feint may still abort any OTHER move
        // (unchanged, free-abort behaviour) but must never silently eat a
        // JumpShot's ball release.
        return currentMove is not JumpShot;
    }
}
