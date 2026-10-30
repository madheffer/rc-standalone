// Frida agent for run_probe.py --frida: the runtime inputs of a cubemap face's time-dependent words (ledger 176),
// logged at their sources so the ports can be checked against what the same run writes.
// Builds: the installed 10-29 DLLs (client / scenesystem / worldrenderer / engine2 = tools/re/binaries/*_20261029.dll;
// ported from the 10-22 addresses with port_addr.py; the comments below keep the 10-22 ones),
// engine2 = engine2_installed_1027.dll); every hook checks its prologue first and reports a mismatch instead.
//
// Records (send, one JSON object each):
//   envwind_init   client C_EnvWindShared init (180b98ce0) / re-init (180b9b340) on leave: the shared block's
//                  networked fields (+0x08 .. +0x2c) and the simulation state (+0x40 .. +0x6b);
//   envwind_sim    the client simulation (180ba0100) on leave: its state (time, speed, direction, vector, targets,
//                  next change, gusting);
//   setwind        worldrenderer SetWind (vf 0x188, 1800282b0): world, vector, speed, time, force (replaced: the
//                  float arguments are only readable through a typed callback);
//   wind_update    worldrenderer's per-frame wind update (vf 0x150, 18002a720): its two float arguments, then every
//                  record after it ran;
//   engine_frame   engine2 180242680: the two doubles it reads from its frame state (+0x28, +0x30);
//   wind_query     worldrenderer's query (vf 0x198, 180028420) on leave: speed / frequencies, offset, direction;
//   attr           client view attribute set (18016ae00) of the wind, gust and game time hashes: the four floats;
//   finish_views   scenesystem FinishRenderingViews (1800fc550): its time argument;
//   perview        scenesystem 1800e8e40 on leave: the block's words 288 .. 300 (tone map, real time, game time);
//   randomfloat    tier0's global RandomFloat (replaced) when scenesystem calls it: bounds, result, caller;
//   buildcubemaps  client 1801ea140 entered.
// Only function entries are hooked (the 10-22 lesson: mid-function hooks crash the engine).
'use strict';

const IMAGE = ptr('0x180000000');
let total = 0;
let hooks = 0;
const counts = {};
const LIMIT = 400000;

function out(kind, rec) {
  counts[kind] = (counts[kind] || 0) + 1;
  if (total++ >= LIMIT)
    return;
  rec.kind = kind;
  rec.n = counts[kind];
  send(rec);
}

function at(mod, va) { return mod.base.add(ptr(va).sub(IMAGE)); }

function expect(mod, va, hex) {
  const p = at(mod, va);
  const got = Array.from(new Uint8Array(p.readByteArray(hex.length / 2))).map(b => b.toString(16).padStart(2, '0')).join('');
  if (got !== hex) {
    send({ kind: 'mismatch', module: mod.name, va, want: hex, got });
    return null;
  }
  hooks++;
  return p;
}

// Floats as their bit patterns too (hex), so a check can compare exactly.
function f32(p) { try { return p.readFloat(); } catch (e) { return null; } }
function u32(p) { try { return p.readU32() >>> 0; } catch (e) { return null; } }
function floats(p, n) { const r = []; for (let i = 0; i < n; i++) r.push(f32(p.add(i * 4))); return r; }
function bits(p, n) { const r = []; for (let i = 0; i < n; i++) r.push(u32(p.add(i * 4))); return r; }
function fbits(x) { const b = new ArrayBuffer(4); new DataView(b).setFloat32(0, x, true); return new DataView(b).getUint32(0, true); }
function va(mod, p) { return '0x' + p.sub(mod.base).add(IMAGE).toString(16); }

