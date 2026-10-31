// Frida agent for run_probe.py --frida: a barn light's scene bounds and who writes their radius (CUBEMAPS.md F.1).
// Build: scenesystem 20261029 (the 10-15 addresses, code-identical). 1800ce950 (desc, object) computes the light's
// world box (1804a2480 on the description at +0xf0 and the transform at +0x30, clipped by the illumination box
// +0x220 when +0x251 is set) and stores it through CSceneSystem vf 0xe0 (1800d5410, snapped outward) into the
// bounds block at object +0x20: min +0, max +0x10, radius +0x1c, which the object cull (18003d380) hands
// 18004c390 as the sphere's radius. The snap leaves +0x1c 0, so something else writes it.
// Logs every call's object, box and radius word on return (at most BOXES), and arms one write watchpoint on
// the radius of the first WATCHED objects whose bounds block exists on entry, sending the writer's backtrace
// once per hit (at most HITS), then disarming. Write-only watchpoints; the handler reads no watched word.
'use strict';

const IMAGE = ptr('0x180000000');
const BOXES = 400;
const WATCHED = 2;
const HITS = 6;
const watched = [];
let boxes = 0;
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

function disarm() {
  for (let s = 0; s < watched.length; s++)
    for (const t of Process.enumerateThreads())
      try { t.unsetHardwareWatchpoint(s); } catch (e) { }
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
    disarm();
  return true;
});

function hook(ss) {
  Interceptor.attach(ss.base.add(ptr('0x1800ce950').sub(IMAGE)), {
    onEnter(args) {
      this.object = args[1];
      const bounds = this.object.add(0x20).readPointer();
      if (bounds.isNull() || watched.length >= WATCHED || hits >= HITS)
        return;
      const slot = watched.length;
      watched.push(bounds);
      for (const t of Process.enumerateThreads())
        try { t.setHardwareWatchpoint(slot, bounds.add(0x1c), 4, 'w'); } catch (e) { }
      send({ kind: 'armed', object: '0x' + this.object.toString(16), bounds: '0x' + bounds.toString(16) });
    },
    onLeave() {
      if (boxes >= BOXES)
        return;
      boxes++;
      const bounds = this.object.add(0x20).readPointer();
      if (bounds.isNull()) {
        send({ kind: 'box', object: '0x' + this.object.toString(16), bounds: null });
        return;
      }
      // Not a watched block (the handler never reads one, and neither does this).
      if (watched.some(w => w.equals(bounds)))
        return;
      send({ kind: 'box', object: '0x' + this.object.toString(16), transform: floats(this.object.add(0x30), 12),
             box: floats(bounds, 8), illumination: this.object.add(0x251).readU8() });
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
