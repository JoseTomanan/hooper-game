using Godot;
using Hooper.Moves;

namespace Hooper.Player;

// Observability only: setup and simulation continue through the production
// dispatch, committed behavior, release and prediction interfaces.
public partial class PlayerController
{
    internal float ShotHeadingTargetForHarness =>
        _machine.CurrentMove is JumpShot shot ? shot.TargetHeading : float.NaN;
    internal int ShotHeadingTokenForHarness =>
        _machine.CurrentMove is JumpShot shot ? shot.BeginToken : 0;
    internal Vector3 ShotHeadingContactCorrectionForHarness =>
        _lastContactResponse.Self.Position - _lastContactResponse.Opponent.Position;
    internal Vector3? ShotHeadingContactOpponentForHarness => _lastContactOpponent?.Position;
}
