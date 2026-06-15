namespace Hooper.Ball;

/// <summary>
/// The four exclusive states a basketball can occupy.
///
/// These map directly to recognisable basketball moments so that anyone
/// watching the game can mentally anchor to the current state:
///
///   Held      — the ball is attached to a player's hand; no physics ticks.
///               The player IS the ball's position until they dribble or shoot.
///
///   Dribbling — the ball is in a dribble cycle (bouncing between the player's
///               hand and the floor). The mini-physics layer drives the bounce
///               curve; the ball is still "controlled" by the player.
///
///   InFlight  — the ball is on a shot or pass arc. The mini-physics layer
///               runs the arc calculation. No player controls the ball.
///
///   Loose     — the ball is uncontrolled on the floor (missed shot, knocked
///               away, turnover). The mini-physics layer rolls/slides it;
///               either player may pick it up.
///
/// IMPORTANT: transitions are NEVER implicit. The state machine requires an
/// explicit method call for every edge. If you add a new state, add explicit
/// transition methods too — do not let the state drift on its own.
/// </summary>
public enum BallState
{
    Held,
    Dribbling,
    InFlight,
    Loose,
}
