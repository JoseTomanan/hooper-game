using System.Collections.Generic;
using System.Linq;
using Godot;
using Hooper.Moves;
using Hooper.Networking;

namespace Hooper.Ball;

public partial class BallController
{
    private readonly Queue<WorldPresentationFrame> _continuityDelayedFrames = new();
    private int _continuityDelayTicks;
    private ulong _continuityDelayStarted, _continuityDelayDeadline;
    private bool _continuityDelayApplied, _continuityDelayReleased;
    internal int ContinuityDelayedFrameCount { get; private set; }
    internal int ContinuityActualDelayTicks { get; private set; }
    internal bool ContinuityPresentationDelayApplied => _continuityDelayApplied;

    internal void ConfigureContinuityPresentationDelay(int ticks) => _continuityDelayTicks = ticks;

    partial void DeferRemotePresentationForHarness(WorldPresentationFrame frame, ref bool defer)
    {
        if (_continuityDelayTicks == 0 || _continuityDelayReleased) return;
        if (!_continuityDelayApplied)
        {
            if (!frame.Players.Any(p => p.PeerId == 1 && p.MoveId == "crossover"
                && p.Phase == MovePhase.Startup && p.FrameInPhase == 0)) return;
            _continuityDelayApplied = true;
            _continuityDelayStarted = Engine.GetPhysicsFrames();
            _continuityDelayDeadline = _continuityDelayStarted + (ulong)_continuityDelayTicks;
        }
        // Hold the entire immutable envelope and every successor. No gameplay
        // RPC, phase, hand, position or simulation clock is changed by this seam.
        _continuityDelayedFrames.Enqueue(frame);
        ContinuityDelayedFrameCount++;
        defer = true;
        FlushRemotePresentationForHarness();
    }

    partial void FlushRemotePresentationForHarness()
    {
        if (!_continuityDelayApplied || _continuityDelayReleased
            || Engine.GetPhysicsFrames() < _continuityDelayDeadline) return;
        ContinuityActualDelayTicks = (int)(Engine.GetPhysicsFrames() - _continuityDelayStarted);
        while (_continuityDelayedFrames.TryDequeue(out var frame)) EnqueueRemotePresentationFrame(frame);
        _continuityDelayReleased = true;
    }
}
