"""capture_subdivgrids.py <addon> <map> [--out file]: the subdivision bake's patch normal and tangent grids (ledger 38).

The bake gives each face-vertex stream of mode 3 (the tangent) the patch's tangent grid (1813c1cc0 -> 1813c2090,
0923 addresses) and that grid reads the patch's normal grid (1813c3cf0). Runs a -world -fshallow compile under
Frida and records, per call:

  normal   1813c3cf0(mesh, &halfedge, positions, count, x, out): on leave, the half-edge (four dwords), the count,
           the position grid (count x 3 floats) and the normal grid written (count x 3 floats)
  tangent  1813c2090(mesh, &halfedge, normals, uv, out, count): on entry the normal grid and the (u, v) pairs, on
           leave the tangent grid (count x 4 floats)
  gather   1813bdf80(mesh, &halfedge, stream, out): on leave the patch's four tangents (12 floats: the corner's,
           next and previous halves, the mean), as the tangent grid reads them

The mesh pointer and a per-kind call index join the records. Floats are hex bits. Addresses go through
tools/re/rva_map.py, so a changed function refuses to hook. CS2 must be closed; the map's package is backed up and
restored, cmp-checked, afterwards.
"""
import argparse
import filecmp
import json
import os
import shutil
import subprocess
import sys
import threading

import frida

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "re"))
from rva_map import installed  # noqa: E402

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")

AGENT = r"""
'use strict';
function words(p, n) {
  const out = [];
  const w = new Uint32Array(p.readByteArray(n * 4));
  for (const x of w) out.push(x.toString(16).padStart(8, '0'));
  return out;
}
let nn = 0, nt = 0;
function hook() {
  const mod = Process.findModuleByName('resourcecompiler.dll');
  if (mod === null) { setTimeout(hook, 5); return; }
  Interceptor.attach(mod.base.add(%(normal)d), {
    onEnter(args) {
      this.mesh = args[0]; this.he = words(args[1], 4); this.pos = args[2];
      this.count = args[3].toInt32(); this.out = args[5]; this.call = nn++;
    },
    onLeave() {
      send({kind: 'normal', call: this.call, mesh: this.mesh.toString(), he: this.he, count: this.count,
            positions: words(this.pos, this.count * 3), normals: words(this.out, this.count * 3)});
    }
  });
  Interceptor.attach(mod.base.add(%(tangent)d), {
    onEnter(args) {
      this.mesh = args[0]; this.he = words(args[1], 4); this.out = args[4];
      this.count = args[5].toInt32(); this.call = nt++;
      this.normals = words(args[2], this.count * 3); this.uv = words(args[3], this.count * 2);
    },
    onLeave() {
      send({kind: 'tangent', call: this.call, mesh: this.mesh.toString(), he: this.he, count: this.count,
            normals: this.normals, uv: this.uv, tangents: words(this.out, this.count * 4)});
    }
  });
  Interceptor.attach(mod.base.add(%(gather)d), {
    onEnter(args) { this.mesh = args[0]; this.he = words(args[1], 4); this.out = args[3]; },
    onLeave() { send({kind: 'gather', mesh: this.mesh.toString(), he: this.he, corners: words(this.out, 12)}); }
  });
  send({hooked: true});
}
hook();
"""


def running(name):
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq " + name], capture_output=True, text=True).stdout
    return name.lower() in out.lower()


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("addon")
    p.add_argument("map")
    p.add_argument("--out")
    a = p.parse_args()
    if running("cs2.exe"):
        raise SystemExit("CS2 is running; not starting")
    if running("resourcecompiler.exe"):
        raise SystemExit("another resourcecompiler is running; not starting")
    rvas = {"normal": installed("resourcecompiler", 0x13c3cf0), "tangent": installed("resourcecompiler", 0x13c2090),
            "gather": installed("resourcecompiler", 0x13bdf80)}
    out_path = a.out or os.path.join(os.environ.get("TEMP", "."), "subdivgrids_capture", a.map + ".jsonl")
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    vpk = os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    backup = out_path + ".vpk.bak"
    if os.path.exists(vpk):
        shutil.copyfile(vpk, backup)
        os.remove(vpk)
    out = open(out_path, "w")
    lock = threading.Lock()

    def on_message(msg, _data):
        if msg.get("type") != "send":
            print("agent:", msg.get("description") or msg)
        elif "kind" in msg["payload"]:
            with lock:
                out.write(json.dumps(msg["payload"]) + "\n")

    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    argv = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"),
            "-i", source, "-world", "-fshallow"]
    dev = frida.get_local_device()
    pid = dev.spawn(argv, cwd=BIN, stdio="pipe")
    ses = dev.attach(pid)
    sc = ses.create_script(AGENT % rvas)
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
    print(out_path)
    if os.path.exists(backup):
        shutil.copyfile(backup, vpk)
        ok = filecmp.cmp(backup, vpk, shallow=False)
        print("restored", vpk, "(cmp ok)" if ok else "(CMP FAILED)")
        if ok:
            os.remove(backup)


if __name__ == "__main__":
    main()
