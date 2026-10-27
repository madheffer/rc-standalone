// Which call fills the animation helper's pose, for capture_settle.py --agent after settle_bundle.js
// and settle_pose.js.
//
// Hooks every slot of CAnimationHelper's vtable (animationsystem.dll 2026-10-23 0x723108, slots
// 0..0xf8) and, for each call on a helper, compares its pose (helper +0x40: buffer at +0x48, bone
// count at +0x5c) before and after. A call that changes it sends "posewrite": the slot, the helper,
// the count, the buffer before and after, and the caller chain. Small slots Frida cannot hook are
// listed in "posewriter_skipped".

const AS_HELPER_VTABLE = 0x723108;

function poseBytes(helper) {
  try {
    const n = helper.add(0x5c).readS32(), buf = helper.add(0x48).readPointer();
    if (n <= 0 || n > 4096 || buf.isNull()) return {n: n, buf: buf.toString(), hex: ''};
    return {n: n, buf: buf.toString(), hex: hexOf(buf, n * 0x20)};
  } catch (e) { return {n: -1, buf: '', hex: ''}; }
}

function hookHelper(as) {
  const vt = as.base.add(AS_HELPER_VTABLE), seen = {}, skipped = [];
  for (let k = 0; k < 0x100; k += 8) {
    const fn = vt.add(k).readPointer();
    if (seen[fn]) continue;
    seen[fn] = true;
    const slot = k;
    try {
      Interceptor.attach(fn, {
        onEnter(a) {
          this.helper = a[0];
          if (this.helper.isNull() || !this.helper.readPointer().equals(vt)) { this.helper = null; return; }
          this.before = poseBytes(this.helper);
          if (slot === 0x88) {
            // The update's branch inputs: +0x249 (byte), +0x150 (graph instance), +0x70 (sequence count), +0x8c.
            const inst = this.helper.add(0x150).readPointer();
            send({ev: 'helperupdate', helper: this.helper.toString(), b249: this.helper.add(0x249).readU8(),
                  instance: inst.toString(), instanceVtable: inst.isNull() ? null : rcOffset(inst.readPointer()),
                  seqCount: this.helper.add(0x70).readS32(), b8c: this.helper.add(0x8c).readU8(),
                  head: hexOf(this.helper, 0x260), args: [a[1], a[2], a[3], a[4], a[5]].map(x => x.toString())});
          }
        },
        onLeave() {
          if (!this.helper) return;
          const after = poseBytes(this.helper);
          if (after.hex !== this.before.hex || after.n !== this.before.n)
            sendJson({ev: 'posewrite', slot: slot, fn: rcOffset(fn), helper: this.helper.toString(),
                      before: this.before, after: after,
                      callers: Thread.backtrace(this.context, Backtracer.ACCURATE).slice(0, 16).map(rcOffset)});
        },
      });
    } catch (e) { skipped.push({slot: slot, fn: rcOffset(fn), why: '' + e}); }
  }
  send({ev: 'posewriter_skipped', skipped: skipped});
}

watch('animationsystem.dll', hookHelper);
