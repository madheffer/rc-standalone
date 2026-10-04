"""probe_origin.py <addon> <map> [--limit n] -- where the welded map meshes come from.

FUN_18020b230 copies each per-material piece (FUN_1812d66f0) before welding
it. This records every CMesh allocation (FUN_1812d6a90) with its call stack
and, for each copy made at that site, prints the source mesh's allocation
stack as resourcecompiler.dll RVAs.
"""
import argparse
import os
import sys
import threading

import frida
sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "re"))
from rva_map import require  # noqa: E402

CS2 = os.environ.get(
    "CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")

AGENT = r"""
'use strict';
const born = new Map();
const seen = new Set();
let shown = 0;
function hook() {
  const m = Process.findModuleByName('resourcecompiler.dll');
  if (m === null) { setTimeout(hook, 5); return; }
  const lo = m.base, hi = m.base.add(m.size);
  const stack = ctx => Thread.backtrace(ctx, Backtracer.ACCURATE)
    .filter(a => a.compare(lo) >= 0 && a.compare(hi) < 0)
    .map(a => '0x' + a.sub(lo).toString(16));
  Interceptor.attach(m.base.add(0x12d6a90), {
    onEnter(args) {
      born.set(args[0].toString(), {s: stack(this.context), nv: args[1].toInt32(), ni: args[2].toInt32(), stride: args[3].toInt32()});
    }
  });
  Interceptor.attach(m.base.add(0x12d66f0), {
    onEnter(args) {
      const at = this.returnAddress.sub(lo).toInt32();
      if (at !== 0x20b526 && at !== 0x20b52d) return;
      const src = born.get(args[1].toString());
      const key = src ? src.s.join(' ') : 'unknown ' + args[1];
      if (seen.has(key) || shown >= %(limit)d) return;
      seen.add(key);
      shown++;
      send({from: '0x' + at.toString(16), source: src || args[1].toString()});
    }
  });
  send({hooked: true});
}
hook();
"""


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("addon")
    p.add_argument("map")
    p.add_argument("--limit", type=int, default=6)
    a = p.parse_args()
    # Literal 0923 addresses below: refuse a build that moved them.
    require("resourcecompiler", [0x12d6a90, 0x12d66f0])
    vpk = os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    if os.path.exists(vpk):
        os.remove(vpk)
    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    argv = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4",
            "-game", os.path.join(CS2, "game", "csgo"), "-i", source, "-world", "-fshallow"]
    dev = frida.get_local_device()
    pid = dev.spawn(argv, cwd=BIN, stdio="pipe")
    ses = dev.attach(pid)
    sc = ses.create_script(AGENT % {"limit": a.limit})
    sc.on("message", lambda m, d: print(m.get("payload", m)))
    sc.load()
    done = threading.Event()
    ses.on("detached", lambda *x: done.set())
    dev.resume(pid)
    done.wait()


if __name__ == "__main__":
    main()
