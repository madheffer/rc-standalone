// Frida agent for run_probe.py --frida: who reads and writes a barn light object, to find its g_BarnLights record
// (CUBEMAPS.md A.6). Build: scenesystem 20261029 (1800cd8c0 at the 10-15 address, rva_map.installed).
//
// 1800cd8c0 creates a barn light scene object (0x270 bytes, vftable 1805edfd0 in this build; its constructor zeroes
// +0x1e0 .. +0x21c and +0x250 .. +0x26c). Armed during the map load, a watchpoint stops the load (run 2026-10-30:
// the host died 27 s in), so arming waits for rendering: the ARM_AT-th base-object record batch 18007a540 asks
// 1800ed2d0 for (return 18007a5af). Then the last WATCHED objects created get read/write watchpoints on +0x1e0
// and +0x200 (8 bytes each, all threads). Logs (send):
//   create  each object created, with its index and the caller
//   access  the first PER accesses from each pc: the pc, the backtrace and the object's 0x270 bytes
//   final   per watched object, its 0x270 bytes on the agent's counts() call
// Function entries and returns only, nothing patched.
'use strict';

const IMAGE = ptr('0x180000000');
const WATCHED = 2;
const ARM_AT = 600;
const PER = 4;
const OFFSETS = [0x1e0, 0x200];
let batches = 0;
const created = [];
const watched = [];
const seen = new Map();
let sent = 0;

function where(a) {
  const m = Process.findModuleByAddress(a);
  return m ? m.name + '!0x' + a.sub(m.base).add(IMAGE).toString(16) : '0x' + a.toString(16);
}

function bytes(p, n) {
  return Array.from(new Uint8Array(p.readByteArray(n))).map(b => b.toString(16).padStart(2, '0')).join('');
}

Process.setExceptionHandler(details => {
  if (details.type !== 'single-step' && details.type !== 'breakpoint')
    return false;
  if (watched.length === 0)
    return false;
  const pc = where(details.address);
  const n = (seen.get(pc) || 0) + 1;
  seen.set(pc, n);
  if (n <= PER && sent++ < 2000) {
    let bt = [];
    try { bt = Thread.backtrace(details.context, Backtracer.ACCURATE).slice(0, 10).map(where); } catch (e) { bt = ['error ' + e]; }
    send({ kind: 'access', pc, n, objects: watched.map(o => bytes(o, 0x270)), backtrace: bt });
  }
  return true;
});

function hook(ss) {
  Interceptor.attach(ss.base.add(ptr('0x1800cd8c0').sub(IMAGE)), {
    onLeave(retval) {
      if (retval.isNull())
        return;
      const ret = ptr(retval.toString());
      created.push(ret);
      send({ kind: 'create', index: created.length - 1, object: '0x' + ret.toString(16), caller: where(this.returnAddress) });
    }
  });
  const ret = ss.base.add(ptr('0x18007a5af').sub(IMAGE));
  Interceptor.attach(ss.base.add(ptr('0x1800ed2d0').sub(IMAGE)), {
    onEnter() {
      if (!this.returnAddress.equals(ret) || ++batches !== ARM_AT)
        return;
      for (const o of created.slice(-WATCHED)) {
        const first = watched.length * OFFSETS.length;
        watched.push(o);
        for (const t of Process.enumerateThreads())
          OFFSETS.forEach((off, i) => { try { t.setHardwareWatchpoint(first + i, o.add(off), 8, 'rw'); } catch (e) { } });
        send({ kind: 'armed', object: '0x' + o.toString(16), bytes: bytes(o, 0x270) });
      }
    }
  });
}

rpc.exports = {
  counts() {
    for (const o of watched)
      try { send({ kind: 'final', object: '0x' + o.toString(16), bytes: bytes(o, 0x270) }); } catch (e) { }
    return { created: created.length, pcs: Object.fromEntries(seen), accesses: sent };
  }
};

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
