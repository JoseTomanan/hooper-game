using System;
using System.Collections.Generic;

namespace Hooper.Systems;

/// <summary>
/// Pure C# scoring and win-condition rules for a 1v1 half-court game
/// (ADR-0002, issues #24/#25). No Godot Node inheritance, no engine
/// singletons, no Random, no DateTime — and unlike BallStateMachine /
/// RimBackboard it doesn't even need Godot value types (Vector3 etc.), so it
/// carries no GodotSharp dependency at all.
///
/// ── Authority model ──────────────────────────────────────────────────────
/// Per ADR-0002 the SERVER owns the single live instance of this class as
/// ground truth. Clients never run their own Scoreboard logic — they only
/// display a broadcast mirror of its state (ScoreOf per peer, IsGameOver,
/// WinnerPeerId). This class is deterministic and side-effect free beyond
/// its own fields, so the server can run it directly off ball-make events
/// without any networking concerns leaking in here.
///
/// ── Rules modelled ───────────────────────────────────────────────────────
/// Both players shoot at the same hoop (half-court 1v1). Every made basket
/// is worth PointsPerBasket to the SHOOTER. First player to reach or exceed
/// TargetScore wins immediately — there is no "win by 2" requirement at this
/// milestone. Once the game is over, the result is final: any further
/// RegisterBasket() call (including for the winner) is a no-op. This matters
/// because a make can be detected by ball/rim resolution on the very same
/// tick the game already ended (e.g. two simultaneous makes resolved in the
/// same physics step) — the FIRST one to register decides the match.
///
/// ── Peer ID convention ───────────────────────────────────────────────────
/// Mirrors BallStateMachine / NetworkManager: peer ids are assigned by
/// Godot's multiplayer API starting at 1. 0 always means "nobody" and is
/// never a valid scorer — RegisterBasket(0) is silently ignored.
///
/// ── Why throw in the constructor but return silently elsewhere? ─────────
/// BallStateMachine's per-tick transition methods return bool instead of
/// throwing, because an exception inside _PhysicsProcess crashes the whole
/// game loop. RegisterBasket() follows that same discipline for the same
/// reason (it's driven by per-tick rim resolution) — invalid calls (zero
/// peer, post-game-over) are silently ignored rather than throwing.
/// The constructor is different: it runs once, outside any per-tick path,
/// when the server sets up a match. A target score ≤ 0 is a setup bug, not
/// a runtime event, so failing loudly and immediately (ArgumentException)
/// is safer than running an entire match with a broken target.
/// </summary>
public sealed class Scoreboard
{
    // ── Tunables ──────────────────────────────────────────────────────────

    /// <summary>
    /// First player to reach or exceed this score wins. Set at construction;
    /// immutable thereafter — changing the win condition mid-match would be
    /// a rules change the server has no business making once play has begun.
    /// </summary>
    public int TargetScore { get; }

    /// <summary>
    /// Points awarded to the shooter for each made basket. Defaults to 1.
    /// Mutable (not just construction-time) in case a future milestone wants
    /// to tune scoring live (e.g. a "deep shot" worth more) — but nothing in
    /// this milestone changes it after construction.
    /// </summary>
    public int PointsPerBasket { get; set; } = 1;

    // ── State ─────────────────────────────────────────────────────────────

    // Peer → score. A Dictionary (not two hardcoded fields) because the 1v1
    // player cap is NetworkManager's concern, not this class's — modelling
    // it generally here keeps Scoreboard correct even if a future milestone
    // changes the player count.
    private readonly Dictionary<int, int> _scores = new();

    /// <summary>
    /// True once any player has reached or exceeded TargetScore. Once true,
    /// it never reverts to false — the match is final.
    /// </summary>
    public bool IsGameOver { get; private set; }

    /// <summary>
    /// Peer ID of the winning player, or 0 if the game is not yet over.
    /// 0 is never a valid peer id (see class doc), so it doubles cleanly as
    /// "no winner yet."
    /// </summary>
    public int WinnerPeerId { get; private set; }

    // ── Constructor ───────────────────────────────────────────────────────

    /// <summary>
    /// Creates a Scoreboard for a match ending at <paramref name="targetScore"/>.
    /// </summary>
    /// <param name="targetScore">
    /// Score a player must reach or exceed to win. Must be positive — see
    /// class doc for why this throws instead of returning a failure code.
    /// </param>
    /// <exception cref="ArgumentException">targetScore is zero or negative.</exception>
    public Scoreboard(int targetScore = 11)
    {
        if (targetScore <= 0)
            throw new ArgumentException($"targetScore must be positive, got {targetScore}.", nameof(targetScore));

        TargetScore = targetScore;
    }

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the given peer's current score, or 0 if they have never scored.
    /// </summary>
    public int ScoreOf(int peerId) => _scores.TryGetValue(peerId, out int score) ? score : 0;

    /// <summary>
    /// Records a made basket for <paramref name="scorerPeerId"/>, awarding
    /// PointsPerBasket. Ignored (no-op) if:
    ///   - the game is already over (the result is final — see class doc), or
    ///   - scorerPeerId is 0 ("nobody," never a valid scorer by convention).
    /// Sets IsGameOver and WinnerPeerId the instant a score reaches or
    /// exceeds TargetScore.
    /// </summary>
    public void RegisterBasket(int scorerPeerId)
    {
        if (IsGameOver) return;
        if (scorerPeerId == 0) return;

        int newScore = ScoreOf(scorerPeerId) + PointsPerBasket;
        _scores[scorerPeerId] = newScore;

        if (newScore >= TargetScore)
        {
            IsGameOver   = true;
            WinnerPeerId = scorerPeerId;
        }
    }
}
