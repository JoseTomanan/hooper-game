namespace Hooper.Moves;

/// <summary>
/// A planted turn followed by the existing shot windup. The target is captured
/// at Begin so stick/rim changes cannot steer a committed shot (#348).
/// At 60 Hz, 48 startup ticks allow a 180-degree turn at 360 deg/s (30 ticks)
/// followed by the ordinary 18-tick windup. No pump-fake/flow-cancel is legal.
/// </summary>
public sealed class TurnaroundShot : CommittedMove
{
    public static readonly MoveFrameData DefaultFrameData =
        new(startupFrames: 48, activeFrames: 4, recoveryFrames: 20, feintWindowFrames: 0);

    public float TargetHeading { get; }

    public TurnaroundShot(float targetHeading)
        : base(id: "turnaround", displayName: "Turnaround shot", frameData: DefaultFrameData)
    {
        TargetHeading = targetHeading;
    }
}
