"""probe_stack.py <addon> <map> <rva hex> [--limit n] [--js cond] -- who calls a function.

Runs a compile under Frida and prints the call stack (resourcecompiler.dll
RVAs) of each call to the function at <rva>, the first --limit distinct
stacks. --js is a JavaScript condition on `args` (e.g. "args[3].toInt32() == 8")
that a call must meet to count.
"""
import argparse
import os
import sys
import threading

import frida

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from package_guard import PackageGuard  # noqa: E402

CS2 = os.environ.get(
    "CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")

AGENT = r"""
'use strict';
const seen = new Set();
let shown = 0;
function hook() {
  const m = Process.findModuleByName('resourcecompiler.dll');
  if (m === null) { setTimeout(hook, 5); return; }
  const lo = m.base, hi = m.base.add(m.size);
  Interceptor.attach(m.base.add(%(rva)d), {
    onEnter(args) {
      if (shown >= %(limit)d || !(%(cond)s)) return;
      const frames = Thread.backtrace(this.context, Backtracer.ACCURATE)
        .filter(a => a.compare(lo) >= 0 && a.compare(hi) < 0)
        .map(a => '0x' + a.sub(lo).toString(16));
      const key = frames.join(' ');
      if (seen.has(key)) return;
      seen.add(key);
      shown++;
      send({frames: frames, a1: args[1].toString(), a2: args[2].toString(), a3: args[3].toString()});
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
    p.add_argument("rva")
    p.add_argument("--limit", type=int, default=10)
    p.add_argument("--js", default="true")
    a = p.parse_args()
    vpk = os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    # Moved aside (a current package makes RC skip the compile) and put back,
    # byte checked, once the compile has exited (tools/package_guard.py).
    with PackageGuard(vpk, os.path.join(os.environ.get("TEMP", "."), "package_guard", "%s__%s.vpk.bak" % (a.addon, a.map))) as guard:
        source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
        argv = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4",
                "-game", os.path.join(CS2, "game", "csgo"), "-i", source, "-world", "-fshallow"]
        dev = frida.get_local_device()
        pid = dev.spawn(argv, cwd=BIN, stdio="pipe")
        guard.pid = pid
        ses = dev.attach(pid)
        sc = ses.create_script(AGENT % {"rva": int(a.rva, 16), "limit": a.limit, "cond": a.js})
        sc.on("message", lambda m, d: print(m.get("payload", m)))
        sc.load()
        done = threading.Event()
        ses.on("detached", lambda *x: done.set())
        dev.resume(pid)
        done.wait()


if __name__ == "__main__":
    main()
