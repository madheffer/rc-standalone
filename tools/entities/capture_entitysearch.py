"""capture_entitysearch.py <addon> <map> [--out file] [--switches "..."]: every entity search, in order.

Hooks MapWorld_FindEntitiesByClass's worker (resourcecompiler 180fc7220 in the 09-23 analysis build,
180fc7790 in the installed 10-15 build: world, out list, class name, mode, flags) during a compile.
The first search in each world also logs that world's whole flat node array (+0x1830, node ids in
array order). Each call logs one JSON line when it returns: the call's index, the class name searched, the flags,
the caller (resourcecompiler.dll offset), the world's own node count (+0x1828), and the node ids
(+0x300) of the entities in the out list (CUtlVector: count +0, data +8) in the order found. The
scene build's light collection, the nav seeds and the rest read their order from here (L17).
CS2 must be closed; the map's package is backed up and restored.
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
let n = 0;
const seen = {};
function hook() {
  const mod = Process.findModuleByName('resourcecompiler.dll');
  if (mod === null) { setTimeout(hook, 5); return; }
  // CMapNode::SetIndexInWorld (180ff00c0 in 10-15: node, index -> node +0x498): every change to a
  // world's flat node array, as [node id (+0x300), index] in call order (index -1: removed).
  const sets = [];
  Interceptor.attach(mod.base.add(0xff00c0), { onEnter(args) {
    let id = null;
    try { id = args[0].add(0x300).readS32(); } catch (e) {}
    sets.push([id, args[1].toInt32()]);
    if (sets.length >= 4096) send({sets: sets.splice(0, sets.length)});
  } });
  Interceptor.attach(mod.base.add(0xfc7790), {
    onEnter(args) {
      if (sets.length) send({sets: sets.splice(0, sets.length)});
      this.world = args[0];
      this.list = args[1];
      this.start = args[1].readS32();
      let cls = null;
      try { cls = args[2].readCString(); } catch (e) {}
      this.rec = {call: n++, cls: cls, mode: args[3].toInt32(), caller: '0x' + this.returnAddress.sub(mod.base).toString(16)};
      try { this.rec.worldNodes = args[0].add(0x1828).readS32(); } catch (e) {}
      // The first search of a world: its whole flat node array (+0x1830), node ids (+0x300) in order.
      try {
        const key = args[0].toString();
        if (!seen[key]) {
          seen[key] = true;
          const count = args[0].add(0x1828).readS32();
          const arr = args[0].add(0x1830).readPointer();
          const nodes = [];
          for (let i = 0; i < count; i++) {
            const p = arr.add(i * 8).readPointer();
            nodes.push(p.isNull() ? null : p.add(0x300).readS32());
          }
          send({world: key, nodes: nodes});
        }
      } catch (e) {}
    },
    onLeave(ret) {
      const count = this.list.readS32();
      const data = this.list.add(8).readPointer();
      const ids = [];
      for (let i = this.start; i < count; i++) {
        try { ids.push(data.add(i * 8).readPointer().add(0x300).readS32()); } catch (e) { ids.push(null); }
      }
      this.rec.ids = ids;
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
    p.add_argument("--switches", default="-world -phys -fshallow")
    a = p.parse_args()
    if running("cs2.exe") or running("resourcecompiler.exe"):
        raise SystemExit("CS2 or a compile is running; not starting")
    out_path = a.out or os.path.join(os.environ.get("TEMP", "."), "entitysearch_capture", a.map + ".jsonl")
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
                     "-i", source] + a.switches.split(), cwd=BIN, stdio="pipe")
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
    if os.path.exists(backup):
        shutil.copyfile(backup, vpk)
        ok = filecmp.cmp(backup, vpk, shallow=False)
        print("restored" if ok else "RESTORE CMP FAILED", vpk)
        if ok:
            os.remove(backup)


if __name__ == "__main__":
    main()
