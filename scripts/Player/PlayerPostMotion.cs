using System;
using Godot;
using Hooper.Ball;
using Hooper.Moves;

namespace Hooper.Player;

public partial class PlayerController
{
    /// <summary>Post movement ceiling in metres per second.</summary>
    [Export] public float PostSpeed { get; set; } = 1.5f;
    /// <summary>Post entry turn ceiling in degrees per second.</summary>
    [Export] public float PostTurnRateDeg { get; set; } = 360f;
    /// <summary>Allowed away-from-rim entry error in degrees.</summary>
    [Export] public float PostEntryToleranceDeg { get; set; } = 2f;
    /// <summary>Committed turnaround turn ceiling in degrees per second.</summary>
    [Export] public float TurnaroundTurnRateDeg { get; set; } = 360f;

    private bool _requestedPost, _pendingPostRequest, _effectivePost, _postEstablished;
    private bool _serverPost, _serverPostEstablished;
    private float _turnaroundTargetHeading;
    private int _turnaroundToken, _serverTurnaroundToken;

    public bool IsPosting => _requestedPost && PostEligible();
    public bool DisplayPosting => !IsServer && !IsLocalPlayer ? _serverPost : IsPosting;
    public bool DisplayPostEstablished => !IsServer && !IsLocalPlayer
        ? _serverPost && _serverPostEstablished : IsPosting && _postEstablished;

    private bool PostEligible() => !_machine.IsActive && IsBallHolder
        && GetBall()?.State == BallState.Dribbling && GetGameManager()?.IsGameOver != true;

    private Vector3 PostRimSample() => GetBall()?.RimCenter ?? GlobalPosition;

    private bool CanBeginTurnaround()
    {
        if (!IsPosting || !_effectivePost || !_postEstablished || _pivot.HasLatch) return false;
        Vector3 away = GlobalPosition - PostRimSample();
        return new Vector2(away.X, away.Z).Length() < .01f
            || MathF.Abs(Mathf.AngleDifference(Heading, MathF.Atan2(away.X, away.Z)))
                <= PostEntryToleranceDeg * MathF.PI / 180f;
    }

    private float SnapshotTurnaroundTarget()
    {
        Vector3 toward = PostRimSample() - GlobalPosition;
        return new Vector2(toward.X, toward.Z).Length() < .01f
            ? Heading : MathF.Atan2(toward.X, toward.Z);
    }

    private void ClearEffectivePost() { _effectivePost = _postEstablished = false; }

    // Motion-only replay: never Tick/ForceState the live machine or reproduce
    // release events while rebuilding a past position/heading.
    private void TickTurnaroundMotion(MovePhase phase, float targetHeading,
        double delta, ContactOpponent? opponent)
    {
        ClearEffectivePost();
        if (phase == MovePhase.Startup)
        {
            Heading = PostMath.StepTurn(Heading, targetHeading, delta, TurnaroundTurnRateDeg);
            // Fixed per-tick distance, with no overshoot:
            // https://docs.godotengine.org/en/4.7/classes/class_vector3.html#class-vector3-method-move-toward
            Velocity = Velocity.MoveToward(Vector3.Zero, GatherDecel * (float)delta);
        }
        else if (phase == MovePhase.Active) Velocity = Vector3.Zero;
        else Velocity = Velocity.MoveToward(Vector3.Zero, Decel * (float)delta);
        IntegrateContactMotion(delta, opponent);
    }
}
