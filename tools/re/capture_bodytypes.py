"""capture_bodytypes.py <addon> <map> [--out file] [--switches "-world -phys -nav"]: every body's type, by node.

Runs resourcecompiler on the map under Frida (CS2 closed; the map's package is backed up and restored)
and logs, as JSON lines, every CRnBody::SetType (vphysics2 1801bd3e0, the same bytes in 0924 to 1015:
body, type, the body's type before (+0x54), the caller as module+RVA) and, once per body and query type mask,
the body's type and world mass centre at the query test 1801ff7b0 (vphysics2 1015), until navsystem's
first world cast (vphysics2 180065360 called from navsystem.dll), which logs {"nav": true} and ends the
compile. The type each body has then is the type the nav world's casts and extraction see.
"""
import argparse
import filecmp
import json
import os
import shutil
import subprocess
import threading

import frida

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")

AGENT = r"""
'use strict';
function where(addr) {
  const m = Process.findModuleByAddress(addr);
  return m === null ? addr.toString() : m.name + '+0x' + addr.sub(m.base).toString(16);
}
const seen = {};
function hook() {
  const vp = Process.findModuleByName('vphysics2.dll');
  if (vp === null) { setTimeout(hook, 5); return; }
  Interceptor.attach(vp.base.add(0x1bd3e0), { onEnter(args) {
    const body = args[0];
    let before = null;
    try { before = body.add(0x54).readS32(); } catch (e) {}
    send({body: body.toString(), type: args[1].toInt32(), before: before, caller: where(this.returnAddress)});
  } });
  // 1801ff7b0 (world, shape, query): the query's body type test (shape +0x10 the body, its type
  // +0x54, the query's type mask +0x2f) before the attribute test; each body once per mask, with
  // its world mass centre (+0xfc), the first shape's broadphase tree (proxy +0x1c, bits 0..2) and
  // whether the test passed.
  Interceptor.attach(vp.base.add(0x1ff7b0), {
    onEnter(args) {
      this.body = args[1].add(0x10).readPointer();
      this.proxy = args[1].add(0x1c).readS32();
      this.mask = args[2].add(0x2f).readU8();
      this.key = this.body.toString() + ':' + this.mask;
    },
    onLeave(ret) {
      if (seen[this.key]) return;
      seen[this.key] = true;
      const b = this.body;
      send({tested: b.toString(), type: b.add(0x54).readS32(), mask: this.mask, pass: ret.toInt32() & 0xff, tree: this.proxy & 7,
            pos: [b.add(0xfc).readFloat(), b.add(0x100).readFloat(), b.add(0x104).readFloat()]});
    }
  });
  let navDone = false;
  Interceptor.attach(vp.base.add(0x65360), { onEnter(args) {
    if (navDone) return;
    const m = Process.findModuleByAddress(this.returnAddress);
    if (m !== null && m.name.toLowerCase() === 'navsystem.dll') {
      navDone = true;
      send({nav: true});
    }
  } });
}
hook();
"""


def running(name):
    return name.lower() in subprocess.run(["tasklist"], capture_output=True, text=True).stdout.lower()


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("addon")
    p.add_argument("map")
    p.add_argument("--out")
    p.add_argument("--switches", default="-world -phys -nav")
    a = p.parse_args()
    if running("cs2.exe") or running("resourcecompiler.exe"):
        raise SystemExit("CS2 or a compile is running; not starting")
    out_path = a.out or os.path.join(os.environ.get("TEMP", "."), "bodytypes_capture", a.map + ".jsonl")
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    vpk = os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    backup = out_path + ".vpk.bak"
    if os.path.exists(vpk):
        shutil.copyfile(vpk, backup)
        os.remove(vpk)
    out = open(out_path, "wb")
    lock = threading.Lock()
    count = [0]
    nav = threading.Event()

    def on_message(msg, data):
        if msg.get("type") != "send":
            print("agent:", msg)
            return
        with lock:
            out.write((json.dumps(msg["payload"]) + chr(10)).encode())
            count[0] += 1
        if msg["payload"].get("nav"):
            nav.set()

    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    dev = frida.get_local_device()
    pid = dev.spawn([os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"),
                     "-i", source] + a.switches.split(), cwd=BIN, stdio="pipe")
    ses = dev.attach(pid)
    sc = ses.create_script(AGENT)
    sc.on("message", on_message)
    sc.load()
    done = threading.Event()
    ses.on("detached", lambda *x: done.set())
    dev.resume(pid)
    while not done.wait(2):
        # A few seconds past the first cast, so the casts' own query tests are logged too.
        if nav.is_set():
            threading.Event().wait(5)
        if nav.is_set() or running("cs2.exe"):
            subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True)
    while running("resourcecompiler.exe"):
        threading.Event().wait(2)
    out.close()
    print(out_path, count[0], "records", "(nav reached)" if nav.is_set() else "(nav NOT reached)")
    if os.path.exists(backup):
        shutil.copyfile(backup, vpk)
        ok = filecmp.cmp(backup, vpk, shallow=False)
        print("restored" if ok else "RESTORE CMP FAILED", vpk)
        if ok:
            os.remove(backup)


if __name__ == "__main__":
    main()
