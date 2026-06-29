using Hooper.Ball;

namespace Hooper.Ball.Tests;

/// <summary>
/// Unit tests for BallStateMachine — the pure C# class that drives the ball's
/// state transitions (ADR-0004).
///
/// These tests run without a live Godot instance.  That is the point: proving
/// that the state logic is isolated from the engine so it can be verified
/// deterministically in CI.
///
/// ── Test naming convention ───────────────────────────────────────────────
/// [MethodUnderTest]_[Scenario]_[ExpectedOutcome]
///
/// Each test contains exactly one logical assertion.  Multiple Assert lines
/// are only present when they form one indivisible logical check
/// (e.g., state AND return value from the same transition call).
///
/// ── Transition graph under test ──────────────────────────────────────────
/// Legal edges:
///   Held       → Dribbling  : StartDribble()   → returns true
///   Held       → InFlight   : Shoot()           → returns true
///   Dribbling  → Held       : StopDribble()     → returns true
///   Dribbling  → InFlight   : Shoot()           → returns true
///   Dribbling  → Loose      : GoLoose()         → returns true
///   InFlight   → Loose      : GoLoose()         → returns true
///   InFlight   → Held       : Catch(peerId)     → returns true
///   Loose      → Held       : Catch(peerId)     → returns true
///
/// All other edges return false and leave state unchanged.
/// </summary>
public class BallStateMachineTests
{
    // ── Factory helper ────────────────────────────────────────────────────

    /// <summary>Returns a freshly constructed machine (starts Held, holder = 1).</summary>
    private static BallStateMachine NewMachine(int holderId = 1) => new(holderId);

    // ═════════════════════════════════════════════════════════════════════
    // Initial state
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void Constructor_Default_StartsHeld()
    {
        var sm = new BallStateMachine();
        Assert.Equal(BallState.Held, sm.Current);
    }

    [Fact]
    public void Constructor_WithHolderId_RecordsHolder()
    {
        var sm = new BallStateMachine(initialHolderPeerId: 7);
        Assert.Equal(7, sm.HolderPeerId);
    }

    // ═════════════════════════════════════════════════════════════════════
    // StartDribble — Held → Dribbling
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void StartDribble_WhenHeld_ReturnsTrueAndStateIsDribbling()
    {
        var sm = NewMachine();
        bool result = sm.StartDribble();

        Assert.True(result);
        Assert.Equal(BallState.Dribbling, sm.Current);
    }

    [Fact]
    public void StartDribble_WhenDribbling_ReturnsFalse()
    {
        var sm = NewMachine();
        sm.StartDribble(); // Held → Dribbling

        bool result = sm.StartDribble(); // invalid: already Dribbling
        Assert.False(result);
    }

    [Fact]
    public void StartDribble_WhenDribbling_StateUnchanged()
    {
        var sm = NewMachine();
        sm.StartDribble();
        sm.StartDribble(); // invalid attempt

        Assert.Equal(BallState.Dribbling, sm.Current);
    }

    [Fact]
    public void StartDribble_WhenInFlight_ReturnsFalse()
    {
        var sm = NewMachine();
        sm.Shoot(); // Held → InFlight

        bool result = sm.StartDribble();
        Assert.False(result);
    }

    [Fact]
    public void StartDribble_WhenLoose_ReturnsFalse()
    {
        var sm = NewMachine();
        sm.Shoot();   // Held → InFlight
        sm.GoLoose(); // InFlight → Loose

        bool result = sm.StartDribble();
        Assert.False(result);
    }

    // ═════════════════════════════════════════════════════════════════════
    // StopDribble — Dribbling → Held
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void StopDribble_WhenDribbling_ReturnsTrueAndStateIsHeld()
    {
        var sm = NewMachine();
        sm.StartDribble();

        bool result = sm.StopDribble();

        Assert.True(result);
        Assert.Equal(BallState.Held, sm.Current);
    }

    [Fact]
    public void StopDribble_WhenHeld_ReturnsFalse()
    {
        var sm = NewMachine();
        bool result = sm.StopDribble();
        Assert.False(result);
    }

    [Fact]
    public void StopDribble_WhenInFlight_ReturnsFalse()
    {
        var sm = NewMachine();
        sm.Shoot();

        bool result = sm.StopDribble();
        Assert.False(result);
    }

