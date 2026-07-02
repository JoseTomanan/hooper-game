using System;
using Godot;

namespace Hooper.Ball;

/// <summary>
/// Deterministic hand-authored dribble oscillation for the Dribbling ball state.
///
/// ── Why hand-authored and not a physics solver? ──────────────────────────
/// ADR-0004 mandates that all ball motion be deterministic across every peer.
/// A physics solver would produce emergent, potentially non-deterministic
/// bounce behaviour. This class instead maps a normalised phase [0, 1] to a
/// height via a smooth cosine curve, giving the artist explicit control over
/// the "feel" of the dribble while guaranteeing bit-identical results on
/// server and every client.
///
/// ── Phase convention ─────────────────────────────────────────────────────
///   Phase 0.0 = ball at hand height (top of bounce, just caught/released)
///   Phase 0.5 = ball at floor height (bottom of bounce, contact point)
///   Phase 1.0 = wraps to 0.0 (one full cycle complete)
///
/// ── Caller contract ──────────────────────────────────────────────────────
/// Each physics tick BallController should call Advance(dt) and then
/// GetBallPosition(holderWorldPos) to obtain the ball's world position.
/// The holder's world-space position is injected here (not stored) so this
/// class remains a pure value object with no engine references.
///
/// ── Tunables ─────────────────────────────────────────────────────────────
///   HandHeight   — Y of the player's hand / release / catch point (metres).
///   Period       — duration of one full down-and-up cycle (seconds).
///
/// ── No engine I/O, no DateTime, no Random ────────────────────────────────
/// Trajectory is a pure function of (initial conditions, accumulated fixed
/// steps). Same inputs always produce the same outputs.
/// </summary>
public sealed class DribbleCycle
{
    // ── Tunables ──────────────────────────────────────────────────────────

    /// <summary>
    /// Height of the player's hand above the ground plane (metres).
    /// The ball reaches this Y at phase 0 / 1 (top of the bounce cycle).
    /// </summary>
    public float HandHeight { get; }

    /// <summary>
    /// Duration of one complete dribble cycle — from hand down to floor and
    /// back up — in seconds.
    /// </summary>
    public float Period { get; }

    // ── State ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Current normalised cycle phase in [0, 1).
    ///   0 = top (hand height)
    ///   0.5 = bottom (floor)
    ///   1 wraps to 0.
    /// </summary>
    public float Phase { get; private set; }

    // ── Constructor ───────────────────────────────────────────────────────

    /// <summary>
    /// Creates a dribble cycle with explicit tunables.
    /// Phase starts at 0 (ball at hand height, start of a new dribble).
    /// </summary>
    /// <param name="handHeight">Y of the player's hand (metres). Default 1 m.</param>
    /// <param name="period">Full cycle duration (seconds). Default 0.6 s.</param>
    public DribbleCycle(float handHeight = 1.0f, float period = 0.6f)
    {
        HandHeight = handHeight;
        Period     = period;
        Phase      = 0.0f;
    }

    // ── Phase advance ─────────────────────────────────────────────────────

    /// <summary>
    /// Advances the dribble cycle by one physics tick of <paramref name="dt"/>
    /// seconds. Call once per physics tick before reading position.
    ///
    /// Uses modulo wrap so the phase stays in [0, 1) regardless of how many
    /// ticks accumulate — no floating-point drift from repeated addition because
    /// a fmod is applied each tick rather than comparing against an accumulator.
    /// </summary>
    /// <param name="dt">Elapsed time this tick (seconds). Must be ≥ 0.</param>
    public void Advance(float dt)
    {
        // Normalise dt to a phase increment (dt / Period) and wrap to [0, 1).
        // MathF.IEEERemainder isn't used here because it returns values in
        // [-0.5, 0.5]; a simple modulo is clearer and always non-negative.
        Phase = (Phase + dt / Period) % 1.0f;
    }

