using Godot;

namespace Hooper.Player;

/// <summary>
/// Godot glue node (issue #170) that gives a Mixamo-rigged player independently
/// scalable HEIGHT and WINGSPAN, replacing the single uniform Transform3D scale
/// the old Kenney chibi model used. It walks the player's <see cref="Skeleton3D"/>,
/// asks the pure <see cref="RigScale"/> classifier which chain each bone belongs
/// to, and applies the chain's scale factor via
/// <c>Skeleton3D.SetBonePoseScale</c> — so the spine+leg chain and the arm chain
/// scale separately.
///
/// The RISKY decision (which bones each factor is allowed to touch) lives in
/// <see cref="RigScale"/> and is unit-tested there against an independent Mixamo
/// bone enumeration; this node owns only the engine calls, which cannot run
/// headlessly in the unit-test project (it extends Node and touches a live
/// Skeleton3D). The real-skeleton behaviour — that a scaled rig reads correctly
/// against the NBA-2K proportion reference — is the in-editor verify #178
/// (ADR-0011 import step is a standing HITL exclusion); its state-checkable parts
/// (right bone counts scaled, default identity) can close earlier via the harness
/// (ADR-0016) using the *ForHarness observability fields below.
///
/// ── Why baseline × factor, not overwrite ─────────────────────────────────────
/// Each bone's authored scale is captured once from its REST pose
/// (<c>GetBoneRest().Basis.Scale</c>, the immutable import value, independent of
/// any animation the pose may already carry). Applied scale is always
/// baseline × factor, so:
///   • factor 1.0 reproduces the imported model's authored proportions EXACTLY
///     (the #170 "default 1.0/1.0 unchanged" acceptance) — overwriting with a raw
///     Vector3.One would instead clobber any non-unit authored scale;
///   • re-applying (a later SetHeight call) never compounds — it recomputes from
///     the baseline, not the current pose.
///
/// ── Known caveat, deferred to #178 / the retarget follow-up ───────────────────
/// SetBonePoseScale writes the ANIMATED pose. Mixamo locomotion clips animate
/// rotation (and hip position), not per-bone scale, so nothing overwrites this in
/// practice today — and the current clips are Kenney-rig-authored, so they do not
/// even bind to this skeleton until retarget lands. IF a retargeted clip ever
/// animates a scale track, it would fight this node; the fix then is to bake into
/// the bone REST (SetBoneRest) or a SkeletonModifier3D. Flagged, not solved here —
/// it needs the real imported skeleton, which is HITL.
/// </summary>
public partial class PlayerRigScaler : Node
{
    /// <summary>
    /// Optional explicit root to search for the <see cref="Skeleton3D"/> under.
    /// Left empty by default: the node searches from its <c>Owner</c> (the player
    /// scene root) so it does not depend on the FBX's internal node layout, which
    /// is only known after the HITL editor import. Set this only to disambiguate
    /// a scene with more than one skeleton.
    /// </summary>
    [Export] public NodePath SkeletonRoot { get; set; }

    /// <summary>
    /// Height multiplier for the spine+leg chain. 1.0 = authored proportions.
    /// Non-positive values are rejected (a zero/negative scale collapses or
    /// inverts the mesh) and coerced to 1.0 with a warning.
    /// </summary>
    [Export]
    public float Height
    {
        get => _height;
        set { _height = SanitizeFactor(value, nameof(Height)); if (_ready) ApplyScales(); }
    }

    /// <summary>
    /// Wingspan multiplier for the arm chain. 1.0 = authored proportions.
    /// </summary>
    [Export]
    public float Wingspan
    {
        get => _wingspan;
        set { _wingspan = SanitizeFactor(value, nameof(Wingspan)); if (_ready) ApplyScales(); }
    }

    private float _height = 1.0f;
    private float _wingspan = 1.0f;
    private bool _ready;

    private Skeleton3D _skeleton;
    // Per-bone authored rest scale, captured once so factor 1.0 is a true no-op.
    private Vector3[] _baselineScale = System.Array.Empty<Vector3>();

    // ── Harness observability (issue #178, ADR-0016). Populated by ApplyScales;
    // a harness scenario reads these to assert the right bone SUBSET was scaled
    // without solving spike #87's live bone-pose-read gap. ─────────────────────
    /// <summary>Bones the last ApplyScales classified into the height chain.</summary>
    public int HeightBonesScaledForHarness { get; private set; }
    /// <summary>Bones the last ApplyScales classified into the wingspan chain.</summary>
    public int WingspanBonesScaledForHarness { get; private set; }
    /// <summary>True once a Skeleton3D was resolved and its baseline captured.</summary>
    public bool SkeletonResolvedForHarness => _skeleton != null;