function sharedRecord(p) {
  return {
    start: f32(p.add(8)), startBits: u32(p.add(8)), seed: u32(p.add(0xc)),
    minWind: p.add(0x10).readU16(), maxWind: p.add(0x12).readU16(), radius: p.add(0x14).readS32(),
    minGust: p.add(0x18).readU16(), maxGust: p.add(0x1a).readU16(), minGustDelay: f32(p.add(0x1c)), maxGustDelay: f32(p.add(0x20)),
    gustDuration: f32(p.add(0x24)), gustDirChange: p.add(0x28).readU16(), initialDir: p.add(0x2a).readU16(), initialSpeed: f32(p.add(0x2c)),
    state: bits(p.add(0x40), 10), gusting: p.add(0x68).readU8(), stream: bits(p.add(0x6c), 3),
  };
}

function hookClient(cl) {
  for (const [addr, hex, name] of [['0x180b99740', '48895c2408574883', 'init'], ['0x180b9bda0', '48895c2408488974', 'reinit']]) {
    const p = expect(cl, addr, hex);
    if (p)
      Interceptor.attach(p, {
        onEnter(args) { this.shared = args[0]; },
        onLeave() { const r = sharedRecord(this.shared); r.which = name; r.shared = this.shared.toString(); out('envwind_init', r); }
      });
  }
  const sim = expect(cl, '0x180ba0b60', '488bc44889580848');
  if (sim)
    Interceptor.attach(sim, {
      onEnter(args) { this.state = args[0]; },
      onLeave() {
        const s = this.state;
        out('envwind_sim', { state: s.toString(), bits: bits(s, 11), gusting: s.add(0x28).readU8(), floats: floats(s, 10) });
      }
    });
  const HASHES = { 0x0bb9ee08: 'WindDirection', 0xeb0d997e: 'WindStrength', 0xcde54581: 'Wind0xcde54581', 0xc4eabee1: 'GameTime' };
  const set = expect(cl, '0x18016af20', '48896c2410488974');
  if (set)
    Interceptor.attach(set, {
      onEnter(args) {
        const h = args[1].toUInt32() >>> 0;
        if (HASHES[h] === undefined)
          return;
        out('attr', { name: HASHES[h], values: floats(args[2], 4), bits: bits(args[2], 4), block: args[0].toString(), caller: va(cl, this.returnAddress) });
      }
    });
}

function hookBuild(cl) {
  const p = expect(cl, '0x1801ea260', '48894c2408555748');
  if (p)
    Interceptor.attach(p, { onEnter() { out('buildcubemaps', {}); } });
}

function hookWorldRenderer(wr) {
  const setWind = expect(wr, '0x1800282b0', '48895c2408574883');
  if (setWind) {
    const original = new NativeFunction(setWind, 'void', ['pointer', 'pointer', 'pointer', 'float', 'float', 'int'], 'win64');
    Interceptor.replace(setWind, new NativeCallback(function (mgr, world, vec, speed, time, force) {
      out('setwind', { world: world.toString(), vec: floats(vec, 3), vecBits: bits(vec, 3), speed, speedBits: fbits(speed), time, timeBits: fbits(time), force: force & 0xff });
      original(mgr, world, vec, speed, time, force);
    }, 'void', ['pointer', 'pointer', 'pointer', 'float', 'float', 'int'], 'win64'));
  }
  const update = expect(wr, '0x18002a720', 'f30f114c24105541');
  if (update) {
    const original = new NativeFunction(update, 'void', ['pointer', 'float', 'float'], 'win64');
    Interceptor.replace(update, new NativeCallback(function (mgr, step, time) {
      original(mgr, step, time);
      const n = mgr.add(0x200).readS32();
      const base = mgr.add(0x208).readPointer();
      const records = [];
      for (let i = 0; i < n && i < 8; i++)
        records.push({ world: base.add(i * 0x40).readPointer().toString(), bits: bits(base.add(i * 0x40 + 8), 13), force: base.add(i * 0x40 + 0x3c).readU8() });
      out('wind_update', { step, stepBits: fbits(step), time, timeBits: fbits(time), overrides: bits(mgr.add(0x268), 2), records });
    }, 'void', ['pointer', 'float', 'float'], 'win64'));
  }
  const query = expect(wr, '0x180028420', '48895c240848896c');
  if (query)
    Interceptor.attach(query, {
      onEnter(args) { this.a = [args[1], args[2], args[3], args[4]]; },
      onLeave() { out('wind_query', { world: this.a[0].toString(), strength: bits(this.a[1], 3), offset: bits(this.a[2], 3), dir: bits(this.a[3], 3) }); }
    });
}

