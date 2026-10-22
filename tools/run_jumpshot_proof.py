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
    subprocess.run([sys.executable,str(ROOT/"tools/run_jumpshot_capture.py"),"--godot",args.godot,
                    "--headless","--remote","--port",str(args.port),"--commit",commit,"--output",str(output)],cwd=ROOT,check=True)
    subprocess.run([sys.executable,str(ROOT/"tools/analyze_jumpshot_capture.py"),str(output),
                    "--output",str(output/"proof.json")],cwd=ROOT,check=True)
    print(f"[harness] PASS net-jumpshot-form report={output/'proof.json'}")


if __name__ == "__main__": main()
