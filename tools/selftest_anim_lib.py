"""Self-test for the NEW machinery in `blender_anim_lib` (#315).

    "$BLENDER" --background --python-exit-code 1 \
        --python tools/selftest_anim_lib.py -- assets/Dribble.fbx

Exits nonzero on any failure; prints `SELFTEST_OK` on success.

WHY THIS EXISTS
═══════════════
The extraction of `author_dribble_move.py` is proven by re-running it and
comparing poses against the committed `assets/dribble_move_authored.fbx` -- exact
zero, all 4160 (frame,bone) pairs. That gate is strong, but it covers only what
the dribble authorer actually calls, and the dribble authorer poses LEGS AND
SPINE ONLY. It never touches the arms.

`aim_arm` is therefore new code with zero coverage from that gate, and it is what
steal (#282), block (#283), contest (#314), layup (#313), the jump-shot re-author
(#316) and every ball-carrying dribble move will be built on. Shipping it
unproven would put an unverified primitive underneath a dozen downstream clips,
which is precisely the "wrong-but-confident answer that is expensive to discover
later" that the doubt-driven discipline exists to prevent.

The over-reach case is not a nicety. An arm's reach is ~0.55 m against a leg's
~0.83 m, so a `hand_target` sized by eye against leg geometry silently exceeds
it; a clamped two-link solve then yields a locked, straight arm that reads as a
mannequin rather than a reach. Handoff 00 requires callers treat that as a
FAILURE, so this asserts it raises rather than logs.
"""
import math
import os
import sys

import bpy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import blender_anim_lib as lib  # noqa: E402

FPS = 30
# Wrist placement tolerance, in metres. The two-link solve is exact by
# construction (it places the elbow so the wrist is exactly `ulna` from it, then
# aims the forearm at the target), so anything above float noise means the chain
# is wired wrong -- e.g. a bone whose tail does not coincide with its child's
# head, which would show up as a constant offset.
WRIST_TOL_M = 1e-4

_failures = []


def check(name, passed, detail=""):
    lib.report(f"selftest_{name}", "PASS" if passed else f"FAIL {detail}")
    if not passed:
        _failures.append(f"{name}: {detail}")


