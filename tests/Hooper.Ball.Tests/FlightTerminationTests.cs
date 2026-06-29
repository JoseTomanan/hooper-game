using Godot;
using Hooper.Ball;

namespace Hooper.Ball.Tests;

/// <summary>
/// Unit tests for FlightTermination — the rule that ends the InFlight state when
/// a shot/pass arc makes NO rim or backboard contact (an air ball, a wide-
/// scattered shot, a long pass). Before this rule existed, TickInFlight only
/// left InFlight on a rim/backboard contact, so a clean miss integrated forever:
/// the ball fell straight through the floor (Y → −∞) or flew through the walls
/// to infinity — the "ball disappears" bug.
///
/// Headless (ADR-0004): pure predicate, no engine.
///
/// ── Test naming convention ───────────────────────────────────────────────
/// [MethodUnderTest]_[Scenario]_[ExpectedOutcome]
/// </summary>
public class FlightTerminationTests
{
    // Representative court matching BallController's default exports.
    private static readonly Vector2 Min = new(-4.88f, -1f);
    private static readonly Vector2 Max = new(4.88f, 11.88f);
    private const float BallRadius = 0.12f;

    [Fact]
    public void ShouldGoLoose_BallReachedFloorInBounds_ReturnsTrue()
    {
        // An air ball that has fallen back down to floor level, still inside the
        // court, must end its flight so TickLoose can bounce it and run the
        // rebound contest.
        var pos = new Vector3(1f, BallRadius, 5f);
        Assert.True(FlightTermination.ShouldGoLoose(pos, BallRadius, Min, Max));
    }

    [Fact]
    public void ShouldGoLoose_AirborneInBounds_ReturnsFalse()
    {
        // A normal shot mid-arc — well above the floor, inside the court — must
        // NOT terminate, or every shot would die the instant it left the hand.
        var pos = new Vector3(1f, 3f, 5f);
        Assert.False(FlightTermination.ShouldGoLoose(pos, BallRadius, Min, Max));
    }

    [Fact]
    public void ShouldGoLoose_OutOfBoundsWhileAirborne_ReturnsTrue()
    {
        // A wide-scattered shot or a long pass whose arc carries it over the
        // sideline — still high in the air — is a dead ball: end the flight now
        // so TickLoose's OobResolution awards the turnover, instead of letting it
        // sail away to infinity first.
        var pos = new Vector3(Max.X + 2f, 3f, 5f);
        Assert.True(FlightTermination.ShouldGoLoose(pos, BallRadius, Min, Max));
    }

    [Fact]
    public void ShouldGoLoose_ExactlyOnCourtEdgeAndAirborne_ReturnsFalse()
    {
        // The court line is in-bounds (CourtBounds is inclusive). A ball passing
        // directly above the sideline is still live and still flying — it has
        // neither landed nor crossed out.
        var pos = new Vector3(Max.X, 3f, Max.Y);
        Assert.False(FlightTermination.ShouldGoLoose(pos, BallRadius, Min, Max));
    }
}
