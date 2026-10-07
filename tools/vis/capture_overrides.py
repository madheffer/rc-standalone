"""capture_overrides.py <addon> <map> [--out dir] [--switches "..."]: the inputs behind RED2's hashes.

Runs a -world -fshallow compile (or --switches) under Frida and keeps:

  override_<n>.bin   every buffer handed to the argument-dependency recorder
                     (0923 180174ae0) with its parameter name: the bytes whose
                     CRC32 is RED2's ___OverrideInputData___ fingerprint
                     (world.vrman, world_visibility.vvis)
  saved_<n>.bin      every file the manifest writer saves (181c1fae0,
                     SaveKV3 text then the filesystem), read back on return:
                     the source whose CRC32 is n0.vrman's m_nFileCRC

and an index.jsonl naming each with its parameter or path and CRC32.
Addresses go through tools/re/rva_map.py. CS2 must be closed and 10 GB free;
the map's package is backed up first and restored, cmp-checked, afterwards.
"""
import argparse
import filecmp
import json
import os
import shutil
import subprocess
import sys
import threading
import zlib

import frida

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "re"))
from rva_map import installed  # noqa: E402

CS2 = os.environ.get(
    "CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")

AGENT = r"""
'use strict';
function hook() {
  const mod = Process.findModuleByName('resourcecompiler.dll');
  if (mod === null) { setTimeout(hook, 5); return; }
  // FUN_180174ae0(this, name, CUtlBuffer *buffer, int fingerprint): the buffer's
  // memory at +8 (when its capacity word +4 is non-zero), its size at +0x20.
  Interceptor.attach(mod.base.add(%(record)d), {
    onEnter(args) {
      const name = args[1].isNull() ? '' : args[1].readUtf8String();
      const buffer = args[2];
      if (buffer.isNull()) { send({kind: 'override', name: name, size: -1}); return; }
      const size = buffer.add(0x20).readS32();
      const memory = (buffer.add(4).readU32() & 0x7fffffff) !== 0 ? buffer.add(8).readPointer() : ptr(0);
      send({kind: 'override', name: name, size: size}, memory.isNull() || size <= 0 ? null : memory.readByteArray(size));
    }
  });
  // FUN_181c1fae0(kv3, path, pathId, flags): read the file it wrote on return.
  Interceptor.attach(mod.base.add(%(save)d), {
    onEnter(args) { this.path = args[1].isNull() ? '' : args[1].readUtf8String(); },
    onLeave() {
      let bytes = null;
      try { bytes = File.readAllBytes(this.path); } catch (e) { }
      send({kind: 'saved', path: this.path, read: bytes !== null}, bytes);
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
    p.add_argument("--switches", default="-world -fshallow", help="the builder switches (default: -world -fshallow)")
    a = p.parse_args()
    if running("cs2.exe"):
        raise SystemExit("CS2 is running; not starting")
    if running("resourcecompiler.exe"):
        raise SystemExit("another resourcecompiler is running; not starting")
    if shutil.disk_usage(os.path.splitdrive(CS2)[0] + "\\").free < 10 * 1024 ** 3:
        raise SystemExit("under 10 GB free on the game drive; not starting")
    rvas = {"record": installed("resourcecompiler", 0x174ae0), "save": installed("resourcecompiler", 0x1c1fae0)}
    out_dir = a.out or os.path.join(os.environ.get("TEMP", "."), "override_capture", a.map)
    os.makedirs(out_dir, exist_ok=True)
    vpk = os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    backup = os.path.join(out_dir, a.map + ".vpk.bak")
    if os.path.exists(vpk):
        shutil.copyfile(vpk, backup)
        os.remove(vpk)
    index = open(os.path.join(out_dir, "index.jsonl"), "w")
    lock = threading.Lock()
    hooked = threading.Event()
    counter = {"override": 0, "saved": 0}

    def on_message(msg, data):
        if msg.get("type") != "send":
            print("agent:", msg.get("description") or msg)
            return
        payload = msg["payload"]
        if payload.get("hooked"):
            hooked.set()
            return
        with lock:
            kind = payload["kind"]
            n = counter[kind]
            counter[kind] = n + 1
            entry = dict(payload, n=n)
            if data is not None:
                name = f"{kind}_{n}.bin"
                with open(os.path.join(out_dir, name), "wb") as f:
                    f.write(data)
                entry.update(file=name, bytes=len(data), crc32=zlib.crc32(data))
            index.write(json.dumps(entry) + "\n")

    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    argv = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"),
            "-i", source] + a.switches.split()
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
    index.close()
    print(out_dir, "(hooked)" if hooked.is_set() else "(NEVER HOOKED)", counter)
    if os.path.exists(backup):
        shutil.copyfile(backup, vpk)
        ok = filecmp.cmp(backup, vpk, shallow=False)
        print("restored", vpk, "(cmp ok)" if ok else "(CMP FAILED)")
        if ok:
            os.remove(backup)


if __name__ == "__main__":
    main()
