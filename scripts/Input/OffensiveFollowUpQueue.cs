using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Hooper.Moves;

internal sealed class OffensiveFollowUpQueue<T>
{
    internal const int RecoveryWindowTicks = 6;
    private const int Capacity = 2;
    private readonly Queue<T> _pending = new(Capacity);

    internal bool TryEnqueue(T input, MovePhase phase, int recoveryTicksRemaining)
    {
        if (phase != MovePhase.Recovery
            || recoveryTicksRemaining is <= 0 or > RecoveryWindowTicks
            || _pending.Count == Capacity)
            return false;

        _pending.Enqueue(input);
        return true;
    }

    internal bool TryDequeue([MaybeNullWhen(false)] out T input) => _pending.TryDequeue(out input);

    internal int Count => _pending.Count;

    internal void InvalidatePossession() => _pending.Clear();
}
