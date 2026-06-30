using Godot;
using Hooper.Ball;

namespace Hooper.Ball.Tests;

/// <summary>
/// Unit tests for FloorBounce — the pure deterministic floor-bounce resolver
/// for a loose basketball (issue #66, M8 realism pass, ADR-0004).
///
/// All tests run headlessly: FloorBounce is a pure static class with no Node
/// inheritance, no engine singletons, no Random, no DateTime.
///
/// ── Physics model ─────────────────────────────────────────────────────────
/// The floor contact plane is Y = ballRadius. On contact:
///   - Position is depenetrated to Y = ballRadius.
///   - vY is reflected: vY' = −vY × floorRestitution.
///   - vX, vZ are decayed: v' = v × horizontalDecay.
///   - If |vY| × floorRestitution &lt; settleSpeed the ball settles (velocity → 0).
///
/// ── Test naming convention ────────────────────────────────────────────────
/// [Subject]_[Scenario]_[ExpectedOutcome]
/// One logical assertion per test; multiple Assert lines only when they are
/// inseparable (e.g., position AND velocity of a single result tuple).
/// </summary>
public class FloorBounceTests
{
    // ── Shared constants ──────────────────────────────────────────────────

    private const float Epsilon = 0.0001f;

    private const float BallRadius        = 0.12f;
    private const float Restitution       = 0.55f;
    private const float HorizontalDecay   = 0.8f;
    private const float SettleSpeed       = 0.5f;

    // ── Helper ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Calls Resolve with the shared default tunables. Tests that need custom
    /// values construct their own call.
    /// </summary>
    private static (Vector3 pos, Vector3 vel) Resolve(Vector3 position, Vector3 velocity) =>
        FloorBounce.Resolve(position, velocity, BallRadius, Restitution, HorizontalDecay, SettleSpeed);

    // ═══════════════════════════════════════════════════════════════════════
    // No contact — ball above the floor
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_BallAboveFloor_PositionUnchanged()
    {
        // Ball well above the floor — helper must be a no-op.
        var pos = new Vector3(1f, 2f, 3f);
        var vel = new Vector3(0f, -5f, 0f);

        var (resultPos, _) = Resolve(pos, vel);

        Assert.Equal(pos.X, resultPos.X, Epsilon);
        Assert.Equal(pos.Y, resultPos.Y, Epsilon);
        Assert.Equal(pos.Z, resultPos.Z, Epsilon);
    }

