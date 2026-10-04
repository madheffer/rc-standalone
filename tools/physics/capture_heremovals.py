"""capture_heremovals.py <addon> <map> -- who removes half-edge elements during a -world -phys build.

HalfEdge_ContainerRemove (resourcecompiler 0923: 1813995a0) frees a vertex,
edge or face handle of a half-edge mesh. This tallies its calls by the
caller chain (six return addresses, as rc 0923 RVAs where they map), so the
step that drops faces from a mesh before the export (ledger 49: the
error-model copies' 7 slivers) can be named.

The .vpk is moved aside for the compile and put back after. CS2 must be closed.
"""
import argparse
import collections
import os
import shutil
import sys
import threading

import frida

from capture_physshapes import BIN, CS2, busy, low_disk
sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "re"))
from rva_map import installed  # noqa: E402

AGENT = r"""
'use strict';
function hook(m) {
  Interceptor.attach(m.base.add(%(site)d), {
    onEnter() {
      const bt = Thread.backtrace(this.context, Backtracer.ACCURATE).slice(0, 6).map(a => {
        const mod = Process.findModuleByAddress(a);
        return mod ? mod.name.replace('.dll', '') + '+0x' + a.sub(mod.base).toString(16) : a.toString();
      });
      send({bt: bt.join(' ')});
    }
  });
  send({hooked: m.base.toString()});
}
const found = Process.findModuleByName('resourcecompiler.dll');
if (found) hook(found);
else Process.attachModuleObserver({
  onAdded(m) { if (m.name.toLowerCase() === 'resourcecompiler.dll') hook(m); }
});
"""


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("addon")
    p.add_argument("map")
    a = p.parse_args()
    site = installed("resourcecompiler", 0x13995a0)
    if busy():
        raise SystemExit("CS2 or another resourcecompiler is running; not starting")
    if low_disk():
        raise SystemExit("under 10 GB free on the game drive; not starting")
    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    vpk = os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    if os.path.exists(vpk):
        shutil.move(vpk, vpk + ".capture_backup")
    counts = collections.Counter()
    lock = threading.Lock()

    def on_message(msg, data):
        if msg.get("type") != "send":
            print(msg, flush=True)
            return
        pay = msg["payload"]
        if "bt" in pay:
            with lock:
                counts[pay["bt"]] += 1
        else:
            print(pay, flush=True)

    try:
        dev = frida.get_local_device()
        pid = dev.spawn([os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"),
                         "-i", source, "-world", "-phys", "-fshallow"], cwd=BIN, stdio="pipe")
        ses = dev.attach(pid)
        sc = ses.create_script(AGENT % {"site": site})
        sc.on("message", on_message)
        sc.load()
        done = threading.Event()
        ses.on("detached", lambda *x: done.set())
        dev.resume(pid)
        done.wait()
    finally:
        if os.path.exists(vpk + ".capture_backup"):
            shutil.move(vpk + ".capture_backup", vpk)
    for bt, n in counts.most_common(30):
        print(n, bt)


if __name__ == "__main__":
    main()