    /// <summary>
    /// Restarts the dribble cycle at Phase 0 (ball at hand height) — the same
    /// state a brand-new <see cref="DribbleCycle"/> begins in.
    ///
    /// ── Why this exists (issue #176) ─────────────────────────────────────
    /// Per real half-court 1v1 rules, EVERY change of possession — tipoff, a
    /// live rebound, a made-basket award, or a defensive steal/block recovery
    /// — starts a fresh dribble; a dribble legally ends the instant the ball
    /// leaves the previous holder's control. Before this method existed,
    /// <c>BallController</c> constructed one <c>DribbleCycle</c> in
    /// <c>_Ready()</c> and never reset it, so <see cref="Phase"/> stayed
    /// frozen at whatever value existed when the ball last went Loose. That
    /// let a defender who forced a scramble, then recovered the loose ball,
    /// re-attempt a steal against their OWN frozen phase — if it happened to
    /// still sit inside the steal-exposed band, the re-steal could resolve
    /// with no genuine timing read (ADR-0014 call recorded on #176).
    ///
    /// <c>BallController.AwardPossession</c> calls this on every award path
    /// (rebound, steal/block recovery, OOB turnover, make-it-take-it), so no
    /// possession change can ever inherit a stale phase from the previous
    /// holder's cycle. Resetting to exactly 0 — not merely "outside the
    /// band" — mirrors the constructor default: a new dribble always starts
    /// at the top, ball in hand, matching real-ball rules rather than
    /// introducing a new tunable magnitude (that would be #104's tuning
    /// surface, not this correctness fix's).
    /// </summary>
    public void Reset()
    {
        Phase = 0.0f;
    }

    // ── Spatial output ────────────────────────────────────────────────────

    /// <summary>
    /// Returns the ball's world-space position given the holder's current
    /// world-space position.
    ///
    /// X and Z match the holder exactly (the ball stays directly below the hand
    /// on the ground plane). Y is determined by HeightAtPhase(Phase).
    ///
    /// The holder position is a parameter, not stored state, so this method
    /// is a pure function — safe to call speculatively during reconciliation.
    /// </summary>
    /// <param name="holderWorldPos">Holder's current world-space position.</param>
    /// <returns>Ball's world-space position for this tick.</returns>
    public Vector3 GetBallPosition(Vector3 holderWorldPos)
    {
        return new Vector3(holderWorldPos.X, HeightAtPhase(Phase), holderWorldPos.Z);
    }

    // ── Height curve ──────────────────────────────────────────────────────

    /// <summary>
    /// Maps a normalised phase [0, 1] to a ball height in metres.
    ///
    /// Uses a cosine curve:
    ///   height = HandHeight * (cos(phase * 2π) + 1) / 2
    ///
    /// This gives:
    ///   phase 0.0 → HandHeight   (top)
    ///   phase 0.5 → 0            (floor)
    ///   phase 1.0 → HandHeight   (top, wraps)
    ///
    /// The cosine shape is deliberately chosen over a linear sawtooth because
    /// it produces smooth acceleration into and out of the floor contact,
    /// matching the visual rhythm of a real basketball bounce.
    ///
    /// Floor height is implicitly 0 (the ground plane). If the court geometry
    /// ever changes to a non-zero floor Y, this can be extended with a
    /// floorOffset parameter — but for M2 the court is always Y=0.
    /// </summary>
    /// <param name="phase">Normalised phase [0, 1].</param>
    /// <returns>Ball height in metres above the ground plane.</returns>
    public float HeightAtPhase(float phase)
    {
        // Cosine-based smooth oscillation.
        // cos(0) = 1 → (1+1)/2 = 1 → HandHeight (top)
        // cos(π) = -1 → (-1+1)/2 = 0 → 0 (floor)
        float t = (MathF.Cos(phase * 2.0f * MathF.PI) + 1.0f) * 0.5f;
        return HandHeight * t;
    }
}
