#nullable enable
using System;
using Godot;
namespace Hooper.Player;

/// <summary>Immutable raw upright-capsule snapshot; center height is relative to Position.Y.</summary>
public readonly record struct ContactOpponent(Vector3 Position, Vector3 Velocity, float Heading, float CapsuleRadius, float CapsuleHeight, float CapsuleCenterHeight, long PeerId);
/// <summary>Corrections relative to the original free endpoint and original velocity.</summary>
public readonly record struct ContactCorrection(Vector3 Position, Vector3 Velocity);
public readonly record struct ContactResponse(ContactCorrection Self, ContactCorrection Opponent);
// Provisional calibration: .5 m/s is one shipped acceleration quantum (30 m/s² / 60 Hz),
// below one tenth of the shipped 6 m/s top speed. The 60° facing cone follows ADR-0018's
// authoritative held-exposure precedent. Both await tuning #238 / feel #173.
public sealed record ContactSettings(float MaxSetSpeed = .5f, float FacingHalfAngle = MathF.PI / 3f)
{
    public static ContactSettings Default { get; } = new();
}
/// <summary>Pure deterministic ADR-0025 contact policy; no engine state or collision solver.</summary>
public static class ContactMath
{
    /// <summary>
    /// Resolves one frozen pair. Apply endpoint = originalPosition + originalVelocity * delta
    /// + correction.Position, then velocity += correction.Velocity. For a pre-solver caller
    /// integrating corrected velocity, the pre-step offset is Position - Velocity * delta;
    /// applying both the full Position correction and corrected velocity double-counts it.
    /// A null opponent is unconditionally a no-contact result. Zero delta depenetrates only.
    /// Relative sweep travel and output corrections must fit finite floats; otherwise this
    /// method rejects the request. Very large endpoints still carry ordinary float rounding.
    /// </summary>
    public static ContactResponse Resolve(ContactOpponent self, ContactOpponent? opponent, double delta, ContactSettings? settings = null)
    {
        if (opponent is not { } other) return default;
        Validate(self);
        Validate(other);
        if (self.PeerId == other.PeerId) throw new ArgumentException("Contact peers must have distinct stable IDs.");
        if (!double.IsFinite(delta) || delta < 0) throw new ArgumentOutOfRangeException(nameof(delta));
        settings ??= ContactSettings.Default;
        Validate(settings);
        double x = (double)self.Position.X - other.Position.X;
        double z = (double)self.Position.Z - other.Position.Z;
        double vx = (double)self.Velocity.X - other.Velocity.X;
        double vz = (double)self.Velocity.Z - other.Velocity.Z;
        // Godot height is the TOTAL capsule height, including both hemispheres.
        // https://docs.godotengine.org/en/4.7/classes/class_capsuleshape3d.html#class-capsuleshape3d-property-height
        double radiusSum = (double)self.CapsuleRadius + other.CapsuleRadius;
        double segmentHalves = (self.CapsuleHeight / 2d - self.CapsuleRadius) + (other.CapsuleHeight / 2d - other.CapsuleRadius);
        // Complete each body-local quantity before combining the pair, so cancellation
        // from large offsets cannot change geometry when caller/opponent are swapped.
        double centerDifference = Math.Abs(((double)self.Position.Y + self.CapsuleCenterHeight) - ((double)other.Position.Y + other.CapsuleCenterHeight));
        double gap = Math.Max(0, centerDifference - segmentHalves);
        if (gap >= radiusSum) return default;
        double radius = Math.Sqrt(radiusSum * radiusSum - gap * gap);
        double positionX = 0, positionZ = 0, otherPositionX = 0, otherPositionZ = 0;
        double distance = Math.Sqrt(x * x + z * z);
        bool overlapping = distance < radius;
        if (overlapping)
        {
            double normalX = distance == 0 ? (self.PeerId < other.PeerId ? 1 : -1) : x / distance;
            double normalZ = distance == 0 ? 0 : z / distance;
            var weights = Weights(self, other, normalX, normalZ, settings);
            double penetration = radius - distance;
            positionX = normalX * penetration * weights.Self;
            positionZ = normalZ * penetration * weights.Self;
            otherPositionX = -normalX * penetration * weights.Other;
            otherPositionZ = -normalZ * penetration * weights.Other;
            x = normalX * radius;
            z = normalZ * radius;
        }
        ContactResponse Separation() => new(new(V(positionX, positionZ), Vector3.Zero), new(V(otherPositionX, otherPositionZ), Vector3.Zero));
        double speed = Math.Sqrt(vx * vx + vz * vz);
        double travel = speed * delta;
        if (!double.IsFinite(travel) || travel > float.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(delta), "Relative travel must be representable as a finite float.");
        if (travel == 0) return Separation();
        double ux = vx / speed, uz = vz / speed;
        double approach = -(x * vx + z * vz) / speed;
        // Form the raw determinant before normalizing: dividing its factors first
        // can turn exact diagonal center crossings into a spurious lateral gap.
        double perpendicular = (x * vz - z * vx) / speed;
        double chordSquared = radius * radius - perpendicular * perpendicular;
        if (approach <= 0 || chordSquared <= 0) return Separation();
        double chord = Math.Sqrt(chordSquared);
        // Distance-to-entry avoids subtracting two huge quadratic terms. The factored
        // numerator preserves tiny separations; the chord retains submeter glancing hits.
        double entry = overlapping ? 0 : (distance - radius) * (distance + radius) / (approach + chord);
        double t = entry / travel;
        if (t < 0 || t > 1) return Separation();
        // Reconstruct from the closest point and chord, avoiding x + displacement*t
        // cancellation when the starting position is far from the collision.
        double nx = (perpendicular * uz - ux * chord) / radius;
        double nz = (-perpendicular * ux - uz * chord) / radius;
        var (selfWeight, otherWeight) = Weights(self, other, nx, nz, settings);
        double closing = vx * nx + vz * nz;
        double impulse = -Math.Min(0, closing);
        // Continue the unused tick with corrected velocity, preserving tangential motion.
        double remaining = delta * (1 - t);
        return new(new(V(positionX + nx * impulse * selfWeight * remaining, positionZ + nz * impulse * selfWeight * remaining), V(nx * impulse * selfWeight, nz * impulse * selfWeight)),
            new(V(otherPositionX - nx * impulse * otherWeight * remaining, otherPositionZ - nz * impulse * otherWeight * remaining), V(-nx * impulse * otherWeight, -nz * impulse * otherWeight)));
    }
    private static Vector3 V(double x, double z)
    {
        if (!double.IsFinite(x) || !double.IsFinite(z) || Math.Abs(x) > float.MaxValue || Math.Abs(z) > float.MaxValue)
            throw new ArgumentOutOfRangeException("correction", "Contact correction components must be representable as finite floats.");
        return new((float)x, 0, (float)z);
    }
    private static (double Self, double Other) Weights(ContactOpponent self, ContactOpponent other, double nx, double nz, ContactSettings settings)
    {
        bool selfSet = IsSet(self, new((float)-nx, (float)-nz), settings);
        bool otherSet = IsSet(other, new((float)nx, (float)nz), settings);
        return selfSet == otherSet ? (.5, .5) : selfSet ? (0, 1) : (1, 0);
    }
    /// <summary>Inclusive speed AND authoritative-heading cone. Direction magnitude is ignored; zero is unset.</summary>
    public static bool IsSet(ContactOpponent state, Vector2 towardContact, ContactSettings? settings = null)
    {
        settings ??= ContactSettings.Default;
        Validate(settings);
        Validate(state);
        if (!float.IsFinite(towardContact.X) || !float.IsFinite(towardContact.Y))
            throw new ArgumentException("Contact direction must be finite.", nameof(towardContact));
        double speedSquared = (double)state.Velocity.X * state.Velocity.X + (double)state.Velocity.Z * state.Velocity.Z;
        double length = Math.Sqrt((double)towardContact.X * towardContact.X + (double)towardContact.Y * towardContact.Y);
        if (length == 0 || speedSquared > (double)settings.MaxSetSpeed * settings.MaxSetSpeed) return false;
        double angle = Math.Atan2(towardContact.X, towardContact.Y);
        double difference = Math.Abs(Math.IEEERemainder(angle - state.Heading, 2 * Math.PI));
        return difference <= settings.FacingHalfAngle;
    }

    private static void Validate(ContactOpponent state)
    {
        if (!Finite(state.Position) || !Finite(state.Velocity) || !float.IsFinite(state.Heading) || !float.IsFinite(state.CapsuleRadius) || !float.IsFinite(state.CapsuleHeight) || !float.IsFinite(state.CapsuleCenterHeight))
            throw new ArgumentException("Contact snapshots must be finite.", nameof(state));
        if (state.Velocity.Y != 0 || state.CapsuleRadius <= 0 || (double)state.CapsuleHeight < 2d * state.CapsuleRadius)
            throw new ArgumentException("Contact supports fixed-height upright capsules with positive radius and total height at least twice radius.", nameof(state));
    }
    private static bool Finite(Vector3 vector) => float.IsFinite(vector.X) && float.IsFinite(vector.Y) && float.IsFinite(vector.Z);
    private static void Validate(ContactSettings settings)
    {
        if (!float.IsFinite(settings.MaxSetSpeed) || settings.MaxSetSpeed < 0 || !float.IsFinite(settings.FacingHalfAngle) || settings.FacingHalfAngle < 0 || settings.FacingHalfAngle > MathF.PI)
            throw new ArgumentOutOfRangeException(nameof(settings));
    }
}
