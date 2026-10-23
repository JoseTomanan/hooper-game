using Godot;
using Hooper.Player;

namespace Hooper.Ball;

public partial class BallController
{
    private int _recoveryReceipt, _recoveryRecipient, _recoveryInputSequence;
    private ulong _recoveryReceiptTick;
    private int RecoveryReceiptAge => _recoveryReceipt == 0 ? 0 :
        (int)System.Math.Min(Engine.GetPhysicsFrames() - _recoveryReceiptTick, int.MaxValue);

    // Cosmetic observation only. The authority's age travels in the SAME snapshot
    // as the holder, avoiding subtraction between unrelated client/server clocks.
    internal (int Receipt, int Recipient, int Age, int InputSequence) ConfirmedRecovery { get; private set; }
    private (int Receipt, int Recipient, int Age, int InputSequence) _pendingConfirmedRecovery;

    private void RecordRecoveryPresentation(int recipient)
    {
        _recoveryReceipt++;
        _recoveryRecipient = recipient;
        _recoveryReceiptTick = Engine.GetPhysicsFrames();
        _recoveryInputSequence = Players?.GetNodeOrNull<PlayerController>(recipient.ToString())?.RecoveryInputSequence ?? 0;
    }

    private void ApplyReboundHeldPresentation()
    {
        if (_mesh == null || State != BallState.Held || (!IsServer && !IsLocalHolder)) return;
        var holder = Players?.GetNodeOrNull<PlayerController>(StateMachine.HolderPeerId.ToString());
        if (holder == null || !holder.IsPlayingReboundGrab) return;
        // TickHeld already attaches the live ball to this tick's holder. An
        // independent ball reconciliation offset still carries the previous
        // owner's/falling position and separates the ball from the catching palm.
        // Share the holder's visual correction during the held catch only.
        // Node3D global/local coordinate contract:
        // https://docs.godotengine.org/en/4.7/classes/class_node3d.html#class-node3d-property-global-position
        _mesh.GlobalPosition = GlobalPosition + holder.ReboundVisualOffset;
    }
}
