using Godot;
namespace Hooper.Ball
{
    public partial class BallController
    {
        internal int ContinuitySweepTicks => _sweepTicks;
        internal int ContinuitySweepDurationTicks => _sweepDurationTicks;
        internal float ContinuitySweepProgress => CurrentSweepT();
    }
}
namespace Hooper.Player
{
    public partial class PlayerController
    {
        // Read-only instrumentation: leave the production AnimationTree callback unchanged.
        // Playback APIs verified against GodotSharp 4.7.1 XML and the official class reference:
        // https://docs.godotengine.org/en/4.7/classes/class_animationnodestatemachineplayback.html
        internal bool ContinuityIsLocalPlayer => IsLocalPlayer;
        internal string ContinuityAnimationCallback => _animTree?.CallbackModeProcess.ToString() ?? "missing";
        internal string ContinuityFadingFrom => _animPlayback?.GetFadingFromNode().ToString() ?? "";
        internal double ContinuityPlaybackTime => _animPlayback?.GetCurrentPlayPosition() ?? 0;
        internal double ContinuityFadingTime => _animPlayback?.GetFadingFromPlayPosition() ?? 0;
    }
}
