using Godot;
using Hooper.Player;

namespace Hooper.Ball.Tests;

public class ProtectionTests
{
    [Fact]
    public void PendingProtectionRetainsHistoricalEligibilityAfterAcknowledgement()
    {
        var buffer = new PredictionBuffer();
        buffer.Record(Vector2.Up, protect: true);
        buffer.Record(Vector2.Right, protect: false);
        buffer.Record(Vector2.Down, protect: true);
        buffer.Acknowledge(1);
        Assert.Equal(new[] { false, true }, buffer.ReplayInputs().Select(frame => frame.Protect));
    }
}