def main():
    src = sys.argv[sys.argv.index("--") + 1]
    arm, f0, f1 = lib.load_source(src, FPS)
    geom = lib.RigGeometry(arm)
    geom.log_summary()
    lib.enter_pose_mode(arm)
    bpy.context.scene.frame_set(f0)

    # ---- 0. the L/R chains sit on opposite sides, and WHICH side -----------
    # THE SYMMETRIC BLIND SPOT. Every assertion in section 1 below would still
    # pass if ARM_CHAIN["L"] were copy-pasted to the Right bones: the wrist
    # checks are computed from whatever shoulder the chain reports, so the IK
    # reaches its (mirrored) target either way, and the hint/over-reach/
    # degenerate checks are all side-agnostic. A symmetric assertion cannot
    # detect a symmetric error -- the #255 mirror-bug lesson.
    #
    # THE TWO LATERAL AXES ARE ASSERTED SEPARATELY, because they make different
    # claims (#320). `geom.lateral` is a BASIS vector whose sign is load-bearing
    # and whose anatomy is meaningless -- it points at the character's LEFT, and
    # it must keep doing so, because `plant_foot` hands it to `aim_matrix` as a
    # ROLL reference (flipping it rolls every posed leg bone 180 deg; measured at
    # 179.99 deg). `geom.body_right` is the ANATOMICAL axis, and it must point at
    # the character's right.
    #
    # THIS IS THE ASSERTION THAT WOULD HAVE CAUGHT THE ORIGINAL DEFECT. The
    # library used to expose the basis vector under the name `right`, and six
    # authoring scripts each independently worked around it with a local
    # `-geom.right`. A gate on `body_right`'s anatomy applied to that old `right`
    # fails outright -- see the mutation note on `body_right_points_rig_right`
    # below.
    hips_head = arm.pose.bones[lib.HIPS].head.copy()
    on_lateral, on_body_right = {}, {}
    for side in ("L", "R"):
        shoulder = arm.pose.bones[lib.ARM_CHAIN[side][0]].head - hips_head
        on_lateral[side] = shoulder.dot(geom.lateral)
        on_body_right[side] = shoulder.dot(geom.body_right)
        lib.report(f"shoulder_on_lateral_{side}_m",
                   f"{geom.to_m(on_lateral[side]):+.4f}")
        lib.report(f"shoulder_on_body_right_{side}_m",
                   f"{geom.to_m(on_body_right[side]):+.4f}")

    # Catches the copy-paste this section exists for: two chains resolving to the
    # same bones land on the same side (or the same point).
    check("arm_chains_are_opposite_sides",
          on_lateral["L"] * on_lateral["R"] < 0.0,
          f"L and R shoulders are on the same side of the rig "
          f"(L={geom.to_m(on_lateral['L']):+.4f} m, "
          f"R={geom.to_m(on_lateral['R']):+.4f} m); "
          f"ARM_CHAIN is wired to one side twice")

    # ---- the BASIS axis: pinned, because its sign drives bone ROLL -----------
    # Not an anatomical claim. This exists so that a change to `derive_axes`' sign
    # handling cannot slip through unnoticed: it would silently roll every
    # `aim_matrix`-posed bone 180 deg and move every authored clip.
    check("lateral_axis_sign_pinned",
          on_lateral["L"] > 0.0 > on_lateral["R"],
          f"`geom.lateral`'s sign CHANGED (L={geom.to_m(on_lateral['L']):+.4f}, "
          f"R={geom.to_m(on_lateral['R']):+.4f}). This axis is `aim_matrix`'s roll "
          f"reference, so flipping it rotates every posed bone 180 deg about its "
          f"own axis and moves every authored clip -- re-run every equivalence "
          f"gate before accepting this, and update README-blender.md.")

    # ---- the ANATOMICAL axis: NON-SYMMETRIC, and it names a side ------------
    # THE #320 GATE. It must fail if the sides swap, so it names them: the RIGHT
    # shoulder is on the POSITIVE side of `body_right` and the LEFT shoulder on
    # the negative side. A symmetric assertion cannot ever detect a mirror error
    # -- that is precisely why the original defect survived #300 and #315, where
    # every check was either side-agnostic or read the side off whichever chain
    # it was already given (the #255 lesson).
    #
    # MUTATION-PROVEN, not argued: forcing `derive_body_right` to return
    # `lateral` unchanged -- i.e. reinstating exactly the old, misnamed `right`
    # -- makes this read L=+0.1343 R=-0.1804 and FAIL, while
    # `lateral_axis_sign_pinned` and every other check in this file stay green.
    # So this gate is a real discriminator and not a restatement of the pin above.
    #
    # KNOWN LIMIT, so this is not oversold: it reads `ARM_CHAIN[side][0]`, i.e.
    # `mixamorig:{Left,Right}Arm` -- the SAME two bones `derive_body_right`
    # builds its shoulder span from. Posed-vs-rest makes it a real discriminator
    # for an `ARM_CHAIN` mis-wiring (proven by the mutation above), but it shares
    # that function's landmarks, so it CANNOT catch a rig whose `LeftArm` and
    # `RightArm` labels are themselves swapped -- the #255-class mirror failure.
    #
    # No purely axis-derived alternative exists: `up` and `forward` do not
    # determine handedness on a mirror-symmetric skeleton, so SOME bone label
    # must be trusted. The limitation is inherent, not an oversight.
    check("body_right_points_rig_right",
          on_body_right["R"] > 0.0 > on_body_right["L"],
          f"`geom.body_right` does not point at the character's RIGHT "
          f"(R shoulder={geom.to_m(on_body_right['R']):+.4f} m, "
          f"L shoulder={geom.to_m(on_body_right['L']):+.4f} m). Every authored "
          f"clip places hands and feet along this axis, so an inverted sign puts "
          f"every one of them on the WRONG SIDE OF THE BODY.")

    # And `body_right` must be EXACTLY +/-`lateral`, component for component.
    # This is the property the whole #320 approach rests on: it is what lets a
    # call site move from `-geom.right` to `geom.body_right` without perturbing a
    # single exported clip, so it is asserted rather than assumed.
    #
    # Asserted BITWISE, on the components, and not as a dot product -- which was
    # the first attempt and is the wrong instrument. `mathutils` vectors are
    # FLOAT32, so `.normalized()` leaves a residual: `lateral.dot(-lateral)`
    # measured -1.0000000053, i.e. |lateral|^2 off unit by 5.3e-9. A dot-product
    # gate therefore needs a tolerance derived from float32 epsilon, and it would
    # pass for a re-derived vector that merely happens to be antiparallel to
    # within that tolerance -- which is exactly the thing being ruled out. IEEE
    # negation is exact, so if `body_right` really is `+/-lateral` then equality
    # holds with NO tolerance at all, and a test that needs none cannot be
    # loosened later.
    same = all(b == l for b, l in zip(geom.body_right, geom.lateral))
    negated = all(b == -l for b, l in zip(geom.body_right, geom.lateral))
    lib.report("body_right_vs_lateral",
               "identical" if same else "negated" if negated else "NEITHER")
    check("body_right_is_exactly_plus_or_minus_lateral", same or negated,
          f"`body_right` {tuple(geom.body_right)} is neither exactly `lateral` "
          f"{tuple(geom.lateral)} nor its exact negation. It must be +/-`lateral`, "
          f"never an independently re-derived vector: any deviation moves every "
          f"authored clip and breaks every equivalence gate.")

    for side in ("L", "R"):
        humerus_u, ulna_u = lib.arm_lengths(arm, side)
        reach_u = humerus_u + ulna_u
        shoulder = arm.pose.bones[lib.ARM_CHAIN[side][0]].head.copy()

        # ---- 1. reachable targets land the wrist exactly ---------------------
        # Three directions, each at a different fraction of reach, so a
        # direction-dependent error cannot hide behind one lucky pose. The elbow
        # hint points down and outward, which is where a real elbow goes for a
        # reach in front of the body.
        # `outward` = away from the midline for THIS side, so the hint and the
        # `side_up` target below mean the same anatomical thing on both arms.
        # Built from `body_right`, not the basis axis (#320): these were written
        # against the old `geom.right`, which points at the character's LEFT, so
        # "outward for R" was silently INBOARD. The assertions in this section are
        # side-agnostic (does the wrist reach, does the elbow follow the hint), so
        # that never made them fail -- it just meant the poses being tested were
        # the mirror of the ones the code reads as testing.
        outward = geom.body_right if side == "R" else -geom.body_right
        hint = (-geom.up * 0.8 + outward * 0.6)
        for label, direction, frac in (
            ("fwd_low", geom.forward * 0.8 - geom.up * 0.6, 0.70),
            ("fwd_flat", geom.forward * 1.0, 0.85),
            ("side_up", geom.forward * 0.3 + geom.up * 0.5 + outward * 0.8, 0.60),
        ):
            target = shoulder + direction.normalized() * (reach_u * frac)
            err_u = lib.aim_arm(arm, side, target, hint, geom)
            err_m = geom.to_m(err_u)
            lib.report(f"wrist_err_{side}_{label}_m", f"{err_m:.8f}")
            check(f"wrist_reaches_{side}_{label}", err_m < WRIST_TOL_M,
                  f"wrist off target by {err_m:.6f} m (tol {WRIST_TOL_M})")

        # ---- 2. the elbow actually goes where it was hinted -----------------
        # A two-link solve has a whole circle of valid elbow positions; the hint
        # is what picks one. If the hint were ignored the arm would still reach
        # the target, so the wrist check above CANNOT catch a broken bend plane.
        # Flipping the hint must move the elbow to the other side.
        target = shoulder + (geom.forward * 1.0).normalized() * (reach_u * 0.80)
        lib.aim_arm(arm, side, target, -geom.up, geom)
        elbow_down = arm.pose.bones[lib.ARM_CHAIN[side][1]].head.copy()
        lib.aim_arm(arm, side, target, geom.up, geom)
        elbow_up = arm.pose.bones[lib.ARM_CHAIN[side][1]].head.copy()
        separation_m = geom.to_m((elbow_up - elbow_down).length)
        rose = (elbow_up - elbow_down).dot(geom.up) > 0.0
        lib.report(f"elbow_hint_separation_{side}_m", f"{separation_m:.4f}")
        check(f"elbow_follows_hint_{side}", separation_m > 0.02 and rose,
              f"flipping the hint moved the elbow {separation_m:.4f} m, "
              f"upward={rose}")

        # ---- 3. over-reach is FATAL, not clamped ----------------------------
        too_far = shoulder + (geom.forward * 1.0).normalized() * (reach_u * 1.5)
        try:
            lib.aim_arm(arm, side, too_far, hint, geom)
            check(f"overreach_fails_{side}", False,
                  "aim_arm accepted a target 1.5x beyond reach")
        except SystemExit:
            check(f"overreach_fails_{side}", True)

        # ---- 4. a hint parallel to the reach names no plane -> FATAL --------
        reach_dir = (target - shoulder).normalized()
        try:
            lib.aim_arm(arm, side, target, reach_dir, geom)
            check(f"degenerate_hint_fails_{side}", False,
                  "aim_arm accepted a hint parallel to the reach direction")
        except SystemExit:
            check(f"degenerate_hint_fails_{side}", True)

    # ---- 4b. plant_foot lands the ankle exactly, LATERAL targets included ----
    # THE #321 GATE. `plant_foot` used to build its femur rotation axis as the
    # rig's `right`, and a rotation about `right` only moves the component of
    # `dir_ankle` PERPENDICULAR to `right`. So a laterally-offset ankle target
    # achieved strictly less than the requested hip angle --
    #
    #     cos(theta_eff) = cos^2(alpha) + sin^2(alpha) * cos(hip_offset)
    #
    # -- the knee landed off the IK circle, the tibia was then aimed at the true
    # target from that wrong knee, and the ankle fell SHORT. Measured on the
    # dribble (a nearly-planar gait) at 0.029890 m; a genuinely lateral stance is
    # materially worse. The fix mirrors `aim_arm`: build the axis as
    # `dir_ankle.cross(forward)`, perpendicular by construction.
    #
    # THE SAGITTAL CASE IS THE CONTROL, and it is load-bearing. It was already
    # near-exact before the fix (alpha ~= 90 deg makes the shortfall vanish), so
    # a suite containing only sagittal cases would have reported green on the
    # buggy solver -- which is exactly what happened for two milestones. The
    # lateral cases are what discriminate; the sagittal one proves the harness
    # itself is not simply measuring zero for an unrelated reason.
    #
    # Both signs on both sides, deliberately: an inboard target crosses the
    # midline (the leg reaches ACROSS the body, a euro-step/crossover-step
    # shape) and an outboard one is a defensive slide. A single sign per side
    # would leave the mirror untested -- the #255 lesson.
    #
    # `outward` is derived per side from `body_right` (#320) so that "outboard"
    # and "inboard" in the labels below are ANATOMICALLY true on both legs. With
    # the old basis axis a fixed sign meant outboard on one side and inboard on
    # the other, so the four cases were really two cases run twice.
    # IMPORTED, not restated (#344). These 8 checks and the gate every authoring
    # script runs at export must be the SAME number: while this file carried its
    # own 1e-4 the two merely happened to agree, so tightening the authoring gate
    # would have silently left these bounding something looser than the gate
    # demanded -- a suite that passes while the thing it certifies fails.
    ANKLE_TOL_M = lib.ANKLE_IK_TOL_M
    for side in ("L", "R"):
        # Deliberately not `hip_head` -- that name is the HIPS bone above and is
        # read again by section 6g. This is the femur root.
        leg_hip = arm.pose.bones[lib.LEG_CHAIN[side][0]].head.copy()
        outward = geom.body_right if side == "R" else -geom.body_right
        for label, lat_m, down_m, fore_m in (
            ("sagittal_control", 0.00, 0.60, 0.25),
            ("lateral_outboard", 0.40, 0.60, 0.00),
            ("lateral_inboard", -0.40, 0.60, 0.00),
            ("lateral_fore", 0.40, 0.55, 0.20),
        ):
            # `lat_m` positive = away from the midline, negative = across it.
            target = (leg_hip
                      + outward * geom.m(lat_m)
                      - geom.up * geom.m(down_m)
                      + geom.forward * geom.m(fore_m))
            _solved, err_u = lib.plant_foot(
                arm, side, target, geom.forward, geom)
            err_m = geom.to_m(err_u)
            lib.report(f"ankle_err_{side}_{label}_m", f"{err_m:.8f}")
            check(f"ankle_reaches_{side}_{label}", err_m < ANKLE_TOL_M,
                  f"ankle off target by {err_m:.6f} m (tol {ANKLE_TOL_M}); a "
                  f"rotation axis that is not perpendicular to the reach "
                  f"direction cannot land the knee on the IK circle")

            # ---- and the knee bends FORWARD ------------------------------
            # THE ASSERTION THE ANKLE CHECK CANNOT MAKE, and the one that
            # matters most. A two-link solve has a whole CIRCLE of valid knee
            # positions, and every one of them lands the ankle exactly on
            # target -- so `ankle_reaches_*` above reads 1e-7 for a knee that
            # bends BACKWARD just as happily as for one that bends forward.
            # This is `elbow_follows_hint`'s lesson (section 2), transplanted
            # to the leg, where it was missing: #321 changed the rotation
            # SENSE from `Rotation(-hip_offset, right)` to
            # `Rotation(+hip_offset, dir_ankle x forward)`, and getting that
            # sign wrong would produce an inverted, visually catastrophic knee
            # that every pre-existing gate in this library passes.
            #
            # Measured as displacement of the knee from the hip->ankle CHORD,
            # projected on `forward` -- the axis `derive_axes` verifies
            # anatomically. The chord, not the hip: a leg reaching forward
            # puts the knee ahead of the hip for trivial reasons, so
            # subtracting the chord is what isolates the BEND from the reach.
            knee = arm.pose.bones[lib.LEG_CHAIN[side][1]].head.copy()
            chord = (target - leg_hip)
            from_hip = knee - leg_hip
            # Remove the along-chord component; what remains is the bend.
            chord_n = chord.normalized()
            bend = from_hip - chord_n * from_hip.dot(chord_n)
            bend_fwd_m = geom.to_m(bend.dot(geom.forward))
            lib.report(f"knee_bend_forward_{side}_{label}_m",
                       f"{bend_fwd_m:+.4f}")
            # Floor, not merely >0: a near-straight leg has a tiny bend whose
            # sign is float noise, so a bare sign test could pass vacuously.
            # 0.02 m is far above noise and far below the ~0.07-0.13 m these
            # poses actually produce.
            check(f"knee_bends_forward_{side}_{label}", bend_fwd_m > 0.02,
                  f"the knee displaces {bend_fwd_m:+.4f} m along `forward` "
                  f"from the hip->ankle chord; a human knee bends FORWARD, so "
                  f"a non-positive value means the femur rotation SENSE is "
                  f"inverted -- which the ankle-error check above cannot see, "
                  f"because both bend directions land the ankle exactly")

    # ---- 4c. a fore/aft target names no bend plane -> FATAL -----------------
    # `dir_ankle.cross(forward)` is the zero vector when the ankle target sits
    # directly fore or aft of the hip at hip height. `aim_arm` already refuses
    # its own degenerate hint rather than silently picking a plane; this asserts
    # `plant_foot` mirrors that handling instead of introducing a second
    # convention (or, worse, normalizing a zero vector into NaN and exporting
    # it). The case is anatomically unreachable for a real foot plant -- an
    # ankle at hip height, straight ahead -- which is precisely why it must
    # raise rather than be handled: reaching it means the spec is wrong.
    for side in ("L", "R"):
        leg_hip = arm.pose.bones[lib.LEG_CHAIN[side][0]].head.copy()
        degenerate = leg_hip + geom.forward * (geom.leg_reach * 0.7)
        try:
            lib.plant_foot(arm, side, degenerate, geom.forward, geom)
            check(f"plant_foot_degenerate_fails_{side}", False,
                  "plant_foot accepted an ankle target directly fore of the hip, "
                  "which names no bend plane")
        except SystemExit:
            check(f"plant_foot_degenerate_fails_{side}", True)

    # ---- 4d. the knee HINGE is aligned with the bend plane ------------------
    # THE #338(1) GATE, and the one that discriminates. `plant_foot` hands ONE
    # `side_axis` to THREE `aim_matrix` calls, and `aim_matrix` uses it as a
    # bone ROLL reference. Until #338 that axis was `geom.lateral` for all three.
    #
    # For the FEMUR and TIBIA that is wrong, and wrong CONTINUOUSLY rather than
    # only at a singularity. A knee is a hinge, so the femur's roll decides which
    # way the kneecap faces; it should face along the direction the knee actually
    # bends, i.e. the femur's side axis should be the NORMAL of the hip-knee-ankle
    # plane. `geom.lateral` is that normal only for a purely sagittal leg. As the
    # leg abducts, the two diverge by the abduction angle, and the kneecap ends up
    # facing somewhere the knee does not bend.
    #
    # MEASURED on the real authoring runs, as the angle between `geom.lateral`
    # and the bend-plane normal over every `plant_foot` call each script makes:
    #
    #     dribble 22.47 deg (mean 18.82)   layup 56.53 (mean 7.94)
    #     block   18.11        ( 9.64)     behindtheback 12.74 ( 5.93)
    #     contest  9.27        ( 6.69)     steal          7.47 ( 2.88)
    #     jabstep  6.24        ( 5.18)
    #
    # -- so this is not a hypothetical corner. Every shipped clip already carries
    # it, the dribble worst-case by 22 deg.
    #
    # WHAT THE ISSUE OVERSTATED, recorded so the next reader is not hunting for
    # something that cannot happen: #338 frames this as a "visible leg-twist pop"
    # from a roll DISCONTINUITY. The discontinuity is real -- measured at
    # 1800 deg of roll per degree of target movement -- but it sits at exactly
    # 90 deg of abduction AND near-full extension, which means an ankle at hip
    # height at the end of a straight leg. No foot plant reaches that. The
    # reachable defect is the continuous misalignment above, which is why this
    # gate measures alignment rather than trying to provoke a pop.
    #
    # Post-fix the femur's side axis IS the bend-plane normal by construction, so
    # this reads +1.000000 exactly rather than merely within tolerance.
    #
    # THE COMPARISON IS SIGNED, AND THAT IS THE WHOLE POINT (#339 review).
    # Written as `abs(local_x.dot(normal))` this gate is a MAGNITUDE test, and a
    # 180 deg roll -- the single defect `plant_foot`'s own comment calls "the
    # whole ballgame" -- flips `local_x` straight back onto +1.0 and passes.
    # Mutation-proven: reverting `hinge_axis = -axis` to the unnegated `axis`
    # that #338 literally asked for left this whole file green, exit 0, every
    # `hinge_align_*` reading 1.000000, the sagittal controls included.
    #
    # The expected sign is DERIVED, not observed, so this asserts the geometry
    # rather than whatever the solver happens to emit. `plant_foot` builds
    # `femur_dir = R(+hip_offset, axis) @ dir_ankle`, and for a rotation by theta
    # about a unit axis, `a x R_theta(a) = sin(theta) * axis`. The realized
    # normal below is `femur_dir x (ankle - knee) = d * (femur_dir x dir_ankle)`
    # = `-d * sin(hip_offset) * axis`, and `hip_offset` is in (0, pi) so the sine
    # is strictly positive. Normalized, the realized normal is exactly `-axis` --
    # which IS `hinge_axis`. Hence +1, and hence a mutation reads -1.
    HINGE_TOL = 0.9999          # signed cos -- 0.81 deg of misalignment
    SOLE_TOL_DEG = 0.5
    for side in ("L", "R"):
        leg_hip = arm.pose.bones[lib.LEG_CHAIN[side][0]].head.copy()
        outward = geom.body_right if side == "R" else -geom.body_right
        for label, lat_m, down_m, fore_m in (
            ("sagittal_control", 0.00, 0.60, 0.25),
            ("abducted_22deg", 0.24, 0.60, 0.00),
            ("abducted_45deg", 0.55, 0.55, 0.00),
        ):
            target = (leg_hip
                      + outward * geom.m(lat_m)
                      - geom.up * geom.m(down_m)
                      + geom.forward * geom.m(fore_m))
            lib.plant_foot(arm, side, target, geom.forward, geom)

            # The REALIZED bend plane, read back off the posed skeleton rather
            # than recomputed from the spec -- so this measures what was actually
            # exported, not what the solver intended.
            hip_p = arm.pose.bones[lib.LEG_CHAIN[side][0]].head.copy()
            knee_p = arm.pose.bones[lib.LEG_CHAIN[side][1]].head.copy()
            ankle_p = arm.pose.bones[lib.LEG_CHAIN[side][2]].head.copy()
            normal = (knee_p - hip_p).cross(ankle_p - knee_p)
            if normal.length < 1e-9:
                # A perfectly straight leg spans no plane, so there is no hinge
                # to align and this case would assert nothing. Say so instead of
                # passing vacuously.
                check(f"hinge_plane_exists_{side}_{label}", False,
                      "the posed leg is straight, so the hip-knee-ankle plane is "
                      "degenerate and the hinge assertion below would be vacuous")
                continue
            normal.normalize()

            for bone_label, chain_idx in (("femur", 0), ("tibia", 1)):
                bone = arm.pose.bones[lib.LEG_CHAIN[side][chain_idx]]
                # Column 0 of a pose matrix is the bone's local X in armature
                # space -- the axis `aim_matrix` builds from `side_axis`.
                local_x = bone.matrix.col[0].to_3d().normalized()
                align = local_x.dot(normal)
                lib.report(f"hinge_align_{side}_{label}_{bone_label}",
                           f"{align:+.6f}")
                check(f"hinge_aligned_{side}_{label}_{bone_label}",
                      align > HINGE_TOL,
                      f"the {bone_label}'s roll axis is {align:+.6f} aligned "
                      f"with the hip-knee-ankle plane normal (need "
                      f">+{HINGE_TOL}); the kneecap therefore faces "
                      f"{math.degrees(math.acos(max(-1.0, min(1.0, align)))):.2f}"
                      f" deg away from the direction the knee actually bends. A "
                      f"value near -1 means the roll reference's SIGN is "
                      f"inverted -- the axis is right, the bone is rolled "
                      f"180 deg (see `hinge_axis` in `plant_foot`)")

            # ---- and the SOLE stays LEVEL ----------------------------------
            # THE GATE THAT PROTECTS THE SCOPE OF THE FIX, and the reason
            # `plant_foot` does NOT simply swap its one `side_axis` wholesale.
            #
            # The foot's roll is not a hinge question -- it is the orientation of
            # the SOLE, which should stay flat on the floor no matter how abducted
            # the leg is. `geom.lateral` is horizontal, which is exactly why every
            # authoring run measures 0.0000 deg of sole tilt today.
            #
            # MUTATION-PROVEN, and this is the mutation that matters: passing the
            # bend-plane normal to the FOOT as well -- i.e. #338's proposed
            # one-line fix, applied uniformly -- rolls the planted foot onto its
            # edge by 22.16 deg on the dribble and 46.35 deg on the layup, while
            # every other gate in this file, `verify_grounded` included, stays
            # green. `verify_grounded` cannot see it: it measures ankle and toe
            # HEIGHTS, and a foot rolled about its own long axis keeps both.
            foot = arm.pose.bones[lib.LEG_CHAIN[side][2]]
            sole_x = foot.matrix.col[0].to_3d().normalized()
            tilt_deg = math.degrees(math.asin(
                max(-1.0, min(1.0, sole_x.dot(geom.up)))))
            lib.report(f"sole_tilt_{side}_{label}_deg", f"{tilt_deg:+.4f}")
            check(f"sole_stays_level_{side}_{label}",
                  abs(tilt_deg) < SOLE_TOL_DEG,
                  f"the planted sole is rolled {tilt_deg:+.4f} deg out of "
                  f"horizontal (tol {SOLE_TOL_DEG}); the foot is standing on its "
                  f"edge. The foot's roll reference must stay HORIZONTAL "
                  f"(`geom.lateral`), not follow the knee's bend plane")

    # ---- 4e. the ill-conditioned corner the issue asked for -----------------
    # #338 asks for "an abduction case near the ill-conditioned region (femur
    # within a few degrees of `lateral`)". That corner needs BOTH near-full
    # abduction AND near-full extension: `plant_foot` aims the femur at
    # `dir_ankle` rotated by `hip_offset`, so a BENT knee lifts the femur out of
    # the lateral axis by `hip_offset` all on its own, and the Gram-Schmidt
    # residual never collapses. Straightening the leg is what removes that
    # margin.
    #
    # Anatomically this is a full side-split at hip height -- unreachable for a
    # real plant, which is the honest reason it is a library-level gate here and
    # not a clip-level one. It is included because it is where the old reference
    # degrades WITHOUT BOUND, and a fix justified by conditioning should be
    # asserted at the conditioning limit.
    for side in ("L", "R"):
        leg_hip = arm.pose.bones[lib.LEG_CHAIN[side][0]].head.copy()
        outward = geom.body_right if side == "R" else -geom.body_right
        target = leg_hip + outward * (geom.leg_reach * 0.97)
        lib.plant_foot(arm, side, target, geom.forward, geom)
        hip_p = arm.pose.bones[lib.LEG_CHAIN[side][0]].head.copy()
        knee_p = arm.pose.bones[lib.LEG_CHAIN[side][1]].head.copy()
        ankle_p = arm.pose.bones[lib.LEG_CHAIN[side][2]].head.copy()
        normal = (knee_p - hip_p).cross(ankle_p - knee_p)
        check(f"extreme_hinge_plane_exists_{side}", normal.length > 1e-9,
              "the leg came out perfectly straight, so this case asserts nothing")
        if normal.length < 1e-9:
            continue
        normal.normalize()
        local_x = arm.pose.bones[
            lib.LEG_CHAIN[side][0]].matrix.col[0].to_3d().normalized()
        align = local_x.dot(normal)      # signed -- see HINGE_TOL above
        lib.report(f"hinge_align_{side}_extreme_lateral", f"{align:+.6f}")
        check(f"hinge_aligned_{side}_extreme_lateral", align > HINGE_TOL,
              f"at near-full lateral extension the femur's roll axis is only "
              f"{align:+.6f} aligned with the bend-plane normal. This is the "
              f"corner where `geom.lateral` as a roll reference degrades without "
              f"bound -- the residual it leaves after Gram-Schmidt collapses "
              f"toward zero and the roll becomes noise")

    # ---- 4f. the bend-plane guards refuse NOISE, not merely ZERO ------------
    # THE #338(2) GATE. `plant_foot` and `aim_arm` both build a bend plane as a
    # cross product of two UNIT vectors, so its length is sin(angle) and the
    # guard threshold is an angle. Both used `< 1e-6`, which only trips within a
    # microradian of degenerate.
    #
    # MEASURED, which is what sets the replacement threshold rather than taste:
    # the DIRECTION error of the normalised residual against an exact reference
    # follows roughly 1.1e-5/theta degrees in Blender's float32 vectors --
    #
    #     theta   1e-3 -> 0.011 deg    1e-4 -> 0.109    1e-5 -> 1.12
    #             1e-6 -> 10.31        1e-7 -> 62.02
    #
    # -- so at the OLD threshold the plane was already 10 deg wrong, and the
    # knee (or elbow) was placed in an arbitrary plane while the guard stayed
    # silent. That is the "wrong-but-plausible pose" these guards exist to catch.
    # 1e-3 rad keeps the plane within ~0.01 deg.
    #
    # PROVEN NOT TO FIRE ON REAL WORK, so this is hardening and not a behaviour
    # change: the minimum `|dir_ankle x forward|` over every `plant_foot` call of
    # all seven authoring scripts is 0.402019 (the layup), i.e. 400x the new
    # threshold. No committed clip moves because of this.
    BEND_GUARD_TEST_RAD = 1e-4          # noise-dominated, but >> the old 1e-6
    for side in ("L", "R"):
        leg_hip = arm.pose.bones[lib.LEG_CHAIN[side][0]].head.copy()
        # A target `BEND_GUARD_TEST_RAD` off dead-ahead: the bend plane it names
        # is real arithmetic but pure noise in direction.
        nearly_fore = (geom.forward * math.cos(BEND_GUARD_TEST_RAD)
                       - geom.up * math.sin(BEND_GUARD_TEST_RAD)).normalized()
        target = leg_hip + nearly_fore * (geom.leg_reach * 0.7)
        try:
            lib.plant_foot(arm, side, target, geom.forward, geom)
            check(f"plant_foot_refuses_noise_plane_{side}", False,
                  f"plant_foot accepted an ankle target only "
                  f"{BEND_GUARD_TEST_RAD} rad off dead-ahead. The bend plane it "
                  f"derived is noise, so the knee went into an arbitrary plane "
                  f"and nothing said so")
        except SystemExit:
            check(f"plant_foot_refuses_noise_plane_{side}", True)

        # Same shape for `aim_arm`'s elbow hint.
        shoulder = arm.pose.bones[lib.ARM_CHAIN[side][0]].head.copy()
        h, u = lib.arm_lengths(arm, side)
        hand_target = shoulder + (geom.forward * (h + u) * 0.7)
        reach_dir = (hand_target - shoulder).normalized()
        off = reach_dir.cross(geom.up).normalized()
        near_parallel_hint = (reach_dir * math.cos(BEND_GUARD_TEST_RAD)
                              + off * math.sin(BEND_GUARD_TEST_RAD))
        try:
            lib.aim_arm(arm, side, hand_target, near_parallel_hint, geom)
            check(f"aim_arm_refuses_noise_plane_{side}", False,
                  f"aim_arm accepted an elbow hint only {BEND_GUARD_TEST_RAD} rad "
                  f"from its reach direction; the elbow plane it derived is noise")
        except SystemExit:
            check(f"aim_arm_refuses_noise_plane_{side}", True)

    # ---- 4g. derive_body_right refuses a landmark COIN FLIP -----------------
    # THE #338(3) GATE. The guard was `by_shoulders == 0.0 or by_hips == 0.0` --
    # exact float equality, which essentially never holds, so a span merely
    # NEARLY perpendicular to `lateral` sailed through and the anatomical side
    # was then decided by float noise. That is precisely the coin flip the
    # adjacent error message says it refuses to make.
    #
    # The premise is asserted, not assumed: the crafted axis below is reported
    # with both ratios, so a future edit that stops making the spans
    # near-perpendicular turns this into a visible premise failure rather than a
    # silently vacuous pass.
    #
    # MARGIN ON THE REAL RIG, so the new relative threshold cannot bite honest
    # input: |dot|/|span| measures 0.99999998 (shoulders) and 1.00000000 (hips),
    # i.e. the real landmarks sit 1000x clear of a 1e-3 threshold.
    span = (arm.data.bones["mixamorig:RightArm"].head_local
            - arm.data.bones["mixamorig:LeftArm"].head_local)
    # A unit axis almost exactly perpendicular to the shoulder span, tilted back
    # toward it by a small but entirely non-zero amount.
    perp = span.cross(geom.forward).normalized()
    crafted = (perp + span.normalized() * 1e-4).normalized()
    hip_span = (arm.data.bones["mixamorig:RightUpLeg"].head_local
                - arm.data.bones["mixamorig:LeftUpLeg"].head_local)
    r_sh = abs(span.dot(crafted)) / span.length
    r_hip = abs(hip_span.dot(crafted)) / hip_span.length
    lib.report("body_right_coinflip_ratio_shoulders", f"{r_sh:.8f}")
    lib.report("body_right_coinflip_ratio_hips", f"{r_hip:.8f}")
    check("body_right_coinflip_premise", r_sh < 1e-3 and r_hip < 1e-3,
          f"the crafted axis is not actually near-perpendicular to the landmarks "
          f"(shoulders {r_sh:.8f}, hips {r_hip:.8f}), so the refusal asserted "
          f"below would not be testing the guard")
    try:
        lib.derive_body_right(arm, crafted)
        check("body_right_refuses_coinflip", False,
              "derive_body_right resolved an anatomical side from landmarks that "
              "are perpendicular to the given axis to within float noise; the "
              "sign it returned is a coin flip")
    except SystemExit:
        check("body_right_refuses_coinflip", True)

    # ---- 4h. the ankle-IK accumulator cannot be defeated by NaN -------------
    # THE #344 DEFECT, pinned. Every authoring script used to fold its ankle
    # errors together itself with `worst = max(worst, err)`, then hand the result
    # to `report_ankle_ik`. Both halves looked right. Between them was a hole:
    # CPython's two-arg `max(a, b)` returns `b if b > a else a`, and `nan > 0.0`
    # is False -- so with the accumulator FIRST, `max` silently DISCARDS a NaN
    # and reports the largest finite error instead. A rig that had degenerated
    # into NaN exported clean.
    #
    # Deliberately placed AFTER 4c/4e/4f, which over-reach on purpose: that
    # leaves a real, nonzero reading in the accumulator, so `reset_ankle_ik`
    # below is clearing genuine pollution rather than an already-zero field.
    geom.reset_ankle_ik()
    check("ankle_acc_resets", geom.worst_ankle_ik_m == 0.0,
          f"reset_ankle_ik left {geom.worst_ankle_ik_m} behind; an authoring "
          f"script that builds twice (author_steal, author_behindtheback) would "
          f"judge its second polarity against the first one's worst reading")

    # A finite reading first, so the NaN below has something to be swallowed BY.
    # A NaN arriving into an empty accumulator would propagate even under the
    # buggy `max`, so this ordering is what makes the gate discriminating.
    geom.observe_ankle_ik(geom.m(0.5))
    check("ankle_acc_premise", abs(geom.worst_ankle_ik_m - 0.5) < 1e-9,
          f"expected the accumulator to hold 0.5 m, got "
          f"{geom.worst_ankle_ik_m}; the NaN case below would prove nothing")

    # THE CONTROL. Assert that the OLD formulation really does swallow it --
    # without this the gate below could be passing for some unrelated reason,
    # and the reader has no evidence the rewrite was necessary at all.
    swallowed = max(geom.m(0.5), float("nan"))
    check("ankle_acc_max_would_swallow_nan", swallowed == geom.m(0.5),
          f"`max(acc, nan)` returned {swallowed}, not the accumulator; this "
          f"Python no longer has the behaviour #344 was written against, so "
          f"this section is guarding a bug that cannot happen here")

    geom.observe_ankle_ik(float("nan"))
    worst = geom.worst_ankle_ik_m
    lib.report("ankle_acc_worst_after_nan", f"{worst}")
    check("ankle_acc_propagates_nan", worst != worst,
          f"a NaN ankle solve left the accumulator at {worst}; the run would "
          f"export a degenerate rig and report a healthy number")

    # ...and the gate on top of it must REFUSE, not merely report. `not (x <=
    # tol)` is True for NaN; the tempting `x > tol` is False and would let it by.
    try:
        lib.report_ankle_ik("selftest_nan_probe", geom)
        check("ankle_gate_refuses_nan", False,
              "report_ankle_ik accepted a NaN worst-error instead of raising; "
              "the comparison has been flipped to a fail-open form")
    except SystemExit:
        check("ankle_gate_refuses_nan", True)
    geom.reset_ankle_ik()

    # ---- 5. none of the above introduced a pose scale -----------------------
    # `aim_arm` passes the bend-plane normal to `aim_matrix` as the side axis
    # instead of the rig's `right`. That basis must still be unit, or the clip
    # would carry a SCALE_3D track into Godot.
    lib.verify_pose_unscaled(arm, [bpy.context.scene.frame_current])

    # ---- 6. interp_channels holds absent channels instead of lerping to 0 ---
    # A limb that drifts toward the origin because one keypose omitted a channel
    # is a very easy authoring mistake to make and a hard one to see.
    #
    # Pinned to `ease_linear` DELIBERATELY. These three assertions are about
    # interpolation semantics, not about the easing default, and the midpoint
    # value only equals 2.0 under a curve that is symmetric at t=0.5. Left on
    # the default, changing the default would fail this test for an entirely
    # correct reason -- which reads as an interpolation bug and is not one.
    poses = [lib.Keypose(0.0, "a", fore=1.0, only_in_a=7.0),
             lib.Keypose(1.0, "b", fore=3.0)]
    mid = lib.interp_channels(poses, 0.5, easing=lib.ease_linear)
    check("interp_lerps_shared_channel", abs(mid["fore"] - 2.0) < 1e-9,
          f"expected 2.0 at the midpoint, got {mid.get('fore')}")
    check("interp_holds_absent_channel", mid.get("only_in_a") == 7.0,
          f"expected the absent channel held at 7.0, got {mid.get('only_in_a')}")
    ends = (lib.interp_channels(poses, -1.0), lib.interp_channels(poses, 99.0))
    check("interp_clamps_outside_range",
          ends[0]["fore"] == 1.0 and ends[1]["fore"] == 3.0,
          f"expected endpoints held, got {ends[0]['fore']} / {ends[1]['fore']}")

    # ---- 6b. the easing curves honour their contract and their direction ----
    # f(0)=0 and f(1)=1 is the contract every curve must meet, or a keypose is
    # not actually reached -- the pose the author wrote is not the pose baked.
    for fn in (lib.ease_linear, lib.ease_in, lib.ease_out, lib.ease_in_out):
        check(f"easing_endpoints_{fn.__name__}",
              abs(fn(0.0)) < 1e-12 and abs(fn(1.0) - 1.0) < 1e-12,
              f"{fn.__name__} does not map 0->0 and 1->1")

    # Direction, not just shape: ease_in must lag linear (the load) and ease_out
    # must lead it (the release). Endpoint VELOCITY is the property that actually
    # distinguishes a snap from a glide, so measure it by finite difference
    # rather than trusting the algebra.
    h = 1e-6
    v_in_arrive = (lib.ease_in(1.0) - lib.ease_in(1.0 - h)) / h
    v_out_arrive = (lib.ease_out(1.0) - lib.ease_out(1.0 - h)) / h
    check("ease_in_lags_linear", lib.ease_in(0.5) < 0.5 - 1e-9,
          f"ease_in(0.5)={lib.ease_in(0.5)} should be below linear 0.5")
    check("ease_out_leads_linear", lib.ease_out(0.5) > 0.5 + 1e-9,
          f"ease_out(0.5)={lib.ease_out(0.5)} should be above linear 0.5")
    check("ease_in_arrives_fast", v_in_arrive > 1.5,
          f"ease_in arrival velocity {v_in_arrive:.4f} should exceed linear's 1.0")
    check("ease_out_arrives_slow", v_out_arrive < 0.5,
          f"ease_out arrival velocity {v_out_arrive:.4f} should settle toward 0")
    lib.report("ease_in_arrival_velocity", f"{v_in_arrive:.4f}")
    lib.report("ease_out_arrival_velocity", f"{v_out_arrive:.4f}")

    # ---- 6c. PHASE_EASING resolution, with a non-symmetric control ----------
    # The precedence chain, most specific first.
    kp_startup = lib.Keypose(0.0, "Startup", fore=0.0)
    kp_active = lib.Keypose(1.0, "Active", fore=1.0)
    check("phase_easing_by_label",
          lib.resolve_easing(kp_startup) is lib.ease_in
          and lib.resolve_easing(kp_active) is lib.ease_out,
          "Startup/Active labels did not resolve to ease_in/ease_out")
    check("phase_easing_label_is_case_insensitive",
          lib.resolve_easing(lib.Keypose(0.0, "STARTUP")) is lib.ease_in,
          "label lookup should not depend on case")
    check("phase_easing_unknown_label_falls_back",
          lib.resolve_easing(lib.Keypose(0.0, "gait_cycle")) is lib.DEFAULT_EASING,
          "an unknown label must behave as it did before PHASE_EASING existed")
    check("phase_easing_keypose_override_wins",
          lib.resolve_easing(lib.Keypose(0.0, "Startup", easing=lib.ease_linear))
          is lib.ease_linear,
          "an explicit per-keypose easing must beat the label default")
    check("phase_easing_argument_overrides_all",
          lib.resolve_easing(kp_startup, override=lib.ease_linear) is lib.ease_linear,
          "the whole-timeline escape hatch must beat everything")

    # THE CONTROL. Every check above would still pass if PHASE_EASING mapped
    # startup and active to the SAME curve -- the curves themselves are correct
    # and the lookups all succeed. Only an assertion that two differently
    # labelled segments produce DIFFERENT baked values can catch that wiring
    # mistake. Same lesson as the #255 mirror bug: a symmetric assertion cannot
    # detect a symmetric error.
    startup_mid = lib.interp_channels([kp_startup, kp_active], 0.5)["fore"]
    active_mid = lib.interp_channels(
        [lib.Keypose(0.0, "Active", fore=0.0),
         lib.Keypose(1.0, "Recovery", fore=1.0)], 0.5)["fore"]
    check("phase_easing_startup_differs_from_active",
          abs(startup_mid - active_mid) > 0.1,
          f"startup and active segments interpolated alike "
          f"({startup_mid:.4f} vs {active_mid:.4f}) -- PHASE_EASING is miswired")
    check("phase_easing_startup_loads_first", startup_mid < 0.5,
          f"a Startup segment should lag at its midpoint, got {startup_mid:.4f}")
    lib.report("startup_segment_midpoint", f"{startup_mid:.4f}")
    lib.report("active_segment_midpoint", f"{active_mid:.4f}")

    # ---- 6d. every channel resolves at EVERY frame, frame 0 included --------
    # Regression for the #315-review defect. `bake_timeline` evaluates t_s=0.0 on
    # its first frame, which lands exactly on the opening keypose. Resolving
    # against the SEGMENT returned only that keypose's channels, so a channel
    # introduced later went missing on frame 0 alone -- a KeyError if `apply`
    # indexes it, or one frame of limb-at-origin if `apply` uses .get(k, 0.0).
    # Every clip has a frame 0, so this fired on every move that ramps a channel
    # in from Active.
    steal = [lib.Keypose(0.0, "Startup", crouch_m=0.10),
             lib.Keypose(0.2, "Active", crouch_m=0.15, reach_extend_m=0.50),
             lib.Keypose(0.5, "Recovery", crouch_m=0.10, reach_extend_m=0.0)]
    at0 = lib.interp_channels(steal, 0.0)
    check("interp_total_at_frame_zero", "reach_extend_m" in at0,
          f"channel introduced on Active is missing at t=0: {sorted(at0)}")
    check("interp_holds_backward_not_zero", at0.get("reach_extend_m") == 0.50,
          f"expected the first defined value 0.50 held backward, "
          f"got {at0.get('reach_extend_m')}")
    # And the gap case: defined at the ends, omitted in the middle, must ease
    # across the whole span rather than snapping at the last segment.
    gapped = [lib.Keypose(0.0, "a", v=0.0), lib.Keypose(1.0, "b"),
              lib.Keypose(2.0, "c", v=10.0)]
    check("interp_bridges_channel_gap",
          0.0 < lib.interp_channels(gapped, 1.0)["v"] < 10.0,
          f"expected a mid value across the gap, "
          f"got {lib.interp_channels(gapped, 1.0)['v']}")

    # ---- 6e. a channel colliding with Keypose's own kwargs fails loudly -----
    # `Keypose(**channels)` means a channel named `easing` is swallowed by the
    # kwarg rather than becoming a channel. Left unguarded that surfaces as
    # `TypeError: 'float' object is not callable` from deep inside
    # interpolation, nowhere near the keypose at fault.
    #
    # The `v=` channel is load-bearing in this test: resolution is per channel,
    # so a keypose pair with NO channels never resolves an easing at all and the
    # guard would not fire. (`bake_timeline` catches it eagerly regardless, via
    # the per-segment logging.)
    try:
        lib.interp_channels([lib.Keypose(0.0, "Startup", easing=0.5, v=0.0),
                             lib.Keypose(1.0, "Active", easing=1.0, v=1.0)], 0.5)
        check("easing_collision_fails", False,
              "a non-callable easing was accepted")
    except SystemExit:
        check("easing_collision_fails", True)

    # ---- 6f. bake_timeline: frame->time mapping and the overrun guard -------
    # This is the entry point all nineteen downstream scripts call, and it had
    # zero coverage. Both defects fixed in this review live here.
    seen = []
    lib.bake_timeline(
        arm,
        [lib.Keypose(0.0, "Startup", v=0.0), lib.Keypose(1.0, "Active", v=1.0)],
        lambda frame, t_s, ch: seen.append((frame, round(t_s, 6), ch["v"])),
        f0=10, f1=10 + FPS, fps=FPS)
    check("bake_timeline_frame_count", len(seen) == FPS + 1,
          f"expected {FPS + 1} applies over an inclusive range, got {len(seen)}")
    check("bake_timeline_maps_frames_to_time",
          seen[0][:2] == (10, 0.0) and seen[-1][:2] == (10 + FPS, 1.0),
          f"frame->time mapping wrong: first={seen[0][:2]} last={seen[-1][:2]}")
    check("bake_timeline_reaches_both_endpoints",
          seen[0][2] == 0.0 and seen[-1][2] == 1.0,
          f"endpoints not reached: {seen[0][2]} .. {seen[-1][2]}")

    # The overrun guard. Untrapped, a timeline longer than the frame range
    # silently truncates -- the Recovery pose never appears and every gate in
    # the library still passes, because none of them know what was intended.
    try:
        lib.bake_timeline(
            arm,
            [lib.Keypose(0.0, "Startup", v=0.0), lib.Keypose(5.0, "Recovery", v=1.0)],
            lambda frame, t_s, ch: None,
            f0=10, f1=10 + FPS, fps=FPS)
        check("bake_timeline_rejects_overrun", False,
              "a 5.0s timeline was baked into a 1.0s frame range without error")
    except SystemExit:
        check("bake_timeline_rejects_overrun", True)

    # ---- 6g. aim_matrix refuses a non-orthonormal basis ---------------------
    # The guard that replaced `verify_pose_unscaled` for this job. Proven to bite
    # by handing it a side axis that is not a unit vector's worth of information
    # -- a zero-length tail direction, which cannot yield a unit basis.
    try:
        lib.aim_matrix(hips_head, lib.Vector((0.0, 0.0, 0.0)), geom.lateral)
        check("aim_matrix_rejects_degenerate", False,
              "aim_matrix accepted a zero-length tail direction")
    except (SystemExit, ValueError, ZeroDivisionError):
        check("aim_matrix_rejects_degenerate", True)

    # ---- 7. verify_pose_distinct actually FAILS on identical poses ----------
    # A gate that cannot fail is worse than no gate, so prove this one bites.
    same = lib.snapshot_pose(arm, f0)
    try:
        lib.verify_pose_distinct(same, dict(same), 5.0, label="selftest_identical")
        check("pose_distinct_rejects_identical", False,
              "verify_pose_distinct passed two identical poses")
    except SystemExit:
        check("pose_distinct_rejects_identical", True)

    lib.leave_pose_mode()

    if _failures:
        raise SystemExit("FATAL: blender_anim_lib self-test failures:\n  - "
                         + "\n  - ".join(_failures))
    print("SELFTEST_OK")


main()
