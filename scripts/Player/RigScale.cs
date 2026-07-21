namespace Hooper.Player;

/// <summary>
/// Which independently-scalable chain of a Mixamo-rigged humanoid a given
/// skeleton bone belongs to (issue #170).
/// </summary>
public enum RigChain
{
    /// <summary>Bone belongs to neither scale chain — left at its authored pose
    /// scale by both setters (e.g. the <c>Hips</c> root, finger bones, end-effector
    /// markers). See <see cref="RigScale"/> for why <c>Hips</c> is excluded.</summary>
    Neither,

    /// <summary>Spine + leg chain — scaled by the HEIGHT setter.</summary>
    Height,

    /// <summary>Arm chain — scaled by the WINGSPAN setter.</summary>
    Wingspan,
}

/// <summary>
/// Pure C# classifier mapping a Mixamo skeleton bone name onto the
/// <see cref="RigChain"/> a height- or wingspan-scale setter should touch — no
/// Godot Node inheritance, no engine singletons, so it is unit-testable without
/// a running Godot instance (exactly as <see cref="MoveAnimResolver"/> and
/// <see cref="FacingResolver"/> are). The <c>PlayerRigScaler</c> glue node walks
/// a real <c>Skeleton3D</c>'s bones, calls <see cref="Classify"/> on each name,
/// and applies the corresponding chain scale via
/// <c>Skeleton3D.SetBonePoseScale</c> — this file owns the RISKY decision (which
/// bone subset each setter writes to); the node owns only the engine call.
///
/// This is the correctness crux of #170: the issue explicitly flags that
/// "scaling the wrong bone subset ships silently broken." The partition below is
/// therefore proven by <c>RigScaleTests</c> against an INDEPENDENT enumeration of
/// the full standard Mixamo skeleton (not the classifier's own token sets), so
/// the guard is a genuine cross-check rather than a tautology.
///
/// ── The independence contract, and its ONE documented hierarchical caveat ────
/// The height setter must touch ONLY spine+leg bones and the wingspan setter
/// ONLY arm bones (no cross-contamination) — that WRITE-SET partition is what
/// the guard tests assert and is exact and complete here.
///
/// VISUAL independence is subtler and is NOT fully guaranteed by the write-set
/// partition, because <c>Skeleton3D.SetBonePoseScale</c> propagates to child
/// bones: the arm chains are children of <c>Spine2</c>, so scaling the spine for
/// height does bleed into the arms' world transforms. Two deliberate choices
/// bound that bleed as far as a name-based classifier can:
///   1. <c>Hips</c> (the pelvis root, common ancestor of BOTH the spine/leg and
///      the arm subtrees) is classified <see cref="RigChain.Neither"/> — scaling
///      it would scale the entire body uniformly, which is exactly the single
///      uniform Transform3D scale #170 exists to replace. Excluding it is what
///      lets height and wingspan vary at all rather than one dragging the other.
///   2. Legs and arms live in DISJOINT subtrees (legs under Hips, arms under
///      Spine2), so leg scaling and arm scaling never contaminate each other;
///      only the spine portion of the height chain bleeds into the arms.
/// The residual spine→arm bleed, and whether to refine it (e.g. counter-scaling
/// the shoulders, or per-axis rather than uniform scale once the imported bone
/// axes are known), is an explicit visual/feel judgment left to the in-editor
/// verify #178 against the NBA-2K proportion reference — it needs the real
/// imported skeleton, which is a standing HITL exclusion (ADR-0011). This file
/// deliberately does not guess at it.
///
/// ── Bone-name robustness ──────────────────────────────────────────────────────
/// Classification matches the bone's CORE token (the part after any
/// <c>mixamorig:</c> / <c>mixamorig_</c> prefix and any <c>Left</c>/<c>Right</c>
/// side prefix), case-insensitively and by EXACT token equality — not substring
/// — so <c>Leg</c> (lower leg, height) is never confused with the <c>Leg</c>
/// inside <c>UpLeg</c>, nor <c>Arm</c> (wingspan) with the <c>Arm</c> inside
/// <c>ForeArm</c>. Godot's FBX importer is inconsistent about whether it
/// preserves the <c>mixamorig:</c> colon, so the prefix strip tolerates both the
/// colon and underscore forms.
/// </summary>
public static class RigScale
{
    // Spine + leg cores (Hips intentionally excluded — see class doc). Exact,
    // side-stripped tokens; comparison is case-insensitive via NormalizeCore.
    private static readonly System.Collections.Generic.HashSet<string> HeightCores = new()
    {
        // Spine chain (root Hips excluded)
        "spine", "spine1", "spine2", "neck", "head",
        // Leg chains (per side)
        "upleg", "leg", "foot", "toebase",
    };

