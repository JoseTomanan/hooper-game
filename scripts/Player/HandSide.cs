namespace Hooper.Player;

/// <summary>
/// Which hand the ball-handler currently holds the ball in.
///
/// Introduced cosmetic-only (M7b, issue #73), then promoted to
/// server-authoritative state in M9 (issue #83, ADR-0012): which hand holds the
/// ball now decides whether a right-stick flick resolves as a crossover (flick
/// toward the empty hand → swap + burst) or a hesitation (flick toward the ball
/// hand → freeze). Because that drives move resolution, it must be server-owned
/// and predicted + reconciled (ADR-0002) — it lives on PlayerController.HandSide,
/// and the ball mesh's left/right offset merely READS it for display.
///
/// Body-relative: a player's left hand is their left hand regardless of facing.
/// </summary>
public enum HandSide
{
    Left,
    Right,
}
