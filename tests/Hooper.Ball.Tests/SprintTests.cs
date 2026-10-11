using Godot;
using Hooper.Player;

namespace Hooper.Ball.Tests;

public class SprintTests
{
    [Fact]
    public void SprintShotCostsMoreScatterThanJogWithoutChangingJogPenalty()
    {
        Assert.Equal(1.8f, ShotMovement.Factor(6f, 6f, 9f, 0.8f), 5);
        Assert.Equal(2.2f, ShotMovement.Factor(9f, 6f, 9f, 0.8f), 5);
        Assert.Equal(2.2f, ShotMovement.Factor(90f, 6f, 9f, 0.8f), 5);
    }
    [Fact]
    public void AcknowledgedSprintIsPrunedButPendingSprintKeepsItsHistoricalPace()
    {
        var buffer = new PredictionBuffer();
        buffer.Record(Vector2.Up, true);
        buffer.Record(Vector2.Right, false);
        buffer.Record(Vector2.Down, true);
        buffer.Acknowledge(1);
        var remaining = buffer.ReplayInputs().ToArray();
        Assert.Equal(new[] { false, true }, remaining.Select(input => input.Sprint));
        Assert.Equal(new[] { Vector2.Right, Vector2.Down }, remaining.Select(input => input.Movement));
    }
}
