using System;
using Godot;

namespace Hooper.Player;

/// <summary>Pose-only endpoint interpolation. No scene, animation clock or gameplay state.</summary>
public sealed class CrossoverPoseBridge
{
    public readonly record struct LimbChain(int Root, int Middle, int Effector, bool IsSupport = false);
    public readonly record struct Result(Transform3D[] Pose, float MaxReachError, int ClampCount);

    private readonly int[] _parents;
    private readonly LimbChain[] _chains;
    private Quaternion[] _previousAnimated;
    private Quaternion[] _anchorHemisphere;
    private bool _hasPrevious;

    public CrossoverPoseBridge(int[] parents, LimbChain[] chains)
    {
        _parents = (int[])parents.Clone();
        _chains = (LimbChain[])chains.Clone();
        for (int i = 0; i < parents.Length; i++)
            if (parents[i] < -1 || parents[i] >= i)
                throw new ArgumentException("Bones must be in parent-before-child order.", nameof(parents));
        foreach (var chain in _chains)
            if (chain.Root < 0 || chain.Middle < 0 || chain.Effector < 0
                || chain.Root >= parents.Length || chain.Middle >= parents.Length || chain.Effector >= parents.Length
                || parents[chain.Middle] != chain.Root || parents[chain.Effector] != chain.Middle)
                throw new ArgumentException("Each limb must be a contiguous two-link chain.", nameof(chains));
        _previousAnimated = new Quaternion[parents.Length];
        _anchorHemisphere = new Quaternion[parents.Length];
    }

    public void ResetContinuity() => _hasPrevious = false;

    /// <summary>Place a limb endpoint after blending, including at exact blend endpoints.</summary>
    public Result PlaceEffector(Transform3D[] pose, LimbChain chain, Vector3 target)
    {
        var output = (Transform3D[])pose.Clone();
        var goals = Forward(_parents, pose);
        goals[chain.Effector].Origin = target;
        float error = 0f;
        int clamps = 0;
        Solve(output, goals, goals, 1f, chain, ref error, ref clamps);
        return new Result(output, error, clamps);
    }

    public static Transform3D[] Forward(int[] parents, ReadOnlySpan<Transform3D> locals)
    {
        if (parents.Length != locals.Length) throw new ArgumentException("Pose and hierarchy sizes differ.");
        var globals = new Transform3D[locals.Length];
        for (int i = 0; i < globals.Length; i++)
            globals[i] = parents[i] >= 0 ? globals[parents[i]] * locals[i] : locals[i];
        return globals;
    }

    public Result Blend(ReadOnlySpan<Transform3D> anchor, ReadOnlySpan<Transform3D> animated, float animatedWeight,
        float? supportWeight = null)
    {
        if (anchor.Length != _parents.Length || animated.Length != _parents.Length)
            throw new ArgumentException("Pose and hierarchy sizes differ.");
        float weight = float.IsFinite(animatedWeight) ? Math.Clamp(animatedWeight, 0f, 1f) : 0f;
        float support = supportWeight.HasValue && float.IsFinite(supportWeight.Value)
            ? Math.Clamp(supportWeight.Value, 0f, 1f) : weight;
        var anchorGlobal = Forward(_parents, anchor);
        var animatedGlobal = Forward(_parents, animated);
        var desired = new Quaternion[anchor.Length];
        for (int i = 0; i < desired.Length; i++)
        {
            Quaternion a = Rotation(animatedGlobal[i]);
            Quaternion n = Rotation(anchorGlobal[i]);
            if (_hasPrevious)
            {
                if (a.Dot(_previousAnimated[i]) < 0f) a = -a;
                n = _anchorHemisphere[i];
            }
            else
            {
                if (a.Dot(n) < 0f) n = -n;
                _anchorHemisphere[i] = n;
            }
            _previousAnimated[i] = a;
            // Freeze the anchor's hemisphere and unwrap the moving sample over time:
            // independent shortest-arc choices can flip as a limb crosses 180 degrees.
            // https://docs.godotengine.org/en/4.7/classes/class_quaternion.html#class-quaternion-method-slerpni
            desired[i] = a.Slerpni(n, 1f - weight).Normalized();
        }
        _hasPrevious = true;

        // Exact endpoints also avoid Slerpni's near-parallel shortcut changing a pose.
        if (weight <= 0f) return new Result(anchor.ToArray(), 0f, 0);
        if (weight >= 1f) return new Result(animated.ToArray(), 0f, 0);
        var output = new Transform3D[anchor.Length];
        for (int i = 0; i < output.Length; i++)
        {
            Quaternion local = _parents[i] >= 0 ? desired[_parents[i]].Inverse() * desired[i] : desired[i];
            Vector3 scale = anchor[i].Basis.Scale.Lerp(animated[i].Basis.Scale, weight);
            output[i] = new Transform3D(new Basis(local.Normalized()).Scaled(scale),
                anchor[i].Origin.Lerp(animated[i].Origin, weight));
        }
        float maxError = 0f;
        int clampCount = 0;
        foreach (var chain in _chains)
            Solve(output, anchorGlobal, animatedGlobal, chain.IsSupport ? support : weight, chain, ref maxError, ref clampCount);
        return new Result(output, maxError, clampCount);
    }

