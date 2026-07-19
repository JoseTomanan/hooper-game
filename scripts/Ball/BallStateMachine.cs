namespace Hooper.Ball;

/// <summary>
/// Pure C# state machine for the basketball — no Godot Node inheritance,
/// no engine singletons, no _PhysicsProcess, no RPCs.
///
/// This separation exists because ADR-0004 requires the ball's logic to be
/// unit-testable without a running Godot instance.  A Node3D cannot be
/// instantiated headlessly (Godot's engine must be running), so the logic
/// lives here as a plain class that tests can drive directly.  The thin
/// BallController node composes this class and calls its methods each tick.
///
/// ── Legal transition graph ───────────────────────────────────────────────
///
///                      ┌──────────────────────────────────────────┐
///                      ▼                                          │
///   [start] ──────► Held ──StartDribble()──► Dribbling           │
///                    │  ▲         │                               │
///              Shoot()│  │Catch() │StopDribble()                  │
///                    ▼  │        ▼                               │
///                 InFlight ◄── Held (via StopDribble → Held)     │
///                    │                                            │
///              GoLoose()│                                         │
///                    ▼                                            │
///                  Loose ──────────────────Catch()───────────────┘
///
/// Full legal edge list:
///   Held       → Dribbling  : StartDribble()
///   Held       → InFlight   : Shoot()
///   Dribbling  → Held       : StopDribble()    (player cradles the ball)
///   Dribbling  → InFlight   : Shoot()          (shoot out of a dribble)
///   Dribbling  → Loose      : GoLoose()        (knocked away mid-dribble)
///   Held       → Loose      : GoLoose()        (issue #206, ADR-0018 Amendment
///                                              2026-07-19: poked loose during
///                                              a JumpShot's Startup/feint-
///                                              Recovery cradle — a held ball
///                                              was steal-immune before this)
///   InFlight   → Loose      : GoLoose()        (missed shot / block)
///   InFlight   → Held       : Catch()          (caught pass / alley-oop)
///   Loose      → Held       : Catch()          (picked up off the floor)
///   Held       → Held       : Turnover()       (dead-ball handoff: held-ball OOB, #63)
///   Dribbling  → Held       : Turnover()       (dead-ball handoff: held-ball OOB, #63)
///
/// (The Held/Dribbling → Held Turnover edge re-assigns the holder without a
///  loose scramble — see Turnover() below. Omitted from the diagram above to
///  keep it legible; it is a same-or-adjacent-state self-handoff, not a new path.)
///
/// All other transitions are INVALID and return false (no throw — callers
/// are expected to guard, but an invalid call should never crash a game tick).
///
/// ── Why return bool instead of throwing? ────────────────────────────────
/// A thrown exception inside _PhysicsProcess crashes the entire game loop.
/// Returning false lets the caller log and gracefully ignore a bad transition
/// — useful both in production and in tests that assert the return value.
/// </summary>
public sealed class BallStateMachine
{
    // ── State ─────────────────────────────────────────────────────────────

    /// <summary>The current ball state. Read-only to external code.</summary>
    public BallState Current { get; private set; }

    // ── Identity ──────────────────────────────────────────────────────────

    /// <summary>
    /// Peer ID of the player who currently controls the ball.
    /// 0 means no player (loose / in-flight with no designated catcher).
    ///
    /// Used by the server to know which player's position to attach the ball to
    /// (Held / Dribbling) and to authorise Catch() calls from the right peer.
    /// Not validated here — kept as data the BallController can inspect.
    /// </summary>
    public int HolderPeerId { get; private set; }

    // ── Constructor ───────────────────────────────────────────────────────

    /// <summary>
    /// Creates the state machine.  The ball always starts Held by the
    /// specified peer (tipoff / game-start handoff).
    /// </summary>
    /// <param name="initialHolderPeerId">Peer ID of the player who starts with the ball.</param>
    public BallStateMachine(int initialHolderPeerId = 0)
    {
        Current       = BallState.Held;
        HolderPeerId  = initialHolderPeerId;
    }

    // ── Transition methods ────────────────────────────────────────────────

    /// <summary>
    /// Held → Dribbling.  The holder begins a dribble cycle.
    /// The ball remains attached to the same player (HolderPeerId unchanged).
    /// </summary>
    /// <returns>True if the transition was legal; false if it was not.</returns>
    public bool StartDribble()
    {
        if (Current != BallState.Held) return false;

        Current = BallState.Dribbling;
        return true;
    }

    /// <summary>
    /// Dribbling → Held.  The player cradles the ball, ending the dribble cycle.
    /// Once cradled, the player may shoot or pass but cannot dribble again
    /// (double-dribble rule — enforced at the game rules layer, not here).
    /// </summary>
    /// <returns>True if the transition was legal; false if it was not.</returns>
    public bool StopDribble()
    {
        if (Current != BallState.Dribbling) return false;

        Current = BallState.Held;
        return true;
    }

