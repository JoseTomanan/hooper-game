#nullable enable
using System;
using System.Collections.Generic;
using Godot;
using Hooper.Ball;
using Hooper.Moves;
using Hooper.Player;

namespace Hooper.Networking;

public sealed record PlayerPresentationFrame(int PeerId, Vector3 Position, Vector3 Velocity,
    float Heading, HandSide Hand, MovePhase Phase, int FrameInPhase, string MoveId, float MoveParam,
    bool Pivoting, bool Beaten, bool Protecting, bool DefensiveStance, bool Posting,
    bool PostEstablished, bool ReboundGrab, string AnimationNode, double AnimationTime);

public sealed record BallPresentationFrame(Vector3 Position, BallState State, int HolderPeerId,
    bool Cleared, float DribblePhase, bool SweepActive, int SweepTicks, int SweepDurationTicks);

/// <summary>Copies player membership once; neither sender nor caller can mutate history.</summary>
public sealed class WorldPresentationFrame
{
    private readonly IReadOnlyList<PlayerPresentationFrame> _players;
    public long Tick { get; }
    public IReadOnlyList<PlayerPresentationFrame> Players => _players;
    public BallPresentationFrame Ball { get; }
    public WorldPresentationFrame(long tick, PlayerPresentationFrame[] players, BallPresentationFrame ball)
    {
        Tick = tick; Ball = ball;
        _players = Array.AsReadOnly((PlayerPresentationFrame[])players.Clone());
    }
}
