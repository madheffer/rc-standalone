"""capture_texcoordtransform.py <addon> <map> [--out file] [-- extra rc args]: what ConvertMeshForBuilder's
texcoord test reads from each mesh material (ledger 201, CS2_CLOSED_QUEUE A.7).

Resourcecompiler 20261103 only: 1810e1227 calls the material's vf 0x48 (GetBoolAttribute) with
HasTexcoordTransform (0x2661694a), the answer byte at [rbp + 0x3630]; 1810e1250 the same with 0x7c928c7c,
the answer at [rsp + 0x48]. The hooks sit on the instruction after each call: found (al), value, the mesh
(r14) and the material, named by the first pointer in its first 0x400 bytes that holds a "materials/" path.
One JSON line per call. A -world -fshallow compile; CS2 must be closed (killed if it starts), and the map's
package is backed up and restored, cmp-checked, as capture_argdeps.py does.
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

CS2 = os.environ.get("CS2_DIR", r"D:\Steam\steamapps\common\Counter-Strike Global Offensive")
BIN = os.path.join(CS2, "game", "bin", "win64")
AFTER_TRANSFORM_RVA = 0x10e122a
AFTER_FLAG_RVA = 0x10e1253
HANDLE_RVA = 0x10e1204  # after 1810d6020: rax points at the material's handle

AGENT = r"""
'use strict';
function str(p) {
  try {
    const s = p.readUtf8String(160);
    if (s !== null && /vmat|materials\//i.test(s)) return s;
  } catch (e) {}
  return null;
}
// The material's path: a string at, or one or two pointers from, the first 0x200 bytes of the material or
// its resource binding.
function name(m) {
  for (let o = 0; o < 0x200; o += 8) {
    try {
      const p = m.add(o).readPointer();
      let s = str(p);
      if (s !== null) return s;
      for (let o2 = 0; o2 < 0x40; o2 += 8) {
        s = str(p.add(o2).readPointer());
        if (s !== null) return s;
      }
    } catch (e) {}
  }
  return null;
}
let binding = null;
// Any short printable string one pointer from the material's first 0x40 bytes (CMaterial2 logs its name from +0x10).
function raw(m) {
  const out = [];
  for (let o = 0; o < 0x40; o += 8) {
    try {
      const t = m.add(o).readPointer().readUtf8String(64);
      if (t !== null && /^[ -~]{3,}$/.test(t)) out.push(o.toString(16) + ':' + t);
    } catch (e) {}
  }
  return out.join(' | ');
}
// IMaterial2's name getter: the first of vf 0 / 8 / 0x10 that returns a path.
function vname(m) {
  for (const slot of [0, 8, 0x10]) {
    try {
      const f = new NativeFunction(m.readPointer().add(slot).readPointer(), 'pointer', ['pointer']);
      const s = str(f(m));
      if (s !== null) return s;
    } catch (e) {}
  }
  return null;
}
function hook() {
  const mod = Process.findModuleByName('resourcecompiler.dll');
  if (mod === null) { setTimeout(hook, 5); return; }
  send({ev: 'hooked'});
  Interceptor.attach(mod.base.add(%(handle)d), function () {
    try { binding = this.context.rax.readPointer(); } catch (e) { binding = null; }
  });
  Interceptor.attach(mod.base.add(%(transform)d), function () {
    const c = this.context;
    send({ev: 'call', key: 'HasTexcoordTransform', found: c.rax.and(0xff).toInt32(),
          value: c.rbp.add(0x3630).readU8(), mesh: c.r14.toString(), material: name(c.rbx) || (binding && name(binding)) || vname(c.rbx), raw: raw(c.rbx), ptr: c.rbx.toString()});
  });
  Interceptor.attach(mod.base.add(%(flag)d), function () {
    const c = this.context;
    send({ev: 'call', key: '0x7c928c7c', found: c.rax.and(0xff).toInt32(),
          value: c.rsp.add(0x48).readU8(), mesh: c.r14.toString(), material: name(c.rbx)});
  });
}
hook();
"""


def running(name):
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq " + name], capture_output=True, text=True).stdout
    return name.lower() in out.lower()


def main():
    argv = sys.argv[1:]
    extra = argv[argv.index("--") + 1:] if "--" in argv else []
    argv = argv[:argv.index("--")] if "--" in argv else argv
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("addon")
    p.add_argument("map")
    p.add_argument("--out")
    a = p.parse_args(argv)
    if running("cs2.exe") or running("resourcecompiler.exe"):
        raise SystemExit("CS2 or a compile is running; not starting")
    out_path = a.out or os.path.join(os.environ.get("TEMP", "."), "texcoordtransform", a.map + ".jsonl")
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    vpk = os.path.join(CS2, "game", "csgo_addons", a.addon, "maps", a.map + ".vpk")
    backup = out_path + ".vpk.bak"
    if os.path.exists(vpk):
        shutil.copy2(vpk, backup)
        os.remove(vpk)  # a current package makes RC skip the compile

    out = open(out_path, "w", encoding="utf-8")
    lock = threading.Lock()
    stats = {}

    def on_message(msg, data):
        if msg.get("type") != "send":
            print("agent:", msg.get("description") or msg, file=sys.stderr)
            return
        pay = msg["payload"]
        stats[pay["ev"]] = stats.get(pay["ev"], 0) + 1
        if pay["ev"] == "call":
            with lock:
                out.write(json.dumps(pay) + "\n")

    source = os.path.join(CS2, "content", "csgo_addons", a.addon, "maps", a.map + ".vmap")
    cmd = [os.path.join(BIN, "resourcecompiler.exe"), "-nop4", "-game", os.path.join(CS2, "game", "csgo"), "-i", source,
           "-world", "-fshallow"] + extra
    dev = frida.get_local_device()
    pid = dev.spawn(cmd, cwd=BIN, stdio="pipe")
    ses = dev.attach(pid)
    sc = ses.create_script(AGENT % {"transform": AFTER_TRANSFORM_RVA, "flag": AFTER_FLAG_RVA, "handle": HANDLE_RVA})
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
        shutil.copy2(backup, vpk)
        ok = filecmp.cmp(backup, vpk, shallow=False)
        print("restored", vpk, "(cmp ok)" if ok else "(CMP FAILED)")
        if ok:
            os.remove(backup)


if __name__ == "__main__":
    main()
