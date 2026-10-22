"""Analyze #418 final modified pose captures; missing/coherently unobserved frames fail proof."""
import argparse
import copy
import json
import math
import struct
from pathlib import Path


def sub(a, b): return [x-y for x, y in zip(a, b)]
def dot(a, b): return sum(x*y for x, y in zip(a, b))
def norm(v):
    size = math.sqrt(dot(v, v))
    if size < 1e-10:
        raise ValueError("degenerate anatomical axis")
    return [x/size for x in v]
def distance(a, b): return math.sqrt(dot(sub(a, b), sub(a, b)))
def angle(a, b): return math.degrees(math.acos(max(-1, min(1, dot(norm(a), norm(b))))))


def coordinate_resolution(magnitude):
    # Godot Vector3 components are 32-bit by default. Two 3D endpoints each
    # contribute at most sqrt(3) native component ULPs to a distance comparison.
    # This is input-coordinate precision, not animation repeat noise or taste.
    # https://docs.godotengine.org/en/4.7/classes/class_vector3.html
    value = struct.unpack("f",struct.pack("f",magnitude))[0]
    bits = struct.unpack("I",struct.pack("f",value))[0]
    ulp = struct.unpack("f",struct.pack("I",bits+1))[0]-value
    return 2*math.sqrt(3)*ulp


def geometry(pose):
    # Match JumpshotFormTest: the authored clip's shooting arm is always Right,
    # independent of the ball-hand state entering the shot.
    def bone(name): return pose["bones"]["mixamorig_"+name]["body"][:3]
    shoulder, elbow, wrist = [bone("Right"+n) for n in ("Arm", "ForeArm", "Hand")]
    gs, ge, gw = [bone("Left"+n) for n in ("Arm", "ForeArm", "Hand")]
    up = norm(sub(bone("Neck"), bone("Spine")))
    toes = [x+y for x,y in zip(sub(bone("RightToeBase"),bone("RightFoot")), sub(bone("LeftToeBase"),bone("LeftFoot")))]
    forward = norm(sub(toes,[dot(toes,up)*x for x in up]))
    forearm, hand = norm(sub(wrist,elbow)), norm(sub(bone("RightHandMiddle1"),wrist))
    flex = math.degrees((math.atan2(dot(hand,forward),dot(hand,up))-math.atan2(dot(forearm,forward),dot(forearm,up))+math.pi)%(2*math.pi)-math.pi)
    ea, ga = angle(sub(shoulder,elbow),sub(wrist,elbow)), angle(sub(gs,ge),sub(gw,ge))
    return dict(elbow=ea, guideElbow=ga, wristHeight=dot(sub(wrist,elbow),up), wristFlexion=flex, asymmetry=ea-ga)


def clock(row, remote):
    return row["selectedPresentationTick"] if remote else row["physicsFrame"]


def boundaries(rows, remote, mutate=False):
    transitions = {("Startup","Active"),("Active","Recovery"),("Startup","Recovery"),("Recovery","Inactive")}
    unique = []
    for row in rows:
        if unique and clock(row,remote)==clock(unique[-1],remote):
            continue
        unique.append(row)
    result = []
    injected = False
    for i in range(1,len(unique)):
        old,new = unique[i-1]["pose"], unique[i]["pose"]
        pair = old.get("phase"),new.get("phase")
        if pair not in transitions or old.get("move") != "jumpshot":
            continue
        neighborhood = copy.deepcopy(unique[max(0,i-2):min(len(unique),i+2)])
        # A presentation reset is a discontinuous clock jump. Never connect an
        # edge across it and then describe that edge as ordinary continuity.
        if remote and len({r.get("presentationResyncCount",0) for r in neighborhood}) != 1:
            continue
        if mutate and not injected:
            original_max = max(distance(a["pose"]["bones"][name]["body"][:3],b["pose"]["bones"][name]["body"][:3])
                               for a,b in zip(neighborhood,neighborhood[1:]) for name in a["pose"]["bones"])
            neighborhood[min(2,i)]["pose"]["bones"]["mixamorig_RightHand"]["body"][0] += 2*original_max+1
            injected = True
        edges = []
        for a,b in zip(neighborhood,neighborhood[1:]):
            gap = clock(b,remote)-clock(a,remote)
            values = {name:distance(a["pose"]["bones"][name]["body"][:3],b["pose"]["bones"][name]["body"][:3]) for name in a["pose"]["bones"]}
            worst = max(values,key=values.get)
            edges.append(dict(fromTick=clock(a,remote),toTick=clock(b,remote),tickGap=gap,bone=worst,maxDisplacement=values[worst]))
        magnitude = max(abs(x) for r in neighborhood for bone in r["pose"]["bones"].values() for space in ("body","world") for x in bone[space][:3])
        result.append(dict(transition="->".join(pair),fromFrame=old["frame"],toFrame=new["frame"],edges=edges,
                           coordinateMagnitudeM=magnitude,coordinateResolutionM=coordinate_resolution(magnitude),
                           maxDisplacement=max(e["maxDisplacement"] for e in edges),maxTickGap=max(e["tickGap"] for e in edges)))
    return result


