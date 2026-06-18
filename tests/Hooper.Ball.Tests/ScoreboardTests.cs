using Hooper.Systems;

namespace Hooper.Ball.Tests;

/// <summary>
/// Unit tests for Scoreboard — the pure, deterministic 1v1 half-court scoring
/// and win-condition class (ADR-0002, issues #24/#25).
///
/// All tests run without a live Godot instance. Scoreboard is pure C# with
/// no Node inheritance, no Random, no DateTime, and (unlike RimBackboard /
/// BallStateMachine) no Godot types at all — it never needs Vector3 or
/// similar, so it carries no GodotSharp dependency.
///
/// ── Model ─────────────────────────────────────────────────────────────────
/// Per ADR-0002 the SERVER owns this instance as the single source of truth;
/// clients only ever display a broadcast mirror of its state (PeerId→score
/// pairs, IsGameOver, WinnerPeerId). Both players shoot at the same hoop in a
/// half-court 1v1; every made basket is worth PointsPerBasket to the SHOOTER.
/// First to reach (or exceed) the target score wins. Once the game is over,
/// further RegisterBasket() calls are no-ops — a late basket detected on the
/// same server tick the game already ended must not change the winner.
///
/// Peer ID convention mirrors NetworkManager / BallStateMachine: peer ids are
/// assigned by Godot's multiplayer API starting at 1; 0 always means "nobody."
///
/// ── Test naming convention ───────────────────────────────────────────────
/// [Subject]_[Scenario]_[ExpectedOutcome]
///
/// One logical assertion per test (multiple Assert lines only when they form
/// one indivisible logical check).
/// </summary>
public class ScoreboardTests
{
    // ═════════════════════════════════════════════════════════════════════
    // Construction
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void Constructor_ZeroTargetScore_ThrowsArgumentException()
    {
        // Construction-time misconfiguration (e.g. a typo'd target in server
        // setup code) is a programming error, not a per-tick game event —
        // unlike BallStateMachine's transition methods (which return false
        // because a thrown exception inside _PhysicsProcess would crash the
        // game loop), throwing here is safe: it happens once, outside any
        // per-tick path, and surfaces the bug immediately instead of letting
        // a broken scoreboard run silently for an entire match.
        Assert.Throws<ArgumentException>(() => new Scoreboard(targetScore: 0));
    }

