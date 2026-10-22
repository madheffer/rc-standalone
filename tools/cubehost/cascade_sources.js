// Frida agent for run_probe.py --frida: where a cubemap face's cascade inputs come from (ledger 121).
// Builds: client 20261021, scenesystem 20261015 (the installed ones on 10-22); every probe checks its bytes first.
//
// Logs (send, one JSON object per distinct record):
//   set     client 18016ae20 (view attribute set) for the cascade hashes, with the value and the caller;
//   camera  client 180cb2100's result when 1808898d0 calls it (return address 1808898d9): non-null means 75;
//   enter   the cascade setup (scenesystem 180496ef0 and the client's copy 18173ad60): view +0x38, the light
//           desc's count (+0x3e) and distances (+0x48), the frustum's +0x18/+0x20/+0x24;
//   far     at 1804975e5: ShadowFarZ's value and csm_max_shadow_dist_override's;
//   split   at 1804976e5: the split distances before the attribute pass;
//   d0      at 1804977d0: cascade 0's distance after it (the attribute's value when one is set).
'use strict';

const HASHES = { 0x61fac369: 'cascade0', 0x70644345: 'cascade1', 0x7b2a64e1: 'ShadowFarZ', 0xb506b94b: 'CsmMaxVisibleDistance', 0xb7a3d0d5: 'CsmPushbackDistance', 0x172c3cca: 'fov0x172c3cca' };
const IMAGE = ptr('0x180000000');
const seen = {};
let total = 0;
let hooks = 0;

function report(kind, rec) {
  rec.kind = kind;
  const key = JSON.stringify(rec);
  seen[key] = (seen[key] || 0) + 1;
  if (seen[key] === 1 && total++ < 5000)
    send(rec);
}

function attach(p, cb) { Interceptor.attach(p, cb); hooks++; }

function at(mod, va) { return mod.base.add(ptr(va).sub(IMAGE)); }

function expect(mod, va, hex) {
  const p = at(mod, va);
  const got = Array.from(new Uint8Array(p.readByteArray(hex.length / 2))).map(b => b.toString(16).padStart(2, '0')).join('');
  if (got !== hex) {
    send({ kind: 'mismatch', module: mod.name, va, want: hex, got });
    return null;
  }
  return p;
}

function f32(p) { try { return p.readFloat(); } catch (e) { return null; } }
function floats(p, n) { const r = []; for (let i = 0; i < n; i++) r.push(f32(p.add(i * 4))); return r; }

// A rip-relative qword load (48 8b 05 disp32) at p: the global it reads.
function ripTarget(p) { return p.add(7).add(p.add(3).readS32()); }

function hookClient(cl) {
  const setter = expect(cl, '0x18016ae20', '48896c2410');
  if (setter)
    attach(setter, {
      onEnter(args) {
        const h = args[1].toUInt32() >>> 0;
        if (HASHES[h] === undefined)
          return;
        report('set', { name: HASHES[h], value: f32(args[2]), caller: '0x' + this.returnAddress.sub(cl.base).add(IMAGE).toString(16) });
      }
    });
  const camera = expect(cl, '0x180cb2100', '669090803d');
  const back = at(cl, '0x1808898d9');
  if (camera)
    attach(camera, {
      onEnter() { this.mine = this.returnAddress.equals(back); },
      onLeave(r) { if (this.mine) report('camera', { found: !r.isNull() }); }
    });
  hookSetup(cl, '0x18173ad60');
}

// The cascade setup at fn (scenesystem 180496ef0, or the client's copy): same layout, own rip offsets.
function hookSetup(mod, fn) {
  const d = ptr(fn).sub(ptr('0x180496ef0'));
  const va = x => ptr(x).add(d).toString();
  const entry = expect(mod, va('0x180496ef0'), '4c89442418');
  if (!entry)
    return;
  attach(entry, {
    onEnter(args) {
      const r8 = args[2];
      const light = r8.add(0x10).readPointer(), frustum = r8.add(0x18).readPointer();
      report('enter', {
        module: mod.name, viewCount: r8.add(0x38).readS32(),
        lightCount: light.isNull() ? null : light.add(0x3e).readS8(), lightDistances: light.isNull() ? null : floats(light.add(0x48), 4),
        frustum18: f32(frustum.add(0x18)), near: f32(frustum.add(0x20)), far: f32(frustum.add(0x24))
      });
    }
  });
  const far = expect(mod, va('0x1804975e5'), 'f30f1038488b05');
  if (far) {
    const convar = ripTarget(far.add(4));
    attach(far, {
      onEnter() {
        const cv = convar.readPointer();
        report('far', { module: mod.name, shadowFarZ: f32(this.context.rax), csm_max_shadow_dist_override: cv.isNull() ? null : f32(cv.add(0x58)) });
      }
    });
  }
  const split = expect(mod, va('0x1804976e5'), 'f30f104580');
  if (split)
    attach(split, { onEnter() { report('split', { module: mod.name, d: floats(this.context.rbp.sub(0x80), 4) }); } });
  const d0 = expect(mod, va('0x1804977d0'), '0f10442470');
  if (d0)
    attach(d0, { onEnter() { report('d0', { module: mod.name, d0: f32(this.context.rsp.add(0x70)) }); } });
}

const done = {};
function poll() {
  for (const name of ['client.dll', 'scenesystem.dll']) {
    if (done[name])
      continue;
    const mod = Process.findModuleByName(name);
    if (!mod)
      continue;
    done[name] = true;
    send({ kind: 'module', name, base: mod.base.toString() });
    const before = hooks;
    if (name === 'client.dll') hookClient(mod); else hookSetup(mod, '0x180496ef0');
    send({ kind: 'hooked', name, count: hooks - before });
  }
  if (!done['client.dll'] || !done['scenesystem.dll'])
    setTimeout(poll, 200);
}
poll();

rpc.exports = { counts: () => seen };
