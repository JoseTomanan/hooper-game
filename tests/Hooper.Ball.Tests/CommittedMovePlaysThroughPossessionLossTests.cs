using Hooper.Moves;

namespace Hooper.Ball.Tests;

/// <summary>
/// Regression locks for a RATIFIED design behavior: a committed move plays to
/// completion even when an external event (a defensive steal, ADR-0018 #96, or
/// an OOB carry-turnover, ADR-0008 #63/#118) has taken the ball away mid-move
/// and made its payload structurally impossible.
///
/// These began (2026-07-03) as CHARACTERIZATION tests — pinning behavior whose
/// correctness was undecided, found while diagnosing the same reported bug
/// FeintGateResolverTests fixes one instance of (windup animation plays, ball
/// never releases). Issue #189 escalated the "should anything interrupt the
/// move?" question to the human, who ruled (2026-07-20) that the move SHOULD
/// play through: the player is planted for its full duration and loses that
/// time, and the lost time IS the punishment for committing to a move the
/// defender broke up. See the ADR-0003 amendment "External events do not
/// interrupt a committed move (#189)". The behavior below is therefore
/// design-INTENDED, and these tests now lock it against regression rather than
/// documenting an open gap.
///
/// BallController.CheckJumpShotRelease/ApplyShootLocally extend a Godot Node
/// and cannot run headless (ADR-0004), so — exactly as OobShotReleaseTests and
/// FlightTerminationIntegrationTests do — this replicates the decision over
/// the SAME pure collaborators the real wiring composes:
///
///     BallController.TickHeld/TickDribbling resolve `holder` from
///     StateMachine.HolderPeerId EVERY tick, then call
///     CheckJumpShotRelease(holder), which reads holder.JustReleasedJumpShot
///     -- i.e. THAT PLAYER's CommittedMoveMachine, not the machine that
///     actually began the JumpShot.
///
/// CommittedMoveMachine has no Cancel()/Interrupt() by design (ADR-0003 "no
/// flow-cancel"), and the #189 ruling confirms that absence extends to
/// external events too: nothing stops a player's machine from completing a
/// full JumpShot lifecycle (and its windup/active/recovery animation, which
/// reads Phase alone via MoveAnimResolver — see MoveAnimResolverTests) after
/// the ball is gone. The release the machine "fires" is structurally
/// unreachable — BallController resolves `holder` to whoever the ball's
/// CURRENT HolderPeerId is, not the original shooter — so no phantom basket
/// ever scores; only the animation plays.
///
/// This is NOT the same case #120/OobShotReleaseTests covers (OOB at the exact
/// release tick, which correctly voids the shot). This is possession changing
/// at ANY earlier point during Startup/Active, well before release — a much
/// wider window (up to 18 Startup + 4 Active ticks vs. one release tick) and,
/// for the steal case, something that happens in ordinary 1v1 defense far
/// more often than a carry-OOB.
///
/// ── Test naming ──────────────────────────────────────────────────────────────
/// [Scenario]_[ExpectedOutcome]
/// </summary>
public class CommittedMovePlaysThroughPossessionLossTests
{
    private const int Shooter  = 1;
    private const int Opponent = 2;

    [Fact]
    public void PossessionTurnedOverDuringStartup_ShooterMachineStillCompletesFullLifecycle()
    {
        // Shooter begins a JumpShot while holding the ball.
        var shooterMachine = new CommittedMoveMachine();
        var ball = new BallStateMachine(initialHolderPeerId: Shooter);
        MoveFrameData fd = JumpShot.DefaultFrameData;
        shooterMachine.Begin(new JumpShot(fd));

        // A few ticks into Startup, possession is taken away by an EXTERNAL
        // event -- e.g. a carry-OOB turnover (BallController.ResolvePlayerOutOfBounds,
        // which runs every server tick regardless of committed-move phase).
        // A steal (GoLoose, requiring Dribbling first) reaches the identical
        // outcome via a different BallStateMachine edge.
        for (int i = 0; i < 5; i++) shooterMachine.Tick();
        Assert.True(ball.Turnover(Opponent));
        Assert.Equal(Opponent, ball.HolderPeerId);

        // Nothing on the shooter's machine knows the ball is gone -- ADR-0003
        // deliberately has no Cancel()/Interrupt(). Ticking it the rest of the
        // way out completes Startup -> Active -> Recovery -> Inactive exactly
        // as if the shot had gone through normally.
        int remaining = fd.StartupFrames - 5 + fd.ActiveFrames + fd.RecoveryFrames;
        bool everFiredJustEnteredActive = false;
        for (int i = 0; i < remaining; i++)
        {
            shooterMachine.Tick();
            if (shooterMachine.JustEnteredActive) everFiredJustEnteredActive = true;
        }

        Assert.Equal(MovePhase.Inactive, shooterMachine.Phase);
        Assert.True(everFiredJustEnteredActive,
            "the shooter's machine DOES reach Active and fire its one-shot signal " +
            "-- i.e. the windup/active/recovery animation plays in full");

        // But the ball's actual holder never came back to the shooter, so
        // BallController.CheckJumpShotRelease(holder) -- which resolves holder
        // from StateMachine.HolderPeerId, NOT from "whoever began the move" --
        // would have checked the OPPONENT's JustReleasedJumpShot on every one
        // of those ticks. The opponent has no JumpShot running, so the release
        // this animation implies is structurally unreachable.
        Assert.NotEqual(Shooter, ball.HolderPeerId);
    }

    [Fact]
    public void PossessionStolenDuringStartup_ShooterMachineStillCompletesFullLifecycle()
    {
        // Same gap via the steal path (ADR-0018, #96): a defender's swipe
        // during the dribble gather knocks the ball loose mid-Startup -- a far
        // more common 1v1 interaction than a carry-OOB.
        var shooterMachine = new CommittedMoveMachine();
        var ball = new BallStateMachine(initialHolderPeerId: Shooter);
        Assert.True(ball.StartDribble()); // must be Dribbling for GoLoose (the steal edge)
        MoveFrameData fd = JumpShot.DefaultFrameData;
        shooterMachine.Begin(new JumpShot(fd));

        for (int i = 0; i < 5; i++) shooterMachine.Tick();
        Assert.True(ball.GoLoose()); // ResolveStealAttempts' success edge
        Assert.Equal(BallState.Loose, ball.Current);
        Assert.Equal(0, ball.HolderPeerId); // no holder while loose

        int remaining = fd.StartupFrames - 5 + fd.ActiveFrames + fd.RecoveryFrames;
        bool everFiredJustEnteredActive = false;
        for (int i = 0; i < remaining; i++)
        {
            shooterMachine.Tick();
            if (shooterMachine.JustEnteredActive) everFiredJustEnteredActive = true;
        }

        Assert.Equal(MovePhase.Inactive, shooterMachine.Phase);
        Assert.True(everFiredJustEnteredActive);
        // The ball never returned to the shooter during the animation, so the
        // implied release was, again, structurally unreachable.
        Assert.NotEqual(Shooter, ball.HolderPeerId);
    }
}