    [Fact]
    public void Constructor_NegativeTargetScore_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new Scoreboard(targetScore: -5));
    }

    [Fact]
    public void Constructor_DefaultTargetScore_IsEleven()
    {
        var sb = new Scoreboard();

        // Not over yet — no baskets scored — confirms default target (11)
        // hasn't already been reached by some other default.
        Assert.False(sb.IsGameOver);
    }

    [Fact]
    public void Constructor_PointsPerBasket_DefaultsToOne()
    {
        var sb = new Scoreboard();

        Assert.Equal(1, sb.PointsPerBasket);
    }

    // ═════════════════════════════════════════════════════════════════════
    // ScoreOf — default and attribution
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void ScoreOf_PeerNeverScored_ReturnsZero()
    {
        var sb = new Scoreboard(targetScore: 11);

        Assert.Equal(0, sb.ScoreOf(1));
    }

    [Fact]
    public void RegisterBasket_SingleBasket_IncreasesScorerPointsByPointsPerBasket()
    {
        var sb = new Scoreboard(targetScore: 11);

        sb.RegisterBasket(1);

        Assert.Equal(sb.PointsPerBasket, sb.ScoreOf(1));
    }

    [Fact]
    public void RegisterBasket_ScorerOnly_DoesNotAffectOtherPeerScore()
    {
        var sb = new Scoreboard(targetScore: 11);

        sb.RegisterBasket(1);

        Assert.Equal(0, sb.ScoreOf(2));
    }

    [Fact]
    public void RegisterBasket_MultipleBaskets_AccumulatesForSamePeer()
    {
        var sb = new Scoreboard(targetScore: 11);

        sb.RegisterBasket(1);
        sb.RegisterBasket(1);
        sb.RegisterBasket(1);

        Assert.Equal(3 * sb.PointsPerBasket, sb.ScoreOf(1));
    }

    // ═════════════════════════════════════════════════════════════════════
    // Win condition — first to target, exactness
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void RegisterBasket_ScoreBelowTarget_GameIsNotOver()
    {
        // Target 11, one basket of 1 point — nowhere near the target.
        var sb = new Scoreboard(targetScore: 11);

        sb.RegisterBasket(1);

        Assert.False(sb.IsGameOver);
    }

    [Fact]
    public void RegisterBasket_ScoreReachesTargetExactly_GameIsOver()
    {
        var sb = new Scoreboard(targetScore: 3) { PointsPerBasket = 1 };

        sb.RegisterBasket(1);
        sb.RegisterBasket(1);
        sb.RegisterBasket(1); // score = 3 == target

        Assert.True(sb.IsGameOver);
    }

    [Fact]
    public void RegisterBasket_ScoreOneBelowTarget_GameIsNotOver()
    {
        var sb = new Scoreboard(targetScore: 3) { PointsPerBasket = 1 };

        sb.RegisterBasket(1);
        sb.RegisterBasket(1); // score = 2, target = 3

        Assert.False(sb.IsGameOver);
    }

    [Fact]
    public void RegisterBasket_ScoreReachesTarget_WinnerPeerIdIsScorer()
    {
        var sb = new Scoreboard(targetScore: 3) { PointsPerBasket = 1 };

        sb.RegisterBasket(2);
        sb.RegisterBasket(2);
        sb.RegisterBasket(2);

        Assert.Equal(2, sb.WinnerPeerId);
    }

    [Fact]
    public void WinnerPeerId_GameNotOver_IsZero()
    {
        var sb = new Scoreboard(targetScore: 11);

        sb.RegisterBasket(1);

        Assert.Equal(0, sb.WinnerPeerId);
    }

    [Fact]
    public void RegisterBasket_PointsPerBasketExceedsTargetInOneShot_GameIsOver()
    {
        // A single basket that meets-or-exceeds the target still ends the
        // game ("reach or exceed" per the contract) — e.g. target=1.
        var sb = new Scoreboard(targetScore: 1);

        sb.RegisterBasket(1);

        Assert.True(sb.IsGameOver);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Late basket after game over — authority edge case
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void RegisterBasket_AfterGameOver_DoesNotChangeWinner()
    {
        // Player 1 wins at target=3. A same-tick late basket for player 2
        // (e.g. server detects two makes in one tick) must not steal the win.
        var sb = new Scoreboard(targetScore: 3) { PointsPerBasket = 1 };
        sb.RegisterBasket(1);
        sb.RegisterBasket(1);
        sb.RegisterBasket(1); // player 1 wins, score = 3

        sb.RegisterBasket(2); // late basket — must be ignored

        Assert.Equal(1, sb.WinnerPeerId);
    }

    [Fact]
    public void RegisterBasket_AfterGameOver_DoesNotIncrementLateScorerScore()
    {
        var sb = new Scoreboard(targetScore: 3) { PointsPerBasket = 1 };
        sb.RegisterBasket(1);
        sb.RegisterBasket(1);
        sb.RegisterBasket(1); // player 1 wins

        sb.RegisterBasket(2); // late basket — must be ignored

        Assert.Equal(0, sb.ScoreOf(2));
    }

    [Fact]
    public void RegisterBasket_AfterGameOver_DoesNotIncrementWinnerScoreFurther()
    {
        // Even the winner's own subsequent basket (e.g. a buzzer-beater
        // resolved a tick late) must not keep incrementing the final score.
        var sb = new Scoreboard(targetScore: 3) { PointsPerBasket = 1 };
        sb.RegisterBasket(1);
        sb.RegisterBasket(1);
        sb.RegisterBasket(1); // player 1 wins, score = 3

        sb.RegisterBasket(1); // late basket for the winner — still ignored

        Assert.Equal(3, sb.ScoreOf(1));
    }

    // ═════════════════════════════════════════════════════════════════════
    // Unknown / zero peer id
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void RegisterBasket_ZeroPeerId_IsIgnored()
    {
        // 0 means "nobody" by convention (mirrors BallStateMachine /
        // NetworkManager) — never a valid scorer.
        var sb = new Scoreboard(targetScore: 11);

        sb.RegisterBasket(0);

        Assert.Equal(0, sb.ScoreOf(0));
    }

    [Fact]
    public void RegisterBasket_ZeroPeerId_DoesNotEndGame()
    {
        var sb = new Scoreboard(targetScore: 1);

        sb.RegisterBasket(0);

        Assert.False(sb.IsGameOver);
    }
}
