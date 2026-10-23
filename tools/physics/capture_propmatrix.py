"""capture_propmatrix.py <addon> <map> [--out file]: the angles and origin physicsbuilder builds each prop matrix from.

physicsbuilder's PropMatrix_Angles (0924: 1800b5a50, (QAngle *angles, Vector *origin, matrix3x4 *out)) is what
CreateStaticPropShapesForPropInstance (1800174e0) calls for a prop instance. This records every call made from
there: the angles and origin as float bits, and the matrix it returns, one JSON line each. The compile is -world
-fshallow -phys with the map's .vpk moved aside and restored (tools/package_guard.py). CS2 must be closed.
"""
import argparse
import json
import os
import sys
import threading
import time

import frida

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "re"))
from package_guard import PackageGuard  # noqa: E402
from rva_map import installed  # noqa: E402

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")

AGENT = r"""
'use strict';
function bits(p, n) { const r = []; for (let i = 0; i < n; i++) r.push(p.add(4 * i).readU32()); return r; }
function hook() {
  const m = Process.findModuleByName('physicsbuilder.dll');
  if (m === null) { setTimeout(hook, 5); return; }
  const caller = m.base.add(%(caller_lo)d), callerEnd = m.base.add(%(caller_hi)d);
  send({ev: 'hooked'});
  Interceptor.attach(m.base.add(%(propmatrix)d), {
    onEnter(args) {
      const r = this.returnAddress;
      this.on = r.compare(caller) >= 0 && r.compare(callerEnd) < 0;
      if (!this.on) return;
      this.out = args[2];
      this.rec = {ev: 'prop', angles: bits(args[0], 3), origin: bits(args[1], 3)};
    },
    onLeave(ret) {
      if (!this.on) return;
      this.rec.matrix = bits(this.out, 12);
      send(this.rec);
    }
  });
}
hook();
"""


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("addon")
    parser.add_argument("map")
    parser.add_argument("--out")
    args = parser.parse_args()
    out_path = args.out or os.path.join(os.environ.get("TEMP", "."), "propmatrix_%s.jsonl" % args.map)
    out = open(out_path, "w", encoding="utf-8")
    lock = threading.Lock()
    stats = {}

    def on_message(message, data):
        if message["type"] != "send":
            print("agent:", message.get("description") or message, file=sys.stderr)
            return
        payload = message["payload"]
        stats[payload["ev"]] = stats.get(payload["ev"], 0) + 1
        with lock:
            out.write(json.dumps(payload) + "\n")

    # CreateStaticPropShapesForPropInstance spans 1800174e0 .. its next function; the call sits at 180017549.
    rva = {"propmatrix": installed("physicsbuilder", 0xb5a50, build="20260924"),
           "caller_lo": installed("physicsbuilder", 0x174e0, build="20260924"),
           "caller_hi": installed("physicsbuilder", 0x174e0, build="20260924") + 0x400}
    vpk = os.path.join(CS2, "game", "csgo_addons", args.addon, "maps", args.map + ".vpk")
    with PackageGuard(vpk, out_path + ".vpk.bak") as guard:
        source = os.path.join(CS2, "content", "csgo_addons", args.addon, "maps", args.map + ".vmap")
        argv = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"),
                "-i", source, "-world", "-fshallow", "-phys"]
        device = frida.get_local_device()
        pid = device.spawn(argv, cwd=BIN, stdio="pipe")
        guard.pid = pid
        session = device.attach(pid)
        script = session.create_script(AGENT % rva)
        script.on("message", on_message)
        script.load()
        done = threading.Event()
        session.on("detached", lambda *a: done.set())
        started = time.time()
        device.resume(pid)
        done.wait()
        out.close()
        print("%.1fs  %s  -> %s" % (time.time() - started, stats, out_path))


if __name__ == "__main__":
    main()
