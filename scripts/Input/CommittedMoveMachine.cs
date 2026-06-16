#nullable enable

namespace Hooper.Moves;

/// <summary>
/// Pure C# state machine for committed moves — no Godot Node inheritance,
/// no engine singletons, no _PhysicsProcess, no RPCs.
///
/// This separation mirrors the BallStateMachine pattern: all the sequencing
/// logic lives here as a testable plain class; the thin PlayerController node
/// drives it each tick and applies effects based on the Phase/JustEnteredActive
/// it reads. Keep new behaviour here; keep node glue thin.
///
/// ── Legal phase graph ────────────────────────────────────────────────────────
///
///   [start] ──► Inactive ──Begin()──► Startup ──Tick()──► Active ──Tick()──► Recovery
///                   ▲                   │                                        │
///                   │             Feint()│ (within feint window)                  │
///                   │                   ▼                                        │
///                   └───────────── Inactive ◄───────── Tick() ──────────────────┘
///
///   Begin()  : Inactive → Startup  (returns false if already in a move)
///   Tick()   : advances one frame; Startup→Active→Recovery→Inactive by frame counts
///   Feint()  : Startup → Inactive  (only if FrameInPhase &lt; FeintWindowFrames)
///
/// ── No flow-cancel ───────────────────────────────────────────────────────────
/// There is intentionally no Cancel() or Interrupt() method. A committed move
/// runs to completion (or to a feint abort during its designated window). This is
/// the design value per ADR-0003 — the absence of a cancel IS the mind game.
///
/// ── Why return bool instead of throwing? ────────────────────────────────────
/// Returning false on Begin() keeps the caller safe inside _PhysicsProcess:
/// a thrown exception would crash the physics loop. Same contract as BallStateMachine.
/// MoveFrameData validation throws at construction time, not here.
/// </summary>
public sealed class CommittedMoveMachine
{
    // ── State ─────────────────────────────────────────────────────────────────

    /// <summary>The current phase of the active committed move.</summary>
    public MovePhase Phase { get; private set; } = MovePhase.Inactive;

    /// <summary>
    /// The move currently running, or null if Inactive.
    /// Cleared when the machine returns to Inactive (completion or feint abort).
    /// </summary>
    public CommittedMove? CurrentMove { get; private set; }

    /// <summary>
    /// How many ticks the machine has been in the current phase.
    /// Resets to 0 on every phase transition.
    /// </summary>
    public int FrameInPhase { get; private set; }

    /// <summary>True while any phase other than Inactive is active.</summary>
    public bool IsActive => Phase != MovePhase.Inactive;

    /// <summary>
    /// True for exactly ONE tick — the tick on which the machine enters Active.
    /// The node polls this to know when to apply the move's one-shot effect
    /// (e.g. the lateral burst of a crossover). Cleared on the following Tick().
    /// </summary>
    public bool JustEnteredActive { get; private set; }

    // ── Transitions ───────────────────────────────────────────────────────────

    /// <summary>
    /// Begins a committed move. Legal only from Inactive.
    ///
    /// Sets Phase to Startup, records the move, and resets FrameInPhase.
    /// Idempotent on failure — calling Begin() while a move is running is a
    /// no-op that returns false; the in-progress move is unaffected.
    /// </summary>
    /// <param name="move">The move to begin. Must not be null.</param>
    /// <returns>True if the move started; false if a move was already in progress.</returns>
    public bool Begin(CommittedMove move)
    {
        if (Phase != MovePhase.Inactive) return false;

        CurrentMove       = move;
        Phase             = MovePhase.Startup;
        FrameInPhase      = 0;
        JustEnteredActive = false;
        return true;
    }

    /// <summary>
    /// Advances the machine by one physics tick.
    ///
    /// No-op when Inactive (nothing to advance).
    /// Transitions happen AFTER the frame is consumed — FrameInPhase reaches
    /// the threshold and THEN the phase changes:
    ///   Startup  → Active   when FrameInPhase reaches StartupFrames
    ///   Active   → Recovery when FrameInPhase reaches ActiveFrames
    ///   Recovery → Inactive when FrameInPhase reaches RecoveryFrames
    ///
    /// JustEnteredActive is set to true on the tick that enters Active and
    /// cleared on all subsequent ticks.
    /// </summary>
    public void Tick()
    {
        if (Phase == MovePhase.Inactive) return;

        // Clear the one-shot flag from the previous tick before we (potentially)
        // re-set it below. This guarantees it is true for exactly one tick.
        JustEnteredActive = false;

        MoveFrameData fd = CurrentMove!.FrameData;
        FrameInPhase++;

        switch (Phase)
        {
            case MovePhase.Startup:
                if (FrameInPhase >= fd.StartupFrames)
                    EnterPhase(MovePhase.Active);
                break;

            case MovePhase.Active:
                if (FrameInPhase >= fd.ActiveFrames)
                    EnterPhase(MovePhase.Recovery);
                break;

            case MovePhase.Recovery:
                if (FrameInPhase >= fd.RecoveryFrames)
                    EnterPhase(MovePhase.Inactive);
                break;
        }
    }

    /// <summary>
    /// Attempts a feint abort. Legal only while in Startup AND within the
    /// feint window (FrameInPhase &lt; FeintWindowFrames).
    ///
    /// A successful feint cancels the move straight to Inactive — Active never
    /// fires, and there are no recovery frames. This is the trade: the player
    /// baited the opponent's read but must eat a small startup cost.
    ///
    /// Returns false without changing state if outside the window or outside
    /// Startup — the machine is never disrupted by an ill-timed feint call.
    /// </summary>
    /// <returns>True if the feint succeeded; false if the window has passed or phase is wrong.</returns>
    public bool Feint()
    {
        if (Phase != MovePhase.Startup) return false;
        if (FrameInPhase >= CurrentMove!.FrameData.FeintWindowFrames) return false;

        // Abort to Inactive immediately — no recovery, Active never fires.
        EnterPhase(MovePhase.Inactive);
        return true;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void EnterPhase(MovePhase next)
    {
        Phase        = next;
        FrameInPhase = 0;

        if (next == MovePhase.Active)
            JustEnteredActive = true;

        if (next == MovePhase.Inactive)
            CurrentMove = null;
    }
}
