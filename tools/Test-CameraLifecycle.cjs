'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const html = fs.readFileSync(path.join(__dirname, '../src/ViewerProbe.Shared/viewer-probe.html'), 'utf8');
const script = html.match(/<script>\s*([\s\S]*?)<\/script>/)[1];
new vm.Script(script); // Parse the entire production script, not just extracted helpers.
function section(start, end) { return html.slice(html.indexOf(start), html.indexOf(end)); }
const helpers = section('  function pointsMatch(', '  function buildViewport(');
const runners = section('  async function runProbe(request)', '  window.__startProbe =');
const wanted = { eye: [6.56167979, 0, 0], target: [0, 0, 0], up: [0, 0, 1],
  worldUpVector: [0, 0, 1], isOrthographic: false, projection: 'perspective',
  fieldOfView: 45, orthographicHeight: 8, distanceToOrbit: 6.56167979 };
const clone = x => JSON.parse(JSON.stringify(x));
class Vector3 {
  constructor(x, y, z) { this.x = x; this.y = y; this.z = z; }
  toArray() { return [this.x, this.y, this.z]; }
}
function harness(options = {}) {
  const events = new Map(), logs = [], order = [], timers = new Set();
  const camera = clone(wanted);
  camera.isOrthographic = true; // The published coordination view.
  let applications = 0, frames = 0, dirty = false, pending = null, active = false, stateReads = 0;
  const vector = a => new Vector3(...a);
  const schedule = fn => {
    const id = setTimeout(() => { timers.delete(id); fn(); }, 1);
    timers.add(id); return id;
  };
  const emit = name => { for (const fn of [...(events.get(name) || [])]) fn(); };
  const nav = {
    getPosition: () => vector(camera.eye), getTarget: () => vector(camera.target),
    getCameraUpVector: () => vector(camera.up), getCamera: () => ({ isPerspective: !camera.isOrthographic }),
    getVerticalFov: () => camera.fieldOfView,
    setRequestTransition: value => { if (!value) pending = null; },
    setRequestFitToView: value => { assert.equal(value, false); },
    setRequestHomeView: value => { assert.equal(value, false); },
    getRequestTransition: () => pending, getTransitionActive: () => active,
    toPerspective: () => { camera.isOrthographic = false; },
    setWorldUpVector: () => {}, setVerticalFov: value => { camera.fieldOfView = value; },
    setView: (eye, target) => {
      applications++; order.push('apply');
      camera.eye = eye.toArray(); camera.target = target.toArray(); dirty = true;
      if (options.normalize) camera.eye[0] += 0.0002;
    },
    setCameraUpVector: up => { camera.up = up.toArray().map(v => v * (options.normalize ? 1.000002 : 1)); },
    setPivotPoint: () => {}
  };
  const viewer = {
    navigation: nav, autocam: { shotParams: { duration: 1 }, currentlyAnimating: false },
    addEventListener: (name, fn) => { if (!events.has(name)) events.set(name, new Set()); events.get(name).add(fn); },
    removeEventListener: (name, fn) => events.get(name)?.delete(fn),
    impl: { invalidate: () => {
      if (options.noFrames) return;
      schedule(() => {
        frames++;
        if (options.overwrite && (!options.firstOnly || applications === 1)) camera.target = [1, 0, 0];
        if (options.jitter) { camera.eye[1] += 0.01; dirty = true; }
        if (dirty && !options.noEvent) { dirty = false; emit('cameraChanged'); }
        emit('renderPresented');
      });
    } },
    getState: () => {
      stateReads++; order.push('getState');
      const result = { viewport: clone(camera), objectSet: [{ id: [12, 13] }] };
      if (options.serializedMismatch) result.viewport.target[0] += 0.01;
      if (options.stateSideEffect) camera.eye[0] += 1;
      return result;
    }
  };
  const context = vm.createContext({ viewer, THREE: { Vector3 },
    Autodesk: { Viewing: { CAMERA_CHANGE_EVENT: 'cameraChanged', RENDER_PRESENTED_EVENT: 'renderPresented' } },
    progress: text => logs.push(text), setTimeout: (fn, ms) => setTimeout(fn, Math.min(ms, 250)), clearTimeout,
    requestAnimationFrame: schedule, cancelAnimationFrame: id => { clearTimeout(id); timers.delete(id); }
  });
  vm.runInContext(helpers + '\n' + runners + '\nthis.api = { captureIssueCamera, compareArstCamera, cameraDto, assertIssueCamera, issueCameraState, assertLegacyIssueCamera, stopIssueCameraTransition, runProbe, runProbeBatch };', context);
  return { ...context.api, context, camera, viewer, logs, order,
    counts: () => ({ applications, frames, stateReads, listeners: [...events.values()].reduce((s, v) => s + v.size, 0) }),
    active: value => { active = value; viewer.autocam.currentlyAnimating = value; },
    close: () => { for (const id of timers) clearTimeout(id); }
  };
}
let passed = 0;
async function test(name, body) {
  await body(); passed++; console.log('[OK] ' + name);
}
(async () => {
  await test('Original orthographic restore deterministically changes eye; new capture uses perspective', async () => {
    const h = harness();
    const inherited = h.issueCameraState(wanted).viewport;
    assert.equal(inherited.isOrthographic, true);
    // Autodesk UnifiedCamera.adjustOrthoCamera: eye = target - normalized(target-eye) * orthoScale.
    const sdkResult = clone(inherited); sdkResult.eye = [inherited.orthographicHeight, 0, 0];
    assert.throws(() => h.assertLegacyIssueCamera(sdkResult, inherited), /CAMERA CAPTURE REJECTED/);
    const state = await h.captureIssueCamera(wanted, wanted.target);
    assert.equal(state.viewport.isOrthographic, false);
    assert.deepEqual(Array.from(state.viewport.target), wanted.target);
    h.close();
  });
  await test('Capture follows camera event and two unchanged presented-frame comparisons', async () => {
    const h = harness(); await h.captureIssueCamera(wanted, wanted.target);
    assert.ok(h.counts().frames >= 3); assert.equal(h.counts().stateReads, 1);
    const entry = JSON.parse(h.logs[0].split(' | ')[1]);
    assert.ok(entry.cameraEventCount >= 1); assert.equal(entry.stableFrameCount, 2);
    assert.equal(entry.pushpinToTargetDelta, 0); assert.equal(entry.serializedTargetToPushpinDelta, 0);
    assert.equal(h.counts().listeners, 0); h.close();
  });
  await test('Harmless Viewer normalization accepted and actual eye retained in payload', async () => {
    const h = harness({ normalize: true }); const state = await h.captureIssueCamera(wanted, wanted.target);
    assert.equal(state.viewport.eye[0], wanted.eye[0] + 0.0002);
    assert.equal(h.counts().applications, 1); h.close();
  });
  for (const [name, options] of [
    ['Actual camera overwrite detected', { overwrite: true }],
    ['Serialized target mismatch detected', { serializedMismatch: true }],
    ['getState extension side effect detected', { stateSideEffect: true }],
    ['Missing render frames time out', { noFrames: true }],
    ['Missing camera event times out', { noEvent: true }],
    ['Moving camera times out', { jitter: true }]
  ]) await test(name + '; at most one retry and no leaked listeners', async () => {
    const h = harness(options); const started = Date.now();
    await assert.rejects(h.captureIssueCamera(wanted, wanted.target), /CAMERA_CAPTURE_FAILED/);
    assert.equal(h.counts().applications, 2); assert.equal(h.counts().listeners, 0);
    assert.ok(Date.now() - started < 2000); h.close();
  });
  await test('One overwrite recovers on exactly one retry', async () => {
    const h = harness({ overwrite: true, firstOnly: true });
    await h.captureIssueCamera(wanted, wanted.target);
    assert.equal(h.counts().applications, 2); assert.equal(h.counts().stateReads, 1); h.close();
  });
  await test('Small coordinate differences accepted; large eye, target and up deviations rejected', async () => {
    const h = harness();
    const good = clone(wanted); good.target[1] = 0.00002; good.up[2] = 2;
    h.assertIssueCamera(good, wanted, wanted.target);
    for (const key of ['eye', 'target', 'up']) {
      const bad = clone(wanted); bad[key][1] += 0.01;
      assert.throws(() => h.assertIssueCamera(bad, wanted, wanted.target));
    }
    const wrongPin = [0.01, 0, 0]; assert.throws(() => h.assertIssueCamera(wanted, wanted, wrongPin));
    h.close();
  });
  await test('Prior AutoCam finishes before setView and shot duration is restored', async () => {
    const h = harness(); h.active(true);
    const timer = setInterval(() => {
      if (h.viewer.autocam.shotParams.duration === 0) { h.order.push('old-transition-finished'); h.active(false); clearInterval(timer); }
    }, 1);
    await h.captureIssueCamera(wanted, wanted.target);
    assert.equal(h.order[0], 'old-transition-finished');
    assert.equal(h.viewer.autocam.shotParams.duration, 1); h.close();
  });
  await test('Stuck previous transition rejects in bounded time without installing a new camera', async () => {
    const h = harness(); h.active(true);
    await assert.rejects(h.captureIssueCamera(wanted, wanted.target), /CAMERA_CAPTURE_FAILED/);
    assert.equal(h.counts().applications, 0); assert.equal(h.viewer.autocam.shotParams.duration, 1); h.close();
  });
  await test('Two batch calls and single probe have one exclusive owner until getState completes', async () => {
    const h = harness(); let owners = 0, maxOwners = 0;
    h.context.loadProbeScene = async request => { h.order.push('load:' + request.id); return {}; };
    h.context.resolveLoadedProbe = async (scene, request) => {
      owners++; maxOwners = Math.max(maxOwners, owners); h.order.push('begin:' + request.id);
      try {
        const result = await h.captureIssueCamera(wanted, wanted.target);
        h.order.push('captured:' + request.id); return result;
      } finally { owners--; }
    };
    await Promise.all([h.runProbeBatch([{ id: 'a' }, { id: 'b' }]), h.runProbeBatch([{ id: 'c' }]), h.runProbe({ id: 'd' })]);
    assert.equal(maxOwners, 1);
    for (const [a, b] of [['a', 'b'], ['b', 'c'], ['c', 'd']])
      assert.ok(h.order.indexOf('captured:' + a) < h.order.indexOf('begin:' + b));
    assert.ok(h.order.indexOf('captured:b') < h.order.indexOf('load:c'));
    assert.equal(h.counts().stateReads, 4); h.close();
  });
  await test('Rejected host invocation does not poison the queue', async () => {
    const h = harness();
    h.context.loadProbeScene = async () => ({});
    h.context.resolveLoadedProbe = async (_, req) => { if (req.fail) throw Error('failed item'); return { ok: true }; };
    await assert.rejects(h.runProbe({ fail: true }), /failed item/);
    const results = await h.runProbeBatch([{ fail: true }, {}]);
    assert.match(results[0].error, /failed item/); assert.equal(results[1].result.ok, true); h.close();
  });
  await test('Unstoppable writer prevents next item, scene load and queued invocation', async () => {
    const h = harness(); let starts = 0, loads = 0;
    h.context.loadProbeScene = async () => { loads++; return {}; };
    h.context.resolveLoadedProbe = async () => {
      starts++; h.active(true); return h.captureIssueCamera(wanted, wanted.target);
    };
    const results = await h.runProbeBatch([{}, {}]);
    assert.equal(starts, 1); assert.match(results[1].error, /cleanup failed/);
    await assert.rejects(h.runProbe({}), /cleanup failed/);
    assert.equal(loads, 1); h.close();
  });
  await test('Clash integration selects before capturing; AR-ST keeps legacy behavior', async () => {
    const resolver = section('  async function resolveLoadedProbe(', '  async function runProbe(request)');
    assert.match(resolver, /request.highlightSecondary &&[\s\S]*request.preferIntersectionAnchor/);
    assert.ok(resolver.indexOf('viewer.setAggregateSelection(aggregateSelection)') < resolver.indexOf('await captureIssueCamera('));
    assert.match(resolver, /captureArstCamera\(issueCamera, anchor\.toArray\(\),/);
    const workflow = fs.readFileSync(path.join(__dirname, '../src/Plugin5.ClashFormaIntegration/Issues/ClashIssueWorkflow.cs'), 'utf8');
    assert.match(workflow, /CAMERA_CAPTURE_FAILED/);
  });
  await test('AR-ST camera waits for Viewer stabilization and retries once without changing Clash path', async () => {
    const resolver = section('  async function resolveLoadedProbe(', '  async function runProbe(request)');
    assert.match(resolver, /await captureArstCamera\(issueCamera/);
    assert.doesNotMatch(resolver, /assertLegacyIssueCamera\(viewerState\.viewport/);
    assert.match(html, /function waitForArstCamera\([\s\S]*?frames >= 2/);
    assert.match(html, /ARST_CAMERA_RETRY/);
    assert.match(html, /attempt < 2/);
    assert.match(html, /compareArstCamera\(actual, viewport, pushpin/);
    assert.match(html, /distanceChangedOnly/);
    assert.doesNotMatch(html, /stableReads = stats\.events && stable/);
    assert.match(html, /setInterval\(rendered, 50\)/);
    assert.match(html, /ARST_CAMERA_COORDINATE_AUDIT/);
    assert.match(html, /AR-ST CAMERA CAPTURE REJECTED:/);
    assert.match(resolver, /captureIssueCamera\(viewport, anchor\.toArray\(\)\)/);
    const clash = fs.readFileSync(path.join(__dirname, '../src/Plugin5.ClashFormaIntegration/Issues/ClashIssueWorkflow.cs'), 'utf8');
    assert.doesNotMatch(clash, /captureArstCamera|ARST_CAMERA_/);
  });
  await test('AR-ST accepts Viewer distance normalization when target, direction, up and mode remain equal', async () => {
    const h = harness();
    const expected = { viewport: { eye: [0, 0, 6.561679790026246], target: [0, 0, 0], up: [0, 1, 0], projection: 'perspective', isOrthographic: false, fieldOfView: 45, distanceToOrbit: 6.561679790026246 } };
    const actual = { eye: [0, 0, 30], target: [0, 0, 0], up: [0, 1, 0], isOrthographic: false, fieldOfView: 45 };
    const result = h.compareArstCamera(actual, expected, [0, 0, 0], 703);
    assert.equal(result.accepted, true); assert.equal(result.distanceChangedOnly, true);
    assert.ok(result.actualDistance > result.expectedDistance); h.close();
  });
  console.log(`Camera lifecycle: ${passed} tests passed.`);
})().catch(error => { console.error(error); process.exitCode = 1; });
