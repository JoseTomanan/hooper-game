using System;

namespace Hooper.Moves;

/// <summary>
/// Immutable frame-count record for a committed move.
///
/// "Frames" here are integer physics ticks (1 / Engine.PhysicsTicksPerSecond).
/// Using ticks — not wall-clock seconds — keeps committed-move timing
/// deterministic and reproducible in the server-replay loop that M4 will add.
///
/// ── Design ──────────────────────────────────────────────────────────────────
/// Each committed move carries its own MoveFrameData instance so frame counts
/// are data-driven and tunable per move at construction time rather than
/// hard-coded in the machine. The machine reads these; it does not own them.
///
/// ── Validation ──────────────────────────────────────────────────────────────
/// Validation is done at construction time only — never inside a tick loop.
/// A tick loop that throws risks crashing _PhysicsProcess; construction-time
/// failures catch bad data during development/design.
/// </summary>
public sealed class MoveFrameData
{
    /// <summary>
    /// Number of physics ticks the move stays in Startup before Active fires.
    /// Must be >= 1 — a zero-frame startup would be invisible to the opponent,
    /// eliminating the legibility the frame data exists to create (ADR-0003).
    /// </summary>
    public int StartupFrames  { get; }

    /// <summary>
    /// Number of physics ticks the Active phase lasts.
    /// Must be >= 1 — a zero-frame active phase means the effect never runs.
    /// </summary>
    public int ActiveFrames   { get; }

    /// <summary>
    /// Number of physics ticks the Recovery phase lasts.
    /// Must be >= 1 — a zero-frame recovery removes the punish window, collapsing
    /// the mind game that is the point of the committed move (ADR-0003).
    /// </summary>
    public int RecoveryFrames { get; }

    /// <summary>
    /// How many Startup frames — counting from frame 0 — the feint window covers.
    /// 0 means this move cannot be feinted. Must be &lt;= StartupFrames.
    ///
    /// Example: StartupFrames = 8, FeintWindowFrames = 4 → feint is legal on
    /// frames 0..3 of Startup (i.e. FrameInPhase &lt; 4).
    /// </summary>
    public int FeintWindowFrames { get; }

    /// <param name="startupFrames">Physics ticks in Startup. Must be >= 1.</param>
    /// <param name="activeFrames">Physics ticks in Active. Must be >= 1.</param>
    /// <param name="recoveryFrames">Physics ticks in Recovery. Must be >= 1.</param>
    /// <param name="feintWindowFrames">Feint-legal ticks from start of Startup. 0 = no feint. Must be &lt;= startupFrames.</param>
    public MoveFrameData(int startupFrames, int activeFrames, int recoveryFrames, int feintWindowFrames)
    {
        if (startupFrames  < 1) throw new ArgumentOutOfRangeException(nameof(startupFrames),  "Must be >= 1.");
        if (activeFrames   < 1) throw new ArgumentOutOfRangeException(nameof(activeFrames),   "Must be >= 1.");
        if (recoveryFrames < 1) throw new ArgumentOutOfRangeException(nameof(recoveryFrames), "Must be >= 1.");
        if (feintWindowFrames < 0)
            throw new ArgumentOutOfRangeException(nameof(feintWindowFrames), "Must be >= 0.");
        if (feintWindowFrames > startupFrames)
            throw new ArgumentOutOfRangeException(nameof(feintWindowFrames),
                "Cannot exceed startupFrames — the feint window lives inside Startup.");

        StartupFrames    = startupFrames;
        ActiveFrames     = activeFrames;
        RecoveryFrames   = recoveryFrames;
        FeintWindowFrames = feintWindowFrames;
    }
}
