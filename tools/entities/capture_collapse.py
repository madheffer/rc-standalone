"""capture_collapse.py <addon> <map> [--out file]: the order the bake collapses instances in.

Hooks MapDoc_CollapseInstance (resourcecompiler 180f5ff40 in the 09-23
analysis build, mapped to the installed build by rva_map) during a -world
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

import sys

import frida

sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "re"))
from rva_map import installed  # noqa: E402

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")
AGENT = r"""
'use strict';
let n = 0, current = -1;
function hook() {
  const mod = Process.findModuleByName('resourcecompiler.dll');
  if (mod === null) { setTimeout(hook, 5); return; }
  Interceptor.attach(mod.base.add(COLLAPSE_RVA), {
    onEnter(args) {
      // The instance is the first argument past the document that points at
      // readable memory (the 10-01 build passes it third).
      let i = null;
      for (const a of [args[1], args[2], args[3]])
        if (a.compare(ptr(0x10000)) > 0) { try { a.add(0x300).readS32(); i = a; break; } catch (e) {} }
      if (i === null) { send({call: n++, error: [args[1], args[2], args[3]].map(String)}); this.rec = null; return; }
      current = n;
      let target = null;
      try { const t = i.add(0x550).readPointer(); if (!t.isNull()) { const up = t.add(0x198).readPointer();
        target = {id: t.add(0x300).readS32(), children: t.add(0x1f8).readS32(),
                  parent: up.isNull() ? null : {id: up.add(0x300).readS32(), vt: up.readPointer().sub(mod.base).toInt32()}}; } } catch (e) {}
      this.rec = {call: n++, id: i.add(0x300).readS32(), target: target,
                  origin: [i.add(0xa0).readFloat(), i.add(0xa4).readFloat(), i.add(0xa8).readFloat()],
                  angles: [i.add(0xac).readFloat(), i.add(0xb0).readFloat(), i.add(0xb4).readFloat()]};
    },
    onLeave(ret) {
      if (this.rec === null) return;
      this.rec.root = ret.isNull() ? null : ret.add(0x300).readS32();
      send(this.rec);
      current = -1;
    }
  });
  if (PARENTS) {
    // 180f75010 AddChild(parent, child): every reparenting, with the caller.
    Interceptor.attach(mod.base.add(ADDCHILD_RVA), {
      onEnter(args) {
        try {
          const old = args[1].add(0x198).readPointer();
          const rec = {addchild: args[0].add(0x300).readS32(), child: args[1].add(0x300).readS32(),
                       from: old.isNull() ? null : old.add(0x300).readS32(), during: current};
          // A reparenting (the child had a parent): who did it, as installed-build RVAs.
          if (!old.isNull())
            rec.stack = Thread.backtrace(this.context, Backtracer.ACCURATE).slice(0, 12)
              .map(a => a.compare(mod.base) >= 0 && a.compare(mod.base.add(mod.size)) < 0 ? a.sub(mod.base).toInt32() : null);
          send(rec);
        } catch (e) {}
      }
    });
  }
  if (CLONES) {
    // 180f86a90: one node's clone and its children's, during a collapse; null for a node it skips.
    Interceptor.attach(mod.base.add(CLONE_RVA), {
      onEnter(args) { this.src = args[0]; this.on = current >= 0; },
      onLeave(ret) {
        if (!this.on) return;
        send({clone: current, src: this.src.add(0x300).readS32(), vt: this.src.readPointer().sub(mod.base).toInt32(),
              copy: ret.isNull() ? null : ret.add(0x300).readS32()});
      }
    });
  }
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
    p.add_argument("--clones", action="store_true", help="also log every node the collapse's clone (180f86a90) copies or skips")
    p.add_argument("--parents", action="store_true", help="also log every AddChild (180f75010): parent, child, old parent, caller")
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
    sc = ses.create_script(AGENT.replace("COLLAPSE_RVA", hex(installed("resourcecompiler", 0xf5ff40, build="20260923")))
                           .replace("CLONE_RVA", hex(installed("resourcecompiler", 0xf86a90, build="20260923")))
                           .replace("ADDCHILD_RVA", hex(installed("resourcecompiler", 0xf75010, build="20260923")))
                           .replace("PARENTS", "true" if a.parents else "false")
                           .replace("CLONES", "true" if a.clones else "false"))
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
