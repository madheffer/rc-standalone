"""Name the map nodes that hold given node ids when the instance bake starts.

At the start of FUN_180f60740 the map is loaded and upgraded but no instance
is collapsed yet, so any node id above the file's own maximum belongs to a node
the loader created. This scans memory for each id at a map node's id field
(+0x300) whose object starts with a resourcecompiler.dll vtable, and reports
the class from the vtable's RTTI, then stops the compile.

    python find_new_nodes.py <addon> <map> <first id> <last id>

Never run it with CS2 open.
"""
import json
import os
import subprocess
import sys
import threading

import frida

CS2 = os.environ.get(
    "CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")
BAKE_ALL_RVA = 0xf60740

AGENT = r"""
'use strict';
function rtti(vt, m) {
  try {
    const col = vt.sub(8).readPointer();
    const td = m.base.add(col.add(12).readU32());
    return td.add(16).readCString();
  } catch (e) { return '?'; }
}
function hook(m) {
  const lo = m.base, hi = m.base.add(m.size);
  Interceptor.attach(m.base.add(%(all)d), {
    onEnter() {
      const found = [];
      const ranges = Process.enumerateRanges('rw-');
      for (let id = %(first)d; id <= %(last)d; id++) {
        const b = [id & 255, (id >> 8) & 255, (id >> 16) & 255, (id >>> 24) & 255]
          .map(x => ('0' + x.toString(16)).slice(-2)).join(' ');
        for (const r of ranges) {
          let hits;
          try { hits = Memory.scanSync(r.base, r.size, b); } catch (e) { continue; }
          for (const h of hits) {
            const obj = h.address.sub(0x300);
            let vt;
            try { vt = obj.readPointer(); } catch (e) { continue; }
            if (vt.compare(lo) >= 0 && vt.compare(hi) < 0)
              found.push({id: id, obj: obj.toString(), cls: rtti(vt, m)});
          }
        }
      }
      send({ev: 'nodes', found: found});
      send({ev: 'done'});
    }
  });
}
const f = Process.findModuleByName('resourcecompiler.dll');
if (f) hook(f);
else Process.attachModuleObserver({ onAdded(m) { if (m.name.toLowerCase() === 'resourcecompiler.dll') hook(m); } });
"""


def main():
    addon, map_name, first, last = sys.argv[1], sys.argv[2], int(sys.argv[3]), int(sys.argv[4])
    if "cs2.exe" in subprocess.run(["tasklist"], capture_output=True, text=True).stdout.lower():
        sys.exit("cs2.exe is running; captures never run beside the game.")
    source = None
    for root, _, files in os.walk(os.path.join(CS2, "content", "csgo_addons", addon, "maps")):
        if map_name + ".vmap" in files:
            source = os.path.join(root, map_name + ".vmap")
            break
    done = threading.Event()

    def on_message(message, data):
        if message["type"] != "send":
            print("agent:", message.get("description") or message, file=sys.stderr)
            return
        if message["payload"]["ev"] == "nodes":
            for n in message["payload"]["found"]:
                print(json.dumps(n))
        else:
            done.set()

    argv = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-f",
            "-game", os.path.join(CS2, "game", "csgo"), "-i", source]
    device = frida.get_local_device()
    pid = device.spawn(argv, cwd=BIN, stdio="pipe")
    session = device.attach(pid)
    script = session.create_script(AGENT % {"all": BAKE_ALL_RVA, "first": first, "last": last})
    script.on("message", on_message)
    script.load()
    session.on("detached", lambda *a: done.set())
    device.resume(pid)
    done.wait()
    try:
        device.kill(pid)
    except frida.ProcessNotFoundError:
        pass


if __name__ == "__main__":
    main()
