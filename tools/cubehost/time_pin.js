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
// With RANDOMS, every other caller of tier0's global RandomFloat / RandomInt (time-seeded, ledger 186) gets a fixed
// sequence of its own: a counter per call site (module + RVA of the return address, so ASLR does not move it)
// hashed with splitmix32; `randoms` records tally the calls per module. Only GAME_MODULES: a fixed sequence for
// networksystem (16,691 calls a run: connection challenges) left the next run's map unloaded.
// With SORT, every scene layer sort (scenesystem 1800ed4c0: layer, items, count; CMeshDrawPrimitive_t 0x70 bytes)
// is redone on leave with the comparator's priorities (GROUND_TRUTH 156) but no address in it: the object (+0x18)
// becomes its world transform (+0x30, 3x4), the material (+0x20) its name (the CUtlString at the CMaterial2's
// +0x10, which materialsystem2 18000dc00 sets; read directly, since many are named before the agent attaches and
// a hook on the naming call missed 540 of 7,384 sorts), the per-object material (+0x28) and +0x48 are left out; ties fall to the draw's index count and then
// the input position. Valve breaks those ties by heap address, which changes run to run (ledger 187). Layers with
// +0x48 bit 1 are not sorted by Valve and are left alone.
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
const RANDOMS = true;
const SORT = true;
const GAME_MODULES = ['client.dll', 'server.dll', 'particles.dll', 'scenesystem.dll', 'worldrenderer.dll'];
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
const siteN = {}, perModule = {};
// Module ranges are cached by a timer, never looked up inside the callback: Process.findModuleByAddress there
// takes the loader lock from engine threads during start-up and stalled three runs in a row before the map loaded.
const ranges = [];
function refreshRanges() {
  for (const name of GAME_MODULES) {
    if (ranges.some(x => x.name === name)) continue;
    const m = Process.findModuleByName(name);
    if (m) ranges.push({ name, base: m.base, end: m.base.add(m.size) });
  }
  if (ranges.length < GAME_MODULES.length) setTimeout(refreshRanges, 200);
}
function siteOf(r) {  // 'module+rva' for a game module, null for anything else
  for (const x of ranges)
    if (r.compare(x.base) >= 0 && r.compare(x.end) < 0) return x.name + '+' + r.sub(x.base).toString(16);
  return null;
}
function unit(site) {  // the next value of this call site's own sequence, in [0, 1)
  const n = siteN[site] = (siteN[site] || 0) + 1;
  let h = 0x9e3779b9;
  for (let i = 0; i < site.length; i++) h = Math.imul(h ^ site.charCodeAt(i), 0x85ebca6b) >>> 0;
  let z = (h + Math.imul(n, 0x9e3779b9)) >>> 0;
  z = Math.imul(z ^ (z >>> 16), 0x85ebca6b) >>> 0;
  z = Math.imul(z ^ (z >>> 13), 0xc2b2ae35) >>> 0;
  z = (z ^ (z >>> 16)) >>> 0;
  const mod = site.split('+')[0];
  perModule[mod] = (perModule[mod] || 0) + 1;
  if ((counts.random = (counts.random || 0) + 1) % 2000 === 1) send({ kind: 'randoms', perModule });
  return z / 4294967296;
}
function hookTier0(t0, ss) {
  const rf = t0.getExportByName('RandomFloat');
  const original = new NativeFunction(rf, 'float', ['float', 'float'], 'win64');
  const lo = ss.base, hi = ss.base.add(ss.size), sites = {};
  const f = new DataView(new ArrayBuffer(4));
  if (RANDOMS) {
    const ri = t0.getExportByName('RandomInt');
    const riOriginal = new NativeFunction(ri, 'int', ['int', 'int'], 'win64');
    Interceptor.replace(ri, new NativeCallback(function (a, b) {
      const site = siteOf(this.returnAddress);
      if (site === null) return riOriginal(a, b);
      return b <= a ? a : a + Math.min(b - a, Math.floor(unit(site) * (b - a + 1)));
    }, 'int', ['int', 'int'], 'win64'));
  }
  Interceptor.replace(rf, new NativeCallback(function (a, b) {
    const r = this.returnAddress;
    if (!(r && r.compare(lo) >= 0 && r.compare(hi) < 0)) {
      const site = RANDOMS ? siteOf(r) : null;
      return site !== null ? a + (b - a) * unit(site) : original(a, b);
    }
    const k = r.toString();
    if (sites[k] === undefined) sites[k] = Object.keys(sites).length % 2;
    f.setUint32(0, DITHER[sites[k]], true);
    count('dither');
    return a + (b - a) * f.getFloat32(0, true);
  }, 'float', ['float', 'float'], 'win64'));
}
const materialNames = {};
function hookMaterialNames(ms) {
  for (const [va, bytes, utl] of [['0x18000cb40', '48895424104889', true], ['0x18000dc00', '48895c24104c89', false]]) {
    const p = expect(ms, va, bytes);
    if (p)
      Interceptor.attach(p, { onEnter(args) {
        try { materialNames[args[0].toString()] = utl ? args[1].readPointer().readCString() : args[1].readCString(); } catch (e) {}
      } });
  }
}
function hookSort(ss) {
  const sort = expect(ss, '0x1800ed4c0', '4585c9');
  if (!sort) return;
  Interceptor.attach(sort, {
    onEnter(args) { this.layer = args[1]; this.items = args[2]; this.n = args[3].toUInt32(); },
    onLeave() {
      const n = this.n;
      if (n < 2) return;
      const flags = this.layer.add(0x48).readU32();
      if (flags & 2) return;
      const full = (flags & 1) !== 0, recs = [];
      for (let i = 0; i < n; i++) {
        const p = this.items.add(i * 0x70), draw = p.add(0x08).readPointer(), obj = p.add(0x18).readPointer();
        const k62 = p.add(0x62).readU16(), k58 = p.add(0x58).readS32();
        const xf = obj.isNull() ? '' : Array.from(new Uint32Array(obj.add(0x30).readByteArray(48))).map(x => x.toString(16)).join(',');
        const matp = p.add(0x20).readPointer();
        let mat;
        try { mat = matp.isNull() ? '' : matp.add(0x10).readPointer().readCString(); } catch (e) { mat = null; }
        recs.push({ i, bytes: p.readByteArray(0x70), key: [full ? k58 : 0, xf, (k62 >> 3) & 1, (k62 >> 10) & 1,
          draw.isNull() ? 0 : (draw.add(0x14).readU32() >> 6) & 1, k62 & 0x1344, mat === null ? '~' + matp : mat, k62 & 0xecbb,
          draw.isNull() ? 0 : draw.add(100).readU32(), k58, draw.isNull() ? 0 : draw.add(0x60).readU32(), i] });
      }
      recs.sort((a, b) => { for (let k = 0; k < a.key.length; k++) if (a.key[k] !== b.key[k]) return a.key[k] < b.key[k] ? -1 : 1; return 0; });
      for (let i = 0; i < n; i++) this.items.add(i * 0x70).writeByteArray(recs[i].bytes);
      count('sort');
      if (recs.some(r => r.key[6][0] === '~')) count('unnamed');
    } });
}
function poll() {
  const cl = Process.findModuleByName('client.dll'), ss = Process.findModuleByName('scenesystem.dll');
  if (cl && !(done & 1)) { done |= 1; hookClient(cl); hookStyles(cl); }
  const ms = Process.findModuleByName('materialsystem2.dll');
  if (ms && !(done & 4)) { done |= 4; if (SORT) hookMaterialNames(ms); }
  // tier0's RandomFloat / RandomInt are replaced only once client.dll is up: replacing them as scenesystem loads,
  // while start-up threads call them, stalled about half the runs at the asset system's start.
  if (ss && cl && !(done & 2)) { done |= 2; if (SORT) hookSort(ss); hookScene(ss); hookFinish(ss); hookTier0(Process.findModuleByName('tier0.dll'), ss); }
  if (done !== 7) setTimeout(poll, 5); else send({ kind: 'hooked' });
}
refreshRanges();
poll();
