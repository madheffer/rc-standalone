// Frida agent for run_probe.py --frida: who reads and writes a barn light object, to find its g_BarnLights record
// (CUBEMAPS.md A.6). Build: scenesystem 20261029 (1800cd8c0 at the 10-15 address, rva_map.installed).
//
// 1800cd8c0 creates a barn light scene object (0x270 bytes, vftable 1805edfd0 in this build; its constructor zeroes
// +0x1e0 .. +0x21c and +0x250 .. +0x26c). Armed during the map load, a watchpoint stops the load (run 2026-10-30:
// the host died 27 s in), so arming waits for rendering: the ARM_AT-th base-object record batch 18007a540 asks
// 1800ed2d0 for (return 18007a5af). Then WATCHED objects still alive, spread over the live set, get a
// read/write watchpoint on +0x30 (the transform 1800ce950 reads; 8 bytes, all threads; +0x1e0 / +0x200 drew only
// client.dll's writes). Objects are destroyed and re-created all the time (157 created
// on one run), so 1800cdae0 (the desc's destroy, vfunc 0x28) drops an object from the live set and, when it is
// watched, its watchpoints: left on freed memory they trap on every allocation there and the build stalls.
// Logs (send):
//   create  each object created, with its index and the caller
//   access  the first PER accesses from each pc: the pc, the backtrace and the object's 0x270 bytes
//   final   per watched object, its 0x270 bytes on the agent's counts() call
// Function entries and returns only, nothing patched.
'use strict';

const IMAGE = ptr('0x180000000');
const WATCHED = 4;
const ARM_AT = 600;
const PER = 4;
const OFFSETS = [0x30];
let batches = 0;
const created = [];
const alive = new Set();
const watched = [];
const seen = new Map();
let sent = 0;
let exceptions = 0;
let traps = 0;
const TRAPS = 200000;

function where(a) {
  const m = Process.findModuleByAddress(a);
  return m ? m.name + '!0x' + a.sub(m.base).add(IMAGE).toString(16) : '0x' + a.toString(16);
}

function bytes(p, n) {
  return Array.from(new Uint8Array(p.readByteArray(n))).map(b => b.toString(16).padStart(2, '0')).join('');
}

// An object's bytes with the watched words left out ('?'): a read of a word under a read/write watchpoint
// traps, and from this script (send, or the exception handler itself) that trap is what killed the host in
// the first four runs.
function unwatched(o) {
  let out = '';
  let at = 0;
  for (const off of OFFSETS) {
    out += bytes(o.add(at), off - at) + '?'.repeat(16);
    at = off + 8;
  }
  return out + bytes(o.add(at), 0x270 - at);
}

Process.setExceptionHandler(details => {
  if (details.type !== 'single-step' && details.type !== 'breakpoint') {
    // Anything else is the game's own: log it (it may be what kills the host) and let it through.
    if (exceptions++ < 50)
      send({ kind: 'exception', type: details.type, at: where(details.address), memory: details.memory ? { op: details.memory.operation, address: '0x' + details.memory.address.toString(16) } : null, thread: Process.getCurrentThreadId() });
    return false;
  }
  if (!watched.some(w => w !== null))
    return false;
  // A safety net for a destroy path 1800cdae0 does not see: past TRAPS traps, drop every watchpoint.
  if (++traps === TRAPS) {
    for (const t of Process.enumerateThreads())
      for (let k = 0; k < 4; k++)
        try { t.unsetHardwareWatchpoint(k); } catch (e) { }
    watched.fill(null);
    send({ kind: 'gave-up', traps });
  }
  const pc = where(details.address);
  const n = (seen.get(pc) || 0) + 1;
  seen.set(pc, n);
  if (n <= PER && sent++ < 2000) {
    let bt = [];
    try { bt = Thread.backtrace(details.context, Backtracer.ACCURATE).slice(0, 10).map(where); } catch (e) { bt = ['error ' + e]; }
    send({ kind: 'access', pc, n, objects: watched.map(w => w === null ? null : unwatched(w)), backtrace: bt });
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
      alive.add(ret.toString());
      send({ kind: 'create', index: created.length - 1, object: '0x' + ret.toString(16), caller: where(this.returnAddress) });
    }
  });
  Interceptor.attach(ss.base.add(ptr('0x1800cdae0').sub(IMAGE)), {
    onEnter(args) {
      const o = args[1];
      alive.delete(o.toString());
      const i = watched.findIndex(w => w !== null && w.equals(o));
      if (i < 0)
        return;
      watched[i] = null;
      for (const t of Process.enumerateThreads())
        OFFSETS.forEach((off, k) => { try { t.unsetHardwareWatchpoint(i * OFFSETS.length + k); } catch (e) { } });
      send({ kind: 'freed', slot: i, object: '0x' + o.toString(16), caller: where(this.returnAddress) });
    }
  });
  const ret = ss.base.add(ptr('0x18007a5af').sub(IMAGE));
  Interceptor.attach(ss.base.add(ptr('0x1800ed2d0').sub(IMAGE)), {
    onEnter() {
      if (!this.returnAddress.equals(ret) || ++batches !== ARM_AT)
        return;
      send({ kind: 'arming', batches, threads: Process.enumerateThreads().length });
      const live = created.filter(c => alive.has(c.toString()));
      for (const o of live.filter((_, i) => i % Math.max(1, Math.floor(live.length / WATCHED)) === 0).slice(0, WATCHED)) {
        const first = watched.length * OFFSETS.length;
        send({ kind: 'armed', object: '0x' + o.toString(16), bytes: bytes(o, 0x270) });
        watched.push(o);
        for (const t of Process.enumerateThreads())
          OFFSETS.forEach((off, i) => { try { t.setHardwareWatchpoint(first + i, o.add(off), 8, 'rw'); } catch (e) { } });
      }
    }
  });
}

rpc.exports = {
  counts() {
    for (const o of watched.filter(w => w !== null))
      try { send({ kind: 'final', object: '0x' + o.toString(16), bytes: unwatched(o) }); } catch (e) { }
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
