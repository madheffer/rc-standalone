// Frida agent for run_probe.py --frida: who writes a base scene object's g_instanceBuffer record (CUBEMAPS.md A.4).
// Build: scenesystem 20261029 (1800ed2d0 and 18007a540 are at the 10-15 addresses, rva_map.installed).
//
// 1800ed2d0 (pool, count, out) hands out <count> 0x20-byte records and stores the first's address in *out;
// 18007a540 calls it (return 18007a5af) and then writes w1, w2 and w6 itself. Every STEP-th such call, the first
// record of the batch gets four hardware write watchpoints (+0, +8, +0x10, +0x18: the whole record, all threads).
// Logs (send):
//   armed   the call index, the record address and its 32 bytes at the allocator's return
//   write   per write: the pc, the backtrace and the record's 32 bytes after it
//   later   the previously armed record's 32 bytes at the next arm (words no CPU write touched)
// Function entries and returns only, nothing patched.
'use strict';

const IMAGE = ptr('0x180000000');
const STEP = Number('%STEP%') || 4000;
const ARMS = 40;
let ss = null;
let calls = 0;
let arms = 0;
let record = null;
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
  if (record === null)
    return false;
  let bt = [];
  try { bt = Thread.backtrace(details.context, Backtracer.ACCURATE).slice(0, 10).map(where); } catch (e) { bt = ['error ' + e]; }
  if (sent++ < 3000)
    send({ kind: 'write', arm: arms, pc: where(details.address), record: bytes(record, 0x20), backtrace: bt });
  return true;
});

function hook() {
  const ret = ss.base.add(ptr('0x18007a5af').sub(IMAGE));
  Interceptor.attach(ss.base.add(ptr('0x1800ed2d0').sub(IMAGE)), {
    onEnter(args) { this.out = args[2]; this.count = args[1].toInt32(); this.mine = this.returnAddress.equals(ret); },
    onLeave(retval) {
      if (!this.mine || arms >= ARMS || calls++ % STEP !== STEP - 1)
        return;
      const r = this.out.readPointer();
      if (r.isNull())
        return;
      if (record !== null)
        send({ kind: 'later', arm: arms, record: bytes(record, 0x20) });
      record = ptr(r.toString());
      arms++;
      for (const t of Process.enumerateThreads())
        for (let slot = 0; slot < 4; slot++)
          try { t.setHardwareWatchpoint(slot, record.add(slot * 8), 8, 'w'); } catch (e) { }
      send({ kind: 'armed', arm: arms, call: calls, count: this.count, index: retval.toInt32(), address: '0x' + record.toString(16), record: bytes(record, 0x20) });
    }
  });
}

rpc.exports = { counts() { return { calls, arms, writes: sent }; } };

function poll() {
  ss = Process.findModuleByName('scenesystem.dll');
  if (ss)
    hook();
  else
    setTimeout(poll, 20);
}
poll();
