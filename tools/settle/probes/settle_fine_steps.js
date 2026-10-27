// Dynamic bodies at every step of a window, for capture_settle.py --agent after settle_bundle.js
// (which records steps 0..90 and every 10th after). Sends the same "bodies" events for steps
// FINE_FROM..FINE_TO that the bundle skips, taken as each step's Collide starts.
// RVAs are for vphysics2.dll of 2026-09-24.

const VP_FINE = {collide: 0x1f6980};
const FINE_FROM = 91, FINE_TO = 130;

function hookFineSteps(vp) {
  Interceptor.attach(vp.base.add(VP_FINE.collide), {
    onEnter() {
      if (!dynamic || lastStep < FINE_FROM || lastStep > FINE_TO || lastStep % 10 === 0) return;
      send({ev: 'bodies', step: lastStep, bodies: dynamic.map(([i, b]) => [i, hexOf(b, 0x280)])});
    },
  });
}

watch('vphysics2.dll', hookFineSteps);
