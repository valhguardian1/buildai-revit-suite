'use strict';
const fs = require('node:fs'), path = require('node:path'), vm = require('node:vm');
const assert = require('node:assert/strict');
const root = path.join(__dirname, '..');
const html = fs.readFileSync(path.join(root, 'src/ViewerProbe.Shared/viewer-probe.html'), 'utf8');
const section = (a,b) => html.slice(html.indexOf(a), html.indexOf(b));
class V {
  constructor(x=0,y=0,z=0) { Object.assign(this,{x,y,z}); }
  clone() { return new V(this.x,this.y,this.z); }
  toArray() { return [this.x,this.y,this.z]; }
  add(v) { this.x+=v.x; this.y+=v.y; this.z+=v.z; return this; }
  sub(v) { this.x-=v.x; this.y-=v.y; this.z-=v.z; return this; }
  multiplyScalar(n) { this.x*=n; this.y*=n; this.z*=n; return this; }
  lengthSq() { return this.dot(this); }
  length() { return Math.sqrt(this.lengthSq()); }
  normalize() { return this.multiplyScalar(1/(this.length() || 1)); }
  dot(v) { return this.x*v.x+this.y*v.y+this.z*v.z; }
  cross(v) { return this.crossVectors(this.clone(),v); }
  crossVectors(a,b) { this.x=a.y*b.z-a.z*b.y; this.y=a.z*b.x-a.x*b.z; this.z=a.x*b.y-a.y*b.x; return this; }
  negate() { return this.multiplyScalar(-1); }
}
class Box {
  constructor(min,max) { this.min=min; this.max=max; }
  getSize() { return this.max.clone().sub(this.min); }
}
const bounds = new Box(new V(-.1,-.1,-.1),new V(.1,.1,.1));
function harness(accept = () => true) {
  const logs=[], distances=[];
  const context=vm.createContext({THREE:{Vector3:V,Ray:class {constructor(origin,direction){Object.assign(this,{origin,direction});}}},
    progress:s=>logs.push(s), viewer:{navigation:{getWorldUpVector:()=>new V(0,0,1)},impl:{rayIntersect:ray=>{
      const m=ray.origin.length()*.3048; distances.push(m);
      return accept(m) ? null : {distance:.01,dbId:999};
    }}}});
  vm.runInContext('const CLASH_CAMERA_DISTANCE_FT=.70/.3048; const CLASH_FOCUS_MIN_FT=8,CLASH_FOCUS_MAX_FT=30,CLASH_CONTEXT_FACTOR=1.5;\n'+
    section('  function pointInsideBounds(', '  function pointsMatch(')+
    section('  function cameraDistance(', '  function cameraAngle(')+
    section('  function buildViewport(', '  function matrixElements(')+
    '\nthis.api={chooseClashCameraDirection,tryFindValidClashCameraAtDistance,buildViewport};', context);
  const choose=()=>context.api.chooseClashCameraDirection(new V(),new V(1,0,0),new V(0,0,1),bounds,bounds,1209337,957021,{}, {},null);
  return {context,logs,distances,choose,...context.api};
}
let passed=0;
function test(name,fn) { fn(); passed++; console.log('[OK] '+name); }
test('Primary output equals original 0.70 m first direction; no fallback',()=>{
  const h=harness(), c=h.choose();
  assert.equal(c.distanceFt,.7/.3048);
  assert.deepEqual(c.direction.toArray(),[0,-Math.cos(Math.PI/6),Math.sin(Math.PI/6)]);
  assert.ok(!h.logs.some(s=>s.includes('NEAR_FALLBACK')));
  const actual=h.buildViewport(bounds,new V(),null,null,null,null,c.direction,c.distanceFt);
  const prior=h.buildViewport(bounds,new V(),null,null,null,null,new V(0,-Math.cos(Math.PI/6),Math.sin(Math.PI/6)),.7/.3048);
  assert.equal(JSON.stringify(actual),JSON.stringify(prior));
});
for(const wanted of [.50,.40]) test('Fallback accepts '+wanted+' m after descending rejected distances',()=>{
  const h=harness(m=>Math.abs(m-wanted)<1e-9),c=h.choose();
  assert.ok(Math.abs(c.distanceFt*.3048-wanted)<1e-9);
  const visited=[...new Set(h.distances.map(m=>m.toFixed(2)))];
  assert.deepEqual(visited,[.7,.6,.8,.55,.5,.45,.4].filter((m,i)=>i<3||m>=wanted).map(m=>m.toFixed(2)));
  assert.ok(h.logs.some(s=>s.includes('reason=los_blocked')));
  const camera=h.buildViewport(bounds,new V(),null,null,null,null,c.direction,c.distanceFt);
  assert.deepEqual(Array.from(camera.target),[0,0,0]);
  assert.ok(camera.eye.some(v=>v!==0));
  assert.ok([...camera.eye,...camera.target,...camera.up].every(Number.isFinite));
  assert.ok(Math.abs(camera.up.reduce((s,v,i)=>s+v*(camera.target[i]-camera.eye[i]),0))<1e-6);
  assert.equal(camera.fieldOfView,45); assert.equal(camera.isOrthographic,false);
});
test('Inside geometry rejects distance and permits next smaller one',()=>{
 const h=harness(), reasons=[];
 const obstacle=new Box(new V(1.7,-1,-1),new V(1.9,1,1));
 const attempt=m=>h.tryFindValidClashCameraAtDistance(new V(),[new V(1,0,0)],m/.3048,new V(0,0,1),obstacle,null,{},null,r=>reasons.push(r));
 assert.equal(attempt(.55),null); assert.deepEqual(reasons,['inside_geometry']); assert.ok(attempt(.50));
});
test('No ideal LOS uses deterministic fallback after near and far search; next row still succeeds',()=>{
 let clear=false; const h=harness(()=>clear), fallback=h.choose();
 assert.equal(fallback.quality,'DeterministicFallback');
 assert.equal(Math.min(...h.distances).toFixed(2),'0.25');
 assert.ok(h.distances.some(m=>Math.abs(m-3.0)<1e-9));
 assert.ok(h.logs.includes('CLASH_CAMERA_NEAR_FALLBACK_FAILED | minDistanceM=0.25'));
 assert.ok(h.logs.some(s=>s.includes('CAMERA_FALLBACK_USED | quality=DeterministicFallback')));
 clear=true; assert.equal(h.choose().quality,'Primary');
});
test('Non-finite eye and degenerate basis rejected',()=>{
 const h=harness(),reasons=[];
 const call=d=>h.tryFindValidClashCameraAtDistance(new V(),[d],1,new V(0,0,1),null,null,{},null,r=>reasons.push(r));
 assert.equal(call(new V(NaN,0,0)),null); assert.equal(reasons[0],'non_finite');
 assert.equal(call(new V()),null); assert.equal(reasons[1],'invalid_basis');
});
test('Shared helper and fallback gated by exact Clash intersection; objectSet retained',()=>{
 assert.match(html,/const clashCamera = exactIntersection\s*\? chooseClashCameraDirection/);
 assert.match(html,/exactIntersection \? clashCamera.distanceFt : null/);
 assert.match(html,/isolated: entry.id.slice\(\)/);
 assert.match(html,/capturedSelectionCount < expectedSelectionCount/);
 const wf=fs.readFileSync(path.join(root,'src/Plugin5.ClashFormaIntegration/Issues/ClashIssueWorkflow.cs'),'utf8');
 assert.match(wf,/probe.SecondaryDbId > 0 \? \(int\?\)probe.SecondaryDbId : null/);
 for(const key of ['cameraPrimaryAccepted','cameraNearFallbackAccepted','cameraRejected','postAttempted','created','failed']) assert.ok(wf.includes(key));
 assert.ok(wf.indexOf('postAttempted++;')<wf.indexOf('var created = await'));
 assert.ok(wf.indexOf('ok++;')>wf.indexOf('var created = await'));
});
test('Both reported dbIds remain selected and isolated',()=>{
 const state={objectSet:[{id:[1209337,957021]}]};
 const c=vm.createContext({viewerState:state,expectedSelectionCount:2});
 vm.runInContext(section('    viewerState.objectSet = viewerState.objectSet', '    // Keep the complete coordination model'),c);
 assert.deepEqual(Array.from(state.objectSet[0].id),[1209337,957021]);
 assert.deepEqual(Array.from(state.objectSet[0].isolated),[1209337,957021]);
});
test('Non-Clash sources, payload, publication and Viewer sections are byte-identical',()=>{
 const crypto=require('node:crypto'),hash=b=>crypto.createHash('sha256').update(b).digest('hex');
 const contract=JSON.parse(fs.readFileSync(path.join(__dirname,'non-clash-7.3.28-contract.json'),'utf8'));
 for(const [file,expected] of Object.entries(contract.files)) assert.equal(hash(fs.readFileSync(path.join(root,file))),expected,file);
 const locator=fs.readFileSync(path.join(root,'src/RevitCompat.Shared/PushpinLocator.cs'));
 assert.equal(hash(locator.subarray(locator.indexOf('        /// <summary>\n        /// Points a 3D view'))),contract.localNavigationTailSha256);
 const bytes=fs.readFileSync(path.join(root,'src/ViewerProbe.Shared/viewer-probe.html'));
 for(const s of contract.viewerSections) assert.equal(hash(bytes.subarray(bytes.indexOf(s.start),bytes.indexOf(s.end))),s.sha256,s.start);
});
console.log(`Clash near camera: ${passed} tests passed.`);
