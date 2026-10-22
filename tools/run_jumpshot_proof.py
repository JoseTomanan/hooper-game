"""Run isolated production-input ENet captures and require their observed #418 proof."""
import argparse
from pathlib import Path
import subprocess
import sys
import uuid

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--godot", default="godot")
    parser.add_argument("--port", type=int, default=23473)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    output = args.output or ROOT/".godot"/"418"/("catalog-net-"+uuid.uuid4().hex)
    commit = subprocess.check_output(["git","rev-parse","HEAD"],cwd=ROOT,text=True).strip()
    output.mkdir(parents=True,exist_ok=False)
    # Measure the legacy clips through exactly the SAME remote presentation
    # clock, rather than applying local AnimationTree seam limits to ADR-0027.
    for name,extra in (("before",["--legacy-shot"]),("after",[])):
        subprocess.run([sys.executable,str(ROOT/"tools/run_jumpshot_capture.py"),"--godot",args.godot,
                        "--headless","--remote","--port",str(args.port),"--commit",commit,"--output",str(output/name),*extra],cwd=ROOT,check=True)
    analyzer = [sys.executable,str(ROOT/"tools/analyze_jumpshot_capture.py")]
    baseline = output/"baseline.json"
    subprocess.run([*analyzer,str(output/"before"),"--no-form-gates","--output",str(baseline)],cwd=ROOT,check=True)
    subprocess.run([*analyzer,str(output/"after"),"--baseline",str(baseline),"--output",str(output/"proof.json")],cwd=ROOT,check=True)
    control = output/"mismatched-boundary-control.json"
    result = subprocess.run([*analyzer,str(output/"after/right-normal-1"),"--baseline",str(baseline),
                             "--mutate-boundary","--output",str(control)],cwd=ROOT)
    import json
    report = json.loads(control.read_text())
    failures = [failure for case in report["cases"] for failure in case["failures"]]
    if result.returncode != 1 or not any(f.startswith("boundary displacement exceeds baseline:") for f in failures):
        raise RuntimeError("mismatched-boundary control escaped its intended detector")
    print(f"[harness] PASS net-jumpshot-form report={output/'proof.json'}")


if __name__ == "__main__": main()