function hookEngine(e2) {
  const p = expect(e2, '0x1802426c0', '488b0de9194f00f2');
  if (p)
    Interceptor.attach(p, {
      onEnter(args) {
        const f = args[1];
        out('engine_frame', { d28: f.add(0x28).readDouble(), d30: f.add(0x30).readDouble(), d28bits: f.add(0x28).readU64().toString(16), d30bits: f.add(0x30).readU64().toString(16) });
      }
    });
}

function hookSceneSystem(ss) {
  const finish = expect(ss, '0x1800fc550', '488bc44889581048');
  if (finish) {
    const original = new NativeFunction(finish, 'void', ['pointer', 'float'], 'win64');
    Interceptor.replace(finish, new NativeCallback(function (sys, time) {
      out('finish_views', { time, timeBits: fbits(time) });
      original(sys, time);
    }, 'void', ['pointer', 'float'], 'win64'));
  }
  const perview = expect(ss, '0x1800e8e40', '488bc44889582055');
  if (perview)
    Interceptor.attach(perview, {
      onEnter(args) { this.block = args[1]; },
      onLeave() { out('perview', { block: this.block.toString(), words288: bits(this.block.add(288), 4), real: f32(this.block.add(296)), game: f32(this.block.add(300)) }); }
    });
}

function hookTier0(t0, ss) {
  const rf = t0.getExportByName('RandomFloat');
  const original = new NativeFunction(rf, 'float', ['float', 'float'], 'win64');
  const lo = ss.base, hi = ss.base.add(ss.size);
  hooks++;
  Interceptor.replace(rf, new NativeCallback(function (a, b) {
    const v = original(a, b);
    const r = this.returnAddress;
    if (r && r.compare(lo) >= 0 && r.compare(hi) < 0)
      out('randomfloat', { lo: a, hi: b, value: v, valueBits: fbits(v), caller: va(ss, r) });
    return v;
  }, 'float', ['float', 'float'], 'win64'));
}

// Hook each module as it loads (cubehost loads them during engine start).
const pending = { 'client.dll': false, 'worldrenderer.dll': false, 'engine2.dll': false, 'scenesystem.dll': false, 'tier0.dll': false };
function tryHook() {
  const mods = {};
  for (const m of Process.enumerateModules()) mods[m.name.toLowerCase()] = m;
  if (!pending['client.dll'] && mods['client.dll']) { pending['client.dll'] = true; hookClient(mods['client.dll']); hookBuild(mods['client.dll']); }
  if (!pending['worldrenderer.dll'] && mods['worldrenderer.dll']) { pending['worldrenderer.dll'] = true; hookWorldRenderer(mods['worldrenderer.dll']); }
  if (!pending['engine2.dll'] && mods['engine2.dll']) { pending['engine2.dll'] = true; hookEngine(mods['engine2.dll']); }
  if (!pending['scenesystem.dll'] && mods['scenesystem.dll']) { pending['scenesystem.dll'] = true; hookSceneSystem(mods['scenesystem.dll']); }
  if (!pending['tier0.dll'] && mods['tier0.dll'] && mods['scenesystem.dll']) { pending['tier0.dll'] = true; hookTier0(mods['tier0.dll'], mods['scenesystem.dll']); }
  const done = Object.values(pending).every(x => x);
  if (!done)
    setTimeout(tryHook, 50);
  else
    send({ kind: 'hooked', hooks });
}
tryHook();

rpc.exports = { counts() { return { total, hooks, counts }; } };
