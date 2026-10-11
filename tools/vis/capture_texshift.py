"""capture_texshift.py <addon> <map> [--out file]: a map mesh's texcoords through ConvertMeshForBuilder.

Runs a -world -fshallow compile under Frida and snapshots each map mesh's
2-float face-vertex streams (type 1: slot k live at +0x1234 + k, its data
at +0x1240 + k * 0x18 + 8, 8 bytes a corner, as FUN_1813b7630 reads them):

  copy    on leaving HammerMesh_CopyFrom (0923 1810c1eb0)
  shift   entering HammerMesh_ShiftTexcoordIslands (1810d93c0)
  shifted on leaving it

One JSON line per snapshot: the stage, the mesh pointer, the corner count
and the stream as hex float bits. Used for brush entity texcoords one ulp
off ours (ze_hold_em_p's end_breakable models).

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
// The 2-float face-vertex streams (type 1): slot k is live when +0x1234 + k is
// set, its data pointer at +0x1240 + k * 0x18 + 8 (FUN_1813b7630, the island
// shift's reader); +0x1110 counts the corners.
function streams(mesh) {
  const out = [];
  const count = mesh.add(0x1110).readS32();
  for (let k = 0; k < 6; k++) {
    if (mesh.add(0x1234 + k).readU8() === 0) continue;
    const p = mesh.add(0x1240 + k * 0x18 + 8).readPointer();
    if (p.isNull()) continue;
    const words = [];
    for (const w of new Uint32Array(p.readByteArray(count * 8)))
      words.push(w.toString(16).padStart(8, '0'));
    out.push({slot: k, words: words});
  }
  return out;
}
function snap(stage, mesh) {
  counts[stage] = (counts[stage] || 0) + 1;
  send({stage: stage, call: counts[stage] - 1, mesh: mesh.toString(), corners: mesh.add(0x1110).readS32(),
        texcoord: streams(mesh)});
}
function hook() {
  const mod = Process.findModuleByName('resourcecompiler.dll');
  if (mod === null) { setTimeout(hook, 5); return; }
  Interceptor.attach(mod.base.add(%(copy)d), {
    onEnter(args) { this.mesh = args[0]; },
    onLeave() { snap('copy', this.mesh); }
  });
  Interceptor.attach(mod.base.add(%(shift)d), {
    onEnter(args) { this.mesh = args[0]; snap('shift', this.mesh); },
    onLeave() { snap('shifted', this.mesh); }
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
    rvas = {"copy": installed("resourcecompiler", 0x10c1eb0),
            "shift": installed("resourcecompiler", 0x10d93c0)}
    out_path = a.out or os.path.join(os.environ.get("TEMP", "."), "texshift_capture", a.map + ".jsonl")
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
