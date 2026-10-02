"""capture_vertexcopy.py <addon> <map>: how DmeMeshToCMesh copies each vertex attribute (ledger 37).

Hooks the per-attribute copy (resourcecompiler 180d46590 in the 09-23
analysis build, 180d46900 in the installed 10-01 build) during a -world
-fshallow compile and prints each distinct combination of the attribute
type (+0x1c & 0x3f), the point and transform flags (8th and 9th arguments)
and the bits of the matrix and inverse transpose it is handed.

CS2 must be closed; the map's package is backed up and restored.
"""
import filecmp
import os
import shutil
import subprocess
import sys
import threading

import frida

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")
AGENT = r"""
'use strict';
const seen = new Set();
function bits(p) {
  if (p.isNull()) return 'null';
  const out = [];
  for (let i = 0; i < 12; i++) out.push(p.add(i * 4).readU32().toString(16));
  return out.join(' ');
}
function hook() {
  const mod = Process.findModuleByName('resourcecompiler.dll');
  if (mod === null) { setTimeout(hook, 5); return; }
  Interceptor.attach(mod.base.add(0xd46900), {
    onEnter(args) {
      const type = args[1].add(0x1c).readU32() & 0x3f;
      if (type !== 0x2a && type !== 0x2b) return;
      const key = [type, args[6].toInt32() & 0xff, args[7].toInt32() & 0xff, args[8].toInt32() & 0xff, bits(args[3]), bits(args[4])].join('|');
      if (seen.has(key)) return;
      seen.add(key);
      send(key);
    }
  });
}
hook();
"""


def running(name):
    return name.lower() in subprocess.run(["tasklist"], capture_output=True, text=True).stdout.lower()


def main():
    addon, name = sys.argv[1], sys.argv[2]
    if running("cs2.exe") or running("resourcecompiler.exe"):
        raise SystemExit("CS2 or a compile is running; not starting")
    vpk = os.path.join(CS2, "game", "csgo_addons", addon, "maps", name + ".vpk")
    backup = vpk + ".capture.bak"
    if os.path.exists(vpk):
        shutil.copyfile(vpk, backup)
        os.remove(vpk)
    source = os.path.join(CS2, "content", "csgo_addons", addon, "maps", name + ".vmap")
    dev = frida.get_local_device()
    pid = dev.spawn([os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"),
                     "-i", source, "-world", "-fshallow"], cwd=BIN, stdio="pipe")
    ses = dev.attach(pid)
    sc = ses.create_script(AGENT)
    sc.on("message", lambda m, d: print(m.get("payload") if m.get("type") == "send" else m))
    sc.load()
    done = threading.Event()
    ses.on("detached", lambda *x: done.set())
    dev.resume(pid)
    while not done.wait(2):
        if running("cs2.exe"):
            subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True)
    while running("resourcecompiler.exe"):
        threading.Event().wait(2)
    if os.path.exists(backup):
        shutil.copyfile(backup, vpk)
        ok = filecmp.cmp(backup, vpk, shallow=False)
        print("restored" if ok else "RESTORE CMP FAILED", vpk)
        if ok:
            os.remove(backup)


if __name__ == "__main__":
    main()
