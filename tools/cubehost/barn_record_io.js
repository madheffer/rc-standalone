// Frida agent for run_probe.py --frida: input / output pairs of 1801b14b0, the g_BarnLights record writer
// (CUBEMAPS.md A.6), so the port can be checked byte for byte. Build: scenesystem 20261029 (the 10-15 addresses,
// rva_map.installed). No watchpoints: an entry and a return only.
//
// 1801b14b0 (binner, entry, record): the 0x640-byte CPU light entry in, the 0x1e0-byte record out. On entry the
// agent copies the entry and the binner state the decompile shows it reading (view +0x10 / +0x18 through binner
// +0xe1e8, the flags word +0x48 through +0xe1f0, +0xe540 .. +0xe54c, the globals 18091f834 and 180922570); on
// return the record. Logs (send) one 'pair' per distinct entry (by its first 0x200 bytes), at most PAIRS.
'use strict';

const IMAGE = ptr('0x180000000');
const PAIRS = 64;
const keys = new Set();
let pairs = 0;
let calls = 0;

function where(a) {
  const m = Process.findModuleByAddress(a);
  return m ? m.name + '!0x' + a.sub(m.base).add(IMAGE).toString(16) : '0x' + a.toString(16);
}

function bytes(p, n) {
  return Array.from(new Uint8Array(p.readByteArray(n))).map(b => b.toString(16).padStart(2, '0')).join('');
}

function hook(ss) {
  const g = a => ss.base.add(ptr(a).sub(IMAGE));
  Interceptor.attach(g('0x1801b14b0'), {
    onEnter(args) {
      calls++;
      if (pairs >= PAIRS)
        return;
      const binner = args[0];
      this.record = args[2];
      try {
        const entry = bytes(args[1], 0x640);
        const key = entry.slice(0, 0x400);
        if (keys.has(key))
          return;
        keys.add(key);
        this.pair = {
          kind: 'pair', caller: where(this.returnAddress), entry,
          view: bytes(binner.add(0xe1e8).readPointer(), 0x20),
          flags: bytes(binner.add(0xe1f0).readPointer().add(0x48), 4),
          e540: bytes(binner.add(0xe540), 0x10),
          g91f834: bytes(g('0x18091f834'), 8),
          g922570: bytes(g('0x180922570'), 1),
        };
      } catch (e) {
        this.pair = { kind: 'error', error: '' + e };
      }
    },
    onLeave() {
      if (!this.pair)
        return;
      try { this.pair.record = bytes(this.record, 0x1e0); } catch (e) { this.pair.error = '' + e; }
      pairs++;
      send(this.pair);
    }
  });
}

rpc.exports = { counts() { return { calls, pairs }; } };

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
