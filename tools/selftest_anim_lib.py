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

    for side in ("L", "R"):
        humerus_u, ulna_u = lib.arm_lengths(arm, side)
        reach_u = humerus_u + ulna_u
        shoulder = arm.pose.bones[lib.ARM_CHAIN[side][0]].head.copy()

        # ---- 1. reachable targets land the wrist exactly ---------------------
        # Three directions, each at a different fraction of reach, so a
        # direction-dependent error cannot hide behind one lucky pose. The elbow
        # hint points down and outward, which is where a real elbow goes for a
        # reach in front of the body.
        hint = (-geom.up * 0.8 + geom.right * (0.6 if side == "R" else -0.6))
        for label, direction, frac in (
            ("fwd_low", geom.forward * 0.8 - geom.up * 0.6, 0.70),
            ("fwd_flat", geom.forward * 1.0, 0.85),
            ("side_up", geom.forward * 0.3 + geom.up * 0.5
             + geom.right * (0.8 if side == "R" else -0.8), 0.60),
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

    # ---- 5. none of the above introduced a pose scale -----------------------
    # `aim_arm` passes the bend-plane normal to `aim_matrix` as the side axis
    # instead of the rig's `right`. That basis must still be unit, or the clip
    # would carry a SCALE_3D track into Godot.
    lib.verify_pose_unscaled(arm, [bpy.context.scene.frame_current])

    # ---- 6. interp_channels holds absent channels instead of lerping to 0 ---
    # A limb that drifts toward the origin because one keypose omitted a channel
    # is a very easy authoring mistake to make and a hard one to see.
    poses = [lib.Keypose(0.0, "a", fore=1.0, only_in_a=7.0),
             lib.Keypose(1.0, "b", fore=3.0)]
    mid = lib.interp_channels(poses, 0.5)
    check("interp_lerps_shared_channel", abs(mid["fore"] - 2.0) < 1e-9,
          f"expected 2.0 at the midpoint, got {mid.get('fore')}")
    check("interp_holds_absent_channel", mid.get("only_in_a") == 7.0,
          f"expected the absent channel held at 7.0, got {mid.get('only_in_a')}")
    ends = (lib.interp_channels(poses, -1.0), lib.interp_channels(poses, 99.0))
    check("interp_clamps_outside_range",
          ends[0]["fore"] == 1.0 and ends[1]["fore"] == 3.0,
          f"expected endpoints held, got {ends[0]['fore']} / {ends[1]['fore']}")

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
