#nullable enable
using System;
using Godot;

namespace Hooper.Player;

public partial class PlayerController
{
    // ReceiveState consumption is a display/reconcile detail, not snapshot availability.
    private bool _hasContactSnapshot;
    private bool _contactStaging;
    private int? _contactDeferredAck;
    private Vector2? _contactDeferredDribble;
    private ulong _contactAutomaticFrame = ulong.MaxValue;
    private int _contactPairSteps;
    private int _contactMotionSteps;
    private ContactOpponent? _lastContactOpponent;
    private ContactResponse _lastContactResponse;
    private bool _lastContactWasCommitted;
    // Harness partials may subscribe without changing the production movement path.
    private Action<ContactOpponent?, ContactResponse, bool>? _contactMotionObserver = null;
    private Action<Vector3, Vector3, int>? _contactReconciliationObserver = null;

    private ContactOpponent? CaptureContactSnapshot(bool raw)
    {
        if (CollisionLayer == 0 || (raw && !_hasContactSnapshot)) return null;
        foreach (Node child in GetChildren())
        {
            if (child is not CollisionShape3D { Disabled: false, Shape: CapsuleShape3D capsule } shape)
                continue;
            // This model supports upright, centered unit-scale capsules only. Shape height
            // includes both hemispheres; the center offset remains body-local.
            // https://docs.godotengine.org/en/4.7/classes/class_capsuleshape3d.html#class-capsuleshape3d-property-height
            if (!GlobalBasis.IsEqualApprox(Basis.Identity) || !shape.Basis.IsEqualApprox(Basis.Identity)
                || shape.Position.X != 0 || shape.Position.Z != 0)
                throw new InvalidOperationException("Contact requires an upright centered unit-scale capsule.");
            long peer = OwnPeerId != 0 ? OwnPeerId : unchecked((long)GetInstanceId());
            return new ContactOpponent(raw ? _serverPos : GlobalPosition,
                raw ? _serverVel : Velocity, raw ? _serverHeading : Heading,
                capsule.Radius, capsule.Height, shape.Position.Y, peer);
        }
        return null;
    }

    private PlayerController? FindContactPlayer(bool automaticOnly)
    {
        Node? parent = GetParent();
        if (parent == null || CaptureContactSnapshot(raw: false) == null) return null;
        foreach (Node child in parent.GetChildren())
        {
            if (child is PlayerController player && player != this
                && (!automaticOnly || (player.IsPhysicsProcessing() && player.CanProcess()))
                && player.CaptureContactSnapshot(raw: false) != null)
                return player;
        }
        return null;
    }

    private ContactOpponent? GetContactOpponent(bool server) =>
        FindContactPlayer(automaticOnly: false)?.CaptureContactSnapshot(raw: !server);

    private bool TryTickAutomaticContactPair(double delta)
    {
        // Physics frame identity gates the pair, regardless of sibling traversal order.
        // https://docs.godotengine.org/en/4.7/classes/class_engine.html#class-engine-method-get-physics-frames
        ulong frame = Engine.GetPhysicsFrames();
        if (_contactAutomaticFrame == frame) return true;
        PlayerController? other = FindContactPlayer(automaticOnly: true);
        if (other == null || !other.IsServer || other.GetGameManager()?.IsGameOver == true) return false;
        _contactAutomaticFrame = other._contactAutomaticFrame = frame;
        TickServerContactPair(other, delta);
        ApplyContactTickPresentation();
        other.ApplyContactTickPresentation();
        return true;
    }

    // Also used by harness partials with explicit pending input. Staging both role ticks
    // makes the inputs, headings and proposed velocities one immutable pair before motion.
    private void TickServerContactPair(PlayerController other, double delta)
    {
        _contactStaging = other._contactStaging = true;
        _contactDeferredAck = other._contactDeferredAck = null;
        _contactDeferredDribble = other._contactDeferredDribble = null;
        try
        {
            if (IsLocalPlayer) TickServerOwnPlayer(delta); else TickServerRemotePlayer(delta);
            if (other.IsLocalPlayer) other.TickServerOwnPlayer(delta); else other.TickServerRemotePlayer(delta);
        }
        finally
        {
            _contactStaging = other._contactStaging = false;
        }
        ContactOpponent? self = CaptureContactSnapshot(raw: false);
        ContactOpponent? opponent = other.CaptureContactSnapshot(raw: false);
        ContactResponse response = self is { } a && opponent is { } b
            ? ContactMath.Resolve(a, b, delta) : default;
        ObserveContact(opponent, response);
        other.ObserveContact(self, new ContactResponse(response.Opponent, response.Self));
        ApplyContactCorrection(response.Self, delta);
        other.ApplyContactCorrection(response.Opponent, delta);
        DepenetrateContactPair(other);
        _contactPairSteps++;
        other._contactPairSteps++;
        if (_contactDeferredDribble is { } input) CheckAutoStartDribble(input);
        if (other._contactDeferredDribble is { } otherInput) other.CheckAutoStartDribble(otherInput);
        PublishContactSnapshot();
        other.PublishContactSnapshot();
    }

