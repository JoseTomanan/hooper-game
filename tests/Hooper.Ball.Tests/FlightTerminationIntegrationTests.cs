using Godot;
using Hooper.Ball;

namespace Hooper.Ball.Tests;

/// <summary>
/// Regression test for the "ball disappears" bug (issue #63 follow-up).
///
/// Reproduces BallController.TickInFlight's per-tick loop against the real pure
/// pieces (ShotArc + RimBackboard + FlightTermination) and proves that a shot
/// arc making NO rim/backboard contact now TERMINATES — instead of integrating
/// forever and sinking through the floor / sailing through the walls.
///
/// TickInFlight itself extends a Godot Node and cannot run headless (ADR-0004),
/// so — exactly as ShotMakeCurveTests does — we replicate its loop here over the
/// pure collaborators. The only line under test that differs from the old code
/// is the FlightTermination.ShouldGoLoose check: the BUG case below omits it and
/// shows the ball is uncontained; the FIX case includes it and shows the ball
/// terminates and stays contained.
/// </summary>
public class FlightTerminationIntegrationTests
{
    private const float Dt         = 1f / 60f;
    private const float RimRadius  = 0.23f;
    private const float BallRadius = 0.12f;
    private const float Apex       = 4.0f;
    private const float Gravity    = 9.8f;
    private const float HandHeight = 1.0f;
    private static readonly Vector3 RimCenter   = new(0, 3.05f, 0);
    private static readonly Vector3 BoardCenter = new(0, 3.5f, 0.3f);
    private static readonly Vector3 BoardNormal = new(0, 0, -1);

    // Court matching BallController's default exports, both of which derive
    // from CourtBounds.Default{Min,Max} (single source of truth).
    private static readonly Vector2 CourtMin = CourtBounds.DefaultMin;
    private static readonly Vector2 CourtMax = CourtBounds.DefaultMax;

    private static RimBackboard MakeRim() => new(
        RimCenter, RimRadius, BallRadius, 0.65f,
        BoardCenter, BoardNormal, 0.46f, 0.30f, 0.65f);

    /// <summary>
    /// An air ball aimed well wide of the rim, released from in front of the
    /// basket. It never contacts rim or backboard — the case the old TickInFlight
    /// could not terminate.
    /// </summary>
    private static ShotArc MakeAirBallArc()
    {
        // Aim 3 m to the side of the rim and short of it: the arc passes nowhere
        // near the rim ring, so RimBackboard.Resolve returns None every tick.
        var release = new Vector3(0f, HandHeight, -4f);
        var target  = new Vector3(3f, RimCenter.Y, 2f);
        return new ShotArc(release, target, Apex, Gravity);
    }

    [Fact]
    public void AirBall_WithTerminationCheck_GoesLooseAndStaysContained()
    {
        ShotArc arc = MakeAirBallArc();
        RimBackboard rim = MakeRim();

        bool wentLoose = false;
        int  endTick   = -1;
        for (int tick = 0; tick < 600; tick++)
        {
            arc.Step(Dt);
            ContactResult r = rim.Resolve(arc);
            if (r == ContactResult.Bounce || r == ContactResult.Make)
            {
                wentLoose = true; endTick = tick; break;
            }
            // The fix: terminate flight on floor-contact or OOB.
            if (FlightTermination.ShouldGoLoose(arc.Position, BallRadius, CourtMin, CourtMax))
            {
                wentLoose = true; endTick = tick; break;
            }
        }

        Assert.True(wentLoose, "air ball must leave InFlight");
        // Terminated promptly — a routine arc lands in ~1–2 s (< 120 ticks),
        // nowhere near the 600-tick (10 s) safety ceiling.
        Assert.InRange(endTick, 0, 200);
        // And it was caught AT the floor, not metres below it.
        Assert.True(arc.Position.Y >= -BallRadius,
            $"ball should terminate at the floor, was at Y={arc.Position.Y}");
    }

    [Fact]
    public void AirBall_WithoutTerminationCheck_IsUncontained()
    {
        // Characterizes the BUG: the old loop (RimBackboard.Resolve only) never
        // terminates an air ball — after the full 10 s safety window the ball has
        // fallen far below the floor, demonstrating the runaway integration that
        // looked like the ball "disappearing."
        ShotArc arc = MakeAirBallArc();
        RimBackboard rim = MakeRim();

        for (int tick = 0; tick < 600; tick++)
        {
            arc.Step(Dt);
            ContactResult r = rim.Resolve(arc);
            if (r == ContactResult.Bounce || r == ContactResult.Make) break;
            // NB: no FlightTermination check here — this is the pre-fix behaviour.
        }

        Assert.True(arc.Position.Y < -10f,
            $"pre-fix: air ball runs away below the floor (was Y={arc.Position.Y})");
    }

    [Fact]
    public void WideScatterShot_CrossesSidelineAirborne_GoesLoose()
    {
        // A shot scattered well past the sideline must terminate (for the OOB
        // turnover) rather than fly out of the arena.
        var release = new Vector3(0f, HandHeight, -3f);
        var target  = new Vector3(CourtMax.X + 5f, RimCenter.Y, 1f);
        var arc = new ShotArc(release, target, Apex, Gravity);
        RimBackboard rim = MakeRim();

        bool wentLoose = false;
        for (int tick = 0; tick < 600; tick++)
        {
            arc.Step(Dt);
            ContactResult r = rim.Resolve(arc);
            if (r == ContactResult.Bounce || r == ContactResult.Make) { wentLoose = true; break; }
            if (FlightTermination.ShouldGoLoose(arc.Position, BallRadius, CourtMin, CourtMax))
            {
                wentLoose = true;
                // Confirm it was the OOB branch that fired while still airborne,
                // not a floor landing inside the court.
                Assert.True(CourtBounds.IsOutOfBounds(arc.Position, CourtMin, CourtMax));
                break;
            }
        }

        Assert.True(wentLoose, "wide-scattered shot must leave InFlight via OOB");
    }
}
