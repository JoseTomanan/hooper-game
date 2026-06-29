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
    /// 0 means this move cannot be feinted. Must be strictly &lt; StartupFrames so
    /// that at least one "committed tail" Startup frame always exists — the point
    /// of no return that makes the move a genuine commitment (ADR-0003).
    ///
    /// Example: StartupFrames = 18, FeintWindowFrames = 12 → feint is legal on
    /// frames 0..11 of Startup (i.e. FrameInPhase &lt; 12); frames 12..17 are the
    /// committed tail where a feint is no longer possible.
    /// </summary>
    public int FeintWindowFrames { get; }

    /// <summary>
    /// Physics ticks of Recovery a <em>feint</em> costs (0 = feint aborts straight
    /// to Inactive, the default). Lets a feinted move be a commitment of its own
    /// without incurring the full completed-move RecoveryFrames. Must be &lt;= RecoveryFrames.
    ///
    /// Example: RecoveryFrames = 20, FeintRecoveryFrames = 8 → a pump-fake costs 8
    /// ticks of Recovery (shorter than the full 20-tick landing recovery because you
    /// never left the ground), while Crossover/Hesitation keep the default 0 and
    /// still abort straight to Inactive on feint.
    /// </summary>
    public int FeintRecoveryFrames { get; }

    /// <summary>
    /// Earliest Startup frame on which a feint becomes legal — a feint is rejected
    /// while <c>FrameInPhase &lt; FeintMinStartupFrames</c> (#138, ADR-0003). 0 (the
    /// default) means a feint is legal from frame 0, preserving the original
    /// Crossover/Hesitation zero-cost-abort behaviour. A positive value guarantees
    /// a minimum visible telegraph before the move can be aborted — without it a
    /// same-tick begin+feint produces a zero-startup, invisible move (the
    /// arcade-decoupling anti-goal). When the feint window is open
    /// (<see cref="FeintWindowFrames"/> &gt; 0) this must be strictly less than it,
    /// so the legal feint window [min, window) is non-empty.
    /// </summary>
    public int FeintMinStartupFrames { get; }

    /// <param name="startupFrames">Physics ticks in Startup. Must be >= 1.</param>
    /// <param name="activeFrames">Physics ticks in Active. Must be >= 1.</param>
    /// <param name="recoveryFrames">Physics ticks in Recovery. Must be >= 1.</param>
    /// <param name="feintWindowFrames">Feint-legal ticks from start of Startup. 0 = no feint. Must be strictly &lt; startupFrames (or 0).</param>
    /// <param name="feintRecoveryFrames">Recovery ticks a feint costs. 0 = abort to Inactive. Must be &lt;= recoveryFrames.</param>
    /// <param name="feintMinStartupFrames">Earliest feint-legal Startup frame. 0 = legal from frame 0. Must be &lt; feintWindowFrames when the window is open.</param>
    public MoveFrameData(int startupFrames, int activeFrames, int recoveryFrames,
        int feintWindowFrames, int feintRecoveryFrames = 0, int feintMinStartupFrames = 0)
    {
        if (startupFrames  < 1) throw new ArgumentOutOfRangeException(nameof(startupFrames),  "Must be >= 1.");
        if (activeFrames   < 1) throw new ArgumentOutOfRangeException(nameof(activeFrames),   "Must be >= 1.");
        if (recoveryFrames < 1) throw new ArgumentOutOfRangeException(nameof(recoveryFrames), "Must be >= 1.");
        if (feintWindowFrames < 0)
            throw new ArgumentOutOfRangeException(nameof(feintWindowFrames), "Must be >= 0.");
        // Strict < : at least one committed-tail Startup frame must remain after the window
        // so the move is a genuine commitment (ADR-0003). A window == startup would allow
        // feinting all the way up to the Active transition, eliminating the point of no return.
        if (feintWindowFrames >= startupFrames && feintWindowFrames > 0)
            throw new ArgumentOutOfRangeException(nameof(feintWindowFrames),
                "Must be strictly < startupFrames — the committed tail (at least 1 Startup frame) must always exist.");
        if (feintRecoveryFrames < 0)
            throw new ArgumentOutOfRangeException(nameof(feintRecoveryFrames), "Must be >= 0.");
        if (feintRecoveryFrames > recoveryFrames)
            throw new ArgumentOutOfRangeException(nameof(feintRecoveryFrames),
                "Cannot exceed recoveryFrames — a feint cannot cost more recovery than the completed move.");
        if (feintMinStartupFrames < 0)
            throw new ArgumentOutOfRangeException(nameof(feintMinStartupFrames), "Must be >= 0.");
        // The legal feint window is [feintMinStartupFrames, feintWindowFrames). When a
        // feint window is open it must be non-empty, so the floor must sit strictly
        // below it — otherwise the move would advertise a feint that can never fire.
        if (feintMinStartupFrames > 0 && feintMinStartupFrames >= feintWindowFrames)
            throw new ArgumentOutOfRangeException(nameof(feintMinStartupFrames),
                "Must be strictly < feintWindowFrames — the legal feint window [min, window) must be non-empty.");

        StartupFrames         = startupFrames;
        ActiveFrames          = activeFrames;
        RecoveryFrames        = recoveryFrames;
        FeintWindowFrames     = feintWindowFrames;
        FeintRecoveryFrames   = feintRecoveryFrames;
        FeintMinStartupFrames = feintMinStartupFrames;
    }
}
