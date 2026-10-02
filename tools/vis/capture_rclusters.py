"""capture_rclusters.py <addon> <map> [--out file]: the render cluster boxes and the centroids they come from.

Hooks the cluster box builder (resourcecompiler 180282bf0 in the 09-23
analysis build, 180282c20 in the installed 10-01 build) during a -world
-fshallow compile. Each top-level call (depth argument 0) writes a record:
its arguments (minimum triangles, split size, size-split flag), the
centroids as handed in (12-byte float3s, before the builder reorders them)
and, on return, the output list's count and raw bytes (count at +0, data at
+8). Records: u32 json length, json, u32 blob length, blob.

CS2 must be closed; the map's package is backed up and restored.
"""
import argparse
import filecmp
import json
import os
import shutil
import struct
import subprocess
import threading

import frida

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")
AGENT = r"""
'use strict';
function hook() {
  const mod = Process.findModuleByName('resourcecompiler.dll');
  if (mod === null) { setTimeout(hook, 5); return; }
  Interceptor.attach(mod.base.add(0x282c20), {
    onEnter(args) {
      this.top = args[6].toInt32() === 0;
      if (!this.top) return;
      this.out = args[0];
      const n = args[5].toInt32();
      const before = this.out.readS32();
      send({ev: 'in', minTriangles: args[1].toInt32(), size: new Float32Array(new Uint32Array([args[2].toInt32()]).buffer)[0],
            sizeSplit: args[3].toInt32() & 0xff, count: n, outBefore: before}, args[4].readByteArray(n * 12));
    },
    onLeave() {
      if (!this.top) return;
      const n = this.out.readS32();
      const data = this.out.add(8).readPointer();
      send({ev: 'out', count: n}, n > 0 ? data.readByteArray(n * 24) : null);
    }
  });
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
    out_path = a.out or os.path.join(os.environ.get("TEMP", "."), "rclusters_capture", a.map + ".bin")
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    vpk = os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    backup = out_path + ".vpk.bak"
    if os.path.exists(vpk):
        shutil.copyfile(vpk, backup)
        os.remove(vpk)
    out = open(out_path, "wb")
    lock = threading.Lock()

    def on_message(msg, data):
        if msg.get("type") != "send":
            print("agent:", msg)
            return
        head = json.dumps(msg["payload"]).encode()
        blob = data or b""
        with lock:
            out.write(struct.pack("<I", len(head)) + head + struct.pack("<I", len(blob)) + blob)
        print(msg["payload"])

    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    dev = frida.get_local_device()
    pid = dev.spawn([os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"),
                     "-i", source, "-world", "-fshallow"], cwd=BIN, stdio="pipe")
    ses = dev.attach(pid)
    sc = ses.create_script(AGENT)
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
    if os.path.exists(vpk):
        shutil.copyfile(vpk, out_path + ".vpk")
    if os.path.exists(backup):
        shutil.copyfile(backup, vpk)
        ok = filecmp.cmp(backup, vpk, shallow=False)
        print("restored" if ok else "RESTORE CMP FAILED", vpk)
        if ok:
            os.remove(backup)


if __name__ == "__main__":
    main()