    /// <summary>
    /// Held or Dribbling → InFlight.  The holder releases the ball on a shot or
    /// pass arc.  HolderPeerId is cleared (0) because no player "holds" the ball
    /// during flight; Catch() will restore it when a player receives.
    /// </summary>
    /// <returns>True if the transition was legal; false if it was not.</returns>
    public bool Shoot()
    {
        if (Current != BallState.Held && Current != BallState.Dribbling) return false;

        Current      = BallState.InFlight;
        HolderPeerId = 0;
        return true;
    }

    /// <summary>
    /// InFlight or Loose → Held.  A player picks up or catches the ball.
    ///
    /// <paramref name="newHolderPeerId"/> identifies who now controls it.
    /// The caller (BallController) is responsible for validating that the
    /// peer is close enough and has priority — the state machine just records
    /// the transition.
    /// </summary>
    /// <param name="newHolderPeerId">Peer ID of the player catching / picking up.</param>
    /// <returns>True if the transition was legal; false if it was not.</returns>
    public bool Catch(int newHolderPeerId)
    {
        if (Current != BallState.InFlight && Current != BallState.Loose) return false;

        Current      = BallState.Held;
        HolderPeerId = newHolderPeerId;
        return true;
    }

    /// <summary>
    /// Dribbling, Held, or InFlight → Loose.  The ball becomes uncontrolled —
    /// a stolen dribble, a poked-loose cradle, a missed shot, or a deflected
    /// pass.  HolderPeerId is cleared.
    ///
    /// Named GoLoose (not "KnockLoose") because it covers both a knock-away
    /// and a missed shot falling to the floor — the ball just went loose.
    ///
    /// Held was NOT a legal source here before issue #206 (ADR-0018 Amendment
    /// 2026-07-19): a held ball had no way to become uncontrolled at all,
    /// which is exactly what made it steal-immune — a holder could cradle a
    /// JumpShot to escape a Dribbling-targeted steal read with zero
    /// consequence. Adding Held here is what lets
    /// BallController.ResolveHeldStealAttempts's Held-branch (the pump-fake-
    /// window fix) actually take effect; the caller-side timing/window logic
    /// lives entirely in DefensiveResolution/PlayerController, not here — this
    /// method only had to stop silently refusing the transition.
    /// </summary>
    /// <returns>True if the transition was legal; false if it was not.</returns>
    public bool GoLoose()
    {
        if (Current != BallState.Dribbling && Current != BallState.InFlight && Current != BallState.Held)
            return false;

        Current      = BallState.Loose;
        HolderPeerId = 0;
        return true;
    }

    /// <summary>
    /// Held or Dribbling → Held (by a NEW holder).  A dead-ball change of
    /// possession — the ball passes DIRECTLY from the current handler to a new
    /// one without going loose for a scramble.  Models an out-of-bounds
    /// violation (the ballhandler crossed the court line): play stops and the
    /// opponent is awarded the ball.
    ///
    /// Distinct from the two adjacent edges:
    ///   • Catch    recovers an InFlight / Loose ball (a live recovery).
    ///   • GoLoose  makes a controlled ball uncontrolled (a live scramble).
    ///   • Turnover is a dead-ball handoff between two players, no scramble.
    ///
    /// Legal ONLY while a player actually holds the ball (Held or Dribbling);
    /// rejected otherwise so a stray call can never fabricate a holder out of a
    /// loose or in-flight ball (those go through Catch, which enforces its own
    /// recovery rules).  Lands in Held — the awarding glue (AwardPossession)
    /// then begins a dribble, matching the post-Catch possession shape.
    /// </summary>
    /// <param name="newHolderPeerId">Peer ID of the player awarded the ball.</param>
    /// <returns>True if the transition was legal; false if it was not.</returns>
    public bool Turnover(int newHolderPeerId)
    {
        if (Current != BallState.Held && Current != BallState.Dribbling) return false;

        Current      = BallState.Held;
        HolderPeerId = newHolderPeerId;
        return true;
    }

    // ── Network reconciliation ───────────────────────────────────────────

    /// <summary>
    /// Unconditionally overwrites Current and HolderPeerId to match a server
    /// snapshot (M4, issue #20).
    ///
    /// Why this exists alongside the transition methods above: those methods
    /// enforce the legal edge graph from the CALLER's current state, which is
    /// correct for locally-driven gameplay but wrong for reconciliation. A
    /// client's locally predicted state can legitimately disagree with the
    /// server (e.g. the client predicted Held→InFlight via Shoot() a tick
    /// before the server did, or a dropped packet left the client one state
    /// behind). Calling, say, Catch() to force a resync would fail and return
    /// false whenever the client's CURRENT state doesn't have that edge —
    /// exactly the situation reconciliation needs to repair. A discrete enum
    /// has no "smooth" partial-correction the way position does; the only
    /// correct fix for a mismatch is to snap directly to the authoritative
    /// value, so this method bypasses the edge graph entirely.
    /// </summary>
    /// <param name="state">Authoritative state from the server broadcast.</param>
    /// <param name="holderPeerId">Authoritative holder peer ID from the server broadcast.</param>
    public void ForceState(BallState state, int holderPeerId)
    {
        Current      = state;
        HolderPeerId = holderPeerId;
    }
}
