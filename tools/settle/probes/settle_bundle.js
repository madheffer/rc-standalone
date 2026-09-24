// Everything the entity side needs from one compile, for capture_settle.py --agent.
//
// 1. build:   the settle world as resourcecompiler builds it, call by call:
//             bodies created, typed, placed; hulls and meshes added; user data.
// 2. world:   every body and shape at step 0 with its geometry; the dynamic
//             bodies before each Collide for steps 0..90 and every 10th after;
//             with the world, vphysics2's collision group table and default.
// 3. writeback: each settled object's node before and after the write-back,
//             with its body and bind pose.
// 4. lights:  every LightPrecompute_SampleVolume call, arguments and what they
//             point at, before and after; the compile is held until
//             WRB_PrecomputeLightVisMembership returns.
// RVAs are for resourcecompiler.dll / vphysics2.dll of 2026-09-24.
'use strict';

const RC = {
  physObjBuild: 0x105c330, writeBack: 0x105b350, sampleVolume: 0xf19f20,
  precomputeLights: 0x247d40,
};
const VP = {
  createBody: 0x64190, setType: 0x11210, addHull: 0x114d0, addMesh: 0x11540,
  setTransform: 0x123a0, setUserData: 0x15000, collide: 0x1f6980,
  groupTable: 0x45b328, groupDefault: 0x45b2b8,
};
let vpBase = null;

function hexOf(p, n) {
  if (p.isNull() || n <= 0) return '';
  try {
    const b = new Uint8Array(p.readByteArray(n));
    let s = '';
    for (let i = 0; i < b.length; i++) s += (b[i] < 16 ? '0' : '') + b[i].toString(16);
    return s;
  } catch (e) { return 'unreadable'; }
}
function vecHex(p, off, stride) {
  const n = p.add(off).readS32();
  return n > 0 ? hexOf(p.add(off + 8).readPointer(), n * stride) : '';
}
function inlineVec(p, off) {
  const count = p.add(off).readS32();
  const cap = p.add(off + 4).readU32() & 0x7fffffff;
  if (cap === 0 || count === 0) return [];
  const data = cap > 1 ? p.add(off + 8).readPointer() : p.add(off + 8);
  const out = [];
  for (let i = 0; i < count; i++) out.push(data.add(i * 8).readPointer());
  return out;
}

// ---- 1. build log --------------------------------------------------------------
let building = false;
function log(kind, fields) { send(Object.assign({ev: 'build', kind: kind}, fields)); }
function hookBuild(vp) {
  const on = (rva, kind, enter, leave) => Interceptor.attach(vp.base.add(rva), {
    onEnter(a) { if (building) { this.args = [a[0], a[1], a[2], a[3]]; if (enter) enter.call(this, a); } },
    onLeave(r) { if (building && leave) leave.call(this, r); },
  });
  on(VP.createBody, 'body', null, function (r) {
    log('body', {wrapper: r.toString(), rn: r.isNull() ? null : r.add(8).readPointer().toString()});
  });
  on(VP.setType, 'type', function (a) { log('type', {wrapper: a[0].toString(), type: a[1].toInt32()}); });
  on(VP.setTransform, 'xf', function (a) { log('xf', {wrapper: a[0].toString(), xf: hexOf(a[1], 0x20)}); });
  on(VP.setUserData, 'user', function (a) { log('user', {wrapper: a[0].toString(), part: a[1].toString()}); });
  on(VP.addHull, 'hull', function (a) { this.w = a[0]; this.h = a[1]; this.x = a[2]; }, function (r) {
    log('hull', {wrapper: this.w.toString(), arg1: this.h.toString(), arg2: this.x.toString(),
                 shape: r.isNull() ? null : r.add(8).readPointer().toString()});
  });
  on(VP.addMesh, 'mesh', function (a) { this.w = a[0]; this.m = a[1]; this.s = a[2]; }, function (r) {
    log('mesh', {wrapper: this.w.toString(), arg1: this.m.toString(), scale: hexOf(this.s, 12),
                 shape: r.isNull() ? null : r.add(8).readPointer().toString()});
  });
}
function hookObjects(rc) {
  Interceptor.attach(rc.base.add(RC.physObjBuild), {
    onEnter(a) {
      building = true;
      const obj = a[0], node = obj.add(0x40).readPointer();
      this.obj = obj;
      send({ev: 'build', kind: 'object', obj: obj.toString(),
            node: node.isNull() ? null : node.add(0x300).readU32()});
    },
  });
}

