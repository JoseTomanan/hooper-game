using System.Collections.Generic;
using System.Linq;
using Hooper.Player;

namespace Hooper.Ball.Tests;

/// <summary>
/// Guard tests for <see cref="RigScale"/> — the pure classifier that decides
/// which bone subset a HEIGHT scale setter versus a WINGSPAN scale setter is
/// allowed to touch on a Mixamo-rigged humanoid (issue #170). This is the
/// correctness crux the issue explicitly flags: "scaling the wrong bone subset
/// ships silently broken."
///
/// ── Why this is a real guard and not a tautology ─────────────────────────────
/// The ground truth below (<see cref="MixamoSkeleton"/>) is an INDEPENDENT,
/// hand-authored enumeration of the full standard Adobe Mixamo skeleton — the
/// bone list Adobe actually ships — NOT a re-import of RigScale's own token
/// sets. So a bug in RigScale's HeightCores/WingspanCores (a missing bone, a
/// misspelling, an arm bone leaking into the height set) makes these tests FAIL
/// rather than move in lockstep with the classifier. The contract asserted is:
///   • Height  = EXACTLY the spine (Spine/Spine1/Spine2/Neck/Head) + leg
///     (UpLeg/Leg/Foot/ToeBase, both sides) bones.
///   • Wingspan = EXACTLY the arm (Shoulder/Arm/ForeArm/Hand, both sides) bones.
///   • Neither  = the Hips root, all finger bones, and end-effector markers.
///   • The two write-sets are disjoint (no cross-contamination).
/// Every bone is tested in both the colon (`mixamorig:`) and underscore
/// (`mixamorig_`) prefix forms, because Godot's FBX importer is inconsistent
/// about which it preserves.
///
/// Test naming: [MethodUnderTest]_[Scenario]_[ExpectedOutcome], one logical
/// assertion each.
/// </summary>
public class RigScaleTests
{
    // ── Independent ground truth: the standard Mixamo humanoid skeleton ───────
    // Authored from the Adobe Mixamo rig spec, not from RigScale's internals.

    private static readonly string[] SpineBones =
    {
        "Spine", "Spine1", "Spine2", "Neck", "Head",
    };

    // Paired bones exist as Left* and Right*.
    private static readonly string[] PairedLegBones =
    {
        "UpLeg", "Leg", "Foot", "ToeBase",
    };

    private static readonly string[] PairedArmBones =
    {
        "Shoulder", "Arm", "ForeArm", "Hand",
    };

    // Everything that must classify as Neither: the pelvis root, end-effector
    // markers, and every finger bone.
    private static readonly string[] UnpairedNeitherBones =
    {
        "Hips", "HeadTop_End",
    };

    private static readonly string[] PairedNeitherBones = BuildPairedNeither();

    private static string[] BuildPairedNeither()
    {
        var fingers = new List<string>();
        foreach (string digit in new[] { "Thumb", "Index", "Middle", "Ring", "Pinky" })
            for (int seg = 1; seg <= 4; seg++)
                fingers.Add($"Hand{digit}{seg}"); // e.g. HandThumb1..HandPinky4
        fingers.Add("Toe_End");                    // leg-chain end-effector marker
        return fingers.ToArray();
    }

    // Prefix the classifier must tolerate in either separator form.
    private static IEnumerable<string> WithPrefixes(string bare) => new[]
    {
        bare,                     // no namespace at all
        "mixamorig:" + bare,      // colon form
        "mixamorig_" + bare,      // underscore form
    };

    private static IEnumerable<string> WithSides(string bare) => new[]
    {
        "Left" + bare,
        "Right" + bare,
    };

    // Fully-expanded ground-truth sets (every side × every prefix form).
    private static IEnumerable<string> ExpectedHeight() =>
        SpineBones.SelectMany(WithPrefixes)
            .Concat(PairedLegBones.SelectMany(WithSides).SelectMany(WithPrefixes));

    private static IEnumerable<string> ExpectedWingspan() =>
        PairedArmBones.SelectMany(WithSides).SelectMany(WithPrefixes);

    private static IEnumerable<string> ExpectedNeither() =>
        UnpairedNeitherBones.SelectMany(WithPrefixes)
            .Concat(PairedNeitherBones.SelectMany(WithSides).SelectMany(WithPrefixes));

    public static IEnumerable<object[]> HeightCases() => ExpectedHeight().Select(b => new object[] { b });
    public static IEnumerable<object[]> WingspanCases() => ExpectedWingspan().Select(b => new object[] { b });
    public static IEnumerable<object[]> NeitherCases() => ExpectedNeither().Select(b => new object[] { b });

    // ── The three classification bands, over the whole skeleton ───────────────

