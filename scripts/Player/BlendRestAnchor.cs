using Godot;

namespace Hooper.Player;

/// <summary>
/// Fixes issue #287 (the idle&lt;-&gt;run locomotion twitch residual left after
/// #275/PR #286): anchors EXACTLY TWO Skeleton3D bone rests —
/// <c>mixamorig_LeftUpLeg</c> and <c>mixamorig_RightUpLeg</c> — to
/// <c>locomotion/idle</c>'s own first rotation key, instead of Y Bot's raw
/// T-pose import rest.
///
/// ── Why (the confirmed mechanism, not a guess) ───────────────────────────────
/// A #287 diagnostic probe proved BOTH idle and run sit near the ANTIPODE of Y
/// Bot's raw rest for these two bones across their ENTIRE timeline (idle
/// 162-177 deg from rest, run 131-180 deg — not just far apart from EACH
/// OTHER). Godot's AnimationTree mixer blends multiple contributions via a
/// REST-ANCHORED accumulation; when two contributions both sit near rest's
/// antipode but along different great circles, their rest-relative components
/// partially cancel at intermediate blend weights, producing a pose on NEITHER
/// clip's arc (up to 134 deg of excess deviation, confirmed by the #287
/// corridor-sweep harness). Splitting the interval with synthesized
/// midpoint clips (tried first, per the issue's own recommendation) only
/// shrinks this — it can't close it, because the degeneracy condition ("both
/// contributions near rest's antipode") holds for ANY two poses drawn from
/// this same pose family, not just the idle/run endpoints (measured trend:
/// 53 -&gt; 65 -&gt; 28 violations as points were added, never converging to 0).
///
/// Re-anchoring the REST itself to sit near the animated pose family (instead
/// of Y Bot's raw T-pose, which is nowhere close to how these two bones ever
/// actually pose in this rig's clips) removes the antipode condition at its
/// source. Empirically verified against the #287 corridor-sweep harness:
/// 0/90 violations, and BOTH endpoint poses (full idle, full run) are
/// UNCHANGED to 0.0000 deg — because ROTATION_3D animation tracks are
/// ABSOLUTE local rotations in Godot, so any bone with full-weight
/// contribution from a single clip (i.e. every point OTHER than intermediate
/// BlendSpace1D weights) ignores rest entirely; rest anchoring only ever
/// affects PARTIAL-weight blending. Concretely:
///   (a) idle-at-blend-0 and run-at-blend-6 output ROTATION_3D key values
///       verbatim, rest is never consulted — endpoints are byte-identical
///       before/after this fix;
///   (b) `pivot` (the plant/turn clip) animates BOTH UpLeg bones directly on
///       its own 4 rotation tracks and always plays at full weight (its own
///       AnimationNodeStateMachine state, never blended with anything else)
///       — its rendered pose is unaffected;
///   (c) scenes/Player.tscn's AnimationNodeStateMachine transitions are all
///       `xfade_time` unset (Godot default 0 -- verified by reading every
///       AnimationNodeStateMachineTransition sub_resource in the .tscn: none
///       sets xfade_time), so state-to-state switches (Locomotion/Startup/
///       Active/Recovery/Pivot/FadeawayActive) are hard cuts, never a blend —
///       the Locomotion BlendSpace1D (idle&lt;-&gt;run) is the ONLY
///       multi-contribution blend surface in the whole tree. This fix
///       therefore cannot expose any new partial-weight surface.
///
/// ── PlayerRigScaler ordering (scripts/Player/PlayerRigScaler.cs) ────────────
/// RigScaler.CaptureBaseline() reads ONLY `GetBoneRest(i).Basis.Scale` for
/// every bone, then ApplyScales() writes `SetBonePoseScale` (the POSE, not the
/// REST). This node mutates ONLY the REST's rotation (Basis built from a pure
/// quaternion, scaled by the ORIGINAL rest's own Basis.Scale so the scale
/// component this node writes back is byte-identical to what was already
/// there) and leaves Origin untouched — so regardless of which of these two
/// nodes' _Ready() runs first in scene order, RigScaler's baseline capture and
/// this node's rotation-only rest mutation cannot observe or corrupt each
/// other's work.
///
/// ── Timing ────────────────────────────────────────────────────────────────
/// Must run before AnimationTree processes its first frame, so the mixer never
/// blends against the raw T-pose rest even once. Godot guarantees this for
/// free: EVERY node's _Ready() in a freshly-instantiated scene subtree runs
/// synchronously (depth-first) during the "add to tree" call, strictly before
/// the SceneTree's next _process/_physics_process pass — and AnimationTree's
/// own automatic advance (its default CallbackModeProcess=Physics) only fires
/// on that later pass. No explicit node-order dependency is required, but this
/// node is still placed before AnimationPlayer/AnimationTree in
/// scenes/Player.tscn's node list for readability, matching RigScaler's own
/// existing position.
///
/// See docs/spikes/0012-headless-import-retarget.md's #287 addendum for the
/// full investigation (rejected alternatives: synthesized midpoint clips,
/// RESET-track anchoring — both proven empirically insufficient/inert before
/// this fix was authorized).
/// </summary>
public partial class BlendRestAnchor : Node
{
    // Scoped by explicit orchestrator authorization (issue #287) after the
    // corridor-sweep proved these are the ONLY bones whose rest-relative
    // representation degenerates the Locomotion blend. Do not widen this set
    // without re-running the #287 corridor sweep against the new bone list —
    // widening it un-verifies the "endpoints byte-identical" guarantee above
    // for whichever bone gets added.
    private static readonly string[] AnchoredBones =
    {
        "mixamorig_LeftUpLeg",
        "mixamorig_RightUpLeg",
    };

