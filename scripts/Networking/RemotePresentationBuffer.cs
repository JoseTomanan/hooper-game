#nullable enable
using System.Collections.Generic;

namespace Hooper.Networking;

/// <summary>A bounded, discrete presentation timeline. Never changes gameplay state.</summary>
public sealed class RemotePresentationBuffer<T> where T : class
{
    public const int FutureFrames = 3;
    public const int Capacity = 16;
    public const int MaximumLagTicks = 12;
    private readonly SortedDictionary<long, T> _frames = new();
    private bool _hasStamp;
    private ulong _stamp;
    public T? Current { get; private set; }
    public long CurrentTick { get; private set; } = -1;
    public int Count => _frames.Count;
    public int ResyncCount { get; private set; }
    public bool IsHolding { get; private set; } = true;

    public void Enqueue(long tick, T frame)
    {
        if (tick < 0 || tick <= CurrentTick || _frames.ContainsKey(tick)) return;
        _frames.Add(tick, frame);
        while (_frames.Count > Capacity) _frames.Remove(OldestTick());
    }

    public T? Advance(ulong localPhysicsStamp)
    {
        if (_hasStamp && localPhysicsStamp == _stamp) return Current;
        _hasStamp = true; _stamp = localPhysicsStamp;
        long newest = NewestTick();
        if ((Current == null || newest - CurrentTick > MaximumLagTicks)
            && HasRange(newest - FutureFrames, FutureFrames + 1))
        {
            if (Current != null) ResyncCount++;
            Select(newest - FutureFrames);
            IsHolding = false;
            return Current;
        }
        if (Current == null) return null;
        if (!HasRange(CurrentTick + 1, IsHolding ? FutureFrames : 1))
        {
            IsHolding = true;
            return Current;
        }
        Select(CurrentTick + 1);
        IsHolding = false;
        return Current;
    }

    public void Reset()
    {
        _frames.Clear(); Current = null; CurrentTick = -1;
        _hasStamp = false; IsHolding = true; ResyncCount = 0;
    }

    private void Select(long tick)
    {
        Current = _frames[tick]; CurrentTick = tick;
        while (_frames.Count > 0 && OldestTick() <= tick) _frames.Remove(OldestTick());
    }
    private bool HasRange(long first, int count)
    {
        if (first < 0) return false;
        for (int i = 0; i < count; i++) if (!_frames.ContainsKey(first + i)) return false;
        return true;
    }
    private long OldestTick()
    {
        foreach (long tick in _frames.Keys) return tick;
        return -1;
    }
    private long NewestTick()
    {
        long newest = -1;
        foreach (long tick in _frames.Keys) newest = tick;
        return newest;
    }
}
