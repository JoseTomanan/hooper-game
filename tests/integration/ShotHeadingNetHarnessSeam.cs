#nullable enable
using System;
using System.Linq;
using Godot;
using Hooper.Moves;
using Hooper.Networking;

namespace Hooper.Player;

public partial class PlayerController
{
    internal readonly record struct ShotNetCorrection(float Error, float Change, int Samples,
        bool MachineStable, bool ReleasesStable);
    internal MovePhase ShotNetPhase => _machine.Phase;
    internal int ShotNetFrame => _machine.FrameInPhase;
    internal int ShotNetToken => _machine.CurrentMove is JumpShot shot ? shot.BeginToken : 0;
    internal float ShotNetTarget => _machine.CurrentMove is JumpShot shot ? shot.TargetHeading : float.NaN;
    internal int ShotNetAuthorityToken => _serverShotToken;
    internal float ShotNetAuthorityTarget => _serverShotTarget;
    internal float ShotNetMeshYaw => _mesh?.Rotation.Y ?? float.NaN;
    internal double ShotNetAnimationTime => _animPlayback?.GetCurrentPlayPosition() ?? -1;
    internal float ShotNetBeforePredictionHeading { get; private set; }
    internal PlayerPresentationFrame? ShotNetDisplayed => _remotePresentation;

    internal void ShotNetReset(Vector3 position, bool busy)
    {
        _machine.ForceState(MovePhase.Inactive, 0, null);
        _buffer.Acknowledge(_buffer.LastSequence);
        _hasNewState = false;
        ResetContactStateForHarness(position, 0f, Vector3.Zero);
        MaxTurnRateDeg = 90f; BackTurnSlowFactor = .95f;
        if (busy) _machine.ForceState(MovePhase.Recovery, 0,
            new Hesitation(new MoveFrameData(4, 8, 300, 0)));
        _animTree.Active = true;
        _animTree.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Manual;
    }

    internal void ShotNetTickServer(double dt)
    { TickServerRemotePlayer(dt); ApplyContactTickPresentation(); }
    internal void ShotNetTickRemote() { TickClientRemotePlayer(); ApplyContactTickPresentation(); }

    // Observe the actual production reconciliation call before its following new
    // prediction tick. The oracle neither calls HeadingMath nor advances a machine.
    internal void ShotNetTickOwner(double dt, Action<ShotNetCorrection> observe, bool perturbBegin = false)
    {
        ShotNetBeforePredictionHeading = Heading;
        if (_hasNewState)
        {
            float before = Heading, expected = _serverHeading;
            int sequence = _buffer.LastSequence - _buffer.Count;
            int samples = 0;
            foreach (var frame in _buffer.ReplayInputs())
            {
                if (++sequence <= _serverAckedSeq) continue;
                if (frame.ShotPhase == MovePhase.Startup)
                {
                    float target = frame.ShotToken == _serverShotToken && _serverShotToken != 0
                        ? _serverShotTarget : frame.ShotTarget;
                    expected = ShotNetReferenceStep(expected, target, 1.0 / Engine.PhysicsTicksPerSecond,
                        MaxTurnRateDeg, BackTurnSlowFactor);
                    samples++;
                }
            }
            var move = _machine.CurrentMove; var phase = _machine.Phase; int counter = _machine.FrameInPhase;
            int releases = GetBall().SuccessfulShotReleasesForHarness;
            _contactReconciliationObserver = (_, _, _) =>
            {
                observe(new(MathF.Abs(ShotNetWrap(Heading - expected)), MathF.Abs(ShotNetWrap(Heading - before)), samples,
                    ReferenceEquals(move, _machine.CurrentMove) && phase == _machine.Phase && counter == _machine.FrameInPhase,
                    releases == GetBall().SuccessfulShotReleasesForHarness));
                ShotNetBeforePredictionHeading = Heading;
                // Fixture perturbation comes AFTER this correction, immediately
                // before the shipped input sampling captures the accepted Begin.
                if (perturbBegin && _machine.Phase == MovePhase.Inactive)
                    GlobalPosition += new Vector3(1.5f, 0, 0);
            };
        }
        else if (perturbBegin && _machine.Phase == MovePhase.Inactive)
            GlobalPosition += new Vector3(1.5f, 0, 0);
        TickClientOwnPlayer(dt);
        _contactReconciliationObserver = null;
        ApplyContactTickPresentation();
    }

    internal static float ShotNetWrap(float value)
    {
        while (value > MathF.PI) value -= 2f * MathF.PI;
        while (value < -MathF.PI) value += 2f * MathF.PI;
        return value;
    }
    internal static float ShotNetReferenceStep(float heading, float target, double dt, float rate, float slow)
    {
        float remaining = ShotNetWrap(target - heading);
        if (MathF.Abs(MathF.Abs(remaining) - MathF.PI) <= 1e-6f) remaining = MathF.PI;
        if (MathF.Abs(remaining) < 1e-5f) return target;
        float radians = rate * (1f - (1f - slow) * MathF.Abs(remaining) / MathF.PI)
            * (float)dt * MathF.PI / 180f;
        return ShotNetWrap(heading + MathF.Sign(remaining) * MathF.Min(MathF.Abs(remaining), radians));
    }

