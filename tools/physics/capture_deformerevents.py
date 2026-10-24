"""capture_deformerevents.py <addon> <map> [--out file]: the order in which a compile moves, recentres and refits
path deformers.

Hooks, during a -world -phys -fshallow compile, these resourcecompiler functions (0923 addresses, mapped to the
installed build by rva_map) and writes one JSON line per entry and per exit, in call order with the nesting depth:
- 18117aeb0 CMapDeformerPath_Recentre, 18117a900 _Update, 181178ce0 _ReferenceChanged,
  181178b00 _CopyFrom: the deformer, its origin and angles, and the node its targetPath resolves to (+0x548 + 0x28)
  with that node's origin;
- 181076410 CMapDeformer_TransformBy, 181076850 the deformer's subtree move and 180fded80 the node origin store (the
  value stored in `to`): the node and its origin before and after;
- 1810191b0 CMapPrefab_SetPlaced, 181019d00 CMapPrefab_PlaceGroup, 180f96d40 CMapPrefab_Collapse, 180f834d0 the
  group transform setter: the node and its origin;
- 181288080 PropDeformer_Transform: the lattice transform's position.
Each node is given by pointer, nodeID (+0x300) and vtable RVA. CS2 must be closed; the map's package is backed up
and restored.
"""
import argparse
import json
import os
import shutil
import subprocess
import sys
import threading

import frida

sys.path.insert(0, os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "re"))
from rva_map import installed  # noqa: E402

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")
HOOKS = {
    "recentre": 0x117aeb0, "update": 0x117a900, "refchanged": 0x1178ce0, "copyfrom": 0x1178b00,
    "transformby": 0x1076410, "setorigin": 0x0fded80, "subtree": 0x1076850, "setplaced": 0x10191b0, "placegroup": 0x1019d00,
    "collapse": 0x0f96d40, "groupset": 0x0f834d0, "deform": 0x1288080,
}
AGENT = r"""
'use strict';
const HOOKS = %s;
let depth = 0, seq = 0;
function hook() {
  const m = Process.findModuleByName('resourcecompiler.dll');
  if (m === null) { setTimeout(hook, 5); return; }
  function v3(p) { return [p.readFloat(), p.add(4).readFloat(), p.add(8).readFloat()]; }
  function node(p) {
    if (p.isNull()) return null;
    try {
      return {p: p.toString(), id: p.add(0x300).readS32(), vt: p.readPointer().sub(m.base).toInt32(), o: v3(p.add(0xa0)), a: v3(p.add(0xac))};
    } catch (e) { return {p: p.toString(), bad: true}; }
  }
  function target(d) {
    try { const t = d.add(0x548 + 0x28).readPointer(); return node(t); } catch (e) { return null; }
  }
  for (const name of Object.keys(HOOKS)) {
    Interceptor.attach(m.base.add(HOOKS[name]), {
      onEnter(args) {
        this.name = name;
        this.n = args[0];
        const r = {s: seq++, d: depth, ev: name, at: 'in', caller: this.returnAddress.sub(m.base).toInt32()};
        if (name === 'deform') {
          r.lattice = v3(args[0]);
        } else {
          r.node = node(args[0]);
          if (['recentre', 'update', 'refchanged', 'copyfrom'].includes(name)) r.target = target(args[0]);
          if (name === 'copyfrom') { r.from = node(args[1]); r.flags = args[2].toInt32(); }
          if (name === 'setplaced') r.placed = args[1].toInt32() & 0xff;
          if (name === 'placegroup' || name === 'groupset') { try { r.to = v3(name === 'groupset' ? args[3] : args[1]); } catch (e) {} }
          if (name === 'setorigin') { try { r.to = v3(args[1]); } catch (e) {} }
          if (name === 'subtree') { try { r.pivot = v3(args[1]); } catch (e) {} }
        }
        send(r);
        depth++;
      },
      onLeave() {
        depth--;
        if (this.name === 'deform') return;
        const r = {s: seq++, d: depth, ev: this.name, at: 'out', node: node(this.n)};
        if (['recentre', 'update', 'refchanged', 'copyfrom'].includes(this.name)) r.target = target(this.n);
        send(r);
      }
    });
  }
  send({ev: 'hooked'});
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
    out_path = a.out or os.path.join(os.environ.get("TEMP", "."), "deformer_capture", a.map + ".events.jsonl")
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    vpk = os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    backup = out_path + ".vpk.bak"
    if os.path.exists(vpk):
        shutil.copyfile(vpk, backup)
        os.remove(vpk)
    hooks = {k: installed("resourcecompiler", v, build="20260923") for k, v in HOOKS.items()}
    out = open(out_path, "w", encoding="utf-8")
    lock = threading.Lock()
    count = [0]

    def on_message(msg, data):
        if msg.get("type") != "send":
            print("agent:", msg)
            return
        with lock:
            out.write(json.dumps(msg["payload"]) + "\n")
            count[0] += 1

    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    dev = frida.get_local_device()
    pid = dev.spawn([os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"),
                     "-i", source, "-world", "-phys", "-fshallow"], cwd=BIN, stdio="pipe")
    ses = dev.attach(pid)
    sc = ses.create_script(AGENT % json.dumps(hooks))
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
    with lock:
        out.close()
    if os.path.exists(backup):
        shutil.copyfile(backup, vpk)
        os.remove(backup)
    print(out_path, count[0], "events")


if __name__ == "__main__":
    main()
