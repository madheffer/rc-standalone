// Frida agent for run_probe.py --frida: pins the two runtime inputs that reach a cubemap face (ledger 177) so that
// two builds of an animated map can be compared: every per-view block's real and game time words (scenesystem
// 1800e8e40 on leave, words 296 / 300) and the client's view attributes GameTime, WindDirection, WindStrength and
// the third wind word (client 18016af20, hash at arg 1, four floats at arg 2), written with the values atixref's
// 10-28 run (cubehost_run/atixref_time1) built with. Builds: the installed 10-29 DLLs (tools/re/binaries/
// *_20261029.dll); each hook checks its prologue first. Records: pinned (counts per input), mismatch.
// Also the light block's dither pair: tier0's global RandomFloat (time-seeded, ledger 186) called from scenesystem
// returns a constant per call site (the 10-28 run's first two draws), since the call count varies run to run.
// And the light styles' phase: the light component's style evaluation (client 1801e4880 .. 1801e5460) reads the
// game time through 18075e820 (writes a float to its out pointer, rcx) and subtracts the style's start time (the
// component's +0x1a0, set at spawn, so it varies too); the time it gets is forced to start + GAME, so every
// style is evaluated at the same phase. rdi is the component in the caller and the accessor does not touch it.
// And scenesystem's real time: FinishRenderingViews (1800fc550) is passed FLT_MAX, which makes g_flRealTime a fresh
// Plat_FloatTime per view (ledger 177); it is called with REAL instead.
'use strict';

const IMAGE = ptr('0x180000000');
const REAL = 1126352187, GAME = 1104530619;  // 162.77, 26.724966
const ATTRS = {
  0xc4eabee1: [GAME, GAME, GAME, GAME],                                   // GameTime
  0x0bb9ee08: [1065353216, 0, 0, 0],                                      // WindDirection
  0xeb0d997e: [996509919, 1022739087, 994352038, 1048576000],             // WindStrength
  0xcde54581: [1087753403, 1090537380, 1084951087, 0],                    // the third wind word
};
const DITHER = [1058042080, 1063685580];  // 0.5855, 0.9216 (10-28 draws 1 and 2)
const counts = {};
let done = 0;

function at(mod, va) { return mod.base.add(ptr(va).sub(IMAGE)); }
function expect(mod, va, hex) {
  const p = at(mod, va);
  const got = Array.from(new Uint8Array(p.readByteArray(hex.length / 2))).map(b => b.toString(16).padStart(2, '0')).join('');
  if (got !== hex) { send({ kind: 'mismatch', module: mod.name, va, want: hex, got }); return null; }
  return p;
}
function count(k) { counts[k] = (counts[k] || 0) + 1; if (counts[k] % 5000 === 1) send({ kind: 'pinned', counts }); }

function hookClient(cl) {
  const set = expect(cl, '0x18016af20', '48896c2410488974');
  if (set)
    Interceptor.attach(set, { onEnter(args) {
      const v = ATTRS[args[1].toUInt32() >>> 0];
      if (v === undefined) return;
      for (let i = 0; i < 4; i++) args[2].add(4 * i).writeU32(v[i]);
      count('attr');
    } });
}
function hookStyles(cl) {
  const get = expect(cl, '0x18075e820', '40534883ec20488b');
  if (!get) return;
  const lo = at(cl, '0x1801e4880'), hi = at(cl, '0x1801e5460'), f = new DataView(new ArrayBuffer(4));
  f.setUint32(0, GAME, true);
  const k = f.getFloat32(0, true);
  Interceptor.attach(get, {
    onEnter(args) { this.out = args[0]; this.mine = this.returnAddress.compare(lo) >= 0 && this.returnAddress.compare(hi) < 0; },
    onLeave() {
      if (!this.mine) return;
      this.out.writeFloat(this.context.rdi.add(0x1a0).readFloat() + k);
      count('style');
    } });
}
function hookFinish(ss) {
  const finish = expect(ss, '0x1800fc550', '488bc44889581048');
  if (!finish) return;
  const original = new NativeFunction(finish, 'void', ['pointer', 'float'], 'win64');
  const f = new DataView(new ArrayBuffer(4));
  f.setUint32(0, REAL, true);
  const real = f.getFloat32(0, true);
  Interceptor.replace(finish, new NativeCallback(function (sys, time) {
    count('finish');
    original(sys, real);
  }, 'void', ['pointer', 'float'], 'win64'));
}
function hookScene(ss) {
  const perview = expect(ss, '0x1800e8e40', '488bc44889582055');
  if (perview)
    Interceptor.attach(perview, {
      onEnter(args) { this.block = args[1]; },
      onLeave() { this.block.add(296).writeU32(REAL); this.block.add(300).writeU32(GAME); count('perview'); } });
}
function hookTier0(t0, ss) {
  const rf = t0.getExportByName('RandomFloat');
  const original = new NativeFunction(rf, 'float', ['float', 'float'], 'win64');
  const lo = ss.base, hi = ss.base.add(ss.size), sites = {};
  const f = new DataView(new ArrayBuffer(4));
  Interceptor.replace(rf, new NativeCallback(function (a, b) {
    const r = this.returnAddress;
    if (!(r && r.compare(lo) >= 0 && r.compare(hi) < 0)) return original(a, b);
    const k = r.toString();
    if (sites[k] === undefined) sites[k] = Object.keys(sites).length % 2;
    f.setUint32(0, DITHER[sites[k]], true);
    count('dither');
    return a + (b - a) * f.getFloat32(0, true);
  }, 'float', ['float', 'float'], 'win64'));
}
function poll() {
  const cl = Process.findModuleByName('client.dll'), ss = Process.findModuleByName('scenesystem.dll');
  if (cl && !(done & 1)) { done |= 1; hookClient(cl); hookStyles(cl); }
  if (ss && !(done & 2)) { done |= 2; hookScene(ss); hookFinish(ss); hookTier0(Process.findModuleByName('tier0.dll'), ss); }
  if (done !== 3) setTimeout(poll, 5); else send({ kind: 'hooked' });
}
poll();
