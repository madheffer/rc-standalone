// Frida agent for run_probe.py --frida: which layers an aggregate's fragments join (CUBEMAPS.md "The draw list").
// Build: scenesystem 20261023 (180041f30 and 180041a00 are at the 10-15 addresses; only rip displacements moved).
//
// 180041f30 (_, object, context, out) tests the object's material against the layer (context +0x10: +0x48 flags,
// +0x4a0 .. +0x4ac masks, the material's +0x1c with the object's +0xe0) and then calls 180041a00 (_, object,
// context), which keeps each visible fragment (context +0x30: +4 count, +0x28 16-byte entries, +0x30 indices)
// whose draw record (object +0x168, 0x50 bytes each) +0x14 flags hold the layer's required bits and none of its
// excluded ones (context +0x18: +0x480, +0x488, +0x498; +0x6d0 the prepass split). Logs (send, distinct records):
//   object  once per object: its fragments' draw records (count +0x160; +0x14 flags, +0x16, +0x1c), the material's
//           +0x1c and +0x14, the object's +0xe0
//   attributes  once per object: its draw call's CMaterial2 attribute store and the parent stores it falls back to
//   test    per 180041f30 call: both layers' fields, the context's +0x3c and +0x65, whether 180041a00 ran, the
//           visible fragments (index, entry +0xc) and those it kept
// Only function entries and returns are hooked.
'use strict';

const IMAGE = ptr('0x180000000');
const seen = {};
let total = 0;

