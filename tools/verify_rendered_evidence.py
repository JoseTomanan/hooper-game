#!/usr/bin/env python3
"""Small, dependency-free verifier for #365 capture manifests.

It deliberately compares state-linked event timing and camera framing, not PNG
pixels: identical pixels across GPUs are outside the experiment's contract.
"""
import json
import os
import sys
import struct
import zlib

LOCAL_LABELS = ["stationary-to-moving-dribble", "both-hands-direction-change", "shot-fadeaway", "pivot"]

def decode_rgba_png(path):
    """Decode the persisted RGBA PNG, including PNG scanline filters.

    Godot writes standard non-interlaced PNGs.  Keeping this dependency-free
    lets CI prove the *saved* bytes still decode after a truncation mutation.
    """
    raw = open(path, "rb").read()
    if raw[:8] != b"\x89PNG\r\n\x1a\n":
        raise AssertionError(f"{path}: missing PNG signature")
    offset, width, height, color_type, idat = 8, None, None, None, b""
    while offset < len(raw):
        if offset + 12 > len(raw):
            raise AssertionError(f"{path}: truncated PNG chunk")
        size = struct.unpack(">I", raw[offset:offset + 4])[0]
        kind = raw[offset + 4:offset + 8]
        payload_end = offset + 8 + size
        if payload_end + 4 > len(raw):
            raise AssertionError(f"{path}: truncated {kind!r} chunk")
        payload = raw[offset + 8:payload_end]
        if zlib.crc32(kind + payload) & 0xffffffff != struct.unpack(">I", raw[payload_end:payload_end + 4])[0]:
            raise AssertionError(f"{path}: corrupt {kind!r} CRC")
        if kind == b"IHDR":
            width, height, depth, color_type, compression, filt, interlace = struct.unpack(">IIBBBBB", payload)
            if depth != 8 or color_type not in (2, 6) or compression or filt or interlace:
                raise AssertionError(f"{path}: unsupported PNG format (depth={depth}, color={color_type}, interlace={interlace})")
        elif kind == b"IDAT":
            idat += payload
        elif kind == b"IEND":
            break
        offset = payload_end + 4
    if not width or not height or not idat:
        raise AssertionError(f"{path}: missing IHDR or IDAT")
    try:
        scanlines = zlib.decompress(idat)
    except zlib.error as error:
        raise AssertionError(f"{path}: corrupt IDAT stream: {error}")
    channels, stride = (4 if color_type == 6 else 3), width * (4 if color_type == 6 else 3)
    if len(scanlines) != (stride + 1) * height:
        raise AssertionError(f"{path}: decompressed scanline length is invalid")
    rows, previous, cursor = [], bytearray(stride), 0
    for _ in range(height):
        filter_type, source = scanlines[cursor], scanlines[cursor + 1:cursor + 1 + stride]
        cursor += stride + 1
        row = bytearray(stride)
        for i, value in enumerate(source):
            left = row[i - channels] if i >= channels else 0
            up = previous[i]
            up_left = previous[i - channels] if i >= channels else 0
            if filter_type == 0: decoded = value
            elif filter_type == 1: decoded = (value + left) & 255
            elif filter_type == 2: decoded = (value + up) & 255
            elif filter_type == 3: decoded = (value + ((left + up) >> 1)) & 255
            elif filter_type == 4:
                p, pa, pb, pc = left + up - up_left, abs(up - up_left), abs(left - up_left), abs(left + up - 2 * up_left)
                predictor = left if pa <= pb and pa <= pc else (up if pb <= pc else up_left)
                decoded = (value + predictor) & 255
            else: raise AssertionError(f"{path}: unknown PNG filter {filter_type}")
            row[i] = decoded
        rows.append(row); previous = row
    return width, height, rows, channels

