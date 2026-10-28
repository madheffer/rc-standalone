"""capture_entitylist.py <addon> <map> [--out file.json] [--switches "-world -phys -nav"]: the map document's entity
list (doc +0x1828 count, +0x1830 CMapEntity* array), which FUN_180fc7220's search walks in stored order.

Snapshots, each entry as node id (+0x300), vtable RVA and, for CMapNavLink entries (vtable of the first one the
collector's search returns), locatoroffset (+0x185c):
  links                    the nav link collector (0923 180f203b0; 1023 180f21570), param_2 the CMapWorld that owns
                           the list (180fca3d0 adds the world itself at +0x1828).
And the list's edits in order, as events: add (OnNodeAdded 0923 180fca010, 1023 180fcb1d0: an entity node not yet
listed goes to the tail) and remove (0923 180fcb150, 1023 180fcc310: the last entry moves into the slot), each with
the node id and the list count before; bake_enter / bake_leave (MapDoc_BakeInstances 0923 180f60740, 1023
180f61900) as markers.
Installed build 20261023 only (port_addr.py from 0923). CS2 must be closed; the map's package is backed up and
restored as capture_navgen.py does.
"""
import argparse
import filecmp
import json
import os
import shutil
import subprocess
import threading

import frida

from capture_navgen import BIN, CS2, running

BAKE, LINKS, ADD, REMOVE = 0xf61900, 0xf21570, 0xfcb1d0, 0xfcc310

AGENT = r"""
'use strict';
function snap(tag, doc) {
  const n = doc.add(0x1828).readS32(), arr = doc.add(0x1830).readPointer(), rc = Process.findModuleByName('resourcecompiler.dll');
  const out = [];
  for (let i = 0; i < n; i++) {
    const e = arr.add(i * 8).readPointer();
    if (e.isNull()) { out.push(null); continue; }
    const vt = e.readPointer();
    const r = {id: e.add(0x300).readU32(), vt: vt.sub(rc.base).toInt32()};
    try { r.off = [e.add(0x185c).readFloat(), e.add(0x1860).readFloat(), e.add(0x1864).readFloat()]; } catch (x) {}
    out.push(r);
  }
  send({tag: tag, list: out});
}
function hook() {
  const rc = Process.findModuleByName('resourcecompiler.dll');
  if (rc === null) { setTimeout(hook, 5); return; }
  const events = [];
  const ev = (kind, world, node) => {
    let id = null, count = null;
    try { id = node.isNull() ? null : node.add(0x300).readU32(); } catch (x) {}
    try { count = world.add(0x1828).readS32(); } catch (x) {}
    events.push([kind, id, count]);
  };
  Interceptor.attach(rc.base.add(ADD), { onEnter(args) { ev('add', args[0], args[1]); } });
  Interceptor.attach(rc.base.add(REMOVE), { onEnter(args) { ev('remove', args[0], args[1]); } });
  Interceptor.attach(rc.base.add(BAKE), {
    onEnter() { events.push(['bake_enter', null, null]); },
    onLeave() { events.push(['bake_leave', null, null]); } });
  Interceptor.attach(rc.base.add(LINKS), { onEnter(args) { snap('links', args[1]); send({tag: 'events', list: events}); } });
  Interceptor.attach(rc.base.add(LINKS), { onEnter(args) { snap('links', args[1]); } });
  send({hooked: true});
}
hook();
"""


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("addon")
    p.add_argument("map")
    p.add_argument("--out")
    p.add_argument("--switches", default="-world -phys -nav")
    a = p.parse_args()
    if running("cs2.exe"):
        raise SystemExit("CS2 is running; not starting")
    if running("resourcecompiler.exe"):
        raise SystemExit("another resourcecompiler is running; not starting")
    out = a.out or os.path.join(os.environ.get("TEMP", "."), "navgen_capture", a.map + "_entitylist.json")
    vpk = os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    backup = out + ".vpk.bak"
    if os.path.exists(vpk):
        shutil.copy2(vpk, backup)
    snaps, hooked = [], threading.Event()

    def on_message(msg, data):
        if msg.get("type") != "send":
            print("agent:", msg.get("description") or msg)
        elif msg["payload"].get("hooked"):
            hooked.set()
        else:
            snaps.append(msg["payload"])

    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    argv = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"),
            "-i", source] + a.switches.split()
    dev = frida.get_local_device()
    pid = dev.spawn(argv, cwd=BIN, stdio="pipe")
    ses = dev.attach(pid)
    sc = ses.create_script(AGENT.replace("BAKE", hex(BAKE)).replace("LINKS", hex(LINKS)).replace("ADD", hex(ADD)).replace("REMOVE", hex(REMOVE)))
    sc.on("message", on_message)
    sc.load()
    done = threading.Event()
    ses.on("detached", lambda *x: done.set())
    dev.resume(pid)
    while not done.wait(2):
        if running("cs2.exe"):
            subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True)
    json.dump(snaps, open(out, "w"), indent=0)
    print(out, [(s["tag"], len(s["list"])) for s in snaps], "(hooked)" if hooked.is_set() else "(NEVER HOOKED)")
    if os.path.exists(backup):
        shutil.copy2(backup, vpk)
        ok = filecmp.cmp(backup, vpk, shallow=False)
        print("restored", vpk, "(cmp ok)" if ok else "(CMP FAILED)")
        if ok:
            os.remove(backup)


if __name__ == "__main__":
    main()
