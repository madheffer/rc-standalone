// Joints and aggregates in the settle, for capture_settle.py --agent after settle_bundle.js.
//
// 1. aggregates: each vphysics2 aggregate instance the settle creates (world vfn 0x418,
//    FUN_1800614f0): its physics data's joint records (+0x90) and the instance after the
//    build, with its bodies in creation order.
// 2. joints: every joint of the world (RnWorld +0x6a8 and +0x6b8 lists) at the steps the
//    bundle records bodies (0..90, then every 10th) and at the end, 0x200 bytes each.
// RVAs are for vphysics2.dll of 2026-09-24.

const VPJ = {createAggregate: 0x614f0};

function jointsOf(world) {
  const out = [];
  for (const [list, name] of [[0x6a8, 'a'], [0x6b8, 'b']]) {
    const count = world.add(list).readS32();
    const cap = world.add(list + 4).readU32() & 0x7fffffff;
    if (count === 0 || cap === 0) continue;
    const arr = world.add(list + 8).readPointer();
    for (let i = 0; i < count; i++) {
      const j = arr.add(i * 8).readPointer();
      out.push({list: name, i: i, ptr: j.toString(), b1: j.add(0x18).readPointer().toString(),
                b2: j.add(0x20).readPointer().toString(), raw: hexOf(j, 0x200)});
    }
  }
  return out;
}

function hookAggregates(vp) {
  Interceptor.attach(vp.base.add(VPJ.createAggregate), {
    onEnter(a) { this.phys = a[1]; this.xf = a[2]; },
    onLeave(r) {
      const phys = this.phys;
      const records = [];
      const joints = phys.add(0x90).readPointer();
      const n = joints.isNull() ? 0 : joints.readS32();
      for (let i = 0; i < n; i++) records.push(hexOf(joints.add(8 + i * 8).readPointer(), 0xd0));
      const bodies = [];
      if (!r.isNull()) {
        const count = r.add(0x38).readS32(), arr = r.add(0x40).readPointer();
        for (let i = 0; i < count; i++) {
          const w = arr.add(i * 8).readPointer();
          bodies.push(w.isNull() ? null : {wrapper: w.toString(), rn: w.add(8).readPointer().toString(),
                                           body: hexOf(w.add(8).readPointer(), 0x280)});
        }
      }
      send({ev: 'aggregate', phys: phys.toString(), xf: hexOf(this.xf, 0x20), parts: phys.add(0x78).readS32(),
            instance: r.toString(), head: r.isNull() ? '' : hexOf(r, 0xa0), joints: records, bodies: bodies});
    },
  });
}

// 3. islands: the island manager (RnWorld +0x118) at steps 0..3 and every 100th: every node
//    in its all-list (+0x00) with kind, list index and, for a heap island, its bodies (+0x10)
//    and joints (+0x38) in order; and the three solve lists (+0x10, +0x20, +0x30).
function smallVec(p, off) {
  const count = p.add(off).readS32();
  const cap = p.add(off + 4).readU32() & 0x7fffffff;
  if (count === 0 || cap === 0) return [];
  const data = cap > 1 ? p.add(off + 8).readPointer() : p.add(off + 8);
  const out = [];
  for (let i = 0; i < count; i++) out.push(data.add(i * 8).readPointer().toString());
  return out;
}
function islandsOf(world) {
  const m = world.add(0x118);
  const all = smallVec(m, 0x00).map(s => {
    const n = ptr(s), kind = n.add(8).readS32();
    const r = {p: s, kind: kind, list: n.add(4).readS32()};
    if (kind === 1) {
      const bodies = [], count = n.add(0x10).readS32(), arr = n.add(0x18).readPointer();
      for (let i = 0; i < count; i++) bodies.push(arr.add(i * 8).readPointer().toString());
      r.bodies = bodies;
      r.joints = smallVec(n, 0x38);
      r.contacts = [smallVec(n, 0x58).length, smallVec(n, 0x68).length];
      r.iters = [n.add(0x80).readS32(), n.add(0x84).readS32()];
      r.awake = n.add(0x7c).readS32();
      r.colouring = n.add(0xa0).readPointer().toString();
    }
    return r;
  });
  return {all: all, free: smallVec(m, 0x10), serial: smallVec(m, 0x20), coloured: smallVec(m, 0x30)};
}

let jointStep = -1;
const bundleStep = onStep;
onStep = function (world, n) {
  if (bundleStep) bundleStep(world, n);
  jointStep = n;
  if (n <= 90 || n % 10 === 0) send({ev: 'joints', step: n, joints: jointsOf(world)});
  if (n <= 3 || n % 100 === 0) send({ev: 'islands', step: n, islands: islandsOf(world)});
  lastWorld = world;
};
let lastWorld = null;
const bundleEnd = onSettleEnd;
onSettleEnd = function () {
  if (bundleEnd) bundleEnd();
  if (lastWorld) send({ev: 'joints_end', joints: jointsOf(lastWorld)});
};

watch('vphysics2.dll', hookAggregates);
