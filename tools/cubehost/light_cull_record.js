// Frida agent for run_probe.py --frida: the object cull's bounds record of each barn light (CUBEMAPS.md F.1) and
// who writes its radius. Build: scenesystem 20261029 (the 10-15 addresses, code-identical).
// 18003d380 (cull, index) reads the object's 32-byte record at [[cull +0x20] +0x180] + index * 32 (min +0, max
// +0x10, radius +0x1c, which it hands 18004c390 as the sphere's) and its object pointer at [[cull +0x20] +0x168] +
// index * 0x50. Barn light objects are the second argument of 1800ce950 (their bounds). For each light the first
// RECORDS records are sent; the radius of the first WATCHED is put under a write watchpoint (write-only, the
// handler reads nothing watched) and each hit's backtrace is sent, at most HITS.
'use strict';

const IMAGE = ptr('0x180000000');
const RECORDS = 3;
const WATCHED = 1;
const HITS = 8;
const lights = new Set();
const sentPer = new Map();
const watched = [];
let hits = 0;

function where(a) {
  const m = Process.findModuleByAddress(a);
  return m ? m.name + '!0x' + a.sub(m.base).add(IMAGE).toString(16) : '0x' + a.toString(16);
}

function floats(p, n) {
  const out = [];
  for (let i = 0; i < n; i++)
    out.push(p.add(4 * i).readFloat());
  return out;
}

Process.setExceptionHandler(details => {
  if (details.type !== 'single-step' && details.type !== 'breakpoint')
    return false;
  if (watched.length === 0)
    return false;
  let bt = [];
  try { bt = Thread.backtrace(details.context, Backtracer.ACCURATE).slice(0, 10).map(where); } catch (e) { bt = ['error ' + e]; }
  hits++;
  send({ kind: 'write', pc: where(details.address), backtrace: bt });
  if (hits >= HITS)
    for (const t of Process.enumerateThreads())
      try { t.unsetHardwareWatchpoint(0); } catch (e) { }
  return true;
});

function hook(ss) {
  const g = a => ss.base.add(ptr(a).sub(IMAGE));
  Interceptor.attach(g('0x1800ce950'), {
    onEnter(args) { lights.add(args[1].toString()); }
  });
  Interceptor.attach(g('0x18003d380'), {
    onEnter(args) {
      if (lights.size === 0)
        return;
      try {
        const index = args[1].toInt32();
        const block = args[0].add(0x20).readPointer();
        const object = block.add(0x168).readPointer().add(index * 0x50).readPointer();
        const key = object.toString();
        if (!lights.has(key))
          return;
        const n = sentPer.get(key) || 0;
        const record = block.add(0x180).readPointer().add(index * 32);
        if (n < RECORDS && !watched.some(w => w.equals(record))) {
          sentPer.set(key, n + 1);
          send({ kind: 'record', object: key, index, record: floats(record, 8), transform: floats(object.add(0x30), 12) });
        }
        if (watched.length < WATCHED && hits < HITS) {
          watched.push(record);
          for (const t of Process.enumerateThreads())
            try { t.setHardwareWatchpoint(0, record.add(0x1c), 4, 'w'); } catch (e) { }
          send({ kind: 'armed', object: key, record: '0x' + record.toString(16) });
        }
      } catch (e) {
        send({ kind: 'error', error: '' + e });
      }
    }
  });
}

let done = false;
function poll() {
  const mod = Process.findModuleByName('scenesystem.dll');
  if (mod && !done) {
    done = true;
    hook(mod);
  }
  if (!done)
    setTimeout(poll, 20);
}
poll();