    private void Solve(Transform3D[] output, Transform3D[] anchor, Transform3D[] animated,
        float weight, LimbChain chain, ref float maxError, ref int clampCount)
    {
        const float epsilon = 0.000001f;
        var globals = Forward(_parents, output);
        Vector3 h = globals[chain.Root].Origin, k = globals[chain.Middle].Origin, a = globals[chain.Effector].Origin;
        Vector3 wanted = anchor[chain.Effector].Origin.Lerp(animated[chain.Effector].Origin, weight);
        Quaternion effectorRotation = Rotation(globals[chain.Effector]);
        float l1 = h.DistanceTo(k), l2 = k.DistanceTo(a);
        Vector3 offset = wanted - h;
        float distance = offset.Length();
        if (l1 <= epsilon || l2 <= epsilon || !Finite(offset) || !float.IsFinite(l1 + l2))
        {
            // A degenerate imported chain cannot define an arc; retain its finite blended pose.
            float error = a.DistanceTo(wanted);
            if (float.IsFinite(error)) maxError = Math.Max(maxError, error);
            return;
        }
        float minReach = Math.Abs(l1 - l2) + epsilon;
        float maxReach = l1 + l2 - epsilon;
        float reach = Math.Clamp(distance, minReach, Math.Max(minReach, maxReach));
        if (Math.Abs(reach - distance) > epsilon) clampCount++;
        Vector3 direction = distance > epsilon ? offset / distance : (a - h).Normalized();
        if (direction.LengthSquared() < epsilon * epsilon) direction = Vector3.Up;
        Vector3 target = h + direction * reach;
        float along = (l1 * l1 - l2 * l2 + reach * reach) / (2f * reach);
        float height = MathF.Sqrt(Math.Max(0f, l1 * l1 - along * along));
        Vector3 hint = anchor[chain.Middle].Origin.Lerp(animated[chain.Middle].Origin, weight) - h;
        hint -= direction * hint.Dot(direction);
        if (hint.LengthSquared() < epsilon * epsilon)
        {
            hint = k - h;
            hint -= direction * hint.Dot(direction);
        }
        if (hint.LengthSquared() < epsilon * epsilon)
            hint = direction.Cross(Math.Abs(direction.X) < .9f ? Vector3.Right : Vector3.Up);
        Vector3 targetKnee = h + direction * along + hint.Normalized() * height;
        // Quaternion(from, to) constructs the shortest arc between normalized vectors.
        // https://docs.godotengine.org/en/4.7/classes/class_quaternion.html#class-quaternion-constructor-quaternion
        Quaternion rootRotation = new Quaternion((k - h).Normalized(), (targetKnee - h).Normalized())
            * Rotation(globals[chain.Root]);
        SetGlobalRotation(output, globals, chain.Root, rootRotation);
        globals = Forward(_parents, output);
        k = globals[chain.Middle].Origin;
        a = globals[chain.Effector].Origin;
        Quaternion middleRotation = new Quaternion((a - k).Normalized(), (target - k).Normalized())
            * Rotation(globals[chain.Middle]);
        SetGlobalRotation(output, globals, chain.Middle, middleRotation);
        globals = Forward(_parents, output);
        SetGlobalRotation(output, globals, chain.Effector, effectorRotation);
        globals = Forward(_parents, output);
        maxError = Math.Max(maxError, globals[chain.Effector].Origin.DistanceTo(wanted));
    }

    private void SetGlobalRotation(Transform3D[] locals, Transform3D[] globals, int bone, Quaternion rotation)
    {
        int parent = _parents[bone];
        Quaternion local = parent >= 0 ? Rotation(globals[parent]).Inverse() * rotation : rotation;
        locals[bone] = new Transform3D(new Basis(local.Normalized()).Scaled(locals[bone].Basis.Scale), locals[bone].Origin);
    }

    private static Quaternion Rotation(Transform3D transform) => transform.Basis.Orthonormalized().GetRotationQuaternion().Normalized();
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
