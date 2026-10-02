"""capture_collapse.py <addon> <map> [--out file]: the order the bake collapses instances in.

Hooks MapDoc_CollapseInstance (resourcecompiler 180f5ff40 in the 09-23
analysis build, 180f602b0 in the installed 10-01 build) during a -world
-phys -fshallow compile. Each call logs one JSON line: the call's index, the
instance's node id (+0x300), origin (+0xa0) and angles (+0xac), and the node
id of the copy's root the collapse returns. Settles how the copies of
instances inside a prefab are numbered.

CS2 must be closed; the map's package is backed up and restored.
"""
import argparse
import filecmp
import json
import os
import shutil
import struct
import subprocess
import threading

import frida

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")
AGENT = r"""
'use strict';
let n = 0;
function hook() {
  const mod = Process.findModuleByName('resourcecompiler.dll');
  if (mod === null) { setTimeout(hook, 5); return; }
  Interceptor.attach(mod.base.add(0xf602b0), {
    onEnter(args) {
      // The instance is the first argument past the document that points at
      // readable memory (the 10-01 build passes it third).
      let i = null;
      for (const a of [args[1], args[2], args[3]])
        if (a.compare(ptr(0x10000)) > 0) { try { a.add(0x300).readS32(); i = a; break; } catch (e) {} }
      if (i === null) { send({call: n++, error: [args[1], args[2], args[3]].map(String)}); this.rec = null; return; }
      this.rec = {call: n++, id: i.add(0x300).readS32(),
                  origin: [i.add(0xa0).readFloat(), i.add(0xa4).readFloat(), i.add(0xa8).readFloat()],
                  angles: [i.add(0xac).readFloat(), i.add(0xb0).readFloat(), i.add(0xb4).readFloat()]};
    },
    onLeave(ret) {
      if (this.rec === null) return;
      this.rec.root = ret.isNull() ? null : ret.add(0x300).readS32();
      send(this.rec);
    }
  });
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
    a = p.parse_args()
    if running("cs2.exe") or running("resourcecompiler.exe"):
        raise SystemExit("CS2 or a compile is running; not starting")
    out_path = a.out or os.path.join(os.environ.get("TEMP", "."), "collapse_capture", a.map + ".jsonl")
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    vpk = os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    backup = out_path + ".vpk.bak"
    if os.path.exists(vpk):
        shutil.copyfile(vpk, backup)
        os.remove(vpk)
    out = open(out_path, "wb")
    lock = threading.Lock()
    count = [0]

    def on_message(msg, data):
        if msg.get("type") != "send":
            print("agent:", msg)
            return
        with lock:
            out.write((json.dumps(msg["payload"]) + chr(10)).encode())
            count[0] += 1

    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    dev = frida.get_local_device()
    pid = dev.spawn([os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"),
                     "-i", source, "-world", "-phys", "-fshallow"], cwd=BIN, stdio="pipe")
    ses = dev.attach(pid)
    sc = ses.create_script(AGENT)
    sc.on("message", on_message)
    sc.load()
    done = threading.Event()
    ses.on("detached", lambda *x: done.set())
    dev.resume(pid)
    while not done.wait(2):
        if running("cs2.exe"):
            subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True)
    while running("resourcecompiler.exe"):
        threading.Event().wait(2)
    out.close()
    print(out_path, count[0], "calls")
    if os.path.exists(vpk):
        shutil.copyfile(vpk, out_path + ".vpk")
    if os.path.exists(backup):
        shutil.copyfile(backup, vpk)
        ok = filecmp.cmp(backup, vpk, shallow=False)
        print("restored" if ok else "RESTORE CMP FAILED", vpk)
        if ok:
            os.remove(backup)


if __name__ == "__main__":
    main()
