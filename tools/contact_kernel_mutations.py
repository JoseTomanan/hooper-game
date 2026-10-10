#!/usr/bin/env python3
"""Reproduce #369 assertion-killed mutations; restore source/build even on failure.

Run only while nobody else edits ContactMath.cs or builds the same assemblies.
Uses the integration assembly explicitly; compilation failures never count as kills.
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import uuid
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "scripts/Player/ContactMath.cs"
UNIT_PROJECT = "tests/Hooper.Ball.Tests/Hooper.Ball.Tests.csproj"
UNIT_NAME = "SetRequiresSpeedAndFacingWithInclusiveBoundaries"
MUTATIONS = (
    ("disable-kernel", "if (opponent is not { } other) return default;",
     "if (opponent is not { } other) return default;\n        if (delta >= 0) return default;", "set-drive"),
    ("ignore-facing", "return difference <= settings.FacingHalfAngle;", "return true;", "unset-drive"),
    ("remove-overlap", "bool overlapping = distance < radius;", "bool overlapping = false;", "overlap"),
    ("disable-sweep", "double travel = speed * delta;",
     "double travel = speed * delta;\n        if (speed > 0) return Separation();", "crossing"),
    ("fixed-dimensions", "double radiusSum = (double)self.CapsuleRadius + other.CapsuleRadius;",
     "double radiusSum = 1;", "dimensions"),
    ("remove-speed-predicate", " || speedSquared > (double)settings.MaxSetSpeed * settings.MaxSetSpeed", "", None),
)
EXPECTED_GATE = {
    "disable-kernel": "kernel non-overlap gate failed",
    "ignore-facing": "wrong-facing opponent must yield under the same drive",
    "remove-overlap": "kernel non-overlap gate failed",
    "disable-sweep": "set absorption gate failed",
    "fixed-dimensions": "set absorption gate failed",
}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--godot", default=os.environ.get("GODOT", "godot"))
    parser.add_argument("--timeout", type=float, default=180, help="per-command timeout in seconds")
    args = parser.parse_args()
    if args.timeout <= 0:
        parser.error("--timeout must be positive")
    run_id = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-" + uuid.uuid4().hex[:8]
    logs = ROOT / ".godot" / f"contact-mutation-{run_id}"
    logs.mkdir(parents=True, exist_ok=False)
    report = {"run_id": run_id, "source": str(SOURCE.relative_to(ROOT)), "results": [], "restored": False}
    original = SOURCE.read_bytes()
    text = original.decode("utf-8")
    newline = "\r\n" if "\r\n" in text else "\n"
    build = ["dotnet", "build", "HOOPER GAME.csproj", "--configuration", "Debug", "-p:IncludeIntegrationHarness=true", "--nologo"]

    def run(command: list[str], name: str) -> tuple[int, str]:
        log = logs / f"{name}.txt"
        try:
            result = subprocess.run(command, cwd=ROOT, capture_output=True, text=True,
                                    encoding="utf-8", errors="replace", timeout=args.timeout)
            output = result.stdout + result.stderr
            code = result.returncode
        except subprocess.TimeoutExpired as error:
            def decode(value):
                return value.decode("utf-8", errors="replace") if isinstance(value, bytes) else value or ""
            output = decode(error.stdout) + decode(error.stderr) + "\nCOMMAND TIMED OUT\n"
            code = -999
        except OSError as error:
            output, code = str(error), -998
        log.write_text(json.dumps(command) + "\n" + output, encoding="utf-8")
        return code, output

    def require_build(command: list[str], name: str):
        code, _ = run(command, name)
        if code != 0:
            raise RuntimeError(f"{name} failed ({code}); compilation failure is not a mutation kill")

    def harness(scenario: str, name: str) -> tuple[int, str]:
        native_log = logs / f"{name}.log"
        return run([args.godot, "--headless", "--path", ".", "--log-file", str(native_log),
                    "tests/integration/ContactKernelTest.tscn", "--", f"--harness-scenario={scenario}"], name)

    def unit(name: str) -> tuple[int, str, Path]:
        trx = logs / f"{name}.trx"
        code, output = run(["dotnet", "test", UNIT_PROJECT, "--configuration", "Debug", "--no-build",
                            "--filter", f"FullyQualifiedName~{UNIT_NAME}",
                            "--logger", f"trx;LogFileName={trx}", "--logger", "console;verbosity=normal"], name)
        return code, output, trx

    try:
        # Check every exact anchor before altering source, so source drift fails safely.
        for name, before, _, _ in MUTATIONS:
            if text.count(before) != 1:
                raise RuntimeError(f"{name}: expected exactly one source anchor, found {text.count(before)}")
        version_code, version = run([args.godot, "--version"], "godot-version")
        if version_code != 0 or "4.7.1.stable.mono" not in version:
            raise RuntimeError("Godot must report 4.7.1.stable.mono")
        require_build(build, "baseline-game-build")
        require_build(["dotnet", "build", UNIT_PROJECT, "--configuration", "Debug", "--nologo"], "baseline-unit-build")
        for name, _, _, scenario in MUTATIONS:
            if scenario:
                code, output = harness(scenario, f"baseline-{name}")
                if code != 0 or "[harness] PASS ContactKernelTest:" not in output or "[harness] FAIL" in output:
                    raise RuntimeError(f"baseline {name} did not pass")
            else:
                code, _, trx = unit(f"baseline-{name}")
                results = ET.parse(trx).getroot().findall(".//{*}UnitTestResult") if trx.exists() else []
                if code != 0 or len(results) != 1 or results[0].get("outcome") != "Passed":
                    raise RuntimeError("baseline speed predicate test did not execute and pass exactly once")
        for name, before, after, scenario in MUTATIONS:
            SOURCE.write_bytes(text.replace(before, after.replace("\n", newline), 1).encode("utf-8"))
            row = {"mutation": name, "scenario": scenario or UNIT_NAME, "killed": False}
            report["results"].append(row)
            require_build(build, f"{name}-game-build")
            row["build_passed"] = True
            if scenario:
                code, output = harness(scenario, name)
                killed = (code == 1 and "[harness] FAIL ContactKernelTest:" in output
                          and f"[harness] FAIL ContactKernelTest: {EXPECTED_GATE[name]};" in output
                          and "[harness] PASS" not in output
                          and not re.search(r"crash|segmentation fault|access violation|Unhandled exception", output, re.I))
            else:
                require_build(["dotnet", "build", UNIT_PROJECT, "--configuration", "Debug", "--nologo"], f"{name}-unit-build")
                code, output, trx = unit(name)
                results = ET.parse(trx).getroot().findall(".//{*}UnitTestResult") if trx.exists() else []
                failed = [r for r in results if r.get("outcome") == "Failed" and UNIT_NAME in r.get("testName", "")]
                assertion = "".join(failed[0].itertext()) if len(failed) == 1 else ""
                killed = code == 1 and len(results) == 1 and len(failed) == 1 and bool(re.search(r"Assert\.|XunitException|Xunit\.Sdk\.\w+Exception", assertion))
            row.update(exit_code=code, killed=killed, log=str((logs / f"{name}.txt").relative_to(ROOT)))
            if not killed:
                raise RuntimeError(f"{name}: did not produce the required assertion failure; see logs")
            SOURCE.write_bytes(original)
    except Exception as error:
        report["error"] = str(error)
    finally:
        SOURCE.write_bytes(original)
        report["restored"] = SOURCE.read_bytes() == original
        code, _ = run(build, "restored-game-build")
        report["restored_build_passed"] = code == 0
        if code != 0:
            report["restoration_error"] = f"clean integration build failed ({code})"
        # Also restore the independently compiled unit assembly after its mutation.
        code, _ = run(["dotnet", "build", UNIT_PROJECT, "--configuration", "Debug", "--nologo"], "restored-unit-build")
        report["restored_unit_build_passed"] = code == 0
        report["passed"] = ("error" not in report and report["restored"] and report["restored_build_passed"]
                            and report["restored_unit_build_passed"] and len(report["results"]) == len(MUTATIONS)
                            and all(row["killed"] for row in report["results"]))
        rendered = json.dumps(report, indent=2) + "\n"
        (logs / "summary.json").write_text(rendered, encoding="utf-8")
        (ROOT / ".godot/contact-kernel-mutations.json").write_text(rendered, encoding="utf-8")
        print(rendered)
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    sys.exit(main())
