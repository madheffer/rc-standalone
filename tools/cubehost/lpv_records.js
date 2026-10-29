// Frida agent for run_probe.py --frida: where a light probe volume object's GPU record comes from (CUBEMAPS.md A.1).
// Build: scenesystem 20261029 (1800cd300 and 1801baa00 are at the 10-15 addresses, rva_map.installed).
//
// 1800cd300 creates a CSceneLightProbeVolumeObject (0x1d0 bytes, vftable 1805edfe8 in this build) and links it
// into the list 1801ba530 walks; 1801baa00 (_, object) copies the object's 0x90 bytes at +0x130 into
// PerViewLightProbeVolumeConstantBuffer_t. Logs (send):
//   create  each object created, with its index and the caller
//   write   per write into the first WATCHED objects' +0x130 (a hardware write watchpoint on each, all threads):
//           the pc, the backtrace and the 8 bytes written
//   record  once per distinct object passed to 1801baa00: the whole object, hex
// Function entries and returns only, nothing patched.
'use strict';

const IMAGE = ptr('0x180000000');
// WATCHED objects get a watchpoint, counted from the SKIP-th created (the tools scene's own volume comes first;
// a watchpoint during map load can stop the load, so 0 is the default and a watch run is a separate run).
const WATCHED = Number('%WATCHED%') || 0;
const SKIP = 1;
const watched = [];
const created = [];
const recorded = new Set();
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
  let bt = [];
  try { bt = Thread.backtrace(details.context, Backtracer.ACCURATE).slice(0, 10).map(where); } catch (e) { bt = ['error ' + e]; }
  if (sent++ < 400)
    send({ kind: 'write', pc: where(details.address), objects: watched.map(o => bytes(o.add(0x130), 0x90)), backtrace: bt });
  return true;
});

function hook(ss) {
  Interceptor.attach(ss.base.add(ptr('0x1800cd300').sub(IMAGE)), {
    onLeave(retval) {
      if (retval.isNull())
        return;
      // A copy: Frida reuses the retval object after the callback.
      const ret = ptr(retval.toString());
      created.push(ret);
      send({ kind: 'create', index: created.length - 1, object: '0x' + ret.toString(16), caller: where(this.returnAddress) });
      if (watched.length < WATCHED && created.length > SKIP) {
        const slot = watched.length;
        watched.push(ret);
        for (const t of Process.enumerateThreads())
          try { t.setHardwareWatchpoint(slot, ret.add(0x130), 8, 'w'); } catch (e) { }
        send({ kind: 'armed', slot, object: '0x' + ret.toString(16) });
      }
    }
  });
  Interceptor.attach(ss.base.add(ptr('0x1801baa00').sub(IMAGE)), {
    onEnter(args) {
      const o = args[1];
      const key = o.toString();
      if (recorded.has(key))
        return;
      recorded.add(key);
      send({ kind: 'record', object: '0x' + o.toString(16), index: created.findIndex(c => c.equals(o)), bytes: bytes(o, 0x1d0) });
    }
  });
}

rpc.exports = { counts() { return { created: created.length, recorded: recorded.size, writes: sent }; } };

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
