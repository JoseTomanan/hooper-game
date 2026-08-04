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
    # Note the shoulders are an INDEPENDENT landmark from the hip pair that
    # `derive_axes` builds `lateral` from, and from the shoulder span
    # `derive_body_right` reads -- this measures the POSED skeleton, that one
    # reads REST geometry. So this is a third opinion, not the same measurement.
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
    ANKLE_TOL_M = 1e-4
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
