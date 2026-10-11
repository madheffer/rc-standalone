"""capture_uvdensity.py <addon> <map> --counts n,n,... [--out file] [--switches "..."]: UV density inputs.

Runs a compile (default -world -fshallow) under Frida and hooks the draw
call's UV density (0923 1812d9920, moved to the installed build by
tools/re/rva_map.py), called from CResourceCompilerMesh_AddDrawDescriptors
with (vertex data, ?, index count, percentile). The data struct: +0 the float
vertices, +8 the fields (0x28 each: +0 name, +0x14 float offset, +0x18
component count), +0x10 the indices, +0x1c the stride in floats, +0x20 the
field count. The function reads the first field named like "position" with 3
components and the first like "tex" with 2. For a call whose index count is
one of --counts, one record is written: u32 index count, u32 stride, u32
position offset, u32 texcoord offset, i32 percentile, then per index its 3
position floats and 2 texcoord floats, as the function sees them. Used for
the aggregates whose m_flUvDensity differs from ours in the last digits.

CS2 must be closed and 10 GB free; the map's package is backed up first and
restored, cmp-checked, afterwards.
"""
import argparse
import filecmp
import os
import shutil
import struct
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
function hook() {
  const mod = Process.findModuleByName('resourcecompiler.dll');
  if (mod === null) { setTimeout(hook, 5); return; }
  Interceptor.attach(mod.base.add(DENSITY), {
    onEnter(args) {
      const count = args[2].toInt32();
      if (COUNTS.indexOf(count) < 0) return;
      const p = args[0];
      const data = p.readPointer(), fields = p.add(8).readPointer(), indices = p.add(16).readPointer();
      const stride = p.add(0x1c).readS32(), nfields = p.add(0x20).readS32();
      let po = -1, to = -1;
      for (let i = 0; i < nfields; i++) {
        const f = fields.add(i * 0x28);
        const np = f.readPointer();
        const name = np.isNull() ? '' : np.readUtf8String().toLowerCase();
        const comps = f.add(0x18).readS32(), off = f.add(0x14).readS32();
        if (po < 0 && name.indexOf('position') >= 0 && comps === 3) po = off;
        if (to < 0 && name.indexOf('tex') >= 0 && comps === 2) to = off;
      }
      if (po < 0 || to < 0) return;
      const out = new ArrayBuffer(20 + count * 20);
      const view = new DataView(out);
      view.setUint32(0, count, true); view.setUint32(4, stride, true);
      view.setUint32(8, po, true); view.setUint32(12, to, true); view.setInt32(16, args[3].toInt32(), true);
      for (let k = 0; k < count; k++) {
        const v = indices.add(k * 4).readU32();
        const base = data.add(v * stride * 4);
        for (let c = 0; c < 3; c++) view.setUint32(20 + k * 20 + c * 4, base.add((po + c) * 4).readU32(), true);
        for (let c = 0; c < 2; c++) view.setUint32(20 + k * 20 + 12 + c * 4, base.add((to + c) * 4).readU32(), true);
      }
      send({call: count}, out);
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
    p.add_argument("--counts", required=True)
    p.add_argument("--out")
    p.add_argument("--switches", default="-world -fshallow")
    a = p.parse_args()
    if running("cs2.exe") or running("resourcecompiler.exe"):
        raise SystemExit("CS2 or a compile is running; not starting")
    if shutil.disk_usage(os.path.splitdrive(CS2)[0] + "\\").free < 10 * 1024 ** 3:
        raise SystemExit("under 10 GB free on the game drive; not starting")
    head = f"const DENSITY = {installed('resourcecompiler', 0x12d9920)};\nconst COUNTS = [{a.counts}];\n"
    out_path = a.out or os.path.join(os.environ.get("TEMP", "."), "uvdensity_capture", a.map + ".bin")
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    vpk = os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    backup = out_path + ".vpk.bak"
    if os.path.exists(vpk):
        shutil.copyfile(vpk, backup)
    sink = open(out_path, "wb")
    lock = threading.Lock()
    calls = [0]

    def on_message(msg, data):
        if msg.get("type") != "send":
            print("agent:", msg.get("description") or msg)
            return
        if msg["payload"].get("hooked"):
            print("hooked", flush=True)
            return
        with lock:
            sink.write(data)
            calls[0] += 1

    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    dev = frida.get_local_device()
    pid = dev.spawn([os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"),
                     "-i", source] + a.switches.split(), cwd=BIN, stdio="pipe")
    ses = dev.attach(pid)
    sc = ses.create_script(AGENT.replace("'use strict';\n", "'use strict';\n" + head, 1))
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
    sink.close()
    print(out_path, calls[0], "calls")
    if os.path.exists(backup):
        shutil.copyfile(backup, vpk)
        ok = filecmp.cmp(backup, vpk, shallow=False)
        print("restored" if ok else "RESTORE CMP FAILED", vpk)
        if ok:
            os.remove(backup)


if __name__ == "__main__":
    main()
