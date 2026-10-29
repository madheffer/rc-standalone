// Frida agent for run_probe.py --frida: who reads a barn light's 0x640-byte CPU light entry (CUBEMAPS.md A.6),
// to reach the g_BarnLights writer. Build: scenesystem 20261029 (the 10-15 addresses, rva_map.installed).
//
// 18019fe30 (the per-view barn light gather) copy-constructs its entry into a bucket list with 1801c9a40
// (dest, src; return 1801a0f21). 1801c9a40 has 16 other call sites (1801cc930 alone has ten), any of which may
// copy entries on into the list the GPU pack reads. Once rendering runs (the ARM_AT-th base-object record batch,
// 18007a540 -> 1800ed2d0, return 18007a5af):
//   slots 0, 1  read/write on +0 (the light object) and +0x148 (in the transform copy) of the next entry the
//               gather appends;
//   slots 2, 3  the same on the next entry any OTHER call site copies.
// A read under a read/write watchpoint traps, so the agent never reads a watched word (bytes are taken before
// arming), and every watchpoint is dropped past TRAPS traps (entries are per frame and their memory is reused).
// Logs (send): callers (1801c9a40 return addresses with counts, every 2000 calls), armed, access (pc, backtrace,
// the first PER per pc), gave-up. Function entries and returns only, nothing patched.
'use strict';

const IMAGE = ptr('0x180000000');
const ARM_AT = 600;
const PER = 4;
const TRAPS = 20000;
const OFFSETS = [0x0, 0x148];
let batches = 0;
let calls = 0;
let traps = 0;
let sent = 0;
const callers = new Map();
const watched = [null, null];
const seen = new Map();
const armedOnce = [false, false];

function where(a) {
  const m = Process.findModuleByAddress(a);
  return m ? m.name + '!0x' + a.sub(m.base).add(IMAGE).toString(16) : '0x' + a.toString(16);
}

function bytes(p, n) {
  return Array.from(new Uint8Array(p.readByteArray(n))).map(b => b.toString(16).padStart(2, '0')).join('');
}

function arm(group, entry) {
  watched[group] = entry;
  for (const t of Process.enumerateThreads())
    OFFSETS.forEach((off, k) => { try { t.setHardwareWatchpoint(group * 2 + k, entry.add(off), 8, 'rw'); } catch (e) { } });
}

function disarmAll() {
  for (const t of Process.enumerateThreads())
    for (let k = 0; k < 4; k++)
      try { t.unsetHardwareWatchpoint(k); } catch (e) { }
  watched.fill(null);
}

Process.setExceptionHandler(details => {
  if (details.type !== 'single-step' && details.type !== 'breakpoint')
    return false;
  if (!watched.some(w => w !== null))
    return false;
  if (++traps === TRAPS) {
    disarmAll();
    send({ kind: 'gave-up', traps });
    return true;
  }
  const pc = where(details.address);
  const n = (seen.get(pc) || 0) + 1;
  seen.set(pc, n);
  if (n <= PER && sent++ < 3000) {
    let bt = [];
    try { bt = Thread.backtrace(details.context, Backtracer.ACCURATE).slice(0, 10).map(where); } catch (e) { bt = ['error ' + e]; }
    send({ kind: 'access', pc, n, backtrace: bt });
  }
  return true;
});

function hook(ss) {
  const batch = ss.base.add(ptr('0x18007a5af').sub(IMAGE));
  const gather = ss.base.add(ptr('0x1801a0f21').sub(IMAGE));
  Interceptor.attach(ss.base.add(ptr('0x1800ed2d0').sub(IMAGE)), {
    onEnter() { if (this.returnAddress.equals(batch)) batches++; }
  });
  Interceptor.attach(ss.base.add(ptr('0x1801c9a40').sub(IMAGE)), {
    onEnter(args) { this.dest = args[0]; this.from = this.returnAddress; },
    onLeave() {
      const key = where(this.from);
      callers.set(key, (callers.get(key) || 0) + 1);
      if (++calls % 2000 === 0)
        send({ kind: 'callers', calls, batches, callers: Object.fromEntries(callers) });
      if (batches < ARM_AT)
        return;
      const group = this.from.equals(gather) ? 0 : 1;
      if (armedOnce[group])
        return;
      const entry = ptr(this.dest.toString());
      // Never watch the stack: the exception handler's own frames land on it, trap there and kill the host
      // (run 2026-10-30: 1801a222f copies into a local).
      const sp = this.context.rsp;
      if (entry.compare(sp.sub(0x400000)) > 0 && entry.compare(sp.add(0x400000)) < 0)
        return;
      armedOnce[group] = true;
      // Never read across an entry that is already watched (the trap would land inside this script).
      const near = watched.some(w => w !== null && w.compare(entry.add(0x640)) < 0 && entry.compare(w.add(0x640)) < 0);
      send({ kind: 'armed', group, caller: key, entry: '0x' + entry.toString(16), bytes: near ? null : bytes(entry, 0x640) });
      arm(group, entry);
    }
  });
}

rpc.exports = { counts() { return { calls, batches, traps, callers: Object.fromEntries(callers), pcs: Object.fromEntries(seen) }; } };

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
