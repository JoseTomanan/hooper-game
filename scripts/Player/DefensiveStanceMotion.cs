using Godot;
using Hooper.Ball;

namespace Hooper.Player;

public partial class PlayerController
{
    // #346: half neutral coverage, quicker finite lateral response. These are
    // provisional modeling defaults from the approved brief, for #238/#173.
    [Export] public float StanceSpeed { get; set; } = 3f; // metres/second
    [Export] public float StanceAccel { get; set; } = 60f; // metres/second squared
    [Export] public float StanceDecel { get; set; } = 90f; // metres/second squared
    [Export] public float StanceTurnRateDeg { get; set; } = 360f; // degrees/second
    private bool _effectiveStance, _serverStance;

    public bool IsInDefensiveStance => _effectiveStance && _requestedProtect
        && StanceEligible() && !_pivot.HasLatch;
    public bool DisplayDefensiveStance => !IsServer && !IsLocalPlayer
        ? _serverStance : IsInDefensiveStance;

    // Caller samples gameplay context, never cosmetic transforms. The server
    // computes eligibility independently; SubmitInput carries only the request.
    private bool StanceEligible()
    {
        var ball = GetBall();
        if (_machine.IsActive || ball == null || ball.State is not (BallState.Held or BallState.Dribbling))
            return false;
        var other = FindContactPlayer(automaticOnly: false);
        return other != null && other.OwnPeerId != 0
            && ball.StateMachine.HolderPeerId == other.OwnPeerId;
    }
}