    [Fact]
    public void StopDribble_WhenLoose_ReturnsFalse()
    {
        var sm = NewMachine();
        sm.Shoot();
        sm.GoLoose();

        bool result = sm.StopDribble();
        Assert.False(result);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Shoot — Held or Dribbling → InFlight
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void Shoot_WhenHeld_ReturnsTrueAndStateIsInFlight()
    {
        var sm = NewMachine();
        bool result = sm.Shoot();

        Assert.True(result);
        Assert.Equal(BallState.InFlight, sm.Current);
    }

    [Fact]
    public void Shoot_WhenHeld_ClearsHolder()
    {
        var sm = NewMachine(holderId: 2);
        sm.Shoot();

        Assert.Equal(0, sm.HolderPeerId);
    }

    [Fact]
    public void Shoot_WhenDribbling_ReturnsTrueAndStateIsInFlight()
    {
        var sm = NewMachine();
        sm.StartDribble();

        bool result = sm.Shoot();

        Assert.True(result);
        Assert.Equal(BallState.InFlight, sm.Current);
    }

    [Fact]
    public void Shoot_WhenDribbling_ClearsHolder()
    {
        var sm = NewMachine(holderId: 3);
        sm.StartDribble();
        sm.Shoot();

        Assert.Equal(0, sm.HolderPeerId);
    }

    [Fact]
    public void Shoot_WhenInFlight_ReturnsFalse()
    {
        var sm = NewMachine();
        sm.Shoot();

        bool result = sm.Shoot(); // invalid: already in flight
        Assert.False(result);
    }

    [Fact]
    public void Shoot_WhenLoose_ReturnsFalse()
    {
        var sm = NewMachine();
        sm.Shoot();
        sm.GoLoose();

        bool result = sm.Shoot();
        Assert.False(result);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Catch — InFlight or Loose → Held
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void Catch_WhenInFlight_ReturnsTrueAndStateIsHeld()
    {
        var sm = NewMachine();
        sm.Shoot();

        bool result = sm.Catch(newHolderPeerId: 2);

        Assert.True(result);
        Assert.Equal(BallState.Held, sm.Current);
    }

    [Fact]
    public void Catch_WhenInFlight_SetsNewHolder()
    {
        var sm = NewMachine(holderId: 1);
        sm.Shoot();
        sm.Catch(newHolderPeerId: 2);

        Assert.Equal(2, sm.HolderPeerId);
    }

    [Fact]
    public void Catch_WhenLoose_ReturnsTrueAndStateIsHeld()
    {
        var sm = NewMachine();
        sm.Shoot();
        sm.GoLoose();

        bool result = sm.Catch(newHolderPeerId: 5);

        Assert.True(result);
        Assert.Equal(BallState.Held, sm.Current);
    }

    [Fact]
    public void Catch_WhenLoose_SetsNewHolder()
    {
        var sm = NewMachine(holderId: 1);
        sm.Shoot();
        sm.GoLoose();
        sm.Catch(newHolderPeerId: 5);

        Assert.Equal(5, sm.HolderPeerId);
    }

    [Fact]
    public void Catch_WhenHeld_ReturnsFalse()
    {
        var sm = NewMachine(holderId: 1);
        bool result = sm.Catch(newHolderPeerId: 2);
        Assert.False(result);
    }

    [Fact]
    public void Catch_WhenHeld_HolderUnchanged()
    {
        var sm = NewMachine(holderId: 1);
        sm.Catch(newHolderPeerId: 2); // invalid

        Assert.Equal(1, sm.HolderPeerId);
    }

    [Fact]
    public void Catch_WhenDribbling_ReturnsFalse()
    {
        var sm = NewMachine();
        sm.StartDribble();

        bool result = sm.Catch(newHolderPeerId: 2);
        Assert.False(result);
    }

    // ═════════════════════════════════════════════════════════════════════
    // GoLoose — Dribbling or InFlight → Loose
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void GoLoose_WhenDribbling_ReturnsTrueAndStateIsLoose()
    {
        var sm = NewMachine();
        sm.StartDribble();

        bool result = sm.GoLoose();

        Assert.True(result);
        Assert.Equal(BallState.Loose, sm.Current);
    }

    [Fact]
    public void GoLoose_WhenDribbling_ClearsHolder()
    {
        var sm = NewMachine(holderId: 4);
        sm.StartDribble();
        sm.GoLoose();

        Assert.Equal(0, sm.HolderPeerId);
    }

    [Fact]
    public void GoLoose_WhenInFlight_ReturnsTrueAndStateIsLoose()
    {
        var sm = NewMachine();
        sm.Shoot();

        bool result = sm.GoLoose();

        Assert.True(result);
        Assert.Equal(BallState.Loose, sm.Current);
    }

    [Fact]
    public void GoLoose_WhenInFlight_ClearsHolder()
    {
        var sm = NewMachine(holderId: 2);
        sm.Shoot();
        sm.GoLoose();

        Assert.Equal(0, sm.HolderPeerId);
    }

    [Fact]
    public void GoLoose_WhenHeld_ReturnsFalse()
    {
        var sm = NewMachine();
        bool result = sm.GoLoose();
        Assert.False(result);
    }

    [Fact]
    public void GoLoose_WhenLoose_ReturnsFalse()
    {
        var sm = NewMachine();
        sm.Shoot();
        sm.GoLoose();

        bool result = sm.GoLoose(); // invalid: already loose
        Assert.False(result);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Multi-step sequences — realistic gameplay flows
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void Sequence_HeldDribblingShootGoLooseCatch_EndsHeld()
    {
        // A full possession sequence: player holds, dribbles, shoots, the shot
        // misses (GoLoose), then the other player picks it up (Catch).
        var sm = NewMachine(holderId: 1);
        sm.StartDribble();
        sm.Shoot();
        sm.GoLoose();
        sm.Catch(newHolderPeerId: 2);

        Assert.Equal(BallState.Held, sm.Current);
    }

    [Fact]
    public void Sequence_HeldDribblingShootGoLooseCatch_NewHolderIsCorrect()
    {
        var sm = NewMachine(holderId: 1);
        sm.StartDribble();
        sm.Shoot();
        sm.GoLoose();
        sm.Catch(newHolderPeerId: 2);

        Assert.Equal(2, sm.HolderPeerId);
    }

    [Fact]
    public void Sequence_ShootAndCatch_PlayerCanShootAgain()
    {
        // Pass caught by a player who then immediately shoots.
        var sm = NewMachine(holderId: 1);
        sm.Shoot();          // Held → InFlight (pass)
        sm.Catch(newHolderPeerId: 2); // InFlight → Held
        bool canShoot = sm.Shoot();  // Held → InFlight (shot)

        Assert.True(canShoot);
    }

    [Fact]
    public void Sequence_DribblingKnockedLooseThenPickedUp_ValidFlow()
    {
        // A steal mid-dribble: dribble → loose → caught by opponent.
        var sm = NewMachine(holderId: 1);
        sm.StartDribble();
        sm.GoLoose();
        bool caught = sm.Catch(newHolderPeerId: 2);

        Assert.True(caught);
    }

    // ═════════════════════════════════════════════════════════════════════
    // ForceState — unconditional network resync (M4, issue #20)
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void ForceState_FromHeld_SetsStateRegardlessOfLegality()
    {
        // InFlight is not a legal direct edge from Held in the transition
        // graph, but ForceState must bypass that graph entirely — this is
        // exactly the divergence-repair scenario reconciliation needs.
        var sm = NewMachine(holderId: 1);

        sm.ForceState(BallState.InFlight, holderPeerId: 0);

        Assert.Equal(BallState.InFlight, sm.Current);
    }

    [Fact]
    public void ForceState_FromHeld_SetsHolderRegardlessOfLegality()
    {
        var sm = NewMachine(holderId: 1);

        sm.ForceState(BallState.InFlight, holderPeerId: 0);

        Assert.Equal(0, sm.HolderPeerId);
    }

    [Fact]
    public void ForceState_ToHeldWithNewHolder_OverwritesHolderId()
    {
        // Simulates a client whose local prediction lagged the server: the
        // server says the ball is already Held by peer 2, force-resync.
        var sm = NewMachine(holderId: 1);
        sm.Shoot(); // Held -> InFlight, holder cleared

        sm.ForceState(BallState.Held, holderPeerId: 2);

        Assert.Equal(2, sm.HolderPeerId);
    }

    [Fact]
    public void ForceState_SameStateDifferentHolder_UpdatesHolderOnly()
    {
        var sm = NewMachine(holderId: 1);
        sm.StartDribble();

        sm.ForceState(BallState.Dribbling, holderPeerId: 1);

        Assert.Equal(BallState.Dribbling, sm.Current);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Turnover — Held/Dribbling → Held (by a NEW holder)
    //
    // A dead-ball change of possession (out-of-bounds violation): the ball
    // passes DIRECTLY from the current handler to the opponent without first
    // going loose for a scramble. Distinct from Catch (recovers an InFlight/
    // Loose ball) and GoLoose (makes the ball uncontrolled). Legal only while a
    // player actually holds the ball.
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void Turnover_WhenHeld_ReturnsTrueAndStateIsHeldByNewHolder()
    {
        var sm = NewMachine(holderId: 1);

        bool result = sm.Turnover(newHolderPeerId: 2);

        Assert.True(result);
        Assert.Equal(BallState.Held, sm.Current);
        Assert.Equal(2, sm.HolderPeerId);
    }

    [Fact]
    public void Turnover_WhenDribbling_ReturnsTrueAndStateIsHeldByNewHolder()
    {
        var sm = NewMachine(holderId: 1);
        sm.StartDribble(); // Held → Dribbling

        bool result = sm.Turnover(newHolderPeerId: 2);

        Assert.True(result);
        Assert.Equal(BallState.Held, sm.Current);
        Assert.Equal(2, sm.HolderPeerId);
    }

    [Fact]
    public void Turnover_WhenInFlight_ReturnsFalseAndStateUnchanged()
    {
        // A ball in flight has no handler to take it from — recovery is Catch,
        // not a turnover. Reject so a stray call cannot fabricate a holder.
        var sm = NewMachine(holderId: 1);
        sm.Shoot(); // Held → InFlight, holder cleared

        bool result = sm.Turnover(newHolderPeerId: 2);

        Assert.False(result);
        Assert.Equal(BallState.InFlight, sm.Current);
        Assert.Equal(0, sm.HolderPeerId);
    }

    [Fact]
    public void Turnover_WhenLoose_ReturnsFalseAndStateUnchanged()
    {
        // A loose ball is recovered by Catch (a live scramble), not handed over.
        var sm = NewMachine(holderId: 1);
        sm.StartDribble();
        sm.GoLoose(); // Dribbling → Loose, holder cleared

        bool result = sm.Turnover(newHolderPeerId: 2);

        Assert.False(result);
        Assert.Equal(BallState.Loose, sm.Current);
        Assert.Equal(0, sm.HolderPeerId);
    }
}
