#nullable enable
using System;
using System.Collections.Generic;
using Godot;
using Hooper.Networking;
using Hooper.Player;
using Hooper.Moves;
using GArray = Godot.Collections.Array;

namespace Hooper.Ball;

public partial class BallController
{
    private readonly RemotePresentationBuffer<WorldPresentationFrame> _presentationBuffer = new();
    public int PresentationEpoch { get; private set; }
    public int PresentationResyncCount => _presentationBuffer.ResyncCount;
    public bool PresentationHolding => _presentationBuffer.IsHolding;
    public int PresentationQueuedFrames => _presentationBuffer.Count;
    public long RenderedPresentationTick { get; private set; } = -1;
    public Vector3 RenderedWorldPosition => _mesh?.GlobalPosition ?? GlobalPosition;
    public int PresentationEnvelopeBytes { get; private set; }
    public long LatestPresentationReceivedTick { get; private set; } = -1;

    private void InitializeRemotePresentation()
    {
        Multiplayer.ConnectedToServer += ResetRemotePresentation;
        Multiplayer.ServerDisconnected += ResetRemotePresentation;
    }
    private void DisposeRemotePresentation()
    {
        Multiplayer.ConnectedToServer -= ResetRemotePresentation;
        Multiplayer.ServerDisconnected -= ResetRemotePresentation;
    }
    private void ResetRemotePresentation()
    {
        _presentationBuffer.Reset(); PresentationEpoch++; RenderedPresentationTick = -1; LatestPresentationReceivedTick = -1;
    }
    public WorldPresentationFrame? GetPresentationFrame()
    {
        if (IsServer) return null;
        FlushRemotePresentationForHarness();
        return _presentationBuffer.Advance(Engine.GetPhysicsFrames());
    }
    partial void DeferRemotePresentationForHarness(WorldPresentationFrame frame, ref bool defer);
    partial void FlushRemotePresentationForHarness();
    private void EnqueueRemotePresentationFrame(WorldPresentationFrame frame) => _presentationBuffer.Enqueue(frame.Tick, frame);

    private void PublishRemotePresentation()
    {
        if (!IsServer || Multiplayer.GetPeers().Length == 0) return;
        var players = new GArray();
        foreach (Node node in GetTree().GetNodesInGroup("players"))
        {
            if (node is not PlayerController player) continue;
            var p = player.CapturePresentationFrame();
            if (p.PeerId <= 0) continue;
            players.Add(new GArray { p.PeerId, p.Position, p.Velocity, p.Heading, (int)p.Hand, (int)p.Phase,
                p.FrameInPhase, p.MoveId, p.MoveParam, p.Pivoting, p.Beaten, p.Protecting, p.DefensiveStance,
                p.Posting, p.PostEstablished, p.ReboundGrab, p.AnimationNode, p.AnimationTime });
        }
        if (players.Count is < 1 or > 2) return;
        var ball = new GArray { GlobalPosition, (int)State, StateMachine.HolderPeerId, IsCleared,
            _dribble.Phase, _sweepActive, _sweepTicks, _sweepDurationTicks };
        long tick = unchecked((long)Engine.GetPhysicsFrames());
        PresentationEnvelopeBytes = GD.VarToBytes(new GArray { tick, players, ball }).Length;
        // Separate channel: reliable visual head-of-line holds cannot block channel-zero gameplay RPCs.
        // https://docs.godotengine.org/en/4.7/tutorials/networking/high_level_multiplayer.html#channels
        Rpc(MethodName.ReceivePresentationFrame, tick, players, ball);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = 1)]
    private void ReceivePresentationFrame(long tick, GArray players, GArray ball)
    {
        if (IsServer || Multiplayer.GetRemoteSenderId() != 1 || tick < 0 || players.Count is < 1 or > 2
            || ball.Count != 8) return;
        var decoded = new List<PlayerPresentationFrame>(2);
        foreach (Variant value in players)
        {
            if (value.VariantType != Variant.Type.Array) return;
            GArray p = value.AsGodotArray();
            if (p.Count != 18 || !Types(p, new[] { Variant.Type.Int, Variant.Type.Vector3, Variant.Type.Vector3,
                Variant.Type.Float, Variant.Type.Int, Variant.Type.Int, Variant.Type.Int, Variant.Type.String,
                Variant.Type.Float, Variant.Type.Bool, Variant.Type.Bool, Variant.Type.Bool, Variant.Type.Bool,
                Variant.Type.Bool, Variant.Type.Bool, Variant.Type.Bool, Variant.Type.String, Variant.Type.Float })) return;
            int peer = p[0].AsInt32(); Vector3 position = p[1].AsVector3(); Vector3 velocity = p[2].AsVector3();
            float heading = p[3].AsSingle(); int hand = p[4].AsInt32(); int phase = p[5].AsInt32();
            int counter = p[6].AsInt32(); string move = p[7].AsString(); float param = p[8].AsSingle();
            string animation = p[16].AsString(); double time = p[17].AsDouble();
            if (peer <= 0 || decoded.Exists(x => x.PeerId == peer) || !position.IsFinite() || !velocity.IsFinite()
                || !float.IsFinite(heading) || !float.IsFinite(param) || !double.IsFinite(time) || time < 0
                || !Enum.IsDefined(typeof(HandSide), hand) || !Enum.IsDefined(typeof(MovePhase), phase)
                || counter < 0 || counter > 10000 || move.Length > 64 || animation.Length > 64) return;
            decoded.Add(new(peer, position, velocity, heading, (HandSide)hand, (MovePhase)phase, counter, move, param,
                p[9].AsBool(), p[10].AsBool(), p[11].AsBool(), p[12].AsBool(), p[13].AsBool(), p[14].AsBool(),
                p[15].AsBool(), animation, time));
        }
        if (!Types(ball, new[] { Variant.Type.Vector3, Variant.Type.Int, Variant.Type.Int, Variant.Type.Bool,
            Variant.Type.Float, Variant.Type.Bool, Variant.Type.Int, Variant.Type.Int })) return;
        Vector3 ballPosition = ball[0].AsVector3(); int state = ball[1].AsInt32(); int holder = ball[2].AsInt32();
        float dribble = ball[4].AsSingle(); int sweepTicks = ball[6].AsInt32(); int duration = ball[7].AsInt32();
        if (!ballPosition.IsFinite() || !Enum.IsDefined(typeof(BallState), state) || holder < 0
            || !float.IsFinite(dribble) || dribble < 0 || sweepTicks < 0 || duration < 0) return;
        var frame = new WorldPresentationFrame(tick, decoded.ToArray(), new BallPresentationFrame(ballPosition,
            (BallState)state, holder, ball[3].AsBool(), dribble, ball[5].AsBool(), sweepTicks, duration));
        LatestPresentationReceivedTick = Math.Max(LatestPresentationReceivedTick, tick);
        bool defer = false;
        DeferRemotePresentationForHarness(frame, ref defer);
        if (!defer) EnqueueRemotePresentationFrame(frame);
    }
    private static bool Types(GArray values, Variant.Type[] types)
    {
        for (int i = 0; i < types.Length; i++) if (values[i].VariantType != types[i]) return false;
        return true;
    }

    private void ApplyRemoteBallPresentation()
    {
        RenderedPresentationTick = -1;
        var frame = GetPresentationFrame();
        int local = Multiplayer.GetUniqueId();
        if (_mesh == null || frame == null || StateMachine.HolderPeerId == local || frame.Ball.HolderPeerId == local)
            return;
        _mesh.GlobalPosition = frame.Ball.Position;
        RenderedPresentationTick = frame.Tick;
    }
}
