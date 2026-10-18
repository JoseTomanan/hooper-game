using System;
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
    private ulong? _continuityLastDrainStamp;
    private bool _continuityDelayApplied, _continuityDelayReleased;
    private Func<bool> _continuityReleaseReady;
    internal int ContinuityDelayedFrameCount { get; private set; }
    internal int ContinuityActualDelayTicks { get; private set; }
    internal int ContinuityDelayedStartupCounter { get; private set; } = -1;
    internal bool ContinuityPresentationUnderflowObserved { get; private set; }
    internal bool ContinuityPresentationDelayApplied => _continuityDelayApplied;
    internal int ContinuityReleaseWaitTicks { get; private set; }

    internal void ConfigureContinuityPresentationDelay(int ticks, Func<bool> releaseReady = null)
    {
        _continuityDelayTicks = ticks;
        _continuityReleaseReady = releaseReady;
        if (ticks == 0)
        {
            // End this diagnostic epoch before a lifecycle fixture reconnects.
            // Retain the completed measurement, but never replay queued old-peer history.
            _continuityDelayedFrames.Clear();
            _continuityReleaseReady = null;
            _continuityDelayApplied = _continuityDelayReleased = false;
            _continuityLastDrainStamp = null;
        }
    }

    partial void DeferRemotePresentationForHarness(WorldPresentationFrame frame, ref bool defer)
    {
        if (_continuityDelayTicks == 0 || (_continuityDelayReleased && _continuityReleaseReady == null)) return;
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
        if (!_continuityDelayApplied || (_continuityDelayReleased && _continuityReleaseReady == null)) return;
        ulong stamp = Engine.GetPhysicsFrames();
        if (_continuityDelayReleased)
        {
            DrainContinuityFrame(stamp, 1);
            return;
        }
        if (stamp < _continuityDelayDeadline || !_presentationBuffer.IsHolding || _presentationBuffer.Current == null) return;
        // A render can span two physics ticks and miss the held tick. Latch the
        // actual queue event, and do not release merely because wall ticks elapsed:
        // existing queued history can otherwise absorb the entire injected delay.
        ContinuityPresentationUnderflowObserved = true;
        // Independent gameplay and presentation channels can arrive in either
        // order. A fixed delay alone cannot establish a cross-channel premise.
        // The capture may require real gameplay receipt before draining history;
        // callers testing only underflow (e.g. frozen Startup) retain the default.
        // https://docs.godotengine.org/en/4.7/tutorials/networking/high_level_multiplayer.html#channels
        if (_continuityReleaseReady != null && !_continuityReleaseReady())
        {
            ContinuityReleaseWaitTicks = (int)(Engine.GetPhysicsFrames() - _continuityDelayDeadline);
            return;
        }
        ContinuityActualDelayTicks = (int)(Engine.GetPhysicsFrames() - _continuityDelayStarted);
        if (_continuityReleaseReady == null)
            while (_continuityDelayedFrames.TryDequeue(out var frame)) EnqueueRemotePresentationFrame(frame);
        else
            // A receipt gate can hold more history than the production lag cap.
            // Refill, then deliver one unchanged source envelope per physics tick
            // instead of injecting a burst that deliberately triggers a resync.
            DrainContinuityFrame(stamp, RemotePresentationBuffer<WorldPresentationFrame>.FutureFrames);
        _continuityDelayReleased = true;
    }

    private void DrainContinuityFrame(ulong stamp, int count)
    {
        if (_continuityLastDrainStamp == stamp) return;
        _continuityLastDrainStamp = stamp;
        while (count-- > 0 && _continuityDelayedFrames.TryDequeue(out var frame)) EnqueueRemotePresentationFrame(frame);
    }
}