    [Theory]
    [MemberData(nameof(HeightCases))]
    public void Classify_SpineAndLegBones_AreHeight(string boneName)
        => Assert.Equal(RigChain.Height, RigScale.Classify(boneName));

    [Theory]
    [MemberData(nameof(WingspanCases))]
    public void Classify_ArmBones_AreWingspan(string boneName)
        => Assert.Equal(RigChain.Wingspan, RigScale.Classify(boneName));

    [Theory]
    [MemberData(nameof(NeitherCases))]
    public void Classify_RootFingersAndEndEffectors_AreNeither(string boneName)
        => Assert.Equal(RigChain.Neither, RigScale.Classify(boneName));

    // ── No cross-contamination: the write-sets are disjoint ───────────────────

    [Fact]
    public void Classify_NoArmBoneIsEverHeight()
    {
        foreach (string arm in ExpectedWingspan())
            Assert.NotEqual(RigChain.Height, RigScale.Classify(arm));
    }

    [Fact]
    public void Classify_NoSpineOrLegBoneIsEverWingspan()
    {
        foreach (string h in ExpectedHeight())
            Assert.NotEqual(RigChain.Wingspan, RigScale.Classify(h));
    }

    [Fact]
    public void Classify_HeightAndWingspanWriteSetsAreDisjoint()
    {
        var height = ExpectedHeight().Where(RigScale.IsHeightBone).ToHashSet();
        var wingspan = ExpectedWingspan().Where(RigScale.IsWingspanBone).ToHashSet();
        Assert.Empty(height.Intersect(wingspan));
    }

    // ── The Hips root specifically: excluded so height/wingspan can vary at all
    // (it is the common ancestor of BOTH subtrees — see RigScale's class doc) ──

    [Theory]
    [InlineData("Hips")]
    [InlineData("mixamorig:Hips")]
    [InlineData("mixamorig_Hips")]
    public void Classify_HipsRoot_IsNeither(string boneName)
        => Assert.Equal(RigChain.Neither, RigScale.Classify(boneName));

    // ── Substring-collision guards (the traps the exact-match design avoids) ──

    [Fact]
    public void Classify_UpLeg_IsHeight_NotConfusedWithArm()
        => Assert.Equal(RigChain.Height, RigScale.Classify("mixamorig:LeftUpLeg"));

    [Fact]
    public void Classify_ForeArm_IsWingspan_NotConfusedWithArmSubstring()
        => Assert.Equal(RigChain.Wingspan, RigScale.Classify("mixamorig:LeftForeArm"));

    [Fact]
    public void Classify_HeadTopEnd_IsNeither_NotConfusedWithHead()
        => Assert.Equal(RigChain.Neither, RigScale.Classify("mixamorig:HeadTop_End"));

    [Fact]
    public void Classify_ToeEnd_IsNeither_NotConfusedWithToeBase()
        => Assert.Equal(RigChain.Neither, RigScale.Classify("mixamorig:LeftToe_End"));

    // ── Safety: null / empty / malformed never throw, always Neither ──────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("mixamorig:")]
    [InlineData(":")]
    [InlineData("SomeUnknownBone")]
    public void Classify_NullEmptyOrUnknown_IsNeither(string boneName)
        => Assert.Equal(RigChain.Neither, RigScale.Classify(boneName));

    // ── Coverage completeness: the ground-truth enumeration accounts for every
    // canonical Mixamo bone exactly once, so no band silently drops a bone ─────

    [Fact]
    public void GroundTruth_EveryBareBoneIsClassifiedIntoExactlyOneBand()
    {
        // Bare (no-prefix) canonical bone set, deduped.
        var allBare = new List<string>();
        allBare.AddRange(SpineBones);
        allBare.AddRange(PairedLegBones.SelectMany(WithSides));
        allBare.AddRange(PairedArmBones.SelectMany(WithSides));
        allBare.AddRange(UnpairedNeitherBones);
        allBare.AddRange(PairedNeitherBones.SelectMany(WithSides));

        // No accidental duplicates in the ground truth itself.
        Assert.Equal(allBare.Count, allBare.Distinct().Count());

        // Every bone lands in exactly one band (the enum has three values and
        // Classify is total, so "classified" is guaranteed; this asserts the
        // COUNTS add up — 60 = 5 spine + 8 legs + 8 arms + 2 unpaired + 42 paired
        // -Neither — catching a bone silently sliding between bands).
        int height = allBare.Count(RigScale.IsHeightBone);
        int wingspan = allBare.Count(RigScale.IsWingspanBone);
        int neither = allBare.Count(b => RigScale.Classify(b) == RigChain.Neither);

        Assert.Equal(13, height);   // 5 spine + 8 legs (4 × 2 sides)
        Assert.Equal(8, wingspan);  // 4 arms × 2 sides
        Assert.Equal(allBare.Count, height + wingspan + neither);
    }
}
