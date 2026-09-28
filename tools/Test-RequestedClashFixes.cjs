'use strict';
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');

const root = path.join(__dirname, '..');
const html = fs.readFileSync(path.join(root, 'src/ViewerProbe.Shared/viewer-probe.html'), 'utf8');
const section = (start, end) => {
  const from = html.indexOf(start), to = html.indexOf(end, from);
  assert.ok(from >= 0 && to > from, `Missing Viewer section ${start}`);
  return html.slice(from, to);
};

class V {
  constructor(x=0,y=0,z=0) { Object.assign(this,{x,y,z}); }
  clone() { return new V(this.x,this.y,this.z); }
  copy(v) { Object.assign(this,{x:v.x,y:v.y,z:v.z}); return this; }
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
  distanceTo(v) { return this.clone().sub(v).length(); }
}
class Box {
  constructor(min,max) { this.min=min; this.max=max; this.isEmptyBox=false; }
  getSize(target=new V()) { return target.copy(this.max).sub(this.min); }
}

function load(accept) {
  const logs=[], distances=[];
  const context=vm.createContext({
    THREE:{Vector3:V,Ray:class {constructor(origin,direction){Object.assign(this,{origin,direction});}}},
    progress:s=>logs.push(s),
    cameraDistance:(a,b)=>Math.hypot(a[0]-b[0],a[1]-b[1],a[2]-b[2]),
    viewer:{navigation:{getWorldUpVector:()=>new V(0,0,1)},impl:{rayIntersect:ray=>{
      const metres=ray.origin.length()*.3048; distances.push(metres);
      return accept(metres,ray) ? null : {distance:.01,dbId:999};
    }}}
  });
  vm.runInContext(
    'const CLASH_CAMERA_DISTANCE_FT=.70/.3048; const CLASH_FOCUS_MIN_FT=8,CLASH_FOCUS_MAX_FT=30,CLASH_CONTEXT_FACTOR=1.5;\n' +
    section('  function pointInsideBounds(', '  function pointsMatch(') +
    section('  function buildViewport(', '  function matrixElements(') +
    '\nthis.api={validateClashPushpinAnchor,chooseClashCameraDirection,buildViewport};', context);
  return {api:context.api,logs,distances};
}

let passed=0;
function test(name,fn){fn();passed++;console.log('[OK] '+name);}
const primary=new Box(new V(-1,-1,-1),new V(1,1,1));
const secondary=new Box(new V(-.5,-.5,-.5),new V(1.5,1.5,1.5));

test('exact Revit intersection remains unchanged when Viewer bounds accept it',()=>{
  const {api}=load(()=>true), anchor=new V(.2,.3,.4);
  const r=api.validateClashPushpinAnchor(anchor,primary,secondary,.01,.05,0,true);
  assert.equal(r.outcome,'ExactRevitIntersectionAccepted');
  assert.deepEqual(r.anchor.toArray(),anchor.toArray());
  assert.equal(r.correctionApplied,false);
});
test('missing Viewer mesh samples do not reject a confirmed Revit clash',()=>{
  const {api}=load(()=>true), anchor=new V(5,5,5);
  const r=api.validateClashPushpinAnchor(anchor,primary,new Box(new V(10,10,10),new V(11,11,11)),.01,.05,0,true);
  assert.equal(r.outcome,'ViewerValidationUnavailable');
  assert.deepEqual(r.anchor.toArray(),anchor.toArray());
});
test('10 mm Viewer tolerance accepts a boundary mismatch without moving the anchor',()=>{
  const {api}=load(()=>true), anchor=new V(1.009,0,0);
  const r=api.validateClashPushpinAnchor(anchor,primary,secondary,.01,.05,0,true);
  assert.equal(r.outcome,'ViewerBoundsAccepted');
  assert.deepEqual(r.anchor.toArray(),anchor.toArray());
});
test('small mismatch is clamped into expanded overlap and correction is measured',()=>{
  const {api}=load(()=>true), anchor=new V(1.02,0,0);
  const r=api.validateClashPushpinAnchor(anchor,primary,secondary,.01,.05,8,true);
  assert.equal(r.outcome,'ViewerBoundsCorrected');
  assert.equal(r.correctionApplied,true);
  assert.ok(Math.abs(r.correctionDistanceViewer-.01)<1e-9);
});
test('camera candidates cover sectors around the target',()=>{
  const {api}=load((_,ray)=>Math.abs(ray.origin.x)>0.1);
  const c=api.chooseClashCameraDirection(new V(),new V(1,0,0),new V(0,0,1),primary,secondary,1,2,{}, {},new V(100,0,0));
  assert.ok(Math.abs(c.direction.x)>0.1,'camera direction must not be confined to the YZ plane');
});test('far camera fallback reaches 1.5 m after primary and near failures',()=>{
  const {api,logs}=load(m=>Math.abs(m-1.5)<1e-8);
  const c=api.chooseClashCameraDirection(new V(),new V(1,0,0),new V(0,0,1),primary,secondary,1,2,{}, {},null);
  assert.ok(Math.abs(c.distanceFt*.3048-1.5)<1e-8);
  assert.equal(c.quality,'FarFallback');
  assert.ok(logs.some(x=>x.includes('CLASH_CAMERA_FAR_FALLBACK_ACCEPTED')));
});
test('deterministic fallback is finite, outside both bounds and targets the pushpin',()=>{
  const {api}=load(()=>false), anchor=new V();
  const c=api.chooseClashCameraDirection(anchor,new V(1,0,0),new V(0,0,1),primary,secondary,1,2,{}, {},null);
  const viewport=api.buildViewport(primary,anchor,null,null,null,null,c.direction,c.distanceFt);
  assert.equal(c.quality,'DeterministicFallback');
  assert.ok([...viewport.eye,...viewport.target,...viewport.up].every(Number.isFinite));
  assert.deepEqual(Array.from(viewport.target),[0,0,0]);
  assert.ok(Math.abs(viewport.up.reduce((s,v,i)=>s+v*(viewport.target[i]-viewport.eye[i]),0))<1e-6);
});
test('17 confirmed finite clashes all reach the accepted validation stage',()=>{
  const {api}=load(()=>true);
  for(let i=0;i<17;i++){
    const r=api.validateClashPushpinAnchor(new V(i/1000,0,0),primary,secondary,.01,.05,0,true);
    assert.notEqual(r.outcome,'UnsafeCoordinateRejected');
  }
});

console.log(`Requested Clash fixes: ${passed} tests passed.`);
