"""Assertion-killed #370 mutations. Run without concurrent source edits or builds."""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import subprocess
import sys
import uuid

ROOT = Path(__file__).resolve().parents[1]
KERNEL = ROOT / "scripts/Player/ContactMath.cs"
MOTION = ROOT / "scripts/Player/PlayerContactMotion.cs"
CONTROLLER = ROOT / "scripts/Player/PlayerController.cs"
SCENE = ROOT / "scenes/Player.tscn"


def replace_once(text, old, new):
    if text.count(old) != 1:
        raise RuntimeError(f"mutation anchor must occur once: {old!r}")
    return text.replace(old, new, 1)


def skip_active(text):
    start = text.index("private void TickCommittedMoveBehavior")
    active = text.index("case MovePhase.Active:", start)
    end = text.index("case MovePhase.Recovery:", active)
    return text[:active] + replace_once(text[active:end], "IntegrateContactMotion(delta, opponent);", "MoveAndSlide();") + text[end:]


MUTATIONS = (
    ("disabled-contact", KERNEL,
     lambda t: replace_once(t, "if (opponent is not { } other) return default;",
                           "if (opponent is not { } other) return default;\n        if (delta >= 0) return default;"),
     "set-drive", "production contact separation failed"),
    ("display-state", MOTION,
     lambda t: replace_once(t, "raw ? _serverPos : GlobalPosition", "GlobalPosition"),
     None, "actual movement used display state instead of raw broadcast"),
    ("skipped-active", CONTROLLER, skip_active, "active", "committed sweep emerged behind set body"),
    ("solver-contact", SCENE,
     lambda t: replace_once(t, "collision_layer = 2\ncollision_mask = 1", "collision_layer = 1\ncollision_mask = 1"),
     "solver-mask", "player solver mask bypass failed"),
)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--godot", required=True)
    parser.add_argument("--only", choices=[m[0] for m in MUTATIONS])
    args = parser.parse_args()
    run_id = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "-" + uuid.uuid4().hex[:8]
    directory = ROOT / ".godot" / "production-contact" / "mutations" / run_id
    directory.mkdir(parents=True, exist_ok=False)
    originals = {p: p.read_bytes() for p in (KERNEL, MOTION, CONTROLLER, SCENE)}
    report = {"run_id": run_id, "results": [], "restored": False}
    build = ["dotnet", "build", "HOOPER GAME.csproj", "--configuration", "Debug",
             "-p:IncludeIntegrationHarness=true", "--nologo"]

    def run(command, name, timeout=180):
        # Argument arrays avoid shell interpretation; timeout kills and reaps the child.
        # Source: https://docs.python.org/3.14/library/subprocess.html#subprocess.run
        result = subprocess.run(command, cwd=ROOT, capture_output=True, text=True,
                                encoding="utf-8", errors="replace", timeout=timeout,
                                creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
        output = result.stdout + result.stderr
        (directory / f"{name}.txt").write_text(output, encoding="utf-8")
        return result.returncode, output

    def require_build(name):
        code, _ = run(build, name)
        if code != 0:
            raise RuntimeError(f"{name} failed; compilation failure is not mutation evidence")

    def evidence(scenario, name):
        if scenario is not None:
            return run([args.godot, "--headless", "--path", str(ROOT), "--log-file", str(directory / f"{name}.log"),
                        "res://tests/integration/ProductionContactTest.tscn", "--", f"--harness-scenario={scenario}"], name, 45)
        output_dir = directory / name
        code, output = run([sys.executable, "tools/run_production_contact.py", "--godot", args.godot,
                            "--move", "neutral", "--contact", "contact", "--delay-ms", "30",
                            "--output", str(output_dir)], name, 90)
        output += "\n".join(p.read_text(encoding="utf-8", errors="replace") for p in output_dir.rglob("*.stdout.log"))
        return code, output

    try:
        require_build("baseline-build")
        selected = [m for m in MUTATIONS if args.only is None or m[0] == args.only]
        for name, path, mutate, scenario, expected in selected:
            code, output = evidence(scenario, name + "-baseline")
            marker = "[Harness] PASS production-contact" if scenario else '"passed": true'
            if code != 0 or marker not in output:
                raise RuntimeError(f"{name} clean baseline failed")
            text = originals[path].decode("utf-8").replace("\r\n", "\n")
            mutated = mutate(text)
            path.write_bytes(mutated.encode("utf-8"))
            try:
                require_build(name + "-build")
                code, output = evidence(scenario, name + "-mutant")
                killed = code == 1 and expected in output
                report["results"].append({"mutation": name, "exit_code": code, "assertion": expected, "killed": killed})
                if not killed:
                    raise RuntimeError(f"{name} did not fail its named behavioral assertion")
            finally:
                path.write_bytes(originals[path])
                require_build(name + "-restore-build")
    except Exception as error:
        report["error"] = str(error)
    finally:
        # Restore EXACT source bytes, including scene and line endings, even on failure.
        # Source: https://docs.python.org/3.14/library/pathlib.html#pathlib.Path.write_bytes
        for path, original in originals.items():
            path.write_bytes(original)
        try:
            require_build("final-restored-build")
            report["restored"] = all(p.read_bytes() == b for p, b in originals.items())
        except Exception as error:
            report["restore_error"] = str(error)
        (directory / "summary.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report), flush=True)
    return 0 if "error" not in report and "restore_error" not in report and report["restored"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
