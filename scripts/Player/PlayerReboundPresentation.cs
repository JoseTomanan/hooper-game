namespace Hooper.Player;

public partial class PlayerController
{
    internal Godot.Vector3 ReboundVisualOffset => _mesh == null ? Godot.Vector3.Zero :
        _mesh.GlobalPosition - GlobalPosition - GlobalBasis * _meshRestPosition;
    private int _lastRecoveryReceipt, _recoveryReceiptEpoch = -1;
    private int _lastReboundInterruptSequence = -1;
    internal int RecoveryInputSequence => _serverAckedSeq;

    private bool ConsumeConfirmedRebound(Hooper.Ball.BallController ball)
    {
        if (IsServer || !IsLocalPlayer || ball == null) return false;
        if (_recoveryReceiptEpoch != ball.PresentationEpoch)
        {
            _lastRecoveryReceipt = 0;
            _lastReboundInterruptSequence = -1;
            _recoveryReceiptEpoch = ball.PresentationEpoch;
        }
        var receipt = ball.ConfirmedRecovery;
        if (receipt.Receipt == 0 || receipt.Receipt == _lastRecoveryReceipt) return false;
        // Consume before eligibility: a move/loss/old receipt must not restart the
        // flourish on a later neutral tick. Prediction's already-running flourish
        // also consumes confirmation without rewinding its local display clock.
        _lastRecoveryReceipt = receipt.Receipt;
        return receipt.Recipient == OwnPeerId && ball.StateMachine.HolderPeerId == OwnPeerId
            && !ball.IsCleared && receipt.Age < ReboundGrabDisplayTicks
            && receipt.InputSequence > _lastReboundInterruptSequence
            && _reboundGrabTicksRemaining == 0;
    }

    private void InterruptReboundPresentation()
    {
        _reboundGrabTicksRemaining = 0;
        if (!IsServer && IsLocalPlayer)
            _lastReboundInterruptSequence = _buffer.LastSequence + 1;
    }
}
