using Hooper.Moves;

namespace Hooper.Ball.Tests;

/// <summary>
/// Unit tests for CommittedMoveMachine — the pure C# class that sequences
/// committed-move phases (Startup / Active / Recovery / Inactive).
///
/// These tests run without a live Godot instance. Frame counting is integer
/// ticks (identical to what BallController uses), so the logic is deterministic
/// and reproducible in the M4 server-replay loop.
///
/// ── Test naming ──────────────────────────────────────────────────────────────
/// [MethodUnderTest]_[Scenario]_[ExpectedOutcome]
/// Each test contains exactly one logical assertion.
///
/// ── Legal phase graph under test ────────────────────────────────────────────
///   Inactive → Startup   : Begin(move)  → returns true
///   Startup  → Active    : Tick() × StartupFrames
///   Active   → Recovery  : Tick() × ActiveFrames
///   Recovery → Inactive  : Tick() × RecoveryFrames
///   Startup  → Inactive  : Feint() within feint window (no recovery)
///   All else             : returns false / no state change
/// </summary>
public class CommittedMoveMachineTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>A standard crossover move for use in tests.</summary>
    private static readonly MoveFrameData StandardData =
        new(startupFrames: 6, activeFrames: 3, recoveryFrames: 10, feintWindowFrames: 4);

    private static CommittedMoveMachine NewMachine() => new();

    /// <summary>Returns a test move with the given frame data.</summary>
    private static CommittedMove TestMove(MoveFrameData? fd = null) =>
        new Crossover(burstDirection: 1f, frameData: fd ?? StandardData);

    /// <summary>Ticks the machine n times.</summary>
    private static void TickN(CommittedMoveMachine m, int n)
    {
        for (int i = 0; i < n; i++) m.Tick();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Initial state
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Constructor_Default_PhaseIsInactive()
    {
        var m = NewMachine();
        Assert.Equal(MovePhase.Inactive, m.Phase);
    }

    [Fact]
    public void Constructor_Default_CurrentMoveIsNull()
    {
        var m = NewMachine();
        Assert.Null(m.CurrentMove);
    }

    [Fact]
    public void Constructor_Default_IsActiveFalse()
    {
        var m = NewMachine();
        Assert.False(m.IsActive);
    }

    [Fact]
    public void Constructor_Default_JustEnteredActiveFalse()
    {
        var m = NewMachine();
        Assert.False(m.JustEnteredActive);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Begin — Inactive → Startup
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Begin_WhenInactive_ReturnsTrue()
    {
        var m    = NewMachine();
        bool ok  = m.Begin(TestMove());
        Assert.True(ok);
    }

    [Fact]
    public void Begin_WhenInactive_PhaseIsStartup()
    {
        var m = NewMachine();
        m.Begin(TestMove());
        Assert.Equal(MovePhase.Startup, m.Phase);
    }

    [Fact]
    public void Begin_WhenInactive_CurrentMoveIsSet()
    {
        var m    = NewMachine();
        var move = TestMove();
        m.Begin(move);
        Assert.Same(move, m.CurrentMove);
    }

    [Fact]
    public void Begin_WhenInactive_IsActiveTrue()
    {
        var m = NewMachine();
        m.Begin(TestMove());
        Assert.True(m.IsActive);
    }

    [Fact]
    public void Begin_WhenInactive_FrameInPhaseIsZero()
    {
        var m = NewMachine();
        m.Begin(TestMove());
        Assert.Equal(0, m.FrameInPhase);
    }

    [Fact]
    public void Begin_WhenStartup_ReturnsFalse()
    {
        var m = NewMachine();
        m.Begin(TestMove());
        bool second = m.Begin(TestMove());
        Assert.False(second);
    }

    [Fact]
    public void Begin_WhenStartup_PhaseUnchanged()
    {
        var m = NewMachine();
        m.Begin(TestMove());
        m.Begin(TestMove()); // no-op
        Assert.Equal(MovePhase.Startup, m.Phase);
    }

    [Fact]
    public void Begin_WhenActive_ReturnsFalse()
    {
        var m = NewMachine();
        m.Begin(TestMove());
        TickN(m, StandardData.StartupFrames); // → Active
        bool second = m.Begin(TestMove());
        Assert.False(second);
    }

    [Fact]
    public void Begin_WhenRecovery_ReturnsFalse()
    {
        var m = NewMachine();
        m.Begin(TestMove());
        TickN(m, StandardData.StartupFrames + StandardData.ActiveFrames); // → Recovery
        bool second = m.Begin(TestMove());
        Assert.False(second);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Tick — frame counting and phase transitions
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Tick_WhenInactive_PhaseRemainsInactive()
    {
        var m = NewMachine();
        m.Tick();
        Assert.Equal(MovePhase.Inactive, m.Phase);
    }

    [Fact]
    public void Tick_WhenInactive_FrameInPhaseRemainsZero()
    {
        var m = NewMachine();
        m.Tick();
        Assert.Equal(0, m.FrameInPhase);
    }

    [Fact]
    public void Tick_DuringStartup_FrameInPhaseIncrements()
    {
        var m = NewMachine();
        m.Begin(TestMove());
        m.Tick();
        Assert.Equal(1, m.FrameInPhase);
    }

    [Fact]
    public void Tick_StartupFramesMinus1_StillInStartup()
    {
        var m = NewMachine();
        m.Begin(TestMove());
        TickN(m, StandardData.StartupFrames - 1);
        Assert.Equal(MovePhase.Startup, m.Phase);
    }

    [Fact]
    public void Tick_ExactlyStartupFrames_TransitionsToActive()
    {
        var m = NewMachine();
        m.Begin(TestMove());
        TickN(m, StandardData.StartupFrames);
        Assert.Equal(MovePhase.Active, m.Phase);
    }

    [Fact]
    public void Tick_EnteringActive_FrameInPhaseResetsToZero()
    {
        var m = NewMachine();
        m.Begin(TestMove());
        TickN(m, StandardData.StartupFrames);
        Assert.Equal(0, m.FrameInPhase);
    }

    [Fact]
    public void Tick_ExactlyActiveFrames_TransitionsToRecovery()
    {
        var m = NewMachine();
        m.Begin(TestMove());
        TickN(m, StandardData.StartupFrames + StandardData.ActiveFrames);
        Assert.Equal(MovePhase.Recovery, m.Phase);
    }

    [Fact]
    public void Tick_EnteringRecovery_FrameInPhaseResetsToZero()
    {
        var m = NewMachine();
        m.Begin(TestMove());
        TickN(m, StandardData.StartupFrames + StandardData.ActiveFrames);
        Assert.Equal(0, m.FrameInPhase);
    }

    [Fact]
    public void Tick_ExactlyRecoveryFrames_TransitionsToInactive()
    {
        var m = NewMachine();
        m.Begin(TestMove());
        TickN(m, StandardData.StartupFrames + StandardData.ActiveFrames + StandardData.RecoveryFrames);
        Assert.Equal(MovePhase.Inactive, m.Phase);
    }

    [Fact]
    public void Tick_FullCycle_CurrentMoveIsNull()
    {
        var m = NewMachine();
        m.Begin(TestMove());
        TickN(m, StandardData.StartupFrames + StandardData.ActiveFrames + StandardData.RecoveryFrames);
        Assert.Null(m.CurrentMove);
    }

    [Fact]
    public void Tick_FullCycle_IsActiveFalse()
    {
        var m = NewMachine();
        m.Begin(TestMove());
        TickN(m, StandardData.StartupFrames + StandardData.ActiveFrames + StandardData.RecoveryFrames);
        Assert.False(m.IsActive);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // JustEnteredActive — fires exactly once
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Tick_FirstTickOfActive_JustEnteredActiveTrue()
    {
        var m = NewMachine();
        m.Begin(TestMove());
        // Tick to the boundary — the tick that transitions Startup→Active.
        TickN(m, StandardData.StartupFrames);
        Assert.True(m.JustEnteredActive);
    }

    [Fact]
    public void Tick_SecondTickOfActive_JustEnteredActiveFalse()
    {
        var m = NewMachine();
        m.Begin(TestMove());
        TickN(m, StandardData.StartupFrames); // → Active, JustEnteredActive = true
        m.Tick();                              // 2nd Active tick, must clear it
        Assert.False(m.JustEnteredActive);
    }

    [Fact]
    public void Tick_DuringStartup_JustEnteredActiveFalse()
    {
        var m = NewMachine();
        m.Begin(TestMove());
        m.Tick(); // still in Startup
        Assert.False(m.JustEnteredActive);
    }

    [Fact]
    public void Tick_DuringRecovery_JustEnteredActiveFalse()
    {
        var m = NewMachine();
        m.Begin(TestMove());
        TickN(m, StandardData.StartupFrames + StandardData.ActiveFrames); // → Recovery
        Assert.False(m.JustEnteredActive);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // EndActiveEarly — resolves Active before ActiveFrames elapse (issue #96
    // single-fire guard: a steal that resolves mid-Active must not be able to
    // fire a second time while the ball is still Loose and the dribble phase
    // is frozen in-band)
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void EndActiveEarly_WhenActive_ReturnsTrue()
    {
        var m = NewMachine();
        m.Begin(TestMove());
        TickN(m, StandardData.StartupFrames); // → Active
        Assert.True(m.EndActiveEarly());
    }

    [Fact]
    public void EndActiveEarly_WhenActive_TransitionsToRecovery()
    {
        var m = NewMachine();
        m.Begin(TestMove());
        TickN(m, StandardData.StartupFrames); // → Active
        m.EndActiveEarly();
        Assert.Equal(MovePhase.Recovery, m.Phase);
    }

    [Fact]
    public void EndActiveEarly_WhenActive_FrameInPhaseResetsToZero()
    {
        var m = NewMachine();
        m.Begin(TestMove());
        TickN(m, StandardData.StartupFrames); // → Active
        m.Tick();                              // FrameInPhase = 1, still well short of ActiveFrames
        m.EndActiveEarly();
        Assert.Equal(0, m.FrameInPhase);
    }

    [Fact]
    public void EndActiveEarly_WhenActive_EndsBeforeActiveFramesWouldHaveElapsed()
    {
        // The whole point: Recovery is reached in FEWER ticks than a natural
        // Active → Recovery transition would take.
        var m = NewMachine();
        m.Begin(TestMove());
        TickN(m, StandardData.StartupFrames); // → Active, FrameInPhase = 0
        m.EndActiveEarly();
        // Only 1 tick has elapsed since Active would naturally need ActiveFrames (3).
        Assert.True(1 < StandardData.ActiveFrames);
        Assert.Equal(MovePhase.Recovery, m.Phase);
    }

    [Fact]
    public void EndActiveEarly_ThenTicksToRecoveryFrames_TransitionsToInactive()
    {
        // A move ended early still pays the FULL RecoveryFrames cost — it is
        // not a shortcut out of the move entirely, only out of the remainder
        // of Active.
        var m = NewMachine();
        m.Begin(TestMove());
        TickN(m, StandardData.StartupFrames); // → Active
        m.EndActiveEarly();                    // → Recovery, FrameInPhase = 0
        TickN(m, StandardData.RecoveryFrames - 1);
        Assert.Equal(MovePhase.Recovery, m.Phase);
        m.Tick();
        Assert.Equal(MovePhase.Inactive, m.Phase);
    }

    [Fact]
    public void EndActiveEarly_WhenStartup_ReturnsFalse()
    {
        var m = NewMachine();
        m.Begin(TestMove()); // Startup
        Assert.False(m.EndActiveEarly());
    }

    [Fact]
    public void EndActiveEarly_WhenStartup_PhaseUnchanged()
    {
        var m = NewMachine();
        m.Begin(TestMove());
        m.EndActiveEarly(); // no-op
        Assert.Equal(MovePhase.Startup, m.Phase);
    }

    [Fact]
    public void EndActiveEarly_WhenRecovery_ReturnsFalse()
    {
        var m = NewMachine();
        m.Begin(TestMove());
        TickN(m, StandardData.StartupFrames + StandardData.ActiveFrames); // → Recovery
        Assert.False(m.EndActiveEarly());
    }

    [Fact]
    public void EndActiveEarly_WhenInactive_ReturnsFalse()
    {
        var m = NewMachine();
        Assert.False(m.EndActiveEarly());
    }

    [Fact]
    public void EndActiveEarly_CalledTwiceInActive_SecondCallReturnsFalse()
    {
        // Guards against a caller invoking it more than once per Active window —
        // the first call already left Active, so a second call is a no-op.
        var m = NewMachine();
        m.Begin(TestMove());
        TickN(m, StandardData.StartupFrames); // → Active
        Assert.True(m.EndActiveEarly());
        Assert.False(m.EndActiveEarly());
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Feint — abort during startup window
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Feint_WhenInactive_ReturnsFalse()
    {
        var m = NewMachine();
        bool ok = m.Feint();
        Assert.False(ok);
    }

    [Fact]
    public void Feint_InStartupWithinWindow_ReturnsTrue()
    {
        var m = NewMachine();
        m.Begin(TestMove()); // FeintWindowFrames = 4
        m.Tick();            // FrameInPhase = 1 (< 4, inside window)
        bool ok = m.Feint();
        Assert.True(ok);
    }

    [Fact]
    public void Feint_InStartupWithinWindow_PhaseIsInactive()
    {
        var m = NewMachine();
        m.Begin(TestMove());
        m.Tick();
        m.Feint();
        Assert.Equal(MovePhase.Inactive, m.Phase);
    }

    [Fact]
    public void Feint_InStartupWithinWindow_CurrentMoveCleared()
    {
        var m = NewMachine();
        m.Begin(TestMove());
        m.Tick();
        m.Feint();
        Assert.Null(m.CurrentMove);
    }

    [Fact]
    public void Feint_InStartupAtFrame0_ReturnsTrue()
    {
        // Frame 0 is inside any window > 0 (FrameInPhase = 0 < FeintWindowFrames = 4).
        var m = NewMachine();
        m.Begin(TestMove());
        // Do NOT tick — FrameInPhase is 0
        bool ok = m.Feint();
        Assert.True(ok);
    }

    [Fact]
    public void Feint_InStartupAtExactWindowBoundary_ReturnsFalse()
    {
        // FrameInPhase == FeintWindowFrames means the window has closed.
        var m = NewMachine();
        m.Begin(TestMove()); // FeintWindowFrames = 4
        TickN(m, 4);         // FrameInPhase = 4 (== window, window closed)
        bool ok = m.Feint();
        Assert.False(ok);
    }

    [Fact]
    public void Feint_InStartupAtExactWindowBoundary_PhaseStillStartup()
    {
        var m = NewMachine();
        m.Begin(TestMove());
        TickN(m, 4); // window closed
        m.Feint();   // no-op
        Assert.Equal(MovePhase.Startup, m.Phase);
    }

    // ── Feint min-startup floor (#138) ──────────────────────────────────────

    /// <summary>A move with a feint floor: legal window is [2, 4) of Startup.</summary>
    private static MoveFrameData FlooredFeintData =>
        new(startupFrames: 6, activeFrames: 3, recoveryFrames: 10,
            feintWindowFrames: 4, feintRecoveryFrames: 0, feintMinStartupFrames: 2);

    [Fact]
    public void Feint_BeforeMinStartupFrames_ReturnsFalse()
    {
        // A feint must be rejected until FeintMinStartupFrames startup ticks have
        // elapsed, so a same-tick begin+feint cannot produce a zero-startup
        // (invisible) move (#138, ADR-0003 legibility).
        var m = NewMachine();
        m.Begin(TestMove(FlooredFeintData)); // FrameInPhase = 0
        Assert.False(m.Feint(), "feint at frame 0 must be below the min-startup floor (2)");
        m.Tick();                            // FrameInPhase = 1
        Assert.False(m.Feint(), "feint at frame 1 must be below the min-startup floor (2)");
    }

    [Fact]
    public void Feint_BelowMinStartup_PhaseStaysStartup()
    {
        var m = NewMachine();
        m.Begin(TestMove(FlooredFeintData));
        m.Feint(); // rejected, must not disrupt the move
        Assert.Equal(MovePhase.Startup, m.Phase);
    }

    [Fact]
    public void Feint_AtMinStartupFrame_ReturnsTrue()
    {
        // Frame 2 == floor and still inside the window (< 4): feint is now legal.
        var m = NewMachine();
        m.Begin(TestMove(FlooredFeintData));
        TickN(m, 2); // FrameInPhase = 2
        Assert.True(m.Feint(), "feint at frame 2 (== floor, < window 4) should be allowed");
    }

    [Fact]
    public void Feint_AfterWindowClosed_ReturnsFalse()
    {
        var m = NewMachine();
        m.Begin(TestMove()); // FeintWindowFrames = 4
        TickN(m, 5);         // FrameInPhase = 5 (past window, still in Startup since StartupFrames=6)
        bool ok = m.Feint();
        Assert.False(ok);
    }

    [Fact]
    public void Feint_WhenActive_ReturnsFalse()
    {
        var m = NewMachine();
        m.Begin(TestMove());
        TickN(m, StandardData.StartupFrames); // → Active
        bool ok = m.Feint();
        Assert.False(ok);
    }

    [Fact]
    public void Feint_WhenRecovery_ReturnsFalse()
    {
        var m = NewMachine();
        m.Begin(TestMove());
        TickN(m, StandardData.StartupFrames + StandardData.ActiveFrames); // → Recovery
        bool ok = m.Feint();
        Assert.False(ok);
    }

    [Fact]
    public void Feint_ZeroFeintWindow_AlwaysReturnsFalse()
    {
        // A move with no feint window cannot be feinted at all.
        var fd = new MoveFrameData(startupFrames: 6, activeFrames: 3, recoveryFrames: 10, feintWindowFrames: 0);
        var m  = NewMachine();
        m.Begin(TestMove(fd));
        bool ok = m.Feint(); // FrameInPhase = 0, not < 0 → false
        Assert.False(ok);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // After feint — machine is re-usable immediately
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void AfterFeint_CanBeginNewMove()
    {
        var m = NewMachine();
        m.Begin(TestMove());
        m.Feint(); // aborts to Inactive
        bool ok = m.Begin(TestMove());
        Assert.True(ok);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Multi-step sequence — realistic gameplay flows
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Sequence_FullMoveCycle_MachineReady()
    {
        // Full cycle: Begin → all startup ticks → all active ticks → all recovery
        // ticks → Inactive. The machine should be clean and accept a new move.
        var m = NewMachine();
        m.Begin(TestMove());
        TickN(m, StandardData.StartupFrames + StandardData.ActiveFrames + StandardData.RecoveryFrames);

        bool canBeginAgain = m.Begin(TestMove());
        Assert.True(canBeginAgain);
    }

    [Fact]
    public void Sequence_FeintThenNewMove_FullCycleWorks()
    {
        // Feint resets cleanly; the next move runs a full cycle.
        var m = NewMachine();
        m.Begin(TestMove());
        m.Feint();             // abort to Inactive
        m.Begin(TestMove());   // start again
        TickN(m, StandardData.StartupFrames + StandardData.ActiveFrames + StandardData.RecoveryFrames);

        Assert.Equal(MovePhase.Inactive, m.Phase);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // ForceState — unconditional network resync (M4, issue #21)
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ForceState_FromInactive_SetsPhaseRegardlessOfLegality()
    {
        // Active is not reachable directly from Inactive in the legal phase
        // graph, but ForceState must bypass that graph entirely — this is
        // exactly the divergence-repair scenario reconciliation needs.
        var m = NewMachine();

        m.ForceState(MovePhase.Active, frameInPhase: 1, TestMove());

        Assert.Equal(MovePhase.Active, m.Phase);
    }

    [Fact]
    public void ForceState_SetsFrameInPhase()
    {
        var m = NewMachine();

        m.ForceState(MovePhase.Startup, frameInPhase: 3, TestMove());

        Assert.Equal(3, m.FrameInPhase);
    }

    [Fact]
    public void ForceState_SetsCurrentMove()
    {
        var m = NewMachine();
        var move = TestMove();

        m.ForceState(MovePhase.Startup, frameInPhase: 0, move);

        Assert.Same(move, m.CurrentMove);
    }

    [Fact]
    public void ForceState_ToInactive_ClearsCurrentMove()
    {
        // Simulates a client whose local prediction lagged the server: the
        // server says the move already completed back to Inactive.
        var m = NewMachine();
        m.Begin(TestMove());

        m.ForceState(MovePhase.Inactive, frameInPhase: 0, move: null);

        Assert.Null(m.CurrentMove);
    }

    [Fact]
    public void ForceState_ToActive_JustEnteredActiveIsFalse()
    {
        // A forced resync is the machine catching up to where the server
        // already is, not "entering Active this tick" — JustEnteredActive
        // must stay false even when phase == Active, so the caller doesn't
        // incorrectly re-fire the one-shot Active effect (e.g. the burst).
        var m = NewMachine();

        m.ForceState(MovePhase.Active, frameInPhase: 1, TestMove());

        Assert.False(m.JustEnteredActive);
    }

    [Fact]
    public void ForceState_OverwritesPriorLocalPrediction()
    {
        // Local prediction ran ahead (Begin + a few ticks); the server
        // disagrees (still Inactive, e.g. Begin() was rejected server-side
        // because the server's copy was still in Recovery). ForceState must
        // win over whatever the local machine predicted.
        var m = NewMachine();
        m.Begin(TestMove());
        TickN(m, 2);

        m.ForceState(MovePhase.Inactive, frameInPhase: 0, move: null);

        Assert.Equal(MovePhase.Inactive, m.Phase);
        Assert.Null(m.CurrentMove);
    }

    [Fact]
    public void ForceState_NonInactivePhaseWithNullMove_NormalizesToInactive()
    {
        // Doubt cycle 1, finding #6: a non-Inactive phase with no move is
        // nonsensical (Tick() dereferences CurrentMove! unconditionally
        // whenever Phase != Inactive) and would crash the physics loop on the
        // next Tick(). ForceState must defend against this combination rather
        // than trust the caller — normalizing to Inactive instead of crashing.
        var m = NewMachine();

        m.ForceState(MovePhase.Active, frameInPhase: 1, move: null);

        Assert.Equal(MovePhase.Inactive, m.Phase);
        Assert.Null(m.CurrentMove);
    }

    // ═════════════════════════════════════════════════════════════════════
    // ShouldForceInactive — reconciliation gate (M4, issue #21, doubt cycle 2)
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void ShouldForceInactive_ServerInactiveClientInStartup_ReturnsFalse()
    {
        // Startup grace window: a transient one-RTT confirmation delay is
        // expected on every legitimate move attempt and has no visible
        // effect yet (Velocity is zero during Startup either way).
        bool result = CommittedMoveMachine.ShouldForceInactive(
            localPhase: MovePhase.Startup, localIsActive: true, serverPhase: MovePhase.Inactive);

        Assert.False(result);
    }

    [Fact]
    public void ShouldForceInactive_ServerInactiveClientInActive_ReturnsTrue()
    {
        // The real punish-window-violation case: the client's Begin() was
        // rejected server-side (still mid-Recovery from a prior move) but the
        // client predicted ahead into Active anyway.
        bool result = CommittedMoveMachine.ShouldForceInactive(
            localPhase: MovePhase.Active, localIsActive: true, serverPhase: MovePhase.Inactive);

        Assert.True(result);
    }

    [Fact]
    public void ShouldForceInactive_ServerInactiveClientInRecovery_ReturnsTrue()
    {
        bool result = CommittedMoveMachine.ShouldForceInactive(
            localPhase: MovePhase.Recovery, localIsActive: true, serverPhase: MovePhase.Inactive);

        Assert.True(result);
    }

    [Fact]
    public void ShouldForceInactive_ServerActiveClientInActive_ReturnsFalse()
    {
        // Both sides agree a move is running — FrameInPhase is deliberately
        // not compared (doubt cycle 1, finding #2), so no correction fires
        // even though the broadcast is structurally stale.
        bool result = CommittedMoveMachine.ShouldForceInactive(
            localPhase: MovePhase.Active, localIsActive: true, serverPhase: MovePhase.Active);

        Assert.False(result);
    }

    [Fact]
    public void ShouldForceInactive_ServerInactiveClientInactive_ReturnsFalse()
    {
        // Nothing to correct — both already agree.
        bool result = CommittedMoveMachine.ShouldForceInactive(
            localPhase: MovePhase.Inactive, localIsActive: false, serverPhase: MovePhase.Inactive);

        Assert.False(result);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Feint with FeintRecoveryFrames == 0 — regression guard (existing behavior)
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Feint_WithFeintRecoveryZero_RoutesToInactiveImmediately()
    {
        // Regression guard: Crossover/Hesitation (feintRecoveryFrames == 0) must
        // still abort straight to Inactive — no recovery ticks. The new routing
        // logic in Feint() must not affect the default path.
        var fd = new MoveFrameData(startupFrames: 6, activeFrames: 3, recoveryFrames: 10,
            feintWindowFrames: 4, feintRecoveryFrames: 0);
        var m = NewMachine();
        m.Begin(TestMove(fd));
        m.Tick(); // FrameInPhase = 1 (within window)

        m.Feint();

        Assert.Equal(MovePhase.Inactive, m.Phase);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Feint with FeintRecoveryFrames > 0 — pump-fake routing (#77)
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Feint_WithFeintRecovery_RoutesToRecovery()
    {
        // A feint on a move with feintRecoveryFrames > 0 should land in Recovery,
        // not Inactive — the player still has to eat the shortened recovery cost.
        var fd = new MoveFrameData(startupFrames: 18, activeFrames: 4, recoveryFrames: 20,
            feintWindowFrames: 12, feintRecoveryFrames: 8);
        var m = NewMachine();
        m.Begin(new JumpShot(fd));
        m.Tick(); // FrameInPhase = 1 (within 12-frame window)

        m.Feint();

        Assert.Equal(MovePhase.Recovery, m.Phase);
    }

    [Fact]
    public void Feint_WithFeintRecovery_FrameInPhaseIsPreAdvanced()
    {
        // The machine enters Recovery at offset (RecoveryFrames - FeintRecoveryFrames)
        // so Tick()'s existing threshold check (FrameInPhase >= RecoveryFrames) fires
        // after exactly FeintRecoveryFrames more ticks — no new phase or wire state needed.
        var fd = new MoveFrameData(startupFrames: 18, activeFrames: 4, recoveryFrames: 20,
            feintWindowFrames: 12, feintRecoveryFrames: 8);
        var m = NewMachine();
        m.Begin(new JumpShot(fd));
        m.Tick();

        m.Feint();

        // Should be RecoveryFrames - FeintRecoveryFrames = 20 - 8 = 12.
        Assert.Equal(fd.RecoveryFrames - fd.FeintRecoveryFrames, m.FrameInPhase);
    }

    [Fact]
    public void Feint_WithFeintRecovery_ExitsAfterExactlyFeintRecoveryFramesTicks()
    {
        // After the feint lands in pre-advanced Recovery, exactly FeintRecoveryFrames
        // Tick() calls should return the machine to Inactive — not one sooner.
        var fd = new MoveFrameData(startupFrames: 18, activeFrames: 4, recoveryFrames: 20,
            feintWindowFrames: 12, feintRecoveryFrames: 8);
        var m = NewMachine();
        m.Begin(new JumpShot(fd));
        m.Tick();
        m.Feint();

        // One tick short of the threshold — must still be in Recovery.
        TickN(m, fd.FeintRecoveryFrames - 1);
        Assert.Equal(MovePhase.Recovery, m.Phase);

        // The final tick must cross the threshold.
        m.Tick();
        Assert.Equal(MovePhase.Inactive, m.Phase);
    }

    [Fact]
    public void Feint_WithFeintRecovery_JustEnteredActiveNeverSetThroughoutSequence()
    {
        // A feint must NEVER set JustEnteredActive — the ball release is gated on
        // that flag, so a pump-fake that triggers a release would be broken.
        // Assert it stays false on every tick from the feint through return to Inactive.
        var fd = new MoveFrameData(startupFrames: 18, activeFrames: 4, recoveryFrames: 20,
            feintWindowFrames: 12, feintRecoveryFrames: 8);
        var m = NewMachine();
        m.Begin(new JumpShot(fd));
        m.Tick();
        m.Feint(); // enters pre-advanced Recovery

        for (int i = 0; i < fd.FeintRecoveryFrames; i++)
        {
            Assert.False(m.JustEnteredActive, $"JustEnteredActive was true on tick {i} of feint recovery");
            m.Tick();
        }

        Assert.Equal(MovePhase.Inactive, m.Phase);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Reconciliation: feint-recovery state is reconstructable from Phase + FrameInPhase
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ForceState_ToFeintRecoveryOffset_ExitsAfterExactlyFeintRecoveryFramesTicks()
    {
        // (Doubt-driven test #10) A feint-recovery landing point — Phase=Recovery,
        // FrameInPhase = RecoveryFrames - FeintRecoveryFrames — must be
        // reconstructable via ForceState() alone, with no extra wire state.
        // This proves the server can broadcast (Recovery, offset) and the client
        // will snap to the correct remaining duration.
        var fd = new MoveFrameData(startupFrames: 18, activeFrames: 4, recoveryFrames: 20,
            feintWindowFrames: 12, feintRecoveryFrames: 8);
        var move = new JumpShot(fd);
        var m = NewMachine();

        int feintOffset = fd.RecoveryFrames - fd.FeintRecoveryFrames; // 12
        m.ForceState(MovePhase.Recovery, frameInPhase: feintOffset, move);

        TickN(m, fd.FeintRecoveryFrames - 1);
        Assert.Equal(MovePhase.Recovery, m.Phase);

        m.Tick();
        Assert.Equal(MovePhase.Inactive, m.Phase);
    }

    [Fact]
    public void ShouldForceInactive_ClientInRecoveryFromFeint_ServerInactive_ReturnsTrue()
    {
        // (Doubt-driven test #11) If the client predicted a feint into Recovery but
        // the server rejected it and is already Inactive, the existing reconciliation
        // logic must correctly snap the client back. ShouldForceInactive checks
        // (localPhase != Startup && serverPhase == Inactive) — Recovery satisfies this,
        // so the existing code already covers the feint-recovery mispredict case.
        // This test documents that invariant explicitly.
        bool result = CommittedMoveMachine.ShouldForceInactive(
            localPhase: MovePhase.Recovery, localIsActive: true, serverPhase: MovePhase.Inactive);

        Assert.True(result);
    }
}
