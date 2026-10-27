// The model pose resourcecompiler poses aggregate bodies from, for capture_settle.py --agent after
// settle_bundle.js.
//
// 1. posenode: each call of FUN_181f0bb60 (the node's pose build): the node, the object at node
//    +0x2b0 (the animation graph), its vtable and vfn 0x90 as module offsets.
// 2. graphpose: each call of FUN_1817dd320 (the graph's default pose to model space): the pose
//    object's head, its skeleton (pose +0 -> +0), the bone count (pose +0x1c), every bone's name
//    (FUN_181750790) and parent (FUN_1817507d0), the local transforms it composes (pose +8, 0x20
//    each), the root transform, the model-space output, and the caller chain.
// RVAs are for resourcecompiler.dll of 2026-09-23.

const RC_POSE = {nodePose: 0x1f0bb60, graphPose: 0x17dd320, boneName: 0x1750790, boneParent: 0x17507d0};

function rcOffset(p) {
  const m = Process.findModuleByAddress(p);
  return m ? m.name + '+0x' + p.sub(m.base).toString(16) : p.toString();
}

function hookPose(rc) {
  const boneName = new NativeFunction(rc.base.add(RC_POSE.boneName), 'pointer', ['pointer', 'int']);
  const boneParent = new NativeFunction(rc.base.add(RC_POSE.boneParent), 'int', ['pointer', 'int']);
  Interceptor.attach(rc.base.add(RC_POSE.nodePose), {
    onEnter(a) {
      const node = a[0], graph = node.add(0x2b0).readPointer();
      let vt = null, vfn = null;
      if (!graph.isNull()) { vt = graph.readPointer(); vfn = vt.add(0x90).readPointer(); }
      send({ev: 'posenode', node: node.toString(), graph: graph.toString(),
            vtable: vt ? rcOffset(vt) : null, vfn90: vfn ? rcOffset(vfn) : null,
            graphHead: graph.isNull() ? '' : hexOf(graph, 0x100)});
    },
  });
  Interceptor.attach(rc.base.add(RC_POSE.graphPose), {
    onEnter(a) {
      this.out = a[3]; this.n = a[2].toInt32();
      const pose = a[0], holder = pose.readPointer();
      const skel = holder.isNull() ? ptr(0) : holder.readPointer();
      const count = pose.add(0x1c).readS32();
      const bones = [];
      if (!skel.isNull()) {
        const total = skel.add(0x178).readS32();
        for (let i = 0; i < total; i++) {
          const s = boneName(skel, i);
          bones.push({name: s.isNull() ? null : s.readUtf8String(), parent: boneParent(skel, i)});
        }
      }
      this.rec = {ev: 'graphpose', pose: pose.toString(), poseHead: hexOf(pose, 0x40),
                  poseVtable: rcOffset(pose.readPointer()), skel: skel.toString(), count: count, n: this.n,
                  locals: hexOf(pose.add(8).readPointer(), Math.max(count, 0) * 0x20), root: hexOf(a[1], 0x20),
                  mask: a[4].toString(), bones: bones,
                  callers: Thread.backtrace(this.context, Backtracer.ACCURATE).slice(0, 12).map(rcOffset)};
    },
    onLeave(r) {
      this.rec.result = r.toInt32();
      this.rec.out = hexOf(this.out, Math.min(this.n, r.toInt32()) * 0x20);
      sendJson(this.rec);
    },
  });
}

watch('resourcecompiler.dll', hookPose);
