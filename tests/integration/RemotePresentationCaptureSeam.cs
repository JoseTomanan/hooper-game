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
    internal int ContinuityDelayedStartupCounter { get; private set; } = -1;
    internal bool ContinuityPresentationUnderflowObserved { get; private set; }
    internal bool ContinuityPresentationDelayApplied => _continuityDelayApplied;

    internal void ConfigureContinuityPresentationDelay(int ticks) => _continuityDelayTicks = ticks;

    partial void DeferRemotePresentationForHarness(WorldPresentationFrame frame, ref bool defer)
    {
        if (_continuityDelayTicks == 0 || _continuityDelayReleased) return;
        if (!_continuityDelayApplied)
        {
            var firstStartup = frame.Players.FirstOrDefault(p => p.PeerId == 1 && p.MoveId == "crossover"
                && p.Phase == MovePhase.Startup && p.FrameInPhase <= 1);
            if (firstStartup == null) return;
            // Begin is followed by the machine tick before the host publishes;
            // the actual first snapshot normally has counter 1, not an invented 0.
            ContinuityDelayedStartupCounter = firstStartup.FrameInPhase;
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
            || Engine.GetPhysicsFrames() < _continuityDelayDeadline
            || !_presentationBuffer.IsHolding || _presentationBuffer.Current == null) return;
        // A render can span two physics ticks and miss the held tick. Latch the
        // actual queue event, and do not release merely because wall ticks elapsed:
        // existing queued history can otherwise absorb the entire injected delay.
        ContinuityPresentationUnderflowObserved = true;
        ContinuityActualDelayTicks = (int)(Engine.GetPhysicsFrames() - _continuityDelayStarted);
        while (_continuityDelayedFrames.TryDequeue(out var frame)) EnqueueRemotePresentationFrame(frame);
        _continuityDelayReleased = true;
    }
}
