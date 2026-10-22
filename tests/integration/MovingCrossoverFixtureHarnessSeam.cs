using Godot;

namespace Hooper.Player;

// Read-only evidence for the fixture's cross-transfer-mode completion barrier.
public partial class PlayerController
{
    internal int CrossoverFixtureSentSequence => _buffer.LastSequence;
    internal int CrossoverFixtureReceivedSequence => _serverAckedSeq;
    internal bool CrossoverFixtureNeutralInput => _pendingInput == Vector2.Zero && _pendingRawStick == Vector2.Zero;
}
