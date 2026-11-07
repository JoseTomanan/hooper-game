using System;
using Godot;
using Hooper.Player;
using Hooper.Moves;

namespace Hooper.Ball;

/// <summary>Immutable simulation anchors for one accepted normal-shot instance.</summary>
public readonly record struct ShotGatherState(bool Valid, Vector3 EntryOffset, HandSide Hand,
    float DribblePhase, bool Faking = false, Vector3 FakeOffset = default, bool Released = false,
    Vector3 ReleaseOrigin = default);

/// <summary>Fixed-frame ball attachment; no animation, skeleton or render clock.</summary>
public static class ShotGather
{
    public static Vector3 Position(ShotGatherState state, MovePhase phase, int frame,
        Vector3 root, float heading, Vector3 heldStation, Vector3 authoredTarget)
    {
        if (!state.Valid) return heldStation;
        // A stale same-holder Held correction may outlive the four-tick Active
        // window. Preserve the already-released origin in world space; neither
        // Recovery nor later body motion may drag it back to the waist.
        if (state.Released) return state.ReleaseOrigin;
        if (phase == MovePhase.Startup)
        {
            float t = Math.Clamp((frame - 1) / 7f, 0f, 1f);
            t = t * t * (3f - 2f * t);
            return (root + state.EntryOffset).Lerp(root + new Basis(Vector3.Up, heading) * authoredTarget, t);
        }
        if (phase == MovePhase.Active && !state.Faking)
            return root + new Basis(Vector3.Up, heading) * authoredTarget;
        if (phase == MovePhase.Recovery && state.Faking)
        {
            float t = Math.Clamp((frame - 13) / 6f, 0f, 1f);
            t = t * t * (3f - 2f * t);
            return (root + state.FakeOffset).Lerp(heldStation, t);
        }
        return heldStation;
    }
}
