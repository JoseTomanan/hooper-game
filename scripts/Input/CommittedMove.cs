namespace Hooper.Moves;

/// <summary>
/// Abstract base for all committed moves.
///
/// A committed move is a discrete, locked-in action with explicit startup,
/// active, and recovery frames (ADR-0003). Once begun it runs to completion —
/// there is no flow-cancel. The frame data lives here; the machine that
/// sequences those frames is CommittedMoveMachine.
///
/// Concrete moves subclass this to add any move-specific data (e.g. a
/// direction, a target peer) while sharing the common identity + frame-data
/// contract. Using an abstract class rather than an interface lets subclasses
/// carry data fields without requiring a wrapper object.
///
/// ── Adding a new move ────────────────────────────────────────────────────────
/// 1. Subclass CommittedMove.
/// 2. Supply a stable Id string (used for logging / networking in M4).
/// 3. Pass a MoveFrameData to base() with tuned frame counts.
/// 4. Implement any Active-phase side effects in the PlayerController switch
///    (or the equivalent move-effect layer) — not here.
/// </summary>
public abstract class CommittedMove
{
    /// <summary>
    /// Stable, unique identifier for this move type — used for logging and
    /// (in M4) for serializing committed moves across the network.
    /// </summary>
    public string Id          { get; }

    /// <summary>Human-readable name for debugging and UI display.</summary>
    public string DisplayName { get; }

    /// <summary>Frame counts that govern this move's startup / active / recovery timing.</summary>
    public MoveFrameData FrameData { get; }

    protected CommittedMove(string id, string displayName, MoveFrameData frameData)
    {
        Id          = id;
        DisplayName = displayName;
        FrameData   = frameData;
    }
}