function report(kind, rec) {
  rec.kind = kind;
  const key = JSON.stringify(rec);
  seen[key] = (seen[key] || 0) + 1;
  if (seen[key] === 1 && total++ < 300000)
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
function u64(p) { return '0x' + p.readU64().toString(16); }
function name(p) {
  try { const s = p.readCString(); if (s && s.length > 1) return s; } catch (e) { }
  try { return p.readPointer().readCString(); } catch (e) { return null; }
}

// The layer's view name, as 1800ed4c0's debug print gets it: the first virtual of the object at +0x6f0.
const viewNames = new Map();
function viewName(l) {
  const v = l.add(0x6f0).readPointer();
  if (v.isNull())
    return null;
  const k = v.toString();
  if (!viewNames.has(k)) {
    let n = null;
    try { n = new NativeFunction(v.readPointer().readPointer(), 'pointer', ['pointer'])(v).readCString(); } catch (e) { n = 'error ' + e; }
    viewNames.set(k, n);
  }
  return viewNames.get(k);
}

function layerFields(l) {
  if (l.isNull())
    return null;
  return {
    at: hex(l), name: name(l.add(0x4b8)), viewName: viewName(l), viewObject: hex(l.add(0x6f0).readPointer()), f48: u64(l.add(0x48)), f480: u64(l.add(0x480)), f488: u64(l.add(0x488)), f498: u64(l.add(0x498)),
    f4a0: l.add(0x4a0).readU32(), f4a4: l.add(0x4a4).readU32(), f4a8: l.add(0x4a8).readU32(), f4ac: l.add(0x4ac).readU32(),
    split: l.add(0x6d0).readFloat(), f780: hex(l.add(0x780).readPointer()), fc18: l.add(0xc18).readU8(),
  };
}

// A CMaterial2 attribute store as materialsystem2 18000ba80 (vf 0x48) walks it: 12 keys at +0x00 and their
// values at +0x30, overflow blocks from +0x68 (next +0x00, keys +0x10, values +0x40), the parent store at +0x60.
function store(c) {
  const entries = [];
  const take = (keys, values) => {
    for (let i = 0; i < 12; i++) {
      const k = keys.add(i * 4).readU32();
      if (k !== 0)
        entries.push(['0x' + k.toString(16), values.add(i * 4).readU32()]);
    }
  };
  take(c, c.add(0x30));
  for (let b = c.add(0x68).readPointer(), n = 0; !b.isNull() && n < 4096; b = b.readPointer(), n++)
    take(b.add(0x10), b.add(0x40));
  return entries;
}

const stores = new Set();
function dumpMaterial(material) {
  const chain = [];
  for (let c = material.add(0x180), n = 0; !c.isNull() && n < 16; c = c.add(0x60).readPointer(), n++) {
    const shared = n > 0;
    if (shared && stores.has(c.toString())) {
      chain.push({ at: hex(c), seen: true });
      break;
    }
    if (shared)
      stores.add(c.toString());
    chain.push({ at: hex(c), entries: store(c) });
  }
  return chain;
}

const objects = new Set();
function describeObject(o) {
  const k = o.toString();
  if (objects.has(k))
    return;
  objects.add(k);
  const n = o.add(0x160).readS32();
  const records = o.add(0x168).readPointer();
  const frags = [];
  for (let i = 0; i < n && i < 4096; i++) {
    const r = records.add(i * 0x50);
    frags.push([r.add(0x14).readU16(), r.add(0x16).readU16(), r.add(0x1c).readU32()]);
  }
  let matFlags = null;
  try {
    const box = o.add(0x110).readPointer();
    const mat = (box.add(0x1c).readU32() & 0x7fffffff) === 0 ? ptr(0) : box.add(0x20).readPointer();
    matFlags = mat.isNull() ? null : [mat.add(0x1c).readU32(), mat.add(0x14).readU16()];
    // The draw call's material: +0x118 is a handle whose first pointer is the CMaterial2 (meshsystem 180027140).
    if (!mat.isNull()) {
      const handle = mat.add(0x118).readPointer();
      const material = handle.isNull() ? ptr(0) : handle.readPointer();
      if (!material.isNull())
        report('attributes', { object: hex(o), material: hex(material), chain: dumpMaterial(material) });
    }
  } catch (e) { matFlags = 'error ' + e; }
  report('object', { object: hex(o), fragments: frags, material1c14: matFlags, objectE0: o.add(0xe0).readU32() });
}

const state = {};
function hookScenesystem(ss) {
  const test = expect(ss, '0x180041f30', '48896c2420');
  const keep = expect(ss, '0x180041a00', '4889542410');
  if (!test || !keep)
    return;
  Interceptor.attach(test, {
    onEnter(args) {
      const tid = this.threadId;
      state[tid] = { object: args[1], context: args[2], ran: false, kept: null, visible: null };
      try { describeObject(args[1]); } catch (e) { send({ kind: 'error', where: 'object', message: '' + e }); }
    },
    onLeave() {
      const s = state[this.threadId];
      if (!s)
        return;
      delete state[this.threadId];
      try {
        report('test', {
          object: hex(s.object), layer: layerFields(s.context.add(0x10).readPointer()), view: layerFields(s.context.add(0x18).readPointer()),
          c3c: s.context.add(0x3c).readFloat(), c65: s.context.add(0x65).readU8(),
          ran: s.ran, visible: s.visible, kept: s.kept,
        });
      } catch (e) { send({ kind: 'error', where: 'test', message: '' + e }); }
    }
  });
  Interceptor.attach(keep, {
    onEnter(args) {
      const s = state[this.threadId];
      if (!s)
        return;
      s.ran = true;
      const list = args[2].add(0x30).readPointer();
      const n = list.add(4).readU32();
      const idx = list.add(0x30).readPointer();
      const entries = list.add(0x28).readPointer();
      const visible = [];
      for (let i = 0; i < n && i < 4096; i++) {
        const f = idx.add(i * 2).readU16();
        visible.push([f, entries.add(f * 0x10 + 0xc).readU8()]);
      }
      s.visible = visible;
    },
    onLeave(ret) {
      const s = state[this.threadId];
      if (!s)
        return;
      if (ret.isNull()) {
        s.kept = [];
        return;
      }
      const n = ret.add(0x24).readU32();
      const idx = ret.add(0x10).readPointer();
      const kept = [];
      for (let i = 0; i < n && i < 4096; i++)
        kept.push(idx.add(i * 2).readU16());
      s.kept = kept;
    }
  });
  send({ kind: 'hooked', name: ss.name });
}

// worldrenderer 20261023 18001ed7e calls vf 0x120 of its material system (global 180231bf0) on the draw call's
// material; its result & 0x180 decides the fragment's flag 8. Logs which function that slot is (once set).
function slot(mod, global, offset) {
  const iface = at(mod, global).readPointer();
  if (iface.isNull())
    return null;
  const fn = iface.readPointer().add(offset).readPointer();
  const m = Process.findModuleByAddress(fn);
  return { iface: hex(iface), fn: m ? m.name + '!0x' + fn.sub(m.base).add(IMAGE).toString(16) : hex(fn) };
}

// worldrenderer 18001f1be calls the vis object's vf 8 for a fragment's cluster members (no precomputed list): a
// one-shot hardware execute breakpoint there names that function (rcx's vtable slot 8), then is cleared.
let visBp = null;
function armVis(wr) {
  visBp = at(wr, '0x18001f1be');
  for (const t of Process.enumerateThreads())
    try { t.setHardwareBreakpoint(0, visBp); } catch (e) { }
}
Process.setExceptionHandler(details => {
  if (visBp === null || !details.address.equals(visBp))
    return false;
  const ctx = details.context;
  let rec = {};
  try {
    const fn = ctx.rcx.readPointer().add(8).readPointer();
    const m = Process.findModuleByAddress(fn);
    const vt = ctx.rcx.readPointer();
    const mv = Process.findModuleByAddress(vt);
    rec = { fn: m ? m.name + '!0x' + fn.sub(m.base).add(IMAGE).toString(16) : hex(fn),
      vtable: mv ? mv.name + '!0x' + vt.sub(mv.base).add(IMAGE).toString(16) : hex(vt) };
  } catch (e) { rec = { error: '' + e }; }
  for (const t of Process.enumerateThreads())
    try { t.unsetHardwareBreakpoint(0); } catch (e) { }
  visBp = null;
  send({ kind: 'visquery', ...rec });
  return true;
});

let done = false;
let wrDone = false;
function poll() {
  const mod = Process.findModuleByName('scenesystem.dll');
  if (mod && !done) {
    done = true;
    send({ kind: 'module', name: mod.name, base: hex(mod.base) });
    hookScenesystem(mod);
  }
  const wr = Process.findModuleByName('worldrenderer.dll');
  if (wr && !wrDone) {
    const s = slot(wr, '0x180231bf0', 0x120);
    if (s) {
      wrDone = true;
      armVis(wr);
      // 180027160 ends by running the meshsystem object's flag callbacks (+0xa8 count, +0xb0 array).
      const iface = at(wr, '0x180231bf0').readPointer();
      const callbacks = [];
      for (let i = 0; i < iface.add(0xa8).readS32() && i < 64; i++) {
        const fn = iface.add(0xb0).readPointer().add(i * 8).readPointer();
        const m = Process.findModuleByAddress(fn);
        callbacks.push(m ? m.name + '!0x' + fn.sub(m.base).add(IMAGE).toString(16) : hex(fn));
      }
      send({ kind: 'slot', what: 'worldrenderer 180231bf0 vf 0x120', ...s, vfd0: slot(wr, '0x180231bf0', 0xd0), callbacks });
    }
  }
  if (!done || !wrDone)
    setTimeout(poll, 50);
}
poll();

rpc.exports = { counts() { return { total, distinct: Object.keys(seen).length }; } };
