using System.Collections.Generic;
using Godot;
using Hooper.Player;

namespace Hooper.Ball.Tests;

/// <summary>
/// Unit tests for PredictionBuffer — the pure client-side seq/ack bookkeeping
/// extracted from PlayerController's _seq/_pending fields (issue #55, sibling
/// of #37's MovementMath extraction). This is the contract that used to be
/// smeared across TickClientOwnPlayer, SubmitInput/ReceiveState, and
/// ReconcileFromServer — what gets recorded, evicted, pruned, and replayed.
///
/// These tests run without a live Godot instance, using Godot.Vector2 from the
/// GodotSharp NuGet (same pattern as RightStickGestureRecognizer's tests).
///
/// ── Test naming ──────────────────────────────────────────────────────────────
/// [MethodUnderTest]_[Scenario]_[ExpectedOutcome]
/// Each test contains exactly one logical assertion.
/// </summary>
public class PredictionBufferTests
{
    // ── Record: sequence numbering ───────────────────────────────────────────

    [Fact]
    public void Record_FirstCall_ReturnsSequenceOne()
    {
        var buffer = new PredictionBuffer();

        int seq = buffer.Record(Vector2.Right);

        Assert.Equal(1, seq);
    }

    [Fact]
    public void Record_SecondCall_ReturnsSequenceTwo()
    {
        var buffer = new PredictionBuffer();
        buffer.Record(Vector2.Right);

        int seq = buffer.Record(Vector2.Up);

        Assert.Equal(2, seq);
    }

    [Fact]
    public void Record_MultipleCalls_IncreasesCount()
    {
        var buffer = new PredictionBuffer();

        buffer.Record(Vector2.Right);
        buffer.Record(Vector2.Up);
        buffer.Record(Vector2.Down);

        Assert.Equal(3, buffer.Count);
    }

    // ── Replay: FIFO order ────────────────────────────────────────────────────

    [Fact]
    public void Replay_AfterMultipleRecords_YieldsInputsInFifoOrder()
    {
        var buffer = new PredictionBuffer();
        buffer.Record(Vector2.Right);
        buffer.Record(Vector2.Up);
        buffer.Record(Vector2.Down);

        var replayed = new List<Vector2>(buffer.Replay());

        Assert.Equal(new List<Vector2> { Vector2.Right, Vector2.Up, Vector2.Down }, replayed);
    }

    // ── Capacity eviction ─────────────────────────────────────────────────────

    [Fact]
    public void Record_ExceedsCapacity_EvictsOldestEntry()
    {
        // Capacity 3: a 4th Record() must evict the 1st (Right), not the 2nd.
        var buffer = new PredictionBuffer(capacity: 3);
        buffer.Record(Vector2.Right);
        buffer.Record(Vector2.Up);
        buffer.Record(Vector2.Down);
        buffer.Record(Vector2.Left);

        var replayed = new List<Vector2>(buffer.Replay());

        Assert.Equal(new List<Vector2> { Vector2.Up, Vector2.Down, Vector2.Left }, replayed);
    }

    [Fact]
    public void Record_ExceedsCapacity_CountStaysAtCapacity()
    {
        var buffer = new PredictionBuffer(capacity: 3);
        buffer.Record(Vector2.Right);
        buffer.Record(Vector2.Up);
        buffer.Record(Vector2.Down);

        buffer.Record(Vector2.Left); // 4th — should not grow Count past 3

        Assert.Equal(3, buffer.Count);
    }

    [Fact]
    public void Record_ExceedsCapacity_SequenceNumberingIsUnaffectedByEviction()
    {
        // Eviction only drops the buffered entry — the sequence counter itself
        // keeps incrementing, since the server still needs a unique seq per tick.
        var buffer = new PredictionBuffer(capacity: 3);
        buffer.Record(Vector2.Right);
        buffer.Record(Vector2.Up);
        buffer.Record(Vector2.Down);

        int seq = buffer.Record(Vector2.Left);

        Assert.Equal(4, seq);
    }

    // ── Acknowledge: pruning ──────────────────────────────────────────────────

    [Fact]
    public void Acknowledge_PrunesConfirmedSeqs_KeepsRemainder()
    {
        var buffer = new PredictionBuffer();
        buffer.Record(Vector2.Right); // seq 1
        buffer.Record(Vector2.Up);    // seq 2
        buffer.Record(Vector2.Down);  // seq 3

        buffer.Acknowledge(ackSeq: 2); // confirms seq 1 and 2

        var replayed = new List<Vector2>(buffer.Replay());
        Assert.Equal(new List<Vector2> { Vector2.Down }, replayed);
    }

    [Fact]
    public void Acknowledge_ZeroAckSeq_PrunesNothing()
    {
        // ackSeq=0 is the host's own-player broadcast (no client seq to ack,
        // see PlayerController.TickServerOwnPlayer) — must never prune seq 1+.
        var buffer = new PredictionBuffer();
        buffer.Record(Vector2.Right);
        buffer.Record(Vector2.Up);

        buffer.Acknowledge(ackSeq: 0);

        Assert.Equal(2, buffer.Count);
    }

    [Fact]
    public void Acknowledge_CalledTwiceWithSameSeq_IsIdempotent()
    {
        var buffer = new PredictionBuffer();
        buffer.Record(Vector2.Right);
        buffer.Record(Vector2.Up);
        buffer.Acknowledge(ackSeq: 1);

        buffer.Acknowledge(ackSeq: 1); // repeat — must not throw or double-prune

        Assert.Equal(1, buffer.Count);
    }

    [Fact]
    public void Acknowledge_SeqAlreadyEvicted_IsNoOp()
    {
        // Sustained packet loss evicted seq 1 already (capacity 2); acking it
        // after the fact must not error or affect what remains.
        var buffer = new PredictionBuffer(capacity: 2);
        buffer.Record(Vector2.Right); // seq 1 — will be evicted
        buffer.Record(Vector2.Up);    // seq 2
        buffer.Record(Vector2.Down);  // seq 3 — evicts seq 1

        buffer.Acknowledge(ackSeq: 1);

        var replayed = new List<Vector2>(buffer.Replay());
        Assert.Equal(new List<Vector2> { Vector2.Up, Vector2.Down }, replayed);
    }

    // ── Replay after Acknowledge ──────────────────────────────────────────────

    [Fact]
    public void Replay_AfterAcknowledgeAll_YieldsEmpty()
    {
        var buffer = new PredictionBuffer();
        buffer.Record(Vector2.Right);
        buffer.Record(Vector2.Up);

        buffer.Acknowledge(ackSeq: 2);

        Assert.Empty(buffer.Replay());
    }
}