    // Explicitly synthetic ordering fixture, separate from all ENet assertions.
    internal void ShotNetVerifyInstanceOrdering(string mutation, double dt)
    {
        _buffer.Acknowledge(_buffer.LastSequence);
        const int older = 50001, newer = 50002;
        int ack = _buffer.LastSequence;
        _buffer.Record(Vector2.Zero, shotPhase: MovePhase.Startup, shotTarget: 1f, shotToken: older);
        _buffer.Record(Vector2.Zero, shotPhase: MovePhase.Startup, shotTarget: -1f, shotToken: newer);
        var current = new JumpShot { BeginToken = newer, TargetHeading = -1f };
        _machine.ForceState(MovePhase.Startup, 2, current);
        ReceiveState(ack, GlobalPosition, Vector3.Zero, (int)MovePhase.Inactive, 0, "", 0f,
            0f, (int)HandSide, false, false, 0f, false, shotToken: older, shotTarget: .7f);
        if (mutation == "drop-target") _serverShotToken = 0;
        if (mutation == "wrong-instance") current.BeginToken = older;
        int releases = GetBall().SuccessfulShotReleasesForHarness;
        ReconcileFromServer(GlobalPosition, Vector3.Zero, ack, 0f, dt);
        float expected = ShotNetReferenceStep(ShotNetReferenceStep(0f, .7f, dt, MaxTurnRateDeg, BackTurnSlowFactor),
            -1f, dt, MaxTurnRateDeg, BackTurnSlowFactor);
        var frames = _buffer.ReplayInputs().ToArray();
        if (MathF.Abs(ShotNetWrap(Heading - expected)) > 1e-5f || current.TargetHeading != -1f
            || frames[0].ShotTarget != .7f || frames[1].ShotTarget != -1f || _machine.FrameInPhase != 2)
            throw new InvalidOperationException("synthetic older-A confirmation corrupted historical replay or live unconfirmed B");
        ReceiveShotBeginResult(older, false, 0f);
        if (!ReferenceEquals(_machine.CurrentMove, current) || current.BeginToken != newer
            || _buffer.ReplayInputs().First().ShotPhase != MovePhase.Inactive
            || _buffer.ReplayInputs().Last().ShotToken != newer
            || releases != GetBall().SuccessfulShotReleasesForHarness)
            throw new InvalidOperationException("synthetic late rejection A cleared B or emitted a release");
        GD.Print("[shot-net] synthetic-ordering old-confirm/new-unconfirmed + late-reject PASS; no payload or phase advance");
        _buffer.Acknowledge(_buffer.LastSequence);
        int releaseAck = _buffer.LastSequence;
        _machine.ForceState(MovePhase.Active, 2, current);
        current.IsFadeaway = false;
        ReceiveState(releaseAck, GlobalPosition, Vector3.Zero, (int)MovePhase.Active, 0, "jumpshot", 1f,
            0f, (int)HandSide, false, false, 0f, false, shotToken: older, shotTarget: .7f);
        ReconcileFromServer(GlobalPosition, Vector3.Zero, releaseAck, 0f, dt);
        if (current.IsFadeaway) throw new InvalidOperationException("synthetic older release flag overwrote newer live shot");
        ReceiveState(releaseAck, GlobalPosition, Vector3.Zero, (int)MovePhase.Recovery, 0, "jumpshot", 1f,
            .3f, (int)HandSide, false, false, 0f, false, shotToken: newer, shotTarget: -.8f);
        ReconcileFromServer(GlobalPosition, Vector3.Zero, releaseAck, .3f, dt);
        if (!current.IsFadeaway || current.TargetHeading != -.8f || _machine.Phase != MovePhase.Active || _machine.FrameInPhase != 2)
            throw new InvalidOperationException("synthetic matching release correction failed or rewound the live machine");
        ReceiveState(releaseAck, GlobalPosition, Vector3.Zero, (int)MovePhase.Active, 0, "layup", 0f,
            .3f, (int)HandSide, false, false, 0f, false, shotToken: newer, shotTarget: -.8f);
        ReconcileFromServer(GlobalPosition, Vector3.Zero, releaseAck, .3f, dt);
        if (!current.IsFadeaway) throw new InvalidOperationException("synthetic unrelated move parameter corrupted release classification");
        ReceiveState(releaseAck, GlobalPosition, Vector3.Zero, (int)MovePhase.Active, 0, "jumpshot", 0f,
            .3f, (int)HandSide, false, false, 0f, false, shotToken: newer, shotTarget: -.8f);
        ReconcileFromServer(GlobalPosition, Vector3.Zero, releaseAck, .3f, dt);
        if (current.IsFadeaway || releases != GetBall().SuccessfulShotReleasesForHarness)
            throw new InvalidOperationException("synthetic ordinary-release correction failed or emitted a payload");
        GD.Print("[shot-net] synthetic-release matching flag correction + older/unrelated flag exclusion PASS; no phase/payload replay");
        _machine.ForceState(MovePhase.Inactive, 0, null);
        _buffer.Acknowledge(_buffer.LastSequence); _hasNewState = false;
    }
}
