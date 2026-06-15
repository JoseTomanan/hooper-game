namespace Hooper.Ball;

/// <summary>
/// The outcome of a single RimBackboard.Resolve() call.
///
/// ── Why a dedicated enum and not a bool? ────────────────────────────────────
/// The caller (BallController) must distinguish three distinct outcomes to drive
/// the correct state-machine transition and game event:
///
///   None   — No geometry contact this tick.  Caller does nothing.
///
///   Bounce — The ball struck the rim ring or the backboard.  RimBackboard has
///            already reflected velocity and depenetrated position inside the
///            ShotArc.  The caller should call stateMachine.GoLoose() so the
///            ball becomes a loose ball that either player can pursue.
///
///   Make   — The ball passed cleanly downward through the hoop opening (a
///            swish or clean bank that cleared the rim).  Velocity is NOT
///            reflected.  The caller handles scoring separately; it must NOT
///            call GoLoose() on this result — the basket is good, the ball
///            continues through or is whistled dead depending on game rules.
///
/// This three-value contract keeps the physics class free of state-machine
/// knowledge (no BallStateMachine dependency) while giving the consumer all
/// the information it needs for a single method call.
/// </summary>
public enum ContactResult
{
    None,
    Bounce,
    Make,
}
