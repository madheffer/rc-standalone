"""capture_matflags.py <addon> <map> [--out file]: the trace flag word each map mesh material gets.

Hooks the store of CMapMesh's per-material flag word (the mesh's +0x34d8
array, filled by FUN_1810dcca0 in the 09-23 analysis build from
Material_VisFlags of the loaded material, or of the resource system's default
'vmat' when it did not load), at 0x1810dd569 in the installed 10-01 build:
`mov word ptr [rax + rsi*2], di` with r13 the mesh and rsi the slot. Each
store logs one JSON line: the mesh's slot name (+0x3490), the material pointer
(+0x34a8) and the flag word. Settles what a missing material traces as in the
editor scene (the light precompute's).

CS2 must be closed; the map's package is backed up and restored.
"""
import argparse
import filecmp
import json
import os
import shutil
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
  const at = mod.base.add(0x10dd569);
  const head = Instruction.parse(at);
  if (head.mnemonic !== 'mov' || head.opStr.indexOf('rsi*2') < 0) {
    send({error: 'not the store: ' + head.toString()});
    return;
  }
  Interceptor.attach(at, function () {
    const c = this.context;
    const mesh = c.r13, slot = c.rsi.toInt32();
    let name = null;
    try { name = mesh.add(0x3490).readPointer().add(slot * 8).readPointer().readUtf8String(); } catch (e) {}
    const material = mesh.add(0x34a8).readPointer().add(slot * 8).readPointer();
    send({slot: slot, name: name, material: material.toString(), flags: c.rdi.toInt32() & 0xffff});
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
    out_path = a.out or os.path.join(os.environ.get("TEMP", "."), "matflags_capture", a.map + ".jsonl")
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    vpk = os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    backup = out_path + ".vpk.bak"
    if os.path.exists(vpk):
        shutil.copyfile(vpk, backup)
        os.remove(vpk)
    out = open(out_path, "wb")
    lock = threading.Lock()
    count = [0]

    def on_message(msg, data):
        if msg.get("type") != "send":
            print("agent:", msg)
            return
        with lock:
            out.write((json.dumps(msg["payload"]) + chr(10)).encode())
            count[0] += 1

    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    dev = frida.get_local_device()
    pid = dev.spawn([os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"),
                     "-i", source, "-world", "-phys", "-fshallow"], cwd=BIN, stdio="pipe")
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
    print(out_path, count[0], "stores")
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