    private void PublishContactSnapshot()
    {
        if (_contactDeferredAck is not { } ack) return;
        Rpc(MethodName.ReceiveState, ack, GlobalPosition, Velocity,
            (int)_machine.Phase, _machine.FrameInPhase, MoveIdOf(_machine.CurrentMove), MoveParamOf(_machine.CurrentMove),
            Heading, (int)HandSide, _machine.WasRecoveryEnteredEarly, _pivot.HasLatch, _pivot.LatchedYaw,
            IsBeaten(PhysicsTick));
        _contactDeferredAck = null;
    }

    private void ApplyContactTickPresentation()
    {
        ApplySmoothCorrection();
        ApplyCosmetics();
        TickReboundGrabLatch();
        ApplyAnimation();
        ApplyBeatenCue();
    }

    private void ObserveContact(ContactOpponent? opponent, ContactResponse response)
    {
        _contactMotionSteps++;
        _lastContactOpponent = opponent;
        _lastContactResponse = response;
        _lastContactWasCommitted = _machine.IsActive;
        _contactMotionObserver?.Invoke(opponent, response, _lastContactWasCommitted);
    }

    private void IntegrateContactMotion(double delta, ContactOpponent? opponent)
    {
        if (_contactStaging) return;
        ContactResponse response = CaptureContactSnapshot(raw: false) is { } self
            ? ContactMath.Resolve(self, opponent, delta) : default;
        ObserveContact(opponent, response);
        // Only this body can move against an immutable opponent sample. Transfer the
        // opponent's response share here as well, including the sweep impulse: repairing
        // only endpoint overlap would miss a high-speed crossing that ends beyond it.
        ApplyContactCorrection(new ContactCorrection(
            response.Self.Position - response.Opponent.Position,
            response.Self.Velocity - response.Opponent.Velocity), delta);
        DepenetrateAgainstContactSnapshot(opponent);
    }

    private void ApplyContactCorrection(ContactCorrection correction, double delta)
    {
        // Position is relative to the ORIGINAL free endpoint. Corrected velocity already
        // contributes its impulse over delta, so subtract that contribution before sweeping.
        SweepContactOffset(correction.Position - correction.Velocity * (float)delta);
        Velocity += correction.Velocity;
        // MoveAndSlide integrates Velocity using the physics timestep and clips it against
        // world obstacles. Keep that clipped result for the next tick and broadcast.
        // https://docs.godotengine.org/en/4.7/classes/class_characterbody3d.html#class-characterbody3d-method-move-and-slide
        MoveAndSlide();
    }

    private Vector3 SweepContactOffset(Vector3 offset)
    {
        Vector3 start = GlobalPosition;
        Vector3 remainder = offset;
        // MoveAndCollide takes displacement (not velocity), so geometry corrections remain
        // world constrained. Slide a floor-parallel remainder rather than treating the floor
        // as an obstruction to every horizontal depenetration.
        // https://docs.godotengine.org/en/4.7/classes/class_physicsbody3d.html#class-physicsbody3d-method-move-and-collide
        for (int slide = 0; slide < MaxSlides && remainder.LengthSquared() > 0; slide++)
        {
            KinematicCollision3D? collision = MoveAndCollide(remainder);
            if (collision == null) break;
            Vector3 next = collision.GetRemainder().Slide(collision.GetNormal());
            if (next.IsEqualApprox(remainder)) break;
            remainder = next;
        }
        return GlobalPosition - start;
    }

    private void DepenetrateAgainstContactSnapshot(ContactOpponent? opponent)
    {
        if (CaptureContactSnapshot(raw: false) is not { } self || opponent == null) return;
        ContactResponse separation = ContactMath.Resolve(self, opponent, 0);
        SweepContactOffset(separation.Self.Position - separation.Opponent.Position);
    }

    private void DepenetrateContactPair(PlayerController other)
    {
        ContactOpponent? self = CaptureContactSnapshot(raw: false);
        ContactOpponent? opponent = other.CaptureContactSnapshot(raw: false);
        if (self is not { } a || opponent is not { } b) return;
        ContactResponse separation = ContactMath.Resolve(a, b, 0);
        Vector3 selfApplied = SweepContactOffset(separation.Self.Position);
        Vector3 otherApplied = other.SweepContactOffset(separation.Opponent.Position);
        // A wall may block the policy's preferred share. Give that unfulfilled share to
        // the other body instead of allowing the world solver to leave overlap behind.
        other.SweepContactOffset(-(separation.Self.Position - selfApplied));
        SweepContactOffset(-(separation.Opponent.Position - otherApplied));
    }
}