// ---- 2. world and per-step bodies ------------------------------------------------
let lastStep = -1, dumped = false, dynamic = null;
const shapeId = new Map();
function dumpWorld(world) {
  building = false;
  const count = world.add(0x678).readS32(), arr = world.add(0x680).readPointer();
  const bodies = [];
  for (let i = 0; i < count; i++) {
    const b = arr.add(i * 8).readPointer();
    const shapes = inlineVec(b, 0x60).map(s => {
      const id = shapeId.size;
      shapeId.set(s.toString(), id);
      const type = s.add(0x18).readS32();
      const r = {id: id, ptr: s.toString(), type: type, head: hexOf(s, 0x110)};
      if (type === 2) {
        const h = s.add(0xc0).readPointer();
        r.hull = hexOf(h, 0xf8); r.pos = vecHex(h, 0x70, 12); r.planes = vecHex(h, 0x88, 16);
        r.verts = vecHex(h, 0xb0, 1); r.edges = vecHex(h, 0xc8, 4); r.faces = vecHex(h, 0xe0, 1);
      } else if (type === 3) {
        const m = s.add(0xc8).readPointer();
        r.mesh = hexOf(m, 0xb0); r.nodes = vecHex(m, 0x18, 32); r.mverts = vecHex(m, 0x30, 12);
        r.tris = vecHex(m, 0x48, 12); r.mats = vecHex(m, 0x90, 1);
      } else if (type === 1 || type === 0) {
        r.geom = hexOf(s.add(0xb8), 0x20);
      }
      return r;
    });
    bodies.push({ptr: b.toString(), body: hexOf(b, 0x280), shapes: shapes});
  }
  send({ev: 'world', bodies: bodies, head: hexOf(world, 0xc00),
        groups: vpBase ? hexOf(vpBase.add(VP.groupTable), 4096 * 2) : '',
        groupDefault: vpBase ? vpBase.add(VP.groupDefault).readU16() : -1});
}
onStep = function (world, n) {
  lastStep = n;
  if (!dumped) { dumped = true; dumpWorld(world); }
};
function hookCollide(vp) {
  Interceptor.attach(vp.base.add(VP.collide), {
    onEnter(a) {
      if (lastStep < 0 || (lastStep > 90 && lastStep % 10 !== 0)) return;
      const world = a[0];
      if (!dynamic) {
        dynamic = [];
        const n = world.add(0x678).readS32(), arr = world.add(0x680).readPointer();
        for (let i = 0; i < n; i++) {
          const b = arr.add(i * 8).readPointer();
          if (b.add(0x54).readS32() === 2) dynamic.push([i, b]);
        }
      }
      send({ev: 'bodies', step: lastStep, bodies: dynamic.map(([i, b]) => [i, hexOf(b, 0x280)])});
    },
  });
}
onSettleEnd = function () {
  if (dynamic) send({ev: 'bodies_end', bodies: dynamic.map(([i, b]) => [i, hexOf(b, 0x280)])});
};

// ---- 3. write-back ---------------------------------------------------------------
function hookWriteBack(rc) {
  Interceptor.attach(rc.base.add(RC.writeBack), {
    onEnter(a) {
      const obj = a[0], node = obj.add(0x40).readPointer();
      this.node = node;
      const parts = obj.add(0x18).readPointer(), nparts = obj.add(0x10).readS32();
      const part = nparts > 0 ? parts.readPointer() : ptr(0);
      const wrapper = part.isNull() ? ptr(0) : part.add(0x28).readPointer();
      const phys = part.isNull() ? ptr(0) : part.add(0x78).readPointer();
      this.rec = {ev: 'writeback', node: node.isNull() ? null : node.add(0x300).readU32(),
                  before: node.isNull() ? '' : hexOf(node.add(0xa0), 0x18), parts: nparts,
                  part: hexOf(part, 0xb0), rn: wrapper.isNull() ? '' : hexOf(wrapper.add(8).readPointer(), 0x280),
                  bindCount: phys.isNull() ? 0 : phys.add(0x18).readS32(),
                  bind: phys.isNull() || phys.add(0x18).readS32() < 1 ? '' : hexOf(phys.add(0x60).readPointer(), 0x30)};
    },
    onLeave() {
      this.rec.after = this.node.isNull() ? '' : hexOf(this.node.add(0xa0), 0x18);
      send(this.rec);
    },
  });
}

// ---- 4. light precompute ---------------------------------------------------------
hold();
function hookLights(rc) {
  Interceptor.attach(rc.base.add(RC.sampleVolume), {
    onEnter(a) {
      this.a = [];
      for (let i = 0; i < 4; i++) this.a.push(a[i]);
      const sp = this.context.rsp;
      for (let i = 4; i < 8; i++) this.a.push(sp.add(8 * (i + 1)).readPointer());
      this.before = this.a.map(p => hexOf(p, 0x100));
    },
    onLeave(r) {
      send({ev: 'light', args: this.a.map(p => p.toString()), before: this.before,
            after: this.a.map(p => hexOf(p, 0x100)), ret: r.toString()});
    },
  });
  Interceptor.attach(rc.base.add(RC.precomputeLights), {
    onEnter() { send({ev: 'lights_start'}); },
    onLeave() { send({ev: 'lights_done'}); release(); },
  });
}

watch('vphysics2.dll', vp => { vpBase = vp.base; hookBuild(vp); hookCollide(vp); });
watch('resourcecompiler.dll', rc => { hookObjects(rc); hookWriteBack(rc); hookLights(rc); });
