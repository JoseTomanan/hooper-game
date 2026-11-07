using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Networking;

namespace Hooper.Player
{
    public partial class PlayerController
    {
        internal ShotGatherState LifecycleGather => SimulationShot?.Gather ?? default;
        internal int LifecycleToken => SimulationShot?.BeginToken ?? 0;

        internal void LifecycleReset(BallController ball, Vector3 position)
        {
            _machine.ForceState(MovePhase.Inactive, 0, null);
            _buffer.Acknowledge(_buffer.LastSequence);
            // Keep Begin tokens distinct across fixtures without inventing a token.
            _buffer.Record(Vector2.Zero);
            _buffer.Acknowledge(_buffer.LastSequence);
            _hasNewState = false;
            _ball = ball;
            ResetContactStateForHarness(position, 0f, Vector3.Zero);
            HandSide = Hooper.Player.HandSide.Right;
        }

        // The fixture supplies fixed machine ticks; Ball's ordinary physics callback
        // still owns attachment, release and cleanup. No release helper is called.
        internal void LifecycleAdvanceMachine() => _machine.Tick();

        internal void LifecycleDeliverGather(int token, ShotGatherState gather)
        {
            var values = new Godot.Collections.Array
            {
                gather.Valid, gather.EntryOffset, (int)gather.Hand, gather.DribblePhase,
                gather.Faking, gather.FakeOffset
            };
            ReceiveState(_buffer.LastSequence, GlobalPosition, Velocity,
                (int)_machine.Phase, _machine.FrameInPhase, "jumpshot", 0f, Heading,
                (int)HandSide, false, false, 0f, false,
                shotToken: token, shotTarget: SimulationShot?.TargetHeading ?? 0f, shotGather: values);
            ReconcileFromServer(GlobalPosition, Velocity, _buffer.LastSequence, Heading,
                1.0 / Engine.PhysicsTicksPerSecond);
            _hasNewState = false;
        }

        internal void LifecycleReject(int token) => ReceiveShotBeginResult(token, false, 0f);

        internal void LifecycleDisplay(long tick) => AnimationDisplaySnapshot = new(
            "jumpshot", MovePhase.Active, 0, HandSide, false, tick);
    }
}

namespace Hooper.Ball
{
    public partial class BallController
    {
        internal bool LifecycleIsServer => IsServer;
        internal bool LifecycleIsLocalHolder => IsLocalHolder;

        internal void LifecycleResetBall(int peer)
        {
            _hasNewState = false;
            _smoothOffset = Vector3.Zero;
            StateMachine.ForceState(BallState.Held, peer);
            AwardPossession(peer);
        }

        // Synthetic packet ordering, deliberately labeled as such: production
        // ReceiveState stages the packet and the next real callback reconciles it.
        internal void LifecycleSnapshot(BallState state, int holder, Vector3 position, bool spent)
            => ReceiveState((int)state, position, Vector3.Zero, holder, false, spent);

        internal void LifecyclePresentation(WorldPresentationFrame frame)
        {
            ResetRemotePresentation();
            // Four contiguous envelopes establish the real buffer's first selection.
            // Do not bypass GetPresentationFrame or the target method's tick check.
            for (int i = 0; i < 4; i++)
                EnqueueRemotePresentationFrame(new WorldPresentationFrame(frame.Tick + i,
                    System.Linq.Enumerable.ToArray(frame.Players), frame.Ball));
        }
    }
}
