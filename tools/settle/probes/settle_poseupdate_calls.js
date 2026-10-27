// Every function CAnimationHelper's update (slot 0x88, animationsystem.dll 2026-10-23 0x6f450)
// calls on one model, for capture_settle.py --agent after settle_bundle.js and settle_pose.js. The first update
// is followed with Stalker; "updatecalls" lists each call target (module offset) with its count, in
// first-call order.

const AS_UPDATE = 0x6f450;
let updateTraced = false;
// Only a helper whose pose has this many bones (94: ctm_sas; the chicken's 117 update leaves its pose).
const TRACE_BONES = 94;

function hookUpdate(as) {
  Interceptor.attach(as.base.add(AS_UPDATE), {
    onEnter(a) {
      if (updateTraced || a[0].add(0x5c).readS32() !== TRACE_BONES) return;
      updateTraced = true;
      this.traced = true;
      this.order = [];
      this.counts = {};
      const self = this;
      Stalker.follow(this.threadId, {
        events: {call: true},
        onReceive(events) {
          for (const e of Stalker.parse(events, {annotate: false, stringify: false})) {
            const k = rcOffset(e[1]);
            if (!(k in self.counts)) { self.counts[k] = 0; self.order.push(k); }
            self.counts[k]++;
          }
        },
      });
    },
    onLeave() {
      if (!this.traced) return;
      Stalker.unfollow(this.threadId);
      Stalker.flush();
      const self = this;
      setTimeout(() => {
        sendJson({ev: 'updatecalls', calls: self.order.map(k => [k, self.counts[k]])});
      }, 500);
    },
  });
}

watch('animationsystem.dll', hookUpdate);