def analyze(directory, form=True, mutate=False):
    manifest = json.loads((directory/"manifest.json").read_text())
    rows = [json.loads(line) for line in (directory/"trace.jsonl").read_text().splitlines()]
    remote = manifest["role"] == "client"
    failures = []
    def require(test,message):
        if not test: failures.append(message)
    require(manifest["result"] == "captured","capture did not finish")
    raw = [r for r in rows if r.get("pose",{}).get("bones")]
    mismatches = [r for r in raw if r["pose"]["physicsFrame"] != r["physicsFrame"]]
    incoherent = [r for r in raw if remote and not(r["pose"].get("presentationTick")==r.get("selectedPresentationTick")==r.get("ballPresentationTick"))]
    coherent = [r for r in raw if r not in mismatches and r not in incoherent]
    shots = [r for r in coherent if r["pose"].get("move")=="jumpshot" and r["pose"].get("phase") != "Inactive"]
    require(bool(shots),"no coherent jumpshot observation")
    feint = manifest["variant"] in ("early","late")
    phases = {r["pose"]["phase"] for r in shots}
    for phase in (("Startup","Recovery") if feint else ("Startup","Active","Recovery")):
        require(phase in phases,"missing phase "+phase)
    require(not feint or "Active" not in phases,"feint displayed Active")
    server_manifest = manifest
    if remote:
        server_path = Path(str(directory)+"-server")/"manifest.json"
        require(server_path.exists(),"remote authoritative manifest missing")
        server_manifest = json.loads(server_path.read_text()) if server_path.exists() else {}
    releases = [e for e in server_manifest.get("events",[]) if e["kind"]=="release"]
    require(server_manifest.get("releases")== (0 if feint else 1),"authoritative release counter mismatch")
    require(len(releases)==(0 if feint else 1),"authoritative release occurrence missing or duplicated")
    for event in releases:
        require(event.get("phase")=="Active" and event.get("frame")==0,"release outside first Active frame")
    if feint:
        requests = [e for e in server_manifest.get("events",[]) if e["kind"]=="feint-input"]
        require(len(requests)==1 and requests[0].get("phaseFrameBeforeInput")== (3 if manifest["variant"]=="early" else 11),"wrong/missing legal feint request")
        require(any(r["pose"]["frame"]>=12 and r["pose"]["phase"]=="Recovery" for r in shots),"accepted feint Recovery unobserved")
    release_window = []
    for event in releases:
        release_tick = event["physicsFrame"]
        for offset in range(-2,3):
            candidates = [r for r in coherent if clock(r,remote)==release_tick+offset]
            require(bool(candidates),f"missing release event window offset {offset}")
            if not candidates: continue
            r = candidates[-1]
            if offset == 0:
                require(r["pose"].get("move")=="jumpshot" and r["pose"]["phase"]=="Active" and r["pose"]["frame"]==0,
                        "release occurrence source tick lacks first-Active final-pose observation")
            wrist = r["pose"]["bones"]["mixamorig_RightHand"]["world"][:3]
            release_window.append(dict(offset=offset,tick=clock(r,remote),phase=r["pose"]["phase"],frame=r["pose"]["frame"],
                wristVisibleBallDistance=distance(wrist,r["ball"]),wristAuthoritativeOriginDistance=distance(wrist,event["position"])))
    metrics = []
    for r in shots:
        value = geometry(r["pose"])
        require(all(math.isfinite(x) for x in value.values()),"nonfinite geometry")
        metrics.append(dict(tick=clock(r,remote),phase=r["pose"]["phase"],frame=r["pose"]["frame"],**value))
    summary = {}
    if manifest["variant"]=="normal":
        groups = {}
        for name,phase,frames in (("gather","Startup",range(8,12)),("release","Active",range(1,4)),("follow","Recovery",range(4))):
            byframe = {m["frame"]:m for m in metrics if m["phase"]==phase and m["frame"] in frames}
            require(set(byframe)==set(frames),"missing normal form samples "+name)
            groups[name] = list(byframe.values())
        if all(groups.values()):
            g,a,f = groups["gather"],groups["release"],groups["follow"]
            summary = dict(gatherMax=max(m["elbow"] for m in g),releaseMean=sum(m["elbow"] for m in a)/len(a),
                releaseMinHeight=min(m["wristHeight"] for m in a),releaseMinAsym=min(m["asymmetry"] for m in a),
                flexionChange=sum(m["wristFlexion"] for m in f)/len(f)-sum(m["wristFlexion"] for m in g)/len(g),
                followMinElbow=min(m["elbow"] for m in f),followMinAsym=min(m["asymmetry"] for m in f))
            if form:
                for passed,msg in ((summary["gatherMax"]<180,"loaded elbow not bent"),(summary["releaseMean"]>summary["gatherMax"],"release extension"),
                    (summary["releaseMinHeight"]>0,"release wrist height"),(summary["releaseMinAsym"]>0,"release shooting/guide asymmetry"),
                    (summary["flexionChange"]>0,"forward/down wrist flexion"),(summary["followMinElbow"]>summary["gatherMax"],"followthrough extension"),
                    (summary["followMinAsym"]>0,"followthrough shooting/guide asymmetry")):
                    require(passed,msg)
    gaps = [b["physicsFrame"]-a["physicsFrame"] for a,b in zip(raw,raw[1:])]
    holds = sum(bool(r.get("presentationHolding")) for r in rows)
    resets = max((r.get("presentationResyncCount",0) for r in rows),default=0)
    stable = [r for i,r in enumerate(coherent) if i==0 or r.get("presentationResyncCount",0)==coherent[i-1].get("presentationResyncCount",0)]
    seams = boundaries(stable,remote,mutate)
    # Frozen motion limits describe three adjacent one-source-tick edges.
    # A missing pose can hide a transient jump even when the remaining endpoints
    # fit that limit, so retain its diagnostics but reject the incomplete proof.
    for seam in seams:
        require(len(seam["edges"]) == 3 and all(edge["tickGap"] == 1 for edge in seam["edges"]),
                "incomplete one-tick phase boundary " + seam["transition"])
    required_seams = {"Startup->Recovery","Recovery->Inactive"} if feint else {"Startup->Active","Active->Recovery","Recovery->Inactive"}
    for transition in sorted(required_seams):
        require(transition in {s["transition"] for s in seams},"missing coherent phase boundary "+transition)
    return dict(case=directory.name,result="failed" if failures else "passed",failures=failures,role=manifest["role"],variant=manifest["variant"],
        hand=manifest["hand"],resourceSha256=manifest.get("animationResourceSha256"),coherentShotSamples=len(shots),posePhysicsMismatches=len(mismatches),
        presentationMismatches=len(incoherent),renderPhysicsGaps=sum(x>1 for x in gaps),maxRenderPhysicsGap=max(gaps,default=0),
        presentationHoldSamples=holds,presentationResets=resets,resetSpanContinuityExcluded=True,form=summary,metrics=metrics,
        releaseWindow=release_window,boundaries=seams)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory",type=Path)
    parser.add_argument("--output",type=Path,required=True)
    parser.add_argument("--no-form-gates",action="store_true",help="baseline measurement; coverage/release contracts still apply")
    parser.add_argument("--baseline",type=Path)
    parser.add_argument("--mutate-boundary",action="store_true",help="negative control: displace wrist by twice the observed seam bound plus one metre")
    args=parser.parse_args()
    directories=[args.directory] if (args.directory/"manifest.json").exists() else sorted(p.parent for p in args.directory.glob("*/manifest.json") if not p.parent.name.endswith("-server"))
    results=[analyze(p,not args.no_form_gates,args.mutate_boundary) for p in directories]
    if args.baseline:
        baseline={r["case"]:r for r in json.loads(args.baseline.read_text())["cases"]}
        for r in results:
            prior=baseline.get(r["case"])
            if prior is None:
                r["failures"].append("baseline case missing")
            else:
                limits={b["transition"]:b for b in prior["boundaries"]}
                for b in r["boundaries"]:
                    old = limits.get(b["transition"])
                    if old is not None:
                        b["baselineMotionBoundM"] = old["maxDisplacement"]
                        b["baselineCoordinateResolutionM"] = old["coordinateResolutionM"]
                    if old is None or b["maxDisplacement"]>old["maxDisplacement"]+old["coordinateResolutionM"]:
                        r["failures"].append("boundary displacement exceeds baseline: "+b["transition"])
            r["result"]="failed" if r["failures"] else "passed"
    passed=bool(results) and all(r["result"]=="passed" for r in results)
    args.output.parent.mkdir(parents=True,exist_ok=True)
    args.output.write_text(json.dumps(dict(result="passed" if passed else "failed",formGates=not args.no_form_gates,cases=results),indent=2))
    print(f"[jumpshot-proof] {'PASS' if passed else 'FAIL'} cases={len(results)} report={args.output}")
    raise SystemExit(0 if passed else 1)


if __name__=="__main__": main()