def validate_png(path, item):
    width, height, rows, channels = decode_rgba_png(path)
    if width != item.get("width") or height != item.get("height") or width < 320 or height < 180:
        raise AssertionError(f"{path}: persisted dimensions do not match manifest or are too small")
    samples = [rows[y * (height - 1) // 31][(x * (width - 1) // 31) * channels:(x * (width - 1) // 31) * channels + 3] for y in range(32) for x in range(32)]
    luminance = [(2126 * p[0] + 7152 * p[1] + 722 * p[2]) / 2_550_000 for p in samples]
    if sum(luminance) / len(luminance) < 0.01 or max(luminance) - min(luminance) < 0.02:
        raise AssertionError(f"{path}: persisted image is black or flat")

def load(path, local=False):
    with open(path, encoding="utf-8") as handle:
        data = json.load(handle)
    if data.get("schema") != "hooper-rendered-evidence/v1" or data.get("result") != "pass":
        raise AssertionError(f"{path}: not a passing v1 manifest")
    captures = data.get("captures", [])
    expected_labels = LOCAL_LABELS if local else ["remote-player-display"]
    if sorted(item.get("label") for item in captures) != expected_labels:
        raise AssertionError(f"{path}: capture labels are missing, duplicated, or unexpected")
    expected_files = {"manifest.json", "capture.avi"} | {item.get("file", "") for item in captures}
    actual_files = set(os.listdir(os.path.dirname(path)))
    if actual_files != expected_files:
        raise AssertionError(f"{path}: output file set is stale or incomplete: {sorted(actual_files)}")
    if os.path.getsize(os.path.join(os.path.dirname(path), "capture.avi")) == 0:
        raise AssertionError(f"{path}: movie writer did not finalize a nonempty AVI")
    if not data.get("effectiveRenderingMethod") or not data.get("effectiveDisplayDriver") or not data.get("adapterName") or not data.get("adapterApiVersion"):
        raise AssertionError(f"{path}: effective renderer/display driver/adapter is unknown")
    for item in captures:
        frame = item.get("physicsFrame")
        if not isinstance(frame, int) or frame < 1:
            raise AssertionError(f"{path}: capture lacks a physics-frame projection")
        image = os.path.join(os.path.dirname(path), item.get("file", ""))
        if not os.path.isfile(image) or os.path.getsize(image) != item.get("byteLength") or os.path.getsize(image) == 0:
            raise AssertionError(f"{path}: manifest image provenance is missing or stale: {image}")
        validate_png(image, item)
    return data

def capture_map(manifest):
    return {capture["label"]: capture for capture in manifest["captures"]}

def main(paths):
    if len(paths) != 3:
        raise AssertionError("usage: local-a.json local-b.json remote.json")
    first, second, remote = load(paths[0], True), load(paths[1], True), load(paths[2])
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
    direction = a["stationary-to-moving-dribble"]
    if not direction.get("directionChanged") or len(direction.get("directionBefore", [])) != 3 or len(direction.get("directionAfter", [])) != 3:
        raise AssertionError("direction-change sample lacks a non-vacuous before/after velocity witness")
    remote_sample = r.get("remote-player-display")
    if not remote_sample or not remote_sample.get("remoteDisplay"):
        raise AssertionError("remote manifest lacks a genuine remote-player image")
    if remote_sample.get("displayMoveId") != "behindtheback" or "Behind" not in remote_sample.get("observedAnimationState", ""):
        raise AssertionError("remote image is not linked to the broadcast display state it claims")
    print("[rendered-evidence] manifest verifier PASS: repeat timing/framing and remote display provenance hold")

if __name__ == "__main__":
    try:
        if len(sys.argv) == 3 and sys.argv[1] == "--local":
            load(sys.argv[2], True)
            print("[rendered-evidence] local artifact verifier PASS")
        else:
            main(sys.argv[1:])
    except (AssertionError, OSError, ValueError, json.JSONDecodeError) as error:
        print(f"[rendered-evidence] manifest verifier FAIL: {error}", file=sys.stderr)
        sys.exit(1)
