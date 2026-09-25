// Why a mesh gets no shape, for capture_settle.py --agent: every mesh the
// settle's PhysObj_Build hands to FUN_18105d760, from the flags it passes down
// to the pieces FUN_181057020 returns. Captures all meshes, so the trigger
// meshes (atixref 351, 6181; Mako's 82) come with the kept ones to compare.
//
//   tm_obj     PhysObj_Build enter: node id, flags at +0 (the param_2 below)
//              and at +0xa0 (d760's param_4; bit 2 builds the shapes).
//   tm_d760    FUN_18105d760 enter: its param_4 and param_5.
//   tm_solid   FUN_1802e3120 leave inside FUN_1810564a0: the material's
//              first byte (0 skips the record) and its four strings.
//   tm_attr    FUN_1802e2f20 leave inside FUN_1810564a0: each material
//              attribute FUN_1802e3120 asks for (name hash) and the answer,
//              for the "translucent" rule (Mako's glass comes out as window).
//   tm_record  FUN_1810564a0 leave: flags, param_4..8 strings, the 0x60
//              record (group at +0x2e) and its name.
//   tm_records FUN_1810562e0 leave: every 0x60 record it made, per material.
//   tm_soups   FUN_18105b600 enter inside FUN_181057020: each 0x90 soup's
//              first 0x60 bytes, vertex count (+0x60), index count (+0x78).
//   tm_pieces  FUN_181057020 leave: node id, flags, each 0x70 piece.
// RVAs are for resourcecompiler.dll of 2026-09-24.
'use strict';

const TM = {
  physObjBuild: 0x105c330, d760: 0x105d760, pieces: 0x1057020, records: 0x10562e0,
  record: 0x10564a0, matPhys: 0x2e3120, matAttr: 0x2e2f20, soupsFree: 0x105b600,
};
const tmState = new Map();
function tmOf() {
  const id = Process.getCurrentThreadId();
  let s = tmState.get(id);
  if (!s) { s = {node: null, inPieces: 0, inRecord: 0}; tmState.set(id, s); }
  return s;
}
function tmHex(p, n) {
  if (p.isNull() || n <= 0) return '';
  try {
    const b = new Uint8Array(p.readByteArray(n));
    let s = '';
    for (let i = 0; i < b.length; i++) s += (b[i] < 16 ? '0' : '') + b[i].toString(16);
    return s;
  } catch (e) { return 'unreadable'; }
}
function tmStr(p) {
  try { return p.isNull() ? null : p.readUtf8String(); } catch (e) { return 'unreadable'; }
}
function tmUtl(p) { return tmStr(p.readPointer()); }
function tmNode(p) {
  try { return p.isNull() ? null : p.add(0x300).readU32(); } catch (e) { return null; }
}
function tmSend(kind, fields) { send(Object.assign({ev: 'tm', kind: kind, node: tmOf().node}, fields)); }

function hookTriggerMeshes(rc) {
  Interceptor.attach(rc.base.add(TM.physObjBuild), {
    onEnter(a) {
      const obj = a[0], s = tmOf();
      this.prev = s.node;
      s.node = tmNode(obj.add(0x40).readPointer());
      tmSend('tm_obj', {flags: obj.readU32(), flagsA0: obj.add(0xa0).readU32()});
    },
    onLeave() { tmOf().node = this.prev; },
  });
  Interceptor.attach(rc.base.add(TM.d760), {
    onEnter(a) {
      tmSend('tm_d760', {mesh: tmNode(a[1]), param4: a[3].toUInt32(), param5: a[4].toUInt32()});
    },
  });
  Interceptor.attach(rc.base.add(TM.records), {
    onEnter(a) { this.vec = a[2]; this.mesh = tmNode(a[0]); this.flags = a[1].toUInt32(); },
    onLeave() {
      const n = this.vec.readS32(), data = n > 0 ? this.vec.add(8).readPointer() : ptr(0), recs = [];
      for (let i = 0; i < n; i++) {
        const r = data.add(i * 0x60);
        recs.push({name: tmUtl(r), rec: tmHex(r, 0x60)});
      }
      tmSend('tm_records', {mesh: this.mesh, flags: this.flags, records: recs});
    },
  });
  Interceptor.attach(rc.base.add(TM.record), {
    onEnter(a) {
      tmOf().inRecord++;
      const sp = this.context.rsp;
      this.mesh = tmNode(a[0]);
      this.flags = a[1].toUInt32();
      this.surface = tmStr(a[3]);
      // param_5..param_9 are on the stack, above the 0x20 home area.
      this.strs = [0, 1, 2, 3].map(i => tmStr(sp.add(0x28 + 8 * i).readPointer()));
      this.rec = sp.add(0x48).readPointer();
    },
    onLeave() {
      tmOf().inRecord--;
      tmSend('tm_record', {mesh: this.mesh, flags: this.flags, surface: this.surface,
                           group: this.strs[0], layers: this.strs.slice(1),
                           name: tmUtl(this.rec), rec: tmHex(this.rec, 0x60)});
    },
  });
  Interceptor.attach(rc.base.add(TM.matPhys), {
    onEnter(a) { this.out = a[0]; },
    onLeave() {
      if (tmOf().inRecord <= 0) return;
      const o = this.out;
      tmSend('tm_solid', {first: o.readU8(),
                          strs: [8, 0x10, 0x18, 0x20].map(off => tmUtl(o.add(off)))});
    },
  });
  Interceptor.attach(rc.base.add(TM.matAttr), {
    onEnter(a) { this.hash = a[1].toUInt32(); this.dflt = a[2].toUInt32() & 0xff; },
    onLeave(r) {
      if (tmOf().inRecord <= 0) return;
      tmSend('tm_attr', {hash: this.hash, dflt: this.dflt, ret: r.toUInt32() & 0xff});
    },
  });
  Interceptor.attach(rc.base.add(TM.pieces), {
    onEnter(a) {
      tmOf().inPieces++;
      this.mesh = tmNode(a[0]);
      this.flags = a[1].toUInt32();
      this.out = a[5];
    },
    onLeave() {
      tmOf().inPieces--;
      const n = this.out.readS32(), data = n > 0 ? this.out.add(8).readPointer() : ptr(0), pieces = [];
      for (let i = 0; i < n; i++) {
        const p = data.add(i * 0x70);
        pieces.push({name: tmUtl(p), piece: tmHex(p, 0x70)});
      }
      tmSend('tm_pieces', {mesh: this.mesh, flags: this.flags, pieces: pieces});
    },
  });
  Interceptor.attach(rc.base.add(TM.soupsFree), {
    onEnter(a) {
      if (tmOf().inPieces <= 0) return;
      const n = a[0].readS32(), data = n > 0 ? a[0].add(8).readPointer() : ptr(0), soups = [];
      for (let i = 0; i < n; i++) {
        const p = data.add(i * 0x90);
        soups.push({name: tmUtl(p), head: tmHex(p, 0x60),
                    verts: p.add(0x60).readS32(), indices: p.add(0x78).readS32()});
      }
      tmSend('tm_soups', {soups: soups});
    },
  });
}

watch('resourcecompiler.dll', hookTriggerMeshes);
