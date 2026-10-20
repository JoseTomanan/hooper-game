# Frozen diagnostic of the old trace corpus against the current imported rig.
# Two-pass command is in the accompanying analysis. Intermediate mesh JSON is
# ~131 MB under .godot; this is an opt-in measurement, never a CI/runtime hook.
import gzip,json,math,pathlib
root=pathlib.Path(__file__).resolve().parents[4]
inputs=[]
for hand in ('left','right'):
 path=root/f'docs/analysis/evidence/0417/local-{hand}-stationary-1-trace.jsonl.gz'
 for r in map(json.loads,gzip.open(path,'rt')):
  if r['measuring'] and r['sweepActive']:
   r['origin']=hand;inputs.append(r)
(root/'.godot/417-clearance-input.json').write_text(json.dumps(inputs))
meshfile=root/'.godot/417-clearance-meshes.json'
if not meshfile.exists():
 print('Input extracted. Run Godot --headless --path . --log-file .godot/417-clearance.log --script docs/analysis/evidence/0417-repair/reconstruct-mesh.gd, then rerun this script.');raise SystemExit(0)
meshes=json.load(meshfile.open());rows=[]
for m,r in zip(meshes,inputs):
 assert m['sample']==r['sample'] and m['origin']==r['origin']
 assert m['maxBoneError'] < 0.001, 'Reconstructed pose does not match independent trace'
 forward=(math.sin(r['heading']),0,math.cos(r['heading']));origin=r['root'][:3]
 def fwd(p):return sum((p[i]-origin[i])*forward[i] for i in range(3))
 rear=min(fwd(v) for t in m['triangles'] for v in t)
 # Full triangle vertices make this a conservative lower bound: a triangle may
 # enter the slab while its rearmost vertex lies outside it.
 floorrear=min(fwd(v) for t in m['triangles'] if min(v[1] for v in t)<=.24 and max(v[1] for v in t)>=0 for v in t)
 old=fwd(r['ball']);dip=(.5-old)/.7;new=.5-dip*1.0
 rows.append(dict(origin=r['origin'],sample=r['sample'],progress=r['sweepProgress'],maxBoneErrorMetres=m['maxBoneError'],meshRearPlane=rear,ballHeightSlabConservativeRearPlane=floorrear,oldBallForwardSurface=old+.12,newBallForwardSurface=new+.12,oldEntireMeshPlaneClearance=rear-old-.12,newEntireMeshPlaneClearance=rear-new-.12))
report=dict(schema='hooper-417-clearance-diagnostic/v1',source='Existing 0417 local stationary traces; imported Y Bot mesh and Skin; CPU skinning with traced local pose rotations and traced world Hips. Bone-error is max distance against independently logged world bones.',scope='14 sampled sweep poses, both hands; body-forward halfspace separation only. No renderer visibility or feel claim. Floor slab plane is conservative.',maxBoneErrorMetres=max(r['maxBoneErrorMetres'] for r in rows),rows=rows)
(root/'.godot/417-clearance-report.json').write_text(json.dumps(report,indent=2))
print(json.dumps({k:v for k,v in report.items() if k!='rows'},indent=2))
print('Wrote .godot/417-clearance-report.json')
