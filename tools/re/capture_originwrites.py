"""capture_originwrites.py <addon> <map> <x> <y> <z> [--tol t] [--out file]: who writes a node origin.

Hooks resourcecompiler's node origin store (0923: 180fded80, (CMapNode *, Vector *)) during a -world -fshallow -phys
compile and records every call whose new origin lies within --tol (default 0.01) of (x, y, z): the value as float
bits and the call stack (return addresses), each frame given as the installed build's RVA and, where its function's
first 48 bytes occur once in the 0923 analysis build, the 0923 address of that function plus the offset. One JSON
line per hit. CS2 must be closed; the map's .vpk is backed up and restored.
"""
import argparse
import bisect
import json
import os
import struct
import sys
import threading
import time

import frida
import pefile

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from package_guard import PackageGuard  # noqa: E402
from rva_map import installed  # noqa: E402

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")
ANALYSIS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "binaries", "resourcecompiler_20260923.dll")

AGENT = r"""
'use strict';
const want = [%(x)s, %(y)s, %(z)s], tol = %(tol)s;
function hook() {
  const m = Process.findModuleByName('resourcecompiler.dll');
  if (m === null) { setTimeout(hook, 5); return; }
  send({ev: 'hooked'});
  Interceptor.attach(m.base.add(%(setter)d), {
    onEnter(args) {
      const p = args[1];
      const v = [p.readFloat(), p.add(4).readFloat(), p.add(8).readFloat()];
      if (Math.abs(v[0] - want[0]) > tol || Math.abs(v[1] - want[1]) > tol || Math.abs(v[2] - want[2]) > tol) return;
      const bits = [p.readU32(), p.add(4).readU32(), p.add(8).readU32()];
      const stack = Thread.backtrace(this.context, Backtracer.ACCURATE)
        .map(a => { const mm = Process.findModuleByAddress(a); return mm && mm.name === 'resourcecompiler.dll' ? a.sub(mm.base).toInt32() : -1; });
      send({ev: 'write', value: v, bits: bits, stack: stack});
    }
  });
}
hook();
"""


class Functions:
    """Function starts (.pdata) of a PE, and its mapped image."""

    def __init__(self, path):
        pe = pefile.PE(path, fast_load=True)
        pe.parse_data_directories([3])
        self.image = pe.get_memory_mapped_image()
        self.starts = sorted(r.struct.BeginAddress for r in pe.DIRECTORY_ENTRY_EXCEPTION)
        self.base = pe.OPTIONAL_HEADER.ImageBase

    def containing(self, rva):
        i = bisect.bisect_right(self.starts, rva) - 1
        return self.starts[i] if i >= 0 else None


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("addon")
    p.add_argument("map")
    p.add_argument("x", type=float)
    p.add_argument("y", type=float)
    p.add_argument("z", type=float)
    p.add_argument("--tol", type=float, default=0.01)
    p.add_argument("--out")
    a = p.parse_args()
    out_path = a.out or os.path.join(os.environ.get("TEMP", "."), "originwrites_%s.jsonl" % a.map)
    live = Functions(os.path.join(BIN, "resourcecompiler.dll"))
    old = Functions(ANALYSIS)

    def to_analysis(rva):
        if rva < 0:
            return None
        start = live.containing(rva)
        if start is None:
            return None
        head = live.image[start:start + 48]
        at = old.image.find(head)
        if at < 0 or old.image.find(head, at + 1) >= 0:
            return None
        return "%x+%x" % (old.base + at, rva - start)

    out = open(out_path, "w", encoding="utf-8")
    stats = {}

    def on_message(message, data):
        if message["type"] != "send":
            print("agent:", message.get("description") or message, file=sys.stderr)
            return
        payload = message["payload"]
        stats[payload["ev"]] = stats.get(payload["ev"], 0) + 1
        if payload["ev"] == "write":
            payload["analysis"] = [to_analysis(r) for r in payload["stack"]]
        out.write(json.dumps(payload) + "\n")

    setter = installed("resourcecompiler", 0xfded80)
    vpk = os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    with PackageGuard(vpk, out_path + ".vpk.bak") as guard:
        source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
        argv = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"),
                "-i", source, "-world", "-fshallow", "-phys"]
        device = frida.get_local_device()
        pid = device.spawn(argv, cwd=BIN, stdio="pipe")
        guard.pid = pid
        session = device.attach(pid)
        script = session.create_script(AGENT % {"x": repr(a.x), "y": repr(a.y), "z": repr(a.z), "tol": repr(a.tol), "setter": setter})
        script.on("message", on_message)
        script.load()
        done = threading.Event()
        session.on("detached", lambda *x: done.set())
        started = time.time()
        device.resume(pid)
        done.wait()
        out.close()
        print("%.1fs  %s  -> %s" % (time.time() - started, stats, out_path))


if __name__ == "__main__":
    main()