    // Arm cores. Fingers (Hand<Digit>N) are NOT here — they are children of Hand
    // and inherit its scale; scaling them would change hand size, not wingspan.
    private static readonly System.Collections.Generic.HashSet<string> WingspanCores = new()
    {
        "shoulder", "arm", "forearm", "hand",
    };

    /// <summary>
    /// Returns the <see cref="RigChain"/> the given skeleton bone belongs to.
    /// A null/empty/unrecognized name — including the <c>Hips</c> root, finger
    /// bones, and end-effector markers — returns <see cref="RigChain.Neither"/>,
    /// so a caller that scales only the two named chains leaves everything else at
    /// its authored pose (making default 1.0/1.0 scale a no-op on the whole rig).
    /// </summary>
    public static RigChain Classify(string boneName)
    {
        string core = NormalizeCore(boneName);
        if (core.Length == 0)
            return RigChain.Neither;
        if (HeightCores.Contains(core))
            return RigChain.Height;
        if (WingspanCores.Contains(core))
            return RigChain.Wingspan;
        return RigChain.Neither;
    }

    /// <summary>True iff the height setter should scale this bone.</summary>
    public static bool IsHeightBone(string boneName) => Classify(boneName) == RigChain.Height;

    /// <summary>True iff the wingspan setter should scale this bone.</summary>
    public static bool IsWingspanBone(string boneName) => Classify(boneName) == RigChain.Wingspan;

    /// <summary>
    /// Reduces a raw bone name to its lowercase core token: drops any
    /// <c>mixamorig:</c>/<c>mixamorig_</c> namespace prefix and any leading
    /// <c>Left</c>/<c>Right</c> side word, leaving e.g. <c>"mixamorig:LeftForeArm"</c>
    /// → <c>"forearm"</c>. Returns "" for null/whitespace.
    /// </summary>
    private static string NormalizeCore(string boneName)
    {
        if (string.IsNullOrWhiteSpace(boneName))
            return string.Empty;

        string s = boneName.Trim();

        // Drop the "mixamorig" namespace prefix in whichever separator form the
        // importer produced ("mixamorig:Hips" or "mixamorig_Hips"). Take the part
        // after the last ':' first (covers the colon form and any namespace), then
        // strip a leading "mixamorig_"/"mixamorig".
        int colon = s.LastIndexOf(':');
        if (colon >= 0)
            s = s.Substring(colon + 1);
        if (s.StartsWith("mixamorig_", System.StringComparison.OrdinalIgnoreCase))
            s = s.Substring("mixamorig_".Length);
        else if (s.StartsWith("mixamorig", System.StringComparison.OrdinalIgnoreCase))
            s = s.Substring("mixamorig".Length);

        // Strip a leading side word. No real core token begins with "left"/"right",
        // so this is unambiguous. (Mixamo uses the prefix form; a suffix form like
        // ".L" is not produced by this rig, so it is not handled.)
        if (s.StartsWith("left", System.StringComparison.OrdinalIgnoreCase) && s.Length > 4)
            s = s.Substring(4);
        else if (s.StartsWith("right", System.StringComparison.OrdinalIgnoreCase) && s.Length > 5)
            s = s.Substring(5);

        return s.ToLowerInvariant();
    }
}
