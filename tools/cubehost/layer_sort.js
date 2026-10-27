// Frida agent for run_probe.py --frida: a scene layer's draw order (CUBEMAPS.md "The draw list").
// Build: scenesystem 20261023 (1800ed4c0, 18010f4e0 and 1801112a0 are at the 10-15 addresses; only rip
// displacements moved). Every probe checks its bytes first.
//
// 1800ed4c0 (layer, items, count) sorts a layer's CMeshDrawPrimitive_t (0x70 bytes each): full sort when the
// layer's +0x48 has bit 0, none with bit 1, else the batch sort. Logs (send, one JSON object per distinct record):
//   sort  the layer's flags (+0x48), and each item before and after the sort as the comparator's keys: the object
//         (+0x18), the material (+0x20, named by 1800ed4c0's debug print), the per-object material (+0x28), +0x48,
//         +0x58, +0x5e, +0x62 and the draw's (+0x08) +0x60 (the debug print's IndexCount).
// Only the function's entry and return are hooked.
'use strict';

const IMAGE = ptr('0x180000000');
const seen = {};
let total = 0;
let hooks = 0;

function report(kind, rec) {
  rec.kind = kind;
  const key = JSON.stringify(rec);
  seen[key] = (seen[key] || 0) + 1;
  if (seen[key] === 1 && total++ < 200000)
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
  return p;
}

function hex(p) { return '0x' + p.toString(16); }
function item(p) {
  const draw = p.add(0x08).readPointer();
  return [hex(p.add(0x18).readPointer()), hex(p.add(0x20).readPointer()), hex(p.add(0x28).readPointer()), hex(p.add(0x48).readPointer()),
    p.add(0x58).readS32(), p.add(0x5e).readU16(), p.add(0x62).readU16(), draw.isNull() ? null : draw.add(0x60).readU32()];
}

function items(base, n) { const r = []; for (let i = 0; i < n; i++) r.push(item(base.add(i * 0x70))); return r; }

function hookScenesystem(ss) {
  const sort = expect(ss, '0x1800ed4c0', '4585c9');
  if (!sort)
    return;
  Interceptor.attach(sort, {
    onEnter(args) {
      this.n = args[3].toUInt32();
      if (this.n === 0)
        return;
      this.layer = args[1];
      this.items = args[2];
      try { this.before = items(this.items, this.n); } catch (e) { this.before = 'error ' + e; }
    },
    onLeave() {
      if (!this.n)
        return;
      let after;
      try { after = items(this.items, this.n); } catch (e) { after = 'error ' + e; }
      report('sort', { flags: hex(this.layer.add(0x48).readU64()), before: this.before, after });
    }
  });
  hooks++;
  send({ kind: 'hooked', name: ss.name, count: hooks });
}

let done = false;
function poll() {
  const mod = Process.findModuleByName('scenesystem.dll');
  if (mod && !done) {
    done = true;
    send({ kind: 'module', name: mod.name, base: hex(mod.base) });
    hookScenesystem(mod);
  }
  if (!done)
    setTimeout(poll, 200);
}
poll();

rpc.exports = { counts() { return { total, distinct: Object.keys(seen).length }; } };
