// Frida agent for run_probe.py --frida: a scene layer's draw order (CUBEMAPS.md "The draw list").
// Build: scenesystem 20261023 (1800ed4c0, 18010f4e0 and 1801112a0 are at the 10-15 addresses; only rip
// displacements moved). Every probe checks its bytes first.
//
// 1800ed4c0 (layer, items, count) sorts a layer's CMeshDrawPrimitive_t (0x70 bytes each): full sort when the
// layer's +0x48 has bit 0, none with bit 1, else the batch sort. Logs (send, one JSON object per distinct record):
//   sort  the layer's flags (+0x48), and each item before and after the sort as the comparator's keys: the object
//         (+0x18), the material (+0x20, named by 1800ed4c0's debug print), the per-object material (+0x28), +0x48,
//         +0x58, +0x5e, +0x62 and the draw's (+0x08) +0x60 (the debug print's IndexCount).
//   material  each CMaterial2 as it is named (materialsystem2 18000cb40 with a CUtlString, 18000dc00 with a char*;
//         the four creation sites 180033de0, 1800356f0, 18003abf0 and 18003ade0 allocate 0x7e0 bytes from
//         g_pMemAlloc and count a creation serial into +0x3c): address, serial, name, thread, in call order.
// Only function entries and returns are hooked.
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
// An object's vtable as module!rva, so its RTTI names the class (tools/re/vtname.py).
function vtable(o) {
  if (o.isNull())
    return null;
  try {
    const vt = o.readPointer();
    const m = Process.findModuleByAddress(vt);
    return m ? m.name + '!0x' + vt.sub(m.base).add(IMAGE).toString(16) : hex(vt);
  } catch (e) { return null; }
}

function item(p) {
  const draw = p.add(0x08).readPointer();
  return [hex(p.add(0x18).readPointer()), hex(p.add(0x20).readPointer()), hex(p.add(0x28).readPointer()), hex(p.add(0x48).readPointer()),
    p.add(0x58).readS32(), p.add(0x5e).readU16(), p.add(0x62).readU16(), draw.isNull() ? null : draw.add(0x60).readU32(),
    vtable(p.add(0x20).readPointer()), vtable(p.add(0x08).readPointer())];
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

let named = 0;
function hookMaterials(ms) {
  const sites = [['0x18000cb40', '48895424104889', true], ['0x18000dc00', '48895c24104c89', false]];
  for (const [va, bytes, utl] of sites) {
    const p = expect(ms, va, bytes);
    if (!p)
      continue;
    Interceptor.attach(p, {
      onEnter(args) {
        let name = null;
        try { name = utl ? (args[1].isNull() ? null : args[1].readPointer().readCString()) : args[1].readCString(); } catch (e) { name = 'error ' + e; }
        send({ kind: 'material', seq: named++, site: va, address: hex(args[0]), serial: args[0].add(0x3c).readS32(), name, thread: Process.getCurrentThreadId() });
      }
    });
  }
  send({ kind: 'hooked', name: ms.name });
}

const done = {};
function poll() {
  for (const [name, hook] of [['scenesystem.dll', hookScenesystem], ['materialsystem2.dll', hookMaterials]]) {
    const mod = done[name] ? null : Process.findModuleByName(name);
    if (!mod)
      continue;
    done[name] = true;
    send({ kind: 'module', name: mod.name, base: hex(mod.base) });
    hook(mod);
  }
  if (!done['scenesystem.dll'] || !done['materialsystem2.dll'])
    setTimeout(poll, 50);
}
poll();

rpc.exports = { counts() { return { total, distinct: Object.keys(seen).length }; } };
