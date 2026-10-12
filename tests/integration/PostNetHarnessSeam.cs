using Godot;
using Hooper.Moves;

namespace Hooper.Player
{
    public partial class PlayerController
    {
        // Calls the shipped selection and animation pipeline after fresh broadcast adoption.
        internal void PostNetApplyPresentation(double dt)
        {
            _animTree.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Manual;
            _animTree.Active = true;
            ApplyAnimation();
            _animTree.Advance(dt);
        }
        // Reliable actual owner request, with production sender validation/admission intact.
        // https://docs.godotengine.org/en/4.7/tutorials/networking/high_level_multiplayer.html#remote-procedure-calls
        internal bool PostNetReceived => _hasContactSnapshot && _serverPost;
        internal bool PostNetReceivedEstablished => _serverPostEstablished;
        internal string PostNetReceivedMove => _serverMoveId;
        internal MovePhase PostNetReceivedPhase => _serverMovePhase;
        internal float PostNetTarget => _turnaroundTargetHeading;
        internal bool PostNetEffective => _effectivePost;
        internal bool PostNetEstablished => _postEstablished;
        internal bool PostNetPivot => _pivot.HasLatch;
        internal void PostNetRejectRequest(int token) =>
            RpcId(1, MethodName.RequestBeginMove, "turnaround", 0f, false, token);
        internal void PostNetCorrection(Vector3 position, Vector3 velocity, float heading, int ack, double dt)
        {
            ReceiveState(ack, position, velocity, (int)MovePhase.Inactive, 0, "", 0f,
                heading, (int)HandSide.Right, false, false, 0f, false, false, false, false, false, 0);
            ReconcileFromServer(position, velocity, ack, heading, dt);
            _hasNewState = false;
        }
        internal void PostNetMutateHistory(string mutation)
        {
            var frames = new System.Collections.Generic.List<PredictionBuffer.InputFrame>(_buffer.ReplayInputs());
            _buffer.Acknowledge(_buffer.LastSequence);
            foreach (var frame in frames)
                _buffer.Record(frame.Movement, frame.Sprint, frame.Protect, frame.PostureHeld,
                    frame.StanceEligible, mutation == "drop-post" ? false : frame.PostHeld,
                    mutation == "current-eligibility" ? false : frame.PostEligible,
                    frame.RimSample, frame.TurnaroundPhase, frame.TurnaroundTarget, frame.TurnaroundToken);
        }
    }
}
