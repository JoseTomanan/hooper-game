using Godot;
using Hooper.Player;

namespace Hooper.Ball.Tests;

/// <summary>
/// Unit tests for FacingResolver.ResolveYaw — the pure yaw resolver extracted
/// from PlayerController (M7a, issues #38/#39) so that mesh-facing logic can
/// be verified without a running Godot instance.
///
/// The function maps a world-space velocity onto a Y-rotation (radians) using
/// Atan2(velocity.X, velocity.Z), guarded by a SpeedEpsilon (0.1 m/s) below
/// which the current yaw is returned unchanged to prevent Atan2 snap and
/// accidental facing changes during drift.
///
/// Cosmetic-only: the return value only drives the visual mesh transform;
/// collision shapes, Velocity, and all authoritative/replicated state are
/// completely unaffected (ADR-0002, ADR-0004).
///
/// ── Test naming ──────────────────────────────────────────────────────────────
/// [MethodUnderTest]_[Scenario]_[ExpectedOutcome]
/// Each test contains exactly one logical assertion.
/// </summary>
public class FacingResolverTests
{
    // ── Moving horizontally ───────────────────────────────────────────────────

    [Fact]
    public void ResolveYaw_MovingHorizontally_ReturnsYawFacingVelocityDirection()
    {
        // Any horizontal speed above SpeedEpsilon should produce a yaw derived
        // from the velocity direction, not the current yaw.
        float currentYaw = 1.5f;
        float result = FacingResolver.ResolveYaw(
            velocity: new Vector3(1, 0, 0), currentYaw: currentYaw);

        Assert.NotEqual(currentYaw, result);
    }

    [Fact]
    public void ResolveYaw_MovingHorizontally_ResultIsFiniteNotNaN()
    {
        float result = FacingResolver.ResolveYaw(
            velocity: new Vector3(5, 0, 3), currentYaw: 0f);

        Assert.True(float.IsFinite(result));
    }

    // ── Zero / near-zero velocity guard ──────────────────────────────────────

    [Fact]
    public void ResolveYaw_ZeroVelocity_ReturnsCurrentYawUnchanged()
    {
        // SpeedEpsilon guard: Atan2(0, 0) would return 0 and snap the mesh to
        // face +X the instant the player releases the stick. Returning currentYaw
        // keeps the mesh pointing where it was already facing.
        float currentYaw = 2.1f;
        float result = FacingResolver.ResolveYaw(
            velocity: Vector3.Zero, currentYaw: currentYaw);

        Assert.Equal(currentYaw, result);
    }

    [Fact]
    public void ResolveYaw_ZeroVelocity_ResultIsFinite()
    {
        float result = FacingResolver.ResolveYaw(
            velocity: Vector3.Zero, currentYaw: 0.5f);

        Assert.True(float.IsFinite(result));
    }

    [Fact]
    public void ResolveYaw_NearZeroVelocityBelowEpsilon_ReturnsCurrentYawUnchanged()
    {
        // A speed of 0.05 m/s is above true zero but below SpeedEpsilon (0.1 m/s).
        // This is drift, not intentional movement — the guard must hold.
        float currentYaw = -0.8f;
        float result = FacingResolver.ResolveYaw(
            velocity: new Vector3(0.05f, 0, 0), currentYaw: currentYaw);

        Assert.Equal(currentYaw, result);
    }

    // ── Directional sensitivity ───────────────────────────────────────────────

    [Fact]
    public void ResolveYaw_MovingInPositiveX_ReturnsPositiveYaw()
    {
        // Atan2(velocity.X, velocity.Z) with X=1, Z=0 → Atan2(1, 0) = π/2 > 0.
        float result = FacingResolver.ResolveYaw(
            velocity: new Vector3(1, 0, 0), currentYaw: 0f);

        Assert.True(result > 0f);
    }

    [Fact]
    public void ResolveYaw_MovingInPositiveZVsPositiveX_ProducesDifferentYaws()
    {
        // Confirms the formula is directionally sensitive: +X and +Z must not
        // resolve to the same yaw or the mesh facing would be meaningless.
        float yawForX = FacingResolver.ResolveYaw(
            velocity: new Vector3(1, 0, 0), currentYaw: 0f);
        float yawForZ = FacingResolver.ResolveYaw(
            velocity: new Vector3(0, 0, 1), currentYaw: 0f);

        Assert.NotEqual(yawForX, yawForZ);
    }
}
