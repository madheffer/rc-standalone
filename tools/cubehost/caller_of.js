// Frida agent for run_probe.py --frida: who calls a function (a one-shot hardware execute breakpoint per thread).
// TARGET is module!va; on its first hits the backtrace (module!va per frame) and the first four arguments are sent,
// then the breakpoint is cleared on that thread. Used for scenesystem 180041f30 (CAggregateSceneObjectDesc slot
// 0x20, GROUND_TRUTH 157). Nothing is patched.
'use strict';

const IMAGE = ptr('0x180000000');
const TARGET = ['scenesystem.dll', '0x180041f30'];
const HITS = 8;
let address = null;
let hits = 0;

function where(a) {
  const m = Process.findModuleByAddress(a);
  return m ? m.name + '!0x' + a.sub(m.base).add(IMAGE).toString(16) : '0x' + a.toString(16);
}

Process.setExceptionHandler(details => {
  if (address === null || !details.address.equals(address))
    return false;
  const c = details.context;
  let bt = [];
  try { bt = Thread.backtrace(c, Backtracer.ACCURATE).slice(0, 10).map(where); } catch (e) { bt = ['error ' + e]; }
  send({ kind: 'call', thread: Process.getCurrentThreadId(), backtrace: bt, rcx: '0x' + c.rcx.toString(16), rdx: '0x' + c.rdx.toString(16), r8: '0x' + c.r8.toString(16) });
  try { Process.enumerateThreads().find(t => t.id === Process.getCurrentThreadId()).unsetHardwareBreakpoint(0); } catch (e) { }
  if (++hits >= HITS)
    for (const t of Process.enumerateThreads())
      try { t.unsetHardwareBreakpoint(0); } catch (e) { }
  return true;
});

function arm() {
  for (const t of Process.enumerateThreads())
    try { t.setHardwareBreakpoint(0, address); } catch (e) { }
}

function poll() {
  const mod = Process.findModuleByName(TARGET[0]);
  if (mod && address === null) {
    address = mod.base.add(ptr(TARGET[1]).sub(IMAGE));
    send({ kind: 'armed', at: where(address) });
  }
  // Threads made later (the render job threads) are armed too, until enough hits came in.
  if (address !== null && hits < HITS)
    arm();
  if (hits < HITS)
    setTimeout(poll, 500);
}
poll();
