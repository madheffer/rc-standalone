"""capture_subdivtangents.py <addon> <map> [--out file]: where subdivided meshes' tangents change (ledger 38).

Runs a -world -fshallow compile under Frida and snapshots each map mesh's
normal and tangent face-vertex streams at four points:

  enter   BakeSubdivisionForFaces (0923 1810c65c0), before anything is baked
  bake    on leaving SubdivisionBake_Bake (1813baa40): split, written back
  baked   on leaving BakeSubdivisionForFaces: merged, normals recomputed
  world   entering HammerMesh_TransformToWorld (1810c4e50), as the builder
          moves the mesh to the world (ConvertMeshForBuilder's last step)

One JSON line per snapshot: the stage, the mesh pointer (to join the
stages), a per-stage call index, the corner count and both streams as hex
float bits. Addresses go through tools/re/rva_map.py, so a changed function
refuses to hook instead of hooking the wrong code.

CS2 must be closed and 10 GB free; the map's package is backed up first and
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

CS2 = os.environ.get(
    "CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")

AGENT = r"""
'use strict';
const counts = {};
// Normal (+0x10da, 12 bytes a corner) and tangent (+0x10dc, 16 bytes) streams:
// a handle's high byte picks a slot whose data sits at +0x1330 / +0x1430 +
// slot * 0x18; +0x1110 counts the corners (capture_meshturn.py's reading).
function stream(mesh, handle, base, size) {
  const slot = mesh.add(handle).add(1).readS8();
  if (slot < 0 || slot >= 6) return null;
  const p = mesh.add(base + slot * 0x18).readPointer();
  if (p.isNull()) return null;
  const count = mesh.add(0x1110).readS32();
  const words = [];
  for (const w of new Uint32Array(p.readByteArray(count * size)))
    words.push(w.toString(16).padStart(8, '0'));
  return words;
}
function snap(stage, mesh) {
  counts[stage] = (counts[stage] || 0) + 1;
  send({stage: stage, call: counts[stage] - 1, mesh: mesh.toString(), corners: mesh.add(0x1110).readS32(),
        normal: stream(mesh, 0x10da, 0x1330, 12), tangent: stream(mesh, 0x10dc, 0x1430, 16)});
}
function hook() {
  const mod = Process.findModuleByName('resourcecompiler.dll');
  if (mod === null) { setTimeout(hook, 5); return; }
  Interceptor.attach(mod.base.add(%(bake_faces)d), {
    onEnter(args) { this.mesh = args[0]; snap('enter', this.mesh); },
    onLeave() { snap('baked', this.mesh); }
  });
  const depth = {};  // per thread: only the outermost bake, should it recurse
  Interceptor.attach(mod.base.add(%(bake)d), {
    onEnter(args) { this.mesh = args[0]; depth[this.threadId] = (depth[this.threadId] || 0) + 1; },
    onLeave() { if (--depth[this.threadId] === 0) snap('bake', this.mesh); }
  });
  Interceptor.attach(mod.base.add(%(to_world)d), {
    onEnter(args) { snap('world', args[0]); }
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
    if shutil.disk_usage(os.path.splitdrive(CS2)[0] + "\\").free < 10 * 1024 ** 3:
        raise SystemExit("under 10 GB free on the game drive; not starting")
    rvas = {"bake_faces": installed("resourcecompiler", 0x10c65c0),
            "bake": installed("resourcecompiler", 0x13baa40),
            "to_world": installed("resourcecompiler", 0x10c4e50)}
    out_path = a.out or os.path.join(os.environ.get("TEMP", "."), "subdivtangent_capture", a.map + ".jsonl")
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    vpk = os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    backup = out_path + ".vpk.bak"
    if os.path.exists(vpk):
        shutil.copyfile(vpk, backup)
        os.remove(vpk)
    out = open(out_path, "w")
    lock = threading.Lock()
    hooked = threading.Event()

    def on_message(msg, _data):
        if msg.get("type") != "send":
            print("agent:", msg.get("description") or msg)
        elif msg["payload"].get("hooked"):
            hooked.set()
        else:
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
    print(out_path, "(hooked)" if hooked.is_set() else "(NEVER HOOKED)")
    if os.path.exists(backup):
        shutil.copyfile(backup, vpk)
        ok = filecmp.cmp(backup, vpk, shallow=False)
        print("restored", vpk, "(cmp ok)" if ok else "(CMP FAILED)")
        if ok:
            os.remove(backup)


if __name__ == "__main__":
    main()
