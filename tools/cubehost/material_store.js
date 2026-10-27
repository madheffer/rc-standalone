// Frida agent for run_probe.py --frida: which call puts an attribute into a CMaterial2's store (GROUND_TRUTH 157).
// Build: materialsystem2 1.41.9.2 (installed 10-27; the functions at the 10-15 addresses). Every probe checks its
// bytes first.
//
// The store is the one 18000ba80 (vf 0x48) walks: 12 keys at material +0x180, values at +0x1b0, overflow blocks
// from +0x1e8 (next +0x00, keys +0x10, values +0x40). Logs (send):
//   name   18000cb40 / 18000dc00: a material and its name
//   step   on entry and return of each hooked function: the watched keys the material's store holds then
//   write  each write to reflectivity_30's first eight store keys after its load (hardware watchpoints)
// Hooked: 18000fd00 (LoadShadersAndSetupModes) and functions given in STEPS; only entries and returns.
'use strict';

const IMAGE = ptr('0x180000000');
const WATCH = { 0xe95a490f: 'BindlessResources', 0x3ed9fd4a: 'AllowBackfaceCulling', 0x2c5b5105: 'DepthPassBatchID', 0xd1c3c3b0: 'CanBatchWithDynamicShaderConstants' };
// [address, entry bytes, argument index of the material]
const STEPS = [['0x18000fd00', null, 0], ['0x180010700', null, 0]];
let sent = 0;
const TARGET = 'materials/dev/reflectivity_30.vmat';
const names = {};

function at(mod, va) { return mod.base.add(ptr(va).sub(IMAGE)); }
function hex(p) { return '0x' + p.toString(16); }

function held(material) {
  const out = {};
  const take = (keys, values) => {
    for (let i = 0; i < 12; i++) {
      const k = keys.add(i * 4).readU32() >>> 0;
      if (WATCH[k] !== undefined)
        out[WATCH[k]] = values.add(i * 4).readU32();
    }
  };
  const c = material.add(0x180);
  take(c, c.add(0x30));
  for (let b = c.add(0x68).readPointer(), n = 0; !b.isNull() && n < 4096; b = b.readPointer(), n++)
    take(b.add(0x10), b.add(0x40));
  return out;
}

// Hardware write watchpoints on the first eight key slots of one material's store (four 8-byte slots), armed
// on every thread when its load returns: each write's instruction, key and backtrace.
let watched = null;
function where(a) {
  const m = Process.findModuleByAddress(a);
  return m ? m.name + '!0x' + a.sub(m.base).add(IMAGE).toString(16) : hex(a);
}
function arm(material) {
  watched = material;
  for (const t of Process.enumerateThreads())
    for (let i = 0; i < 4; i++)
      try { t.setHardwareWatchpoint(i, material.add(0x180 + i * 8), 8, 'w'); } catch (e) { }
  send({ kind: 'armed', material: hex(material) });
}
Process.setExceptionHandler(details => {
  if (watched === null || (details.type !== 'single-step' && details.type !== 'breakpoint'))
    return false;
  const keys = [];
  for (let i = 0; i < 8; i++)
    keys.push('0x' + (watched.add(0x180 + i * 4).readU32() >>> 0).toString(16));
  let bt = [];
  try { bt = Thread.backtrace(details.context, Backtracer.ACCURATE).slice(0, 12).map(where); } catch (e) { bt = ['error ' + e]; }
  if (sent++ < 20000)
    send({ kind: 'write', pc: where(details.address), keys, backtrace: bt });
  return true;
});

function hook(ms) {
  for (const [va, , arg] of STEPS) {
    Interceptor.attach(at(ms, va), {
      onEnter(args) { this.m = args[arg]; try { this.before = held(this.m); } catch (e) { this.before = 'error ' + e; } },
      onLeave() {
        if (watched === null && va === '0x18000fd00' && names[this.m.toString()] === TARGET)
          arm(this.m);
        let after;
        try { after = held(this.m); } catch (e) { after = 'error ' + e; }
        if (sent++ < 20000)
          send({ kind: 'step', fn: va, material: hex(this.m), before: this.before, after });
      }
    });
  }
  for (const [va, utl] of [['0x18000cb40', true], ['0x18000dc00', false]])
    Interceptor.attach(at(ms, va), {
      onEnter(args) {
        let name = null;
        try { name = utl ? args[1].readPointer().readCString() : args[1].readCString(); } catch (e) { }
        names[args[0].toString()] = name;
        if (sent++ < 20000)
          send({ kind: 'name', material: hex(args[0]), name });
      }
    });
  send({ kind: 'hooked', name: ms.name });
}

let done = false;
function poll() {
  const mod = Process.findModuleByName('materialsystem2.dll');
  if (mod && !done) {
    done = true;
    hook(mod);
  }
  if (!done)
    setTimeout(poll, 20);
}
poll();
