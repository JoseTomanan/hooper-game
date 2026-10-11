using System;

namespace Hooper.Ball;

/// <summary>The actual shot-release movement penalty. Jog remains the denominator;
/// the sprint ceiling admits its extra momentum without unbounding scatter.</summary>
public static class ShotMovement
{
    public static float Factor(float initiationSpeed, float moveSpeed, float sprintSpeed, float k)
    {
        float ratio = moveSpeed > 0f
            ? Math.Clamp(initiationSpeed / moveSpeed, 0f, Math.Max(1f, sprintSpeed / moveSpeed))
            : 0f;
        return 1f + k * ratio;
    }
}