    public override void _Ready()
    {
        var skeleton = ResolveSkeleton();
        if (skeleton == null)
        {
            GD.PrintErr("[BlendRestAnchor] No Skeleton3D found under the player scene root — " +
                        "rest anchoring disabled (issue #287). This is expected in scenes with no rig.");
            return;
        }

        var animPlayer = GetParent()?.GetNodeOrNull<AnimationPlayer>("AnimationPlayer");
        if (animPlayer == null)
        {
            GD.PrintErr("[BlendRestAnchor] No sibling AnimationPlayer named 'AnimationPlayer' found — " +
                        "rest anchoring disabled (issue #287).");
            return;
        }

        if (!animPlayer.HasAnimationLibrary("locomotion"))
        {
            GD.PrintErr("[BlendRestAnchor] AnimationPlayer has no 'locomotion' library — " +
                        "rest anchoring disabled (issue #287).");
            return;
        }
        AnimationLibrary lib = animPlayer.GetAnimationLibrary("locomotion");
        if (!lib.HasAnimation("idle"))
        {
            GD.PrintErr("[BlendRestAnchor] locomotion library has no 'idle' clip — " +
                        "rest anchoring disabled (issue #287).");
            return;
        }
        Animation idle = lib.GetAnimation("idle");

        // "Prove match count > 0" (this repo's Mixamo-prefix memory): count
        // successful anchors and fail LOUD, never silently, if any expected
        // bone/track doesn't resolve — that's exactly the class of bug a
        // silent no-op would hide (issue #287, mirroring the #170 RigScale
        // convention-drift guard in PlayerRigScaler.ApplyScales).
        int anchored = 0;
        foreach (string boneName in AnchoredBones)
        {
            int boneIdx = skeleton.FindBone(boneName);
            if (boneIdx < 0)
            {
                GD.PushError($"[BlendRestAnchor] Skeleton has no bone '{boneName}' — cannot anchor its " +
                             "rest; leaving it untouched (issue #287).");
                continue;
            }

            int trackIdx = FindRotationTrack(idle, boneName);
            if (trackIdx < 0 || idle.TrackGetKeyCount(trackIdx) <= 0)
            {
                GD.PushError($"[BlendRestAnchor] locomotion/idle has no rotation key0 for '{boneName}' — " +
                             "leaving its rest untouched (issue #287).");
                continue;
            }

            var anchorQuat = ((Quaternion)idle.TrackGetKeyValue(trackIdx, 0)).Normalized();
            Transform3D originalRest = skeleton.GetBoneRest(boneIdx);
            // Rotation only: rebuild the basis from the anchor quaternion, then
            // re-apply the ORIGINAL rest's own scale so RigScaler's baseline
            // capture (Basis.Scale) sees an unchanged value regardless of
            // _Ready() ordering. Origin is carried over untouched.
            Basis newBasis = new Basis(anchorQuat).Scaled(originalRest.Basis.Scale);
            skeleton.SetBoneRest(boneIdx, new Transform3D(newBasis, originalRest.Origin));
            anchored++;
        }

        if (anchored == 0)
        {
            GD.PushError("[BlendRestAnchor] Anchored 0/" + AnchoredBones.Length + " bones — " +
                         "rest-anchoring had no effect at all (issue #287 fix is disabled; the #287 " +
                         "corridor sweep will fail).");
        }
        else
        {
            GD.Print($"[BlendRestAnchor] Anchored {anchored}/{AnchoredBones.Length} bone rest(s) to " +
                      "locomotion/idle's first key (issue #287).");
        }
    }

    // Mirrors PlayerRigScaler.ResolveSkeleton's search-from-Owner pattern —
    // robust to Y Bot.fbx's internal node layout instead of hardcoding it.
    private Skeleton3D ResolveSkeleton()
    {
        Node searchRoot = Owner ?? GetParent();
        if (searchRoot == null) return null;
        if (searchRoot is Skeleton3D direct) return direct;
        var matches = searchRoot.FindChildren("*", nameof(Skeleton3D), recursive: true, owned: false);
        return matches.Count > 0 ? matches[0] as Skeleton3D : null;
    }

    private static int FindRotationTrack(Animation anim, string boneName)
    {
        for (int i = 0; i < anim.GetTrackCount(); i++)
        {
            if (anim.TrackGetType(i) != Animation.TrackType.Rotation3D) continue;
            NodePath path = anim.TrackGetPath(i);
            if (path.GetSubNameCount() == 0) continue;
            if (path.GetSubName(0) == boneName) return i;
        }
        return -1;
    }
}
