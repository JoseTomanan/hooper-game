#!/usr/bin/env python3
"""Small, dependency-free verifier for #365 capture manifests.

It deliberately compares state-linked event timing and camera framing, not PNG
pixels: identical pixels across GPUs are outside the experiment's contract.
"""
import json
import os
import sys

LOCAL_LABELS = ["stationary-to-moving-dribble", "both-hands-direction-change", "shot-fadeaway", "pivot"]

def load(path):
    with open(path, encoding="utf-8") as handle:
        data = json.load(handle)
    if data.get("schema") != "hooper-rendered-evidence/v1" or data.get("result") != "pass":
        raise AssertionError(f"{path}: not a passing v1 manifest")
    for item in data.get("captures", []):
        frame = item.get("physicsFrame")
        if not isinstance(frame, int) or frame < 1:
            raise AssertionError(f"{path}: capture lacks a physics-frame projection")
        image = os.path.join(os.path.dirname(path), item.get("file", ""))
        if not os.path.isfile(image) or os.path.getsize(image) != item.get("byteLength") or os.path.getsize(image) == 0:
            raise AssertionError(f"{path}: manifest image provenance is missing or stale: {image}")
    return data

def capture_map(manifest):
    return {capture["label"]: capture for capture in manifest["captures"]}

def main(paths):
    if len(paths) != 3:
        raise AssertionError("usage: local-a.json local-b.json remote.json")
    first, second, remote = map(load, paths)
    a, b, r = map(capture_map, (first, second, remote))
    if sorted(a) != LOCAL_LABELS or sorted(b) != LOCAL_LABELS:
        raise AssertionError("local manifests do not contain exactly the four required production samples")
    for label in LOCAL_LABELS:
        # Deterministic simulation proves timing/framing replay. Renderer
        # output is intentionally only inspected for decodability/non-emptiness.
        if a[label]["physicsFrame"] != b[label]["physicsFrame"]:
            raise AssertionError(f"{label}: repeat timing diverged")
        if a[label]["cameraTransform"] != b[label]["cameraTransform"] or a[label]["cameraFov"] != b[label]["cameraFov"]:
            raise AssertionError(f"{label}: repeat gameplay framing diverged")
        if not a[label]["subjectScreen"] or not b[label]["subjectScreen"]:
            raise AssertionError(f"{label}: subject projection absent")
    remote_sample = r.get("remote-player-display")
    if not remote_sample or not remote_sample.get("remoteDisplay"):
        raise AssertionError("remote manifest lacks a genuine remote-player image")
    if remote_sample.get("displayMoveId") != "behindtheback" or "Behind" not in remote_sample.get("observedAnimationState", ""):
        raise AssertionError("remote image is not linked to the broadcast display state it claims")
    print("[rendered-evidence] manifest verifier PASS: repeat timing/framing and remote display provenance hold")

if __name__ == "__main__":
    try:
        main(sys.argv[1:])
    except (AssertionError, OSError, ValueError, json.JSONDecodeError) as error:
        print(f"[rendered-evidence] manifest verifier FAIL: {error}", file=sys.stderr)
        sys.exit(1)
