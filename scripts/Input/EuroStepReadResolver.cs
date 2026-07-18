#nullable enable

using Godot;

namespace Hooper.Moves;

/// <summary>
/// Pure decision for "does this drive input carry a lateral read, and to which
/// side" (issue #231) — no Godot Node inheritance, mirroring
/// LayupRangeResolver / JumpShotReleaseResolver: the decision itself is pure and
/// unit-tested; PlayerController.SampleMoveInput is the thin glue that reads the
/// live movement stick and body axis into this function's parameters.
///
/// ── The euro-step's input grammar (design call, ADR-0014) ────────────────────
/// The euro-step reuses the SAME `move_drive` button as the straight drive-
/// gather (#230); the LEFT stick's lateral tilt at press decides which of the
/// two begins — a lateral push promotes the drive into a euro-step and picks the
/// step side, a forward/neutral push stays a straight drive-gather. This mirrors
/// the crossover family's established "same input, the stick shapes the read"
/// exit-vector grammar (the burst family decomposes the stick against the body
/// axes exactly this way), rather than spending a second button on a move that
/// is fundamentally "a drive-gather with a lateral read."
/// </summary>
public static class EuroStepReadResolver
{
    /// <summary>
    /// Resolves the body-relative lateral read from a movement-stick sample.
    /// </summary>
    /// <param name="stick">
    /// World-space movement-stick reading (PlayerController.ReadInput's (X,Z)
    /// convention — the SAME space the crossover family's exit vector uses).
    /// </param>
    /// <param name="bodyRightAxis">
    /// The player's body-relative right axis, HandStateResolver.BurstWorldDir(
    /// heading, +1). Passed in (not computed here) so this stays a pure decision
    /// with no dependency on the heading-math module.
    /// </param>
    /// <param name="deadzone">
    /// Lateral-component magnitude at/below which the tilt counts as neutral (a
    /// straight drive-gather, not a euro-step). Strict-greater gate, matching the
    /// codebase's other boundary conventions (LayupRangeResolver, the crossover
    /// exit deadzone).
    /// </param>
    /// <returns>
    /// +1 = step to the player's right, -1 = to the player's left, 0 = no lateral
    /// read (the input is a straight drive-gather).
    /// </returns>
    public static int ResolveLateralSign(Vector2 stick, Vector2 bodyRightAxis, float deadzone)
    {
        float lateral = stick.Dot(bodyRightAxis);
        if (Mathf.Abs(lateral) <= deadzone)
            return 0;
        return lateral > 0f ? +1 : -1;
    }
}
