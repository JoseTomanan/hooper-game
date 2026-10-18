#nullable enable
using Godot;
using Hooper.Networking;
using Hooper.Moves;

namespace Hooper.Player;

public partial class PlayerController
{
    private PlayerPresentationFrame? _remotePresentation;
    private WorldPresentationFrame? _remoteWorldPresentation;
    private Vector3 _beatenIndicatorRestPosition;
    private long _remoteAnimationTick = -1;
    private int _remoteAnimationResync = -1;
    private int _remoteAnimationEpoch = -1;
    private AnimationMixer.AnimationCallbackModeProcess? _liveAnimationCallback;
    public long PlayerPresentationTick => _remoteWorldPresentation?.Tick ?? -1;
    public HandSide DisplayHandSide => _remotePresentation?.Hand ?? HandSide;
    public bool DisplayPivoting => _remotePresentation?.Pivoting ?? IsPivotingInPlace;
    public float DisplayHeading => _remotePresentation?.Heading ?? Heading;
    public Vector3 DisplayVelocity => _remotePresentation?.Velocity ?? Velocity;
    public Vector3 DisplayRootPosition => _remotePresentation?.Position ?? GlobalPosition;

    // Live ball mini-physics must never infer a sweep path from delayed presentation.
    public string CurrentSimulationMoveId => DisplayPhaseResolver.LocalMachineDrivesDisplay(IsServer, IsLocalPlayer)
        ? MoveIdOf(_machine.CurrentMove) : _serverMoveId;

    public PlayerPresentationFrame CapturePresentationFrame() => new(OwnPeerId, GlobalPosition, Velocity,
        Heading, HandSide, _machine.Phase, _machine.FrameInPhase, MoveIdOf(_machine.CurrentMove),
        MoveParamOf(_machine.CurrentMove), IsPivotingInPlace, IsBeaten(PhysicsTick), IsProtecting,
        IsInDefensiveStance, IsPosting, IsPosting && _postEstablished, IsPlayingReboundGrab,
        _animPlayback?.GetCurrentNode().ToString() ?? "", _animPlayback?.GetCurrentPlayPosition() ?? 0);

    private void RefreshRemotePresentation()
    {
        _remoteWorldPresentation = !IsServer && !IsLocalPlayer ? GetBall()?.GetPresentationFrame() : null;
        _remotePresentation = null;
        if (_remoteWorldPresentation != null)
            foreach (var player in _remoteWorldPresentation.Players)
                if (player.PeerId == OwnPeerId) { _remotePresentation = player; break; }
        if (_remotePresentation == null)
        {
            _remoteWorldPresentation = null;
            if (_animTree != null && _liveAnimationCallback is { } callback)
                _animTree.CallbackModeProcess = callback;
            _liveAnimationCallback = null;
            _remoteAnimationTick = -1; _remoteAnimationResync = -1;
        }
        else if (_animTree != null && _liveAnimationCallback == null)
        {
            _liveAnimationCallback = _animTree.CallbackModeProcess;
            // Manual evaluation makes underflow hold ordinary animation clocks too.
            // https://docs.godotengine.org/en/4.7/classes/class_animationmixer.html#class-animationmixer-property-callback-mode-process
            _animTree.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Manual;
        }
    }

    private void ApplyRemoteVisualPosition()
    {
        if (_mesh != null && _remotePresentation != null)
            _mesh.Position = ToLocal(_remotePresentation.Position) + _meshRestPosition;
    }

    private bool ApplyBufferedAnimation(string target)
    {
        if (_remotePresentation == null || _remoteWorldPresentation == null) return false;
        int resync = GetBall()?.PresentationResyncCount ?? 0;
        int epoch = GetBall()?.PresentationEpoch ?? 0;
        bool reset = _remoteAnimationTick < 0 || _remoteAnimationResync != resync || _remoteAnimationEpoch != epoch;
        if (reset)
        {
            // Start(reset) deliberately re-establishes a sampled timeline after a visible reset.
            // https://docs.godotengine.org/en/4.7/classes/class_animationnodestatemachineplayback.html#class-animationnodestatemachineplayback-method-start
            _animPlayback.Start(target, true);
            _currentAnimStateName = target;
            double position = _remotePresentation.AnimationNode == target ? _remotePresentation.AnimationTime : 0;
            _animTree.Advance(position);
        }
        else
        {
            if (target != _currentAnimStateName)
            {
                bool touchesCrossover = target.StartsWith("Crossover", System.StringComparison.Ordinal)
                    || _currentAnimStateName.StartsWith("Crossover", System.StringComparison.Ordinal);
                _animPlayback.Travel(target);
                _currentAnimStateName = target;
                // Travel exposes its destination before applying that node's pose. Resolve
                // the crossover boundary now, then consume at most one source tick below.
                // https://docs.godotengine.org/en/4.7/classes/class_animationmixer.html#class-animationmixer-method-advance
                if (touchesCrossover) _animTree.Advance(0);
            }
            _animTree.Advance(_remoteAnimationTick == _remoteWorldPresentation.Tick ? 0 : 1.0 / Engine.PhysicsTicksPerSecond);
        }
        _remoteAnimationTick = _remoteWorldPresentation.Tick;
        _remoteAnimationResync = resync;
        _remoteAnimationEpoch = epoch;
        return true;
    }
}