    public override void _Ready()
    {
        _skeleton = ResolveSkeleton();
        if (_skeleton == null)
        {
            GD.PrintErr("[PlayerRigScaler] No Skeleton3D found under the player scene root. " +
                        "Height/wingspan scaling disabled — check the model imported and is a " +
                        "rigged Skeleton3D (issue #170). This is expected in scenes with no rig.");
            return;
        }

        CaptureBaseline();
        _ready = true;
        ApplyScales();
    }

    /// <summary>Set the height factor and re-apply immediately.</summary>
    public void SetHeight(float factor) => Height = factor;

    /// <summary>Set the wingspan factor and re-apply immediately.</summary>
    public void SetWingspan(float factor) => Wingspan = factor;

    /// <summary>Set both factors, applying the skeleton scale only once.</summary>
    public void SetBuild(float height, float wingspan)
    {
        _height = SanitizeFactor(height, nameof(Height));
        _wingspan = SanitizeFactor(wingspan, nameof(Wingspan));
        if (_ready) ApplyScales();
    }

    private Skeleton3D ResolveSkeleton()
    {
        // Explicit override first.
        if (SkeletonRoot != null && !SkeletonRoot.IsEmpty)
        {
            Node root = GetNodeOrNull(SkeletonRoot);
            if (root is Skeleton3D direct) return direct;
            if (root != null) return FindSkeletonUnder(root);
        }

        // Default: search the player scene root (Owner), falling back to the
        // parent for a scene where this node is the root or Owner is unset.
        Node searchRoot = Owner ?? GetParent();
        return searchRoot == null ? null : FindSkeletonUnder(searchRoot);
    }

    private static Skeleton3D FindSkeletonUnder(Node root)
    {
        if (root is Skeleton3D s) return s;
        // Type-filtered recursive search — robust to the FBX's internal node names.
        var matches = root.FindChildren("*", nameof(Skeleton3D), recursive: true, owned: false);
        return matches.Count > 0 ? matches[0] as Skeleton3D : null;
    }

    private void CaptureBaseline()
    {
        int count = _skeleton.GetBoneCount();
        _baselineScale = new Vector3[count];
        for (int i = 0; i < count; i++)
            _baselineScale[i] = _skeleton.GetBoneRest(i).Basis.Scale;
    }

    private void ApplyScales()
    {
        if (_skeleton == null) return;

        int count = _skeleton.GetBoneCount();
        int heightBones = 0, wingspanBones = 0;

        for (int i = 0; i < count; i++)
        {
            RigChain chain = RigScale.Classify(_skeleton.GetBoneName(i));
            float factor = chain switch
            {
                RigChain.Height => _height,
                RigChain.Wingspan => _wingspan,
                _ => 1.0f, // Neither (Hips root, fingers, end-effectors) — untouched.
            };

            if (chain == RigChain.Height) heightBones++;
            else if (chain == RigChain.Wingspan) wingspanBones++;

            // baseline × factor: 1.0 reproduces the authored pose exactly, and a
            // re-apply recomputes from baseline rather than compounding.
            _skeleton.SetBonePoseScale(i, _baselineScale[i] * factor);
        }

        HeightBonesScaledForHarness = heightBones;
        WingspanBonesScaledForHarness = wingspanBones;

        // Doubt-review finding: a rig whose bones don't match Mixamo names would
        // classify every bone as Neither and silently no-op. Warn loudly instead —
        // this is the signal that the wrong model was imported or the bone-name
        // convention changed (e.g. a Godot-humanoid-retargeted rig using
        // LeftUpperArm/LeftLowerLeg), so RigScale's token sets need updating.
        if (count > 0 && heightBones == 0 && wingspanBones == 0)
            GD.PrintErr($"[PlayerRigScaler] Skeleton has {count} bones but none matched the " +
                        "Mixamo height or wingspan chains — scaling is a silent no-op. Is this a " +
                        "Mixamo rig? (issue #170 / RigScale bone-name convention.)");
    }

    private static float SanitizeFactor(float value, string which)
    {
        if (value > 0f) return value;
        GD.PrintErr($"[PlayerRigScaler] {which} factor {value} is non-positive; coercing to 1.0 " +
                    "(a zero/negative bone scale collapses or inverts the mesh).");
        return 1.0f;
    }
}
