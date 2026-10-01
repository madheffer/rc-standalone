"""capture_cones.py <addon> <map> [--out file] [--full]: record every meshlet cone the compile computes.

Hooks meshopt's computeMeshletBounds as CMeshletBuilder_AddMeshlet calls it
(resourcecompiler FUN_1812bd110: hidden return, meshlet vertices, meshlet
triangles, triangle count, positions, vertex count, stride in bytes). Each
call is one record: its triangles (local indices, three bytes each), the
positions of the meshlet's vertices as the call reads them, and the 48-byte
meshopt_Bounds it returns (centre, radius, apex, axis, cutoff, then the s8
axis and cutoff). WorldNodePropConeProbe matches them to the node models of
the compile, which is kept as <out>.vpk.

CS2 must be closed (a watchdog kills the compile if it starts), 10 GB must
be free, and the map's package is backed up first and restored, cmp-checked,
afterwards.
"""
import argparse
import filecmp
import json
import os
import shutil
import struct
import subprocess
import sys
import threading

import frida

CS2 = os.environ.get(
    "CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")
# resourcecompiler.dll 2026-10-01 (the 09-23 build +0x370; tools/re/tracked_rvas.json).
BOUNDS_RVA = 0x12bd480

AGENT = r"""
'use strict';
let seq = 0;
function hook() {
  const mod = Process.findModuleByName('resourcecompiler.dll');
  if (mod === null) { setTimeout(hook, 5); return; }
  send({ev: 'hooked'});
  Interceptor.attach(mod.base.add(%(rva)d), {
    onEnter(args) {
      this.out = args[0];
      const verts = args[1], tris = args[2], n = args[3].toInt32();
      const pos = args[4], stride = args[6].toInt32();
      const tb = new Uint8Array(tris.readByteArray(n * 3));
      let local = 0;
      for (let i = 0; i < tb.length; i++) local = Math.max(local, tb[i] + 1);
      const blob = new Uint8Array(n * 3 + local * 12 + 48);
      blob.set(tb, 0);
      const f = new Float32Array(local * 3);
      for (let v = 0; v < local; v++) {
        const g = verts.add(v * 4).readU32();
        const p = pos.add(g * stride);
        f[v * 3] = p.readFloat(); f[v * 3 + 1] = p.add(4).readFloat(); f[v * 3 + 2] = p.add(8).readFloat();
      }
      blob.set(new Uint8Array(f.buffer), n * 3);
      this.blob = blob;
      this.head = {ev: 'cone', n: seq++, tris: n, local: local, stride: stride};
    },
    onLeave(ret) {
      this.blob.set(new Uint8Array(this.out.readByteArray(48)), this.blob.length - 48);
      send(this.head, this.blob.buffer);
    }
  });
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
    p.add_argument("--full", action="store_true", help="a full compile rather than -world -fshallow")
    a = p.parse_args()
    if running("cs2.exe"):
        raise SystemExit("CS2 is running; not starting")
    if running("resourcecompiler.exe"):
        raise SystemExit("another resourcecompiler is running; not starting")
    if shutil.disk_usage(os.path.splitdrive(CS2)[0] + "\\").free < 10 * 1024 ** 3:
        raise SystemExit("under 10 GB free on the game drive; not starting")

    out_path = a.out or os.path.join(os.environ.get("TEMP", "."), "cone_capture", a.map + ".bin")
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    vpk = os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    backup = out_path + ".vpk.bak"
    if os.path.exists(vpk):
        shutil.copyfile(vpk, backup)
        os.remove(vpk)  # a current package makes RC skip the compile

    out = open(out_path, "wb")
    lock = threading.Lock()
    stats = {}

    def on_message(msg, data):
        if msg.get("type") != "send":
            print("agent:", msg.get("description") or msg, file=sys.stderr)
            return
        pay = msg["payload"]
        stats[pay["ev"]] = stats.get(pay["ev"], 0) + 1
        head = json.dumps(pay).encode()
        blob = data or b""
        with lock:
            out.write(struct.pack("<I", len(head)) + head + struct.pack("<I", len(blob)) + blob)

    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    argv = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4",
            "-game", os.path.join(CS2, "game", "csgo"), "-i", source]
    if not a.full:
        argv += ["-world", "-fshallow"]
    dev = frida.get_local_device()
    pid = dev.spawn(argv, cwd=BIN, stdio="pipe")
    ses = dev.attach(pid)
    sc = ses.create_script(AGENT % {"rva": BOUNDS_RVA})
    sc.on("message", on_message)
    sc.load()
    done = threading.Event()
    ses.on("detached", lambda *x: done.set())
    dev.resume(pid)
    killed = False
    while not done.wait(2):
        if running("cs2.exe"):
            subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True)
            killed = True
    out.close()
    print(out_path, stats, "(killed: cs2 started)" if killed else "")

    if os.path.exists(vpk):
        shutil.copyfile(vpk, out_path + ".vpk")
    if os.path.exists(backup):
        shutil.copyfile(backup, vpk)
        ok = filecmp.cmp(backup, vpk, shallow=False)
        print("restored", vpk, "(cmp ok)" if ok else "(CMP FAILED)")
        if ok:
            os.remove(backup)


if __name__ == "__main__":
    main()
