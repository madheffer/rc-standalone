"""capture_meshturn.py <addon> <map> [--out file]: the matrix each map mesh is moved to the world by.

Runs a -world -fshallow compile under Frida and records the second argument
of HammerMesh_TransformToWorld (resourcecompiler 1810c4e50 in the 09-23
analysis build, +0x370 in the installed 10-01 build): the 3x4 matrix the
mesh's positions, normals and tangents go through (ledger 37). One JSON line
per call: the call index and the twelve floats as hex bits, so a -0 stays
visible.

CS2 must be closed and 10 GB free; the map's package is backed up first and
restored, cmp-checked, afterwards.
"""
import argparse
import filecmp
import json
import os
import shutil
import subprocess
import threading

import frida

CS2 = os.environ.get(
    "CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")
TRANSFORM_TO_WORLD = 0x10c51c0  # 10-01 build

AGENT = r"""
'use strict';
let n = 0;
// The mesh's normal (+0x10da, 12 bytes each) and tangent (+0x10dc, 16 bytes
// each) arrays: a handle's high byte picks a slot whose data sits at
// +0x1330 / +0x1430 + slot * 0x18; +0x1110 counts the corners.
function stream(mesh, handle, base, size) {
  const slot = mesh.add(handle).add(1).readS8();
  if (slot < 0 || slot >= 6) return null;
  const p = mesh.add(base + slot * 0x18).readPointer();
  if (p.isNull()) return null;
  const count = mesh.add(0x1110).readS32();
  const words = [];
  const bytes = new Uint32Array(p.readByteArray(count * size));
  for (const w of bytes) words.push(w.toString(16).padStart(8, '0'));
  return words;
}
function streams(mesh) {
  return {normal: stream(mesh, 0x10da, 0x1330, 12), tangent: stream(mesh, 0x10dc, 0x1430, 16)};
}
function hook() {
  const mod = Process.findModuleByName('resourcecompiler.dll');
  if (mod === null) { setTimeout(hook, 5); return; }
  Interceptor.attach(mod.base.add(%(rva)d), {
    onEnter(args) {
      const m = args[1], bits = [];
      for (let i = 0; i < 12; i++)
        bits.push(m.add(i * 4).readU32().toString(16).padStart(8, '0'));
      this.mesh = args[0];
      this.call = n++;
      send({call: this.call, matrix: bits, before: streams(this.mesh)});
    },
    onLeave() {
      send({call: this.call, after: streams(this.mesh)});
    }
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
    out_path = a.out or os.path.join(os.environ.get("TEMP", "."), "meshturn_capture", a.map + ".jsonl")
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
        elif "call" in msg["payload"]:
            with lock:
                out.write(json.dumps(msg["payload"]) + "\n")

    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    argv = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"),
            "-i", source, "-world", "-fshallow"]
    dev = frida.get_local_device()
    pid = dev.spawn(argv, cwd=BIN, stdio="pipe")
    ses = dev.attach(pid)
    sc = ses.create_script(AGENT % {"rva": TRANSFORM_TO_WORLD})
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
