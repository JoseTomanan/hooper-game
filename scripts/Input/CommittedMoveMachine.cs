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
///   Feint()  : Startup → Inactive  (only if FeintMinStartupFrames &lt;= FrameInPhase &lt; FeintWindowFrames)
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
    /// Two routing paths based on the current move's FeintRecoveryFrames:
    ///
    ///   • FeintRecoveryFrames == 0 (default — Crossover, Hesitation, etc.):
    ///     Startup → Inactive immediately. No recovery cost.
    ///
    ///   • FeintRecoveryFrames &gt; 0 (pump-fake on JumpShot, #77):
    ///     Startup → Recovery, but entered at a pre-advanced FrameInPhase of
    ///     (RecoveryFrames - FeintRecoveryFrames). Tick()'s existing Recovery
    ///     case exits to Inactive when FrameInPhase &gt;= RecoveryFrames, so
    ///     starting at that offset means the machine spends exactly
    ///     FeintRecoveryFrames more ticks in Recovery. This reuses the existing
    ///     machinery with no new phase, no new serialized field, and no new
    ///     wire state — the server can broadcast (Phase=Recovery, FrameInPhase=offset)
    ///     and ForceState reconstructs the remaining duration correctly.
    ///
    ///     Phase and FrameInPhase are set directly (not via EnterPhase) so that
    ///     JustEnteredActive is NEVER set — the ball release is gated on that flag,
    ///     and a pump-fake must never release the ball.
    ///
    /// Returns false without changing state if outside the window or outside
    /// Startup — the machine is never disrupted by an ill-timed feint call.
    /// </summary>
    /// <returns>True if the feint succeeded; false if the window has passed or phase is wrong.</returns>
    public bool Feint()
    {
        if (Phase != MovePhase.Startup) return false;
        // Below the min-startup floor the move has not yet shown a visible
        // telegraph — a same-tick begin+feint would otherwise abort with zero
        // startup, an invisible move (#138, ADR-0003 legibility). 0 floor (the
        // default) preserves the original frame-0-legal feint behaviour.
        if (FrameInPhase < CurrentMove!.FrameData.FeintMinStartupFrames) return false;
        if (FrameInPhase >= CurrentMove.FrameData.FeintWindowFrames) return false;

        MoveFrameData fd = CurrentMove.FrameData;

        if (fd.FeintRecoveryFrames == 0)
        {
            // Default path (Crossover, Hesitation, zero-cost feint): abort to Inactive.
            EnterPhase(MovePhase.Inactive);
        }
        else
        {
            // Pump-fake path (#77): enter Recovery at a pre-advanced offset so that
            // exactly FeintRecoveryFrames ticks remain before Tick() exits to Inactive.
            // We bypass EnterPhase() intentionally — EnterPhase() resets FrameInPhase
            // to 0 and would set JustEnteredActive if entering Active. Neither is correct
            // here: we need a non-zero FrameInPhase, and JustEnteredActive must stay false
            // (a feint must never trigger the one-shot ball-release effect).
            Phase        = MovePhase.Recovery;
            FrameInPhase = fd.RecoveryFrames - fd.FeintRecoveryFrames;
            // JustEnteredActive is already false (cleared at the top of Tick, or by Begin).
            // Do NOT touch it here — leaving it false is what guarantees the ball never releases.
        }

        return true;
    }

    /// <summary>
    /// Ends the Active phase early, transitioning straight to Recovery without
    /// waiting for FrameInPhase to reach ActiveFrames. Legal only from Active;
    /// a no-op returning false otherwise.
    ///
    /// Why this exists (issue #96 remediation): every OTHER phase transition in
    /// this machine is driven purely by frame counts (Tick() above) because no
    /// move previously had a side effect that could resolve before its Active
    /// window naturally expired. The steal is the first — a successful steal
    /// changes BallState (Dribbling → Loose) mid-Active, and the ball can come
    /// straight back to the same holder while dribble phase is frozen (it only
    /// advances in TickDribbling), which would let the SAME resolved StealMove
    /// re-fire on every remaining Active tick. EndActiveEarly caps a committed
    /// move's real-world effect at "resolves once, then pays Recovery like
    /// normal" — the caller (PlayerController.EndResolvedSteal) invokes this
    /// exactly when BallController.ResolveStealAttempts confirms a success.
    /// </summary>
    /// <returns>True if the phase advanced to Recovery; false if not in Active.</returns>
    public bool EndActiveEarly()
    {
        if (Phase != MovePhase.Active) return false;
        EnterPhase(MovePhase.Recovery);
        return true;
    }

    // ── Network reconciliation ───────────────────────────────────────────────

    /// <summary>
    /// Unconditionally overwrites Phase, FrameInPhase, and CurrentMove to match
    /// a server snapshot (M4, issue #21) — same role as BallStateMachine.ForceState
    /// (M4, issue #20).
    ///
    /// Why this exists alongside Begin()/Tick()/Feint(): those methods enforce
    /// the legal phase graph from the CALLER's current phase, which is correct
    /// for locally-driven gameplay but wrong for reconciliation. A client's
    /// locally predicted phase can legitimately disagree with the server (e.g.
    /// the client predicted Begin() a tick before the server's copy left
    /// Recovery, so the server rejected it and stayed Inactive while the client
    /// believes it is in Startup). Calling Begin()/Feint() to force a resync
    /// would fail exactly when the edge graph doesn't already allow it — the
    /// precise situation reconciliation needs to repair. A discrete phase has
    /// no "smooth" partial-correction the way position does, so the only
    /// correct fix is to snap directly to the authoritative value.
    /// </summary>
    /// <param name="phase">Authoritative phase from the server broadcast.</param>
    /// <param name="frameInPhase">Authoritative frame-in-phase from the server broadcast.</param>
    /// <param name="move">
    /// Authoritative move reconstructed from the broadcast's moveId/payload, or
    /// null when phase is Inactive (no move running).
    /// </param>
    public void ForceState(MovePhase phase, int frameInPhase, CommittedMove? move)
    {
        // (Doubt cycle 1, finding #6) A non-Inactive phase with no move is
        // nonsensical — Tick() dereferences CurrentMove! unconditionally
        // whenever Phase != Inactive, so a caller passing this combination
        // (e.g. a malformed broadcast) would crash the physics loop on the
        // very next Tick(). Normalize defensively rather than trust the
        // caller, mirroring this class's own stated design value: returning
        // false/normalizing instead of throwing keeps _PhysicsProcess safe.
        if (phase != MovePhase.Inactive && move == null)
            phase = MovePhase.Inactive;

        Phase        = phase;
        FrameInPhase = phase == MovePhase.Inactive ? 0 : frameInPhase;
        CurrentMove  = phase == MovePhase.Inactive ? null : move;
        // JustEnteredActive is a one-shot, single-tick signal for the LOCAL
        // Tick() loop to apply an Active-phase effect (e.g. the crossover
        // burst). A forced resync is not "entering Active this tick" in that
        // sense — it is the machine catching up to where the server already
        // is — so JustEnteredActive is always cleared here, never set, even
        // when phase == Active. The caller (PlayerController) does not depend
        // on this RPC to trigger the burst; the server's own Tick() already
        // raised it locally inside the server's authoritative simulation.
        JustEnteredActive = false;
    }

    /// <summary>
    /// Decides whether a client's local prediction should be force-corrected
    /// back to Inactive, given the server's last-known phase (M4, issue #21,
    /// second doubt cycle — extracted from PlayerController.ReconcileFromServer
    /// so this decision is unit-testable; PlayerController itself cannot be,
    /// since it extends a Godot Node and is excluded from the pure-class test
    /// project by design).
    ///
    /// Deliberately narrow — see PlayerController.ReconcileFromServer's Step 0
    /// comment for the full reasoning this codifies:
    ///   - Only ever corrects TOWARD Inactive, never INTO a non-Inactive phase
    ///     (there is no scenario where the server broadcast should make a
    ///     client predict a move it didn't locally begin).
    ///   - Ignores FrameInPhase entirely — ReceiveState is structurally ~1 RTT
    ///     stale, so comparing exact frame counts would force-rewind every
    ///     active move under any nonzero latency.
    ///   - Gives Startup a grace window: a transient one-RTT delay before the
    ///     server's confirmation arrives is expected on every legitimate move
    ///     attempt and has no visible effect yet (Velocity is zero during
    ///     Startup either way), so correcting during Startup would falsely
    ///     flicker every committed move, not just mispredicted ones.
    /// </summary>
    /// <param name="localPhase">This machine's current Phase.</param>
    /// <param name="localIsActive">This machine's current IsActive (equivalent to localPhase != Inactive, passed separately so callers don't need to duplicate that check).</param>
    /// <param name="serverPhase">The phase from the most recent server broadcast.</param>
    /// <returns>True if ForceState(Inactive, ...) should be called.</returns>
    public static bool ShouldForceInactive(MovePhase localPhase, bool localIsActive, MovePhase serverPhase)
    {
        bool serverSaysInactive = serverPhase == MovePhase.Inactive;
        bool clientPastStartup  = localIsActive && localPhase != MovePhase.Startup;
        return serverSaysInactive && clientPastStartup;
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
