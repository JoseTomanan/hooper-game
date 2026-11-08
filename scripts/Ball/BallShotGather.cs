using Godot;
using Hooper.Moves;
using Hooper.Player;

namespace Hooper.Ball;

public partial class BallController
{
    internal ShotGatherState CaptureShotGather(PlayerController holder)
    {
        if (holder == null || StateMachine.HolderPeerId != holder.SimulationPeerId
            || State is not (BallState.Held or BallState.Dribbling)) return default;
        // Capture BEFORE the cradle destroys dribble identity. This is the actual
        // mini-physics center; no rendered transform can become an input (ADR-0004).
        return new(true, GlobalPosition - holder.GlobalPosition, holder.HandSide, _dribble.Phase);
    }

    private void ApplyShotGather(PlayerController holder)
    {
        if (holder?.SimulationShot is not { } shot || !shot.Gather.Valid) return;
        GlobalPosition = ShotGather.Position(shot.Gather, holder.SimulationMovePhase, holder.SimulationMoveFrame,
            holder.GlobalPosition, holder.Heading, GlobalPosition, ShotGatherProfile.Target(
                holder.SimulationMovePhase == MovePhase.Active ? 18 : holder.SimulationMoveFrame));
    }

    private void ReplayPredictedGatherCradle()
    {
        if (IsServer || !IsLocalHolder || State != BallState.Dribbling) return;
        var player = Players?.GetNodeOrNull<PlayerController>(StateMachine.HolderPeerId.ToString());
        if (player?.SimulationShot is { Gather.Valid: true } &&
            player.SimulationMovePhase is MovePhase.Startup or MovePhase.Active or MovePhase.Recovery)
            CradleForShotStartup(player.SimulationPeerId);
    }

    private void ClearLostShotGathers()
    {
        if (Players == null) return;
        foreach (Node node in Players.GetChildren())
            if (node is PlayerController player && player.SimulationShot is { } shot
                && !(State == BallState.InFlight && shot.Gather.Released)
                && (State != BallState.Held || StateMachine.HolderPeerId != player.SimulationPeerId))
                shot.Gather = default;
    }

    internal void ResetShotGathers()
    {
        if (Players == null) return;
        foreach (Node node in Players.GetChildren())
            if (node is PlayerController player && player.SimulationShot is { } shot) shot.Gather = default;
    }

    private void ApplyShotHeldPresentation()
    {
        if (_mesh == null) return;
        bool released = State == BallState.InFlight && _inFlightStartTick == PhysicsTick;
        int peer = released ? _lastShooterPeerId : StateMachine.HolderPeerId;
        if (State != BallState.Held && !released) return;
        var holder = Players?.GetNodeOrNull<PlayerController>(peer.ToString());
        if (holder == null || (!IsServer && !holder.SimulationIsLocalPlayer)) return;
        if (released && holder.SimulationShot is not { Gather.Valid: true }) return;
        // Every locally displayed Held ball shares its holder's frame, including
        // neutral after a fake and rebound catches. Retire the independent ball
        // correction too, so an expired shot cannot expose its stale offset.
        // https://docs.godotengine.org/en/4.7/classes/class_node3d.html#class-node3d-property-global-position
        _mesh.GlobalPosition = GlobalPosition + holder.ReboundVisualOffset;
        _smoothOffset = holder.ReboundVisualOffset;
    }

    internal bool TryShotDisplayTarget(PlayerController player, out Vector3 target)
    {
        target = RenderedWorldPosition;
        var display = player.AnimationDisplaySnapshot;
        if (display.MoveId != "jumpshot") return false;
        bool held, released;
        if (display.PresentationTick >= 0)
        {
            var frame = GetPresentationFrame();
            if (frame == null || frame.Tick != display.PresentationTick) return false;
            held = frame.Ball.ShotAttached && frame.Ball.State == BallState.Held && frame.Ball.HolderPeerId == player.SimulationPeerId;
            released = frame.Ball.State == BallState.InFlight && frame.Ball.ShooterPeerId == player.SimulationPeerId
                && display.Phase == MovePhase.Active && display.FrameInPhase == 0;
            target = frame.Ball.Position;
        }
        else
        {
            held = player.SimulationShot is { Gather.Valid: true, Gather.Released: false }
                && State == BallState.Held && StateMachine.HolderPeerId == player.SimulationPeerId;
            released = State == BallState.InFlight && _lastShooterPeerId == player.SimulationPeerId
                && display.Phase == MovePhase.Active && display.FrameInPhase == 0 && _inFlightStartTick == PhysicsTick;
        }
        return (held && display.Phase is MovePhase.Startup or MovePhase.Recovery) || released;
    }

    public Vector3 FlightVelocityForHarness => CurrentVelocity();
    public (bool Advanced, Vector3 Position, Vector3 Velocity) FlightStepForHarness { get; private set; }
    public (bool Received, Vector3 Before, Vector3 Authoritative, Vector3 Velocity, string BeforeState, string State) BallCorrectionForHarness { get; private set; }

    private bool ShotAttached => State == BallState.Held &&
        Players?.GetNodeOrNull<PlayerController>(StateMachine.HolderPeerId.ToString())?.SimulationShot is { Gather.Valid: true, Gather.Released: false };
}
