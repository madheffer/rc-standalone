// Frida agent for run_probe.py --frida: who writes a new scene object's 64-bit layer word (+0x80), GROUND_TRUTH 157.
// scenesystem 1800d5aa0 (CSceneSystem's create, worldrenderer's 180231c00 vf 0x58) returns the object; on its
// return a hardware write watchpoint is armed on +0x80 of the first OBJECTS objects worldrenderer creates (one slot each, all threads),
// and every write sends the new value and the backtrace. Function entries and returns only, nothing patched.
'use strict';

const IMAGE = ptr('0x180000000');
const OBJECTS = 4;
// WATCH=false logs only the word passed to the create (r9) and the object's +0x80 on return (no watchpoints).
const WATCH = false;
const watched = [];
let sent = 0;

function where(a) {
  const m = Process.findModuleByAddress(a);
  return m ? m.name + '!0x' + a.sub(m.base).add(IMAGE).toString(16) : '0x' + a.toString(16);
}

Process.setExceptionHandler(details => {
  if (details.type !== 'single-step' && details.type !== 'breakpoint')
    return false;
  if (watched.length === 0)
    return false;
  let bt = [];
  try { bt = Thread.backtrace(details.context, Backtracer.ACCURATE).slice(0, 8).map(where); } catch (e) { bt = ['error ' + e]; }
  if (sent++ < 2000)
    send({ kind: 'write', pc: where(details.address), words: watched.map(o => '0x' + o.add(0x80).readU64().toString(16)), backtrace: bt });
  return true;
});

function hook(ss) {
  const global = ss.base.add(ptr('0x180897be0').sub(IMAGE));
  Interceptor.attach(ss.base.add(ptr('0x1800d5aa0').sub(IMAGE)), {
    onEnter(args) { this.word = '0x' + args[3].toString(16); this.flags = '0x' + args[2].toString(16); },
    onLeave(ret) {
      if (!WATCH) {
        if (!ret.isNull() && where(this.returnAddress).startsWith('worldrenderer.dll') && sent++ < 50) {
          let init = null;
          try { init = where(global.readPointer().readPointer().add(0x240).readPointer()); } catch (e) { }
          send({ kind: 'create', passed: this.word, flags: this.flags, word: '0x' + ret.add(0x80).readU64().toString(16), f78: '0x' + ret.add(0x78).readU64().toString(16), init });
        }
        return;
      }
      // Only the world node aggregates worldrenderer 18001e920 creates.
      if (watched.length >= OBJECTS || ret.isNull() || !where(this.returnAddress).startsWith('worldrenderer.dll'))
        return;
      const slotIndex = watched.length;
      watched.push(ret);
      for (const t of Process.enumerateThreads())
        try { t.setHardwareWatchpoint(slotIndex, ret.add(0x80), 8, 'w'); } catch (e) { }
      send({ kind: 'armed', object: '0x' + ret.toString(16), word: '0x' + ret.add(0x80).readU64().toString(16), caller: where(this.returnAddress) });
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