    [Fact]
    public void Resolve_BallAboveFloor_VelocityUnchanged()
    {
        var pos = new Vector3(1f, 2f, 3f);
        var vel = new Vector3(0.5f, -5f, -1f);

        var (_, resultVel) = Resolve(pos, vel);

        Assert.Equal(vel.X, resultVel.X, Epsilon);
        Assert.Equal(vel.Y, resultVel.Y, Epsilon);
        Assert.Equal(vel.Z, resultVel.Z, Epsilon);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Depenetration — position is always corrected to Y == ballRadius
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_BallAtFloor_PositionDepenetratedToRadius()
    {
        // Ball centre exactly on the floor plane (Y == ballRadius, fast downward).
        var pos = new Vector3(0f, BallRadius, 0f);
        var vel = new Vector3(0f, -5f, 0f);

        var (resultPos, _) = Resolve(pos, vel);

        Assert.Equal(BallRadius, resultPos.Y, Epsilon);
    }

    [Fact]
    public void Resolve_BallBelowFloor_PositionDepenetratedToRadius()
    {
        // Ball centre below the floor (tunnelled through — common at high speed).
        var pos = new Vector3(0f, 0f, 0f);  // Y = 0 < BallRadius = 0.12
        var vel = new Vector3(0f, -3f, 0f);

        var (resultPos, _) = Resolve(pos, vel);

        Assert.Equal(BallRadius, resultPos.Y, Epsilon);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Vertical reflection — fast downward ball bounces up
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_FastDownwardBall_VelocityYFlipsSign()
    {
        // A fast incoming ball should bounce upward (vY flips from negative to positive).
        var pos = new Vector3(0f, BallRadius, 0f);
        var vel = new Vector3(0f, -5f, 0f);

        var (_, resultVel) = Resolve(pos, vel);

        Assert.True(resultVel.Y > 0f,
            $"Expected vY > 0 after floor bounce (was incoming at -5). Got {resultVel.Y}");
    }

    [Fact]
    public void Resolve_FastDownwardBall_VelocityYScaledByRestitution()
    {
        // With vY = -5 and restitution = 0.55, post-bounce vY should be +2.75.
        var pos = new Vector3(0f, BallRadius, 0f);
        var vel = new Vector3(0f, -5f, 0f);

        var (_, resultVel) = FloorBounce.Resolve(pos, vel, BallRadius, 0.55f, 1.0f, 0.1f);

        Assert.Equal(2.75f, resultVel.Y, Epsilon);
    }

    [Theory]
    [InlineData(-3f, 0.55f, 1.65f)]
    [InlineData(-8f, 0.65f, 5.20f)]
    [InlineData(-1f, 1.0f,  1.0f)]   // elastic: full speed preserved
    [InlineData(-2f, 0.0f,  0.0f)]   // inelastic: dead stop
    public void Resolve_VariousRestitutions_VelocityYMatchesFormula(
        float inVY, float restitution, float expectedOutVY)
    {
        // Settle speed set extremely low so the ball always bounces (not settles).
        var pos = new Vector3(0f, BallRadius, 0f);
        var vel = new Vector3(0f, inVY, 0f);

        var (_, resultVel) = FloorBounce.Resolve(
            pos, vel, BallRadius, restitution, horizontalDecay: 1.0f, settleSpeed: 0.001f);

        Assert.Equal(expectedOutVY, resultVel.Y, Epsilon);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Horizontal decay — XZ speed reduced, direction preserved
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_BounceWithHorizontalDecay_VelocityXDecayed()
    {
        // vX = 3, decay = 0.8 → vX' should be 2.4.
        var pos = new Vector3(0f, BallRadius, 0f);
        var vel = new Vector3(3f, -5f, 0f);

        var (_, resultVel) = FloorBounce.Resolve(pos, vel, BallRadius, 0.55f, 0.8f, 0.1f);

        Assert.Equal(2.4f, resultVel.X, Epsilon);
    }

    [Fact]
    public void Resolve_BounceWithHorizontalDecay_VelocityZDecayed()
    {
        // vZ = -2, decay = 0.8 → vZ' should be -1.6.
        var pos = new Vector3(0f, BallRadius, 0f);
        var vel = new Vector3(0f, -5f, -2f);

        var (_, resultVel) = FloorBounce.Resolve(pos, vel, BallRadius, 0.55f, 0.8f, 0.1f);

        Assert.Equal(-1.6f, resultVel.Z, Epsilon);
    }

    [Fact]
    public void Resolve_BounceWithNoHorizontalDecay_VelocityXZUnchanged()
    {
        // horizontalDecay = 1.0 (frictionless floor) — XZ unchanged.
        var pos = new Vector3(0f, BallRadius, 0f);
        var vel = new Vector3(2f, -4f, -1.5f);

        var (_, resultVel) = FloorBounce.Resolve(
            pos, vel, BallRadius, 0.55f, horizontalDecay: 1.0f, settleSpeed: 0.1f);

        Assert.Equal(vel.X, resultVel.X, Epsilon);
        Assert.Equal(vel.Z, resultVel.Z, Epsilon);
    }

    [Fact]
    public void Resolve_BounceWithHorizontalDecay_DirectionPreserved()
    {
        // Decay should scale the magnitude, not change the sign.
        var pos = new Vector3(0f, BallRadius, 0f);
        var vel = new Vector3(-3f, -4f, 2f);

        var (_, resultVel) = FloorBounce.Resolve(pos, vel, BallRadius, 0.55f, 0.8f, 0.1f);

        // Signs must be preserved (direction unchanged by decay).
        Assert.True(resultVel.X < 0f, $"Expected vX < 0 (same sign as input). Got {resultVel.X}");
        Assert.True(resultVel.Z > 0f, $"Expected vZ > 0 (same sign as input). Got {resultVel.Z}");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Settle — slow ball settles immediately; fast ball converges to rest
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_SlowDownwardBall_SettlesImmediately()
    {
        // Post-bounce vertical speed: |vY| * restitution = 0.1 * 0.55 = 0.055.
        // settleSpeed = 0.5 → 0.055 < 0.5 → should settle.
        var pos = new Vector3(0f, BallRadius, 0f);
        var vel = new Vector3(0f, -0.1f, 0f);

        var (_, resultVel) = Resolve(pos, vel);

        Assert.Equal(0f, resultVel.X, Epsilon);
        Assert.Equal(0f, resultVel.Y, Epsilon);
        Assert.Equal(0f, resultVel.Z, Epsilon);
    }

    [Fact]
    public void Resolve_SlowBallSettle_PositionIsAtBallRadius()
    {
        // Even on settle, the ball must rest on the floor (not below it).
        var pos = new Vector3(0f, 0f, 0f);  // below floor
        var vel = new Vector3(0f, -0.1f, 0f);

        var (resultPos, _) = Resolve(pos, vel);

        Assert.Equal(BallRadius, resultPos.Y, Epsilon);
    }

    [Fact]
    public void Resolve_SlowBall_HorizontalVelocityAlsoZeroedOnSettle()
    {
        // When the ball settles, ALL velocity is zeroed — not just vY.
        var pos = new Vector3(0f, BallRadius, 0f);
        var vel = new Vector3(2f, -0.05f, -1f);  // slow vY, but some XZ movement

        var (_, resultVel) = Resolve(pos, vel);

        Assert.Equal(Vector3.Zero, resultVel);
    }

    [Fact]
    public void Resolve_FastBallDoesNotSettle()
    {
        // Post-bounce vY: |-5| * 0.55 = 2.75 > 0.5 (settleSpeed) → must NOT settle.
        var pos = new Vector3(0f, BallRadius, 0f);
        var vel = new Vector3(0f, -5f, 0f);

        var (_, resultVel) = Resolve(pos, vel);

        Assert.True(resultVel.Y > 0f,
            $"Expected ball to bounce (vY > 0), but got {resultVel.Y}");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Energy convergence — repeated bounces eventually settle
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_RepeatedBounces_EventuallySettle()
    {
        // Feed the result of each bounce back in as the next input. The ball must
        // eventually settle to Vector3.Zero — proving the restitution + settle
        // threshold prevent infinite bouncing. Cap at 200 iterations; real
        // basketballs settle in ~5-8 bounces.
        var pos = new Vector3(0f, BallRadius, 0f);
        var vel = new Vector3(0f, -5f, 0f);

        const int maxBounces = 200;
        bool settled = false;

        for (int i = 0; i < maxBounces; i++)
        {
            (pos, vel) = FloorBounce.Resolve(
                pos, vel, BallRadius, Restitution, HorizontalDecay, SettleSpeed);

            if (vel == Vector3.Zero)
            {
                settled = true;
                break;
            }

            // Simulate rising and coming back down under gravity between bounces.
            // Simple approximation: vY rises, gravity pulls it back down.
            // We just flip the sign to simulate the next incoming contact.
            if (vel.Y > 0f)
            {
                // Ball is going up — simulate it coming back down at the same speed
                // (ideal arc so we can step bounce-to-bounce without a full integrator).
                vel = new Vector3(vel.X, -vel.Y, vel.Z);
                pos = new Vector3(pos.X, BallRadius, pos.Z);
            }
        }

        Assert.True(settled,
            $"Ball did not settle within {maxBounces} bounces. Final velocity: {vel}");
    }

    [Fact]
    public void Resolve_RepeatedBounces_EachBounceWeakerThanPrevious()
    {
        // Each successive bounce should produce a lower outgoing vY than the
        // previous — confirming energy is being absorbed (restitution < 1).
        var pos = new Vector3(0f, BallRadius, 0f);
        var vel = new Vector3(0f, -5f, 0f);

        float prevOutVY = float.MaxValue;
        bool energyDecreasing = true;

        for (int i = 0; i < 10; i++)
        {
            (pos, vel) = FloorBounce.Resolve(
                pos, vel, BallRadius, Restitution, HorizontalDecay, SettleSpeed);

            if (vel == Vector3.Zero) break;  // settled — test purpose satisfied

            if (vel.Y > 0f)
            {
                // Outgoing upward speed must be less than the previous bounce.
                if (vel.Y >= prevOutVY)
                {
                    energyDecreasing = false;
                    break;
                }
                prevOutVY = vel.Y;

                // Simulate the ball coming back down for the next bounce.
                vel = new Vector3(vel.X, -vel.Y, vel.Z);
                pos = new Vector3(pos.X, BallRadius, pos.Z);
            }
        }

        Assert.True(energyDecreasing,
            "Expected each successive bounce to produce less outgoing vY than the previous.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Determinism — identical inputs, identical outputs
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_Determinism_IdenticalInputsProduceIdenticalResults()
    {
        // Two independent calls with the same inputs must produce bit-identical
        // outputs — the ADR-0004 determinism requirement.
        var pos = new Vector3(1f, BallRadius, -2f);
        var vel = new Vector3(0.5f, -3f, 1.2f);

        var (posA, velA) = Resolve(pos, vel);
        var (posB, velB) = Resolve(pos, vel);

        Assert.Equal(posA.X, posB.X, Epsilon);
        Assert.Equal(posA.Y, posB.Y, Epsilon);
        Assert.Equal(posA.Z, posB.Z, Epsilon);
        Assert.Equal(velA.X, velB.X, Epsilon);
        Assert.Equal(velA.Y, velB.Y, Epsilon);
        Assert.Equal(velA.Z, velB.Z, Epsilon);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Edge cases
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Resolve_BallAtRestOnFloor_MovingUpward_NotReflectedAgain()
    {
        // A ball that just bounced and is moving upward while still at floor Y
        // (position hasn't had time to move above BallRadius yet in one tick)
        // must NOT be reflected again — doing so would trap it in an endless
        // flip-flop. Velocity should remain upward.
        var pos = new Vector3(0f, BallRadius, 0f);
        var vel = new Vector3(0f, 2f, 0f);  // moving UP

        var (_, resultVel) = Resolve(pos, vel);

        Assert.True(resultVel.Y >= 0f,
            $"Expected ball moving upward to not be reflected downward. Got vY={resultVel.Y}");
    }

    [Fact]
    public void Resolve_ZeroVerticalVelocityAtFloor_DoesNotExplode()
    {
        // Ball at floor height with no vertical velocity (vY == 0).
        // Should not produce NaN or throw.
        var pos = new Vector3(0f, BallRadius, 0f);
        var vel = new Vector3(1f, 0f, 0f);

        var (resultPos, resultVel) = Resolve(pos, vel);

        Assert.False(float.IsNaN(resultPos.Y), "Position.Y should not be NaN");
        Assert.False(float.IsNaN(resultVel.Y), "Velocity.Y should not be NaN");
    }

    [Fact]
    public void Resolve_PositionXZNotAffectedByFloorContact()
    {
        // Floor contact only changes Y; X and Z must pass through unchanged.
        var pos = new Vector3(3f, BallRadius * 0.5f, -7f);
        var vel = new Vector3(1f, -4f, -2f);

        var (resultPos, _) = Resolve(pos, vel);

        Assert.Equal(pos.X, resultPos.X, Epsilon);
        Assert.Equal(pos.Z, resultPos.Z, Epsilon);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // NBA rebound-height grounding (#79) — FloorRestitution is data-grounded
    // ═══════════════════════════════════════════════════════════════════════
    //
    // The NBA inflation spec is the "real half-court ball" reference (ADR-0014,
    // top-ranked): a ball dropped to the floor from 6 ft (72 in, measured to the
    // BOTTOM of the ball) must rebound to between 49 in and 54 in (measured to
    // the TOP of the ball). Because rebound height scales as e² (e = coefficient
    // of restitution), that spec pins e ∈ [0.741, 0.787] — see the arithmetic in
    // RegulationDrop_ReboundTop_LandsInNbaLegalBand below. This is the
    // floor-bounce analogue of ShotMakeCurveTests: it grounds the magnitude of a
    // tunable against a measurable real-ball number rather than feel.
    //
    // FloorRestitutionDefault MUST mirror BallController.FloorRestitution. If
    // that export is retuned, update this constant — the test will then re-prove
    // the new value is still NBA-legal (or fail loudly if it left the band).

    private const float FloorRestitutionDefault = 0.76f;

    [Fact]
    public void RegulationDrop_ReboundTop_LandsInNbaLegalBand()
    {
        // Drop the ball from regulation height and bounce it through the REAL
        // FloorBounce model, then check where the top of the ball peaks.
        //
        //   drop (to bottom of ball) ...... 72 in = 1.8288 m
        //   legal rebound (to top of ball) . 49–54 in = 1.2446–1.3716 m
        //
        // The ball's centre of mass falls the full 72 in (its bottom travels
        // 72 in → 0), so the impact speed is v_in = √(2·g·1.8288). FloorBounce
        // reflects it to v_out = e·v_in (settle threshold set low so it always
        // bounces). The centre then coasts up by v_out²/(2g) above its resting
        // height of BallRadius, and the TOP of the ball peaks one more radius
        // above that:  reboundTop = 2·BallRadius + v_out²/(2g).
        const float Gravity      = 9.8f;             // matches ShotArc default
        const float DropToBottom = 1.8288f;          // 72 in
        const float LegalMin     = 1.2446f;          // 49 in (top of ball)
        const float LegalMax     = 1.3716f;          // 54 in (top of ball)

        float vIn  = MathF.Sqrt(2f * Gravity * DropToBottom);

        // Bounce through the real resolver. Ball centre is at the resting contact
        // plane (Y = BallRadius); settleSpeed tiny so a regulation-speed drop
        // always rebounds rather than settling.
        var (_, outVel) = FloorBounce.Resolve(
            new Vector3(0f, BallRadius, 0f),
            new Vector3(0f, -vIn, 0f),
            BallRadius, FloorRestitutionDefault,
            horizontalDecay: 1.0f, settleSpeed: 0.001f);

        float reboundCom = (outVel.Y * outVel.Y) / (2f * Gravity);
        float reboundTop = 2f * BallRadius + reboundCom;

        Assert.True(reboundTop >= LegalMin && reboundTop <= LegalMax,
            $"Regulation drop rebound top was {reboundTop:0.000} m " +
            $"({reboundTop / 0.0254f:0.0} in), outside the NBA-legal band " +
            $"[{LegalMin:0.000}, {LegalMax:0.000}] m (49–54 in). " +
            $"FloorRestitution = {FloorRestitutionDefault} is not data-grounded.");
    }
}
